using System.Globalization;
using Alpaca.Markets;
using Microsoft.Extensions.Logging;
using Xakpc.Alpaca.NøIdea.Alpaca;
using Xakpc.Alpaca.NøIdea.Storage;

// US markets, US formatting. Set before anything parses or formats money: the
// local machine culture uses a comma decimal separator.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en-US");
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");

using var loggerFactory = LoggerFactory.Create(builder => builder
    .SetMinimumLevel(LogLevel.Information)
    .AddSimpleConsole(options => options.SingleLine = true));

var log = loggerFactory.CreateLogger("Trader");

if (!args.Contains("--smoke") && !args.Contains("--check-mcp"))
{
    log.LogInformation(
        "Nothing to do. Pass --smoke for the order path, or --check-mcp for the read-only MCP connection.");
    return 0;
}

var symbol = ArgumentValue("--symbol") ?? "SPY";
var maxPremium = decimal.Parse(ArgumentValue("--max-premium") ?? "5.00", CultureInfo.InvariantCulture);
var clientOrderId = ArgumentValue("--client-order-id") ?? $"smoke-{Guid.NewGuid():N}";

TimeProvider time = TimeProvider.System;
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
var ct = cancellation.Token;

try
{
    var credentials = AlpacaOptions.FromEnvironment();

    // The read-only MCP connection exists for the LLM agents only. Checking it is
    // separate from the order path on purpose: the two share no code, which is the
    // point. No MCP server this host runs holds an order tool.
    if (args.Contains("--check-mcp"))
    {
        var mcpOptions = AlpacaMcpOptions.FromEnvironment() with
        {
            ReadOnlyUrl = ArgumentValue("--mcp-url")
                          ?? Environment.GetEnvironmentVariable("Alpaca__Mcp__ReadOnlyUrl")
                          ?? "http://127.0.0.1:8100/mcp",
        };

        var (mcpClient, approvedTools) = await AlpacaMcpClient.ConnectAsync(
            mcpOptions, credentials, loggerFactory, ct);

        await using (mcpClient)
        {
            log.LogInformation(
                "MCP check passed. {Count} tools approved for agent use, and none can change the account.",
                approvedTools.Count);
        }

        if (!args.Contains("--smoke"))
        {
            return 0;
        }
    }

    using var alpaca = new AlpacaClients(credentials);

    var store = new TradingStore(TradingStore.ConnectionStringForFile(
        Path.Combine(AppContext.BaseDirectory, "trader.db")));
    await store.CreateSchemaAsync(ct);

    // 1. Account and clock. Alpaca is the source of truth for both.
    var clock = await alpaca.Trading.GetClockAsync(ct);
    var account = await alpaca.Trading.GetAccountAsync(ct);

    log.LogInformation(
        "Account {Number}: equity {Equity:N2} USD, options level {Level}. Market open: {Open}. Next open {NextOpen:u}.",
        account.AccountNumber, account.Equity, account.OptionsTradingLevel, clock.IsOpen, clock.NextOpenUtc);

    // Fail closed. A blocked account, or a level that cannot buy a long call, would
    // make everything after this produce a misleading failure.
    if (account.IsTradingBlocked || account.IsAccountBlocked)
    {
        log.LogError("Trading is blocked on this account. Stopping.");
        return 1;
    }

    if (account.OptionsTradingLevel is null or OptionsTradingLevel.Disabled)
    {
        log.LogError("Options trading is disabled on this account. Enable it on the paper account.");
        return 1;
    }

    // 2. Idempotency comes FIRST. If this client order id is already reserved, the
    //    recovery path must resolve THAT order. Selecting a contract first and
    //    checking afterwards would pick a fresh contract at a new price, which
    //    defeats the guard entirely.
    var existing = await store.FindAsync(clientOrderId, ct);
    string contractSymbol;

    if (existing is not null)
    {
        contractSymbol = existing.OptionSymbol;
        log.LogWarning(
            "Client order id {Id} is already reserved for {Contract} (status {Status}). Resolving it, not submitting.",
            clientOrderId, contractSymbol, existing.Status);
    }
    else
    {
        // 3. Underlying price, so the strike filter can be centred on it.
        var lastTrade = await alpaca.StockData.GetLatestTradeAsync(new LatestMarketDataRequest(symbol), ct);
        var spot = lastTrade.Price;
        log.LogInformation("{Symbol} last trade {Price:N2} at {Time:u}.", symbol, spot, lastTrade.TimestampUtc);

        // 4. A near-money call ladder over the next two weeks. No feed is named (ADR-010).
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var chain = await alpaca.OptionsData.GetOptionChainAsync(new OptionChainRequest(symbol)
        {
            OptionType = OptionType.Call,
            ExpirationDateGreaterThanOrEqualTo = today,
            ExpirationDateLessThanOrEqualTo = today.AddDays(14),
            StrikePriceGreaterThanOrEqualTo = spot * 0.98m,
            StrikePriceLessThanOrEqualTo = spot * 1.05m,
        }, ct);

        log.LogInformation("Option chain returned {Count} contracts.", chain.Items.Count);

        // 5. Fail closed on quote quality. A missing or one-sided quote is a skip, never
        //    a zero. This is the whole data-validation layer: the SDK typed the rest.
        var candidate = chain.Items
            .Where(entry => entry.Value.Quote is { BidPrice: > 0, AskPrice: > 0 })
            .Where(entry => entry.Value.Quote!.AskPrice >= entry.Value.Quote.BidPrice)
            .Where(entry => entry.Value.Quote!.AskPrice <= maxPremium)
            .OrderBy(entry => entry.Value.Quote!.AskPrice - entry.Value.Quote.BidPrice)
            .Select(entry => (Symbol: entry.Key, entry.Value.Quote))
            .FirstOrDefault();

        if (candidate.Symbol is null)
        {
            log.LogError(
                "No contract had a valid two-sided quote at or below {Max:N2}. Skipping, as fail-closed requires.",
                maxPremium);
            return 1;
        }

        contractSymbol = candidate.Symbol;
        var quote = candidate.Quote!;
        var limitPrice = decimal.Round(quote.AskPrice, 2);

        log.LogInformation(
            "Candidate {Contract}: bid {Bid:N2} ask {Ask:N2} (spread {Spread:N2}). Limit {Limit:N2}, about {Cost:N2}.",
            contractSymbol, quote.BidPrice, quote.AskPrice,
            quote.AskPrice - quote.BidPrice, limitPrice, limitPrice * 100m);

        // 6. Reserve BEFORE submitting. If the submit then fails with an uncertain
        //    result, the client order id is already durable and recovery can ask
        //    Alpaca what happened instead of sending a second order.
        await store.ReserveAsync(new OrderRecord
        {
            ClientOrderId = clientOrderId,
            OptionSymbol = contractSymbol,
            Side = nameof(OrderSide.Buy),
            Quantity = 1,
            OrderType = nameof(OrderType.Limit),
            LimitPrice = limitPrice,
            SubmittedUtc = time.GetUtcNow().ToUnixTimeSeconds(),
            Status = "reserved",
        }, ct);

        var submitted = await alpaca.Trading.PostOrderAsync(new NewOrderRequest(
            contractSymbol, OrderQuantity.FromInt64(1), OrderSide.Buy, OrderType.Limit, TimeInForce.Day)
        {
            ClientOrderId = clientOrderId,
            LimitPrice = limitPrice,
        }, ct);

        log.LogInformation("Submitted {OrderId} status {Status}.", submitted.OrderId, submitted.OrderStatus);

        await store.RecordResultAsync(
            clientOrderId, submitted.OrderId.ToString(), submitted.OrderStatus.ToString(), null, ct);
    }

    // 7. Read back BY CLIENT ORDER ID, never by broker id. This is the lookup the
    //    recovery path depends on, so the check must exercise it.
    var readBack = await alpaca.Trading.GetOrderAsync(clientOrderId, ct);
    log.LogInformation(
        "Read back by client id: {Contract} status {Status}, filled {Filled} at {Price}.",
        readBack.Symbol, readBack.OrderStatus, readBack.IntegerFilledQuantity, readBack.AverageFillPrice);

    // 8. Close what filled; cancel what did not.
    if (readBack.IntegerFilledQuantity > 0)
    {
        var closed = await alpaca.Trading.DeletePositionAsync(new DeletePositionRequest(contractSymbol), ct);
        log.LogInformation("Close order {OrderId} submitted, status {Status}.", closed.OrderId, closed.OrderStatus);
    }
    else if (readBack.OrderStatus is OrderStatus.Canceled or OrderStatus.Expired or OrderStatus.Rejected)
    {
        log.LogInformation("Order is already {Status}. Nothing to cancel.", readBack.OrderStatus);
    }
    else
    {
        log.LogInformation("Nothing filled. Cancelling the open order.");
        await alpaca.Trading.CancelOrderAsync(readBack.OrderId, ct);
    }

    await store.RecordResultAsync(
        clientOrderId, readBack.OrderId.ToString(), readBack.OrderStatus.ToString(),
        time.GetUtcNow().ToUnixTimeSeconds(), ct);

    log.LogInformation("Smoke check finished. Client order id {Id}.", clientOrderId);
    return 0;
}
catch (Exception error)
{
    log.LogError(error, "Smoke check failed.");
    return 1;
}

string? ArgumentValue(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
