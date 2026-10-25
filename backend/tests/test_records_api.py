import copy
import csv
import io
import json

import payloads
from app.domain.city import city_from_dict
from app.domain.metrics import Metrics, ScenarioRules, diff_values, evaluate
from app.factory import create_app
from fastapi.testclient import TestClient

API = "/api/v1"


def _two_alternatives(client, baseline, sess):
    a = payloads.alternative(payloads.edit(baseline, "r01_c03", "park"), baseline, sess["session_id"], "A")
    b = payloads.alternative(payloads.edit(baseline, "r04_c03", "park"), baseline, sess["session_id"], "B")
    assert client.post(f"{API}/alternatives", json=a).status_code == 201
    assert client.post(f"{API}/alternatives", json=b).status_code == 201
    return a, b


def _error(response, status, code):
    assert response.status_code == status, response.text
    body = response.json()
    assert body["error"]["code"] == code, body
    assert body["error"]["message"]
    return body["error"]


# ---------------------------------------------------------------- idempotency

def test_full_flow_then_exact_retries_create_no_duplicates(client, seeded):
    baseline, sess = seeded
    a, b = _two_alternatives(client, baseline, sess)
    ev = payloads.event(sess, 1, baseline)
    pref = payloads.preference(a, b, "B")
    assert client.post(f"{API}/events", json=ev).status_code == 201
    assert client.post(f"{API}/preferences", json=pref).status_code == 201

    for path, body in (("scenarios", payloads.scenario(baseline)), ("sessions", sess), ("events", ev),
                       ("alternatives", a), ("alternatives", b), ("preferences", pref)):
        response = client.post(f"{API}/{path}", json=body)
        assert response.status_code == 200, (path, response.text)
        assert response.json()["status"] == "duplicate"

    manifest = client.get(f"{API}/export").json()["export_manifest"]["counts"]
    assert manifest == {"scenarios": 1, "sessions": 1, "events": 1, "alternatives": 2, "comparisons": 1,
                        "model_runs": 0, "predictions": 0, "evaluation_plans": 0, "evaluation_trials": 0}


def test_same_id_with_different_content_is_refused_and_original_kept(client, seeded):
    baseline, sess = seeded
    a, b = _two_alternatives(client, baseline, sess)
    pref = payloads.preference(a, b, "A")
    assert client.post(f"{API}/preferences", json=pref).status_code == 201
    changed = dict(pref, choice="B", training_eligible=True)
    _error(client.post(f"{API}/preferences", json=changed), 409, "id_conflict")
    assert client.get(f"{API}/export").json()["comparisons"][0]["choice"] == "A"


def test_reregistering_scenario_later_is_a_duplicate(client, seeded):
    baseline, _ = seeded
    again = payloads.scenario(baseline, created_utc="2026-11-01T09:00:00Z")
    response = client.post(f"{API}/scenarios", json=again)
    assert response.status_code == 200
    assert response.json()["status"] == "duplicate"


def test_event_sequence_reused_by_another_event_is_refused(client, seeded):
    baseline, sess = seeded
    assert client.post(f"{API}/events", json=payloads.event(sess, 1, baseline)).status_code == 201
    _error(client.post(f"{API}/events", json=payloads.event(sess, 1, baseline)), 409, "integrity_conflict")


# ---------------------------------------------------------------- durability

def test_records_survive_backend_restart(data_dir):
    first = TestClient(create_app(data_dir))
    baseline = payloads.city()
    sess = payloads.session(baseline)
    assert first.post(f"{API}/scenarios", json=payloads.scenario(baseline)).status_code == 201
    assert first.post(f"{API}/sessions", json=sess).status_code == 201
    events = [payloads.event(sess, i, baseline) for i in (1, 2, 3)]
    for ev in events:
        assert first.post(f"{API}/events", json=ev).status_code == 201
    a, b = _two_alternatives(first, baseline, sess)
    pref = payloads.preference(a, b, "A")
    assert first.post(f"{API}/preferences", json=pref).status_code == 201
    del first

    restarted = TestClient(create_app(data_dir))
    assert restarted.get(f"{API}/health").json()["storage_status"] == "ready"
    timeline = restarted.get(f"{API}/sessions/{sess['session_id']}/events").json()
    assert [e["event_id"] for e in timeline["events"]] == [e["event_id"] for e in events]
    assert restarted.get(f"{API}/preferences/stats").json()["unique_comparisons"] == 1
    # A client that never saw the acknowledgement resends after the restart: still no duplicate.
    assert restarted.post(f"{API}/preferences", json=pref).json()["status"] == "duplicate"
    assert restarted.post(f"{API}/events", json=events[2]).json()["status"] == "duplicate"


