"""Pre-registered blinded evaluation of a frozen preference model (protocol blinded-eval-1.0.0).

1. A plan freezes the active model (version and file checksum) and pre-registers unseen
   evaluation scenarios, the number of pairs per scenario and of repeated pairs. The
   server draws the plan seed that fixes screen sides and repeat selection.
2. Each trial registers one pair of saved designs. The server computes P(A) with the
   frozen model, seals it (SHA-256 over the probability and a random nonce), stores it,
   and returns only the seal and the randomised left/right order.
3. The choice is recorded as an evaluation-mode preference linked to the trial. Only
   then can the sealed prediction be revealed; the reveal returns the sealed payload so
   the client can check it against the seal it received before choosing.
4. Closing the plan scores the frozen model against simple baselines and stores a
   report whose SHA-256 is verified by recomputation whenever it is read.

Evaluation choices are never training data (mode 'evaluation' is excluded from
training), and training is refused while a plan is open.
"""

from __future__ import annotations

import hashlib
import json
import random
import secrets
import sqlite3
from pathlib import Path
from typing import Any

import numpy as np
from scipy.special import expit

from app.domain.metrics import FEATURE_VERSION
from app.errors import ApiError
from app.ml import evaluation as ev
from app.ml import pairwise as pw
from app.ml.artifact import ModelArtifactError, read_artifact
from app.schemas.evaluation import ClosePlanIn, EvaluationPlanIn, EvaluationTrialIn
from app.services import evaluation_rules
from app.services.records import canonical_json, pair_key, payload_sha256
from app.storage.db import Database, utc_now

REPORT_FORMAT = "plannerdna-evaluation-report"
REPORT_FORMAT_VERSION = 1


