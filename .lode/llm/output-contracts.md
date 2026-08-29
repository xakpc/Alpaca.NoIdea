# LLM Output Contracts

LLM output must use a strict structured schema. **The system must not extract a trade
decision from free text.**

## The two contracts

```csharp
public sealed record ResearchForecast(
    decimal Probability,
    decimal Confidence,
    string Summary,
    IReadOnlyList<string> Evidence);

public sealed record CriticForecast(
    decimal Probability,
    decimal Confidence,
    string Summary,
    IReadOnlyList<string> Risks);
```

The two records differ only in the last member: the Research Agent gives **evidence**, the
Critic Agent gives **risks**.

## Validation rules

- `Probability` must be from 0 to 1.
- `Confidence` must be from 0 to 1.
- `Summary` must not be empty.
- Tool failures must be visible.
- **Invalid output causes a candidate skip.**

Validation is not a warning path. A value outside the range is a skip, not a clamp. This
follows the [fail-closed rule](../trading/risk-guardrails.md).

## Persistence

Each valid forecast becomes one `forecasts` row:

```text
forecaster     -> "research" or "critic"
probability    -> Probability
confidence     -> Confidence
reasoning      -> Summary
evidence_json  -> Evidence or Risks, as JSON
```

The stored probability later gets a Brier score. See
[forecast combination](../experts/forecast-combination.md).

## Related

- [Research Agent](../experts/research-agent.md)
- [Critic Agent](../experts/critic-agent.md)
