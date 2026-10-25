"""Preference-model endpoints: readiness, training, history, prediction, ranking.

All labels here are SYNTHETIC TEST FIXTURE LABELS produced by a rule (for example
"always choose the design with more park access"), never real user choices.
"""

from __future__ import annotations

import copy
import json
import sqlite3
from itertools import combinations
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

import payloads
from app.factory import create_app
from app.storage.db import MIGRATION_1

API = "/api/v1"

GRID_NO_PARK = [
    "RRRRRR",
    "RhhvhR",
    "RhvchR",
    "RRRRRR",
    "RhhvCR",
    "RRRRRR",
]

# Each design is a list of (cell, land use, floors) edits on the 6x6 test grid.
DESIGNS = [
    [("r01_c03", "park", 0)],
    [("r04_c03", "park", 0)],
    [("r01_c03", "park", 0), ("r04_c03", "park", 0)],
    [("r01_c01", "residential", 4)],
    [("r01_c01", "residential", 5), ("r04_c01", "residential", 4)],
    [("r04_c03", "park", 0), ("r02_c04", "residential", 6)],
    [("r02_c03", "park", 0)],
    [("r01_c02", "residential", 4), ("r02_c01", "residential", 4), ("r04_c02", "residential", 4)],
    [("r01_c03", "park", 0), ("r01_c04", "residential", 5)],
    [("r01_c03", "residential", 3)],
    [("r02_c03", "park", 0), ("r04_c03", "park", 0)],
    [("r01_c01", "residential", 2), ("r01_c03", "park", 0)],
    [("r02_c03", "residential", 2)],
]
INFEASIBLE = [("r04_c02", "park", 0), ("r04_c01", "park", 0)]   # loses too much housing


def build(baseline: dict, edits: list[tuple[str, str, int]]) -> dict:
    state = copy.deepcopy(baseline)
    for cell_id, use, floors in edits:
        cell = next(c for c in state["cells"] if c["cell_id"] == cell_id)
        cell["land_use"] = use
        cell["floors"] = floors
        cell["units_per_floor"] = 4 if use == "residential" else 0
    return payloads.rehash(state)


def register(client: TestClient, scenario_id: str, seed: int, grid=None):
    baseline = payloads.city(scenario_id=scenario_id, seed=seed, grid=grid)
    assert client.post(f"{API}/scenarios", json=payloads.scenario(baseline)).status_code == 201
    sess = payloads.session(baseline)
    assert client.post(f"{API}/sessions", json=sess).status_code == 201
    return baseline, sess


def save_design(client: TestClient, baseline: dict, sess: dict, edits) -> dict:
    alt = payloads.alternative(build(baseline, edits), baseline, sess["session_id"])
    response = client.post(f"{API}/alternatives", json=alt)
    assert response.status_code == 201, response.text
    return alt


def choose(client: TestClient, a: dict, b: dict, choice: str, mode: str = "train") -> dict:
    pref = payloads.preference(a, b, choice=choice, mode=mode)
    response = client.post(f"{API}/preferences", json=pref)
    assert response.status_code == 201, response.text
    return pref


def favour(feature: str):
    """A synthetic test chooser that always picks the design with the higher value of one feature."""
    def rule(a: dict, b: dict) -> str | None:
        diff = a["features"][feature] - b["features"][feature]
        return None if abs(diff) < 1e-9 else ("A" if diff > 0 else "B")
    return rule


def record_scenario(client, scenario_id, seed, chooser, grid=None, pairs_per_scenario=6):
    baseline, sess = register(client, scenario_id, seed, grid)
    alts = [save_design(client, baseline, sess, d) for d in DESIGNS]
    feasible = [a for a in alts if a["feasible"]]
    made = []
    for a, b in combinations(feasible, 2):
        if len(made) >= pairs_per_scenario:
            break
        choice = chooser(a, b)
        if choice is not None:
            made.append((choose(client, a, b, choice), a, b))
    return baseline, sess, alts, made


@pytest.fixture
def trained_dataset(client):
    """Three scenarios, 18 comparisons labelled by the access-favouring test rule."""
    rule = favour("delta_access_fraction")
    out = [record_scenario(client, f"scn-{i}", i, rule, grid=GRID_NO_PARK if i == 2 else None) for i in range(3)]
    return out


def _error(response, status, code):
    assert response.status_code == status, response.text
    assert response.json()["error"]["code"] == code, response.json()
    return response.json()["error"]


