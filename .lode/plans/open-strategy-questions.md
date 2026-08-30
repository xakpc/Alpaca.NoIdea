# Open Strategy Questions

These questions need an explicit answer before the official run. **They must not be guessed
only to complete a document or a configuration file.** The answers come from replay tests
and from official competition answers.

## 1. Exact option structure

Alpaca MCP supports single-leg and multi-leg option orders, and the hackathon puts no
restriction on the strategy type. The limit is our own time, not the platform.

Per [KISS and YAGNI](../practices.md): **start with a single long call and a single long
put.** Add a defined-risk spread only if replay evidence shows that the single-leg structure
cannot work. The final structure is **TBD**.

## 2. Strike selection

The system needs a deterministic rule to select a small set of strikes from the option
chain. The rule is **TBD**.

## 3. Expiration selection

The system needs a deterministic expiration rule. The system should use short horizons
because the contest is short. The exact range is **TBD**.

## 4. Market probability reference

The first option is the absolute delta as an approximate probability reference. **This is
not exact.** The project must validate this approach against historical results before it
relies on it. A later implementation can use a better option-pricing probability
calculation.

See [Options Evaluator](../trading/risk-guardrails.md).

## 5. Thursday exit policy

**The scoring question is resolved. This is now a strategy question only.**

The FAQ states that Alpaca evaluates total equity as of **end of day Thursday 2026-09-03**,
and that Thursday-expiring exercises and assignments appear in that value. Friday 09:30 ET
only closes the window formally. See
[competition constraints](../operations/competition-constraints.md).

> Thursday end of day is the effective final portfolio state.

The remaining question is what the `PositionManager` does on Thursday. Two supported
options:

1. Close every position before the Thursday market close.
2. Allow a supported Thursday-expiration outcome.

**The choice must be deliberate and tested in the development paper account.** It must not
be a default that nobody checked. The system must not depend on Friday option-market
activity either way.

## 6. Risk-adjusted metrics

Sharpe ratio, Sortino ratio, and maximum drawdown are **not** part of the official score.
They can still be useful evidence for the demo. Per YAGNI, do not build metric machinery
that the score does not need.

## The numeric TBD list

Every numeric value that is still open is listed in
[strategy parameters](../trading/strategy-parameters.md).

## Related

- [Definition of done](definition-of-done.md)
- [Competition constraints](../operations/competition-constraints.md)
