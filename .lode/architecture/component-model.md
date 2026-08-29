# Internal Component Model

All application components run inside one .NET process. Two Alpaca MCP servers run beside it
as child processes.

```mermaid
flowchart TB
    subgraph TraderHost[".NET Trader Host"]
        Loop[TradingLoop]
        Positions[PositionManager]
        Selector[OptionCandidateSelector]
        Ml[HistoricalMlExpert]
        Research[ResearchAgent]
        Critic[CriticAgent]
        Combiner[ForecastCombiner]
        Options[OptionsEvaluator]
        Risk[RiskGuard]

        MarketGateway[IMarketDataGateway]
        TradingGateway[ITradingGateway]

        ResearchMcp[Read-only McpClient]
        TradingMcp[Trading McpClient]

        ReplayMarket[ReplayMarketDataGateway]
        ReplayTrade[ReplayTradingGateway]

        Store[TradingStore]
        UI[Spectre.Console TUI]
        Clock[TimeProvider]
    end

    ROServer[Read-only Alpaca MCP Server]
    TradeServer[Trading Alpaca MCP Server]
    Alpaca[Alpaca Paper Platform]
    LLM[LLM Provider]
    DB[(SQLite)]

    Loop --> Positions
    Loop --> Selector
    Selector --> Ml
    Selector --> Research
    Research --> Critic

    Ml --> Combiner
    Research --> Combiner
    Critic --> Combiner
    Combiner --> Options
    Options --> Risk
    Risk --> TradingGateway

    Research --> ResearchMcp
    Critic --> ResearchMcp
    MarketGateway --> ResearchMcp
    TradingGateway --> TradingMcp

    ResearchMcp --> ROServer
    TradingMcp --> TradeServer
    ROServer --> Alpaca
    TradeServer --> Alpaca

    Research --> LLM
    Critic --> LLM

    Loop --> Store
    Store --> DB
    ReplayMarket --> DB
    ReplayTrade --> DB

    UI --> Loop
    Clock --> Loop
```

## Responsibilities

| Component | Responsibility |
|---|---|
| `TradingLoop` | Runs one cycle. Calls each step in order. Waits for the next cycle. |
| `PositionManager` | Reviews open positions. Applies exit rules. |
| `OptionCandidateSelector` | Selects a small set of option contracts from the chain. |
| `HistoricalMlExpert` | Returns a numerical probability from ML.NET. |
| `ResearchAgent` | LLM expert. Reads news and market data with read-only MCP tools. |
| `CriticAgent` | LLM expert. Challenges the candidate. Returns its own probability. |
| `ForecastCombiner` | Combines three probabilities with reliability weights. |
| `OptionsEvaluator` | Deterministic C#. Gives the market probability reference and checks quote quality. |
| `RiskGuard` | Applies the hard risk rules. Only this component allows an order. |
| `IMarketDataGateway` | Typed read path to Alpaca. Live and replay implementations. |
| `ITradingGateway` | Typed write path to Alpaca. Live and replay implementations. |
| `McpToolCatalog` | The approved read-only tool allowlist. Filters `ListToolsAsync()`. |
| `TradingStore` | All SQLite reads and writes. |
| `TraderConsole` | Read-only Spectre.Console TUI. |
| `TimeProvider` | Live time or replay time. |

## Three key seams

The architecture has three replacement seams. All must stay clean.

1. **`IMarketDataGateway`** — `AlpacaMcpMarketDataGateway` for live,
   `ReplayMarketDataGateway` for replay.
2. **`ITradingGateway`** — `AlpacaMcpTradingGateway` for live, `ReplayTradingGateway` for
   replay. Replay simulates an order. It never sends one.
3. **`TimeProvider`** — live time or replay time. The same strategy code runs in both modes.

```mermaid
flowchart LR
    A[Trading Engine] --> B{Mode}
    B -->|Live| C[Alpaca MCP gateways]
    C --> D[Local Alpaca MCP servers]
    D --> E[Alpaca Paper APIs]
    B -->|Replay| F[Replay gateways]
    F --> G[(SQLite Historical Data)]
    H[TimeProvider] --> A
```

The LLM agents do not use the gateways. They call approved MCP tools directly through
`ChatOptions.Tools`. In replay mode the agents receive replay tool implementations, not live
MCP tools. See [replay mode](../replay/replay-mode.md).

## Related

- [Application structure](application-structure.md)
- [MCP integration](../alpaca/mcp-integration.md)
- [Live cycle](../trading/live-cycle.md)
- [Replay mode](../replay/replay-mode.md)
