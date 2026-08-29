# Architecture Decisions

These decisions are stable. Change one only after an explicit discussion.

## ADR-001: Use Alpaca MCP, not Alpaca CLI

**Decision:** Use the Alpaca MCP server. Use two separate connections.

**Reason:** The C# MCP SDK and `Microsoft.Extensions.AI` let the LLM agents use Alpaca tools
directly. One connection stays open for many calls. A separate trading connection lets
deterministic C# control every account-changing action.

**Change condition:** Return to the CLI only if a required MCP capability is missing or
unstable. Do not use both paths at the same time.

**Scope:** this decision governs the **running system**. The offline acquisition scripts in
`scripts/` use the CLI to download training data before a session. That is not a trading
path, and the two never run at the same time.

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

**Outcome:** The model was built and measured. It beats ignorance and loses to the option
price. The trainer choice was never the problem, so a different trainer does not fix it. See
ADR-013.

## ADR-008: Start with a fixed symbol list

**Decision:** Start with about 10 liquid symbols.

**Reason:** The system does not need market-wide scanning for a four-day competition. The
system does not ask an LLM to discover which companies exist.

## ADR-009: Use historical replay

**Decision:** The same strategy code must support live mode and replay mode.

**Reason:** The official trading window is too short to learn everything from live results.

## ADR-010: Free data only, at the default feed

**Decision:** Buy no data upgrade. Use the feeds that the account gives by default, and pass
no `feed` argument in any request.

**Reason:** The project will not pay for an upgrade. A measurement on 2026-08-29 shows that
the development account already returns **SIP** stock data, not IEX only. The earlier
assumption of an IEX and Indicative limit was wrong. Taking the default keeps replay data and
live data on one feed.

**Effect:** The code never names a feed. The account entitlement must be measured again on the
official competition account, because a difference between the two accounts changes the scale
of every volume feature without an error. See
[market data policy](../alpaca/market-data-policy.md).

## ADR-011: Pin the Alpaca MCP server version

**Decision:** Pin the `external/alpaca-mcp-server` submodule commit. Both images build from
that commit, for development and for the competition.

**Reason:** MCP tool names and schemas can change. A silent upgrade during the official
trading window is an unnecessary risk.

**Effect:** The host logs the pinned version and validates the required tool names at
startup. Pin the base image tags too (`python:3.11-slim`, `ghcr.io/astral-sh/uv:0.9`). Do not
upgrade during the official window. See [MCP safety](../alpaca/mcp-safety.md).

## ADR-012: Two MCP run modes

**Decision:** Development runs the Alpaca MCP servers as permanent containers over
`streamable-http`. The deployed image holds the same pinned server and starts it as two
`stdio` child processes.

**Reason:** A deployed container cannot start a sibling container without the Docker socket,
and the Docker socket gives the application root control of the host. Development needs a
server that stays up between debug runs, so a per-run child process is wrong there.

**Effect:** The host selects the transport from configuration: a URL selects HTTP, a command
selects stdio. One pinned submodule feeds both images. See
[MCP run modes](../alpaca/mcp-run-modes.md).

## ADR-013: The Historical ML Expert is not a forecaster

**Decision:** `HistoricalMlExpert` carries **no weight in the forecast combiner** and **no gate
in the live cycle**. The market probability from the option ladder replaces it as the
reference. The ML code and the shared library stay in the repository.

**Reason:** It was measured against the option price on 149,838 questions and lost in every
period, including the period it was fitted on.

| Period | Model | Market |
|---|---:|---:|
| Train | 0.14276 | 0.13155 |
| Validation | 0.14599 | 0.14234 |
| Test | 0.17142 | 0.15345 |

Three facts make this a decision rather than a tuning problem:

- **An equal blend of the two is worse than the market alone** (0.13842 against 0.13787), so
  the model holds no information the price does not already carry.
- **Disagreement tracks model error, not opportunity.** Where the two differ by more than
  0.20, the model said 54%, the market said 63%, and 67% happened. A cheap filter keyed on
  that gap would select the candidates the model understands least.
- **The comparison favours the model and it still loses.** An option price is risk-neutral, so
  it understates the real-world chance slightly; the handicap runs the model's way.

**Effect:** The [cheap filter](../trading/live-cycle.md) step 5 cannot key on a
model-versus-market gap. Candidate reduction must come from contract quality, a tradeable
market-probability band, and the presence of fresh news. The minimum edge and the
cheap-filter threshold stay TBD, because no forecaster that beats the price yet exists.

**What is kept, and why:** on branch `phase-3-historical-ml-expert` the shared library holds bar reading, the market calendar, the
contract catalog, `OptionPriceBook` (the validated market reference) and the Brier and
calibration scoring. Phase 1 and every later phase need all of it. Only the trained model has
no consumer.

**Change condition:** Reinstate the expert only if a formulation beats the option price on a
period that includes a falling market, by a margin exceeding the bid/ask spread. The ideas
already considered and rejected are in [ML hypotheses](../plans/ml-hypotheses.md).

**Supersedes:** It does not reverse ADR-007. The trainer choice was sound; the target was the
problem.

## Related

- [Model against the market](../replay/model-vs-market.md)
- [ML hypotheses](../plans/ml-hypotheses.md)
- [Technology stack](technology-stack.md)
- [Risk guardrails](../trading/risk-guardrails.md)
