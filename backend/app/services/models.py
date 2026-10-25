"""Training, history, prediction and candidate ranking for the personal preference model.

Training reads only training-eligible comparisons (explicit A/B choices between two
feasible designs, train mode) and uses the features the server itself recomputed
when each alternative was stored. Predictions always load the saved model file
(checksum verified), never an in-memory or hard-coded weight vector.
"""

from __future__ import annotations

import hashlib
import json
import platform
import sqlite3
import uuid
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import numpy as np
import scipy
import sklearn
from scipy.special import expit

from app.domain.metrics import FEATURE_VERSION
from app.errors import ApiError
from app.ml import pairwise as pw
from app.ml.artifact import (
    ARTIFACT_FORMAT,
    ARTIFACT_FORMAT_VERSION,
    ModelArtifactError,
    read_artifact,
    remove_artifact,
    write_artifact,
)
from app.schemas.models import PredictPairIn, RankCandidatesIn
from app.services import evaluation_rules
from app.services.records import _load_scenario, canonical_json, feature_problems, recompute_design
from app.storage.db import Database, utc_now

HYPERPARAMETERS = pw.Hyperparameters()
THRESHOLDS = pw.Thresholds()
# Two candidates whose every feature differs by at most half a percentage point are near duplicates.
NEAR_DUPLICATE_TOLERANCE = 0.005

CAVEATS = [
    "Weights are statistical associations between measured outcome differences and your explicit A/B choices. "
    "They are not your motives, values or expertise, and they may change as you record more choices.",
    "All outcomes are synthetic proxies computed by this sandbox; the model knows nothing about real places or people.",
    "Diagnostics are cross-validated inside the training data (leave one scenario out). They are not a blinded, "
    "pre-registered held-out evaluation.",
    "Each comparison is also used reversed so the model is symmetric; reversed copies are not extra evidence and every "
    "count shown is of unique comparisons.",
]
SUGGESTION_BANNER = ("Synthetic scenario. Suggestions are feasible designs from a seeded rule-based search, ordered by your "
                     "fitted preference model {version}. They are not optimal plans and not professional advice.")


# ---------------------------------------------------------------- data

def load_training_data(conn: sqlite3.Connection) -> pw.PairwiseData:
    rows = conn.execute(
        """SELECT c.comparison_id, c.scenario_id, c.choice, a.features_json AS fa, b.features_json AS fb
           FROM comparison c
           JOIN alternative a ON a.id = c.alt_a_id
           JOIN alternative b ON b.id = c.alt_b_id
           WHERE c.training_eligible = 1 AND c.mode = 'train' AND c.choice IN ('A', 'B')
             AND c.feature_version = ? AND a.feature_version = ? AND b.feature_version = ?
           ORDER BY c.comparison_id""",
        (FEATURE_VERSION, FEATURE_VERSION, FEATURE_VERSION),
    ).fetchall()
    diffs = [pw.feature_vector(json.loads(r["fa"])) - pw.feature_vector(json.loads(r["fb"])) for r in rows]
    return pw.PairwiseData(
        comparison_ids=[r["comparison_id"] for r in rows],
        scenario_ids=[r["scenario_id"] for r in rows],
        diffs=np.array(diffs).reshape(-1, len(pw.FEATURE_NAMES)),
        chose_a=np.array([1 if r["choice"] == "A" else 0 for r in rows], dtype=int),
    )


def excluded_counts(conn: sqlite3.Connection) -> dict[str, int]:
    row = conn.execute(
        """SELECT COUNT(*) AS all_comparisons,
                  COALESCE(SUM(choice IN ('tie', 'skip')), 0) AS ties_and_skips,
                  COALESCE(SUM(mode = 'evaluation'), 0) AS evaluation_mode,
                  COALESCE(SUM(mode = 'train' AND choice IN ('A', 'B') AND training_eligible = 0), 0) AS infeasible
           FROM comparison"""
    ).fetchone()
    return {k: int(row[k]) for k in row.keys()}


