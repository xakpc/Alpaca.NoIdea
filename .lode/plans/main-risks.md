# Main Risks

## 1. The strategy has no real edge

**The largest risk is not software.** The largest risk is that the forecasts do not beat
option pricing.

Mitigation:

- Historical replay.
- Compare the experts against each other.
- Measure Brier scores.
- Do not force trades.
- Keep the strategy parameters visible and testable.

## 2. LLM reasoning is not useful

The Research Agent can add noise. The Critic can be too negative.

Mitigation:

- Give each result a measurable probability.
- Score each expert.
- Reduce the weight of a weak expert.
- Keep the numerical model independent of the LLM output.

## 3. Free option data is not precise enough

The Indicative feed is not consolidated OPRA. Note that the **latest** option quote and
chain are real time on the Basic plan; the 15-minute limit applies to historical option bars
and trades. The risk is quote quality, not live freshness.

Mitigation:

- Use simple short-horizon decisions.
- Reject stale, missing, one-sided, or unusable quotes.
- Do not depend on small pricing differences.
- Require a meaningful edge threshold.
- Account for the historical-data limits in replay.
- State the use of the free Indicative feed in the final submission.

## 4. Alpaca MCP server or tool schema changes

The Alpaca MCP server can change. Tool names and schemas can change with it.

Mitigation:

- Pin the submodule commit and the Docker image tag (ADR-011).
- Validate the required tool names and schemas at startup.
- Keep the typed `IMarketDataGateway` and `ITradingGateway` between strategy code and raw
  MCP results.
- Log the pinned version and the approved tool names at startup.
- Run the MCP integration tests before each competition session.
- **Do not upgrade during the official trading window.**

## 5. Too few historical LLM samples

LLM replay is slow and expensive.

Mitigation:

- Run the ML expert on many historical samples.
- Run the LLM experts only on candidate samples.
- Cache all LLM results.
- Do not repeat the same historical LLM request.

## 6. Thursday final state is mishandled

The FAQ resolves the timing question: Alpaca uses **Thursday 2026-09-03 end-of-day** equity,
and Thursday-expiring exercises and assignments appear in that value.

The remaining risk is our own strategy. It can mishandle Thursday expiration, or it can hold
a position that does not match the intended final-state policy.

Mitigation:

- Treat Thursday end of day as the effective finish line.
- Make the Thursday exit / expiration policy explicit. Do not leave it as a default.
- Test Thursday expiration behavior in the development paper account.
- **Do not depend on Friday option-market activity.**

## Related

- [Open strategy questions](open-strategy-questions.md)
- [Market data policy](../alpaca/market-data-policy.md)
