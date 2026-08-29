# Free Market Data Policy

The project uses the **free Alpaca Basic plan with the Indicative options feed** (ADR-010).
The project will not purchase Algo Trader Plus or OPRA. The hackathon permits the free tier.

## What the Basic plan gives

The official hackathon FAQ resolves the timing question. The rule is not "the free feed is
delayed". The rule depends on **which** option data you ask for.

| Data | Basic plan behavior |
|---|---|
| Latest option quote | **Real time.** No 15-minute delay. |
| Latest option chain | **Real time.** No 15-minute delay. |
| Historical option bars | 15-minute restriction applies. |
| Historical option trades | 15-minute restriction applies. |
| Option Greeks in a snapshot | Available. |
| Full consolidated OPRA | Not included. Requires Algo Trader Plus. |

Two more facts:

- The Alpaca **dashboard charts can lag**. The dashboard is not a data source.
- **The agent must decide from API / MCP data only.** Never from a chart.

## The effect on the live strategy

The live path is stronger than revision 1 and 2 of the architecture assumed. The system
**can** use the current option quote for a live decision.

The limit that remains is quality, not freshness:

> The Indicative feed is not the full consolidated OPRA feed. Do not assume that the two are
> identical.

The [Options Evaluator](../experts/options-evaluator.md) must therefore:

- Require a **meaningful** pricing difference. Do not trade a very small quote difference.
- Reject a stale quote.
- Reject a missing quote.
- Reject a one-sided quote.
- Reject an otherwise unusable quote.

The quote-age check stays. It now guards against a broken or frozen quote, not against a
known feed delay.

## The effect on replay

The historical path keeps the old limits. Historical option bars and trades carry the
15-minute restriction and can be incomplete.

Therefore:

- Replay must account for the delay and the availability limits of historical option data.
- Historical option-chain snapshots can differ from live option-chain data.
- **Replay P&L is only as accurate as the stored historical option data.**

See [replay mode](../replay/replay-mode.md).

## Disclosure requirement

The project must state the use of the free Indicative feed in the final hackathon
submission. This is an honesty requirement, not an optional item.

## Related

- [MCP integration](mcp-integration.md)
- [Options Evaluator](../experts/options-evaluator.md)
- [Main risks](../plans/main-risks.md)
