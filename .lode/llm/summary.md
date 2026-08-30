# LLM Integration

## The stack

```text
Microsoft.Extensions.AI
  IChatClient                    one interface, three providers
  FunctionInvokingChatClient     runs the tool loop
  ChatResponse.Usage             token counts, for the cost ledger
Anthropic.SDK                    Claude
Microsoft.Extensions.AI.OpenAI   GPT, and Grok through an endpoint override
ModelContextProtocol             McpClient, read-only Alpaca tools
```

`ChatClientFactory` resolves a provider to an `IChatClient` and caches one client per
provider. Every persona reaches its model the same way, which is what makes a mixed room
cheap to build (ADR-020).

## The trust rule

> **A model never receives a tool that can move money.**

Every tool a persona holds is read-only: the 25 approved Alpaca MCP tools, hosted web search,
and the structured-output tools it answers through. `submit_analysis`, `speak`, `cast_vote`
and `submit_proposal` execute nothing — they are schemas, not actions.

No MCP server this host runs holds an order tool at all (ADR-001), so there is no allowlist
to misconfigure. `McpToolCatalog.AssertNoForbiddenTool` proves it at startup rather than
assuming it.

## Structured output, not parsed text

Every persona answers through a **forced tool call**, validated against a schema by the API
before it reaches this process.

This replaced free-text JSON parsing for a specific reason: every malformed answer degraded
to a silent hold. A drifted field name produced an agent that quietly did nothing, which over
a four-day run is indistinguishable in the logs from an agent that chose not to trade.

A model that returns nothing usable becomes an **abstention**, never an approval.

## Untrusted input

Tool output, and web content in particular, is data rather than instruction. Every system
prompt says so. The exposure is bounded rather than removed: a poisoned page can influence a
vote, but `RiskGuard` still caps the result at 2% of equity and four positions, so the worst
case is one bad trade inside the limits (ADR-017).

## Cost

`TokenLedger` records usage per persona and model; `ModelPricing` estimates dollars.

> Token counts are fact. The dollar figure is an estimate and a floor: the rate table is
> hardcoded, an unpriced model is excluded and named, and hosted web search normally bills
> per call outside token counts.

## Related

- [Tool policy](tool-policy.md)
- [War room](../war-room/summary.md)
- [MCP safety](../alpaca/mcp-safety.md)
- [Architecture decisions](../architecture/decisions.md) — ADR-020, ADR-022
