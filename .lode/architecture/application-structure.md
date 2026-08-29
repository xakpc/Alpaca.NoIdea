# Application Structure

## Current state

The solution has one project and one file of code.

```text
Xakpc.Alpaca.NøIdea.slnx
Directory.Build.props
alpaca-autonomous-options-agent-avd.md   # source AVD, revision 2 (MCP)
src/Xakpc.Alpaca.NøIdea/
    Program.cs            # Console.WriteLine("Hello, World!") only
    Dockerfile            # Visual Studio default, Linux container, .NET 10 runtime
    Properties/launchSettings.json
build/                    # bin and obj output (set by Directory.Build.props)
cli_0.0.14_windows_amd64/ # old Alpaca CLI binary. Fallback only. No code calls it.
.lode/                    # project memory
```

The `Dockerfile` copies from a `Xakpc.Alpaca.NøIdea/` path. This assumes that the build
context is `src/`. Visual Studio sets this context. Check this if a command-line
`docker build` fails.

## Target structure

The application stays one executable. Add these folders inside the project.

```text
external/
  alpaca-mcp-server/      # git submodule, pinned commit

src/Xakpc.Alpaca.NøIdea/
    Program.cs

    Alpaca/
      AlpacaMcpOptions.cs
      AlpacaMcpClients.cs
      McpToolCatalog.cs

      IMarketDataGateway.cs
      AlpacaMcpMarketDataGateway.cs

      ITradingGateway.cs
      AlpacaMcpTradingGateway.cs

      AlpacaContracts.cs

    Agents/
      ResearchAgent.cs
      CriticAgent.cs
      AgentContracts.cs
      AgentToolPolicy.cs

    Models/
      HistoricalMlExpert.cs
      FeatureGenerator.cs
      TrainingRow.cs
      ForecastResult.cs

    Strategy/
      TradingLoop.cs
      OptionCandidateSelector.cs
      ForecastCombiner.cs
      OptionsEvaluator.cs
      OpportunityPolicy.cs
      RiskGuard.cs
      PositionManager.cs

    Replay/
      ReplayMarketDataGateway.cs
      ReplayTradingGateway.cs
      ReplayRunner.cs
      ReplayClock.cs

    Storage/
      TradingStore.cs
      Schema.sql

    Ui/
      TraderConsole.cs

    Configuration/
      TradingOptions.cs
      RiskOptions.cs
      AgentOptions.cs

tests/
  Trader.Tests/
  Trader.IntegrationTests/
  Trader.ReplayTests/

data/
  trader.db
  historical-model.zip
```

The AVD writes this structure under a project named `Trader/`. This repository uses the
project name `Xakpc.Alpaca.NøIdea`. The folder names inside the project are the same.

## Rules

- One executable. The two Alpaca MCP server child processes are the only exception
  (ADR-003).
- `Strategy/` holds all money logic. No LLM call and no MCP tool name exists in this folder.
- `Agents/` holds LLM code only. It gets approved MCP tools from `Alpaca/McpToolCatalog`. It
  never uses `ITradingGateway`.
- `Alpaca/` is the only folder that knows MCP tool names and MCP result shapes.
- `Storage/` is the only folder that contains SQL.
- Test projects are not created yet. Per [KISS and YAGNI](../practices.md), start with one
  `Trader.Tests` project in Phase 1. Split out the integration and replay projects only when
  the single project becomes hard to run.

## Related

- [Component model](component-model.md)
- [MCP integration](../alpaca/mcp-integration.md)
- [Practices](../practices.md)
- [MVP roadmap](../plans/mvp-roadmap.md)
