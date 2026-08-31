-- The agent database. Two roles, per .lode/storage/summary.md:
--   1. Cache       -- bars, news, option_contracts, option_bars, llm_cache.
--                     Replay reads these so it never calls Alpaca twice for the
--                     same history.
--   2. Audit trail -- evaluation_runs, forecasts, agent_tool_calls, decisions,
--                     orders, expert_scores, equity_snapshots. What the agent
--                     thought and did.
--
-- SQLite is NOT the source of truth for broker positions. Alpaca is.
--
-- Conventions: timestamps are Unix seconds UTC stored as INTEGER. Money is REAL,
-- read back into decimal in C#; never sum money in SQL. JSON columns take a
-- _json suffix. Column names are snake_case and every SELECT aliases them to the
-- record property name.
--
-- Every statement is IF NOT EXISTS, so startup verification is idempotent.

-- ---------------------------------------------------------------- cache

-- available_utc is the instant the bar became knowable, and it is the column every replay
-- read filters on. timestamp_utc is the START of the bar interval, so a bar carries a close
-- that has not happened yet at its own timestamp: a 1Day bar stamped 05:00Z holds the 16:00
-- ET close, and reading it at 09:30 ET would leak most of a session. Filtering on
-- timestamp_utc is therefore a future-data leak, not a clamp.
CREATE TABLE IF NOT EXISTS bars (
    symbol          TEXT NOT NULL,
    timeframe       TEXT NOT NULL,
    timestamp_utc   INTEGER NOT NULL,
    available_utc   INTEGER NOT NULL,
    open            REAL NOT NULL,
    high            REAL NOT NULL,
    low             REAL NOT NULL,
    close           REAL NOT NULL,
    volume          REAL NOT NULL,
    trade_count     REAL,
    vwap            REAL,
    PRIMARY KEY (symbol, timeframe, timestamp_utc)
);

CREATE INDEX IF NOT EXISTS ix_bars_available ON bars (symbol, timeframe, available_utc);

CREATE TABLE IF NOT EXISTS news (
    id              INTEGER PRIMARY KEY,
    published_utc   INTEGER NOT NULL,
    headline        TEXT NOT NULL,
    summary         TEXT,
    source          TEXT,
    author          TEXT,
    url             TEXT,
    symbols_json    TEXT NOT NULL
);

-- One news item names many symbols, so this join table carries the per-symbol
-- lookup that the cheap filter and the replay get_news tool need.
CREATE TABLE IF NOT EXISTS news_symbols (
    news_id         INTEGER NOT NULL,
    symbol          TEXT NOT NULL,
    PRIMARY KEY (symbol, news_id),
    FOREIGN KEY (news_id) REFERENCES news(id)
);

CREATE INDEX IF NOT EXISTS ix_news_published ON news (published_utc);

-- The expired contract catalog. Replay builds an option chain from this joined
-- to option_bars, because Alpaca serves no historical chain snapshot.
CREATE TABLE IF NOT EXISTS option_contracts (
    contract_symbol TEXT PRIMARY KEY,
    underlying      TEXT NOT NULL,
    expiration      TEXT NOT NULL,   -- ISO date; a contract identity, not an instant
    strike          REAL NOT NULL,
    option_type     TEXT NOT NULL,   -- call or put
    style           TEXT,
    multiplier      INTEGER
);

CREATE INDEX IF NOT EXISTS ix_option_contracts_lookup
    ON option_contracts (underlying, expiration, option_type, strike);

-- Daily option OHLCV. Alpaca serves no historical option quote and no historical
-- greek, so this close price is the only record of what the option market
-- charged. There is deliberately no bid, ask, or delta column: the data does not
-- exist, and a nullable column would invite code to pretend otherwise.
-- See .lode/replay/option-data-availability.md.
-- available_utc carries the same meaning as in `bars`, and matters more here: an option bar
-- is one price per session, so filtering on session_utc would let a cycle at 09:30 read that
-- session's own closing premium. Replay reads this column, never session_utc.
CREATE TABLE IF NOT EXISTS option_bars (
    contract_symbol TEXT NOT NULL,
    session_utc     INTEGER NOT NULL,
    available_utc   INTEGER NOT NULL,
    open            REAL NOT NULL,
    high            REAL NOT NULL,
    low             REAL NOT NULL,
    close           REAL NOT NULL,
    volume          REAL NOT NULL,
    trade_count     REAL,
    vwap            REAL,
    PRIMARY KEY (contract_symbol, session_utc),
    FOREIGN KEY (contract_symbol) REFERENCES option_contracts(contract_symbol)
);

CREATE INDEX IF NOT EXISTS ix_option_bars_session ON option_bars (session_utc);
CREATE INDEX IF NOT EXISTS ix_option_bars_available ON option_bars (contract_symbol, available_utc);

-- Replay must never pay twice for the same historical question. The key is a
-- hash over the agent, the model, the replay instant, and the full prompt, so a
-- changed prompt misses the cache instead of returning a stale answer.
CREATE TABLE IF NOT EXISTS llm_cache (
    cache_key       TEXT PRIMARY KEY,
    agent           TEXT NOT NULL,
    model           TEXT NOT NULL,
    as_of_utc       INTEGER NOT NULL,
    response_json   TEXT NOT NULL,
    created_utc     INTEGER NOT NULL
);

-- ---------------------------------------------------------------- audit trail