def _sha(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def _plan(conn: sqlite3.Connection, plan_id: str) -> sqlite3.Row:
    row = conn.execute("SELECT * FROM evaluation_plan WHERE id = ?", (plan_id,)).fetchone()
    if row is None:
        raise ApiError(404, "unknown_plan", f"Evaluation plan '{plan_id}' does not exist.")
    return row


def _trials(conn: sqlite3.Connection, plan_id: str) -> list[sqlite3.Row]:
    return conn.execute(
        """SELECT t.*, c.choice AS choice FROM evaluation_trial t
           LEFT JOIN comparison c ON c.comparison_id = t.comparison_id
           WHERE t.plan_id = ? ORDER BY t.trial_index""", (plan_id,)).fetchall()


def _locked_weights(conn: sqlite3.Connection, data_dir: Path, plan: sqlite3.Row) -> tuple[np.ndarray, dict[str, Any]]:
    run = conn.execute("SELECT * FROM model_run WHERE id = ?", (plan["model_run_id"],)).fetchone()
    if run is None or run["artifact_sha256"] != plan["model_artifact_sha256"]:
        raise ApiError(409, "model_artifact_invalid", "The frozen model of this evaluation is no longer available unchanged.")
    try:
        payload = read_artifact(data_dir, run["model_artifact_path"], plan["model_artifact_sha256"])
    except ModelArtifactError as exc:
        raise ApiError(409, "model_artifact_invalid", f"The frozen model cannot be used: {exc}") from exc
    return np.array(payload["weights_raw"], dtype=float), payload


# ---------------------------------------------------------------- plan

def create_plan(db: Database, data_dir: Path, body: EvaluationPlanIn) -> tuple[str, dict[str, Any]]:
    sha = payload_sha256(body)
    with db.transaction() as conn:
        existing = conn.execute("SELECT payload_sha256 FROM evaluation_plan WHERE id = ?", (body.plan_id,)).fetchone()
        if existing is not None:
            if existing[0] != sha:
                raise ApiError(409, "id_conflict", f"Evaluation plan '{body.plan_id}' already exists with different content.")
            return "duplicate", plan_status(conn, _plan(conn, body.plan_id))
        current = evaluation_rules.open_plan(conn)
        if current is not None:
            raise ApiError(409, "evaluation_already_open",
                           f"Evaluation {current['id']} is still open; close it before pre-registering another.",
                           {"plan_id": current["id"]})
        run = conn.execute("SELECT * FROM model_run WHERE status = 'active'").fetchone()
        if run is None:
            raise ApiError(409, "no_model", "A blinded evaluation needs a trained model to freeze; none exists.")
        if run["id"] != body.model_version:
            raise ApiError(409, "model_changed", f"The active model is {run['id']}, not {body.model_version}. Refresh and try again.",
                           {"active_model_version": run["id"]})
        try:
            artifact = read_artifact(data_dir, run["model_artifact_path"], run["artifact_sha256"])
        except ModelArtifactError as exc:
            raise ApiError(409, "model_artifact_invalid", f"The active model cannot be frozen: {exc}") from exc

        training_scenarios = sorted(artifact["training"]["scenario_ids"])
        training_seeds = {r["seed"] for r in conn.execute(
            f"SELECT seed FROM scenario WHERE id IN ({','.join('?' for _ in training_scenarios)})", training_scenarios)
        } if training_scenarios else set()
        earlier = set()
        for r in conn.execute("SELECT evaluation_scenarios_json FROM evaluation_plan"):
            earlier |= {s["scenario_id"] for s in json.loads(r[0])}
        scenarios = [{"scenario_id": s.scenario_id, "seed": s.seed} for s in body.evaluation_scenarios]
        problems = []
        if len({s["scenario_id"] for s in scenarios}) != len(scenarios) or len({s["seed"] for s in scenarios}) != len(scenarios):
            raise ApiError(422, "duplicate_scenario", "Each evaluation scenario and seed may appear only once.")
        for s in scenarios:
            sid, seed = s["scenario_id"], s["seed"]
            if sid in training_scenarios or seed in training_seeds:
                problems.append(f"{sid}: used to train model {run['id']}")
                continue
            if conn.execute("SELECT 1 FROM comparison WHERE scenario_id = ? AND mode = 'train' LIMIT 1", (sid,)).fetchone():
                problems.append(f"{sid}: already has training choices")
            if sid in earlier:
                problems.append(f"{sid}: used by an earlier evaluation plan")
            stored = conn.execute("SELECT seed FROM scenario WHERE id = ?", (sid,)).fetchone()
            if stored is not None and stored[0] != seed:
                problems.append(f"{sid}: stored with seed {stored[0]}, not {seed}")
        if problems:
            raise ApiError(422, "scenario_not_unseen",
                           "Evaluation scenarios must be unseen: not used for training, without training choices and not "
                           "used by an earlier evaluation.", problems)
        planned = len(scenarios) * body.pairs_per_scenario
        if body.repeat_count > planned:
            raise ApiError(422, "too_many_repeats", f"At most {planned} pairs can be repeated (one repeat per pre-registered pair).")

        plan_seed = secrets.randbelow(2**31 - 2) + 1
        candidate_seed_base = secrets.randbelow(1_000_000_000) + 1
        registered = utc_now()
        registration = {
            "protocol_version": ev.PROTOCOL_VERSION, "plan_id": body.plan_id, "model_version": run["id"],
            "model_artifact_sha256": run["artifact_sha256"], "training_scenario_ids": training_scenarios,
            "training_comparison_count": len(artifact["training"]["comparison_ids"]),
            "evaluation_scenarios": scenarios, "pairs_per_scenario": body.pairs_per_scenario,
            "repeat_count": body.repeat_count, "plan_seed": plan_seed, "candidate_seed_base": candidate_seed_base,
            "pair_rule": body.pair_rule, "created_utc": body.created_utc, "registered_utc": registered,
        }
        conn.execute(
            """INSERT INTO evaluation_plan (id, status, protocol_version, model_run_id, model_artifact_sha256,
                   training_scenario_ids_json, training_comparison_count, evaluation_scenarios_json, pairs_per_scenario,
                   repeat_count, plan_seed, candidate_seed_base, pair_rule, note, app_version, created_utc, registered_utc,
                   registration_sha256, payload_sha256)
               VALUES (?, 'open', ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)""",
            (body.plan_id, ev.PROTOCOL_VERSION, run["id"], run["artifact_sha256"], canonical_json(training_scenarios),
             registration["training_comparison_count"], canonical_json(scenarios), body.pairs_per_scenario,
             body.repeat_count, plan_seed, candidate_seed_base, body.pair_rule, body.note, body.app_version,
             body.created_utc, registered, _sha(canonical_json(registration)), sha),
        )
        return "created", plan_status(conn, _plan(conn, body.plan_id))


def _registration(plan: sqlite3.Row) -> dict[str, Any]:
    return {
        "protocol_version": plan["protocol_version"], "plan_id": plan["id"], "model_version": plan["model_run_id"],
        "model_artifact_sha256": plan["model_artifact_sha256"],
        "training_scenario_ids": json.loads(plan["training_scenario_ids_json"]),
        "training_comparison_count": plan["training_comparison_count"],
        "evaluation_scenarios": json.loads(plan["evaluation_scenarios_json"]),
        "pairs_per_scenario": plan["pairs_per_scenario"], "repeat_count": plan["repeat_count"],
        "plan_seed": plan["plan_seed"], "candidate_seed_base": plan["candidate_seed_base"],
        "pair_rule": plan["pair_rule"], "created_utc": plan["created_utc"], "registered_utc": plan["registered_utc"],
    }


def _repeat_targets(plan: sqlite3.Row, trials: list[sqlite3.Row]) -> list[sqlite3.Row]:
    """Pre-registered pairs to show again, drawn with the plan seed once every first presentation is answered."""
    eligible = [t for t in trials if t["kind"] == "primary" and t["choice"] in ("A", "B")]
    rng = random.Random(f"{plan['plan_seed']}:repeats")
    return rng.sample(eligible, min(plan["repeat_count"], len(eligible)))


def _side_is_a(plan_seed: int, trial_index: int) -> bool:
    return random.Random(f"{plan_seed}:{trial_index}:side").random() < 0.5


def next_step(plan: sqlite3.Row, trials: list[sqlite3.Row]) -> dict[str, Any]:
    if plan["status"] != "open":
        return {"action": "closed", "message": "This evaluation is closed; its report is available."}
    waiting = [t for t in trials if t["status"] == "awaiting_choice"]
    if waiting:
        t = waiting[0]
        return {"action": "answer", "kind": t["kind"], "trial_id": t["id"], "trial_index": t["trial_index"],
                "scenario_id": t["scenario_id"], "seed": _seed_of(plan, t["scenario_id"]), "candidate_seed": t["candidate_seed"],
                "alternative_a_id": t["alt_a_id"], "alternative_b_id": t["alt_b_id"],
                "shown_left_alternative_id": t["shown_left_alternative_id"], "repeat_of_trial_id": t["repeat_of_trial_id"] or "",
                "message": "A registered pair is waiting for your choice."}
    scenarios = json.loads(plan["evaluation_scenarios_json"])
    k = plan["pairs_per_scenario"]
    primaries = [t for t in trials if t["kind"] == "primary"]
    if len(primaries) < len(scenarios) * k:
        i = len(primaries)
        sc = scenarios[i // k]
        return {"action": "new_pair", "kind": "primary", "trial_id": "", "trial_index": len(trials),
                "scenario_id": sc["scenario_id"], "seed": sc["seed"], "candidate_seed": plan["candidate_seed_base"] + len(trials),
                "alternative_a_id": "", "alternative_b_id": "", "shown_left_alternative_id": "", "repeat_of_trial_id": "",
                "message": f"Pair {i % k + 1} of {k} in unseen scenario {i // k + 1} of {len(scenarios)}."}
    targets = _repeat_targets(plan, trials)
    repeats = [t for t in trials if t["kind"] == "repeat"]
    if len(repeats) < len(targets):
        o = targets[len(repeats)]
        return {"action": "repeat", "kind": "repeat", "trial_id": "", "trial_index": len(trials),
                "scenario_id": o["scenario_id"], "seed": _seed_of(plan, o["scenario_id"]), "candidate_seed": o["candidate_seed"],
                "alternative_a_id": o["alt_a_id"], "alternative_b_id": o["alt_b_id"], "shown_left_alternative_id": "",
                "repeat_of_trial_id": o["id"],
                "message": f"Repeated pair {len(repeats) + 1} of {len(targets)}: a pair you have seen, sides swapped."}
    return {"action": "complete", "message": "Every pre-registered pair has been answered. Close the evaluation to see the report."}


def _seed_of(plan: sqlite3.Row, scenario_id: str) -> int:
    for s in json.loads(plan["evaluation_scenarios_json"]):
        if s["scenario_id"] == scenario_id:
            return s["seed"]
    raise ApiError(500, "plan_inconsistent", f"Scenario {scenario_id} is not part of plan {plan['id']}.")


def _public_trial(t: sqlite3.Row) -> dict[str, Any]:
    """A trial as shown before the reveal: the seal, never the probability."""
    revealed = t["status"] == "revealed"
    right = t["alt_b_id"] if t["shown_left_alternative_id"] == t["alt_a_id"] else t["alt_a_id"]
    return {
        "trial_id": t["id"], "plan_id": t["plan_id"], "trial_index": t["trial_index"], "kind": t["kind"],
        "scenario_id": t["scenario_id"], "alternative_a_id": t["alt_a_id"], "alternative_b_id": t["alt_b_id"],
        "shown_left_alternative_id": t["shown_left_alternative_id"], "shown_right_alternative_id": right,
        "repeat_of_trial_id": t["repeat_of_trial_id"] or "", "candidate_seed": t["candidate_seed"],
        "model_version_locked": t["model_run_id"], "sealed_sha256": t["sealed_sha256"], "status": t["status"],
        "comparison_id": t["comparison_id"] or "", "registered_utc": t["registered_utc"],
        "p_a": float(t["p_a"]) if revealed else None, "revealed": revealed,
    }


def plan_status(conn: sqlite3.Connection, plan: sqlite3.Row) -> dict[str, Any]:
    trials = _trials(conn, plan["id"])
    scenarios = json.loads(plan["evaluation_scenarios_json"])
    planned_primary = len(scenarios) * plan["pairs_per_scenario"]
    primaries = [t for t in trials if t["kind"] == "primary"]
    repeats = [t for t in trials if t["kind"] == "repeat"]
    report = json.loads(plan["report_json"]) if plan["report_json"] else None
    return {
        **_registration(plan),
        "status": plan["status"],
        "registration_sha256": plan["registration_sha256"],
        "note": plan["note"],
        "closed_utc": plan["closed_utc"] or "",
        "close_reason": plan["close_reason"] or "",
        "progress": {
            "planned_primary": planned_primary,
            "registered_primary": len(primaries),
            "answered_primary": sum(1 for t in primaries if t["comparison_id"]),
            "planned_repeats": plan["repeat_count"],
            "registered_repeats": len(repeats),
            "answered_repeats": sum(1 for t in repeats if t["comparison_id"]),
            "awaiting_choice": sum(1 for t in trials if t["status"] == "awaiting_choice"),
        },
        "trials": [_public_trial(t) for t in trials],
        "next": next_step(plan, trials),
        "report_available": report is not None,
        "report_summary": report["summary"] if report else "",
        "report_sha256": plan["report_sha256"] or "",
    }


def overview(conn: sqlite3.Connection) -> dict[str, Any]:
    plans = []
    for p in conn.execute("SELECT * FROM evaluation_plan ORDER BY registered_utc DESC, id DESC"):
        report = json.loads(p["report_json"]) if p["report_json"] else None
        model = report["metrics"]["model"] if report else None
        plans.append({
            "plan_id": p["id"], "status": p["status"], "model_version": p["model_run_id"],
            "registered_utc": p["registered_utc"], "closed_utc": p["closed_utc"] or "",
            "scored": report["counts"]["scored"] if report else 0,
            "accuracy": model["accuracy"] if model else 0.0,
            "has_report": report is not None,
            "summary": report["summary"] if report else "",
        })
    current = evaluation_rules.open_plan(conn)
    return {
        "protocol_version": ev.PROTOCOL_VERSION,
        "open_plan_id": current["id"] if current is not None else "",
        "open_plan": plan_status(conn, current) if current is not None else None,
        "count": len(plans),
        "plans": plans,
    }


# ---------------------------------------------------------------- trials

def register_trial(db: Database, data_dir: Path, body: EvaluationTrialIn) -> tuple[str, dict[str, Any]]:
    sha = payload_sha256(body)
    with db.transaction() as conn:
        existing = conn.execute("SELECT * FROM evaluation_trial WHERE id = ?", (body.trial_id,)).fetchone()
        if existing is not None:
            if existing["payload_sha256"] != sha:
                raise ApiError(409, "id_conflict", f"Trial '{body.trial_id}' already exists with different content.")
            return "duplicate", _public_trial(existing)
        plan = _plan(conn, body.plan_id)
        if plan["status"] != "open":
            raise ApiError(409, "evaluation_closed", f"Evaluation {plan['id']} is closed; no further pairs can be registered.")
        trials = _trials(conn, plan["id"])
        step = next_step(plan, trials)
        if step["action"] == "answer":
            raise ApiError(409, "trial_awaiting_choice", "Answer the registered pair before registering another.",
                           {"trial_id": step["trial_id"]})
        if step["action"] == "complete":
            raise ApiError(409, "plan_complete", "Every pre-registered pair has been registered; close the evaluation.")
        if body.trial_index != step["trial_index"]:
            raise ApiError(409, "unexpected_trial_index", f"The next trial index is {step['trial_index']}, not {body.trial_index}.")
        if body.scenario_id != step["scenario_id"]:
            raise ApiError(422, "scenario_not_planned", f"The next pre-registered pair belongs to {step['scenario_id']}.")
        if body.candidate_seed != step["candidate_seed"]:
            raise ApiError(422, "candidate_seed_mismatch", f"The pre-registered candidate seed is {step['candidate_seed']}.")
        if body.pair_rule != plan["pair_rule"]:
            raise ApiError(422, "pair_rule_mismatch", f"The plan pre-registered pair rule {plan['pair_rule']!r}.")
        if step["action"] == "repeat":
            if (body.repeat_of_trial_id, body.alternative_a_id, body.alternative_b_id) != (
                    step["repeat_of_trial_id"], step["alternative_a_id"], step["alternative_b_id"]):
                raise ApiError(422, "repeat_mismatch", "The next trial repeats a specific earlier pair, unchanged.",
                               {"repeat_of_trial_id": step["repeat_of_trial_id"]})
        elif body.repeat_of_trial_id:
            raise ApiError(422, "repeat_mismatch", "The next trial is a first presentation, not a repeat.")

        scenario = conn.execute("SELECT seed FROM scenario WHERE id = ?", (body.scenario_id,)).fetchone()
        if scenario is None:
            raise ApiError(422, "unknown_scenario", f"Scenario '{body.scenario_id}' has not been registered on the server.")
        if scenario["seed"] != step["seed"]:
            raise ApiError(422, "scenario_mismatch", f"Scenario '{body.scenario_id}' was stored with a different seed.")
        if body.alternative_a_id == body.alternative_b_id:
            raise ApiError(422, "self_comparison", "An alternative cannot be compared with itself.")
        alts = {}
        for label, alt_id in (("A", body.alternative_a_id), ("B", body.alternative_b_id)):
            row = conn.execute("SELECT * FROM alternative WHERE id = ?", (alt_id,)).fetchone()
            if row is None:
                raise ApiError(422, "unknown_alternative", f"Alternative {label} '{alt_id}' has not been saved on the server.")
            if row["scenario_id"] != body.scenario_id:
                raise ApiError(422, "scenario_mismatch", f"Alternative {label} belongs to scenario '{row['scenario_id']}'.")
            if not row["feasible"]:
                raise ApiError(422, "infeasible_alternative", f"Alternative {label} violates hard constraints; evaluation pairs must be feasible.")
            if row["feature_version"] != FEATURE_VERSION:
                raise ApiError(422, "unsupported_version", f"Alternative {label} uses feature version {row['feature_version']!r}.")
            alts[label] = row
        if alts["A"]["state_hash"] == alts["B"]["state_hash"]:
            raise ApiError(422, "identical_designs", "A and B are the same design.")
        fa = pw.feature_vector(json.loads(alts["A"]["features_json"]))
        fb = pw.feature_vector(json.loads(alts["B"]["features_json"]))
        key = pair_key(body.alternative_a_id, body.alternative_b_id)
        if step["action"] == "new_pair":
            if not np.any(np.abs(fa - fb) > pw.ZERO_TOLERANCE):
                raise ApiError(422, "uninformative_pair", "The two designs have identical measured outcomes; choose a pair that differs.")
            used = conn.execute(
                "SELECT 1 FROM comparison WHERE alt_a_id IN (?, ?) OR alt_b_id IN (?, ?) LIMIT 1",
                (body.alternative_a_id, body.alternative_b_id, body.alternative_a_id, body.alternative_b_id)).fetchone()
            used = used or conn.execute(
                "SELECT 1 FROM evaluation_trial WHERE alt_a_id IN (?, ?) OR alt_b_id IN (?, ?) LIMIT 1",
                (body.alternative_a_id, body.alternative_b_id, body.alternative_a_id, body.alternative_b_id)).fetchone()
            if used:
                raise ApiError(409, "alternative_already_used", "Evaluation pairs must use freshly saved designs never compared before.")

        weights, _ = _locked_weights(conn, data_dir, plan)
        p_a = float(expit(np.dot(weights, fa - fb)))
        if step["action"] == "repeat":
            original = conn.execute("SELECT shown_left_alternative_id FROM evaluation_trial WHERE id = ?",
                                    (body.repeat_of_trial_id,)).fetchone()
            left_is_a = original["shown_left_alternative_id"] != body.alternative_a_id
        else:
            left_is_a = _side_is_a(plan["plan_seed"], body.trial_index)
        shown_left = body.alternative_a_id if left_is_a else body.alternative_b_id

        nonce = secrets.token_hex(16)
        sealed_payload = canonical_json({
            "trial_id": body.trial_id, "plan_id": plan["id"], "model_version": plan["model_run_id"],
            "alternative_a_id": body.alternative_a_id, "alternative_b_id": body.alternative_b_id,
            "p_a": p_a, "nonce": nonce,
        })
        sealed_sha = _sha(sealed_payload)
        now = utc_now()
        prediction_id = "pred-" + secrets.token_hex(16)
        conn.execute(
            """INSERT INTO prediction (id, model_run_id, scenario_id, comparison_id, candidate_features_json,
                   predicted_probability, revealed_after_choice, created_utc, prediction_kind, in_training_set)
               VALUES (?, ?, ?, NULL, ?, ?, 0, ?, 'evaluation', 0)""",
            (prediction_id, plan["model_run_id"], body.scenario_id,
             canonical_json({"evaluation_trial_id": body.trial_id, "alternative_a_id": body.alternative_a_id,
                             "alternative_b_id": body.alternative_b_id, "features_a": fa.tolist(), "features_b": fb.tolist()}),
             p_a, now),
        )
        conn.execute(
            """INSERT INTO evaluation_trial (id, plan_id, trial_index, kind, scenario_id, alt_a_id, alt_b_id, pair_key,
                   shown_left_alternative_id, repeat_of_trial_id, candidate_seed, pair_rule, prediction_id, model_run_id,
                   p_a, sealed_payload, sealed_sha256, status, registered_utc, payload_sha256)
               VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 'awaiting_choice', ?, ?)""",
            (body.trial_id, plan["id"], body.trial_index, step["kind"], body.scenario_id, body.alternative_a_id,
             body.alternative_b_id, key, shown_left, body.repeat_of_trial_id or None, body.candidate_seed, body.pair_rule,
             prediction_id, plan["model_run_id"], p_a, sealed_payload, sealed_sha, now, sha),
        )
        row = conn.execute("SELECT * FROM evaluation_trial WHERE id = ?", (body.trial_id,)).fetchone()
        return "created", _public_trial(row)


def reveal(db: Database, data_dir: Path, trial_id: str) -> dict[str, Any]:
    with db.transaction() as conn:
        t = conn.execute("SELECT * FROM evaluation_trial WHERE id = ?", (trial_id,)).fetchone()
        if t is None:
            raise ApiError(404, "unknown_trial", f"Blinded trial '{trial_id}' is not registered.")
        if t["comparison_id"] is None:
            raise ApiError(409, "choice_not_recorded",
                           "The sealed prediction is revealed only after your choice for this pair has reached the backend.")
        if t["status"] != "revealed":
            now = utc_now()
            conn.execute("UPDATE evaluation_trial SET status = 'revealed', revealed_utc = ? WHERE id = ?", (now, trial_id))
            conn.execute("UPDATE prediction SET revealed_after_choice = 1 WHERE id = ?", (t["prediction_id"],))
            t = conn.execute("SELECT * FROM evaluation_trial WHERE id = ?", (trial_id,)).fetchone()
        choice = conn.execute("SELECT choice FROM comparison WHERE comparison_id = ?", (t["comparison_id"],)).fetchone()[0]
        plan = _plan(conn, t["plan_id"])
        weights, _ = _locked_weights(conn, data_dir, plan)
        fa, fb = (pw.feature_vector(json.loads(conn.execute("SELECT features_json FROM alternative WHERE id = ?", (i,)).fetchone()[0]))
                  for i in (t["alt_a_id"], t["alt_b_id"]))

    p_a = float(t["p_a"])
    left_is_a = t["shown_left_alternative_id"] == t["alt_a_id"]
    favoured = "neither" if p_a == 0.5 else ("A" if p_a > 0.5 else "B")
    side = {"A": "left" if left_is_a else "right", "B": "right" if left_is_a else "left"}
    matched = None if choice not in ("A", "B") or favoured == "neither" else favoured == choice
    p_left = p_a if left_is_a else 1 - p_a
    favoured_text = "had no preference (50%)" if favoured == "neither" else (
        f"gave the {side[favoured]} design {max(p_a, 1 - p_a) * 100:.0f}%")
    if matched is None:
        outcome = "You chose tie or skip, which the model does not predict." if choice not in ("A", "B") else ""
    else:
        outcome = "The sealed prediction matched your choice." if matched else "The sealed prediction did not match your choice."
    return {
        "trial_id": t["id"], "plan_id": t["plan_id"], "trial_index": t["trial_index"], "kind": t["kind"],
        "model_version": t["model_run_id"], "p_a": p_a, "p_b": 1 - p_a, "p_left": p_left, "p_right": 1 - p_left,
        "shown_left_alternative_id": t["shown_left_alternative_id"], "favoured": favoured,
        "favoured_side": side.get(favoured, "neither"), "choice": choice, "chosen_side": side.get(choice, ""),
        "matched": matched, "comparison_id": t["comparison_id"], "sealed_payload": t["sealed_payload"],
        "sealed_sha256": t["sealed_sha256"], "registered_utc": t["registered_utc"], "answered_utc": t["answered_utc"],
        "revealed_utc": t["revealed_utc"], "contributions": pw.contributions(weights, fa, fb),
        "explanation": f"Sealed before your choice: the frozen model {favoured_text}. {outcome}".strip(),
        "caveat": "One pair says little; the closed evaluation report scores every pre-registered pair together.",
    }


# ---------------------------------------------------------------- close and report

def build_report(conn: sqlite3.Connection, plan: sqlite3.Row) -> dict[str, Any]:
    """Deterministic: the same database rows always give byte-identical canonical JSON."""
    run = conn.execute("SELECT id, trained_utc, artifact_sha256, ml_version, feature_version, random_seed, hyperparameters_json "
                       "FROM model_run WHERE id = ?", (plan["model_run_id"],)).fetchone()
    scenarios = json.loads(plan["evaluation_scenarios_json"])
    seeds = {s["scenario_id"]: s["seed"] for s in scenarios}
    records = []
    for t in _trials(conn, plan["id"]):
        fa, fb = (json.loads(conn.execute("SELECT features_json FROM alternative WHERE id = ?", (i,)).fetchone()[0])
                  for i in (t["alt_a_id"], t["alt_b_id"]))
        records.append(ev.TrialRecord(
            trial_id=t["id"], trial_index=t["trial_index"], kind=t["kind"], scenario_id=t["scenario_id"],
            seed=seeds[t["scenario_id"]], alt_a_id=t["alt_a_id"], alt_b_id=t["alt_b_id"],
            shown_left_is_a=t["shown_left_alternative_id"] == t["alt_a_id"], p_a=float(t["p_a"]),
            diffs=pw.feature_vector(fa) - pw.feature_vector(fb), choice=t["choice"],
            repeat_of_trial_id=t["repeat_of_trial_id"], comparison_id=t["comparison_id"], sealed_sha256=t["sealed_sha256"],
            registered_utc=t["registered_utc"], answered_utc=t["answered_utc"], revealed_utc=t["revealed_utc"]))
    result = ev.evaluate(records, scenarios, plan["pairs_per_scenario"], plan["repeat_count"], plan["model_run_id"])
    return {
        "format": REPORT_FORMAT,
        "format_version": REPORT_FORMAT_VERSION,
        "protocol_version": plan["protocol_version"],
        "plan": {**_registration(plan), "registration_sha256": plan["registration_sha256"], "note": plan["note"],
                 "closed_utc": plan["closed_utc"], "close_reason": plan["close_reason"] or ""},
        "model": {"model_version": run["id"], "trained_utc": run["trained_utc"], "artifact_sha256": run["artifact_sha256"],
                  "ml_version": run["ml_version"], "feature_version": run["feature_version"], "random_seed": run["random_seed"],
                  "hyperparameters": json.loads(run["hyperparameters_json"]), "kind": pw.MODEL_KIND,
                  "features": list(pw.FEATURE_NAMES)},
        "held_out_guarantee": {
            "evaluation_choices_in_training": _evaluation_choices_in_training(conn, plan),
            "evaluation_scenarios_in_training": sorted(set(seeds) & set(json.loads(plan["training_scenario_ids_json"]))),
            "note": "Counts evaluation-mode choices or evaluation scenarios that appear in the frozen model's training data. "
                    "Both are zero by construction; they are recomputed here as a check.",
        },
        **result,
    }


def _evaluation_choices_in_training(conn: sqlite3.Connection, plan: sqlite3.Row) -> int:
    run = conn.execute("SELECT training_comparison_ids_json FROM model_run WHERE id = ?", (plan["model_run_id"],)).fetchone()
    trained = set(json.loads(run[0]))
    answered = {r[0] for r in conn.execute("SELECT comparison_id FROM evaluation_trial WHERE plan_id = ? AND comparison_id IS NOT NULL",
                                           (plan["id"],))}
    return len(trained & answered)


def report_sha256(report: dict[str, Any]) -> str:
    return _sha(canonical_json(report))


def close_plan(db: Database, plan_id: str, body: ClosePlanIn) -> tuple[str, dict[str, Any]]:
    with db.transaction() as conn:
        plan = _plan(conn, plan_id)
        if plan["status"] == "closed":
            return "unchanged", json.loads(plan["report_json"])
        conn.execute("UPDATE evaluation_plan SET status = 'closed', closed_utc = ?, close_reason = ? WHERE id = ?",
                     (utc_now(), body.reason, plan_id))
        plan = _plan(conn, plan_id)
        report = build_report(conn, plan)
        conn.execute("UPDATE evaluation_plan SET report_json = ?, report_sha256 = ? WHERE id = ?",
                     (canonical_json(report), report_sha256(report), plan_id))
        return "closed", report


def get_report(conn: sqlite3.Connection, plan_id: str) -> dict[str, Any]:
    plan = _plan(conn, plan_id)
    if plan["status"] != "closed":
        raise ApiError(409, "evaluation_open", "The report is produced when the evaluation is closed; it is still open.")
    stored = json.loads(plan["report_json"])
    stored_sha = report_sha256(stored)
    recomputed = report_sha256(build_report(conn, plan))
    return {
        "plan_id": plan_id,
        "report_sha256": plan["report_sha256"],
        "verification": {
            "stored_sha256": stored_sha,
            "recomputed_sha256": recomputed,
            "stored_report_unchanged": stored_sha == plan["report_sha256"],
            "reproducible": stored_sha == plan["report_sha256"] == recomputed,
            "note": "The stored report was re-hashed and also recomputed from the stored trials, choices and frozen model "
                    "metadata; three identical hashes mean it is unchanged and exactly reproducible.",
        },
        "report": stored,
    }


TRIAL_CSV_COLUMNS = [
    "trial_id", "trial_index", "kind", "scenario_id", "seed", "alternative_a_id", "alternative_b_id", "shown_left",
    "p_a", "p_left", "model_favoured", "choice", "chosen_side", "model_correct", "scored", "repeat_of_trial_id",
    "comparison_id", "sealed_sha256", "registered_utc", "answered_utc", "revealed_utc",
] + [f"baseline_{name}_p_a" for name, _, _ in ev.EVALUATION_BASELINES] + [f"diff_{name}" for name in pw.FEATURE_NAMES]


def trials_csv_rows(report: dict[str, Any]) -> tuple[list[str], list[list[Any]]]:
    return TRIAL_CSV_COLUMNS, [["" if row.get(c) is None else row.get(c) for c in TRIAL_CSV_COLUMNS] for row in report["trials"]]


def metrics_csv_rows(report: dict[str, Any]) -> tuple[list[str], list[list[Any]]]:
    """Long format, one row per predictor and metric, ready for plotting."""
    header = ["predictor", "label", "metric", "value", "ci_low", "ci_high", "n", "interval_level"]
    level = report["bootstrap"]["level"]
    rows = []
    m = report["metrics"]["model"]
    for metric in ("accuracy", "brier", "log_loss"):
        ci = m.get(f"{metric}_ci") or ["", ""]
        rows.append(["model", f"Frozen model {m['model_version']}", metric, m[metric], ci[0], ci[1], m["n"], level])
    for b in report["metrics"]["baselines"]:
        ci = b.get("accuracy_ci") or ["", ""]
        rows.append([b["name"], b["label"], "accuracy", b["accuracy"], ci[0], ci[1], b["n"], level])
        rows.append([b["name"], b["label"], "brier", b["brier"], "", "", b["n"], level])
        if b["has_log_loss"]:
            rows.append([b["name"], b["label"], "log_loss", b["log_loss"], "", "", b["n"], level])
        diff_ci = b.get("model_minus_baseline_accuracy_ci") or ["", ""]
        rows.append([b["name"], b["label"], "model_minus_baseline_accuracy", b["model_minus_baseline_accuracy"],
                     diff_ci[0], diff_ci[1], b["n"], level])
    c = report["repeat_consistency"]
    if c["rate"] is not None:
        ci = c["rate_ci"] or ["", ""]
        rows.append(["person", "Repeat-choice consistency", "consistency", c["rate"], ci[0], ci[1], c["scored_pairs"], level])
    return header, rows