def test_migrations_are_not_reapplied(data_dir):
    create_app(data_dir)
    app = create_app(data_dir)
    with app.state.db.reader() as conn:
        rows = conn.execute("SELECT version FROM schema_migration").fetchall()
    assert [r[0] for r in rows] == [1, 2, 3]


# ---------------------------------------------------------------- refusals

def test_invalid_choice_and_mode_are_refused(client, seeded):
    baseline, sess = seeded
    a, b = _two_alternatives(client, baseline, sess)
    _error(client.post(f"{API}/preferences", json=dict(payloads.preference(a, b), choice="C")), 422, "validation_error")
    _error(client.post(f"{API}/preferences", json=dict(payloads.preference(a, b), mode="auto")), 422, "validation_error")
    assert client.get(f"{API}/preferences/stats").json()["unique_comparisons"] == 0


def test_preference_across_scenarios_is_refused(client, seeded):
    baseline, sess = seeded
    a, _ = _two_alternatives(client, baseline, sess)
    other = payloads.city(scenario_id="other-scenario", seed=2)
    assert client.post(f"{API}/scenarios", json=payloads.scenario(other)).status_code == 201
    other_sess = payloads.session(other)
    assert client.post(f"{API}/sessions", json=other_sess).status_code == 201
    foreign = payloads.alternative(payloads.edit(other, "r01_c03", "park"), other, other_sess["session_id"], "B")
    assert client.post(f"{API}/alternatives", json=foreign).status_code == 201
    _error(client.post(f"{API}/preferences", json=payloads.preference(a, foreign)), 422, "scenario_mismatch")


def test_preference_against_unknown_alternative_is_refused(client, seeded):
    baseline, sess = seeded
    a, b = _two_alternatives(client, baseline, sess)
    ghost = dict(b, alternative_id="alt-never-saved")
    _error(client.post(f"{API}/preferences", json=payloads.preference(a, ghost)), 422, "unknown_alternative")


def test_self_and_identical_comparisons_are_refused(client, seeded):
    baseline, sess = seeded
    a, _ = _two_alternatives(client, baseline, sess)
    _error(client.post(f"{API}/preferences", json=payloads.preference(a, a)), 422, "self_comparison")
    twin = payloads.alternative(payloads.edit(baseline, "r01_c03", "park"), baseline, sess["session_id"], "B")
    assert client.post(f"{API}/alternatives", json=twin).status_code == 201
    _error(client.post(f"{API}/preferences", json=payloads.preference(a, twin)), 422, "identical_designs")


def test_false_training_eligibility_and_feasibility_are_refused(client, seeded):
    baseline, sess = seeded
    a, b = _two_alternatives(client, baseline, sess)
    _error(client.post(f"{API}/preferences", json=dict(payloads.preference(a, b, "tie"), training_eligible=True)),
           422, "eligibility_mismatch")
    _error(client.post(f"{API}/preferences", json=dict(payloads.preference(a, b), a_feasible=False)),
           422, "feasibility_mismatch")
    _error(client.post(f"{API}/preferences", json=dict(payloads.preference(a, b), model_version_locked="m1")),
           422, "model_lock_requires_evaluation")
    _error(client.post(f"{API}/preferences", json=payloads.preference(a, b, mode="evaluation")),
           422, "evaluation_trial_required")


def test_same_pair_cannot_be_decided_twice(client, seeded):
    baseline, sess = seeded
    a, b = _two_alternatives(client, baseline, sess)
    assert client.post(f"{API}/preferences", json=payloads.preference(a, b, "A")).status_code == 201
    err = _error(client.post(f"{API}/preferences", json=payloads.preference(b, a, "B")), 409, "pair_already_decided")
    assert err["details"]["existing_comparison_id"]


def test_infeasible_designs_are_stored_but_not_training_eligible(client, seeded):
    baseline, sess = seeded
    # Demolishing three homes drops housing below the 90% floor.
    state = baseline
    for cell in ("r01_c01", "r01_c02", "r02_c01"):
        state = payloads.edit(state, cell, "vacant")
    infeasible = payloads.alternative(state, baseline, sess["session_id"], "A")
    assert infeasible["feasible"] is False
    assert client.post(f"{API}/alternatives", json=infeasible).status_code == 201
    good = payloads.alternative(payloads.edit(baseline, "r01_c03", "park"), baseline, sess["session_id"], "B")
    assert client.post(f"{API}/alternatives", json=good).status_code == 201
    pref = payloads.preference(infeasible, good, "B")
    assert pref["training_eligible"] is False
    assert client.post(f"{API}/preferences", json=pref).status_code == 201
    stats = client.get(f"{API}/preferences/stats").json()
    assert stats["unique_comparisons"] == 1 and stats["training_eligible"] == 0