def train(client) -> dict:
    response = client.post(f"{API}/models/train")
    assert response.status_code in (200, 201), response.text
    return response.json()


# ---------------------------------------------------------------- no model / refusals

def test_without_data_everything_refuses_gracefully_and_honestly(client):
    ready = client.get(f"{API}/models/readiness").json()
    assert ready["can_train"] is False and ready["code"] == "not_enough_comparisons"
    assert ready["message"].startswith("Training samples: 0 unique direct comparisons across 0 scenarios.")
    err = _error(client.post(f"{API}/models/train"), 422, "not_enough_comparisons")
    assert err["details"]["eligible_comparisons"] == 0
    current = client.get(f"{API}/models/current").json()
    assert current["has_model"] is False and current["model"] is None
    assert client.get(f"{API}/models/history").json() == {"count": 0, "models": []}
    _error(client.post(f"{API}/models/predict-pair", json={"scenario_id": "x", "alternative_a_id": "a",
                                                            "alternative_b_id": "b"}), 409, "no_model")
    health = client.get(f"{API}/health").json()
    assert health["model_status"] == "no_model" and health["active_model_version"] == ""


def test_homogeneous_comparisons_are_refused(client):
    # The same trade-off (a park versus one extra floor) offered again and again: one direction only.
    rule = favour("delta_housing_fraction")
    for s in range(2):
        baseline, sess = register(client, f"homog-{s}", s)
        for _ in range(7):
            park = save_design(client, baseline, sess, [("r01_c03", "park", 0)])
            floor = save_design(client, baseline, sess, [("r01_c01", "residential", 4)])
            choose(client, floor, park, rule(floor, park))
    ready = client.get(f"{API}/models/readiness").json()
    assert ready["eligible_comparisons"] == 14 and ready["rank"] == 1
    err = _error(client.post(f"{API}/models/train"), 422, "homogeneous_comparisons")
    assert "too similar" in err["message"]
    assert not (client.app.state.data_dir / "models").exists()


def test_ties_skips_and_infeasible_choices_are_not_training_data(client, seeded):
    # Evaluation-mode choices are also excluded; they need a blinded trial (see test_evaluation_api.py).
    baseline, sess = seeded
    a = save_design(client, baseline, sess, DESIGNS[0])
    b = save_design(client, baseline, sess, DESIGNS[1])
    c = save_design(client, baseline, sess, DESIGNS[3])
    bad = save_design(client, baseline, sess, INFEASIBLE)
    assert bad["feasible"] is False
    choose(client, a, b, "tie")
    choose(client, a, c, "skip")
    choose(client, a, bad, "A")
    ready = client.get(f"{API}/models/readiness").json()
    assert ready["eligible_comparisons"] == 0
    assert ready["excluded"] == {"all_comparisons": 3, "ties_and_skips": 2, "evaluation_mode": 0, "infeasible": 1}
    assert ready["evaluation_open"] is False


# ---------------------------------------------------------------- training

def test_training_learns_the_direction_the_fixture_rule_favours(client, trained_dataset):
    body = train(client)
    assert body["status"] == "created"
    model = body["model"]
    assert model["unique_comparisons"] == 18 and model["scenario_count"] == 3 and model["augmented_rows"] == 36
    coef = {c["feature"]: c for c in model["coefficients"]}
    assert coef["delta_access_fraction"]["weight_raw"] > 0
    assert coef["delta_access_fraction"]["direction"] == "positive"
    assert len(model["coefficients"]) == 5
    assert model["diagnostics"]["available"] and model["diagnostics"]["model"]["n"] == 18
    assert {b["name"] for b in model["diagnostics"]["baselines"]} == {"chance", "cheaper", "more_access", "equal_weight"}
    assert any("associations" in c for c in model["caveats"])
    assert model["package_versions"]["scikit-learn"]
    health = client.get(f"{API}/health").json()
    assert health["model_status"] == "trained" and health["active_model_version"] == model["model_version"]


def test_retraining_on_unchanged_data_creates_no_new_version(client, trained_dataset):
    first = train(client)["model"]
    response = client.post(f"{API}/models/train")
    assert response.status_code == 200 and response.json()["status"] == "unchanged"
    assert response.json()["model"]["model_version"] == first["model_version"]
    assert client.get(f"{API}/models/history").json()["count"] == 1


