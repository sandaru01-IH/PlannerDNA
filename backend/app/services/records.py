"""Validation and idempotent storage of research records.

Every write is keyed by the client's own record id. Resending an identical
record returns ``duplicate`` and changes nothing; reusing an id for different
content is refused with 409. Numbers are never trusted: snapshots are re-hashed
and their metrics, constraints and features recomputed with the published rules.
"""

from __future__ import annotations

import hashlib
import json
import sqlite3
from dataclasses import dataclass
from typing import Any

from pydantic import BaseModel

from app.domain.city import City, CityFormatError, city_from_dict
from app.domain.metrics import (
    COST_RULES_VERSION,
    FEATURE_VERSION,
    METRIC_VERSION,
    Metrics,
    ScenarioRules,
    diff_values,
    evaluate,
    evaluate_constraints,
    extract_features,
)
from app.errors import ApiError
from app.schemas.records import AlternativeIn, CityStateIn, EventIn, FeatureVectorIn, PreferenceIn, ScenarioIn, SessionIn
from app.services import evaluation_rules
from app.storage.db import utc_now

EDIT_ACTIONS = {
    "set_park", "set_residential", "set_commercial", "demolish", "raise_floors", "lower_floors",
    "restore_baseline", "load_design",
}
ACTIONS_BY_EVENT_TYPE = {
    "command": EDIT_ACTIONS,
    "undo": EDIT_ACTIONS,
    "redo": EDIT_ACTIONS,
    "checkpoint": {"save_alternative_a", "save_alternative_b", "load_evaluation_pair"},
    "decision": {"record_preference"},
}
CONSTRAINT_ORDER = ("budget", "housing_floor", "max_floors", "cost_rules_defined")


def canonical_json(data: Any) -> str:
    return json.dumps(data, sort_keys=True, separators=(",", ":"), ensure_ascii=False)


def payload_sha256(model: BaseModel, exclude: set[str] | None = None) -> str:
    return hashlib.sha256(canonical_json(model.model_dump(mode="json", exclude=exclude)).encode("utf-8")).hexdigest()


def none_if_empty(value: str | None) -> str | None:
    return value if value else None


def _insert_idempotent(conn: sqlite3.Connection, table: str, key_column: str, key: str, sha: str,
                       row: dict[str, Any], what: str) -> str:
    """Returns 'created' or 'duplicate'. Table/column names are code constants, never user input."""
    existing = conn.execute(f"SELECT payload_sha256 FROM {table} WHERE {key_column} = ?", (key,)).fetchone()
    if existing is not None:
        if existing[0] == sha:
            return "duplicate"
        raise ApiError(409, "id_conflict", f"{what} '{key}' already exists with different content; the original is kept.")
    columns = ", ".join(row)
    placeholders = ", ".join("?" for _ in row)
    try:
        conn.execute(f"INSERT INTO {table} ({columns}) VALUES ({placeholders})", tuple(row.values()))
    except sqlite3.IntegrityError as exc:
        raise ApiError(409, "integrity_conflict", f"{what} '{key}' conflicts with an existing record: {exc}") from exc
    return "created"


# ---------------------------------------------------------------- scenarios

def rules_from_json(constraints_json: str) -> ScenarioRules:
    return ScenarioRules(**json.loads(constraints_json))


def _parse_city(data: dict[str, Any], what: str) -> City:
    try:
        return city_from_dict(data)
    except CityFormatError as exc:
        raise ApiError(422, "invalid_city", f"{what}: {exc}") from exc
    except (KeyError, TypeError, ValueError) as exc:
        raise ApiError(422, "invalid_city", f"{what} could not be parsed: {exc}") from exc


def _require_versions(metric_version: str, cost_rules_version: str | None = None) -> None:
    if metric_version != METRIC_VERSION:
        raise ApiError(422, "unsupported_version", f"Metric version {metric_version!r} is not supported (server: {METRIC_VERSION}).")
    if cost_rules_version is not None and cost_rules_version != COST_RULES_VERSION:
        raise ApiError(422, "unsupported_version",
                       f"Cost rules version {cost_rules_version!r} is not supported (server: {COST_RULES_VERSION}).")


