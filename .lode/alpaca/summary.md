# Alpaca Integration

The Alpaca platform gives the paper account, the market clock, stock bars, news, and option
chains. The system reaches Alpaca through the **Alpaca MCP server** (ADR-001). It does not
use the Alpaca CLI and it does not call the REST API directly.

## The one rule

> Alpaca stores what the account owns. SQLite stores what the agent thought and did.

Alpaca is the source of truth for current positions and current orders. SQLite is not.

## Two connections, two seams

The host runs two Alpaca MCP server instances and creates two `McpClient` objects.

| Connection | Toolsets | Used by |
|---|---|---|
| Read-only | Market data, news, option chains, Greeks, metadata | LLM agents, `IMarketDataGateway` |
| Trading | Account, positions, orders, submit, cancel, close | `ITradingGateway` only |

Two typed gateway interfaces hide the MCP details from strategy code:

| Interface | Live | Replay |
|---|---|---|
| `IMarketDataGateway` | `AlpacaMcpMarketDataGateway` | `ReplayMarketDataGateway` |
| `ITradingGateway` | `AlpacaMcpTradingGateway` | `ReplayTradingGateway` |

Application code must not use an MCP tool name or a raw MCP result outside `Alpaca/`. The
LLM agents are the one exception: they call approved read-only MCP tools directly.

## The MCP server

The Alpaca MCP server (https://github.com/alpacahq/alpaca-mcp-server) is a git submodule at
`external/alpaca-mcp-server`. The host runs it in Docker as a stdio child process. The
submodule commit and the image tag are pinned (ADR-011).

## The old CLI

The folder `cli_0.0.14_windows_amd64/` still holds Alpaca CLI `0.0.14`. The CLI is a
**fallback only**, for the case where a required MCP capability is missing or unstable. No
code may call it. The architecture must not use both paths at the same time.

## Related

- [MCP integration](mcp-integration.md)
- [MCP safety](mcp-safety.md)
- [Market data policy](market-data-policy.md)
