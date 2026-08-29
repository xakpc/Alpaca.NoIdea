# Expert 2: Research Agent

**Technology:** LLM through `Microsoft.Extensions.AI.IChatClient` with
`FunctionInvokingChatClient`.

The Research Agent uses current text and market context. It decides which read-only tools it
needs.

## Tools

The tools come from the **read-only Alpaca MCP connection**. Typical capabilities:

```text
News
Stock bars and quotes
Option chains and option snapshots
Greeks when available
Reference-symbol market data
```

The exact MCP tool names come from the pinned server version. `McpToolCatalog` filters the
discovered tools before the host adds them to `ChatOptions.Tools`. The tools are read-only.
The full policy is in [tool policy](../llm/tool-policy.md).

## Behavior

1. Read recent company news.
2. Check recent price movement.
3. Compare the stock with SPY, QQQ, or a related tracked symbol.
4. Read option chain data if it needs it.
5. Stop when it has enough information.
6. Return one structured probability and the evidence.

## Prompt shape

```text
NVDA is $180.
The option strike is $185.
The option expires in two trading days.

Estimate the probability that NVDA is above $185 at expiration.
Use the available tools if required.
Return structured JSON.
```

## Output contract

```csharp
public sealed record ResearchForecast(
    decimal Probability,
    decimal Confidence,
    string Summary,
    IReadOnlyList<string> Evidence);
```

Example:

```json
{
  "probability": 0.59,
  "confidence": 0.72,
  "summary": "Positive company news exists, but part of the move is sector-wide.",
  "evidence": [
    "Company news item A",
    "NVDA 1-hour bars",
    "QQQ 1-hour bars"
  ]
}
```

Validation rules are in [LLM output contracts](../llm/output-contracts.md). Invalid output
causes a candidate skip.

## Limits

- The Research Agent cannot submit, change, cancel, or close an order.
- In replay mode its tools must read from the replay data source. They must not connect to
  the live Alpaca MCP server.
- The agent can add noise. Its forecast is scored and weighted like every other forecast.

## Related

- [Critic Agent](critic-agent.md)
- [LLM stack](../llm/llm-stack.md)