def register_scenario(conn: sqlite3.Connection, s: ScenarioIn) -> str:
    _require_versions(s.metric_version, s.cost_rules_version)
    _require_versions(s.baseline_metrics.metric_version, s.baseline_metrics.cost_rules_version)
    if not s.baseline.content_hash:
        raise ApiError(422, "invalid_city", "The baseline must include its content_hash.")
    baseline = _parse_city(s.baseline.model_dump(), "Baseline")
    mismatched = [
        name for name, a, b in (
            ("scenario_id", baseline.scenario_id, s.scenario_id),
            ("seed", baseline.seed, s.seed),
            ("generator_version", baseline.generator_version, s.generator_version),
            ("metric_version", baseline.metric_version, s.metric_version),
        ) if a != b
    ]
    if mismatched:
        raise ApiError(422, "scenario_mismatch", "Baseline metadata does not match the scenario.", mismatched)
    if baseline.content_hash() != s.baseline_hash:
        raise ApiError(422, "baseline_mismatch", "baseline_hash does not match the baseline cells.")

    rules = ScenarioRules(**s.constraints.model_dump())
    computed = evaluate(baseline, baseline, rules)
    problems = diff_values(computed.to_dict(), s.baseline_metrics.model_dump(), Metrics.COMPARED_FIELDS)
    if problems:
        raise ApiError(422, "metrics_mismatch", "Baseline metrics differ from the server's recomputation.", problems)

    # created_utc is excluded so re-registering the same scenario later is a harmless duplicate.
    sha = payload_sha256(s, exclude={"created_utc"})
    return _insert_idempotent(conn, "scenario", "id", s.scenario_id, sha, {
        "id": s.scenario_id,
        "seed": s.seed,
        "generator_version": s.generator_version,
        "metric_version": s.metric_version,
        "cost_rules_version": s.cost_rules_version,
        "mission_id": s.mission_id,
        "baseline_hash": s.baseline_hash,
        "city_baseline_json": canonical_json(s.baseline.model_dump(mode="json")),
        "constraints_json": canonical_json(s.constraints.model_dump(mode="json")),
        "baseline_metrics_json": canonical_json(computed.to_dict()),
        "created_utc": s.created_utc,
        "payload_sha256": sha,
        "received_utc": utc_now(),
    }, "Scenario")


def _load_scenario(conn: sqlite3.Connection, scenario_id: str) -> sqlite3.Row:
    row = conn.execute("SELECT * FROM scenario WHERE id = ?", (scenario_id,)).fetchone()
    if row is None:
        raise ApiError(422, "unknown_scenario", f"Scenario '{scenario_id}' has not been registered.")
    return row


def _load_session(conn: sqlite3.Connection, session_id: str, scenario_id: str) -> sqlite3.Row:
    row = conn.execute("SELECT * FROM session WHERE id = ?", (session_id,)).fetchone()
    if row is None:
        raise ApiError(422, "unknown_session", f"Session '{session_id}' has not been started.")
    if row["scenario_id"] != scenario_id:
        raise ApiError(422, "scenario_mismatch",
                       f"Session '{session_id}' belongs to scenario '{row['scenario_id']}', not '{scenario_id}'.")
    return row


# ---------------------------------------------------------------- sessions

def start_session(conn: sqlite3.Connection, s: SessionIn) -> str:
    scenario = _load_scenario(conn, s.scenario_id)
    if s.baseline_hash != scenario["baseline_hash"]:
        raise ApiError(422, "baseline_mismatch", "The session's baseline differs from the registered scenario.")
    sha = payload_sha256(s)
    return _insert_idempotent(conn, "session", "id", s.session_id, sha, {
        "id": s.session_id,
        "scenario_id": s.scenario_id,
        "started_utc": s.started_utc,
        "ended_utc": None,
        "session_status": "active",
        "app_version": s.app_version,
        "payload_sha256": sha,
        "received_utc": utc_now(),
    }, "Session")