def test_session_requires_registered_scenario_and_matching_baseline(client):
    baseline = payloads.city()
    _error(client.post(f"{API}/sessions", json=payloads.session(baseline)), 422, "unknown_scenario")
    assert client.post(f"{API}/scenarios", json=payloads.scenario(baseline)).status_code == 201
    _error(client.post(f"{API}/sessions", json=dict(payloads.session(baseline), baseline_hash="0" * 16)),
           422, "baseline_mismatch")


def test_scenario_with_tampered_hash_or_metrics_is_refused(client):
    baseline = payloads.city()
    body = payloads.scenario(baseline)
    _error(client.post(f"{API}/scenarios", json=dict(body, baseline_hash="0123456789abcdef")), 422, "baseline_mismatch")
    lying = copy.deepcopy(body)
    lying["baseline_metrics"]["access_pct"] = 99.0
    _error(client.post(f"{API}/scenarios", json=lying), 422, "metrics_mismatch")
    corrupted = copy.deepcopy(body)
    corrupted["baseline"]["cells"][7]["floors"] = 5
    _error(client.post(f"{API}/scenarios", json=corrupted), 422, "invalid_city")


def test_alternative_with_wrong_numbers_is_refused(client, seeded):
    baseline, sess = seeded
    alt = payloads.alternative(payloads.edit(baseline, "r01_c03", "park"), baseline, sess["session_id"])
    for mutate in (
        lambda d: d["metrics"].__setitem__("housing_units", d["metrics"]["housing_units"] + 1),
        lambda d: d["metrics"].__setitem__("spent_credits", 0),
        lambda d: d["features"].__setitem__("delta_access_fraction", 0.5),
        lambda d: d.__setitem__("feasible", not d["feasible"]),
    ):
        bad = copy.deepcopy(alt)
        mutate(bad)
        _error(client.post(f"{API}/alternatives", json=bad), 422, "metrics_mismatch")
    _error(client.post(f"{API}/alternatives", json=dict(alt, state_hash="0" * 16)), 422, "state_hash_mismatch")


def test_alternative_that_edits_a_road_is_refused(client, seeded):
    baseline, sess = seeded
    state = payloads.edit(baseline, "r00_c00", "park")
    _error(client.post(f"{API}/alternatives", json=payloads.alternative(state, baseline, sess["session_id"])),
           422, "invalid_design")


def test_alternative_for_other_scenario_session_is_refused(client, seeded):
    baseline, sess = seeded
    other = payloads.city(scenario_id="other-scenario", seed=2)
    assert client.post(f"{API}/scenarios", json=payloads.scenario(other)).status_code == 201
    alt = payloads.alternative(payloads.edit(other, "r01_c03", "park"), other, sess["session_id"])
    _error(client.post(f"{API}/alternatives", json=alt), 422, "scenario_mismatch")


def test_event_validation(client, seeded):
    baseline, sess = seeded
    _error(client.post(f"{API}/events", json=dict(payloads.event(sess, 1, baseline), scenario_id="other-scenario")),
           422, "scenario_mismatch")
    _error(client.post(f"{API}/events", json=dict(payloads.event(sess, 1, baseline), session_id="session-ghost")),
           422, "unknown_session")
    _error(client.post(f"{API}/events", json=dict(payloads.event(sess, 1, baseline), action="teleport")),
           422, "invalid_action")
    _error(client.post(f"{API}/events", json=dict(payloads.event(sess, 1, baseline), sequence=0)), 422, "validation_error")
    _error(client.post(f"{API}/events", json=dict(payloads.event(sess, 1, baseline), extra_field=1)), 422, "validation_error")


def test_events_with_blank_nested_objects_from_jsonutility_are_accepted(client, seeded):
    _, sess = seeded
    checkpoint = payloads.event(sess, 1, None, action="save_alternative_a", event_type="checkpoint")
    checkpoint["original_event_id"] = "alt-0123"
    response = client.post(f"{API}/events", json=checkpoint)
    assert response.status_code == 201, response.text
    stored = client.get(f"{API}/sessions/{sess['session_id']}/events").json()["events"][0]
    assert stored["cell_id"] is None and stored["before_json"] is None
    assert stored["original_event_id"] == "alt-0123" and stored["reversal_of_event_id"] is None


def test_loading_a_blinded_pair_is_a_system_checkpoint(client, seeded):
    _, sess = seeded
    event = payloads.event(sess, 1, None, action="load_evaluation_pair", event_type="checkpoint")
    event["input_source"] = "system"
    assert client.post(f"{API}/events", json=event).status_code == 201
    _error(client.post(f"{API}/events", json=dict(payloads.event(sess, 2, None, action="load_evaluation_pair", event_type="decision"))),
           422, "invalid_action")


# ---------------------------------------------------------------- reads, export, delete

