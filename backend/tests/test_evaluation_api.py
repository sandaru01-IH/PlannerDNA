"""Pre-registered blinded evaluation (Phase 4).

All labels are SYNTHETIC TEST FIXTURE LABELS from a fixed rule ("choose the design with
more park access"), never real user choices.
"""

from __future__ import annotations

import csv
import hashlib
import io
import json
from itertools import combinations

import numpy as np
import pytest

import payloads
from app.ml import evaluation as ev
from test_models_api import API, DESIGNS, _error, build, favour, record_scenario, register, train

PAIR_RULE = "test-pair-rule"
access_rule = favour("delta_access_fraction")


def _plan_body(model_version: str, scenarios, pairs: int = 2, repeats: int = 2, plan_id: str | None = None) -> dict:
    return {"schema_version": 1, "plan_id": plan_id or payloads.new_id("plan"), "model_version": model_version,
            "evaluation_scenarios": [{"scenario_id": s, "seed": seed} for s, seed in scenarios],
            "pairs_per_scenario": pairs, "repeat_count": repeats, "pair_rule": PAIR_RULE, "note": "test",
            "app_version": "0.4.0-test", "created_utc": payloads.NOW}


EVAL_SCENARIOS = [("eval-0", 100), ("eval-1", 101), ("eval-2", 102)]


@pytest.fixture
def trained(client):
    for i in range(3):
        record_scenario(client, f"scn-{i}", i, access_rule)
    return train(client)["model"]


@pytest.fixture
def evaluation(client, trained):
    """Registered evaluation scenarios plus an open plan (3 scenarios x 2 pairs, 2 repeats)."""
    sessions = {sid: register(client, sid, seed) for sid, seed in EVAL_SCENARIOS}
    response = client.post(f"{API}/evaluation/plans", json=_plan_body(trained["model_version"], EVAL_SCENARIOS))
    assert response.status_code == 201, response.text
    return response.json()["plan"], sessions, trained


def _fresh_pair(baseline: dict, sess: dict, index: int) -> tuple[dict, dict]:
    """The index-th feasible design pair that differs in park access, as new (unsaved) alternatives."""
    valid = []
    for i, j in combinations(range(len(DESIGNS)), 2):
        a = payloads.alternative(build(baseline, DESIGNS[i]), baseline, sess["session_id"], "A")
        b = payloads.alternative(build(baseline, DESIGNS[j]), baseline, sess["session_id"], "B")
        if a["feasible"] and b["feasible"] and abs(a["features"]["delta_access_fraction"] - b["features"]["delta_access_fraction"]) > 1e-9:
            valid.append((a, b))
    return valid[index % len(valid)]


def _trial_body(plan: dict, step: dict, a_id: str, b_id: str, **override) -> dict:
    body = {"schema_version": 1, "trial_id": payloads.new_id("trial"), "plan_id": plan["plan_id"],
            "trial_index": step["trial_index"], "scenario_id": step["scenario_id"], "alternative_a_id": a_id,
            "alternative_b_id": b_id, "repeat_of_trial_id": step["repeat_of_trial_id"],
            "candidate_seed": step["candidate_seed"], "pair_rule": PAIR_RULE}
    body.update(override)
    return body


def _eval_pref(a: dict, b: dict, trial: dict, session_id: str, choice: str) -> dict:
    pref = payloads.preference(a, b, choice=choice, mode="evaluation")
    pref.update(session_id=session_id, shown_left_alternative_id=trial["shown_left_alternative_id"],
                model_version_locked=trial["model_version_locked"], evaluation_trial_id=trial["trial_id"],
                training_eligible=False, eligibility_note="blinded evaluation choice")
    return pref


