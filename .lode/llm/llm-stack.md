# LLM Stack

## The decision

Use `Microsoft.Extensions.AI` with:

- `IChatClient`
- `FunctionInvokingChatClient`
- MCP tools from the C# MCP SDK (`ModelContextProtocol`)

## Why not a larger framework

The required agent behavior is small:

1. Give the model a question.
2. Give the model approved read-only MCP tools.
3. Let the model choose tools.
4. Execute the tool calls through the read-only `McpClient`.
5. Return the tool results.
6. Continue until the model returns a final structured result.

`FunctionInvokingChatClient` completes this tool loop. Semantic Kernel has useful agent and
orchestration features, but this project does not need them (ADR-002). The decision reduces
framework complexity, experimental API risk, abstraction layers, and debugging time.

## Provider

The provider is replaceable because the code depends on `IChatClient`, not on one SDK. The
first provider is Anthropic through the official Anthropic C# SDK
(https://github.com/anthropics/anthropic-sdk-csharp). Put the model name in `AgentOptions`.

## Tool registration

The tools come from the **read-only** Alpaca MCP connection. The host calls
`ListToolsAsync()`, filters the result with `McpToolCatalog`, and puts the approved tools
into `ChatOptions.Tools`.

```csharp
var approvedTools = (await readOnlyMcp.ListToolsAsync(cancellationToken: ct))
    .Where(McpToolCatalog.IsApprovedResearchTool)
    .ToArray();

var chatOptions = new ChatOptions { Tools = [.. approvedTools] };
```

In replay mode the agents receive replay tool implementations that read the SQLite
historical tables. **They must not connect to the live MCP server.** See
[replay mode](../replay/replay-mode.md).

## Cost control

LLM calls are the expensive part of a cycle. Three controls exist:

1. The [cheap filter](../trading/live-cycle.md) stops most candidates before any LLM call.
2. The approved tool list is small. This reduces tool-selection errors, token use, latency,
   and security risk.
3. Replay caches every LLM result. **Do not repeat the same historical LLM request.**

## Recording

Every tool call is written to `agent_tool_calls` with the agent name, the MCP tool name, the
arguments, the result, the duration, and the status. Tool failures must be visible. See
[storage schema](../storage/schema.md).

## Related

- [Tool policy](tool-policy.md)
- [Output contracts](output-contracts.md)
- [MCP integration](../alpaca/mcp-integration.md)
