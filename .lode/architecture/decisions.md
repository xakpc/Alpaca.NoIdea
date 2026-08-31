# Architecture Decisions

These decisions are stable. Change one only after an explicit discussion.

## ADR-001: MCP serves the LLM agents; deterministic C# uses the Alpaca SDK

**Decision:** The LLM agents reach Alpaca through **one read-only MCP connection**.
Deterministic C# reaches Alpaca through the typed **`Alpaca.Markets`** NuGet SDK. The Alpaca
CLI is not called by application code.

**Reason:** MCP and the CLI are both LLM-facing wrappers over the same REST API. Nothing
deterministic benefits from either: MCP returns text shaped for a model to read, and parsing
that in money code is where fail-closed defects hide. The SDK returns `IAccount`, `IOrder`,
`IOptionSnapshot` and `IGreeks` already typed with `decimal`, which removes the parsing layer
rather than organising it.

The hackathon requirement to use Alpaca MCP or the Alpaca CLI is met by the agents, which use
MCP for every research call.

**Effect:** There is **no trading MCP connection**. The second server was deleted from
`compose.dev.yaml` and from the deployed image. See ADR-012.

**Scope:** the offline acquisition scripts in `scripts/` may use the CLI to download training
data before a session. That is not a trading path, and the two never run at the same time.

**Supersedes:** Revision 1 of the AVD chose the CLI. The earlier form of this ADR chose MCP
for everything and forbade using both paths at once; the split is now by **caller**, not by
protocol.

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

**Decision:** Only deterministic C# code can submit, replace, cancel, or close an order, and it
does so through the `Alpaca.Markets` SDK.

**Reason:** The model can reason, but it cannot bypass hard limits or submit an order.

**The isolation is now absolute, not layered.** Under the earlier two-connection design this
rule depended on the `ALPACA_TOOLSETS` split holding across server upgrades. Since ADR-001,
**no MCP server this host runs holds an order tool at all**, so there is no toolset to
misconfigure and no write tool for an allowlist to miss. `McpToolCatalog.AssertNoForbiddenTool`
remains as the check that proves this at startup rather than assuming it: it fails the process
if the connection ever exposes an account or order tool.

**Measured:** adding `account,trading` to the read-only toolset makes the host refuse to start,
listing all 20 forbidden tools. The normal configuration exposes 34 tools, of which 25 are
approved for agents and none can reach the account.

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

## ADR-012: One MCP server, two run modes

**Decision:** There is **one** Alpaca MCP server, read-only. Development runs it as a permanent
container over `streamable-http` on `127.0.0.1:8100`. The deployed image holds the same pinned
server and starts it as a single `stdio` child process.

**Reason:** A deployed container cannot start a sibling container without the Docker socket, and
the Docker socket gives the application root control of the host. Development needs a server
that stays up between debug runs, so a per-run child process is wrong there.

The second server is gone because ADR-001 moved deterministic C# to the `Alpaca.Markets` SDK,
leaving the trading connection with no consumer.

**Effect:** The host selects the transport from configuration: `Alpaca__Mcp__ReadOnlyUrl`
selects HTTP, `Alpaca__Mcp__ServerCommand` selects stdio. **Both set, or neither, is a startup
failure.** `Alpaca__Mcp__TradingUrl` and `Alpaca__Mcp__TradingToolsets` no longer exist. See
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

## ADR-014: Gateway contracts use project records, not SDK types

**Decision:** `IMarketDataGateway` and `ITradingGateway` return records that this project owns.
They do not return `Alpaca.Markets` interfaces such as `IOrder`, `IPosition`, or
`IOptionSnapshot`.

**Reason:** The replay implementation cannot construct an SDK type. The SDK interfaces have
internal implementations only. An SDK type in the signature makes the seam impossible to
implement, and it forces replay onto a second code path. The seam exists to prevent exactly
that.

**Effect:** `Alpaca/Gateways/` holds `AccountState`, `PositionState`, `OrderState`,
`OrderRequest`, `PriceBar`, `NewsItem`, `LatestTrade`, `MarketClock`, and `OptionCandidate`.
`LiveMarketDataGateway` and `LiveTradingGateway` are the only classes that convert an SDK type.
`OccOptionSymbol` parses a contract symbol, because the SDK exposes no parser and both the
live chain and the replay chain need one.

**`OptionCandidate.Quality` is part of this decision.** It has four values: `TwoSided`,
`OneSided`, `Missing`, and `UnknownHistorical`. A replayed contract always carries
`UnknownHistorical`, a null bid, a null ask, and a null delta. Alpaca serves no historical
quote and no historical greek, so a nullable bid alone would let a spread rule read a default
and pass. `IsTradeableQuote` is false for every historical candidate. Quote-quality rules are
testable in live paper trading only.