# ---------------------------------------------------------------- events

def record_event(conn: sqlite3.Connection, e: EventIn) -> str:
    _load_session(conn, e.session_id, e.scenario_id)
    if e.action not in ACTIONS_BY_EVENT_TYPE[e.event_type]:
        raise ApiError(422, "invalid_action", f"Action '{e.action}' is not valid for a '{e.event_type}' event.")
    if e.has_cell_change:
        if not e.cell_id or e.before is None or e.after is None:
            raise ApiError(422, "invalid_event", "An event with a cell change needs cell_id, before and after.")
        for label, cell in (("before", e.before), ("after", e.after)):
            if cell.cell_id != e.cell_id:
                raise ApiError(422, "invalid_event", f"'{label}' describes {cell.cell_id!r}, not {e.cell_id!r}.")
    if e.event_type == "undo" and not e.reversal_of_event_id:
        raise ApiError(422, "invalid_event", "An undo event must name the event it reverses.")

    sha = payload_sha256(e)
    return _insert_idempotent(conn, "event", "event_id", e.event_id, sha, {
        "event_id": e.event_id,
        "session_id": e.session_id,
        "scenario_id": e.scenario_id,
        "sequence": e.sequence,
        "event_type": e.event_type,
        "action": e.action,
        "cell_id": none_if_empty(e.cell_id),
        "before_json": canonical_json(e.before.model_dump()) if e.has_cell_change and e.before else None,
        "after_json": canonical_json(e.after.model_dump()) if e.has_cell_change and e.after else None,
        "metrics_before_json": canonical_json(e.metrics_before.model_dump()) if e.metrics_before else None,
        "metrics_after_json": canonical_json(e.metrics_after.model_dump()) if e.metrics_after else None,
        "input_source": e.input_source,
        "utc_time": e.utc_time,
        "reversal_of_event_id": none_if_empty(e.reversal_of_event_id),
        "original_event_id": none_if_empty(e.original_event_id),
        "note": e.note,
        "payload_sha256": sha,
        "received_utc": utc_now(),
    }, "Event")


# ---------------------------------------------------------------- alternatives

def _check_fixed_properties(state: City, baseline: City) -> list[str]:
    problems = []
    for c, b in zip(state.cells, baseline.cells):
        if c.fixed_properties() != b.fixed_properties():
            problems.append(f"{c.id}: fixed parcel properties were altered")
        elif (b.use in ("road", "civic") or not b.editable) and c != b:
            problems.append(f"{c.id}: {b.use} parcel is fixed/protected but was changed")
    return problems


@dataclass
class DesignCheck:
    state: City
    metrics: Metrics
    constraints: dict[str, bool]
    features: dict[str, Any]
    feasible: bool
    failed: list[str]
    changed: list[str]


def recompute_design(scenario: sqlite3.Row, snapshot: CityStateIn, state_hash: str, what: str) -> DesignCheck:
    """Re-hashes a design and recomputes metrics, constraints and features with the published rules."""
    baseline = _parse_city(json.loads(scenario["city_baseline_json"]), "Stored baseline")
    state = _parse_city(snapshot.model_dump(), what)
    if not state.same_scenario_as(baseline):
        raise ApiError(422, "scenario_mismatch", f"{what} belongs to a different scenario, size or version.")
    if state.content_hash() != state_hash:
        raise ApiError(422, "state_hash_mismatch", f"{what}: state_hash does not match the cells.")
    fixed = _check_fixed_properties(state, baseline)
    if fixed:
        raise ApiError(422, "invalid_design", f"{what} changes parcels that cannot be edited.", fixed[:20])

    rules = rules_from_json(scenario["constraints_json"])
    baseline_metrics = evaluate(baseline, baseline, rules)
    metrics = evaluate(state, baseline, rules)
    constraints = evaluate_constraints(state, metrics, baseline_metrics, rules)
    return DesignCheck(
        state=state, metrics=metrics, constraints=constraints,
        features=extract_features(metrics, baseline_metrics, rules),
        feasible=all(constraints.values()),
        failed=[cid for cid in CONSTRAINT_ORDER if not constraints[cid]],
        changed=state.changed_cell_ids(baseline),
    )