def _active_row(conn: sqlite3.Connection) -> sqlite3.Row | None:
    return conn.execute("SELECT * FROM model_run WHERE status = 'active'").fetchone()


def active_model_version(conn: sqlite3.Connection) -> str:
    row = _active_row(conn)
    return row["id"] if row is not None else ""


def readiness(conn: sqlite3.Connection) -> dict[str, Any]:
    result = pw.assess(load_training_data(conn), THRESHOLDS).to_dict()
    result["excluded"] = excluded_counts(conn)
    result["active_model_version"] = active_model_version(conn)
    plan = evaluation_rules.open_plan(conn)
    result["evaluation_open"] = plan is not None
    if plan is not None:
        message = (f"Blinded evaluation {plan['id']} is open, so training is paused until it is closed "
                   f"(the evaluated model {plan['model_run_id']} must not change).")
        result["reasons"].insert(0, {"code": "evaluation_in_progress", "message": message})
        result["can_train"] = False
        result["code"] = "evaluation_in_progress"
    return result


def data_fingerprint(data: pw.PairwiseData) -> str:
    rows = sorted(zip(data.comparison_ids, data.scenario_ids, data.chose_a.tolist(), data.diffs.tolist()))
    payload = {"ml_version": pw.ML_VERSION, "feature_version": FEATURE_VERSION,
               "hyperparameters": HYPERPARAMETERS.to_dict(), "rows": rows}
    return hashlib.sha256(canonical_json(payload).encode("utf-8")).hexdigest()


def package_versions() -> dict[str, str]:
    return {"python": platform.python_version(), "numpy": np.__version__, "scipy": scipy.__version__,
            "scikit-learn": sklearn.__version__}


# ---------------------------------------------------------------- training

