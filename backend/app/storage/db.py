"""SQLite database with ordered, recorded migrations.

Every connection enables foreign keys. Migrations run inside a transaction and
are recorded in ``schema_migration`` so a restarted backend never re-applies
or skips one.
"""

from __future__ import annotations

import sqlite3
from contextlib import contextmanager
from datetime import datetime, timezone
from pathlib import Path
from typing import Iterator

MIGRATION_1 = """
CREATE TABLE scenario (
    id                    TEXT PRIMARY KEY,
    seed                  INTEGER NOT NULL,
    generator_version     TEXT NOT NULL,
    metric_version        TEXT NOT NULL,
    cost_rules_version    TEXT NOT NULL,
    mission_id            TEXT NOT NULL,
    baseline_hash         TEXT NOT NULL,
    city_baseline_json    TEXT NOT NULL,
    constraints_json      TEXT NOT NULL,
    baseline_metrics_json TEXT NOT NULL,
    created_utc           TEXT NOT NULL,
    payload_sha256        TEXT NOT NULL,
    received_utc          TEXT NOT NULL
);

CREATE TABLE session (
    id             TEXT PRIMARY KEY,
    scenario_id    TEXT NOT NULL REFERENCES scenario(id),
    started_utc    TEXT NOT NULL,
    ended_utc      TEXT,
    session_status TEXT NOT NULL CHECK (session_status IN ('active', 'ended')),
    app_version    TEXT NOT NULL,
    payload_sha256 TEXT NOT NULL,
    received_utc   TEXT NOT NULL
);

CREATE TABLE event (
    event_id             TEXT PRIMARY KEY,
    session_id           TEXT NOT NULL REFERENCES session(id),
    scenario_id          TEXT NOT NULL REFERENCES scenario(id),
    sequence             INTEGER NOT NULL CHECK (sequence >= 1),
    event_type           TEXT NOT NULL CHECK (event_type IN ('command', 'undo', 'redo', 'checkpoint', 'decision')),
    action               TEXT NOT NULL,
    cell_id              TEXT,
    before_json          TEXT,
    after_json           TEXT,
    metrics_before_json  TEXT,
    metrics_after_json   TEXT,
    input_source         TEXT NOT NULL,
    utc_time             TEXT NOT NULL,
    reversal_of_event_id TEXT,
    original_event_id    TEXT,
    note                 TEXT NOT NULL,
    payload_sha256       TEXT NOT NULL,
    received_utc         TEXT NOT NULL,
    UNIQUE (session_id, sequence)
);
CREATE INDEX event_by_session ON event(session_id, sequence);

CREATE TABLE alternative (
    id                    TEXT PRIMARY KEY,
    scenario_id           TEXT NOT NULL REFERENCES scenario(id),
    session_id            TEXT NOT NULL REFERENCES session(id),
    slot                  TEXT NOT NULL CHECK (slot IN ('A', 'B')),
    baseline_hash         TEXT NOT NULL,
    state_hash            TEXT NOT NULL,
    snapshot_json         TEXT NOT NULL,
    metrics_json          TEXT NOT NULL,
    features_json         TEXT NOT NULL,
    feature_version       TEXT NOT NULL,
    constraint_state_json TEXT NOT NULL,
    feasible              INTEGER NOT NULL CHECK (feasible IN (0, 1)),
    created_utc           TEXT NOT NULL,
    payload_sha256        TEXT NOT NULL,
    received_utc          TEXT NOT NULL
);
CREATE INDEX alternative_by_scenario ON alternative(scenario_id);

CREATE TABLE comparison (
    comparison_id        TEXT PRIMARY KEY,
    scenario_id          TEXT NOT NULL REFERENCES scenario(id),
    session_id           TEXT NOT NULL REFERENCES session(id),
    alt_a_id             TEXT NOT NULL REFERENCES alternative(id),
    alt_b_id             TEXT NOT NULL REFERENCES alternative(id),
    pair_key             TEXT NOT NULL,
    repeat_index         INTEGER NOT NULL DEFAULT 0,
    choice               TEXT NOT NULL CHECK (choice IN ('A', 'B', 'tie', 'skip')),
    reason               TEXT NOT NULL,
    mode                 TEXT NOT NULL CHECK (mode IN ('train', 'evaluation')),
    display_order_json   TEXT NOT NULL,
    repeated_pair_id     TEXT,
    model_version_locked TEXT,
    a_feasible           INTEGER NOT NULL CHECK (a_feasible IN (0, 1)),
    b_feasible           INTEGER NOT NULL CHECK (b_feasible IN (0, 1)),
    training_eligible    INTEGER NOT NULL CHECK (training_eligible IN (0, 1)),
    eligibility_note     TEXT NOT NULL,
    feature_version      TEXT NOT NULL,
    app_version          TEXT NOT NULL,
    created_utc          TEXT NOT NULL,
    payload_sha256       TEXT NOT NULL,
    received_utc         TEXT NOT NULL,
    CHECK (alt_a_id <> alt_b_id),
    UNIQUE (pair_key, repeat_index)
);
CREATE INDEX comparison_by_scenario ON comparison(scenario_id);

-- Model tables (rows are written from Phase 3; migration 2 adds history columns).
CREATE TABLE model_run (
    id                          TEXT PRIMARY KEY,
    trained_utc                 TEXT NOT NULL,
    model_kind                  TEXT NOT NULL,
    model_artifact_path         TEXT NOT NULL,
    feature_version             TEXT NOT NULL,
    training_scenario_ids_json  TEXT NOT NULL,
    unique_comparison_count     INTEGER NOT NULL,
    hyperparameters_json        TEXT NOT NULL,
    evaluation_json             TEXT NOT NULL,
    random_seed                 INTEGER NOT NULL
);

CREATE TABLE prediction (
    id                      TEXT PRIMARY KEY,
    model_run_id            TEXT NOT NULL REFERENCES model_run(id),
    scenario_id             TEXT NOT NULL REFERENCES scenario(id),
    comparison_id           TEXT REFERENCES comparison(comparison_id),
    candidate_features_json TEXT NOT NULL,
    predicted_probability   REAL NOT NULL CHECK (predicted_probability >= 0 AND predicted_probability <= 1),
    revealed_after_choice   INTEGER NOT NULL CHECK (revealed_after_choice IN (0, 1)),
    created_utc             TEXT NOT NULL
);
"""