def feature_problems(check: DesignCheck, claimed: FeatureVectorIn) -> list[str]:
    if claimed.feature_version != FEATURE_VERSION:
        raise ApiError(422, "unsupported_version", f"Feature version {claimed.feature_version!r} is not supported.")
    return diff_values(check.features, claimed.model_dump(), [k for k in check.features if k != "feature_version"])


def save_alternative(conn: sqlite3.Connection, a: AlternativeIn) -> str:
    scenario = _load_scenario(conn, a.scenario_id)
    _load_session(conn, a.session_id, a.scenario_id)
    _require_versions(a.metric_version, a.cost_rules_version)
    if a.features.feature_version != FEATURE_VERSION:
        raise ApiError(422, "unsupported_version", f"Feature version {a.features.feature_version!r} is not supported.")
    if a.baseline_hash != scenario["baseline_hash"]:
        raise ApiError(422, "baseline_mismatch", "The alternative was derived from a different baseline.")

    check = recompute_design(scenario, a.snapshot, a.state_hash, "Snapshot")
    metrics, constraints, features, feasible, failed = check.metrics, check.constraints, check.features, check.feasible, check.failed

    problems = diff_values(metrics.to_dict(), a.metrics.model_dump(), Metrics.COMPARED_FIELDS)
    problems += feature_problems(check, a.features)
    claimed_constraints = {c.id: c.passed for c in a.constraints}
    if claimed_constraints != constraints:
        problems.append(f"constraints: client sent {claimed_constraints}, server computed {constraints}")
    if a.feasible != feasible:
        problems.append(f"feasible: client sent {a.feasible}, server computed {feasible}")
    if a.failed_constraints != failed:
        problems.append(f"failed_constraints: client sent {a.failed_constraints}, server computed {failed}")
    if a.changed_cell_ids != check.changed:
        problems.append(f"changed_cell_ids: client sent {len(a.changed_cell_ids)}, server computed {len(check.changed)}")
    if problems:
        raise ApiError(422, "metrics_mismatch", "The alternative's reported values differ from the server's recomputation.", problems)

    sha = payload_sha256(a)
    return _insert_idempotent(conn, "alternative", "id", a.alternative_id, sha, {
        "id": a.alternative_id,
        "scenario_id": a.scenario_id,
        "session_id": a.session_id,
        "slot": a.slot,
        "baseline_hash": a.baseline_hash,
        "state_hash": a.state_hash,
        "snapshot_json": canonical_json(a.snapshot.model_dump(mode="json")),
        "metrics_json": canonical_json(metrics.to_dict()),
        "features_json": canonical_json(features),
        "feature_version": FEATURE_VERSION,
        "constraint_state_json": canonical_json({"constraints": constraints, "failed": failed, "feasible": feasible}),
        "feasible": int(feasible),
        "created_utc": a.created_utc,
        "payload_sha256": sha,
        "received_utc": utc_now(),
    }, "Alternative")


# ---------------------------------------------------------------- preferences

def pair_key(a_id: str, b_id: str) -> str:
    first, second = sorted((a_id, b_id))
    return f"{first}|{second}"


