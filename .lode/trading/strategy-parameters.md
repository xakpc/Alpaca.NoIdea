# Strategy Parameters

The architecture separates strategy parameters from code. A strategy number must never
appear in a C# file. It belongs in `TradingOptions`, `RiskOptions`, or `AgentOptions`.

## Decided values

```json
{
  "CycleMinutes": 30,
  "TrackedSymbols": [
    "SPY", "QQQ", "IWM", "AAPL", "MSFT", "NVDA", "AMZN",
    "META", "GOOGL", "TSLA", "AMD", "MU", "INTC"
  ],
  "MaxConcurrentPositions": 4,
  "MaxNewPositionsPerDay": 4,
  "MinExpertSamplesForAdaptiveWeight": 20
}
```

`MaxConcurrentPositions`, `MaxNewPositionsPerDay`, and `CycleMinutes` are development
defaults. They are not final strategy values.

`TrackedSymbols` is **measured**, not chosen. It is the output of the four admission rules in
[universe](universe.md). Rebuild it with `scripts/screen-universe.sh`; do not edit the list
by hand.

Trailing stop is **not** a valid order type for options. See
[MCP integration](../alpaca/mcp-integration.md).

## Open values (TBD)

Replay tests must set every value below. **Do not guess a value only to complete the
configuration.**

| Parameter | Used by |
|---|---|
| Minimum probability edge | [Forecast combination](../experts/forecast-combination.md) |
| Cheap-filter threshold | [Live cycle](live-cycle.md) step 5 |
| Maximum risk for each trade | [Risk guardrails](risk-guardrails.md) |
| Maximum total account risk | Risk guardrails |
| Maximum daily loss | Risk guardrails |
| Take-profit threshold | [Position lifecycle](position-lifecycle.md) |
| Loss threshold | Position lifecycle |
| Maximum bid/ask spread | [Options Evaluator](../experts/options-evaluator.md) |
| Maximum quote age | Options Evaluator |
| Minimum time to expiration | Options Evaluator |
| Maximum time to expiration | Options Evaluator |
| Exact strike-selection rule | `OptionCandidateSelector` |
| Exact call/put selection rule | `OptionCandidateSelector` |
| Exact order type (market, limit, stop, or stop-limit) | `TradingLoop` step 11 |
| Single-leg or multi-leg execution (start single-leg) | `OptionCandidateSelector` |
| Thursday exit / expiration policy | Position lifecycle |

The open design questions behind these values are in
[open strategy questions](../plans/open-strategy-questions.md).

> **The minimum probability edge and the cheap-filter threshold have no valid signal yet.**
> A historical market reference now exists, from the call-ladder slope. But the measurement
> showed the ML model loses to that reference in every period, and that the wider the two
> disagree the more wrong the model is. Setting a threshold on that gap would select for the
> model's own errors. Both values stay TBD until a forecaster that beats the price exists.
> See [model against the market](../replay/model-vs-market.md).

## Rule

A parameter moves from **TBD** to **decided** only when replay evidence or an official
competition answer supports it. Record the evidence with the value.

## Related

- [Risk guardrails](risk-guardrails.md)
- [Replay mode](../replay/replay-mode.md)
