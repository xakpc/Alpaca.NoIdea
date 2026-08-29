# Storage

One SQLite database file holds all persistent agent data (ADR-004). Access uses
`Microsoft.Data.Sqlite` with **Dapper**. The project writes its own SQL. Dapper maps the
result to a record. Do not add a full ORM.

The default path is `data/trader.db`. The trained model file is `data/historical-model.zip`.

## What SQLite stores

- Historical market data (bars).
- Historical news.
- Expert forecasts.
- LLM tool calls.
- Decisions.
- Orders that this application submitted.
- Expert reliability scores.
- Equity snapshots.

## What SQLite does not store

SQLite is **not** the source of truth for current broker positions.

> Alpaca stores what the account owns. SQLite stores what the agent thought and did.

After a restart the application rebuilds the account state from Alpaca. It must not assume
that SQLite has the latest position state. See
[restart and recovery](../operations/restart-recovery.md).

## Two roles

1. **Cache** — `bars` and `news` hold historical data so that replay does not call Alpaca
   for the same data on each run.
2. **Audit trail** — `evaluation_runs`, `forecasts`, `agent_tool_calls`, `decisions`, and
   `orders` record every step of every decision. This trail answers the ten demo questions
   in [observability](../operations/observability.md).

## Conventions

- All timestamps are Unix seconds in UTC, stored as `INTEGER`.
- JSON payloads use a `_json` column suffix and a `TEXT` type.
- Column names are snake_case. Alias them to the record property name in the SELECT, so
  that Dapper binds without extra configuration.
- Every SQL string lives in `Storage/`. `TradingStore` is the only class that runs SQL.
- Always pass a Dapper parameter. **Never build SQL by string concatenation.**
- A failed SQLite write stops new trading. See
  [fault handling](../operations/fault-handling.md).

## Money and precision

SQLite has no decimal type. Prices and P&L are stored as `REAL`, which is a 64-bit float.
Dapper converts `REAL` to `decimal` when the record property is `decimal`.

**Do not compute a money result by summing `REAL` values in SQL.** Read the rows, then do
the arithmetic in `decimal` in C#. The audit trail is the record of what happened; Alpaca
remains the source of truth for the account value.

## Related

- [Schema](schema.md)