class Runner:
    """Drives the protocol through the API the way the Unity client does."""

    def __init__(self, client, plan, sessions):
        self.client, self.plan, self.sessions = client, plan, sessions
        self.alts: dict[str, dict] = {}
        self.trials: list[dict] = []

    def status(self) -> dict:
        return self.client.get(f"{API}/evaluation/plans/{self.plan['plan_id']}").json()

    def register_next(self) -> tuple[dict, dict]:
        step = self.status()["next"]
        assert step["action"] in ("new_pair", "repeat"), step
        if step["action"] == "new_pair":
            baseline, sess = self.sessions[step["scenario_id"]]
            a, b = _fresh_pair(baseline, sess, step["trial_index"])
            for alt in (a, b):
                assert self.client.post(f"{API}/alternatives", json=alt).status_code == 201
                self.alts[alt["alternative_id"]] = alt
            a_id, b_id = a["alternative_id"], b["alternative_id"]
        else:
            a_id, b_id = step["alternative_a_id"], step["alternative_b_id"]
        response = self.client.post(f"{API}/evaluation/trials", json=_trial_body(self.plan, step, a_id, b_id))
        assert response.status_code == 201, response.text
        trial = response.json()["trial"]
        self.trials.append(trial)
        return step, trial

    def answer(self, trial: dict, chooser=access_rule) -> str:
        a, b = self.alts[trial["alternative_a_id"]], self.alts[trial["alternative_b_id"]]
        choice = chooser(a, b) or "tie"
        _, sess = self.sessions[trial["scenario_id"]]
        response = self.client.post(f"{API}/preferences", json=_eval_pref(a, b, trial, sess["session_id"], choice))
        assert response.status_code == 201, response.text
        return choice


# ---------------------------------------------------------------- pre-registration

def test_plan_needs_a_model_and_genuinely_unseen_scenarios(client):
    register(client, "eval-0", 100)
    register(client, "eval-1", 101)
    _error(client.post(f"{API}/evaluation/plans", json=_plan_body("pm-none", EVAL_SCENARIOS[:2])), 409, "no_model")

    for i in range(3):
        record_scenario(client, f"scn-{i}", i, access_rule)
    model = train(client)["model"]
    version = model["model_version"]
    _error(client.post(f"{API}/evaluation/plans", json=_plan_body("pm-other", EVAL_SCENARIOS[:2])), 409, "model_changed")
    err = _error(client.post(f"{API}/evaluation/plans", json=_plan_body(version, [("scn-0", 0), ("eval-1", 101)])),
                 422, "scenario_not_unseen")
    assert any("used to train" in d for d in err["details"])
    # Same seed under another id is still the training city.
    _error(client.post(f"{API}/evaluation/plans", json=_plan_body(version, [("copy-of-scn-1", 1), ("eval-1", 101)])),
           422, "scenario_not_unseen")
    _error(client.post(f"{API}/evaluation/plans", json=_plan_body(version, [("eval-0", 100), ("eval-0", 100)])),
           422, "duplicate_scenario")
    _error(client.post(f"{API}/evaluation/plans", json=_plan_body(version, EVAL_SCENARIOS[:2], pairs=1, repeats=3)),
           422, "too_many_repeats")
    _error(client.post(f"{API}/evaluation/plans", json=dict(_plan_body(version, EVAL_SCENARIOS[:2]), extra=1)),
           422, "validation_error")

    body = _plan_body(version, EVAL_SCENARIOS[:2])
    created = client.post(f"{API}/evaluation/plans", json=body)
    assert created.status_code == 201
    plan = created.json()["plan"]
    assert plan["status"] == "open" and plan["model_version"] == version
    assert plan["model_artifact_sha256"] == model["artifact_sha256"]
    assert plan["training_scenario_ids"] == ["scn-0", "scn-1", "scn-2"]
    assert plan["training_comparison_count"] == model["unique_comparisons"]
    assert len(plan["registration_sha256"]) == 64 and plan["plan_seed"] > 0
    assert plan["next"]["action"] == "new_pair" and plan["next"]["scenario_id"] == "eval-0"
    assert plan["next"]["candidate_seed"] == plan["candidate_seed_base"]
    retry = client.post(f"{API}/evaluation/plans", json=body)
    assert retry.status_code == 200 and retry.json()["plan"]["plan_seed"] == plan["plan_seed"]
    _error(client.post(f"{API}/evaluation/plans", json=dict(body, note="changed")), 409, "id_conflict")
    _error(client.post(f"{API}/evaluation/plans", json=_plan_body(version, [("eval-2", 102), ("eval-3", 103)])),
           409, "evaluation_already_open")


