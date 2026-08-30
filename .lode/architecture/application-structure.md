# Application Structure

The actual folder layout. One host project, one test project.

```text
Xakpc.Alpaca.NøIdea.slnx
Directory.Build.props                     # build output goes to build/, outside src/
alpaca-autonomous-options-agent-avd.md    # source AVD, revision 4. History, not truth.
DEVELOPMENT.md                            # human quickstart
compose.dev.yaml                          # one permanent read-only MCP server, port 8100
docker/alpaca-mcp.dev.Dockerfile
external/alpaca-mcp-server/               # submodule, pinned commit
scripts/acquire-news.sh                   # paginated news backfill (curl, offline)
data/                                     # git-ignored: raw JSON, trader.db, model
.lode/                                    # this knowledge base

src/Xakpc.Alpaca.NøIdea/
    Program.cs                            # five modes, thin
    Alpaca/
        AlpacaClients.cs                  # three typed SDK clients, Environments.Paper
        AlpacaOptions.cs                  # credentials, and Secret() for any provider key
        AlpacaMcpClient.cs                # the one read-only MCP connection
        McpToolCatalog.cs                 # the allowlist; forbidden tools fail startup
        Gateways/
            IMarketDataGateway.cs         # reads
            ITradingGateway.cs            # anything that moves money
            MarketContracts.cs            # PriceBar, NewsItem, OptionCandidate, QuoteQuality
            AccountContracts.cs           # AccountState, PositionState, OrderState
            OccOptionSymbol.cs            # contract symbol parser
            LiveMarketDataGateway.cs      # the only place SDK market types are converted
            LiveTradingGateway.cs         # the only write path in the system
    Agents/
        StrategyContracts.cs              # the typed action space, IStrategyAgent
        StubStrategyAgent.cs              # deterministic, for testing the loop
        Room/
            IPersona.cs                   # the seat interface. Mentions no model.
            LlmPersona.cs                 # shared plumbing for a model-backed seat
            ChatClientFactory.cs          # Anthropic, OpenAI, Grok behind one IChatClient
            WarRoomSession.cs             # the five phases
            WarRoomAgent.cs               # presents the room to the loop
            ProposalPreValidator.cs       # structural checks, before tokens are spent
            VoteTally.cs                  # votes to verdict and size
            RoomCost.cs                   # TokenLedger and ModelPricing
            Personas/
                ProposerPersona.cs        # Claude Opus. Full Alpaca toolset.
                SkepticPersona.cs         # Claude Sonnet
                QuantPersona.cs           # GPT
                MarketPersona.cs          # Grok
                ExposureRiskPersona.cs    # plain C#. No model, no cost.
    Trading/
        TradingLoop.cs                    # one cycle, live and replay
        LiveSession.cs                    # pacing and restart for the live run
        RiskGuard.cs                      # the only thing that can allow an order
        RiskOptions.cs                    # hard limits. No agent can reach these.
        StrategyPolicy.cs                 # agent-writable, clamped to RiskOptions
        TradingOptions.cs                 # universe and cycle interval
        PositionReviewTriggers.cs         # when to convene the room over a position
    Replay/
        ReplayClock.cs                    # TimeProvider that only moves forward
        ReplayRunner.cs                   # steps sessions, owns time and nothing else
        ReplayMarketDataGateway.cs        # SQLite, clamped to the clock
        ReplayTradingGateway.cs           # simulates. Holds no Alpaca client.
        MarketCalendar.cs                 # regular hours, Eastern
        OptionLadder.cs                   # the market probability reference
    Storage/
        Schema.sql                        # cache, audit and score tables
        TradingStore.cs                   # audit half. The only class with SQL.
        TradingStore.Cache.cs             # cache half
        BarAvailability.cs                # when a bar became knowable (ADR-015)
        RawJsonPages.cs                   # concatenated API pages reader
        HistoryImporter.cs                # data/raw -> SQLite, idempotent

tests/Xakpc.Alpaca.NøIdea.Tests/
    SafetyTests.cs                        # paper guarantee, tool policy
    RiskGuardTests.cs                     # the limits an agent cannot pass
    WarRoomTests.cs                       # the room flow, with mock personas
    ReplayTests.cs                        # the no-leak rule
    BarAvailabilityTests.cs               # the leak that running it caught
    MarketReferenceTests.cs               # ladder maths and the OCC parser
```

## Rules

- Build output goes to `build/`, never into `src/`.
- Every SQL string lives in `Storage/`.
- `Alpaca/Gateways/` is the only place `Alpaca.Markets` types are converted (ADR-014).
- A strategy number lives in `RiskOptions` or `StrategyPolicy`, never in a C# expression.

## Related

- [Component model](component-model.md)
- [Practices](../practices.md)
- [War room](../war-room/summary.md)
