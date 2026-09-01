# Architecture Summary

One .NET 10 console host reads the Alpaca paper account, builds a tradeable option catalog,
runs a multi-provider war room, applies deterministic risk rules, and submits paper orders.
SQLite stores audit evidence and the minimum restart state.

```mermaid
flowchart LR
    O[Operator] --> H[Console host]
    H --> M[Read-only Alpaca MCP]
    H --> K[Read-only Keenable MCP]
    H --> S[Typed Alpaca SDK]
    H --> D[(SQLite)]
    M --> A[Alpaca data]
    S --> A
    S --> P[Paper account]
    K --> W[Web pages]
```

```csharp
var loop = new TradingLoop(marketData, trading, agent, riskGuard, options, store, time, logger);
```

## Components

| Component | Contract |
|---|---|
| `LiveSession` | Read the market clock, run cycles, wait after each cycle, and stop at a closed market. |
| `TradingLoop` | Reconcile state, run exits and reviews, build context, apply risk, and record decisions. |
| `WarRoomSession` | Propose, pre-validate, analyse, discuss, rebut, and vote. |
| `RiskGuard` | Own every deterministic permission to add account risk. |
| `ITradingGateway` | Own account reads and broker writes through the typed SDK. |
| `IMarketDataGateway` | Map current Alpaca data to project-owned records. |
| `TradingStore` | Own SQL, transactions, audit rows, and durable policy state. |
| `SpectreConsoleLoggerProvider` | Map structured run events to the operator console. |

The application has one source project and one xUnit test project. Source folders separate
agents, Alpaca gateways, research, storage, trading, and observability. The host has no market
simulation, data import, web UI, or interactive terminal commands. The Spectre console is a
read-only operator view.

## Boundaries

- Personas return typed data. They do not receive a broker, shell, file, or database tool.
- The Alpaca MCP connection is read-only. The optional Keenable MCP connection is also
  read-only.
- The typed Alpaca SDK is the only broker-write path. All SDK clients use the paper
  environment.
- Alpaca owns current account, position, and order state. SQLite owns evidence and restart
  state.
- The Spectre console is curated. The timestamped plain file keeps the complete clipped log.
- A console presentation fault cannot stop trading or durable audit work.
- A normal process stops when the market is closed. A deployed image starts one Alpaca MCP
  child over `stdio`. Development uses one HTTP container on `127.0.0.1:8100`.

## Technology

The main dependencies are `Alpaca.Markets`, `Microsoft.Extensions.AI`, the Anthropic and
OpenAI-compatible clients, Model Context Protocol, SQLite, Dapper, Toon, Spectre.Console,
logging, and xUnit. Spectre renders event-based panels, tables, colors, and active model rows.

```xml
<TargetFramework>net10.0</TargetFramework>
```

## Related lodes

- [Architecture decisions](decisions.md)
- [Project summary](../summary.md)
- [Live cycle](../trading/live-cycle.md)
- [Alpaca integration](../alpaca/mcp-integration.md)
- [Storage schema](../storage/schema.md)
