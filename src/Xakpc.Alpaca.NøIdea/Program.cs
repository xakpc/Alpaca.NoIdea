using System.Globalization;
using Alpaca.Markets;
using Microsoft.Extensions.Logging;
using Xakpc.Alpaca.NøIdea.Alpaca;
using Xakpc.Alpaca.NøIdea.Alpaca.Gateways;
using Xakpc.Alpaca.NøIdea.Agents;
using Xakpc.Alpaca.NøIdea.Agents.Room;
using Xakpc.Alpaca.NøIdea.Agents.Room.Personas;
using Xakpc.Alpaca.NøIdea.Replay;
using Xakpc.Alpaca.NøIdea.Trading;
using Xakpc.Alpaca.NøIdea.Storage;

// US markets, US formatting. Set before anything parses or formats money: the
// local machine culture uses a comma decimal separator.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en-US");
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");

using var loggerFactory = LoggerFactory.Create(builder => builder
    .SetMinimumLevel(LogLevel.Information)
    .AddSimpleConsole(options => options.SingleLine = true));

var log = loggerFactory.CreateLogger("Trader");

if (!args.Contains("--smoke") && !args.Contains("--check-mcp") && !args.Contains("--import-history")
    && !args.Contains("--audit")
    && !args.Contains("--live")
    && !args.Contains("--replay"))
{
    log.LogInformation(
        "Nothing to do. Pass --smoke for the order path, --check-mcp for the read-only MCP "
        + "connection, --import-history to load data/raw into SQLite, or --replay to run the "
        + "offline replay.");
    return 0;
}

// Reads the audit trail back. Offline, needs no credentials, and changes nothing: it exists
// so the record can be inspected without a SQLite client, which is also how the demo shows
// that a rejection was stored with the same care as a trade.
if (args.Contains("--audit"))
{
    using var auditCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    var auditToken = auditCancellation.Token;

    var auditStore = new TradingStore(TradingStore.ConnectionStringForFile(
        DatabasePath(RepositoryRoot())));
    await auditStore.CreateSchemaAsync(auditToken);

    foreach (var (table, total) in await auditStore.AuditRowCountsAsync(auditToken))
    {
        log.LogInformation("  {Table}: {Total:N0} rows.", table, total);
    }

    foreach (var entry in await auditStore.RecentDecisionsAsync(
        int.TryParse(ArgumentValue("--last"), out var last) ? last : 10, auditToken))
    {
        log.LogInformation(
            "  {At} {Mode} {Status} {Action} {Symbol} p={Probability} market={Market} "
            + "net={Net} risk=[{Risk}] seats={Seats} order={Order}",
            DateTimeOffset.FromUnixTimeSeconds(entry.TimestampUtc).ToString("u"),
            entry.Mode, entry.Status, entry.Action, entry.OptionSymbol,
            entry.CombinedProbability?.ToString("P0") ?? "-",
            entry.MarketProbability?.ToString("P0") ?? "-",
            entry.NetVote?.ToString("F2") ?? "-",
            entry.RiskResult, entry.SeatCount, entry.ClientOrderId ?? "-");
    }

    return 0;
}


// The import is offline and needs no Alpaca credentials, so it runs before anything
// reads them. It loads data/raw into the SQLite cache that replay reads.
if (args.Contains("--import-history"))
{
    using var importCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(30));
    var importToken = importCancellation.Token;

    var repositoryRoot = RepositoryRoot();
    var importStore = new TradingStore(TradingStore.ConnectionStringForFile(
        DatabasePath(repositoryRoot)));
    await importStore.CreateSchemaAsync(importToken);

    var window = new ImportWindow();
    if (ArgumentValue("--from") is { } fromText)
    {
        window = window with { From = DateOnly.Parse(fromText, CultureInfo.InvariantCulture) };
    }

    if (ArgumentValue("--to") is { } toText)
    {
        window = window with { To = DateOnly.Parse(toText, CultureInfo.InvariantCulture) };
    }

    log.LogInformation("Importing {From} to {To} from {Raw}.",
        window.From, window.To, Path.Combine(repositoryRoot, "data", "raw"));

    var importer = new HistoryImporter(importStore, log);
    await importer.ImportAsync(Path.Combine(repositoryRoot, "data", "raw"), window, importToken);

    foreach (var (table, total) in (await importStore.CacheRowCountsAsync(importToken))
             .OrderBy(entry => entry.Key, StringComparer.Ordinal))
    {
        log.LogInformation("  {Table}: {Total:N0} rows.", table, total);
    }

    if (!args.Contains("--smoke") && !args.Contains("--check-mcp") && !args.Contains("--replay"))
    {
        return 0;
    }
}

