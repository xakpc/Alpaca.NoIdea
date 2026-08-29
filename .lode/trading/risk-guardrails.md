# Risk and Safety Architecture

The hard risk guardrails are C# rules in `RiskGuard`. **An LLM cannot change them.** Only
the `RiskGuard` can allow an order.

## 1. Paper mode only

**The guarantee is compile-time.** `AlpacaClients` builds every client from
`Environments.Paper`, which is a static property, not configuration. No environment variable,
configuration key, or command-line argument can move the process to a live account; it takes a
source edit. A unit test in `tests/Trader.Tests/SafetyTests.cs` pins it.

`AlpacaOptions.FromEnvironment` additionally rejects `ALPACA_PAPER_TRADE` set to anything other
than `true`, so the MCP server half cannot be pointed at live trading while the SDK half stays
on paper.

## 2. LLM write isolation (ADR-005, ADR-006)

The LLM agents see only the read-only MCP connection. **No MCP server this host runs holds an
order tool at all**, because deterministic C# uses the `Alpaca.Markets` SDK (ADR-001). An LLM
agent must never have a tool that can:

- Submit an order.
- Cancel an order.
- Replace an order.
- Close a position.
- Exercise an option.

Only the deterministic trading engine can do these actions. See
[tool policy](../llm/tool-policy.md).

## 3. Fail closed

The system must skip a trade when:

- Required market data is missing.
- Required market data is stale.
- The ML model fails.
- A required LLM expert fails.
- The option evaluator cannot validate the contract.
- The risk engine fails.
- SQLite cannot record the decision.
- The Alpaca account state cannot be confirmed.
- The system cannot confirm paper mode.

> A skipped trade is better than an unknown trade.

## 4. Order idempotency

Every new order must have a unique `client_order_id`. The system must save the ID **before**
the order attempt, never after.

If the order call fails with an uncertain result, the system must **query the order by the
client ID before it sends another order**. `IAlpacaTradingClient.GetOrderAsync(string, ct)` is
that lookup; a missing order raises `RestClientErrorException` rather than returning null.

**The idempotency check must run before contract selection.** Checking afterwards would let a
re-run pick a fresh contract at a new price instead of resolving the order that may already
exist, which defeats the guard entirely.

The `orders` table enforces uniqueness with `client_order_id TEXT NOT NULL UNIQUE`.

## 5. No arbitrary execution tool

The LLM must never get a general shell-execution tool. The LLM must receive only approved
tools from the read-only Alpaca MCP connection. The system must never pass an LLM-generated
string to a trading tool argument or to the operating system.

## The hard limit list

The `RiskGuard` checks:

- Maximum risk for each trade.
- Maximum number of open positions.
- Maximum new positions for each day.
- Maximum total account risk.
- Maximum daily account loss.
- No duplicate order.
- No duplicate exposure that violates a configured rule.
- No trade with missing or stale data.
- No unsupported option expiration.
- No order if SQLite cannot record the decision.
- No order if the system cannot confirm paper mode.

## Values

The exact financial values are **TBD**. These development defaults are not final strategy
values:

```text
Maximum concurrent positions: 4
Maximum new positions per day: 4
Cycle interval: 30 minutes
```

Risk for each trade, the take-profit level, the stop level, the minimum edge, and the
expiration rules require replay tests before the official run. See
[strategy parameters](strategy-parameters.md).

## Related

- [Live cycle](live-cycle.md)
- [Fault handling](../operations/fault-handling.md)
- [Architecture decisions](../architecture/decisions.md)