def test_scenario_with_training_choices_cannot_be_evaluated(client, trained):
    baseline, sess = register(client, "practised", 77)
    a = payloads.alternative(build(baseline, DESIGNS[0]), baseline, sess["session_id"])
    b = payloads.alternative(build(baseline, DESIGNS[3]), baseline, sess["session_id"])
    for alt in (a, b):
        assert client.post(f"{API}/alternatives", json=alt).status_code == 201
    assert client.post(f"{API}/preferences", json=payloads.preference(a, b, "A")).status_code == 201
    err = _error(client.post(f"{API}/evaluation/plans", json=_plan_body(trained["model_version"], [("practised", 77), ("eval-1", 101)])),
                 422, "scenario_not_unseen")
    assert any("training choices" in d for d in err["details"])


# ---------------------------------------------------------------- the protocol end to end

def test_full_blinded_evaluation_is_sealed_randomised_scored_and_reproducible(client, evaluation, data_dir):
    plan, sessions, model = evaluation
    runner = Runner(client, plan, sessions)

    # While open: the model is frozen and the evaluation scenarios are sealed.
    health = client.get(f"{API}/health").json()
    assert health["evaluation_status"] == "open" and health["open_evaluation_plan_id"] == plan["plan_id"]
    _error(client.post(f"{API}/models/train"), 409, "evaluation_in_progress")
    ready = client.get(f"{API}/models/readiness").json()
    assert ready["can_train"] is False and ready["reasons"][0]["code"] == "evaluation_in_progress" and ready["evaluation_open"]
    baseline, sess = sessions["eval-0"]
    x = payloads.alternative(build(baseline, DESIGNS[0]), baseline, sess["session_id"])
    y = payloads.alternative(build(baseline, DESIGNS[3]), baseline, sess["session_id"])
    for alt in (x, y):
        assert client.post(f"{API}/alternatives", json=alt).status_code == 201
    _error(client.post(f"{API}/preferences", json=payloads.preference(x, y, "A")), 409, "scenario_reserved_for_evaluation")
    _error(client.post(f"{API}/models/predict-pair", json={"scenario_id": "eval-0", "alternative_a_id": x["alternative_id"],
                                                          "alternative_b_id": y["alternative_id"]}), 409, "sealed_for_evaluation")

    seals = {}
    sides = []
    while True:
        step = runner.status()["next"]
        if step["action"] == "complete":
            break
        step, trial = runner.register_next()
        assert trial["p_a"] is None and trial["revealed"] is False, "nothing is predicted before the choice"
        assert len(trial["sealed_sha256"]) == 64 and trial["model_version_locked"] == model["model_version"]
        seals[trial["trial_id"]] = trial["sealed_sha256"]
        sides.append(trial["shown_left_alternative_id"] == trial["alternative_a_id"])

        _error(client.post(f"{API}/evaluation/trials/{trial['trial_id']}/reveal"), 409, "choice_not_recorded")
        export = client.get(f"{API}/export").json()
        sealed = next(t for t in export["evaluation_trials"] if t["id"] == trial["trial_id"])
        assert sealed["p_a"] is None and sealed["sealed_payload"] is None, "an export never leaks a sealed prediction"
        assert all(p["predicted_probability"] is not None or p["prediction_kind"] == "evaluation" for p in export["predictions"])

        if step["action"] == "repeat":
            original = next(t for t in runner.trials if t["trial_id"] == step["repeat_of_trial_id"])
            assert trial["shown_left_alternative_id"] == original["shown_right_alternative_id"], "repeats swap the screen sides"
            assert trial["kind"] == "repeat"

        choice = runner.answer(trial)
        answered = next(t for t in client.get(f"{API}/export").json()["evaluation_trials"] if t["id"] == trial["trial_id"])
        assert answered["status"] == "answered" and answered["p_a"] is None, "unrevealed predictions stay sealed while the test is open"
        revealed = client.post(f"{API}/evaluation/trials/{trial['trial_id']}/reveal").json()
        assert hashlib.sha256(revealed["sealed_payload"].encode("utf-8")).hexdigest() == seals[trial["trial_id"]]
        sealed_payload = json.loads(revealed["sealed_payload"])
        assert sealed_payload["p_a"] == revealed["p_a"] and sealed_payload["model_version"] == model["model_version"]
        assert len(sealed_payload["nonce"]) == 32
        assert revealed["choice"] == choice and revealed["p_a"] + revealed["p_b"] == pytest.approx(1.0)
        assert revealed["matched"] == ((revealed["p_a"] > 0.5) == (choice == "A"))
        again = client.post(f"{API}/evaluation/trials/{trial['trial_id']}/reveal").json()
        assert again["revealed_utc"] == revealed["revealed_utc"]

    status = runner.status()
    assert status["progress"] == {"planned_primary": 6, "registered_primary": 6, "answered_primary": 6, "planned_repeats": 2,
                                  "registered_repeats": 2, "answered_repeats": 2, "awaiting_choice": 0}
    assert all(t["revealed"] and t["p_a"] is not None for t in status["trials"])
    _error(client.get(f"{API}/evaluation/plans/{plan['plan_id']}/report"), 409, "evaluation_open")

    closed = client.post(f"{API}/evaluation/plans/{plan['plan_id']}/close", json={"reason": "all pairs answered"})
    assert closed.status_code == 200, closed.text
    report = closed.json()["report"]
    c = report["counts"]
    assert (c["registered_trials"], c["registered_primary"], c["registered_repeats"], c["scored"], c["unanswered"]) == (8, 6, 2, 6, 0)
    assert c["scenarios_scored"] == 3 and c["choices_for_a"] + c["choices_for_b"] == 6
    assert report["plan"]["registration_sha256"] == plan["registration_sha256"]
    assert report["model"]["model_version"] == model["model_version"]
    assert report["held_out_guarantee"]["evaluation_choices_in_training"] == 0
    assert report["held_out_guarantee"]["evaluation_scenarios_in_training"] == []
    names = {b["name"]: b for b in report["metrics"]["baselines"]}
    assert set(names) == {"chance", "cheaper", "more_access", "equal_weight", "left_position"}
    assert names["chance"]["accuracy"] == 0.5 and names["chance"]["brier"] == 0.25
    assert names["more_access"]["accuracy"] == 1.0, "the fixture rule is exactly 'more access'"
    m = report["metrics"]["model"]
    assert m["n"] == 6 and 0 <= m["accuracy"] <= 1 and m["accuracy"] > 0.5
    assert m["accuracy_ci"] is not None and m["accuracy_ci"][0] <= m["accuracy"] <= m["accuracy_ci"][1]
    rc = report["repeat_consistency"]
    assert rc["scored_pairs"] == 2 and rc["agreements"] == 2 and rc["rate"] == 1.0
    assert rc["chose_same_screen_side"] == 0, "a consistent chooser follows the design, not the side"
    assert report["position"]["n"] == 8
    assert sum(b["n"] for b in report["calibration"]["bins"]) == report["calibration"]["n"]
    assert len(report["trials"]) == 8 and sum(t["scored"] for t in report["trials"]) == 6
    assert any("synthetic" in cv for cv in report["caveats"])
    assert f"of 6 held-out choices" in report["summary"]

    fetched = client.get(f"{API}/evaluation/plans/{plan['plan_id']}/report").json()
    assert fetched["verification"]["reproducible"] is True
    assert fetched["report"] == report and fetched["report_sha256"] == closed.json()["report_sha256"]

    rows = list(csv.reader(io.StringIO(client.get(f"{API}/evaluation/plans/{plan['plan_id']}/trials.csv").text)))
    assert len(rows) == 9 and rows[0][0] == "trial_id"
    metrics = list(csv.DictReader(io.StringIO(client.get(f"{API}/evaluation/plans/{plan['plan_id']}/metrics.csv").text)))
    model_acc = next(r for r in metrics if r["predictor"] == "model" and r["metric"] == "accuracy")
    assert float(model_acc["value"]) == pytest.approx(m["accuracy"])
    assert any(r["predictor"] == "person" and r["metric"] == "consistency" for r in metrics)

    # After closing: training is allowed again and the evaluation labels never become training data.
    assert client.get(f"{API}/health").json()["evaluation_status"] == "none"
    after = client.post(f"{API}/models/train")
    assert after.status_code == 200 and after.json()["status"] == "unchanged", "evaluation choices added nothing to training"
    ready = client.get(f"{API}/models/readiness").json()
    assert ready["excluded"]["evaluation_mode"] == 8 and ready["eligible_comparisons"] == model["unique_comparisons"]
    assert client.post(f"{API}/evaluation/plans/{plan['plan_id']}/close", json={}).json()["status"] == "unchanged"

    overview = client.get(f"{API}/evaluation").json()
    assert overview["open_plan_id"] == "" and overview["count"] == 1 and overview["plans"][0]["has_report"]
    export = client.get(f"{API}/export").json()
    assert export["export_manifest"]["counts"]["evaluation_plans"] == 1
    assert export["export_manifest"]["counts"]["evaluation_trials"] == 8
    assert export["evaluation_plans"][0]["report_json"]["summary"] == report["summary"]
    evaluation_cmp = [c for c in export["comparisons"] if c["mode"] == "evaluation"]
    assert len(evaluation_cmp) == 8 and all(c["training_eligible"] == 0 and c["evaluation_trial_id"] for c in evaluation_cmp)
    assert sum(1 for c in evaluation_cmp if c["repeat_index"] == 1 and c["repeated_pair_id"]) == 2