def train(db: Database, data_dir: Path) -> tuple[str, dict[str, Any]]:
    """Returns ('created' | 'unchanged', model summary). Refuses with a structured 422 when data is inadequate."""
    with db.reader() as conn:
        evaluation_rules.refuse_training_while_open(conn)
        all_data = load_training_data(conn)
        ready = pw.assess(all_data, THRESHOLDS)
        if not ready.can_train:
            details = ready.to_dict()
            details["excluded"] = excluded_counts(conn)
            raise ApiError(422, ready.code, ready.message, details)
        data = all_data.subset(all_data.informative)
        fingerprint = data_fingerprint(data)
        existing = conn.execute("SELECT * FROM model_run WHERE data_fingerprint = ?", (fingerprint,)).fetchone()
        if existing is not None:
            return "unchanged", model_summary(existing)

    model = pw.fit(data, HYPERPARAMETERS)
    training_fit = pw.score(model.p_from_diffs(data.diffs), data.chose_a)
    diagnostics = pw.leave_one_scenario_out(data, HYPERPARAMETERS)
    bootstrap = pw.scenario_bootstrap(data, model.scale, HYPERPARAMETERS)
    coefficients = pw.coefficient_report(model, bootstrap, HYPERPARAMETERS)
    scenario_ids = sorted(set(data.scenario_ids))

    warnings = list(ready.warnings)
    if not model.converged:
        warnings.append(f"The optimiser stopped after {model.iterations} iterations without converging; weights are approximate.")
    if training_fit["accuracy"] == 1.0:
        warnings.append("The weights reproduce every training choice (perfectly separable data). Their size is limited only "
                        "by regularisation, so read them as directions rather than strengths.")
    if len(scenario_ids) < THRESHOLDS.suggested_scenarios:
        warnings.append(f"Uncertainty intervals come from resampling only {len(scenario_ids)} scenarios and are rough.")

    now = datetime.now(timezone.utc)
    trained_utc = now.isoformat()
    version = f"pm-{now.strftime('%Y%m%dT%H%M%SZ')}-{fingerprint[:8]}"
    packages = package_versions()
    training = {
        "comparison_ids": list(data.comparison_ids),
        "scenario_ids": scenario_ids,
        "unique_comparisons": len(data),
        "augmented_rows": 2 * len(data),
        "choices_for_a": int(data.chose_a.sum()),
        "choices_for_b": int(len(data) - data.chose_a.sum()),
        "ignored_uninformative": len(all_data) - len(data),
    }
    artifact = {
        "format": ARTIFACT_FORMAT,
        "format_version": ARTIFACT_FORMAT_VERSION,
        "model_version": version,
        "model_kind": pw.MODEL_KIND,
        "ml_version": pw.ML_VERSION,
        "feature_version": FEATURE_VERSION,
        "feature_names": list(pw.FEATURE_NAMES),
        "weights_raw": model.weights.tolist(),
        "weights_standardised": model.weights_standardised.tolist(),
        "feature_scale": model.scale.tolist(),
        "hyperparameters": HYPERPARAMETERS.to_dict(),
        "random_seed": HYPERPARAMETERS.random_seed,
        "training": training,
        "data_fingerprint": fingerprint,
        "package_versions": packages,
        "trained_utc": trained_utc,
    }
    path, sha = write_artifact(data_dir, version, artifact)
    summary = {
        "model_version": version,
        "status": "active",
        "model_kind": pw.MODEL_KIND,
        "ml_version": pw.ML_VERSION,
        "feature_version": FEATURE_VERSION,
        "trained_utc": trained_utc,
        "unique_comparisons": len(data),
        "scenario_count": len(scenario_ids),
        "training_scenario_ids": scenario_ids,
        "choices_for_a": training["choices_for_a"],
        "choices_for_b": training["choices_for_b"],
        "augmented_rows": training["augmented_rows"],
        "ignored_uninformative": training["ignored_uninformative"],
        "summary": f"Trained on {len(data)} unique direct comparisons across {len(scenario_ids)} scenarios.",
        "coefficients": coefficients,
        "training_fit": training_fit,
        "diagnostics": diagnostics,
        "warnings": warnings,
        "caveats": CAVEATS,
        "hyperparameters": HYPERPARAMETERS.to_dict(),
        "random_seed": HYPERPARAMETERS.random_seed,
        "thresholds": THRESHOLDS.to_dict(),
        "package_versions": packages,
        "artifact_path": path,
        "artifact_sha256": sha,
        "data_fingerprint": fingerprint,
    }
    try:
        with db.transaction() as conn:
            evaluation_rules.refuse_training_while_open(conn)
            conn.execute("UPDATE model_run SET status = 'superseded' WHERE status = 'active'")
            conn.execute(
                """INSERT INTO model_run (id, trained_utc, model_kind, model_artifact_path, feature_version,
                       training_scenario_ids_json, unique_comparison_count, hyperparameters_json, evaluation_json,
                       random_seed, ml_version, status, artifact_sha256, data_fingerprint,
                       training_comparison_ids_json, package_versions_json, summary_json)
                   VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 'active', ?, ?, ?, ?, ?)""",
                (version, trained_utc, pw.MODEL_KIND, path, FEATURE_VERSION, canonical_json(scenario_ids), len(data),
                 canonical_json(HYPERPARAMETERS.to_dict()), canonical_json(diagnostics), HYPERPARAMETERS.random_seed,
                 pw.ML_VERSION, sha, fingerprint, canonical_json(training["comparison_ids"]), canonical_json(packages),
                 canonical_json(summary)),
            )
    except sqlite3.IntegrityError:
        # Another request trained on exactly the same data first; keep its model.
        remove_artifact(data_dir, path)
        with db.reader() as conn:
            existing = conn.execute("SELECT * FROM model_run WHERE data_fingerprint = ?", (fingerprint,)).fetchone()
        if existing is None:
            raise
        return "unchanged", model_summary(existing)
    return "created", summary


# ---------------------------------------------------------------- reads

