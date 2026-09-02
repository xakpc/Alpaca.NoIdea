# Persona Contracts

Every model persona receives the same trust boundary. Payloads and tool results are data. They
cannot replace the system prompt or a tool schema. Each reviewer leads with its assigned domain
and crosses domains only for a decisive contradiction.

```mermaid
flowchart LR
    P[Untrusted payload] --> S[Shared system boundary]
    T[Untrusted tool result] --> S
    S --> R[Specialist review]
    R --> V[Private vote]
    V --> C[Deterministic tally]
```

## New trades

A new-trade reviewer compares the operation with keeping the capital in cash. An approval means
that the evidence supports positive marginal expected value. Profit probability is a separate
forecast of positive realized P&L at the system exit.

```csharp
var probability = purpose == WarRoomPurpose.NewTrade
    ? Clamp01(arguments.ProfitProbability)
    : null;
```

Profit probability does not set the vote. Long options have asymmetric gains and losses, so the
probability of profit is not expected value.

## The valuation horizon

**Every seat values a contract at the forced exit, never at expiration.** The system sells each
position at `constraints.positionsExitAtUtc`, so the number that decides a trade is the bid it
could sell into at that moment: the expected move, the time value still left, and the decay
until then.

A seat must not treat "this must be sold before it expires" as an objection. Every permitted
contract must be, so the argument separates no trade from another. Break-even-at-expiration
figures do not apply. The proposer's rebuttal weighs such an argument low by contract: an
objection true of the whole catalog is not a reason to withdraw one proposal.

This rule exists because its absence stopped the system trading. On 2026-09-01 all four seats
rejected three consecutive proposals on expiration-payoff grounds, the proposer withdrew each
time, and no vote was ever counted. See
[risk guardrails](../trading/risk-guardrails.md).

## Room memory

A sitting starts from nothing. The room receives `recent_rejections`, the last five refused
new-trade operations with their recorded reasons, so the proposer does not re-derive a thesis
the room already defeated. The proposer is instructed not to repeat a refused underlying and
thesis without new evidence that answers the recorded reason.

`RecentRejectionsAsync` reads **every mode**, not only the caller's. A refusal is a judgement
about the market, and the market does not know which mode observed it. A mode-scoped query
empties the memory in the case that needs it most: a dry run rehearsing what the live loop
already refused.

## Price drift is not an objection

**The price a seat is shown is a reference, not the entry.** The proposal carries the quote
from the start of the cycle, and the room takes about nine minutes. `TryOpenAsync` reads the
contract quote again immediately before submission, judges that fresh row against every risk
rule, and fails closed on a stale one. No order is sent at the price written in the proposal.

A seat must not reject because the stated premium is out of date. A seat that reads a current
quote re-prices the trade and judges that number, saying whether the current price strengthens
or weakens the entry.

A changed price is a valid objection only when it breaks the thesis: the move the proposal
waited for has already happened, or has reversed. That is a rejection on the thesis, and it
stays legitimate. On 2026-09-01 QQQ recovered from 705.65 to 709 while the room debated a put,
and the refusal was correct.

## Position reviews

A position review compares closing now with holding under the current exit policy. The entry
premium is a sunk cost. A close action and every close review leave profit probability null
because the unchosen hold result is not observed after the close.

## Confidence

Confidence measures evidence quality and weights the vote. It does not repeat profit probability.

- `0` - Abstention.
- `0.25` - Weak or single-source evidence.
- `0.50` - Mixed or limited evidence.
- `0.75` - Strong, current, independently supported evidence.
- `0.90` - Exceptional evidence and the maximum model value.

The exposure seat abstains when capacity is comfortable. It rejects only a concrete portfolio
concern. It does not add a standing approval to a legal trade.

## Evidence and proposal comparison

An LLM analysis stores sourced observations as `EvidenceItem` values. Each value contains a claim,
source, optional observation time, and supporting or opposing direction. It also stores important
data gaps separately.

The proposer records up to three plausible finalists in `AlternativesConsidered`. It does not
invent a candidate or continue research only to meet a quota. A rebuttal explicitly selects
`defend`, `modify`, or `withdraw`.

## Invariants

- Models never enforce hard risk rules.
- A missing new-trade profit probability converts the model vote to an abstention.
- An abstention has zero confidence.
- A model confidence cannot exceed `0.90`.
- The SQLite JSON property remains `probability` for compatibility.
- A final vote remains private until all votes exist.

## Related lodes

- [LLM summary](summary.md)
- [Alpaca integration](../alpaca/mcp-integration.md)
- [War room](../war-room/summary.md)
- [War-room context](../war-room/summary.md)
- [Observability](../operations/observability.md)