def save_preference(conn: sqlite3.Connection, p: PreferenceIn) -> str:
    # An exact retry is answered before any other check, so it stays a harmless duplicate.
    existing = conn.execute("SELECT payload_sha256 FROM comparison WHERE comparison_id = ?", (p.comparison_id,)).fetchone()
    # Records written before Phase 4 had no evaluation_trial_id; leaving it out when empty keeps their hashes valid.
    sha = payload_sha256(p, exclude=None if p.evaluation_trial_id else {"evaluation_trial_id"})
    if existing is not None:
        if existing[0] == sha:
            return "duplicate"
        raise ApiError(409, "id_conflict", f"Comparison '{p.comparison_id}' already exists with different content; the original is kept.")

    _load_scenario(conn, p.scenario_id)
    _load_session(conn, p.session_id, p.scenario_id)
    if p.alternative_a_id == p.alternative_b_id:
        raise ApiError(422, "self_comparison", "An alternative cannot be compared with itself.")
    if p.feature_version != FEATURE_VERSION:
        raise ApiError(422, "unsupported_version", f"Feature version {p.feature_version!r} is not supported.")
    if p.shown_left_alternative_id not in (p.alternative_a_id, p.alternative_b_id):
        raise ApiError(422, "invalid_display_order", "shown_left_alternative_id must be one of the two alternatives.")
    repeat_index, repeated_pair_id, trial = evaluation_rules.check_preference(conn, p)

    alts = {}
    for label, alt_id in (("A", p.alternative_a_id), ("B", p.alternative_b_id)):
        row = conn.execute("SELECT * FROM alternative WHERE id = ?", (alt_id,)).fetchone()
        if row is None:
            raise ApiError(422, "unknown_alternative", f"Alternative {label} '{alt_id}' has not been saved on the server.")
        if row["scenario_id"] != p.scenario_id:
            raise ApiError(422, "scenario_mismatch",
                           f"Alternative {label} belongs to scenario '{row['scenario_id']}', not '{p.scenario_id}'.")
        alts[label] = row
    if alts["A"]["state_hash"] == alts["B"]["state_hash"]:
        raise ApiError(422, "identical_designs", "A and B are the same design; there is nothing to choose between.")

    a_feasible, b_feasible = bool(alts["A"]["feasible"]), bool(alts["B"]["feasible"])
    if (p.a_feasible, p.b_feasible) != (a_feasible, b_feasible):
        raise ApiError(422, "feasibility_mismatch",
                       f"Client reported feasibility A={p.a_feasible}, B={p.b_feasible}; server has A={a_feasible}, B={b_feasible}.")
    eligible = p.mode == "train" and p.choice in ("A", "B") and a_feasible and b_feasible
    if p.training_eligible != eligible:
        raise ApiError(422, "eligibility_mismatch",
                       f"training_eligible must be {eligible}: only a train-mode A/B choice between two feasible designs is eligible.")

    key = pair_key(p.alternative_a_id, p.alternative_b_id)
    prior = conn.execute("SELECT comparison_id FROM comparison WHERE pair_key = ? AND repeat_index = ?",
                         (key, repeat_index)).fetchone()
    if prior is not None:
        raise ApiError(409, "pair_already_decided", "This A/B pair already has a recorded decision.",
                       {"existing_comparison_id": prior[0]})

    right = p.alternative_b_id if p.shown_left_alternative_id == p.alternative_a_id else p.alternative_a_id
    status = _insert_idempotent(conn, "comparison", "comparison_id", p.comparison_id, sha, {
        "comparison_id": p.comparison_id,
        "scenario_id": p.scenario_id,
        "session_id": p.session_id,
        "alt_a_id": p.alternative_a_id,
        "alt_b_id": p.alternative_b_id,
        "pair_key": key,
        "repeat_index": repeat_index,
        "choice": p.choice,
        "reason": p.reason,
        "mode": p.mode,
        "display_order_json": canonical_json({"left": p.shown_left_alternative_id, "right": right}),
        "repeated_pair_id": repeated_pair_id,
        "model_version_locked": none_if_empty(p.model_version_locked),
        "evaluation_trial_id": none_if_empty(p.evaluation_trial_id),
        "a_feasible": int(a_feasible),
        "b_feasible": int(b_feasible),
        "training_eligible": int(eligible),
        "eligibility_note": p.eligibility_note,
        "feature_version": p.feature_version,
        "app_version": p.app_version,
        "created_utc": p.created_utc,
        "payload_sha256": sha,
        "received_utc": utc_now(),
    }, "Comparison")
    if trial is not None and status == "created":
        evaluation_rules.link_trial(conn, trial, p.comparison_id)
    return status


