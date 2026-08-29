# Technology Stack

| Area | Technology | Reason |
|---|---|---|
| Runtime | .NET 10 | Good support for console, async I/O, process control, and `TimeProvider`. |
| Language | C# | The main project language. |
| TUI | Spectre.Console | Small terminal UI. No web stack is required. |
| AI abstraction | `Microsoft.Extensions.AI` | Gives `IChatClient`, AI tools, and tool invocation. |
| LLM provider | Anthropic C# SDK, or another `IChatClient` provider | Keeps the provider replaceable. |
| Agent tool loop | `FunctionInvokingChatClient` | Completes the tool-call loop for the model. |
| MCP client | C# MCP SDK (`ModelContextProtocol`) | Connects the host to Alpaca MCP. Gives MCP tools to `IChatClient`. |
| Alpaca access | Alpaca MCP Server | Market data, news, option data, account, and paper-trading tools. |
| MCP transport | `StdioClientTransport` over `docker run -i` | Runs the pinned server image as a stdio child process. |
| Numerical ML | ML.NET | Keeps historical prediction in-process in C#. |
| Initial ML model | `SdcaLogisticRegressionBinaryTrainer` | Simple, fast, calibrated, easy to evaluate. |
| Database | SQLite | One local file. No server is required. |
| SQLite driver | `Microsoft.Data.Sqlite` | The ADO.NET provider for SQLite. |
| SQLite mapping | Dapper | Maps a SQL result to a record. Removes the reader and parameter boilerplate. |
| Logging | `Microsoft.Extensions.Logging` | The built-in logging is sufficient. |
| Time | `TimeProvider` | Lets the same code run with live time and replay time. |

## Installed now

The project file currently references one package only:

```xml
<PackageReference Include="Microsoft.VisualStudio.Azure.Containers.Tools.Targets"
                  Version="1.24.1-preview.1" />
```

This package supports the Visual Studio container tooling. It is not part of the trading
architecture. Every package in the table above is still to be added.

The Alpaca MCP server submodule (`external/alpaca-mcp-server`) and its Docker image do not
exist yet. Phase 1 of the [MVP roadmap](../plans/mvp-roadmap.md) creates them.

The old Alpaca CLI binary is still at `cli_0.0.14_windows_amd64/alpaca.exe`. It is a
fallback only. No code may call it.

## Rejected technology

- **Semantic Kernel** — The required LLM flow is small. `Microsoft.Extensions.AI` and the
  MCP SDK give the same behavior with less framework code. This also reduces experimental
  API risk (ADR-002).
- **Alpaca CLI** — MCP gives the LLM agents Alpaca tools directly, keeps one open
  connection for many calls, and lets a separate trading connection hold the write tools. A
  new process for each Alpaca operation is not required (ADR-001). The CLI stays as a
  fallback only. **The architecture must not use both paths at the same time.**
- **Claude Agent SDK process** — A separate agent process is not required.
- **A full ORM** (Entity Framework, NHibernate) — The project writes its own SQL and maps
  the result with Dapper. Change tracking, migrations, and query translation are not needed
  for this data volume (ADR-004).

The full reasoning is in [architecture decisions](decisions.md).

## Related

- [LLM stack](../llm/llm-stack.md)
- [MCP integration](../alpaca/mcp-integration.md)