def model_summary(row: sqlite3.Row) -> dict[str, Any]:
    summary = json.loads(row["summary_json"])
    summary["status"] = row["status"]
    return summary


def current(conn: sqlite3.Connection) -> dict[str, Any]:
    row = _active_row(conn)
    ready = readiness(conn)
    if row is None:
        return {"has_model": False, "message": "No model has been trained yet. " + ready["message"],
                "readiness": ready, "model": None}
    return {"has_model": True, "message": f"Active model {row['id']}.", "readiness": ready, "model": model_summary(row)}


def history(conn: sqlite3.Connection) -> dict[str, Any]:
    models = []
    for row in conn.execute("SELECT * FROM model_run ORDER BY trained_utc DESC, id DESC"):
        s = model_summary(row)
        diag = s.get("diagnostics", {})
        models.append({
            "model_version": s["model_version"], "status": s["status"], "trained_utc": s["trained_utc"],
            "unique_comparisons": s["unique_comparisons"], "scenario_count": s["scenario_count"],
            "diagnostic_accuracy": diag.get("model", {}).get("accuracy", 0.0),
            "diagnostic_n": diag.get("model", {}).get("n", 0),
            "diagnostic_available": bool(diag.get("available", False)),
            "ml_version": s["ml_version"], "feature_version": s["feature_version"],
            "warning_count": len(s.get("warnings", [])),
        })
    return {"count": len(models), "models": models}


# ---------------------------------------------------------------- prediction

def _resolve_model(conn: sqlite3.Connection, data_dir: Path, version: str) -> tuple[sqlite3.Row, dict[str, Any], np.ndarray]:
    if version:
        row = conn.execute("SELECT * FROM model_run WHERE id = ?", (version,)).fetchone()
        if row is None:
            raise ApiError(422, "unknown_model", f"Model '{version}' does not exist.")
    else:
        row = _active_row(conn)
        if row is None:
            raise ApiError(409, "no_model", "No model has been trained yet, so there is no prediction. "
                                           + readiness(conn)["message"])
    try:
        payload = read_artifact(data_dir, row["model_artifact_path"], row["artifact_sha256"])
    except ModelArtifactError as exc:
        raise ApiError(409, "model_artifact_invalid",
                       f"The saved model cannot be used: {exc} Record at least one more comparison and train again "
                       "(a new model file is written), or delete all data.") from exc
    return row, payload, np.array(payload["weights_raw"], dtype=float)


def _new_prediction_id() -> str:
    return "pred-" + uuid.uuid4().hex