def test_new_comparisons_create_a_new_version_and_supersede_the_old(client, trained_dataset):
    first = train(client)["model"]
    baseline, sess, alts, _ = trained_dataset[0]
    feasible = [a for a in alts if a["feasible"]]
    choose(client, feasible[-1], feasible[-3], favour("delta_access_fraction")(feasible[-1], feasible[-3]) or "A")
    second = train(client)["model"]
    assert second["model_version"] != first["model_version"]
    history = client.get(f"{API}/models/history").json()
    assert [m["status"] for m in history["models"]] == ["active", "superseded"]
    assert client.get(f"{API}/models/current").json()["model"]["model_version"] == second["model_version"]


def test_model_file_is_checksummed_json_and_survives_restart(client, trained_dataset, data_dir):
    model = train(client)["model"]
    path = data_dir / model["artifact_path"]
    artifact = json.loads(path.read_text(encoding="utf-8"))
    assert artifact["format"] == "plannerdna-pairwise-model" and len(artifact["weights_raw"]) == 5
    assert artifact["training"]["comparison_ids"] and artifact["random_seed"] == model["random_seed"]

    _, _, alts, made = trained_dataset[1]
    request = {"scenario_id": "scn-1", "alternative_a_id": made[0][1]["alternative_id"],
               "alternative_b_id": made[0][2]["alternative_id"]}
    before = client.post(f"{API}/models/predict-pair", json=request).json()["p_a"]
    restarted = TestClient(create_app(data_dir))
    after = restarted.post(f"{API}/models/predict-pair", json=request).json()["p_a"]
    assert before == after


def test_tampered_model_file_is_refused(client, trained_dataset, data_dir):
    model = train(client)["model"]
    path = data_dir / model["artifact_path"]
    artifact = json.loads(path.read_text(encoding="utf-8"))
    artifact["weights_raw"][0] = 1000.0
    path.write_text(json.dumps(artifact), encoding="utf-8")
    _, _, _, made = trained_dataset[0]
    _error(client.post(f"{API}/models/predict-pair", json={
        "scenario_id": "scn-0", "alternative_a_id": made[0][1]["alternative_id"],
        "alternative_b_id": made[0][2]["alternative_id"]}), 409, "model_artifact_invalid")


# ---------------------------------------------------------------- prediction

def test_pair_prediction_is_symmetric_and_uses_the_saved_model(client, trained_dataset):
    model = train(client)["model"]
    _, _, alts, made = trained_dataset[0]
    pref, a, b = made[0]
    ab = client.post(f"{API}/models/predict-pair", json={
        "scenario_id": "scn-0", "alternative_a_id": a["alternative_id"], "alternative_b_id": b["alternative_id"],
        "comparison_id": pref["comparison_id"]}).json()
    ba = client.post(f"{API}/models/predict-pair", json={
        "scenario_id": "scn-0", "alternative_a_id": b["alternative_id"], "alternative_b_id": a["alternative_id"]}).json()
    assert ab["p_a"] + ab["p_b"] == pytest.approx(1.0, abs=1e-12)
    assert ab["p_a"] + ba["p_a"] == pytest.approx(1.0, abs=1e-12)
    assert ab["model_version"] == model["model_version"]
    assert ab["comparison_in_training_set"] is True and "not a test" in ab["explanation"]
    assert len(ab["contributions"]) == 5
    total = sum(c["contribution"] for c in ab["contributions"])
    assert ab["utility_a"] - ab["utility_b"] == pytest.approx(total)
    export = client.get(f"{API}/export").json()
    assert export["export_manifest"]["counts"]["predictions"] == 2
    logged = {p["comparison_id"]: p for p in export["predictions"]}
    assert logged[pref["comparison_id"]]["revealed_after_choice"] == 1


def test_prediction_for_unseen_designs_favours_more_access(client, trained_dataset):
    train(client)
    baseline, sess = register(client, "unseen", 99)
    more = save_design(client, baseline, sess, [("r01_c03", "park", 0), ("r04_c03", "park", 0)])
    less = save_design(client, baseline, sess, [("r01_c01", "residential", 4)])
    assert more["features"]["delta_access_fraction"] > less["features"]["delta_access_fraction"]
    result = client.post(f"{API}/models/predict-pair", json={
        "scenario_id": "unseen", "alternative_a_id": more["alternative_id"], "alternative_b_id": less["alternative_id"]}).json()
    assert result["p_a"] > 0.5 and result["favoured"] == "A"
    assert result["scenario_in_training_set"] is False and result["comparison_in_training_set"] is False


