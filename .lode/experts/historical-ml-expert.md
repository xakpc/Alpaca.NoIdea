# Expert 1: Historical ML Expert

**Technology:** ML.NET. **Trainer:** `SdcaLogisticRegressionBinaryTrainer` (ADR-007).

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

Initial numerical features:

- 15-minute return.
- 1-hour return.
- 1-day return.
- 5-day return.
- 1-day volatility.
- 5-day volatility.
- Current volume ratio.
- SPY 1-hour return.
- SPY 1-day return.
- QQQ 1-hour return when applicable.
- Distance from the current price to the strike, as a percentage.
- Hours to expiration.

**Invariant:** feature engineering must use only past data. A feature that reads a bar
after time `T` is a defect. See [no future-data leak](../replay/replay-mode.md).

## Label

```text
Call: Label = stock price at target time > strike
Put:  Label = stock price at target time < strike
```

## Evaluation

The main output is probability quality, not a yes/no class. Measure:

- Brier score.
- Log loss, if useful.
- Calibration.
- Accuracy, as a secondary metric only.
- Comparison with a simple reference forecast.

## Honest limits

The model is a baseline. **The system must not assume that this model is profitable.** If
replay tests show no useful predictive value, the architecture permits replacement of the
model. A later model can use LightGBM if there is a clear improvement.

## Fault behavior

If the model cannot load, the system stops new trading. Position-management safety can
continue if possible. See [fault handling](../operations/fault-handling.md).

## Related

- [Model training plan](../replay/model-training.md)
- [Experts summary](summary.md)