def test_report_detects_tampering(client, evaluation):
    plan, sessions, _ = evaluation
    runner = Runner(client, plan, sessions)
    _, trial = runner.register_next()
    runner.answer(trial)
    client.post(f"{API}/evaluation/plans/{plan['plan_id']}/close", json={"reason": "early"})
    with client.app.state.db.transaction() as conn:
        stored = json.loads(conn.execute("SELECT report_json FROM evaluation_plan").fetchone()[0])
        stored["metrics"]["model"]["accuracy"] = 0.99
        conn.execute("UPDATE evaluation_plan SET report_json = ?", (json.dumps(stored),))
    check = client.get(f"{API}/evaluation/plans/{plan['plan_id']}/report").json()["verification"]
    assert check["stored_report_unchanged"] is False and check["reproducible"] is False


# ---------------------------------------------------------------- refusals

def test_trial_and_choice_validation(client, evaluation):
    plan, sessions, model = evaluation
    runner = Runner(client, plan, sessions)
    step = runner.status()["next"]
    baseline, sess = sessions["eval-0"]
    a, b = _fresh_pair(baseline, sess, 0)
    for alt in (a, b):
        assert client.post(f"{API}/alternatives", json=alt).status_code == 201
    a_id, b_id = a["alternative_id"], b["alternative_id"]
    post = lambda **kw: client.post(f"{API}/evaluation/trials", json=_trial_body(plan, step, a_id, b_id, **kw))
    _error(post(trial_index=5), 409, "unexpected_trial_index")
    _error(post(scenario_id="eval-1"), 422, "scenario_not_planned")
    _error(post(candidate_seed=step["candidate_seed"] + 1), 422, "candidate_seed_mismatch")
    _error(post(pair_rule="other"), 422, "pair_rule_mismatch")
    _error(post(repeat_of_trial_id="trial-x"), 422, "repeat_mismatch")
    _error(post(alternative_b_id=a_id), 422, "self_comparison")
    bad = payloads.alternative(build(baseline, [("r04_c02", "park", 0), ("r04_c01", "park", 0)]), baseline, sess["session_id"])
    assert client.post(f"{API}/alternatives", json=bad).status_code == 201
    _error(post(alternative_b_id=bad["alternative_id"]), 422, "infeasible_alternative")

    first = post()
    assert first.status_code == 201
    trial = first.json()["trial"]
    runner.alts.update({a_id: a, b_id: b})
    _error(client.post(f"{API}/evaluation/trials", json=_trial_body(plan, step, a_id, b_id)), 409, "trial_awaiting_choice")

    good = _eval_pref(a, b, trial, sess["session_id"], "A")
    _error(client.post(f"{API}/preferences", json=dict(good, model_version_locked="pm-other")), 422, "model_lock_mismatch")
    other_left = b_id if trial["shown_left_alternative_id"] == a_id else a_id
    _error(client.post(f"{API}/preferences", json=dict(good, shown_left_alternative_id=other_left)), 422, "display_order_mismatch")
    _error(client.post(f"{API}/preferences", json=dict(good, evaluation_trial_id="trial-unknown")), 422, "unknown_trial")
    _error(client.post(f"{API}/preferences", json=dict(good, training_eligible=True)), 422, "eligibility_mismatch")
    _error(client.post(f"{API}/preferences", json=dict(good, mode="train", training_eligible=True)), 422, "model_lock_requires_evaluation")
    assert client.post(f"{API}/preferences", json=good).status_code == 201
    assert client.post(f"{API}/preferences", json=good).status_code == 200, "an exact retry is harmless"
    _error(client.post(f"{API}/preferences", json=dict(good, comparison_id=payloads.new_id("cmp"))), 409, "trial_already_answered")

    step2 = runner.status()["next"]
    assert step2["action"] == "new_pair" and step2["trial_index"] == 1
    _error(client.post(f"{API}/evaluation/trials", json=_trial_body(plan, step2, a_id, b_id)), 409, "alternative_already_used")