def test_prediction_refuses_bad_requests(client, trained_dataset):
    train(client)
    _, _, alts, made = trained_dataset[0]
    other = trained_dataset[1][2][0]
    a, b = made[0][1], made[0][2]
    req = {"scenario_id": "scn-0", "alternative_a_id": a["alternative_id"], "alternative_b_id": b["alternative_id"]}
    _error(client.post(f"{API}/models/predict-pair", json=dict(req, alternative_b_id=a["alternative_id"])), 422, "self_comparison")
    _error(client.post(f"{API}/models/predict-pair", json=dict(req, alternative_b_id=other["alternative_id"])), 422, "scenario_mismatch")
    _error(client.post(f"{API}/models/predict-pair", json=dict(req, alternative_b_id="alt-missing")), 422, "unknown_alternative")
    _error(client.post(f"{API}/models/predict-pair", json=dict(req, comparison_id="cmp-missing")), 422, "unknown_comparison")
    _error(client.post(f"{API}/models/predict-pair", json=dict(req, model_version="pm-nope")), 422, "unknown_model")
    _error(client.post(f"{API}/models/predict-pair", json=dict(req, extra=1)), 422, "validation_error")


# ---------------------------------------------------------------- ranking

def candidate(baseline: dict, edits, cid: str, **override) -> dict:
    alt = payloads.alternative(build(baseline, edits), baseline, "unused")
    body = {"candidate_id": cid, "description": f"test candidate {cid}", "state_hash": alt["state_hash"],
            "feasible": alt["feasible"], "changed_cell_ids": alt["changed_cell_ids"], "snapshot": alt["snapshot"],
            "features": alt["features"]}
    body.update(override)
    return body


def rank_request(baseline: dict, candidates: list[dict], top_k: int = 5) -> dict:
    return {"schema_version": 1, "scenario_id": baseline["scenario_id"], "generator_version": "candidates-test",
            "generator_seed": 7, "top_k": top_k, "candidates": candidates}


def test_ranking_orders_feasible_candidates_by_fitted_utility(client, trained_dataset):
    model = train(client)["model"]
    baseline, _ = register(client, "rank-scn", 50)
    cands = [candidate(baseline, d, f"c{i}") for i, d in enumerate(DESIGNS)]
    response = client.post(f"{API}/models/rank-candidates", json=rank_request(baseline, cands, top_k=20))
    assert response.status_code == 200, response.text
    body = response.json()
    utilities = [s["utility"] for s in body["suggestions"]]
    assert utilities == sorted(utilities, reverse=True)
    assert body["model_version"] == model["model_version"] and "not optimal" in body["banner"]
    assert body["received"] == len(DESIGNS) and body["returned"] + body["skipped_near_duplicates"] <= body["ranked"]
    top = body["suggestions"][0]
    assert top["rank"] == 1 and top["has_next"] is (len(body["suggestions"]) > 1)
    assert top["features"]["delta_access_fraction"] == max(s["features"]["delta_access_fraction"] for s in body["suggestions"])
    assert body["suggestions"][-1]["has_next"] is False
    assert client.get(f"{API}/export").json()["export_manifest"]["counts"]["predictions"] == body["returned"]


def test_ranking_skips_near_duplicate_outcomes(client, trained_dataset):
    train(client)
    baseline, _ = register(client, "dup-scn", 51)
    twin_a = candidate(baseline, [("r01_c01", "residential", 4)], "twin-a")
    twin_b = candidate(baseline, [("r01_c02", "residential", 4)], "twin-b")
    assert twin_a["features"] == twin_b["features"] and twin_a["state_hash"] != twin_b["state_hash"]
    body = client.post(f"{API}/models/rank-candidates", json=rank_request(baseline, [twin_a, twin_b])).json()
    assert body["returned"] == 1 and body["skipped_near_duplicates"] == 1
    assert body["suggestions"][0]["near_duplicates_skipped"] == 1


