# Lode Map

Read this index before you inspect the code.

```mermaid
flowchart TD
    L[.lode] --> A[architecture]
    L --> W[war-room]
    L --> T[trading]
    L --> P[alpaca]
    L --> S[storage]
    L --> M[llm]
    L --> O[operations]
    L --> N[plans]
```

## Authority

```text
code > measured evidence > current ADRs
```

The code is the truth about behavior. A measured session is the truth about a result. A decision
record explains why the code is the way it is. If two files disagree, read the code, then repair
the lode.

## Root

- [Project summary](summary.md) - Current product state and safety contracts.
- [Terminology](terminology.md) - Short domain definitions.
- [Practices](practices.md) - Repository and C# practices.

## Architecture

- [Summary](architecture/summary.md) - Host, components, boundaries, non-goals, standing risks.
- [Decisions](architecture/decisions.md) - Active decisions, and the withdrawn identifiers.
- [Dropped designs](architecture/dropped-designs.md) - What was planned, rejected, and why.

## War room and LLM

- [War-room summary](war-room/summary.md) - Proposal, review, discussion, and vote.
- [Votes to verdict](war-room/vote-and-verdict.md) - Tally, thresholds, quorum, and the rejected
  hold.
- [LLM summary](llm/summary.md) - Model call, prompt cache, and cost contracts.
- [Call limits](llm/call-limits.md) - Deadline, seat, transport, and retry limits.
- [Persona contracts](llm/persona-contracts.md) - Trust, forecasts, evidence, and voting meaning.

## Trading and Alpaca

- [Trading summary](trading/summary.md) - Live loop, fixed universe, and contract catalog.
- [Live cycle](trading/live-cycle.md) - Ordered cycle contract.
- [Hard-exit loop](trading/hard-exit-loop.md) - Exit timer, broker gate, and order limits.
- [Risk guardrails](trading/risk-guardrails.md) - Hard limits, compiled constants, fail-closed
  rules.
- [Alpaca summary](alpaca/summary.md) - SDK and MCP separation.
- [MCP integration](alpaca/mcp-integration.md) - Connection, run-mode, and safety contracts.
- [Market-data policy](alpaca/market-data-policy.md) - Current data rules.

## Storage and operations

- [Storage summary](storage/summary.md) - Durable audit purpose.
- [Schema](storage/schema.md) - Eight-table schema, integrity, and evidence links.
- [Operations summary](operations/summary.md) - Deployment, commands, recovery, faults, tests,
  and the human-facing documents.
- [Local development](operations/local-development.md) - Commands, keys, and the debug profile.
- [Observability](operations/observability.md) - Audit inspection.
- [Console rendering](operations/console-rendering.md) - Operator view, symbols, terminal safety.
- [Session results](operations/session-results.md) - Measured cost, timing, and result per
  session.

## Plans

- [Improvements](plans/improvements.md) - The only plan file. Open repairs, open strategy
  questions, and required decisions.

## Reading paths

- New session: [summary](summary.md), then [terminology](terminology.md).
- Change execution: [live cycle](trading/live-cycle.md), then
  [risk guardrails](trading/risk-guardrails.md), then [schema](storage/schema.md).
- Change agents: [war room](war-room/summary.md), then
  [votes to verdict](war-room/vote-and-verdict.md), then [LLM summary](llm/summary.md).
- Judge a change: [session results](operations/session-results.md), then
  [improvements](plans/improvements.md).
- Operate the host: [local development](operations/local-development.md), then
  [observability](operations/observability.md).
- Before you propose a large feature: [dropped designs](architecture/dropped-designs.md).

```powershell
Get-Content .lode/lode-map.md, .lode/terminology.md, .lode/summary.md
```

## Related lodes

- [Project summary](summary.md)
- [Terminology](terminology.md)
- [Practices](practices.md)
