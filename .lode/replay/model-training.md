# Model Training

> **The Trainer is not on this branch.** It lives on `phase-3-historical-ml-expert`, together with the shared
> feature library. This file records what was built, how it was measured, and the outcome.

The model is the [Historical ML Expert](../replay/model-vs-market.md).
`src/Xakpc.Alpaca.NøIdea.Trainer` builds it. The features live in the shared library
`src/Xakpc.Alpaca.NøIdea.FeatureGenerator`, which the trading host also references, so the
training schema and the prediction schema are one declaration.

```bash
dotnet run --project src/Xakpc.Alpaca.NøIdea.Trainer
```

It opens a menu. The steps are the pipeline in order, and each one stands alone, so the
whole thing can be walked slowly:

| Step | Shows |
|---|---|
| 1 What is on disk | Bars, contracts, date ranges for each symbol |
| 2 One training row | One question with all 14 features explained |
| 3 Build the dataset | Every row, and the balance of the answers |
| 4 Split by time | Where the train and test line falls, and why |
| 5 Train and score | Fit, then Brier on the **validation** period. Repeat freely. |
| 6 What the model learned | The weights, largest first |
| 7 Score one candidate | Load the saved model and answer one question |
| FINAL score | Brier on the **test** period. Read once. Logged. |

When the terminal is not interactive the menu cannot open, so a step name is taken instead:
`dotnet run --project src/Xakpc.Alpaca.NøIdea.Trainer -- train`.

The trainer reads `data/raw/` only. It starts no MCP client and calls no Alpaca API.

## The row

One row is one distinct **`(symbol, decision time, expiration, strike)`**.

A call and a put at one strike and one expiration ask the same above-strike question, so the
option type is not part of a row. The catalog is reduced to distinct strikes. This halves the
dataset and removes a duplicated label.

| Part | Source | Rule |
|---|---|---|
| Decision time | 15Min bars, regular hours | Every fourth bar, so one an hour |
| Expiration | Contract catalog | 0 to 3 calendar days out |
| Strike | Contract catalog | Within 3% of spot at the decision time |
| Label | 1Day bar of the expiration session | `close > strike` |

The strike and the expiration are real: they come from contracts that traded. See
[option data availability](option-data-availability.md).

The label uses the **closing price**, because US index and equity weekly options settle on
the close.

## Features

Twelve initial features, plus two that the measurement forced.

| Feature | Source |
|---|---|
| 15-minute return | 15Min, regular hours |
| 1-hour return | 15Min, regular hours |
| 1-day return | 1Day |
| 5-day return | 1Day |
| 1-day volatility | 15Min, one session of returns |
| 5-day volatility | 1Day, five returns |
| Volume ratio | 15Min, against a 20-bar baseline |
| SPY 1-hour return | 15Min |
| SPY 1-day return | 1Day |
| QQQ 1-hour return | 15Min |
| Distance to strike, % | Spot and strike |
| Hours to expiration | Decision time to the 16:00 ET close |
| **Log moneyness** | `ln(S/K)` |
| **Moneyness z** | `ln(S/K) / (sigma * sqrt(T))`, bounded to +/-8 |

**Moneyness z is the reason the model can be calibrated.** The probability of finishing above
a strike depends on the *ratio* of the distance to the volatility over the remaining time. A
logit that is linear in distance, volatility and time as separate columns cannot express a
ratio. Without this column the model was miscalibrated **in sample**: a bucket predicted at
0.55 realized 0.71.

The bound matters as much as the term. The ratio diverges as the remaining time goes to zero,
and a row 15 minutes before a close reached 215. An unbounded column makes
`NormalizeMeanVariance` compute a huge variance, which squashes the useful range toward zero
and lets the outliers set the fit.

**Invariant:** every feature reads bars at or before the decision time. All reads go through
`BarSeries`, whose as-of lookups cannot return a later bar. A test walks a one-minute grid and
confirms it, and a second test tampers with the series after the decision time and confirms no
feature changes.

## Regular trading hours

The 15-minute features use regular-hours bars only, 09:30 to 16:00 ET. This closes the open
question in [historical dataset](historical-dataset.md).

Overnight movement is not lost. The 1-day and 5-day returns and the 5-day volatility read the
1Day bars, which already span the gap.

## Trainer and split