-- One row is one evaluated option event. The mode column separates live data
-- from replay data.
-- One row is one evaluated option event: the war room sat over this contract, and this is
-- what the market looked like at the time. proposal_id ties it to the sitting in the log.
CREATE TABLE IF NOT EXISTS evaluation_runs (
    id                      INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp_utc           INTEGER NOT NULL,
    mode                    TEXT NOT NULL,
    proposal_id             TEXT,
    symbol                  TEXT NOT NULL,
    current_price           REAL NOT NULL,
    option_symbol           TEXT NOT NULL,
    option_type             TEXT NOT NULL,
    strike                  REAL NOT NULL,
    expiration_utc          INTEGER NOT NULL,
    market_probability      REAL,
    status                  TEXT NOT NULL,
    market_snapshot_json    TEXT
);

CREATE INDEX IF NOT EXISTS ix_evaluation_runs_time ON evaluation_runs (mode, timestamp_utc);

-- One row is one seat's opinion of one evaluated contract.
--
-- probability is NULLABLE, and deliberately. A war-room seat argues and votes; it does not
-- have to produce a number, and ExposureRiskPersona is plain C# that never will. Requiring a
-- probability would drop exactly the seats that reason in words, which are most of them.
CREATE TABLE IF NOT EXISTS forecasts (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id          INTEGER NOT NULL,
    forecaster      TEXT NOT NULL,
    vote            TEXT,
    probability     REAL,
    confidence      REAL,
    reasoning       TEXT,
    evidence_json   TEXT,
    created_utc     INTEGER NOT NULL,
    FOREIGN KEY (run_id) REFERENCES evaluation_runs(id)
);

CREATE INDEX IF NOT EXISTS ix_forecasts_run ON forecasts (run_id);

CREATE TABLE IF NOT EXISTS agent_tool_calls (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id          INTEGER NOT NULL,
    agent           TEXT NOT NULL,
    tool_name       TEXT NOT NULL,
    arguments_json  TEXT NOT NULL,
    result_json     TEXT,
    started_utc     INTEGER NOT NULL,
    duration_ms     INTEGER,
    status          TEXT NOT NULL,
    FOREIGN KEY (run_id) REFERENCES evaluation_runs(id)
);

CREATE INDEX IF NOT EXISTS ix_agent_tool_calls_run ON agent_tool_calls (run_id);

-- What the system decided about one evaluated contract, and whether the guardrails allowed
-- it. A rejected action gets a row exactly like an accepted one: a run that records only its
-- trades cannot show that the risk rules ever did anything.
--
-- combined_probability is NULLABLE for the same reason as forecasts.probability: the room
-- votes, and a vote is not a probability. It holds the proposer's number when there is one.
CREATE TABLE IF NOT EXISTS decisions (
    id                      INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id                  INTEGER NOT NULL,
    combined_probability    REAL,
    market_probability      REAL,
    edge                    REAL,
    net_vote                REAL,
    action                  TEXT NOT NULL,
    reason                  TEXT,
    risk_result             TEXT,
    created_utc             INTEGER NOT NULL,
    FOREIGN KEY (run_id) REFERENCES evaluation_runs(id)
);

CREATE INDEX IF NOT EXISTS ix_decisions_run ON decisions (run_id);

-- One row is one immutable proposal version. A superseded version stays available for
-- audit, but only the final decision can own an order through decisions and orders.
CREATE TABLE IF NOT EXISTS proposal_review_passes (
    id                          INTEGER PRIMARY KEY AUTOINCREMENT,
    proposal_id                 TEXT NOT NULL,
    proposal_version            INTEGER NOT NULL,
    review_pass                 INTEGER NOT NULL,
    superseded                  INTEGER NOT NULL DEFAULT 0,
    verdict                     TEXT NOT NULL,
    rejection_code              TEXT,
    option_symbol               TEXT,
    thesis                      TEXT NOT NULL,
    thesis_conditions_json      TEXT NOT NULL,
    operation_json              TEXT NOT NULL,
    analyses_json               TEXT NOT NULL,
    discussion_json             TEXT NOT NULL,
    votes_json                  TEXT NOT NULL,
    created_utc                 INTEGER NOT NULL,
    UNIQUE (proposal_id, proposal_version, review_pass)
);

CREATE INDEX IF NOT EXISTS ix_proposal_review_passes_proposal
    ON proposal_review_passes (proposal_id, proposal_version, review_pass);

-- client_order_id TEXT NOT NULL UNIQUE is the idempotency guarantee. The row is
-- written BEFORE the order is submitted, so a duplicate submit fails at the
-- database rather than at the broker.
--
-- Deviation from .lode/storage/schema.md: decision_id is nullable and carries no
-- foreign key. The --smoke path reserves an order with no decision behind it,
-- and inventing a synthetic decisions row to satisfy a constraint would put a
-- decision the agent never made into the audit trail. A NULL decision_id means
-- exactly that: an operator check, not an agent decision.
CREATE TABLE IF NOT EXISTS orders (
    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    decision_id         INTEGER,
    mode                TEXT NOT NULL DEFAULT 'live',
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

-- ---------------------------------------------------------------- scores

CREATE TABLE IF NOT EXISTS expert_scores (
    forecaster      TEXT PRIMARY KEY,
    sample_count    INTEGER NOT NULL,
    average_brier   REAL,
    weight          REAL NOT NULL,
    updated_utc     INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS equity_snapshots (
    timestamp_utc   INTEGER NOT NULL,
    mode            TEXT NOT NULL DEFAULT 'live',
    equity          REAL NOT NULL,
    cash            REAL NOT NULL,
    unrealized_pnl  REAL,
    realized_pnl    REAL,
    PRIMARY KEY (mode, timestamp_utc)
);
