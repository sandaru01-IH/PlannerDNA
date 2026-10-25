"""Rules that keep a blinded evaluation blinded, shared by the record, model and evaluation services.

While a plan is open its evaluation scenarios are sealed: no training choice may be
recorded on them, no prediction or ranking may be requested for them outside the
trial protocol, and no model may be trained. An evaluation-mode choice is accepted
only as the answer to one registered trial, with the plan's frozen model version.
"""

from __future__ import annotations

import json
import sqlite3
from typing import Any

from app.errors import ApiError
from app.storage.db import utc_now


def open_plan(conn: sqlite3.Connection) -> sqlite3.Row | None:
    return conn.execute("SELECT * FROM evaluation_plan WHERE status = 'open'").fetchone()


def reserved_scenario_ids(conn: sqlite3.Connection) -> set[str]:
    plan = open_plan(conn)
    if plan is None:
        return set()
    return {s["scenario_id"] for s in json.loads(plan["evaluation_scenarios_json"])}


def refuse_if_sealed(conn: sqlite3.Connection, scenario_id: str, what: str) -> None:
    plan = open_plan(conn)
    if plan is not None and scenario_id in reserved_scenario_ids(conn):
        raise ApiError(409, "sealed_for_evaluation",
                       f"Scenario '{scenario_id}' is reserved for the open blinded evaluation {plan['id']}; {what} is not "
                       "available for it until the evaluation is closed.", {"plan_id": plan["id"]})


def refuse_training_while_open(conn: sqlite3.Connection) -> None:
    plan = open_plan(conn)
    if plan is not None:
        raise ApiError(409, "evaluation_in_progress",
                       f"Blinded evaluation {plan['id']} is open and has frozen model {plan['model_run_id']}. Training is "
                       "paused until the evaluation is closed, so the evaluated model cannot change.",
                       {"plan_id": plan["id"], "model_version": plan["model_run_id"]})


def check_preference(conn: sqlite3.Connection, p: Any) -> tuple[int, str | None, sqlite3.Row | None]:
    """Validates the evaluation fields of a preference. Returns (repeat_index, repeated_pair_id, trial row)."""
    if p.mode == "train":
        if p.model_version_locked:
            raise ApiError(422, "model_lock_requires_evaluation",
                           "model_version_locked is only used for evaluation-mode choices of a blinded trial.")
        if p.evaluation_trial_id:
            raise ApiError(422, "trial_requires_evaluation_mode", "A choice linked to a blinded trial must use mode 'evaluation'.")
        plan = open_plan(conn)
        if plan is not None and p.scenario_id in reserved_scenario_ids(conn):
            raise ApiError(409, "scenario_reserved_for_evaluation",
                           f"Scenario '{p.scenario_id}' is reserved for the open blinded evaluation {plan['id']}, so training "
                           "choices cannot be recorded on it.", {"plan_id": plan["id"]})
        return 0, None, None

    if not p.evaluation_trial_id:
        raise ApiError(422, "evaluation_trial_required",
                       "Evaluation-mode choices must answer a pre-registered blinded trial (evaluation_trial_id). Use mode "
                       "'train' for ordinary comparisons.")
    trial = conn.execute("SELECT * FROM evaluation_trial WHERE id = ?", (p.evaluation_trial_id,)).fetchone()
    if trial is None:
        raise ApiError(422, "unknown_trial", f"Blinded trial '{p.evaluation_trial_id}' is not registered on the server.")
    plan = conn.execute("SELECT * FROM evaluation_plan WHERE id = ?", (trial["plan_id"],)).fetchone()
    if plan["status"] != "open":
        raise ApiError(409, "evaluation_closed", f"Evaluation {plan['id']} is closed; it no longer accepts choices.")
    if trial["comparison_id"] is not None:
        raise ApiError(409, "trial_already_answered", "This blinded trial already has a recorded choice.",
                       {"existing_comparison_id": trial["comparison_id"]})
    if (trial["scenario_id"], trial["alt_a_id"], trial["alt_b_id"]) != (p.scenario_id, p.alternative_a_id, p.alternative_b_id):
        raise ApiError(422, "trial_mismatch", "The choice is about a different scenario or pair than the registered trial.")
    if p.shown_left_alternative_id != trial["shown_left_alternative_id"]:
        raise ApiError(422, "display_order_mismatch", "shown_left_alternative_id differs from the trial's randomised screen order.")
    if p.model_version_locked != trial["model_run_id"]:
        raise ApiError(422, "model_lock_mismatch",
                       f"model_version_locked must be the plan's frozen model {trial['model_run_id']}.")
    if trial["kind"] == "repeat":
        original = conn.execute("SELECT comparison_id FROM evaluation_trial WHERE id = ?", (trial["repeat_of_trial_id"],)).fetchone()
        return 1, original["comparison_id"], trial
    return 0, None, trial


def link_trial(conn: sqlite3.Connection, trial: sqlite3.Row, comparison_id: str) -> None:
    now = utc_now()
    updated = conn.execute(
        "UPDATE evaluation_trial SET comparison_id = ?, status = 'answered', answered_utc = ? WHERE id = ? AND comparison_id IS NULL",
        (comparison_id, now, trial["id"]),
    ).rowcount
    if updated != 1:
        raise ApiError(409, "trial_already_answered", "This blinded trial already has a recorded choice.")
    conn.execute("UPDATE prediction SET comparison_id = ? WHERE id = ?", (comparison_id, trial["prediction_id"]))
