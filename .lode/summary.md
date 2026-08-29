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

The repository has an architecture vision document, an empty application skeleton, and a
working Alpaca MCP container setup.

| Item | State |
|---|---|
| `alpaca-autonomous-options-agent-avd.md` | Complete, revision 3 (MCP + latest FAQ). It is the source document for this lode. |
| `src/Xakpc.Alpaca.NøIdea/Program.cs` | `Console.WriteLine("Hello, World!")` only. |
| `src/Xakpc.Alpaca.NøIdea/Dockerfile` | Builds the .NET host together with the pinned MCP server. The host starts stdio children. |
| `external/alpaca-mcp-server` submodule | Present, pinned. Package version `2.3.0`. |
| `compose.dev.yaml` + `docker/alpaca-mcp.dev.Dockerfile` | Two permanent development servers on `127.0.0.1:8100` and `127.0.0.1:8101`. |
| `alpaca-mcp.http` | Manual `initialize`, `tools/list`, and `tools/call` tests for both servers. |
| C# MCP clients, SQLite schema, experts, trading loop, TUI | Not implemented. |

Phase 1 of the [MVP roadmap](plans/mvp-roadmap.md) is in progress. The MCP servers run; the
C# client code does not exist.

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