## ADR-015: Replay filters on bar availability, never on the bar timestamp

**Decision:** The `bars` and `option_bars` tables carry an `available_utc` column. It holds the
instant the bar became knowable. Every replay read filters on that column.
`Storage/BarAvailability` is the only place that computes it.

**Reason:** A bar timestamp is the **start** of its interval, and the bar carries the **close**
of that interval. A daily bar stamped `05:00Z` holds the 16:00 ET price. A filter on the
timestamp therefore gives a 09:30 cycle more than six hours of the future.

**Measured:** the first replay run did filter on the timestamp. It reported a market
probability of 0% to 10% where about 50% was correct, because each cycle read the closing
premium of the session it was still inside. The numbers looked wrong enough to notice. A
smaller leak would not have.

**The rule is deliberately late.** A daily equity bar becomes available at 20:00 Eastern, after
extended trading. A daily option bar becomes available at 16:15 Eastern. An intraday bar
becomes available when its interval ends. Where the true end of an interval is uncertain, the
rule rounds later. A late rule costs one session of information. An early rule invalidates
every measurement the run produces.

**Effect:** replay steps one cycle per session by default. Historical option prices are daily
closes, so more cycles in one session give the strategy no new option data.

## ADR-016: The agent directs the strategy; C# bounds it

> **Superseded by ADR-019.** The principle stands â agents decide what they want, C# decides
> what they are permitted â but the single decider became a room.

**Decision:** The LLM chooses what to trade **and rewrites the strategy parameters**. It
answers with a `StrategyDecision`: a list of typed actions, and optionally a new
`StrategyPolicy`. Deterministic C# executes, and `RiskGuard` checks every action against
`RiskOptions`, which no agent output can modify.

**Reason:** ADR-013 left the project with **no forecaster that beats the option price**. The
minimum edge and the cheap-filter threshold had no signal to key on, so the TBD numbers in
[strategy parameters](../trading/strategy-parameters.md) could not be calibrated. The choice
was between freezing guessed constants and letting the agent own them and revise them from
measured results. Guessed constants carry no more evidence and do not adapt.

**The action space is closed.** `StrategyActionKind` is `Hold`, `OpenCall`, `OpenPut`, or
`ClosePosition`. The agent emits no code, no SQL, and no shell command. An action outside the
enum cannot be expressed, so it cannot be executed.

**Three limits make this safe:**

1. **The agent may only name a contract the harness offered that cycle.** A hallucinated
   symbol is rejected before it reaches the broker.
2. **`StrategyPolicy.ClampTo` bounds every revision.** A policy may narrow the strategy; it
   can never widen the risk. The clamp lives in the `TradingLoop.Policy` setter, so no caller
   can install an unclamped policy either.
3. **`RiskOptions` is C# only.** Per-trade risk, total exposure, position counts, the daily
   loss halt, and the Thursday flatten are unreachable from any model output.

**The cycle order is part of the decision.** Deterministic exits run **before** the agent is
called, so a hung or malfunctioning agent cannot prevent a stop-loss. The risk guard runs
**after** it, on every action. An agent exception is a skipped cycle, never an open position.

**Effect:** `.lode/trading/strategy-parameters.md` no longer holds a TBD list waiting on
replay. The values are policy defaults the agent owns. The hard bounds are chosen, not
measured, and are recorded as chosen.

## ADR-017: The agent gets read-only research tools, including web search

> **Superseded by ADR-019 and ADR-020.** Tools and web search are unchanged and still
> read-only; they now belong to each persona rather than to one agent.

**Decision:** The strategy agent receives the 25 approved read-only Alpaca MCP tools **and**
a hosted web-search tool. It also receives `submit_decision`, which is its output channel.

**Reason:** ADR-013 removed price history as a source of edge, leaving **the agent reading
text** as the only remaining alpha hypothesis. Before this decision the agent read Alpaca
news headlines and nothing else, and it could not investigate at all: the approved MCP tools
were discovered at startup and then discarded. An agent that cannot look anything up is not a
research agent.

**`submit_decision` is not an action.** It is a schema. It executes nothing, reaches no
broker, and everything it returns still passes `RiskGuard`. It replaced free-text JSON
because a parse failure degraded to a silent hold: a drifted field name produced an agent
that quietly did nothing, which in a four-day run is indistinguishable from an agent that
chose not to trade. A tool call is schema-validated by the API, so a bad answer is a visible
error.