// Replay is fully offline. It constructs no Alpaca client and no MCP client, which is the
// property .lode/replay/replay-mode.md requires and ReplayTests asserts.
if (args.Contains("--replay"))
{
    using var replayCancellation = new CancellationTokenSource(TimeSpan.FromHours(4));
    var replayToken = replayCancellation.Token;

    var connectionString = TradingStore.ConnectionStringForFile(DatabasePath(RepositoryRoot()));
    var replayStore = new TradingStore(connectionString);
    await replayStore.CreateSchemaAsync(replayToken);

    var runner = new ReplayRunner(connectionString, log);
    var tradingOptions = new TradingOptions();
    var riskOptions = new RiskOptions();

    var from = DateOnly.Parse(ArgumentValue("--from") ?? "2026-06-01", CultureInfo.InvariantCulture);
    var to = DateOnly.Parse(ArgumentValue("--to") ?? "2026-08-28", CultureInfo.InvariantCulture);

    // The replay window is historical, so the competition flatten time would close every
    // position on the first cycle. Move it past the window; the live path keeps the real one.
    var replayRisk = riskOptions with
    {
        CompetitionFlattenUtc = new DateTimeOffset(
            to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
    };

    // Replay agents get NO research tools, and that is structural rather than a setting.
    // An Alpaca MCP call in replay reads today's market, and a web search in replay returns
    // everything that has happened since the replay instant. Both are future-data leaks of
    // the worst kind: they would make a historical run look brilliant. Until the replay tool
    // substitutes exist, a replay agent decides from the clamped context alone.
    IStrategyAgent replayAgent = new StubStrategyAgent();

    if (string.Equals(ArgumentValue("--agent"), "llm", StringComparison.OrdinalIgnoreCase))
    {
        var replayFactory = new ChatClientFactory();

        // Empty tool lists, passed explicitly. A live Alpaca call in replay reads today's
        // market and a web search returns everything since the replay instant; either would
        // make a historical run look brilliant for the wrong reason.
        IReadOnlyList<Microsoft.Extensions.AI.AITool> noTools = [];

        var replayPersonas = new List<IPersona>
        {
            new SkepticPersona(replayFactory, log, noTools),
            new QuantPersona(replayFactory, log, noTools),
            new MarketPersona(replayFactory, log, noTools),
            new ExposureRiskPersona(replayRisk),
        };

        var replayProposer = new ProposerPersona(replayFactory, log, noTools);

        var replayMissing = ChatClientFactory.MissingKeys([replayProposer, .. replayPersonas]);
        if (replayMissing.Count > 0)
        {
            log.LogError(
                "Missing model keys: {Keys}. Add them to .env, or omit --agent llm.",
                string.Join(", ", replayMissing));
            return 1;
        }

        var replayValidator = new ProposalPreValidator(
            replayRisk, tradingOptions.TrackedSymbols, TimeProvider.System);

        replayAgent = new WarRoomAgent(
            new WarRoomSession(
                replayProposer, replayPersonas,
                new WarRoomOptions { DiscussionRounds = 1 },
                (operation, request) => replayValidator.Validate(operation, request),
                TimeProvider.System, log),
            TimeProvider.System,
            log);

        log.LogWarning(
            "Replaying with real models and NO research tools. Live tools would read the "
            + "present, which is a future-data leak in a historical run.");
    }

    var replayPolicy = new StrategyPolicy();
    var cycles = 0;
    var opened = 0;
    var closedPositions = 0;

    var result = await runner.RunAsync(
        from, to, stepsPerSession: 1, tradingOptions.StartingEquity,
        async (cycle, token) =>
        {
            // The loop is rebuilt each cycle because the runner owns the gateways, but the
            // policy carries forward: the agent revises it and the next cycle sees it.
            var loop = new TradingLoop(
                cycle.MarketData,
                cycle.Trading,
                replayAgent,
                new RiskGuard(replayRisk, cycle.Clock),
                replayRisk,
                replayStore,
                cycle.Clock,
                log)
            {
                TrackedSymbols = tradingOptions.TrackedSymbols,
                Mode = "replay",
                Policy = replayPolicy,
            };

            var outcome = await loop.RunCycleAsync(token);
            replayPolicy = loop.Policy;

            cycles++;
            opened += outcome.OrdersSubmitted;
            closedPositions += outcome.PositionsClosed;

            if (cycles <= 3 || outcome.OrdersSubmitted > 0)
            {
                log.LogInformation(
                    "  {At:yyyy-MM-dd}: {Offered} candidates, {Opened} opened, {Closed} closed, "
                    + "equity {Equity:N2} USD.",
                    outcome.AtUtc, outcome.CandidatesOffered, outcome.OrdersSubmitted,
                    outcome.PositionsClosed, outcome.Equity);
            }
        },
        replayToken);

    log.LogInformation(
        "Replay finished. {Sessions} sessions, {Cycles} cycles, {Opened} opened, {Closed} closed. "
        + "Equity {Start:N2} -> {End:N2} USD, realized {Pnl:N2}.",
        result.Sessions, result.Cycles, opened, closedPositions,
        result.StartingEquity, result.FinalEquity, result.RealizedPnl);

    log.LogWarning(
        "Replay P&L is evidence about the code, not about the strategy: fills are at daily "
        + "closes and pay no spread, and the stub agent claims no edge.");

    if (!args.Contains("--smoke") && !args.Contains("--check-mcp"))
    {
        return 0;
    }
}

// The live trading session. It runs against the development paper account by default; the
// environment supplies which paper account. AlpacaClients is hard-wired to Environments.Paper
// and no argument can move it (risk guardrail 1).
if (args.Contains("--live"))
{
    using var liveCancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;      // Shut down in order rather than dying mid-order.
        liveCancellation.Cancel();
    };

    var liveToken = liveCancellation.Token;
    var credentials = AlpacaOptions.FromEnvironment();
    using var liveClients = new AlpacaClients(credentials);

    var liveStore = new TradingStore(TradingStore.ConnectionStringForFile(
        DatabasePath(RepositoryRoot())));
    await liveStore.CreateSchemaAsync(liveToken);

    var liveTradingOptions = new TradingOptions();
    var liveRiskOptions = new RiskOptions();

    if (ArgumentValue("--cycle-minutes") is { } cycleText)
    {
        liveTradingOptions = liveTradingOptions with
        {
            CycleInterval = TimeSpan.FromMinutes(double.Parse(cycleText, CultureInfo.InvariantCulture)),
        };
    }

    var liveMarketData = new LiveMarketDataGateway(liveClients);
    var liveTrading = new LiveTradingGateway(liveClients);

    // Fail closed before the first cycle: an account that cannot trade options would make
    // every later rejection look like a strategy decision.
    var liveAccount = await liveTrading.GetAccountAsync(liveToken);

    if (liveAccount.IsTradingBlocked || liveAccount.IsAccountBlocked)
    {
        log.LogError("Trading is blocked on account {Number}. Stopping.", liveAccount.AccountNumber);
        return 1;
    }

    if (liveAccount.OptionsTradingLevel is null or 0)
    {
        log.LogError("Options trading is disabled on this account. Enable it first.");
        return 1;
    }

    ModelContextProtocol.Client.McpClient? liveMcpClient = null;
    IStrategyAgent liveAgent;
    WarRoomAgent? liveWarRoom = null;
    var agentChoice = ArgumentValue("--agent") ?? "llm";

    if (string.Equals(agentChoice, "stub", StringComparison.OrdinalIgnoreCase))
    {
        liveAgent = new StubStrategyAgent();
        log.LogWarning("Running the stub agent. It claims no edge and is for plumbing checks.");
    }
    else
    {
        var apiKey = AlpacaOptions.Secret("ANTHROPIC_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            log.LogError("ANTHROPIC_API_KEY is not set. Add it to .env, or pass --agent stub.");
            return 1;
        }

        // The agent's research tools. Every one is read-only. The MCP connection is the same
        // one --check-mcp proves holds no order tool, and startup fails if it ever does
        // (ADR-005, ADR-006).
        var researchTools = new List<Microsoft.Extensions.AI.AITool>();

        if (!args.Contains("--no-mcp"))
        {
            var mcpOptions = AlpacaMcpOptions.FromEnvironment() with
            {
                ReadOnlyUrl = ArgumentValue("--mcp-url")
                              ?? Environment.GetEnvironmentVariable("Alpaca__Mcp__ReadOnlyUrl")
                              ?? "http://127.0.0.1:8100/mcp",
            };

            var (mcpClient, approved) = await AlpacaMcpClient.ConnectAsync(
                mcpOptions, credentials, loggerFactory, liveToken);

            liveMcpClient = mcpClient;
            researchTools.AddRange(approved);

            log.LogInformation("{Count} approved read-only Alpaca tools given to the agent.", approved.Count);
        }

        if (!args.Contains("--no-web-search"))
        {
            // Server-side search. It widens the text channel beyond Alpaca's Benzinga feed,
            // which matters because reading text is the only remaining alpha hypothesis after
            // ADR-013. Web content is untrusted input: the prompt says so, and RiskGuard
            // bounds what a poisoned page could ever cause. See ADR-017.
            researchTools.Add(new Microsoft.Extensions.AI.HostedWebSearchTool());
            log.LogInformation("Web search is enabled for the agent.");
        }

        // Every seat gets the same read-only tools. The diversity that matters is the model.
        var factory = new ChatClientFactory();

        var personas = new List<IPersona>
        {
            new SkepticPersona(factory, log, researchTools),   // Claude
            new QuantPersona(factory, log, researchTools),     // GPT
            new MarketPersona(factory, log, researchTools),    // Grok
            new ExposureRiskPersona(liveRiskOptions),          // plain C#, no tokens
        };

        var roomProposer = new ProposerPersona(factory, log, researchTools);

        // Fail before the open, not at 09:31. A seat without a key is a dead seat.
        var missing = ChatClientFactory.MissingKeys([roomProposer, .. personas]);
        if (missing.Count > 0)
        {
            log.LogError(
                "Missing model keys: {Keys}. Add them to .env, or pass --agent stub.",
                string.Join(", ", missing));
            return 1;
        }

        var warRoomOptions = new WarRoomOptions
        {
            DiscussionRounds = int.TryParse(ArgumentValue("--rounds"), out var parsedRounds)
                ? parsedRounds
                : 2,
            ApproveThreshold = decimal.TryParse(
                ArgumentValue("--approve-threshold"), NumberStyles.Number,
                CultureInfo.InvariantCulture, out var parsedThreshold)
                ? parsedThreshold
                : 0m,
            AllowRebuttal = !args.Contains("--no-rebuttal"),
        };

        var preValidator = new ProposalPreValidator(
            liveRiskOptions, liveTradingOptions.TrackedSymbols, TimeProvider.System);

        var warRoom = new WarRoomSession(
            roomProposer, personas, warRoomOptions,
            (operation, request) => preValidator.Validate(operation, request),
            TimeProvider.System, log);

        liveWarRoom = new WarRoomAgent(warRoom, TimeProvider.System, log);
        liveAgent = liveWarRoom;

        log.LogInformation(
            "War room seated: proposer + {Seats}. {Rounds} discussion round(s), "
            + "approve threshold {Threshold}.",
            string.Join(", ", personas.Select(persona => $"{persona.Name}[{persona.Provider}]")),
            Math.Clamp(warRoomOptions.DiscussionRounds, 1, WarRoomOptions.MaximumDiscussionRounds),
            warRoomOptions.ApproveThreshold);
    }

    var liveLoop = new TradingLoop(
        liveMarketData,
        liveTrading,
        liveAgent,
        new RiskGuard(liveRiskOptions, TimeProvider.System),
        liveRiskOptions,
        liveStore,
        TimeProvider.System,
        log)
    {
        TrackedSymbols = liveTradingOptions.TrackedSymbols,
        Mode = "live",
    };

    log.LogInformation(
        "Account {Number}: equity {Equity:N2} USD, options level {Level}. Agent: {Agent}.",
        liveAccount.AccountNumber, liveAccount.Equity, liveAccount.OptionsTradingLevel,
        liveAgent.Name);

    var session = new LiveSession(
        liveLoop, liveMarketData, liveTradingOptions, liveRiskOptions, TimeProvider.System, log);

    await session.RunAsync(liveToken);

    if (liveMcpClient is not null)
    {
        await liveMcpClient.DisposeAsync();
    }

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
        DatabasePath(RepositoryRoot())));
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

// Directory.Build.props sends build output to build/bin/<project>/, so the base directory
// is several levels below the repository. Walk up to the solution file rather than guessing
// a relative depth, which would break between a debug run and a container run.
static string RepositoryRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        if (directory.EnumerateFiles("*.slnx").Any() || directory.EnumerateDirectories(".lode").Any())
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    // Deployed, the image holds no solution file. The working directory is the app root.
    return AppContext.BaseDirectory;
}

static string DatabasePath(string repositoryRoot)
{
    var dataDirectory = Path.Combine(repositoryRoot, "data");
    Directory.CreateDirectory(dataDirectory);
    return Path.Combine(dataDirectory, "trader.db");
}
