# Observability

The project does not need a separate observability stack. It uses:

- Structured application logs (`Microsoft.Extensions.Logging`).
- SQLite decision records.
- The TUI status view.
- Equity snapshots.
- Agent MCP tool-call records.

## The ten questions

For every trade, the system must be able to answer:

1. What did the ML model predict?
2. What did the Research Agent predict?
3. What did the Critic predict?
4. What data did the agents read?
5. What did the option market reference show?
6. What was the combined probability?
7. Why did the system trade?
8. What risk rules passed?
9. Which order did Alpaca receive?
10. What was the final result?

**This audit trail is important for the hackathon demo.** It supports the *robustness* and
*creativity* judging criteria.

## Where each answer lives

| Question | Source |
|---|---|
| 1, 2, 3 | `forecasts` rows, one for each forecaster |
| 4 | `agent_tool_calls` rows |
| 5 | `evaluation_runs.market_probability`, `market_snapshot_json` |
| 6 | `decisions.combined_probability` |
| 7 | `decisions.edge`, `decisions.action`, `decisions.reason` |
| 8 | `decisions.risk_result` |
| 9 | `orders.alpaca_order_id`, `orders.client_order_id` |
| 10 | `orders.status`, `orders.realized_pnl`, `equity_snapshots` |

The schema is in [storage schema](../storage/schema.md).

## Rejections count

Record a skip and a rejection with the same care as a trade. A demo that shows why the agent
did not trade proves that the guardrails work.

## Related

- [TUI](../trading/tui.md)
- [Storage summary](../storage/summary.md)
