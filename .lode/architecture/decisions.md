# Architecture Decisions

These decisions are stable. Change one only after an explicit discussion.

## ADR-001: Use Alpaca MCP, not Alpaca CLI

**Decision:** Use the Alpaca MCP server. Use two separate connections.

**Reason:** The C# MCP SDK and `Microsoft.Extensions.AI` let the LLM agents use Alpaca tools
directly. One connection stays open for many calls. A separate trading connection lets
deterministic C# control every account-changing action.

**Change condition:** Return to the CLI only if a required MCP capability is missing or
unstable. Do not use both paths at the same time.

**Supersedes:** Revision 1 of the AVD chose the CLI. That decision is no longer current.

## ADR-002: Do not use Semantic Kernel

**Decision:** Use `Microsoft.Extensions.AI`.

**Reason:** The required flow is: send a question, give a small read-only MCP tool list, let
the model request tools, execute them through the MCP client, return the results, get one
structured forecast. `FunctionInvokingChatClient` gives this behavior. A larger framework
adds complexity, experimental API risk, abstraction layers, and debugging time.

## ADR-003: Keep one .NET application host

**Decision:** Use one .NET console application host.

**Reason:** The hackathon does not require distributed deployment. One process is easier to
build and to debug. The two Alpaca MCP server child processes are the only exception.

## ADR-004: Use SQLite with Dapper

**Decision:** Use one SQLite database with `Microsoft.Data.Sqlite` and **Dapper**.

**Reason:** The data volume is small, so no database server is required. Dapper removes the
hand-written `DbDataReader` and parameter code, which is where mapping mistakes hide. It is
a small dependency and it keeps the SQL visible, so it does not break KISS.

**Effect:** The project still writes its own SQL. Dapper maps the result to a record. A full
ORM (Entity Framework, NHibernate) stays rejected: no change tracking, no migrations, and no
query translation layer.

## ADR-005: LLMs are read-only

**Decision:** LLM agents can use only the read-only Alpaca MCP connection.

**Reason:** A tool that does not exist cannot be selected by the model. This is stronger than
prompt-only protection.

## ADR-006: C# owns money

**Decision:** Only deterministic C# code can use the trading MCP connection.

**Reason:** The model can reason, but it cannot bypass hard limits or submit an order.

## ADR-007: Start with logistic regression

**Decision:** Use ML.NET `SdcaLogisticRegressionBinaryTrainer` for the first historical
probability model.

**Reason:** It is simple, fast, calibrated, and easy to evaluate.

**Change condition:** Move to LightGBM only after replay tests show a clear improvement.

## ADR-008: Start with a fixed symbol list

**Decision:** Start with about 10 liquid symbols.

**Reason:** The system does not need market-wide scanning for a four-day competition. The
system does not ask an LLM to discover which companies exist.

## ADR-009: Use historical replay

**Decision:** The same strategy code must support live mode and replay mode.

**Reason:** The official trading window is too short to learn everything from live results.

## ADR-010: Free data only

**Decision:** Use the free Alpaca Basic plan with the Indicative options feed.

**Reason:** The project will not pay for OPRA access. The FAQ confirms that the latest option
quote and the latest option chain are **real time** on Basic, so the free tier is sufficient
for live decisions.

**Effect:** The strategy must not depend on small pricing differences, because Indicative is
not consolidated OPRA. Replay must account for the 15-minute restriction on historical option
bars and trades. See [market data policy](../alpaca/market-data-policy.md).

## ADR-011: Pin the Alpaca MCP server version

**Decision:** Pin the `external/alpaca-mcp-server` submodule commit and the Docker image tag
for development and for the competition.

**Reason:** MCP tool names and schemas can change. A silent upgrade during the official
trading window is an unnecessary risk.

**Effect:** The host logs the pinned version and validates the required tool names at
startup. Do not upgrade during the official window. See
[MCP safety](../alpaca/mcp-safety.md).

## Related

- [Technology stack](technology-stack.md)
- [Risk guardrails](../trading/risk-guardrails.md)