def test_closing_early_is_reported_honestly(client, evaluation):
    plan, sessions, _ = evaluation
    runner = Runner(client, plan, sessions)
    _, trial = runner.register_next()
    report = client.post(f"{API}/evaluation/plans/{plan['plan_id']}/close", json={"reason": "stopped"}).json()["report"]
    assert report["counts"]["scored"] == 0 and report["counts"]["unanswered"] == 1
    assert report["summary"].startswith("No held-out choice was scored")
    assert any("closed after 1 of 6" in w for w in report["warnings"])
    assert report["trials"][0]["choice"] is None
    a, b = runner.alts[trial["alternative_a_id"]], runner.alts[trial["alternative_b_id"]]
    _, sess = sessions[trial["scenario_id"]]
    _error(client.post(f"{API}/preferences", json=_eval_pref(a, b, trial, sess["session_id"], "A")), 409, "evaluation_closed")
    # A closed plan's scenarios are not reusable by a new plan.
    model = client.get(f"{API}/models/current").json()["model"]
    _error(client.post(f"{API}/evaluation/plans", json=_plan_body(model["model_version"], EVAL_SCENARIOS[:2])),
           422, "scenario_not_unseen")


def test_delete_removes_evaluation_records(client, evaluation):
    plan, sessions, _ = evaluation
    runner = Runner(client, plan, sessions)
    _, trial = runner.register_next()
    runner.answer(trial)
    body = client.request("DELETE", f"{API}/data", json={"confirm": "DELETE ALL PLANNERDNA DATA", "backup": False}).json()
    assert body["deleted_rows"]["evaluation_plan"] == 1 and body["deleted_rows"]["evaluation_trial"] == 1
    assert client.get(f"{API}/evaluation").json() == {"protocol_version": ev.PROTOCOL_VERSION, "open_plan_id": "",
                                                      "open_plan": None, "count": 0, "plans": []}