MIGRATION_2 = """
-- Phase 3: model history. Model files live in <data_dir>/models as checksummed JSON.
ALTER TABLE model_run ADD COLUMN ml_version TEXT NOT NULL DEFAULT '';
ALTER TABLE model_run ADD COLUMN status TEXT NOT NULL DEFAULT 'superseded';
ALTER TABLE model_run ADD COLUMN artifact_sha256 TEXT NOT NULL DEFAULT '';
ALTER TABLE model_run ADD COLUMN data_fingerprint TEXT NOT NULL DEFAULT '';
ALTER TABLE model_run ADD COLUMN training_comparison_ids_json TEXT NOT NULL DEFAULT '[]';
ALTER TABLE model_run ADD COLUMN package_versions_json TEXT NOT NULL DEFAULT '{}';
ALTER TABLE model_run ADD COLUMN summary_json TEXT NOT NULL DEFAULT '{}';
CREATE UNIQUE INDEX model_run_by_fingerprint ON model_run(data_fingerprint) WHERE data_fingerprint <> '';
CREATE UNIQUE INDEX model_run_single_active ON model_run(status) WHERE status = 'active';

ALTER TABLE prediction ADD COLUMN prediction_kind TEXT NOT NULL DEFAULT 'pair';
ALTER TABLE prediction ADD COLUMN in_training_set INTEGER NOT NULL DEFAULT 0;
CREATE INDEX prediction_by_model ON prediction(model_run_id);
"""

