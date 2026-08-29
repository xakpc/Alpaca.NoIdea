# Expert 4: Options Evaluator

**Technology:** deterministic C#. This expert is not an LLM.

The Options Evaluator checks the actual option contract. It answers two questions:

1. What market probability reference can we derive from the option data?
2. Is this option practical to trade?

## Input data

- Call or put.
- Strike.
- Expiration.
- Bid.
- Ask.
- Quote age.
- Option chain data.
- Delta, if available.
- Implied volatility, if available.
- Spread between bid and ask.

## The market probability reference

The first implementation uses the **absolute delta** as an approximate probability
reference when delta is present.

```text
Market reference ≈ |delta|
```

This is a heuristic. **Delta is not an exact real-world probability.** The project must
validate this approach against historical results before it relies on it. A later
implementation can use a better option-pricing probability calculation.

**Invariant:** if delta is not available, the system must not invent a value. The system
skips the contract or uses another validated calculation.

## The practicality check

The evaluator can reject a contract because the contract itself is poor, even when the
price forecast is good. It must reject a quote that is:

- Stale.
- Missing.
- One-sided.
- Otherwise unusable.

It also checks the bid/ask spread, the quote age, the option type, the strike, the
expiration support, and the general data quality.

The exact thresholds (maximum spread, maximum quote age, minimum and maximum time to
expiration) are **TBD**. Replay tests must set them. See
[strategy parameters](../trading/strategy-parameters.md).

## Two call sites

The evaluator runs twice in one cycle:

1. **Before the LLM calls** — to give the reference for the
   [cheap filter](../trading/live-cycle.md).
2. **After the combination** — to revalidate the contract with fresh quote data.

## Data quality note

The **latest** option quote and chain are real time on the free Basic plan. The evaluator
therefore works with current live data, not delayed data. The 15-minute restriction applies
to historical option bars and trades, which matters for replay, not for the live decision.

The remaining limit is quality: the Indicative feed is not consolidated OPRA. **Require a
meaningful pricing difference. Do not trade a very small quote difference.** See
[market data policy](../alpaca/market-data-policy.md).

## Related

- [Forecast combination](forecast-combination.md)
- [Risk guardrails](../trading/risk-guardrails.md)
