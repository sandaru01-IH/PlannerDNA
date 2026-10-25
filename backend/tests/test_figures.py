"""tools/make_figures.py on a real export produced through the API (synthetic test-fixture labels)."""

from __future__ import annotations

import csv
import json

import pytest

pytest.importorskip("matplotlib")

from test_evaluation_api import Runner, evaluation, trained  # noqa: E402,F401  (fixtures)
from test_models_api import API  # noqa: E402
from tools import make_figures  # noqa: E402


def test_figures_come_from_the_export_and_the_model_refit_reproduces(client, evaluation, tmp_path):
    plan, sessions, model = evaluation
    runner = Runner(client, plan, sessions)
    while runner.status()["next"]["action"] != "complete":
        _, trial = runner.register_next()
        runner.answer(trial)
        client.post(f"{API}/evaluation/trials/{trial['trial_id']}/reveal")
    client.post(f"{API}/evaluation/plans/{plan['plan_id']}/close", json={"reason": "done"})
    bundle = client.get(f"{API}/export").json()
    export = tmp_path / "export.json"
    export.write_text(json.dumps(bundle), encoding="utf-8")

    out = tmp_path / "figures"
    assert make_figures.main(["--export", str(export), "--out", str(out), "--formats", "png"]) == 0
    manifest = json.loads((out / "figures_manifest.json").read_text(encoding="utf-8"))
    ids = {f["id"] for f in manifest["figures"]}
    for expected in ("fig01_training_data", "fig02_tradeoff_space", "fig03_coefficients", "fig04_bootstrap_draws",
                     "fig05_loso_vs_baselines", "fig07_learning_curve", "fig08_evaluation_vs_baselines",
                     "fig09_model_minus_baselines", "fig10_calibration", "fig11_per_scenario",
                     "fig12_consistency_and_position", "fig13_sealed_predictions"):
        assert expected in ids, (expected, manifest["skipped"])
        assert (out / f"{expected}.png").stat().st_size > 10_000
    assert {s["id"] for s in manifest["skipped"]} == {"fig06_model_history"}, "only one model version exists"
    check = manifest["reproducibility_check"]
    assert check["reproduced"] is True and check["comparisons"] == model["unique_comparisons"]

    with (out / "fig03_coefficients.csv").open(encoding="utf-8") as fh:
        rows = list(csv.DictReader(fh))
    stored = {c["feature"]: c["weight_standardised"] for c in model["coefficients"]}
    assert {r["feature"]: float(r["weight_standardised"]) for r in rows} == pytest.approx(stored)
    with (out / "fig08_evaluation_vs_baselines.csv").open(encoding="utf-8") as fh:
        model_row = next(r for r in csv.DictReader(fh) if r["predictor"] == "model")
    report = client.get(f"{API}/evaluation/plans/{plan['plan_id']}/report").json()["report"]
    assert float(model_row["accuracy"]) == pytest.approx(report["metrics"]["model"]["accuracy"])
    assert int(model_row["n"]) == report["counts"]["scored"] == 6
    captions = (out / "captions.md").read_text(encoding="utf-8")
    assert "fig08_evaluation_vs_baselines" in captions and "SYNTHETIC" not in captions


def test_non_loopback_backend_is_refused():
    with pytest.raises(SystemExit):
        make_figures.load_export(None, "http://example.com:8765")
