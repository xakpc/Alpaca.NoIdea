# Model Training Plan

The model is the [Historical ML Expert](../experts/historical-ml-expert.md). Training runs
on replay data.

## Target label

For each evaluated option event:

```text
Call: Label = stock price at target time > strike
Put:  Label = stock price at target time < strike
```

The model can train one above-strike probability and derive the complementary probability
for a below-strike event when this is valid.

## Initial features

| Feature | Note |
|---|---|
| 15-minute return | Short momentum |
| 1-hour return | |
| 1-day return | |
| 5-day return | |
| 1-day volatility | |
| 5-day volatility | |
| Current volume ratio | Activity against a recent baseline |
| SPY 1-hour return | Market context |
| SPY 1-day return | |
| QQQ 1-hour return | When applicable |
| Distance from price to strike | As a percentage |
| Hours to expiration | |

**Invariant:** feature engineering must use only past data.

## Trainer

`SdcaLogisticRegressionBinaryTrainer` (ADR-007). It is simple, fast, calibrated, and easy to
evaluate. A later model can use LightGBM only after replay tests show a clear improvement.

The trained model is saved to `data/historical-model.zip`.

## Split

Time-based only. See [replay mode](replay-mode.md).

## Evaluation

Evaluate:

- **Brier score** — the primary metric.
- Log loss, if useful.
- Calibration.
- Accuracy — a secondary metric only.
- A comparison with a simple reference forecast.

> The main output is probability quality, not only a yes/no class.

A model with good accuracy and bad calibration is not useful here. The combiner uses the
probability value, not the class.

## The honest exit condition

If replay tests show that the model has no useful predictive value, the architecture permits
replacement of the model. Record the finding. Do not keep a model because it exists.

## Related

- [Historical ML Expert](../experts/historical-ml-expert.md)
- [Forecast combination](../experts/forecast-combination.md)
