# Project Summary

`Xakpc.Alpaca.NøIdea` is an autonomous AI options trading agent for the Alpaca AI Trading
Agents Hackathon. One .NET 10 console application finds, evaluates, opens, monitors, and
closes option positions in an Alpaca **paper** account without human approval for each
trade. Four experts give independent opinions: an ML.NET logistic regression model, an LLM
Research Agent, an LLM Critic Agent, and a deterministic C# Options Evaluator. The system
combines the first three probabilities with reliability weights, compares the result with a
market probability reference from current option data, and trades only when the difference
(the *edge*) is large enough and all hard C# risk rules pass. The application reaches Alpaca
through **two separate Alpaca MCP connections**, and uses SQLite for the full audit trail of
forecasts, tool calls, decisions, and orders. The build follows **KISS and YAGNI**: the
window is four days, so the system builds the simplest thing that meets each requirement.
The core design rule is: **AI can research and forecast, deterministic C# controls risk and
money.** The MCP security rule is: **LLMs see
only a read-only Alpaca MCP toolset; trading tools exist only on a separate connection that
deterministic C# uses.**

## Current state of the code

> Phase 3 was built and measured on branch `phase-3-historical-ml-expert`. **That code is not on this branch**: the
> model lost to the option price, so only the finding was brought across. The library it
> leaves behind (the option ladder, the market calendar, the Brier scoring) is worth
> recovering from that branch when Phase 1 needs it.

The repository has an architecture vision document, a working Alpaca MCP container setup, a
trained Historical ML Expert, and an empty trading host.

| Item | State |
|---|---|
| `alpaca-autonomous-options-agent-avd.md` | Revision 3. **Seeded this lode; no longer a source of truth.** It still describes a weighted ML expert, which measurement retired (ADR-013). |
| `src/Xakpc.Alpaca.NøIdea/Program.cs` | Hello-world only. The trading host is not started. |
| `src/…FeatureGenerator` (branch `phase-3-historical-ml-expert`) | **Done.** Shared library: bar reading, the regular-hours calendar, the contract catalog, the 14 features, and `HistoricalMlExpert`. |
| `src/…Trainer` (branch `phase-3-historical-ml-expert`) | **Done.** Console: builds 1.36M labelled rows, splits by time, trains SDCA, evaluates, writes the report. |
| `tests/Trader.Tests` (branch `phase-3-historical-ml-expert`) | **Done.** 46 xUnit tests, including the no-future-leak checks. |
| `data/historical-model.zip` | **Trained, and measured against the market: it loses.** Test Brier 0.13988 against a 0.24959 base rate, but 0.17142 against the option price's 0.15345 on the same questions. |
| `data/raw/option-bars/` | 416 files, 94 MB. Near-money call ladders, 2024-01-18 to 2026-08-28. |
| `src/Xakpc.Alpaca.NøIdea/Dockerfile` | Builds the .NET host together with the pinned MCP server. The host starts stdio children. |
| `external/alpaca-mcp-server` submodule | Present, pinned. Package version `2.3.0`. |
| `compose.dev.yaml` + `docker/alpaca-mcp.dev.Dockerfile` | Two permanent development servers on `127.0.0.1:8100` and `127.0.0.1:8101`. |
| `alpaca-mcp.http` | Manual `initialize`, `tools/list`, and `tools/call` tests for both servers. |
| `scripts/*.sh` (branch `phase-3-historical-ml-expert`) | Universe screening, history, contracts, and option bars. Deterministic, no LLM. |
| `data/raw/` | 133 MB of bars and news, 2023-01-03 to 2026-08-28, plus 370 MB of expired option contracts, 2024-01-18 to 2026-08-28. Git-ignored. |
| C# MCP clients, SQLite schema, Research and Critic agents, Options Evaluator, trading loop, TUI | Not implemented. |

Phase 3 of the [MVP roadmap](plans/mvp-roadmap.md) is complete: the Historical ML Expert
returns a calibrated probability. Phase 1 is still open, because the C# MCP client code does
not exist. The two phases are independent, and the model was built first because it needs no
live connection.

> **The Historical ML Expert does not beat the option price (ADR-013).** It beats ignorance easily, but
> the market wins in every period, and the wider the two disagree the more wrong the model is.
> So the cheap filter cannot key on a model-versus-market gap, and the ML expert is not a
> source of edge. See [model against the market](replay/model-vs-market.md).
>
> What survives: the ladder-slope market probability (Brier 0.13787, well calibrated) and the
> measurement machinery that will score the remaining three experts.

## Competition window

- The official paper account starts at **$100,000**.
- The P&L window starts **Monday 2026-08-31 at 09:30 ET**.
- The window formally ends **Friday 2026-09-04 at 09:30 ET**.
- Alpaca evaluates total equity as of **end of day Thursday 2026-09-03**.

> **Thursday end of day is the effective final portfolio state.** The system must not depend
> on Friday option-market activity.

See [competition constraints](operations/competition-constraints.md).

## Where to look next

- [Lode map](lode-map.md) — index of all lode files.
- [Terminology](terminology.md) — finance and project terms.
- [Architecture summary](architecture/summary.md) — containers and components.
- [MCP integration](alpaca/mcp-integration.md) — the two connections and the two gateways.
- [MVP roadmap](plans/mvp-roadmap.md) — the implementation order.