**This does not weaken ADR-005 or ADR-006.** Every tool is read-only. No MCP server this host
runs holds an order tool, and `McpToolCatalog.AssertNoForbiddenTool` still fails startup if
one appears.

### The prompt-injection surface, and why it is acceptable

Web search puts text that this project does not control in front of the model. A hostile or
low-quality page can influence a decision. **The exposure is bounded, not removed:**

- The agent cannot place an order. It returns data.
- It can only name a contract the harness offered that cycle.
- `RiskGuard` still caps the position at 2% of equity, the account at 10%, four positions, and
  a 5% daily loss halt.

So the worst case is a bad trade inside the caps, not an unbounded loss. The system prompt
also tells the agent to treat tool output, and web content in particular, as untrusted
information rather than as instruction.

### Replay gets no research tools, structurally

**A live tool in a replay run is a future-data leak.** An Alpaca MCP call reads today's
market. A web search returns everything that has happened since the replay instant. Either
would make a historical run look brilliant for the wrong reason.

`--replay --agent llm` therefore passes an empty tool list. This is a code path, not a
setting. The replay tool substitutes described in
[replay mode](../replay/replay-mode.md) are the proper fix and are not built.

## ADR-018: The agent room, and the decider always has the final word

> **Superseded by ADR-019.** One round of proposer-and-critic became the five-phase war room.
> Two rules survived intact: no participant may veto, and a failed seat is never an approval.

**Decision:** `AgentRoom` runs a discussion between any number of agents and then asks the
**decider** to close it. Participants speak in the order configured, each reading the market
context and everything said before it. The decider is the only agent that can produce a
`StrategyDecision`, it always speaks last, and its answer may be to do nothing. `RiskGuard`
bounds whatever it decides.

**Reason:** the proposer-and-critic pair was one arrangement of a more general idea. A room
that does not know what its participants are can seat a critic, a risk analyst, a macro reader,
a contrarian or a quant in any order, and a new voice becomes a new role prompt rather than new
code. `RoomRoles` holds the presets; `--room critic,quant,macro` seats them in that order.

**No participant can veto.** This is the rule the whole arrangement rests on, and it is
inherited from the original Critic design in
[critic agent](../war-room/summary.md): **a veto is not testable.** There is no way to tell
a participant that saves money from one that is merely timid, and a timid participant that
blocks everything produces a four-day run that holds cash and returns nothing.

**Every participant commits to a probability** on any contract it has a view about, so whether
a seat earns its tokens is settled by measurement rather than by taste. A speaker who only
narrates risk cannot be wrong: risks always exist, and naming them always sounds astute.

### The decider is not a vote-counter

Participants advise. They do not vote, and a unanimous room does not overrule the decider — a
majority veto is still a veto, and a uniformly cautious room would stop all trading. The prompt
tells the decider to weigh the speakers rather than count them, and that several speakers
repeating one another is still one argument.

### The two silent failure modes

| Failure | What it looks like | What catches it |
|---|---|---|
| The decider capitulates to every objection | The system stops trading and returns nothing | `RoomRecord.DeciderChangedItsMind`, and the prompt says dropping every challenged trade is a loss, not safety |
| Participants rubber-stamp | Cost multiplies, nothing changes | Every contribution is recorded with its stance and probability |

### Calls are not spent on a discussion that cannot matter

- An opening proposal that **trades nothing** does not convene the room. Holding has no
  downside for a participant to find.
- A participant that **fails, throws, or says nothing** is recorded, skipped, and the
  discussion continues.
- Rounds are capped at `AgentRoomOptions.MaximumRounds`, so a configuration mistake cannot burn
  the budget.

The second one is a guardrail rather than politeness. Without it a participant exception would
reach the loop, the loop would skip the cycle, and any broken seat would silently cancel every
trade — the exact veto power this ADR denies. Tests pin it.

**Cost scales with the room.** One cycle costs one decider call per participant round plus the
opening and closing decider calls, each possibly with research tool calls behind it. A room of
three over two rounds is roughly eight model calls per cycle before tools.

**Effect:** `--room none` runs the decider alone. `--room critic` is the default.
`--no-opening-proposal` suits a room of analysts reading the market rather than critiquing a
proposal, and saves one decider call per cycle.

## ADR-019: The war room, and it is one class for both jobs

**Supersedes ADR-016, ADR-017 and ADR-018,** which described a single decider, then a decider
with tools, then a decider and one critic. The shape is now a room.

**Decision:** `WarRoomSession` runs five phases: the proposer proposes, C# pre-validates,
reviewers analyse **independently**, the room debates, the proposer rebuts, and everyone votes
**privately**. `RiskGuard` then validates again immediately before submission.

