# Testing Strategy

Three test projects: `Trader.Tests`, `Trader.IntegrationTests`, and `Trader.ReplayTests`.
None exists yet.

## Unit tests

Test every calculation that touches money, probability, or time:

- Feature calculations.
- Probability combination.
- Brier score.
- Expert weight calculation.
- Option quote validation.
- Risk rules.
- Position exit rules.
- Strategy parameter limits.
- Time calculations.
- The `McpToolCatalog` allowlist logic.
- The mapping from an MCP result to a typed C# contract.

Use an injected `TimeProvider` so that a time test is deterministic.

## MCP integration tests

Run these against the **development** paper account, never the official account:

- Read-only MCP server startup.
- Trading MCP server startup.
- Approved research tool discovery.
- **Confirmation that the read-only server does not expose a trading tool.**
- Account read.
- Clock read.
- Bars read.
- News read.
- Option chain read.
- Position read.
- Paper option order with a safe test size.
- Order lookup by client ID.
- Order cancel.
- Position close.

**Run these tests before each competition session.** They are the detector for an Alpaca MCP
tool name or schema change.

Test a single-leg option order first. Test a multi-leg order only when the strategy needs
one (KISS and YAGNI).

## Replay tests

Confirm that:

- No future data is visible.
- The same live strategy code runs in replay.
- Agent tools use replay data, not live MCP data.
- **A replay run starts no MCP client.**
- ML training uses chronological splits.
- Forecast results are recorded.
- Expert scores update.
- **Orders are simulated, not sent.**

## Failure tests

Force each failure and check the behavior in
[fault handling](fault-handling.md):

- The read-only MCP server exits.
- The trading MCP server exits.
- MCP call timeout.
- An incompatible MCP tool schema.
- A required MCP tool is missing.
- LLM timeout.
- LLM invalid result.
- SQLite locked or unavailable.
- Restart with an open position.
- Duplicate order retry.
- Thursday expiration and assignment behavior, in the development paper account.

The duplicate order retry test is the most important one. It protects real competition
equity.

## Related

- [Fault handling](fault-handling.md)
- [MCP safety](../alpaca/mcp-safety.md)
- [Replay mode](../replay/replay-mode.md)