MIGRATION_3 = """
-- Phase 4: pre-registered blinded evaluation. A plan freezes one model and lists unseen
-- evaluation scenarios before any evaluation choice exists; each trial seals the frozen
-- model's prediction (SHA-256 over the probability and a random nonce) before the choice.
CREATE TABLE evaluation_plan (
    id                          TEXT PRIMARY KEY,
    status                      TEXT NOT NULL CHECK (status IN ('open', 'closed')),
    protocol_version            TEXT NOT NULL,
    model_run_id                TEXT NOT NULL REFERENCES model_run(id),
    model_artifact_sha256       TEXT NOT NULL,
    training_scenario_ids_json  TEXT NOT NULL,
    training_comparison_count   INTEGER NOT NULL,
    evaluation_scenarios_json   TEXT NOT NULL,
    pairs_per_scenario          INTEGER NOT NULL CHECK (pairs_per_scenario >= 1),
    repeat_count                INTEGER NOT NULL CHECK (repeat_count >= 0),
    plan_seed                   INTEGER NOT NULL,
    candidate_seed_base         INTEGER NOT NULL,
    pair_rule                   TEXT NOT NULL,
    note                        TEXT NOT NULL,
    app_version                 TEXT NOT NULL,
    created_utc                 TEXT NOT NULL,
    registered_utc              TEXT NOT NULL,
    registration_sha256         TEXT NOT NULL,
    closed_utc                  TEXT,
    close_reason                TEXT,
    report_json                 TEXT,
    report_sha256               TEXT,
    payload_sha256              TEXT NOT NULL
);
CREATE UNIQUE INDEX evaluation_plan_single_open ON evaluation_plan(status) WHERE status = 'open';

CREATE TABLE evaluation_trial (
    id                        TEXT PRIMARY KEY,
    plan_id                   TEXT NOT NULL REFERENCES evaluation_plan(id),
    trial_index               INTEGER NOT NULL CHECK (trial_index >= 0),
    kind                      TEXT NOT NULL CHECK (kind IN ('primary', 'repeat')),
    scenario_id               TEXT NOT NULL REFERENCES scenario(id),
    alt_a_id                  TEXT NOT NULL REFERENCES alternative(id),
    alt_b_id                  TEXT NOT NULL REFERENCES alternative(id),
    pair_key                  TEXT NOT NULL,
    shown_left_alternative_id TEXT NOT NULL,
    repeat_of_trial_id        TEXT REFERENCES evaluation_trial(id),
    candidate_seed            INTEGER NOT NULL,
    pair_rule                 TEXT NOT NULL,
    prediction_id             TEXT NOT NULL REFERENCES prediction(id),
    model_run_id              TEXT NOT NULL REFERENCES model_run(id),
    p_a                       REAL NOT NULL CHECK (p_a >= 0 AND p_a <= 1),
    sealed_payload            TEXT NOT NULL,
    sealed_sha256             TEXT NOT NULL,
    status                    TEXT NOT NULL CHECK (status IN ('awaiting_choice', 'answered', 'revealed')),
    comparison_id             TEXT UNIQUE REFERENCES comparison(comparison_id),
    registered_utc            TEXT NOT NULL,
    answered_utc              TEXT,
    revealed_utc              TEXT,
    payload_sha256            TEXT NOT NULL,
    CHECK (alt_a_id <> alt_b_id),
    CHECK (shown_left_alternative_id IN (alt_a_id, alt_b_id)),
    UNIQUE (plan_id, trial_index)
);
CREATE UNIQUE INDEX evaluation_trial_one_repeat ON evaluation_trial(repeat_of_trial_id) WHERE repeat_of_trial_id IS NOT NULL;
CREATE UNIQUE INDEX evaluation_trial_primary_pair ON evaluation_trial(pair_key) WHERE kind = 'primary';
CREATE INDEX evaluation_trial_by_plan ON evaluation_trial(plan_id, trial_index);

ALTER TABLE comparison ADD COLUMN evaluation_trial_id TEXT;
CREATE UNIQUE INDEX comparison_by_trial ON comparison(evaluation_trial_id) WHERE evaluation_trial_id IS NOT NULL;
"""

MIGRATIONS: list[tuple[int, str, str]] = [
    (1, "initial research notebook schema", MIGRATION_1),
    (2, "model history and prediction log", MIGRATION_2),
    (3, "pre-registered blinded evaluation", MIGRATION_3),
]

# Deletion order respects foreign keys (children first).
DATA_TABLES_DELETE_ORDER = ["evaluation_trial", "evaluation_plan", "prediction", "model_run", "comparison", "alternative",
                            "event", "session", "scenario"]


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat()


class Database:
    def __init__(self, path: Path):
        self.path = Path(path)

    def connect(self) -> sqlite3.Connection:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        conn = sqlite3.connect(self.path, timeout=10)
        conn.row_factory = sqlite3.Row
        conn.execute("PRAGMA foreign_keys = ON")
        return conn

    @contextmanager
    def transaction(self) -> Iterator[sqlite3.Connection]:
        """One connection, one transaction: commit on success, roll back on any error."""
        conn = self.connect()
        try:
            conn.execute("BEGIN IMMEDIATE")
            yield conn
            conn.commit()
        except BaseException:
            conn.rollback()
            raise
        finally:
            conn.close()

    @contextmanager
    def reader(self) -> Iterator[sqlite3.Connection]:
        conn = self.connect()
        try:
            yield conn
        finally:
            conn.close()

    def migrate(self) -> int:
        conn = self.connect()
        try:
            conn.execute("PRAGMA journal_mode = WAL")
            conn.execute(
                "CREATE TABLE IF NOT EXISTS schema_migration ("
                " version INTEGER PRIMARY KEY, description TEXT NOT NULL, applied_at_utc TEXT NOT NULL)"
            )
            applied = {row[0] for row in conn.execute("SELECT version FROM schema_migration")}
            for version, description, sql in MIGRATIONS:
                if version in applied:
                    continue
                # executescript commits implicitly, so the script carries its own transaction.
                conn.executescript(
                    "BEGIN;\n" + sql + "\nINSERT INTO schema_migration(version, description, applied_at_utc) "
                    f"VALUES ({int(version)}, '{description}', '{utc_now()}');\nCOMMIT;"
                )
            return self.schema_version(conn)
        finally:
            conn.close()

    @staticmethod
    def schema_version(conn: sqlite3.Connection) -> int:
        row = conn.execute("SELECT MAX(version) FROM schema_migration").fetchone()
        return int(row[0] or 0)
