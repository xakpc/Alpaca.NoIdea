# Technology Stack

| Area | Technology | Reason |
|---|---|---|
| Runtime | .NET 10 | Good support for console, async I/O, process control, and `TimeProvider`. |
| Language | C# | The main project language. |
| TUI | Spectre.Console | Small terminal UI. No web stack is required. |
| AI abstraction | `Microsoft.Extensions.AI` | Gives `IChatClient`, AI tools, and tool invocation. |
| LLM provider | `Anthropic.SDK` (the community C# SDK) | Exposes `IChatClient` through `.Messages.AsBuilder()`. **This is not the official `Anthropic` package,** and its API surface differs from it. |
| Agent tool loop | `FunctionInvokingChatClient` | Completes the tool-call loop for the model. |
| MCP client | C# MCP SDK (`ModelContextProtocol`) | Connects the host to Alpaca MCP. Gives MCP tools to `IChatClient`. |
| Alpaca access (deterministic C#) | `Alpaca.Markets` NuGet | Typed REST access for account, orders, market data, and option chains. Returns `decimal` money, so no parsing layer is needed (ADR-001). |
| Alpaca access (LLM agents) | Alpaca MCP Server, read-only | Bars, quotes, news, option chains, and greeks as MCP tools. One connection (ADR-012). |
| MCP transport | `HttpClientTransport` in development, `StdioClientTransport` when deployed | A URL selects HTTP, a command selects stdio. Both or neither fails startup. |
| Numerical ML | ML.NET | Keeps historical prediction in-process in C#. |
| Initial ML model | `SdcaLogisticRegressionBinaryTrainer` | Simple, fast, calibrated, easy to evaluate. |
| Database | SQLite | One local file. No server is required. |
| SQLite driver | `Microsoft.Data.Sqlite` | The ADO.NET provider for SQLite. |
| SQLite mapping | Dapper | Maps a SQL result to a record. Removes the reader and parameter boilerplate. |
| Logging | `Microsoft.Extensions.Logging` | The built-in logging is sufficient. |
| Time | `TimeProvider` | Lets the same code run with live time and replay time. |

## Installed now

```xml
<PackageReference Include="Alpaca.Markets" Version="7.2.2" />
<PackageReference Include="Anthropic.SDK" Version="5.10.0" />
<PackageReference Include="Dapper" Version="2.1.79" />
<PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.11" />
<PackageReference Include="Microsoft.Extensions.Hosting" Version="10.0.11" />
<PackageReference Include="ModelContextProtocol" Version="2.2.0" />
<PackageReference Include="Microsoft.VisualStudio.Azure.Containers.Tools.Targets" Version="1.24.1-preview.1" />
```

The container-tools package supports the Visual Studio tooling and is not part of the trading
architecture. `Microsoft.Extensions.AI` is still to be added; the agents do not exist yet.

The Alpaca MCP server submodule (`external/alpaca-mcp-server`) and its Docker image exist and
run. The old Alpaca CLI binary is at `cli_0.0.14_windows_amd64/alpaca.exe`. **No application
code calls it** (ADR-001); it is for humans and offline scripts.

## Rejected technology

- **Semantic Kernel** — The required LLM flow is small. `Microsoft.Extensions.AI` and the
  MCP SDK give the same behavior with less framework code. This also reduces experimental
  API risk (ADR-002).
- **Alpaca CLI, as an application dependency** — the CLI is capable (`--jq` extracts a
  scalar, `--schema` prints the response shape, `order get-by-client-id` is a first-class
  command), but it is a process per call with stdout parsing and no typed error model. The
  `Alpaca.Markets` SDK gives deterministic C# the same data already typed (ADR-001). The CLI
  stays for humans and offline scripts.
- **A trading MCP connection** — deleted. Once deterministic C# moved to the SDK, the second
  server had no consumer, and removing it means no MCP server this host runs holds an order
  tool. That is a stronger isolation claim than the toolset split it replaces (ADR-006).
- **The Anthropic SDK server-side MCP connector** (`mcp_toolset` /
  `BetaRequestMcpServerUrlDefinition`) — it makes Anthropic's infrastructure dial the MCP
  server by URL. Ours binds to `127.0.0.1` without authentication and is a stdio child when
  deployed, so it is unreachable either way. Decisively, it would hand the model the entire
  discovered toolset and remove `McpToolCatalog`, which is control 2 of the defence in depth.
- **Claude Agent SDK process** — A separate agent process is not required.
- **A full ORM** (Entity Framework, NHibernate) — The project writes its own SQL and maps
  the result with Dapper. Change tracking, migrations, and query translation are not needed
  for this data volume (ADR-004).

The full reasoning is in [architecture decisions](decisions.md).

## Related

- [LLM stack](../llm/llm-stack.md)
- [MCP integration](../alpaca/mcp-integration.md)