`SdcaLogisticRegressionBinaryTrainer` (ADR-007), after `Concatenate` and
`NormalizeMeanVariance`. SDCA is scale sensitive, and hours to expiration span tens while a
return is a fraction of a percent.

**The trainer runs SDCA on one thread.** SDCA updates its dual variables on several threads by
default and those updates race, so the `MLContext` seed alone does not make a run repeatable.
Two runs over identical data gave Brier 0.13689 and 0.14215, with visibly different
calibration. `NumberOfThreads = 1` makes the model reproducible, which a traded model must be.

The split is **time based**, on distinct decision dates, not on rows. One session can never
appear in two periods. A random split is forbidden: two rows minutes apart share almost every
feature, so a random draw puts near-duplicates on both sides and the test score stops meaning
anything.

```text
oldest 70%  -> train        the weights are fitted here
next   15%  -> validation   every development score
newest 15%  -> test         the final score, read once
```

## The validation period protects the test period

> **A held-out period is an honest measure only while it stays unseen.**

Every reading of the test period that is followed by a change to the model moves information
from that period into the design. The reported score then describes the past better than it
forecasts the future, and nothing in the number itself reveals this.

So the Trainer separates them:

- **Step 5 scores the validation period.** Safe to read as often as wanted, and safe to
  change the model because of. This is where tuning belongs.
- **The FINAL step scores the test period.** It asks for confirmation, and it appends the
  reading to `data/test-set-log.md`.

`data/test-set-log.md` is append-only and **committed**, unlike the other derived files. It
is the evidence of how the reported number was produced. One row is a clean result. Many
rows mean the model was tuned against its own exam, and the submission must say so.

**The current number is not a virgin measurement.** The test period was read three times on
2026-08-29 before this protocol existed, while the features were still being built: Brier
0.16434, then 0.13689, then 0.14215. Each reading was followed by a change. Those changes
fixed real defects (a dead volatility feature, an unbounded moneyness term, a
non-reproducible trainer) rather than tuning for the score, but the readings happened. The
history is recorded at the top of the log.

## Measured result

1,361,525 rows, 13 symbols, 2024-01-18 to 2026-08-28. Full numbers in `data/model-metrics.md`.

| Period | Rows | Through |
|---|---:|---|
| Train | 845,670 | 2025-11-13 |
| Validation | 228,198 | 2026-04-08 |
| Test | 287,657 | end of data |

| Test metric | Value |
|---|---:|
| **Brier score** (primary) | **0.13988** |
| Log loss | 0.42626 |
| AUC | 0.8916 |
| Accuracy (secondary) | 0.7944 |
| Base-rate baseline Brier | 0.24959 |
| Constant 0.5 Brier | 0.25000 |

Validation-period Brier is **0.12142** against a 0.25055 baseline, which is what step 5
reports during development.

The model beats both baselines. In-sample Brier is 0.12252, so the gap to the test period is
small and there is no severe overfit. The calibration table is monotonic.

**The calibration is monotonic but not exact.** The middle buckets realize higher than they
predict: a bucket predicted at 0.45 realized 0.56, and one predicted at 0.55 realized 0.66.
The ends are close. The model is therefore **under-confident about an upward move** in the
middle of its range. The combiner consumes the probability, so this bias reaches the edge
calculation. Treat a mid-range probability as a floor, not as a point value, until a
calibration step corrects it.

> **A high AUC here is not proof of alpha.** Distance to strike and time to expiration alone
> separate the outcomes strongly: a strike 3% away with four hours left is almost never
> crossed. The model is measured against a base rate, **not** against what the option market
> was pricing, because no historical delta exists. See
> [option data availability](option-data-availability.md).

## The honest exit condition, applied

The model beats ignorance easily. **It does not beat the option price**, and the wider it
disagrees with the price the more wrong it is. See
[model against the market](model-vs-market.md) for the numbers.

So the Historical ML Expert is not a source of edge, and the cheap filter cannot key on a
model-versus-market gap. The finding is recorded rather than buried, which is what the exit
condition asks for.

## Related

- [Historical ML Expert](../replay/model-vs-market.md)
- [Model against the market](model-vs-market.md)
- [Option data availability](option-data-availability.md)
- [Historical dataset](historical-dataset.md)
- [Forecast combination](../war-room/summary.md)
