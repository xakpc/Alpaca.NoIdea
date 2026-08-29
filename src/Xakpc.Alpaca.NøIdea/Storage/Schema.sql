-- The audit trail. Part A creates only the orders table: a table that nothing
-- writes to is not worth creating yet.
--
-- Deviation from .lode/storage/schema.md: decision_id is nullable and carries no
-- foreign key, because the decisions table does not exist until the forecasting
-- phases land. Restore NOT NULL and the FK when decisions is created.

CREATE TABLE IF NOT EXISTS orders (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    decision_id         INTEGER,
    alpaca_order_id     TEXT,
    client_order_id     TEXT NOT NULL UNIQUE,
    option_symbol       TEXT NOT NULL,
    side                TEXT NOT NULL,
    quantity            INTEGER NOT NULL,
    order_type          TEXT NOT NULL,
    limit_price         REAL,
    submitted_utc       INTEGER NOT NULL,
    closed_utc          INTEGER,
    status              TEXT NOT NULL,
    realized_pnl        REAL
);