# ---------------------------------------------------------------- reads

def preference_stats(conn: sqlite3.Connection) -> dict[str, Any]:
    rows = conn.execute("SELECT choice, mode, training_eligible, scenario_id FROM comparison").fetchall()
    stats = {
        "unique_comparisons": len(rows),
        "scenarios": len({r["scenario_id"] for r in rows}),
        "choice_a": sum(r["choice"] == "A" for r in rows),
        "choice_b": sum(r["choice"] == "B" for r in rows),
        "ties": sum(r["choice"] == "tie" for r in rows),
        "skips": sum(r["choice"] == "skip" for r in rows),
        "train_mode": sum(r["mode"] == "train" for r in rows),
        "evaluation_mode": sum(r["mode"] == "evaluation" for r in rows),
        "training_eligible": sum(bool(r["training_eligible"]) for r in rows),
        "training_eligible_scenarios": len({r["scenario_id"] for r in rows if r["training_eligible"]}),
    }
    stats["summary"] = (
        f"Training samples: {stats['training_eligible']} unique direct comparisons across "
        f"{stats['training_eligible_scenarios']} scenarios ({stats['unique_comparisons']} decisions stored in total)."
    )
    return stats


def session_timeline(conn: sqlite3.Connection, session_id: str) -> list[dict[str, Any]]:
    if conn.execute("SELECT 1 FROM session WHERE id = ?", (session_id,)).fetchone() is None:
        raise ApiError(404, "unknown_session", f"Session '{session_id}' is not stored on the server.")
    rows = conn.execute("SELECT * FROM event WHERE session_id = ? ORDER BY sequence", (session_id,)).fetchall()
    return [_event_dict(r) for r in rows]


def _decode(row: sqlite3.Row, json_columns: tuple[str, ...], drop: tuple[str, ...] = ("payload_sha256",)) -> dict[str, Any]:
    d = {k: row[k] for k in row.keys() if k not in drop}
    for col in json_columns:
        if d.get(col) is not None:
            d[col] = json.loads(d[col])
    return d


def _event_dict(row: sqlite3.Row) -> dict[str, Any]:
    return _decode(row, ("before_json", "after_json", "metrics_before_json", "metrics_after_json"))


