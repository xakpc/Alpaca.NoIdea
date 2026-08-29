# Recorded ML Hypotheses

Ideas that were considered after the [Historical ML Expert lost to the option
price](../replay/model-vs-market.md), with the evidence for and against each. None is
implemented. This file exists so a later session does not rediscover a dead end, and does not
mistake a known artefact for a new result.

## H1: Predict the market's error, not the outcome

**The idea.** Change the target:

```text
target = (what happened) - (what the market implied)
```

If the market's error is predictable, that is edge by construction. This is the only
reformulation with a defensible basis, because the market *is* measurably biased.

**The evidence for.** The market's own calibration over 149,838 questions is not flat:

| Market said | Actually happened |
|---:|---:|
| 2.6% | 3.9% |
| 14.5% | 19.2% |
| 24.7% | 29.2% |
| 44.8% | 49.5% |
| 64.8% | 68.9% |
| 97.0% | 91.4% |

The market **under-predicts** across nearly the whole range.

**The evidence against, and it is decisive.**

*The cause is known and is not skill.* An option price is a **risk-neutral** probability. It
carries a drift of the risk-free rate, not the underlying's expected return. Real equities
drift up, so risk-neutral probabilities understate "finishes above strike". The bias is
structural, not a mistake anyone is making.

*A model would learn a direction bet.* SPY rose **41.6%** across the training period and
**13.9%** across the test period. A residual model fitted on that data learns
`buy calls, the market underprices upside`. That is leveraged long equity, it backtests
beautifully, and it inverts in the first down week. The competition is four days, so it is a
coin flip on one week with leverage.

*The bias is smaller than the cost of trading it.* Weighted by row count the market predicts
**42.8%** against a realized **44.5%**: a 1.7 percentage-point bias, worth about **0.0003**
Brier if corrected perfectly. Measured ATM relative spreads in [universe](../trading/universe.md)
are **0.125 to 0.176**, so execution costs 12 to 18% of premium. The edge does not cover the
spread.

> **If this is ever revisited, the test is not "does Brier improve".** It is whether the
> improvement survives a period where the underlying fell, and whether it exceeds the spread.

## H2: Option volume and open interest

**The idea.** Positioning is not price history, so it is genuinely different information.
Unusual option activity is a recognised signal class.

**What is already on disk.** `open_interest` in `data/raw/contracts/`, and per-contract volume
in `data/raw/option-bars/`. Neither is used by any feature.

**Why it was not pursued.** The evidence for it is mixed, and there was no time to test it
honestly before the competition window. It is the strongest remaining "different data" idea
and costs no new download.

## H3: Rank contracts instead of forecasting

**The idea.** Accept the market probability as correct and use it to choose the best contract
by payoff against risk, rather than trying to out-forecast it.

**Why it is not ML.** It is arithmetic over the ladder and the risk rules. It belongs in
`OptionCandidateSelector` and the [Options Evaluator](../experts/options-evaluator.md), not in
a model. Recorded here only so the idea is not confused with a forecasting hypothesis.

## What was decided instead

ML.NET stays out of the forecasting path. `HistoricalMlExpert` keeps no weight in the combiner
and no gate in the live cycle. The shared library survives because the trading system needs
the bar reading, the market calendar, the contract catalog, the `OptionPriceBook` ladder and
the Brier scoring regardless.

The remaining alpha hypothesis is the Research and Critic agents, which read news text. That is
a different information channel from price history, and it is the only one left untested. See
[MVP roadmap](mvp-roadmap.md).

## Related

- [Model against the market](../replay/model-vs-market.md)
- [Historical ML Expert](../experts/historical-ml-expert.md)
- [Option data availability](../replay/option-data-availability.md)
- [MVP roadmap](mvp-roadmap.md)
