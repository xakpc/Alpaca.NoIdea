# Practices

Patterns and rules that apply to all code in this repository.

## The one core rule

> AI can research and forecast. Deterministic C# controls risk and money.

An LLM must never submit, replace, cancel, or close an order. Only the trading engine can.
The LLM agents see only the read-only Alpaca MCP connection. The trading MCP connection is a
separate server process. See [risk guardrails](trading/risk-guardrails.md).

## KISS and YAGNI

Two rules sit beside the core rule and apply to every design decision in this project.

**KISS — keep it simple.** Choose the simplest design that meets the requirement. A simple
design is easier to debug at 09:30 ET on a Monday, and this system trades without a human in
the loop.

**YAGNI — you are not going to need it.** Build a thing when the requirement exists, not
when it might exist. The competition window is four days.

Concrete effects in this project:

- Build the smallest option structure first. Single long call and single long put. Add a
  multi-leg spread only when replay evidence shows that single-leg cannot work.
- Do not build a metric that the official score does not use. Sharpe, Sortino, and maximum
  drawdown are not scored.
- Do not build a live scoreboard. The hackathon has none.
- Do not build a hosted web application. A GitHub repository is a sufficient submission.
- Do not add an abstraction for a second broker, a second database, or a second LLM
  provider. `IChatClient` already keeps the provider replaceable.
- Do not add a package that is not in the [technology stack](architecture/technology-stack.md).
- Add a configuration value when a real decision needs it. An unused knob is a liability.

The rules do **not** apply to safety. Fail-closed checks, paper-mode enforcement, order
idempotency, the MCP read-only isolation, and the audit trail are requirements, not
extras. **Never simplify a guardrail away.** When KISS and safety disagree, safety wins and
the reason gets written down.

## Fail closed

The system skips a trade when any required input is missing, stale, or invalid. A skipped
trade is better than an unknown trade. The full condition list is in
[fault handling](operations/fault-handling.md).

## Repository layout

The solution file is `Xakpc.Alpaca.NøIdea.slnx` (the new XML solution format). One project
exists: `src/Xakpc.Alpaca.NøIdea/Xakpc.Alpaca.NøIdea.csproj`.

`Directory.Build.props` sends all build output out of the source tree:

```xml
<BaseOutputPath>$(RepositoryRoot)/build/bin/$(MSBuildProjectName)/</BaseOutputPath>
<BaseIntermediateOutputPath>$(RepositoryRoot)/build/obj/$(MSBuildProjectName)/</BaseIntermediateOutputPath>
```

Do not add `bin/` or `obj/` folders inside `src/`. Debug builds use `DebugType=full`.
Release builds use embedded symbols. `SourceRevisionId` comes from `GITHUB_SHA`.

The planned source structure is in
[application structure](architecture/application-structure.md).

## C# style

- Target framework is `net10.0`. `ImplicitUsings` and `Nullable` are enabled.
- Use `record` types for data contracts. Example:
  `public sealed record ResearchForecast(decimal Probability, ...)`.
- Use `decimal` for money, prices, and probabilities in contracts. Use `double` only inside
  ML.NET feature vectors and model output.
- Pass a `CancellationToken` to every asynchronous method.
- Take time from an injected `TimeProvider`. Never call `DateTime.Now` or
  `DateTimeOffset.UtcNow` directly. Replay mode replaces the `TimeProvider`.
- Store all timestamps in SQLite as Unix seconds in UTC (`INTEGER`).
- Put configuration in options records: `TradingOptions`, `RiskOptions`, `AgentOptions`.
  Do not put a strategy number in code. See
  [strategy parameters](trading/strategy-parameters.md).

## External process control

The host starts two Alpaca MCP servers as child processes. Use `ProcessStartInfo` with
`ArgumentList`. Never build one command string. Never use a shell. The host owns the child
lifetime and stops both at shutdown. Details are in
[MCP integration](alpaca/mcp-integration.md).

## Data access

Use `Microsoft.Data.Sqlite` with **Dapper** (ADR-004). Write the SQL yourself. Let Dapper do
the mapping.

```csharp
var rows = await connection.QueryAsync<Bar>(
    """
    SELECT symbol, timestamp_utc AS TimestampUtc, open, high, low, close, volume
    FROM bars
    WHERE symbol = @symbol AND timeframe = @timeframe AND timestamp_utc <= @asOf
    ORDER BY timestamp_utc
    """,
    new { symbol, timeframe, asOf });
```

Rules:

- **Always use a parameter.** Never build SQL with string concatenation or interpolation.
- Alias a snake_case column to the record property name, or keep one mapping place for it.
- Map to a `record`. Dapper binds a constructor.
- Keep every SQL string inside `Storage/`. No other folder contains SQL.
- Do not add Dapper.Contrib or a full ORM. The SQL stays visible (ADR-004).

See [storage summary](storage/summary.md) and [storage schema](storage/schema.md).

## Dependencies

Keep the dependency list small. The approved stack is in
[technology stack](architecture/technology-stack.md). Semantic Kernel, the Alpaca CLI, and
the Claude Agent SDK are rejected. The reasons are in
[architecture decisions](architecture/decisions.md).

## Testing

Write a unit test for every calculation that touches money, probability, or time. The full
plan is in [testing strategy](operations/testing-strategy.md).

## Documentation language

Write all lode files in ASD-STE100 Simplified Technical English. Use short sentences. Use
one idea for each sentence. Use the active voice. Use Mermaid for all diagrams.
