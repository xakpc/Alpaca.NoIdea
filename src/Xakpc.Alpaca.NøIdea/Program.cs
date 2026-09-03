using System.Globalization;
using System.Text;
using Alpaca.Markets;
using Microsoft.Extensions.Logging;
using Xakpc.Alpaca.NøIdea.Alpaca;
using Xakpc.Alpaca.NøIdea.Alpaca.Gateways;
using Xakpc.Alpaca.NøIdea.Agents;
using Xakpc.Alpaca.NøIdea.Agents.Room;
using Xakpc.Alpaca.NøIdea.Agents.Room.Personas;
using Xakpc.Alpaca.NøIdea.Observability;
using Xakpc.Alpaca.NøIdea.Research;
using Xakpc.Alpaca.NøIdea.Trading;
using Xakpc.Alpaca.NøIdea.Storage;

// US markets, US formatting. Set before anything parses or formats money: the
// local machine culture uses a comma decimal separator.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en-US");
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");

// A Windows console starts on an OEM code page. That encoding has no symbol the operator view
// draws, so .NET falls back to the best-fit table: '•' becomes 0x07, the terminal bell, and
// '▲ ▼ →' become other C0 control bytes. The run then beeps on almost every line and prints a
// damaged layout. UTF-8 removes the substitution. ConsoleGlyphs still covers the console that
// refuses it.
if (!Console.IsOutputRedirected)
{
    try
    {
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }
    catch (IOException)
    {
        // No console is attached. The ASCII symbol set covers this.
    }
}

// The file is the complete record. The Spectre console selects the information a person needs
// during a run. Stable RunEvents ids keep that selection separate from the trading code.
// The file and logger factory stay at Information so presentation never removes evidence.
var repositoryRoot = RepositoryRoot();
var fileLogPath = Path.Combine(
    repositoryRoot,
    "data",
    "logs",
    $"trader-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.log");
using var fileLoggerProvider = new PlainFileLoggerProvider(fileLogPath);
using var consoleLoggerProvider = new SpectreConsoleLoggerProvider(args.Contains("--live"));

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(LogLevel.Information);
    builder.AddDebug();
    builder.AddProvider(fileLoggerProvider);
    builder.AddProvider(consoleLoggerProvider);
});

var log = loggerFactory.CreateLogger("Trader");
log.LogInformation("Plain file log: {Path}", fileLogPath);

if (!args.Contains("--smoke") && !args.Contains("--check-mcp")
    && !args.Contains("--audit") && !args.Contains("--recover-sittings")
    && !args.Contains("--live"))
{
    log.LogInformation(
        "Nothing to do. Pass --smoke for the order path, --check-mcp for the read-only MCP "
        + "connection, --audit to inspect the durable record, --recover-sittings to close "
        + "sittings that a stopped process left open, or --live to trade. "
        + "Add --dry-run to decide everything and send "
        + "nothing, --once to run a single cycle out of hours, and --cheap to use the "
        + "low-cost model profile.");
    return 0;
}

// Reads the audit trail back. Offline, needs no credentials, and changes nothing: it exists
// so the record can be inspected without a SQLite client, which is also how the demo shows
// that a rejection was stored with the same care as a trade.
if (args.Contains("--audit"))
{
    return await AuditAsync(log);
}