def test_ranking_refuses_infeasible_tampered_duplicate_or_unchanged_candidates(client, trained_dataset):
    train(client)
    baseline, _ = register(client, "bad-scn", 52)
    good = candidate(baseline, DESIGNS[0], "good")
    other = candidate(baseline, DESIGNS[1], "other")
    infeasible = candidate(baseline, INFEASIBLE, "bad")
    assert infeasible["feasible"] is False
    _error(client.post(f"{API}/models/rank-candidates", json=rank_request(baseline, [good, infeasible])), 422, "infeasible_candidate")
    lying = dict(infeasible, feasible=True)
    _error(client.post(f"{API}/models/rank-candidates", json=rank_request(baseline, [good, lying])), 422, "metrics_mismatch")
    tampered = dict(other, features=dict(other["features"], delta_access_fraction=0.9))
    _error(client.post(f"{API}/models/rank-candidates", json=rank_request(baseline, [good, tampered])), 422, "metrics_mismatch")
    wrong_version = dict(other, features=dict(other["features"], feature_version="features-v0"))
    _error(client.post(f"{API}/models/rank-candidates", json=rank_request(baseline, [good, wrong_version])), 422, "unsupported_version")
    copy_of_good = dict(good, candidate_id="copy")
    _error(client.post(f"{API}/models/rank-candidates", json=rank_request(baseline, [good, copy_of_good])), 422, "duplicate_candidate")
    unchanged = candidate(baseline, [], "same")
    _error(client.post(f"{API}/models/rank-candidates", json=rank_request(baseline, [good, unchanged])), 422, "no_change_candidate")
    _error(client.post(f"{API}/models/rank-candidates", json=rank_request(baseline, [good])), 422, "validation_error")
    unknown = dict(rank_request(baseline, [good, other]), scenario_id="never-registered")
    _error(client.post(f"{API}/models/rank-candidates", json=unknown), 422, "unknown_scenario")


UNITY_FIXTURE = Path(__file__).resolve().parent / "fixtures" / "unity_metrics_fixtures.json"


@pytest.mark.skipif(not UNITY_FIXTURE.exists(), reason="Run PlannerDNA > Export Backend Contract Fixtures in Unity first.")
def test_unity_generated_candidates_are_accepted_and_ranked(client, trained_dataset):
    """Candidates built by Unity's CandidateGenerator pass Python's recomputation unchanged."""
    model = train(client)["model"]
    cases = json.loads(UNITY_FIXTURE.read_text(encoding="utf-8"))["cases"]
    for case in cases:
        request = case["rank_request"]
        assert request["generator_version"] == "candidates-1.0.0" and len(request["candidates"]) >= 2
        assert all(c["feasible"] for c in request["candidates"])
        assert client.post(f"{API}/scenarios", json=case["scenario"]).status_code == 201
        response = client.post(f"{API}/models/rank-candidates", json=request)
        assert response.status_code == 200, (case["seed"], response.text)
        body = response.json()
        assert body["model_version"] == model["model_version"]
        assert body["received"] == body["ranked"] == len(request["candidates"])
        assert 1 <= body["returned"] <= request["top_k"]
        utilities = [s["utility"] for s in body["suggestions"]]
        assert utilities == sorted(utilities, reverse=True)


# ---------------------------------------------------------------- deletion and migration

def test_delete_removes_models_and_predictions(client, trained_dataset, data_dir):
    model = train(client)["model"]
    assert (data_dir / model["artifact_path"]).exists()
    body = client.request("DELETE", f"{API}/data", json={"confirm": "DELETE ALL PLANNERDNA DATA", "backup": True}).json()
    assert body["deleted_rows"]["model_run"] == 1 and body["models_removed"] is True
    assert not (data_dir / "models").exists()
    backup = json.loads(open(body["backup_path"], encoding="utf-8").read())
    assert backup["export_manifest"]["counts"]["model_runs"] == 1
    health = client.get(f"{API}/health").json()
    assert health["model_status"] == "no_model"


def test_phase2_database_is_upgraded_in_place(data_dir):
    data_dir.mkdir(parents=True)
    path = data_dir / "plannerdna.db"
    conn = sqlite3.connect(path)
    conn.executescript("CREATE TABLE schema_migration (version INTEGER PRIMARY KEY, description TEXT NOT NULL, "
                       "applied_at_utc TEXT NOT NULL);" + MIGRATION_1 +
                       "INSERT INTO schema_migration VALUES (1, 'initial', '2026-10-01T00:00:00Z');")
    conn.close()
    client = TestClient(create_app(data_dir))
    health = client.get(f"{API}/health").json()
    assert health["db_schema_version"] == 3 and health["storage_status"] == "ready"
    with client.app.state.db.reader() as c:
        columns = {r[1] for r in c.execute("PRAGMA table_info(model_run)")}
    assert {"status", "artifact_sha256", "data_fingerprint", "summary_json"} <= columns
