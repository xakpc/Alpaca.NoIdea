# Competition Constraints

```mermaid
timeline
    title Effective competition window
    2026-08-31 09:30 ET : Start
    2026-09-03 15:30 ET : Forced flatten boundary
    2026-09-04 09:30 ET : Formal end
```

```json
{ "CompetitionFlattenUtc": "2026-09-03T19:30:00Z" }
```

These items come from the latest official LabLab / Alpaca FAQ supplied for this project.

## The window and the effective finish line

- The official paper account starts with **$100,000**.
- The P&L window starts **Monday 2026-08-31 at 09:30 ET**.
- The window formally ends **Friday 2026-09-04 at 09:30 ET**.
- Alpaca evaluates the portfolio's total equity **as of end of day Thursday 2026-09-03**.
- Exercises and assignments for options that expire on Thursday 2026-09-03 appear in that
  Thursday end-of-day value.

> **Architecture rule: Thursday end of day is the effective final portfolio state.**

**The system must not depend on new Friday option-market activity to improve the result.**
The `PositionManager` must not wait for a Friday quote or a Friday fill. See
[position lifecycle](../trading/position-lifecycle.md).

The exact Thursday exit policy is still a strategy parameter. The system can close positions
before the Thursday close, or it can allow a supported Thursday-expiration outcome. **The
choice must be deliberate and tested.** See
[open strategy questions](../plans/open-strategy-questions.md).

## Judging

- Alpaca measures performance by **total account equity**, not only by the cash balance.
- Risk-adjusted metrics (Sharpe ratio, Sortino ratio, maximum drawdown) are **not** part of
  the official P&L score.
- P&L is important, but P&L is not the only criterion.
- The judges also evaluate **creativity**, **autonomy**, and **robustness**.
- **There is no live scoreboard.** Do not build one and do not plan around one.
- The official result comes from the dedicated live paper account. Research experiments and
  simulated shocks are **supporting evidence only**, never the score.
- A graphical user interface is not required.

## Submission and hosting

- A hosted application is **not** required when the agent runs autonomously and only places
  orders. A GitHub repository is sufficient.
- A hosted link is required only if the submission contains a demo application that the
  judges must open.
- The repository can stay private during the hackathon.
- Pre-event infrastructure, boilerplate, and the owner's existing libraries can be reused.
- **Pre-event work used in the submission must be disclosed.**

## Data and integration rules

- The free Alpaca Basic tier is permitted. It uses the **Indicative** options feed.
- **Latest option quotes and chains are real time on Basic.** The 15-minute restriction
  applies to historical option bars and trades only.
- Dashboard charts can lag. The agent must decide from API / MCP data.
- The paid OPRA feed is permitted but not required. **This project uses the free feed only.**
- The project must use either Alpaca MCP or Alpaca CLI. **This project uses Alpaca MCP.**
- Alpaca MCP supports option contracts, chains, quotes, Greeks, single-leg orders, and
  multi-leg orders. Order types are market, limit, stop, and stop-limit. **Trailing stops
  are for stocks, not options.**
- There are **no** hackathon restrictions on option strategy type.
- There are **no** restrictions on model provider or hosting infrastructure.
- The project can use a separate paper account for development and testing. **The official
  $100,000 account must not be used as the development account.**

## Requirement mapping

| Requirement | Implementation |
|---|---|
| Autonomous AI trading agent | The trading loop runs without per-trade approval. The Research and Critic agents choose read-only MCP tools on their own. |
| Alpaca Trading API | The Alpaca MCP server calls the Alpaca APIs. |
| MCP or CLI | Alpaca MCP (ADR-001). |
| Options trading | All live trade execution uses option contracts. MCP supports single-leg and multi-leg orders. |
| Basic option data | The system uses real-time latest Indicative quotes and chains through MCP and accepts the historical-data limits. |
| P&L | The system trades the official paper account. Thursday EOD is the effective final state. |
| Creativity | The system combines independent model reviews with deterministic risk checks. |
| Autonomy | The system finds, researches, evaluates, opens, monitors, and closes trades without approval. |
| Robustness | Read-only MCP research, typed SDK trading, deterministic risk rules, paper-mode enforcement, idempotent orders, restart recovery, stale-data checks, and full persistence. |
| UI not required | A small TUI is used for monitoring only. |

## References

- Hackathon page: https://lablab.ai/ai-hackathons/alpaca-ai-trading-agents-hackathon
- Alpaca Trading API: https://docs.alpaca.markets/us/docs/trading-api
- Alpaca Market Data: https://docs.alpaca.markets/us/docs/getting-started-with-alpaca-market-data
- Option chain API: https://docs.alpaca.markets/us/v1.4.2/reference/optionchain
- Alpaca MCP server: https://github.com/alpacahq/alpaca-mcp-server
- Alpaca MCP docs: https://docs.alpaca.markets/us/docs/alpaca-mcp-server
- Alpaca Skills: https://github.com/alpacahq/alpaca-skills