**One class serves both callers.** A new trade and a position review differ only in the
`WarRoomRequest`: the purpose, the allowed actions, and whether a position is attached. The
process is identical, which is what stops the two paths drifting apart.

### Independence is the reason a room beats one agent

Analyses are formed in parallel and each reviewer sees nothing from the others. Sharing
opinions first lets the earliest speaker anchor everyone, and a room that agrees because it
was anchored is pure cost. Initial votes are recorded before the debate, so a later change of
mind is visible rather than invisible.

### Privacy is a property of the type

`RoomContext` **has no votes field**. The spec requires final votes to stay hidden until every
vote is in, and leaving the field out means no future edit can leak one vote into another
persona's prompt. A test asserts the type has no such property.

### Nothing that fails may decide anything

A seat that throws is recorded as a fault, counted as an abstention, and **never as an
approval**. A failed proposer becomes NO_TRADE. A failed rebuttal leaves the original proposal
standing, so a broken proposer cannot silently withdraw a trade it already justified. A failed
position review leaves the position alone, still covered by the hard exits.

## ADR-020: A persona is a class, and the diversity that matters is the model

**Decision:** `IPersona` is an interface with a class for each seat. A persona owns its
provider, model, temperature, prompt and tools. Every LLM seat gets the **same** read-only
tools; they differ by model.

**Reason:** a room of one model arguing with itself shares that model's blind spots. Prompt
variety is cheap and shallow; independent errors are what make a second opinion worth its
tokens. The seats run on Claude, GPT and Grok, reached through one `IChatClient` by
`ChatClientFactory`. The xAI API is OpenAI-compatible, so Grok is the OpenAI adapter with an
endpoint override.

**`IPersona` mentions no model.** `ExposureRiskPersona` is plain C#: it computes exposure,
concentration and remaining capacity, votes on the numbers, costs nothing, and cannot
hallucinate. It covers the arithmetic half of the Risk Analyst role, which a language model
should not have been doing.

**Missing keys fail at startup**, not mid-cycle. A seat without a key is a dead seat at 09:31
on the first trading morning.

| Seat | Provider | Purpose |
|---|---|---|
| `proposer` | Claude Opus 5 | Searches and proposes. Carries the full Alpaca toolset. |
| `skeptic` | Claude Sonnet 5 | Assumes the proposal is wrong. |
| `quant` | GPT-5.6-terra | Judges the contract and the numbers. |
| `market` | Grok 4.6 | Price action, context, news and events. |
| `exposure` | none | Portfolio arithmetic in C#. Free. |

## ADR-021: Votes size the position above a threshold

**Decision:** `VoteTally` weights each vote by its confidence, divides by every voter
including the faulted ones, and compares the result with `ApproveThreshold`. At or below the
threshold the proposal is rejected. Above it, the same number becomes the size multiplier.

**`ApproveThreshold` starts at 0** — more weighted conviction for than against. Raising it
approaches the four-of-five recommendation in the flow specification.

> **This is the single number most likely to decide whether the system ever trades.** A room
> that rejects everything produces a four-day run holding cash, which is a loss and not
> safety.

An approved proposal always trades at least one contract. Rounding a positive conviction down
to zero would turn an approval into a rejection wearing the same name.

A faulted voter dilutes conviction rather than vanishing, so a room that half broke cannot
look unanimous. Under `RequireEveryVoter` a fault rejects outright, which is the conservative
reading and the correct one for money.

## ADR-022: The room reports what it costs

**Decision:** `TokenLedger` records `ChatResponse.Usage` per persona and model.
`ModelPricing` turns tokens into an estimated figure in US dollars.

**Token counts are fact; dollars are an estimate.** No provider reports money, so the rate
table is hardcoded and will go stale. Two silent undercounts are recorded with every figure: a
model with no rate is excluded from the total and named in `UnpricedModels`, and hosted web
search normally bills per call outside token counts. **Treat the number as a floor.**

A failed call is still recorded, because a failed call is still billed.

## ADR-023: Dry run is a gateway, not a flag

**Decision:** `--dry-run` wraps the real `ITradingGateway` in `DryRunTradingGateway`. Reads
pass through to the genuine account, positions and orders. The four write methods are
intercepted, recorded, and never forwarded.

**Reason:** a flag is checked at a call site, and a call site can be forgotten. A gateway with
no path to the inner one for a write cannot be bypassed by code that forgets to check. The
loop is handed the object and cannot tell the difference.

**Reads deliberately stay real.** A dry run against a fake account proves nothing. It must
exercise the true equity, the true positions and the true prices, and differ from a live run
in exactly one respect.

