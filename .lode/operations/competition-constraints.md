# Competition Constraints

The public LabLab event runs from 2026-08-28 through 2026-09-04. The application uses a
narrower configured trading window and a forced Thursday exit. These application values are
code configuration, not a restatement of the public event schedule.

```mermaid
timeline
    title Current application timing
    2026-08-31 09:30 ET : Intended trading start
    2026-09-03 15:30 ET : Forced flatten in code
    2026-09-04 : Latest expiration date accepted by RiskGuard
```

```json
{
  "StartingEquity": 100000,
  "CompetitionFlattenUtc": "2026-09-03T19:30:00Z"
}
```

## Code-enforced constraints

- The typed Alpaca clients use `Environments.Paper`.
- `RiskOptions.CompetitionFlattenUtc` is 2026-09-03 19:30 UTC.
- `RiskGuard.CheckContract` accepts expiration dates through 2026-09-04 and rejects later
  dates.
- `RiskGuard.MandatoryExitReason` requests a close at or after the flatten instant.
- The opening strategy supports long calls and long puts only.
- The current universe has 13 fixed symbols.
- The account-equity default in `TradingOptions` is 100,000 USD.

The difference between the accepted Friday expiration and the Thursday forced close is an
open policy decision. Do not document an earlier-expiration invariant that the code does not
enforce.

## Data and integration

The Alpaca Basic plan provides IEX equity coverage and the Indicative options feed. The Basic
historical API excludes the latest 15 minutes. The application names IEX for stock requests
and Indicative for option chains. It uses current quotes for contract admission.

The project uses Alpaca MCP for persona research and the Alpaca Trading API through the typed
SDK for deterministic paper-account access. The MCP server has no account or trading toolset.

## Current requirement mapping

| Goal | Current implementation |
|---|---|
| Autonomous agent | `LiveSession` and `TradingLoop` run without per-trade approval. |
| Alpaca integration | Read-only MCP research plus typed SDK account, market, and order access. |
| Options trading | Long single-leg call and put orders only. |
| Basic market data | IEX stocks and Indicative options are named in requests. |
| Robustness | Deterministic risk, paper-only clients, durable reservations, reconciliation, and audit evidence. |
| Operator view | Structured console and plain file logs. There is no TUI or web UI. |

## External references

- [LabLab hackathon page](https://lablab.ai/ai-hackathons/alpaca-ai-trading-agents-hackathon)
- [Alpaca market-data plans](https://docs.alpaca.markets/us/docs/about-market-data-api)
- [Alpaca historical option data](https://docs.alpaca.markets/us/docs/historical-option-data)
- [Alpaca MCP server](https://github.com/alpacahq/alpaca-mcp-server)

## Related lodes

- [Operations summary](summary.md)
- [Risk guardrails](../trading/risk-guardrails.md)
- [Alpaca integration](../alpaca/mcp-integration.md)
- [Open strategy questions](../plans/open-strategy-questions.md)
