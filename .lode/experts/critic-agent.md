# Expert 3: Critic Agent

**Technology:** LLM through `Microsoft.Extensions.AI.IChatClient`.

The Critic Agent tries to find why the proposed opportunity can be wrong.

**The Critic is not a veto.** It does not return `BLOCK` or `ALLOW`. It produces its own
probability. That probability is one input to the combiner.

## Input

The Critic Agent can see:

- The target question.
- The Historical ML Expert result.
- The Research Agent result.
- The evidence from the Research Agent.
- Current read-only market data.

It can also call the same read-only Alpaca MCP tools as the
[Research Agent](research-agent.md).

## Output contract

```csharp
public sealed record CriticForecast(
    decimal Probability,
    decimal Confidence,
    string Summary,
    IReadOnlyList<string> Risks);
```

Example:

```json
{
  "probability": 0.46,
  "confidence": 0.81,
  "summary": "The positive news is old and the price already moved after publication.",
  "risks": [
    "News can already be priced into the stock.",
    "The sector move explains part of the stock move."
  ]
}
```

## Why a probability and not a veto

A veto is not testable. A probability is testable. The system records the Critic Brier
score with the other experts. If the Critic is always too negative, its measured reliability
falls and its weight falls. See [forecast combination](forecast-combination.md).

## Fault behavior

Invalid JSON or a failed LLM request causes a candidate skip.

## Related

- [Experts summary](summary.md)
- [LLM output contracts](../llm/output-contracts.md)
