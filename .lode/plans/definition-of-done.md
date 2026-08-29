# Definition of Done for Competition Start

The system is ready for the official window only if **every** item is true.

## Safety

- [ ] Paper mode is enforced.
- [ ] Official account starts at $100,000.
- [ ] Risk guardrails work.
- [ ] Duplicate order protection works.
- [ ] Restart recovery works.

## Alpaca integration

- [ ] The Alpaca MCP server version is pinned.
- [ ] The read-only MCP connection works.
- [ ] The trading MCP connection works.
- [ ] The read-only MCP tool allowlist works.
- [ ] Trading tools are not visible to the LLM.
- [ ] Option order submission works.
- [ ] Position close works.

## Experts

- [ ] Historical replay works.
- [ ] ML.NET model loads and predicts.
- [ ] Research Agent tools work.
- [ ] Critic Agent tools work.
- [ ] LLM outputs use strict schemas.
- [ ] Expert scores are stored.
- [ ] Option quote validation works.

## Records and operation

- [ ] SQLite audit records work.
- [ ] TUI shows the current status.
- [ ] No manual per-trade approval is required.

## Strategy values

- [ ] Exact strategy thresholds are set.
- [ ] Exact strike rule is set.
- [ ] Exact expiration rule is set.
- [ ] Thursday exit / expiration policy is set, and it follows the Thursday-EOD rule.
- [ ] The system does not depend on Friday option-market activity.

The last group depends on replay evidence and on an official competition answer. See
[open strategy questions](open-strategy-questions.md) and
[strategy parameters](../trading/strategy-parameters.md).

## Related

- [MVP roadmap](mvp-roadmap.md)