def export_all(conn: sqlite3.Connection, schema_version: int) -> dict[str, Any]:
    scenarios = [_decode(r, ("city_baseline_json", "constraints_json", "baseline_metrics_json"))
                 for r in conn.execute("SELECT * FROM scenario ORDER BY id")]
    sessions = [_decode(r, ()) for r in conn.execute("SELECT * FROM session ORDER BY started_utc, id")]
    events = [_event_dict(r) for r in conn.execute("SELECT * FROM event ORDER BY session_id, sequence")]
    alternatives = [_decode(r, ("snapshot_json", "metrics_json", "features_json", "constraint_state_json"))
                    for r in conn.execute("SELECT * FROM alternative ORDER BY created_utc, id")]
    comparisons = [_decode(r, ("display_order_json",)) for r in conn.execute("SELECT * FROM comparison ORDER BY created_utc, comparison_id")]
    model_runs = [_decode(r, ("training_scenario_ids_json", "hyperparameters_json", "evaluation_json", "training_comparison_ids_json",
                              "package_versions_json", "summary_json"), drop=())
                  for r in conn.execute("SELECT * FROM model_run ORDER BY trained_utc, id")]
    # Sealed predictions stay sealed in an export: before the choice, and, while the test is open,
    # until revealed (seeing earlier predictions could steer later test choices).
    sealed_rows = conn.execute(
        "SELECT t.id, t.prediction_id FROM evaluation_trial t JOIN evaluation_plan p ON p.id = t.plan_id "
        "WHERE t.status = 'awaiting_choice' OR (t.status = 'answered' AND p.status = 'open')").fetchall()
    sealed = {r[1] for r in sealed_rows}
    sealed_trials = {r[0] for r in sealed_rows}
    predictions = []
    for r in conn.execute("SELECT * FROM prediction ORDER BY created_utc, id"):
        d = _decode(r, ("candidate_features_json",), drop=())
        if d["id"] in sealed:
            d["predicted_probability"] = None
            d["candidate_features_json"] = {"sealed": True}
        predictions.append(d)
    plans = []
    for r in conn.execute("SELECT * FROM evaluation_plan ORDER BY registered_utc, id"):
        d = _decode(r, ("training_scenario_ids_json", "evaluation_scenarios_json", "report_json"), drop=())
        plans.append(d)
    trials = []
    for r in conn.execute("SELECT * FROM evaluation_trial ORDER BY plan_id, trial_index"):
        d = _decode(r, (), drop=())
        if d["id"] in sealed_trials:
            d["p_a"] = None
            d["sealed_payload"] = None
        trials.append(d)
    return {
        "export_manifest": {
            "format": "plannerdna-research-export",
            "format_version": 1,
            "db_schema_version": schema_version,
            "metric_version": METRIC_VERSION,
            "cost_rules_version": COST_RULES_VERSION,
            "feature_version": FEATURE_VERSION,
            "exported_utc": utc_now(),
            "counts": {
                "scenarios": len(scenarios), "sessions": len(sessions), "events": len(events),
                "alternatives": len(alternatives), "comparisons": len(comparisons),
                "model_runs": len(model_runs), "predictions": len(predictions),
                "evaluation_plans": len(plans), "evaluation_trials": len(trials),
            },
            "notes": [
                "All data is synthetic and was produced on this computer; nothing was uploaded.",
                "Each alternative's snapshot_json reproduces the city; metrics can be recomputed from it with the rules above.",
                "model_runs lists every fitted preference model with its training comparison ids, hyperparameters, seed, "
                "package versions and diagnostics; the model files themselves are in the backend's data/models folder.",
                "Model weights are statistical associations with explicit choices, not motives or values.",
                "evaluation_plans are pre-registered blinded evaluations (frozen model, unseen scenarios); a closed plan "
                "carries its report. Predictions of trials still awaiting a choice are redacted (sealed).",
            ],
        },
        "scenarios": scenarios,
        "sessions": sessions,
        "events": events,
        "alternatives": alternatives,
        "comparisons": comparisons,
        "model_runs": model_runs,
        "predictions": predictions,
        "evaluation_plans": plans,
        "evaluation_trials": trials,
    }


COMPARISON_CSV_FEATURES = (
    "delta_access_fraction", "delta_housing_fraction", "delta_park_share_fraction",
    "negative_budget_fraction", "negative_lost_housing_fraction",
)


def comparisons_csv_rows(conn: sqlite3.Connection) -> tuple[list[str], list[list[Any]]]:
    header = ["comparison_id", "scenario_id", "session_id", "alt_a_id", "alt_b_id", "choice", "mode",
              "training_eligible", "a_feasible", "b_feasible", "shown_left", "feature_version", "created_utc"]
    header += [f"a_{f}" for f in COMPARISON_CSV_FEATURES] + [f"b_{f}" for f in COMPARISON_CSV_FEATURES] + ["reason"]
    features = {r["id"]: json.loads(r["features_json"]) for r in conn.execute("SELECT id, features_json FROM alternative")}
    rows = []
    for r in conn.execute("SELECT * FROM comparison ORDER BY created_utc, comparison_id"):
        order = json.loads(r["display_order_json"])
        fa, fb = features[r["alt_a_id"]], features[r["alt_b_id"]]
        rows.append(
            [r["comparison_id"], r["scenario_id"], r["session_id"], r["alt_a_id"], r["alt_b_id"], r["choice"], r["mode"],
             r["training_eligible"], r["a_feasible"], r["b_feasible"], order["left"], r["feature_version"], r["created_utc"]]
            + [fa[f] for f in COMPARISON_CSV_FEATURES] + [fb[f] for f in COMPARISON_CSV_FEATURES] + [r["reason"]]
        )
    return header, rows