def predict_pair(db: Database, data_dir: Path, body: PredictPairIn) -> dict[str, Any]:
    if body.alternative_a_id == body.alternative_b_id:
        raise ApiError(422, "self_comparison", "An alternative cannot be compared with itself.")
    with db.reader() as conn:
        row, payload, weights = _resolve_model(conn, data_dir, body.model_version)
        _load_scenario(conn, body.scenario_id)
        evaluation_rules.refuse_if_sealed(conn, body.scenario_id, "a model prediction")
        features = {}
        for label, alt_id in (("A", body.alternative_a_id), ("B", body.alternative_b_id)):
            alt = conn.execute("SELECT scenario_id, features_json FROM alternative WHERE id = ?", (alt_id,)).fetchone()
            if alt is None:
                raise ApiError(422, "unknown_alternative", f"Alternative {label} '{alt_id}' has not been saved on the server.")
            if alt["scenario_id"] != body.scenario_id:
                raise ApiError(422, "scenario_mismatch", f"Alternative {label} belongs to scenario '{alt['scenario_id']}'.")
            features[label] = pw.feature_vector(json.loads(alt["features_json"]))
        if body.comparison_id:
            cmp_row = conn.execute("SELECT alt_a_id, alt_b_id FROM comparison WHERE comparison_id = ?",
                                   (body.comparison_id,)).fetchone()
            if cmp_row is None:
                raise ApiError(422, "unknown_comparison", f"Comparison '{body.comparison_id}' is not stored on the server.")
            if {cmp_row["alt_a_id"], cmp_row["alt_b_id"]} != {body.alternative_a_id, body.alternative_b_id}:
                raise ApiError(422, "comparison_mismatch", "The comparison is about a different pair of alternatives.")

    fa, fb = features["A"], features["B"]
    p_a = float(expit(np.dot(weights, fa - fb)))
    p_b = float(expit(np.dot(weights, fb - fa)))
    training = payload["training"]
    in_training = bool(body.comparison_id) and body.comparison_id in training["comparison_ids"]
    scenario_in_training = body.scenario_id in training["scenario_ids"]
    favoured = "neither" if abs(p_a - 0.5) < 0.05 else ("A" if p_a > 0.5 else "B")
    prediction_id = _new_prediction_id()
    with db.transaction() as conn:
        conn.execute(
            """INSERT INTO prediction (id, model_run_id, scenario_id, comparison_id, candidate_features_json,
                   predicted_probability, revealed_after_choice, created_utc, prediction_kind, in_training_set)
               VALUES (?, ?, ?, ?, ?, ?, ?, ?, 'pair', ?)""",
            (prediction_id, row["id"], body.scenario_id, body.comparison_id or None,
             canonical_json({"alternative_a_id": body.alternative_a_id, "alternative_b_id": body.alternative_b_id,
                             "features_a": fa.tolist(), "features_b": fb.tolist()}),
             p_a, int(bool(body.comparison_id)), utc_now(), int(in_training)),
        )
    explanation = (f"Model {row['id']} gives A a {p_a * 100:.0f}% and B a {p_b * 100:.0f}% probability of being preferred, "
                   "based only on the differences in the five measured outcomes (see contributions).")
    if in_training:
        explanation += " This comparison was part of the model's training data, so this is not a test of the model."
    return {
        "prediction_id": prediction_id,
        "model_version": row["id"],
        "trained_utc": row["trained_utc"],
        "scenario_id": body.scenario_id,
        "alternative_a_id": body.alternative_a_id,
        "alternative_b_id": body.alternative_b_id,
        "p_a": p_a,
        "p_b": p_b,
        "utility_a": float(np.dot(weights, fa)),
        "utility_b": float(np.dot(weights, fb)),
        "favoured": favoured,
        "contributions": pw.contributions(weights, fa, fb),
        "comparison_id": body.comparison_id,
        "comparison_in_training_set": in_training,
        "scenario_in_training_set": scenario_in_training,
        "explanation": explanation,
        "caveat": CAVEATS[0],
    }


# ---------------------------------------------------------------- ranking