def test_timeline_is_ordered_by_sequence(client, seeded):
    baseline, sess = seeded
    evs = [payloads.event(sess, i, baseline) for i in (3, 1, 2)]
    for ev in evs:
        assert client.post(f"{API}/events", json=ev).status_code == 201
    timeline = client.get(f"{API}/sessions/{sess['session_id']}/events").json()
    assert [e["sequence"] for e in timeline["events"]] == [1, 2, 3]
    _error(client.get(f"{API}/sessions/session-ghost/events"), 404, "unknown_session")


def test_export_reproduces_every_alternative(client, seeded):
    baseline, sess = seeded
    a, b = _two_alternatives(client, baseline, sess)
    assert client.post(f"{API}/preferences", json=payloads.preference(a, b, "A")).status_code == 201
    bundle = json.loads(client.get(f"{API}/export").content)
    scenario = bundle["scenarios"][0]
    rules = ScenarioRules(**scenario["constraints_json"])
    base = city_from_dict(scenario["city_baseline_json"])
    assert base.content_hash() == scenario["baseline_hash"]
    for alt in bundle["alternatives"]:
        rebuilt = city_from_dict(alt["snapshot_json"])
        assert rebuilt.content_hash() == alt["state_hash"]
        recomputed = evaluate(rebuilt, base, rules).to_dict()
        assert diff_values(recomputed, alt["metrics_json"], Metrics.COMPARED_FIELDS) == []
    assert {x["id"] for x in bundle["alternatives"]} == {a["alternative_id"], b["alternative_id"]}
    assert bundle["export_manifest"]["format"] == "plannerdna-research-export"


def test_comparisons_csv(client, seeded):
    baseline, sess = seeded
    a, b = _two_alternatives(client, baseline, sess)
    pref = payloads.preference(a, b, "B")
    assert client.post(f"{API}/preferences", json=pref).status_code == 201
    response = client.get(f"{API}/export/comparisons.csv")
    assert response.headers["content-type"].startswith("text/csv")
    rows = list(csv.DictReader(io.StringIO(response.text)))
    assert len(rows) == 1
    assert rows[0]["comparison_id"] == pref["comparison_id"] and rows[0]["choice"] == "B"
    assert float(rows[0]["a_delta_access_fraction"]) == a["features"]["delta_access_fraction"]


def test_stats_counts(client, seeded):
    baseline, sess = seeded
    a, b = _two_alternatives(client, baseline, sess)
    c = payloads.alternative(payloads.edit(baseline, "r02_c03", "park"), baseline, sess["session_id"], "B")
    assert client.post(f"{API}/alternatives", json=c).status_code == 201
    assert client.post(f"{API}/preferences", json=payloads.preference(a, b, "A")).status_code == 201
    assert client.post(f"{API}/preferences", json=payloads.preference(a, c, "tie")).status_code == 201
    stats = client.get(f"{API}/preferences/stats").json()
    assert stats["unique_comparisons"] == 2
    assert stats["choice_a"] == 1 and stats["ties"] == 1
    assert stats["training_eligible"] == 1 and stats["training_eligible_scenarios"] == 1
    assert "Training samples: 1 unique direct comparisons across 1 scenarios" in stats["summary"]


def test_delete_requires_exact_confirmation(client, seeded):
    _error(client.request("DELETE", f"{API}/data", json={"confirm": "yes", "backup": False}), 400, "confirmation_required")
    _error(client.request("DELETE", f"{API}/data", json={"backup": False}), 422, "validation_error")
    assert client.get(f"{API}/export").json()["export_manifest"]["counts"]["scenarios"] == 1


def test_delete_with_backup_removes_everything(client, seeded, data_dir):
    baseline, sess = seeded
    a, b = _two_alternatives(client, baseline, sess)
    assert client.post(f"{API}/preferences", json=payloads.preference(a, b, "A")).status_code == 201
    models = data_dir / "models"
    models.mkdir()
    (models / "placeholder.bin").write_bytes(b"x")

    response = client.request("DELETE", f"{API}/data", json={"confirm": "DELETE ALL PLANNERDNA DATA", "backup": True})
    assert response.status_code == 200, response.text
    body = response.json()
    assert body["deleted_rows"]["comparison"] == 1 and body["deleted_rows"]["alternative"] == 2
    assert body["models_removed"] is True and not models.exists()
    backup = json.loads(open(body["backup_path"], encoding="utf-8").read())
    assert backup["export_manifest"]["counts"]["comparisons"] == 1

    counts = client.get(f"{API}/export").json()["export_manifest"]["counts"]
    assert all(v == 0 for v in counts.values())
    assert client.get(f"{API}/health").json()["storage_status"] == "ready"
    # The database is usable again afterwards.
    assert client.post(f"{API}/scenarios", json=payloads.scenario(baseline)).status_code == 201