// Maintenance, not part of a session: it gives a sitting that a stopped process left open a
// terminal status, so the integrity check reports what is unfinished instead of what is
// broken. Do not run it while a live host is up.
if (args.Contains("--recover-sittings"))
{
    return await RecoverSittingsAsync(log);
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

    // Relaxing quote freshness is only ever allowed when nothing can be sent.
    if (args.Contains("--allow-stale-quotes"))
    {
        if (!args.Contains("--dry-run"))
        {
            log.LogError(
                "--allow-stale-quotes turns off a safety rule, so it requires --dry-run. "
                + "Refusing to start.");
            return 1;
        }

        liveRiskOptions = liveRiskOptions with { AllowStaleQuotes = true };
        log.LogWarning(
            "Quote-age checks are OFF for this dry run. Candidates may be hours stale. "
            + "This exists to watch the machinery out of hours, not to judge a price.");
    }

    if (ArgumentValue("--cycle-minutes") is { } cycleText)
    {
        liveTradingOptions = liveTradingOptions with
        {
            CycleInterval = TimeSpan.FromMinutes(double.Parse(cycleText, CultureInfo.InvariantCulture)),
        };
    }

    var liveMarketData = new LiveMarketDataGateway(liveClients);

    // A decorator, not a flag: a gateway with no way to submit cannot be bypassed by code
    // that forgets to check one.
    var dryRun = args.Contains("--dry-run");
    ITradingGateway liveTrading = new LiveTradingGateway(liveClients);
    DryRunTradingGateway? dryRunGateway = null;

    if (dryRun)
    {
        dryRunGateway = new DryRunTradingGateway(liveTrading, TimeProvider.System, log);
        liveTrading = dryRunGateway;
    }

    // Account restrictions block new risk inside the cycle. They do not stop startup,
    // because deterministic risk-reducing exits must still get one attempt first.
    var liveAccount = await liveTrading.GetAccountAsync(liveToken);

    if (liveAccount.IsTradingBlocked || liveAccount.IsAccountBlocked)
    {
        log.LogWarning(
            "Trading is blocked on account {Number}. The session will run mandatory exits only.",
            liveAccount.AccountNumber);
    }

    if (liveAccount.OptionsTradingLevel is null or 0)
    {
        log.LogWarning(
            "Options trading is disabled. The session will attempt mandatory liquidation but add no risk.");
    }

    ModelContextProtocol.Client.McpClient? liveMcpClient = null;
    ModelContextProtocol.Client.McpClient? liveKeenableClient = null;
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

        // Web research, over MCP like everything else. It widens the text channel beyond
        // Alpaca's Benzinga feed, which matters because reading text is the only remaining
        // alpha hypothesis after ADR-013. Web content is untrusted input: the prompt says so,
        // and RiskGuard bounds what a poisoned page could ever cause. See ADR-017.
        if (!args.Contains("--no-web-search"))
        {
            var (keenableClient, webTools) = await KeenableMcpClient.ConnectAsync(
                AlpacaOptions.Secret(KeenableMcpClient.KeyVariable), loggerFactory, liveToken);

            liveKeenableClient = keenableClient;
            researchTools.AddRange(webTools);

            log.LogInformation("{Count} web research tools given to the agent.", webTools.Count);
        }

        // Every seat gets the same read-only tools. The diversity that matters is the model.
        var modelProfile = args.Contains("--cheap")
            ? ChatModelProfile.Cheap
            : ChatModelProfile.Standard;
        var factory = new ChatClientFactory(modelProfile);

        log.LogInformation(
            "Model profile: {Profile}. Anthropic={Anthropic}; OpenAI={OpenAi}; Grok={Grok}.",
            args.Contains("--cheap") ? "cheap" : "standard",
            modelProfile.Anthropic,
            modelProfile.OpenAi,
            modelProfile.Grok);

        var personas = new List<IPersona>
        {
            new SkepticPersona(factory, log, researchTools, liveStore),   // Claude
            new QuantPersona(factory, log, researchTools, liveStore),     // GPT
            new MarketPersona(factory, log, researchTools, liveStore),    // GPT
            new ExposureRiskPersona(liveRiskOptions),          // plain C#, no tokens
        };

        var roomProposer = new ProposerPersona(
            factory, log, researchTools, liveTradingOptions, liveStore);

        // Fail before the open, not at 09:31. A seat without a key is a dead seat.
        var missing = ChatClientFactory.MissingKeys([roomProposer, .. personas]);
        if (missing.Count > 0)
        {
            log.LogError(
                "Missing model keys: {Keys}. Add them to .env, or pass --agent stub.",
                string.Join(", ", missing));
            return 1;
        }

        // The old name silently defaulted to 0 when it stopped being read, and 0 is exactly the
        // setting that opened nothing. An unknown argument is not otherwise diagnosed, so fail
        // loudly rather than run a session under a threshold the operator did not choose.
        if (args.Contains("--approve-threshold"))
        {
            log.LogError(
                "--approve-threshold no longer exists. Use --new-trade-approve-threshold. "
                + "The position-review threshold is deliberately fixed at 0.");
            return 1;
        }

        var newTradeThreshold = 0m;

        if (ArgumentValue("--new-trade-approve-threshold") is { } thresholdText)
        {
            if (!decimal.TryParse(
                    thresholdText, NumberStyles.Number, CultureInfo.InvariantCulture,
                    out newTradeThreshold)
                || newTradeThreshold < -1m
                || newTradeThreshold >= 1m)
            {
                log.LogError(
                    "--new-trade-approve-threshold must be a decimal in [-1, 1). Got '{Value}'.",
                    thresholdText);
                return 1;
            }
        }

        var warRoomOptions = new WarRoomOptions
        {
            DiscussionRounds = int.TryParse(ArgumentValue("--rounds"), out var parsedRounds)
                ? parsedRounds
                : 2,
            NewTradeApproveThreshold = newTradeThreshold,

            // Not configurable. See WarRoomOptions.PositionReviewApproveThreshold.
            PositionReviewApproveThreshold = 0m,
            AllowRebuttal = !args.Contains("--no-rebuttal"),
        };

        var preValidator = new ProposalPreValidator(
            liveRiskOptions, liveTradingOptions.TrackedSymbols, TimeProvider.System);

        var warRoom = new WarRoomSession(
            roomProposer, personas, warRoomOptions,
            (operation, request) => preValidator.Validate(operation, request),
            TimeProvider.System, log, liveStore);

        liveWarRoom = new WarRoomAgent(
            warRoom, TimeProvider.System, log, dryRun ? "dry-run" : "live");
        liveAgent = liveWarRoom;

        log.LogInformation(
            "War room seated: proposer[{ProposerProvider}] + {Seats}. {Rounds} discussion round(s), "
            + "new-trade threshold {NewTradeThreshold}, position-review threshold {ReviewThreshold}. "
            + "An open needs an approving seat: {RequireApprovalToOpen}.",
            roomProposer.Provider,
            string.Join(", ", personas.Select(persona => $"{persona.Name}[{persona.Provider}]")),
            Math.Clamp(warRoomOptions.DiscussionRounds, 1, WarRoomOptions.MaximumDiscussionRounds),
            warRoomOptions.NewTradeApproveThreshold,
            warRoomOptions.PositionReviewApproveThreshold,
            warRoomOptions.RequireApprovalToOpen);
    }

    var liveLoop = new TradingLoop(
        liveMarketData,
        liveTrading,
        liveAgent,
        new RiskGuard(liveRiskOptions, TimeProvider.System),
        liveRiskOptions,
        liveTradingOptions,
        liveStore,
        TimeProvider.System,
        log)
    {
        Mode = dryRun ? "dry-run" : "live",
        DryRun = dryRun,
    };

    log.LogInformation(
        "Account {Number}: equity {Equity:N2} USD, options level {Level}. Agent: {Agent}.",
        liveAccount.AccountNumber, liveAccount.Equity, liveAccount.OptionsTradingLevel,
        liveAgent.Name);

    var session = new LiveSession(
        liveLoop, liveMarketData, liveTradingOptions, liveRiskOptions, TimeProvider.System, log)
    {
        RunOnceIgnoringMarketHours = args.Contains("--once"),

        // The session does not own the agent, so it asks the agent what the room has spent.
        // Without this the per-cycle cost was hardcoded to an empty total and every cycle
        // reported zero calls, whatever the room actually did.
        RunningCost = () => liveWarRoom?.TotalCost ?? new RoomCost(),
    };

    string[] seats = liveWarRoom is null
        ? []
        : ["proposer", "skeptic", "quant", "market", "exposure"];

    log.LogInformation(
        RunEvents.RunStarted,
        "Run started. Mode {Mode}. Dry run {DryRun}. Seats: {Seats}.",
        dryRun ? "dry-run" : "live", dryRun,
        seats.Length == 0 ? "none" : string.Join(", ", seats));

    var auditFailed = false;
    try
    {
        await session.RunAsync(liveToken);
    }
    catch (AuditPersistenceException error)
    {
        auditFailed = true;
        log.LogCritical(error, "Durable audit failed. The live session is stopped.");
    }

    var totalCost = liveWarRoom?.TotalCost ?? new RoomCost();

    log.LogInformation(
        RunEvents.RunStopped,
        "Run stopped after {Cycles} cycle(s). Total {Cost}.", session.CyclesRun, totalCost);

    // Itemised per seat, because a single total hides which model is the expensive one.
    foreach (var cost in totalCost.PerPersona)
    {
        log.LogInformation(
            RunEvents.RoomSpend,
            "  {Persona} ({Model}): {Calls} calls, {Tokens:N0} tokens, {Usd}",
            cost.Persona, cost.Model, cost.Calls, cost.TotalTokens,
            cost.EstimatedUsd is { } usd ? $"~{usd:F4} USD" : "unpriced");
    }

    if (dryRunGateway is { Planned.Count: > 0 } planned)
    {
        log.LogWarning(
            "Dry run: {Count} order(s) were decided and none were sent. Notional {Notional:N2} USD.",
            planned.Planned.Count, planned.PlannedNotional);
    }

    if (liveMcpClient is not null)
    {
        await liveMcpClient.DisposeAsync();
    }

    if (liveKeenableClient is not null)
    {
        await liveKeenableClient.DisposeAsync();
    }

    return auditFailed ? 1 : 0;
}

