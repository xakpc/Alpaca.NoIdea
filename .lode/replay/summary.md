# Replay

Historical replay is **required** before the official run. The official trading window is
four days. That is too short to learn anything from live results (ADR-009).

## Goals

- Train the ML.NET model.
- Test the trading decision logic.
- Test the expert prompts.
- Measure expert forecast quality.
- Initialize the expert reliability weights.
- Test the risk limits.
- Find obvious strategy errors.
- Test the full system without a live order.

Replay also produces the numbers for every **TBD** value in
[strategy parameters](../trading/strategy-parameters.md).

## What replay is not

**Replay is not the score.** The official result comes only from the dedicated live paper
account. Replay results and simulated shocks are supporting evidence for the submission and
for parameter choices. Never present a replay number as a competition result.

## The core constraint

The live mode and the replay mode must use **the same strategy code**. Only the data source
and the clock change.

## Related

- [Replay mode](replay-mode.md)
- [Model training plan](model-training.md)
