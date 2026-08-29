# Alpaca MCP Safety

The MCP connection is the path to money. These rules protect the paper account.

## The security rule

> LLMs can see only the read-only Alpaca MCP toolset. Trading tools exist only on a separate
> MCP connection that deterministic C# code uses.

A tool that does not exist cannot be selected by the model. This is stronger than prompt-only
protection. It is ADR-005 and ADR-006.

## Defence in depth

The host applies two independent controls.

```mermaid
flowchart TD
    S[Alpaca MCP server] -->|Control 1| RO[Start with read-only toolsets only]
    RO --> L[ListToolsAsync]
    L -->|Control 2| F[Client allowlist filter]
    F --> C[ChatOptions.Tools]
    C --> M[LLM agent]
```

1. **Server side.** Start the research server instance with read-only toolsets only.
2. **Client side.** Filter the discovered tools against `McpToolCatalog` before the host adds
   them to `ChatOptions.Tools`.

If control 1 fails silently after a server upgrade, control 2 still blocks the tool.

## Forbidden for the LLM

An LLM tool list must never contain a tool that can do these actions:

```text
Submit order
Replace order
Cancel order
Close position
Exercise option
Arbitrary shell execution
Arbitrary file access
Environment secret access
```

**Startup must fail if the read-only connection exposes any of these.** This is an
integration failure, not a warning.

## Credentials

Pass credentials to the container as environment variables:

```text
ALPACA_API_KEY
ALPACA_SECRET_KEY
ALPACA_PAPER_TRADE   # must confirm paper mode
```

The host must verify paper mode after it connects, with an account read through the trading
connection. **Startup fails if paper mode cannot be confirmed.**

Both server instances use the same paper account credentials. The difference is the toolset,
not the account.

## Version pinning (ADR-011)

MCP tool names and schemas can change. A silent upgrade during the official window is an
unnecessary risk.

- Pin the submodule commit of `external/alpaca-mcp-server`.
- Pin the Docker image tag.
- Log the pinned version and the approved tool names at startup.
- Validate the required tool names and schemas at startup.
- Treat an incompatible tool schema as an integration failure.
- Do not upgrade during the official trading window.

## Operating rules

- Use paper trading credentials only.
- Apply a timeout to every MCP call.
- Stop new trading when an MCP server is unavailable.
- Use a unique `client_order_id` for each new order.
- Record every MCP tool name and every argument set in `agent_tool_calls` for the audit
  trail.
- Never pass an LLM-generated string to a trading tool argument.

## Process lifetime

The host owns both child processes.

- The host starts them at startup and stops them at shutdown.
- A `docker run` child uses `--rm`, so a crash does not leave a container.
- A server exit is a **stop new trading** event. See
  [fault handling](../operations/fault-handling.md).
- A failure test must kill each server and confirm the behavior.

## Related

- [MCP integration](mcp-integration.md)
- [LLM tool policy](../llm/tool-policy.md)
- [Risk guardrails](../trading/risk-guardrails.md)
- [Fault handling](../operations/fault-handling.md)
