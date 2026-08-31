PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS war_room_sittings (
    proposal_id TEXT PRIMARY KEY, mode TEXT NOT NULL, purpose TEXT NOT NULL,
    started_utc INTEGER NOT NULL, completed_utc INTEGER,
    verdict TEXT, status TEXT NOT NULL, fault TEXT
);
CREATE INDEX IF NOT EXISTS ix_war_room_sittings_time
    ON war_room_sittings (mode, started_utc);

CREATE TABLE IF NOT EXISTS proposal_review_passes (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    proposal_id TEXT NOT NULL, proposal_version INTEGER NOT NULL,
    review_pass INTEGER NOT NULL, superseded INTEGER NOT NULL DEFAULT 0,
    verdict TEXT NOT NULL, rejection_code TEXT, option_symbol TEXT,
    thesis TEXT NOT NULL, thesis_conditions_json TEXT NOT NULL,
    operation_json TEXT NOT NULL, analyses_json TEXT NOT NULL,
    discussion_json TEXT NOT NULL, votes_json TEXT NOT NULL,
    created_utc INTEGER NOT NULL,
    UNIQUE (proposal_id, proposal_version, review_pass),
    FOREIGN KEY (proposal_id) REFERENCES war_room_sittings(proposal_id)
);
CREATE INDEX IF NOT EXISTS ix_proposal_review_passes_proposal
    ON proposal_review_passes (proposal_id, proposal_version, review_pass);

CREATE TABLE IF NOT EXISTS agent_tool_calls (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    proposal_id TEXT NOT NULL, persona TEXT NOT NULL, phase TEXT NOT NULL,
    model TEXT NOT NULL, call_id TEXT NOT NULL, tool_name TEXT NOT NULL,
    arguments_json TEXT NOT NULL, result_json TEXT, status TEXT NOT NULL,
    captured_utc INTEGER NOT NULL,
    UNIQUE (proposal_id, persona, phase, call_id),
    FOREIGN KEY (proposal_id) REFERENCES war_room_sittings(proposal_id)
);
CREATE INDEX IF NOT EXISTS ix_agent_tool_calls_proposal
    ON agent_tool_calls (proposal_id, persona, phase);

CREATE TABLE IF NOT EXISTS decision_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp_utc INTEGER NOT NULL, mode TEXT NOT NULL, proposal_id TEXT,
    purpose TEXT NOT NULL, action TEXT NOT NULL, outcome TEXT NOT NULL,
    reason TEXT, risk_result TEXT, symbol TEXT, option_symbol TEXT,
    option_type TEXT, strike REAL, expiration_utc INTEGER,
    underlying_price REAL, probability REAL, net_vote REAL,
    market_snapshot_json TEXT,
    FOREIGN KEY (proposal_id) REFERENCES war_room_sittings(proposal_id)
);
CREATE INDEX IF NOT EXISTS ix_decision_events_time
    ON decision_events (mode, timestamp_utc);

CREATE TABLE IF NOT EXISTS orders (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    audit_event_id INTEGER, mode TEXT NOT NULL,
    correlation_id TEXT NOT NULL UNIQUE, alpaca_order_id TEXT,
    client_order_id TEXT NOT NULL UNIQUE, option_symbol TEXT NOT NULL,
    side TEXT NOT NULL, quantity INTEGER NOT NULL, order_type TEXT NOT NULL,
    limit_price REAL, submitted_utc INTEGER NOT NULL, closed_utc INTEGER,
    status TEXT NOT NULL, raw_status TEXT,
    filled_quantity INTEGER NOT NULL DEFAULT 0, average_fill_price REAL,
    reconciled_utc INTEGER, realized_pnl REAL,
    FOREIGN KEY (audit_event_id) REFERENCES decision_events(id)
);
CREATE INDEX IF NOT EXISTS ix_orders_audit_event ON orders (audit_event_id);
CREATE INDEX IF NOT EXISTS ix_orders_lifecycle ON orders (mode, status, submitted_utc);

CREATE TABLE IF NOT EXISTS strategy_state (
    mode TEXT PRIMARY KEY,
    policy_json TEXT NOT NULL,
    updated_utc INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS position_review_state (
    mode TEXT NOT NULL, option_symbol TEXT NOT NULL,
    last_reviewed_utc INTEGER NOT NULL, last_news_seen INTEGER NOT NULL,
    PRIMARY KEY (mode, option_symbol)
);

CREATE TABLE IF NOT EXISTS equity_snapshots (
    timestamp_utc INTEGER NOT NULL, mode TEXT NOT NULL,
    equity REAL NOT NULL, cash REAL NOT NULL,
    unrealized_pnl REAL, realized_pnl REAL,
    PRIMARY KEY (mode, timestamp_utc)
);