# ---------------------------------------------------------------- pure scoring

def _record(i, scenario, p_a, choice, kind="primary", left_is_a=True, repeat_of=None, access=0.1):
    diffs = np.array([access, 0.0, 0.0, -0.05, 0.0])
    return ev.TrialRecord(trial_id=f"t{i}", trial_index=i, kind=kind, scenario_id=scenario, seed=i, alt_a_id=f"a{i}",
                          alt_b_id=f"b{i}", shown_left_is_a=left_is_a, p_a=p_a, diffs=diffs, choice=choice,
                          repeat_of_trial_id=repeat_of, comparison_id=f"c{i}" if choice else None, sealed_sha256="x" * 64,
                          registered_utc="t", answered_utc="t" if choice else None, revealed_utc=None)


def test_scoring_counts_are_exact():
    scenarios = [{"scenario_id": f"s{k}", "seed": k} for k in range(4)]
    trials = [
        _record(0, "s0", 0.8, "A"), _record(1, "s0", 0.3, "A", left_is_a=False),
        _record(2, "s1", 0.6, "B"), _record(3, "s1", 0.5, "A"),
        _record(4, "s2", 0.9, "A"), _record(5, "s2", 0.2, "B"),
        _record(6, "s3", 0.7, "tie"), _record(7, "s3", 0.4, None),
        _record(8, "s0", 0.8, "A", kind="repeat", left_is_a=False, repeat_of="t0"),
        _record(9, "s2", 0.2, "A", kind="repeat", left_is_a=False, repeat_of="t5"),
    ]
    out = ev.evaluate(trials, scenarios, pairs_per_scenario=2, repeat_count=2, model_version="pm-test")
    c = out["counts"]
    assert (c["scored"], c["primary_ties"], c["unanswered"], c["registered_repeats"], c["scenarios_scored"]) == (6, 1, 1, 2, 3)
    m = out["metrics"]["model"]
    # Correct: t0, t4, t5 (3) + t3 is p=0.5 (half) -> 3.5 of 6; wrong: t1, t2.
    assert m["correct"] == 3.5 and m["accuracy"] == pytest.approx(3.5 / 6) and m["undecided"] == 1
    p = np.array([0.8, 0.3, 0.6, 0.5, 0.9, 0.2])
    y = np.array([1, 1, 0, 1, 1, 0])
    assert m["brier"] == pytest.approx(np.mean((p - y) ** 2))
    assert m["accuracy_ci"] is not None, "three scored scenarios allow intervals"
    more_access = next(b for b in out["metrics"]["baselines"] if b["name"] == "more_access")
    assert more_access["accuracy"] == pytest.approx(4 / 6), "always A here; A was chosen 4 of 6 times"
    left = next(b for b in out["metrics"]["baselines"] if b["name"] == "left_position")
    # Left was chosen in t0, t3, t4; not in t1 (B on the left, A chosen), t2 and t5 (A on the left, B chosen).
    assert left["accuracy"] == pytest.approx(3 / 6)
    rc = out["repeat_consistency"]
    assert (rc["scored_pairs"], rc["agreements"], rc["disagreements"]) == (2, 1, 1) and rc["rate"] == 0.5
    pos = out["position"]
    assert pos["n"] == 8 and pos["chose_left"] + pos["chose_right"] == 8
    assert sum(b["n"] for b in out["calibration"]["bins"]) == 5, "the p = 0.5 prediction favours neither design"
    per = {r["scenario_id"]: r for r in out["per_scenario"]}
    assert per["s3"]["scored"] == 0 and per["s3"]["ties"] == 1 and per["s3"]["unanswered"] == 1
    assert out["summary"].startswith("The frozen model pm-test predicted 3.5 of 6 held-out choices")
    assert ev.evaluate(trials, scenarios, 2, 2, "pm-test") == out, "deterministic"


def test_scoring_withholds_intervals_with_too_few_scenarios():
    scenarios = [{"scenario_id": "s0", "seed": 0}, {"scenario_id": "s1", "seed": 1}]
    trials = [_record(0, "s0", 0.8, "A"), _record(1, "s1", 0.7, "B")]
    out = ev.evaluate(trials, scenarios, 1, 0, "pm-test")
    assert out["metrics"]["model"]["accuracy_ci"] is None and out["bootstrap"]["available"] is False
    assert any("No interval is reported" in w for w in out["warnings"])
    assert any("Only 2 scored choice" in w for w in out["warnings"])
