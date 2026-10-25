"""Replays records produced by the real Unity code (see ContractFixtures.cs).

Acceptance by the API means the Python port reproduced Unity's content hashes, metrics, constraint
results, feasibility and features for every alternative, and that the JSON produced by Unity's
JsonUtility matches the request schemas exactly.
"""

import json
from pathlib import Path

import pytest

from app.domain.city import city_from_dict
from app.domain.metrics import Metrics, ScenarioRules, diff_values, evaluate

FIXTURE = Path(__file__).resolve().parent / "fixtures" / "unity_metrics_fixtures.json"
API = "/api/v1"

pytestmark = pytest.mark.skipif(not FIXTURE.exists(), reason="Run PlannerDNA > Export Backend Contract Fixtures in Unity first.")


def _cases():
    return json.loads(FIXTURE.read_text(encoding="utf-8"))["cases"]


def test_fixture_covers_feasible_and_infeasible_designs():
    alts = [a for case in _cases() for a in case["alternatives"]]
    assert any(a["feasible"] for a in alts)
    assert any(not a["feasible"] for a in alts)
    assert any(p["training_eligible"] for case in _cases() for p in case["preferences"])


def test_python_recomputes_unity_values_exactly():
    for case in _cases():
        scenario = case["scenario"]
        rules = ScenarioRules(**scenario["constraints"])
        baseline = city_from_dict(scenario["baseline"])
        assert baseline.content_hash() == scenario["baseline_hash"]
        for alt in case["alternatives"]:
            state = city_from_dict(alt["snapshot"])
            assert state.content_hash() == alt["state_hash"]
            computed = evaluate(state, baseline, rules).to_dict()
            assert diff_values(computed, alt["metrics"], Metrics.COMPARED_FIELDS) == [], (case["seed"], alt["alternative_id"])


def test_unity_records_are_accepted_by_the_api(client):
    for case in _cases():
        assert client.post(f"{API}/scenarios", json=case["scenario"]).status_code == 201
        assert client.post(f"{API}/sessions", json=case["session"]).status_code == 201
        for alt in case["alternatives"]:
            response = client.post(f"{API}/alternatives", json=alt)
            assert response.status_code == 201, (case["seed"], response.text)
        for pref in case["preferences"]:
            response = client.post(f"{API}/preferences", json=pref)
            assert response.status_code == 201, (case["seed"], response.text)
        for event in case["events"]:
            response = client.post(f"{API}/events", json=event)
            assert response.status_code == 201, (case["seed"], event["action"], response.text)

    counts = client.get(f"{API}/export").json()["export_manifest"]["counts"]
    assert counts["scenarios"] == len(_cases())
    assert counts["alternatives"] == sum(len(c["alternatives"]) for c in _cases())
    assert counts["comparisons"] == sum(len(c["preferences"]) for c in _cases())
    assert counts["events"] == sum(len(c["events"]) for c in _cases())