**A submitted order is reported as accepted**, so the cycle continues exactly as it would
live: the order is recorded, the daily count advances, and the next cycle sees the same state
machine. Audit rows are written with `mode = "dry-run"`.

## ADR-024: The run writes to the console, and each event has an id

**Decision:** The trading code writes to `ILogger`. There is no terminal view, and there is no
observer interface between the code and the log.

**Each event that tells the story of a run has an `EventId`.** `RunEvents` holds them all. A
line with an id is part of the story. A line with no id is a diagnostic. A view can then select
the ids that it shows, and the trading code does not know that a view exists.

**An id is permanent.** If you give an event a new number, each filter that selects the old
number stops to show that event, and nothing reports the change.

**Why there is no observer interface.** An earlier design sent each event to `IRunObserver`,
and one implementation of that interface wrote the log. This made two paths that report the
same facts. `TradingLoop`, `LiveSession` and `WarRoomSession` each hold an `ILogger` already,
and almost every observer call was two lines away from a log line with the same content.
`ILogger` has the mechanism to select an event already: the id.

**Why there is no dashboard.** A frame that draws again and again was built with
Spectre.Console. It did not work. The frame also needed a presentation model, a log file, and a
fan-out, because a frame and a log stream cannot share a terminal. That is approximately 1,200
lines to show what the console shows. The `Spectre.Console` package stays in the project file
for a later attempt.

**The log level is never reduced.** It stays at `Information`. If you make the record smaller
to keep a display tidy, you lose the thing that explains a bad trade.

**A rejection is reported.** `RunEvents.RiskRejected` marks each action that a deterministic
rule refused. A run that shows only what it traded gives no proof that the guardrails operate.

## ADR-025: Out-of-hours testing, and the one rule that may be relaxed

**Decision:** `--once` runs a single cycle even when the exchange is shut.
`--allow-stale-quotes` skips the quote-age rule. **The host refuses `--allow-stale-quotes`
unless `--dry-run` is also set.**

**Reason:** out of hours every quote is from the previous close, so the cheap filter correctly
rejects every candidate and the war room never sits. Measured on a Sunday: **0 candidates from
13 symbols**, and with the rule relaxed, **40**. Without a relaxation the entire decision path
is untestable on a weekend, which is exactly when there is time to test it.

**Why this is not a hole in the guardrails.** The relaxed rule can only run alongside a
gateway that has no way to submit, so it can never reach an order. The pairing is enforced at
startup and the host refuses to run otherwise. It is a way to watch the machinery, never a way
to trade on stale data.

> Submitting into a closed session is the specific danger this avoids: a resting order can
> fill at the next open, hours later, at a price nobody evaluated.

## ADR-026: The audit trail is written by the loop, and records rejections

**Decision:** `TradingLoop` writes `evaluation_runs`, `forecasts` and `decisions`, and links
`orders.decision_id`. It is the only place holding the market data, the risk verdict and the
order id at once, and it already owns the store.

It reads the seat detail through **`IExplainsDecision`**, which `WarRoomAgent` implements.
The loop audits opinions without knowing a room produced them, so the stub agent and the
replay path take the same code with no room detail.

**A rejected action is recorded exactly like an accepted one.** A stored history of trades
alone cannot show that a risk rule ever fired. `decisions.risk_result` names the rule and
`evaluation_runs.status` separates accepted from rejected.

**A failed audit write never stops a trade.** The audit describes a decision; it does not
take part in one. Losing a row is bad; refusing to trade because a disk is full is worse.

**`forecasts.probability` and `decisions.combined_probability` are nullable.** They were
`NOT NULL` when four weighted experts each returned a number. A war-room seat argues and
votes, and `ExposureRiskPersona` is plain C# that never produces a probability. Requiring
one would drop exactly the seats that reason in words.

**The reshape was a migration guarded on emptiness.** `DropEmptyReshapedTablesAsync` drops
the four audit tables only while they hold no rows. A table with rows is left alone whatever
its shape: silently deleting an audit trail to fit a schema change is the one outcome this
must never have.

**`agent_tool_calls` is deliberately still empty.** Filling it means instrumenting the tool
path inside `FunctionInvokingChatClient`, which sits on the critical path of every research
call.

## Related

- [Critic agent](../war-room/summary.md)
- [Model against the market](../replay/model-vs-market.md)
- [ML hypotheses](../plans/ml-hypotheses.md)
- [Technology stack](technology-stack.md)
- [Risk guardrails](../trading/risk-guardrails.md)
- [Replay mode](../replay/replay-mode.md)
- [Storage schema](../storage/schema.md)
- [Strategy parameters](../trading/strategy-parameters.md)
- [War room](../war-room/summary.md)
