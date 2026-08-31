# Project Summary

`Xakpc.Alpaca.NøIdea` is an autonomous AI options trading agent for the Alpaca AI Trading
Agents Hackathon. One .NET 10 console application finds, evaluates, opens, monitors, and
closes option positions in an Alpaca **paper** account without human approval for each
trade. Decisions come from a **war room** of agents running on three different model
providers: a proposer searches the allowed universe and puts one operation forward, reviewers
analyse it independently, the room debates, the proposer defends or modifies or withdraws, and
everyone votes privately. Confidence-weighted votes set the position size, and a deterministic
`RiskGuard` validates again immediately before submission. The application reaches Alpaca
through **one read-only MCP connection** for the agents and the typed `Alpaca.Markets` SDK for
anything that moves money, and it uses SQLite for the full audit trail of proposals, analyses,
votes, decisions, and orders. The build follows **KISS and YAGNI**: the window is four days, so
the system builds the simplest thing that meets each requirement. The core design rule is:
**agents decide what they want to do; deterministic C# decides what they are permitted to do.**
The MCP security rule is: **LLMs see only a read-only Alpaca MCP toolset, and no MCP server
this host runs holds an order tool at all.**

## Current state of the code

> Phase 3 was built and measured on branch `phase-3-historical-ml-expert`. **That code is not on this branch**: the
> model lost to the option price, so only the finding was brought across. The library it
> leaves behind (the option ladder, the market calendar, the Brier scoring) is worth
> recovering from that branch when Phase 1 needs it.

The repository has an architecture vision document, a working Alpaca MCP container setup, a
trained Historical ML Expert, and an empty trading host.

| Item | State |
|---|---|
| `alpaca-autonomous-options-agent-avd.md` | Revision 4. **Seeded this lode; no longer a source of truth.** Revision 4 records that the war room replaced the weighted combiner (ADR-019) and that the ML expert is excluded (ADR-013). |
| `src/Xakpc.Alpaca.NøIdea/Program.cs` | Five modes. `--live` runs the war room against the paper account; `--smoke` runs the full order path; `--check-mcp` proves the read-only tool isolation; `--import-history` loads `data/raw` into SQLite; `--replay` runs the offline replay. The trading loop runs. |
| `src/…/Alpaca/AlpacaClients.cs` | **Done.** Three typed `Alpaca.Markets` clients on `Environments.Paper`. |
| `src/…/Alpaca/AlpacaMcpClient.cs` + `McpToolCatalog.cs` | **Done.** One read-only MCP connection; 34 tools discovered, 25 approved, forbidden tools fail startup. |
| `src/…/Alpaca/Gateways/` | **Done.** `IMarketDataGateway` and `ITradingGateway` over project-owned records, with the two live SDK implementations and `OccOptionSymbol` (ADR-014). |
| `src/…/Storage/` | **Done.** The full schema, `TradingStore` (cache, orders, and the audit trail), `RawJsonPages`, `HistoryImporter`, and `BarAvailability` (ADR-015). `TradingLoop` fills `evaluation_runs`, `forecasts` and `decisions` for accepted and rejected actions alike; `--audit` reads them back. `agent_tool_calls` stays empty (ADR-026). |
| `src/…/Replay/` | **Done.** `ReplayClock`, `ReplayMarketDataGateway`, `ReplayTradingGateway`, `ReplayRunner`, `MarketCalendar`, and `OptionLadder`. |
| `data/trader.db` | **Populated.** 122,444 bars, 16,088 news items, 195,824 contracts, 151,718 option bars, for 2026-02-01 to 2026-08-28. |
| `scripts/acquire-news.sh` | **Done.** The paginated news backfill. 25,187 items. The old script captured one page per symbol. |
| `tests/Trader.Tests` | **Done.** 119 tests: the paper guarantee, the tool policy, the no-leak rule, bar availability, the ladder, the OCC parser, the risk limits, the war-room flow with mock personas, the dry-run gateway, the audit trail, who owns a research tool, and the model transcript with its per-seat event ids. |
| `src/…FeatureGenerator` (branch `phase-3-historical-ml-expert`) | **Done.** Shared library: bar reading, the regular-hours calendar, the contract catalog, the 14 features, and `HistoricalMlExpert`. |
| `src/…Trainer` (branch `phase-3-historical-ml-expert`) | **Done.** Console: builds 1.36M labelled rows, splits by time, trains SDCA, evaluates, writes the report. |
| `tests/Trader.Tests` (branch `phase-3-historical-ml-expert`) | **Done.** 46 xUnit tests, including the no-future-leak checks. |
| `data/historical-model.zip` | **Trained, and measured against the market: it loses.** Test Brier 0.13988 against a 0.24959 base rate, but 0.17142 against the option price's 0.15345 on the same questions. |
| `data/raw/option-bars/` | 416 files, 94 MB. Near-money call ladders, 2024-01-18 to 2026-08-28. |
| `src/Xakpc.Alpaca.NøIdea/Dockerfile` | Builds the .NET host together with the pinned MCP server. The host starts stdio children. |
| `external/alpaca-mcp-server` submodule | Present, pinned. Package version `2.3.0`. |
| `compose.dev.yaml` + `docker/alpaca-mcp.dev.Dockerfile` | **One** permanent development server on `127.0.0.1:8100`. The trading server was deleted (ADR-001). |
| `scripts/*.sh` (branch `phase-3-historical-ml-expert`) | Universe screening, history, contracts, and option bars. Deterministic, no LLM. |
| `data/raw/` | 133 MB of bars and news, 2023-01-03 to 2026-08-28, plus 370 MB of expired option contracts, 2024-01-18 to 2026-08-28. Git-ignored. |
| `src/…/Trading/` | **Done.** `TradingLoop`, `RiskGuard`, `RiskOptions`, `StrategyPolicy`, `TradingOptions`, `LiveSession`, `PositionReviewTriggers`. |
| `src/…/Agents/Room/` | **Done.** `WarRoomSession`, `IPersona`, five persona classes, `VoteTally`, `TokenLedger`, `ProposalPreValidator`, `ChatClientFactory` (Anthropic, OpenAI, Grok). |
| `src/…/Agents/` | **Done.** The typed action space and `StubStrategyAgent`. |
| `src/…/Observability/` | **Done.** `RunEvents`, the permanent `EventId` of each event that tells the story of a run, and `ChatTranscript`, which writes each seat's whole conversation with its model — the prompts, the turns, each tool call with its arguments, each answer, and a tally — under one block of ids per seat (ADR-027). There is no terminal view: the run writes to the console (ADR-024). |
| Options Evaluator as a separate class | Not implemented. The evaluator's checks live in `RiskGuard.CheckContract`. |

**The strategy is decided by a war room (ADR-019).** A proposer searches the allowed universe
and puts one operation forward. Reviewers analyse it **independently**, then debate, then the
proposer may defend, modify or withdraw, then everyone votes **privately**. Confidence-weighted
votes set the position size; `RiskGuard` then validates again immediately before submission and
cannot be outvoted.

One class, `WarRoomSession`, serves both new trades and position reviews. A persona is a class
rather than a configuration row, and the seats run on **three different models**  --  Claude, GPT
and Grok  --  because a room of one model arguing with itself shares that model's blind spots. One
seat, `exposure`, is plain C#: it computes portfolio arithmetic, costs nothing, and cannot
hallucinate.

`TokenLedger` reports what each sitting cost. Token counts are fact; the dollar figure is an
estimate and a floor.

`--live` runs the loop against the paper account. `--replay --agent llm` runs the same room over
stored history with **no research tools at all**, because a live tool in a historical run reads
the present and is a future-data leak.

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