def rank_candidates(db: Database, data_dir: Path, body: RankCandidatesIn) -> dict[str, Any]:
    with db.reader() as conn:
        row, payload, weights = _resolve_model(conn, data_dir, body.model_version)
        scenario = _load_scenario(conn, body.scenario_id)
        evaluation_rules.refuse_if_sealed(conn, body.scenario_id, "model-ranked suggestion")

    ids: set[str] = set()
    hashes: dict[str, str] = {}
    valid = []
    for c in body.candidates:
        what = f"Candidate {c.candidate_id}"
        if c.candidate_id in ids:
            raise ApiError(422, "duplicate_candidate", f"{what} appears more than once.")
        if c.state_hash in hashes:
            raise ApiError(422, "duplicate_candidate", f"{what} is the same design as candidate {hashes[c.state_hash]}.")
        ids.add(c.candidate_id)
        hashes[c.state_hash] = c.candidate_id
        check = recompute_design(scenario, c.snapshot, c.state_hash, what)
        problems = feature_problems(check, c.features)
        if c.feasible != check.feasible:
            problems.append(f"feasible: client sent {c.feasible}, server computed {check.feasible}")
        if c.changed_cell_ids != check.changed:
            problems.append(f"changed_cell_ids: client sent {len(c.changed_cell_ids)}, server computed {len(check.changed)}")
        if problems:
            raise ApiError(422, "metrics_mismatch", f"{what}: reported values differ from the server's recomputation.", problems)
        if not check.changed:
            raise ApiError(422, "no_change_candidate", f"{what} is identical to the baseline.")
        if not check.feasible:
            raise ApiError(422, "infeasible_candidate", f"{what} violates hard constraints; only feasible candidates are ranked.",
                           check.failed)
        valid.append((c, check, pw.feature_vector(check.features)))

    utilities = [float(np.dot(weights, vec)) for _, _, vec in valid]
    order = sorted(range(len(valid)), key=lambda i: (-utilities[i], valid[i][0].candidate_id))
    selected: list[dict[str, Any]] = []
    skipped = 0
    for i in order:
        if len(selected) >= body.top_k:
            break
        c, check, vec = valid[i]
        twin = next((s for s in selected if np.max(np.abs(vec - s["_vec"])) <= NEAR_DUPLICATE_TOLERANCE), None)
        if twin is not None:
            twin["near_duplicates_skipped"] += 1
            skipped += 1
            continue
        m = check.metrics
        selected.append({
            "_vec": vec,
            "rank": len(selected) + 1,
            "candidate_id": c.candidate_id,
            "description": c.description,
            "utility": utilities[i],
            "state_hash": c.state_hash,
            "changed_cell_ids": check.changed,
            "access_pct": m.access_pct if m.access_defined else 0.0,
            "access_defined": m.access_defined,
            "housing_units": m.housing_units,
            "park_share_pct": m.park_share_pct,
            "spent_credits": m.spent_credits,
            "lost_original_housing_units": m.lost_original_housing_units,
            "features": {name: float(vec[k]) for k, name in enumerate(pw.FEATURE_NAMES)},
            "contributions": [{"feature": name, "label": pw.FEATURE_LABELS[name], "value": float(vec[k]),
                               "weight_raw": float(weights[k]), "contribution": float(weights[k] * vec[k])}
                              for k, name in enumerate(pw.FEATURE_NAMES)],
            "near_duplicates_skipped": 0,
        })
    for k, s in enumerate(selected):
        nxt = selected[k + 1] if k + 1 < len(selected) else None
        s["has_next"] = nxt is not None
        s["p_over_next"] = float(expit(s["utility"] - nxt["utility"])) if nxt else 0.0

    created = utc_now()
    with db.transaction() as conn:
        for s in selected:
            conn.execute(
                """INSERT INTO prediction (id, model_run_id, scenario_id, comparison_id, candidate_features_json,
                       predicted_probability, revealed_after_choice, created_utc, prediction_kind, in_training_set)
                   VALUES (?, ?, ?, NULL, ?, ?, 0, ?, 'rank', 0)""",
                (_new_prediction_id(), row["id"], body.scenario_id,
                 canonical_json({"candidate_id": s["candidate_id"], "rank": s["rank"], "utility": s["utility"],
                                 "state_hash": s["state_hash"], "generator_version": body.generator_version,
                                 "generator_seed": body.generator_seed, "features": s["_vec"].tolist()}),
                 float(expit(s["utility"])), created),
            )
    for s in selected:
        del s["_vec"]
    return {
        "model_version": row["id"],
        "trained_utc": row["trained_utc"],
        "scenario_id": body.scenario_id,
        "scenario_in_training_set": body.scenario_id in payload["training"]["scenario_ids"],
        "generator_version": body.generator_version,
        "generator_seed": body.generator_seed,
        "received": len(body.candidates),
        "ranked": len(valid),
        "skipped_near_duplicates": skipped,
        "returned": len(selected),
        "suggestions": selected,
        "banner": SUGGESTION_BANNER.format(version=row["id"]),
        "caveat": CAVEATS[0],
    }
