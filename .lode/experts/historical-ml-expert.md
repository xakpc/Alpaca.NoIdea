# Expert 1: Historical ML Expert

**Technology:** ML.NET 4.0.2. **Trainer:** `SdcaLogisticRegressionBinaryTrainer` (ADR-007).
**Code:** `HistoricalMlExpert` in the shared library
`src/Xakpc.Alpaca.NøIdea.FeatureGenerator`. It sits beside `FeatureVector` so the training
schema and the prediction schema are one declaration.

This expert uses numerical market data only. It does not use text and it does not use an
LLM. It is the cheap expert. The [cheap filter](../trading/live-cycle.md) uses it to stop a
candidate before any LLM cost occurs.

## The question and the answer

For a call:

> Probability that the stock price is above the strike at expiration.

For a put:

> Probability that the stock price is below the strike at expiration.

The model can train one above-strike probability and derive the complementary probability
for a below-strike event when this is valid.

```text
Question:
Will NVDA finish above $190 at the selected expiration?

Probability: 0.63
```

The model is a binary classification model. It produces a calibrated probability from 0
to 1.

## Features

Fourteen numerical features. The full list, the sources, and the windows are in
[model training](../replay/model-training.md).

The two that the measurement forced are **log moneyness** `ln(S/K)` and **moneyness z**
`ln(S/K) / (sigma * sqrt(T))`, bounded to +/-8. Without the ratio term the model was
miscalibrated in sample, because a logit that is linear in distance, volatility and time as
separate columns cannot express a ratio.

**Invariant:** feature engineering must use only past data. A feature that reads a bar
after time `T` is a defect. See [no future-data leak](../replay/replay-mode.md).

## Label

```text
Call: Label = stock price at target time > strike
Put:  Label = stock price at target time < strike
```

## Evaluation

The main output is probability quality, not a yes/no class.

**Measured on the held-out test period:** Brier **0.13988**, against a base-rate baseline of
0.24959 and a constant-0.5 baseline of 0.25000. Log loss 0.42626, AUC 0.8916. The calibration
table is monotonic. Full numbers in `data/model-metrics.md`.

> A high AUC is not proof of alpha. Distance to strike and time to expiration alone separate
> the outcomes strongly. The model is measured against a base rate, **not** against what the
> option market was pricing, because no historical delta exists. See
> [option data availability](../replay/option-data-availability.md).

**The calibration is monotonic but not exact.** Middle buckets realize higher than they
predict (0.45 predicted realized 0.56). The expert is under-confident about an upward move in
the middle of its range, so treat a mid-range probability as a floor until a calibration step
corrects it.

## Measured against the market: it loses

**The model does not beat the option price.** Brier 0.14925 against the market's 0.13787 over
149,838 paired questions, losing in every period including the one it was fitted on. An equal
blend of the two is worse than the market alone, so the model holds no information the price
does not already carry.

Worse, the wider the model and the market disagree, the more wrong the model is: where the gap
exceeds 0.20 the model said 54%, the market said 63%, and 67% happened.

Full evidence in [model against the market](../replay/model-vs-market.md).

## What the role becomes

**This expert must not be treated as a source of edge, and the cheap filter cannot use a
model-versus-market gap as a trade signal.** The decision is recorded as ADR-013 in
[architecture decisions](../architecture/decisions.md). That gap selects for the model's own errors.

The useful outputs of the work are the validated market probability from the option ladder,
and the measurement machinery that will score the remaining three experts.

## Honest limits

The model is a baseline. **The system must not assume that this model is profitable.** Beating
a base rate is not the same as beating the option market, and the measurement shows the
difference is decisive. If
replay tests show no useful predictive value, the architecture permits replacement of the
model. A later model can use LightGBM if there is a clear improvement.

## Fault behavior

If the model cannot load, the system stops new trading. Position-management safety can
continue if possible. See [fault handling](../operations/fault-handling.md).

## Related

- [Model against the market](../replay/model-vs-market.md)
- [ML hypotheses](../plans/ml-hypotheses.md)
- [Model training plan](../replay/model-training.md)
- [Experts summary](summary.md)
