# Observability

The project does not need a separate observability stack. It uses:

- Structured application logs (`Microsoft.Extensions.Logging`).
- SQLite order and equity records.
- `RunEvents`, the permanent event ids that mark the story of a run.
- Agent MCP tool-call records.

## Where the record lives

The complete record is stdout at `Information`, which is what `docker logs` collects. There is
no terminal view and no log file. The level is never reduced (ADR-024).

## The event ids

`Observability/RunEvents.cs` gives an `EventId` to each event that tells the story of a run:
the start and the end, each cycle, the account and the candidates, each seat of the war room,
each order, and each rejection. A line with an id is part of the story. A line with no id is a
diagnostic.

The ids are in groups: 1000 for the host and the session, 2000 for the trading loop, 3000 for
the war room, 4000 for the conversations with the models. **An id is permanent**, because a
filter selects on the number.

### The 4000 group: one block for each seat

Each seat owns one hundred ids, and the last digit is always the same kind of line
(ADR-027):

| Seat | Block |
|---|---|
| proposer | 41xx |
| quant | 42xx |
| skeptic | 43xx |
| market | 44xx |
| exposure | 45xx |
| a seat this table does not know | 49xx |

| Last digit | Line |
|---|---|
| 1 | The request: the prompt, the payload, and the toolbox |
| 2 | Model prose, and reasoning text |
| 3 | **A tool call, with the arguments that it passed** |
| 4 | What that tool answered |
| 5 | The tally: finish reason, turns, tool calls, tokens, seconds |

So `RunEvents.Chat("proposer", ChatEvent.ToolCall)` is 4103, and its name reads
`proposer.ToolCall`. Select `41xx` to read one seat. Select each id that ends in 3 to see
every tool that the room called.

`Observability/ChatTranscript.cs` writes these lines from the `ChatResponse` that
`LlmPersona.InvokeAsync` gets back. The function-calling client puts the full loop in
`ChatResponse.Messages`, not only the last turn, and `ChatTranscriptTests` holds that
property: if a package version stops doing it, the transcript goes quiet about tool use and
nothing else reports the change.

A view that shows only some of the events does not exist yet. When it is written, it will be an
`ILoggerProvider` that selects on the id. It will not change the trading code.

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
| 1 | Retired. The ML expert is excluded (ADR-013). |
| 2, 3 | `forecasts` rows, one per war-room seat, with the vote, the confidence, and `evidence_json` holding the first opinion, what the seat said in the debate, and any fault |
| 4 | **In the console, not in the database.** The transcript writes each tool call with its arguments and each answer (ADR-027). `agent_tool_calls` is still empty. See the gap below. |
| 5 | `evaluation_runs.market_probability`, `market_snapshot_json` |
| 6 | `decisions.combined_probability`, and `decisions.net_vote` for the tally that sized it |
| 7 | `decisions.action`, `.reason`, `.edge` |
| 8 | `decisions.risk_result`, and `evaluation_runs.status` |
| 9 | `orders.alpaca_order_id`, `.client_order_id`, joined by `orders.decision_id` |
| 10 | `orders.status`, `.realized_pnl`, `equity_snapshots` |

`--audit` prints the row counts and the most recent decisions joined across all three
tables, so the record can be read without a SQLite client. `--audit --last 50` widens it.

### The one gap

> **`agent_tool_calls` is still empty.** The console now holds what the agents read
> (ADR-027), so the demo can answer question 4 from the log. The **database** cannot, and a
> log line is lost when the container restarts.

The extraction exists: `ChatTranscript.Response` walks `ChatResponse.Messages` and already
reads each `FunctionCallContent` and `FunctionResultContent`. What the table needs is a sink
threaded through `LlmPersona.InvokeAsync`, which is now the one call path, so it is one
constructor argument and not five.

## Rejections count

Record a skip and a rejection with the same care as a trade. A demo that shows why the agent
did **not** trade proves that the guardrails work, so a rejection has its own id
(`RunEvents.RiskRejected`) and a view cannot drop it as noise.

Two more ids exist for the same reason. `RunEvents.Hold` carries the reason the war room never
sat at all — halted, no free slot, no candidate passed the cheap filter — which is the common
out-of-hours path. `RunEvents.RebuttalMade` says whether the proposer held its ground, changed
it, or withdrew.

## Related

- [Storage summary](../storage/summary.md)
- [Storage schema](../storage/schema.md)