if (args.Contains("--smoke") || args.Contains("--check-mcp"))
{
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
            var lastTrade = await alpaca.StockData.GetLatestTradeAsync(
                new LatestMarketDataRequest(symbol) { Feed = MarketDataFeed.Iex }, ct);
            var spot = lastTrade.Price;
            log.LogInformation("{Symbol} last trade {Price:N2} at {Time:u}.", symbol, spot, lastTrade.TimestampUtc);

            // 4. A near-money call ladder over the next two weeks. Use the free Indicative feed.
            var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
            var chain = await alpaca.OptionsData.GetOptionChainAsync(new OptionChainRequest(symbol)
            {
                OptionsFeed = OptionsFeed.Indicative,
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
                CorrelationId = clientOrderId,
                ClientOrderId = clientOrderId,
                OptionSymbol = contractSymbol,
                Side = nameof(OrderSide.Buy),
                Quantity = 1,
                OrderType = nameof(OrderType.Limit),
                LimitPrice = limitPrice,
                SubmittedUtc = time.GetUtcNow().ToUnixTimeSeconds(),
                Status = OrderLifecycle.Reserved.ToString(),
                Mode = "smoke",
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
}

return 0;

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

async Task<int> RecoverSittingsAsync(ILogger log)
{
    using var recoveryCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    var recoveryToken = recoveryCancellation.Token;

    var recoveryStore = new TradingStore(TradingStore.ConnectionStringForFile(
        DatabasePath(RepositoryRoot()), readOnly: false));
    var schemaVersion = await recoveryStore.SchemaVersionAsync(recoveryToken);
    if (schemaVersion != TradingStore.CurrentSchemaVersion)
    {
        log.LogError(
            "Audit schema is {Actual}; expected {Expected}. Start with a clean database file.",
            schemaVersion, TradingStore.CurrentSchemaVersion);
        return 1;
    }

    var interrupted = await recoveryStore.InterruptedSittingsAsync(recoveryToken);
    if (interrupted.Count == 0)
    {
        log.LogInformation("No sitting is open. Nothing to recover.");
        return 0;
    }

    foreach (var proposalId in interrupted)
    {
        log.LogInformation("  Interrupted sitting {Proposal}.", proposalId);
    }

    var marked = await recoveryStore.RecoverInterruptedSittingsAsync(
        DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        "The process stopped before the sitting completed. Recovered by --recover-sittings.",
        recoveryToken);

    log.LogInformation("Marked {Marked} sitting(s) abandoned. Run --audit to confirm.", marked);
    return 0;
}

async Task<int> AuditAsync(ILogger log)
{
    using var auditCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    var auditToken = auditCancellation.Token;

    var auditStore = new TradingStore(TradingStore.ConnectionStringForFile(
        DatabasePath(RepositoryRoot()), readOnly: true));
    var schemaVersion = await auditStore.SchemaVersionAsync(auditToken);
    if (schemaVersion != TradingStore.CurrentSchemaVersion)
    {
        log.LogError(
            "Audit schema is {Actual}; expected {Expected}. Start with a clean database file.",
            schemaVersion, TradingStore.CurrentSchemaVersion);
        return 1;
    }

    foreach (var (table, total) in await auditStore.AuditRowCountsAsync(auditToken))
    {
        log.LogInformation("  {Table}: {Total:N0} rows.", table, total);
    }

    foreach (var entry in await auditStore.RecentDecisionsAsync(
        int.TryParse(ArgumentValue("--last"), out var last) ? last : 10, auditToken))
    {
        log.LogInformation(
            "  {At} {Mode} {Purpose} {Outcome} {Action} {Symbol} risk=[{Risk}] "
            + "tools={Tools} proposal={Proposal} order={Order}:{OrderStatus}",
            DateTimeOffset.FromUnixTimeSeconds(entry.TimestampUtc).ToString("u"),
            entry.Mode, entry.Purpose, entry.Outcome, entry.Action,
            entry.OptionSymbol ?? "-", entry.RiskResult ?? "-", entry.ToolCallCount,
            entry.ProposalId ?? "-", entry.CorrelationId ?? "-", entry.OrderStatus ?? "-");
    }

    var issues = await auditStore.AuditIntegrityAsync(auditToken);
    foreach (var issue in issues)
    {
        log.LogError("Audit integrity fault {Code}: {Reference}.", issue.Code, issue.Reference);
    }

    return issues.Count == 0 ? 0 : 1;
}
