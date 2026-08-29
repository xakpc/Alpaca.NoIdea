# Risk and Safety Architecture

The hard risk guardrails are C# rules in `RiskGuard`. **An LLM cannot change them.** Only
the `RiskGuard` can allow an order.

## 1. Paper mode only

The process must fail at startup if it detects live trading mode. The project must use a
competition paper account.

The trading MCP server gets paper credentials only. The application must confirm paper mode
with an account read after it connects. **Startup fails if paper mode cannot be confirmed.**
See [MCP safety](../alpaca/mcp-safety.md).

## 2. LLM write isolation (ADR-005, ADR-006)

The LLM agents see only the read-only MCP connection. The trading MCP tools live in a
separate server process. An LLM agent must never have a tool that can:

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

Every new order must have a unique `client_order_id`. The system must save the ID before or
with the order attempt.

If the MCP order call fails after an uncertain network result, the system must **query the
order by the client ID before it sends another order**.

The `orders` table enforces this with `client_order_id TEXT NOT NULL UNIQUE`.

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
