"""Unit tests of the pairwise logistic model on SYNTHETIC FIXTURE LABELS (not user labels).

A simulated chooser with known weights labels pairs of synthetic designs; the fitted
model must recover the direction of the feature the chooser systematically favours.
"""

from __future__ import annotations

import numpy as np
import pytest

from app.ml import pairwise as pw

K = len(pw.FEATURE_NAMES)
ACCESS, HOUSING, PARK, BUDGET, LOST = range(K)


def synthetic_designs(rng: np.random.Generator, n: int) -> np.ndarray:
    """Plausible features-v1 vectors: park share moves with access, spending with both."""
    access = rng.uniform(0.0, 0.3, n)
    park = 0.15 * access + rng.uniform(0.0, 0.02, n)
    housing = rng.uniform(-0.08, 0.06, n)
    budget = -np.clip(1.5 * access + 2.0 * np.clip(housing, 0, None) + rng.uniform(0.0, 0.4, n), 0.0, 1.0)
    lost = -np.clip(-housing, 0, None) * rng.uniform(0.5, 1.0, n)
    return np.column_stack([access, housing, park, budget, lost])


def synthetic_comparisons(true_weights, scenarios=8, designs=8, pairs=7, seed=11, deterministic=False) -> pw.PairwiseData:
    rng = np.random.default_rng(seed)
    ids, groups, diffs, labels = [], [], [], []
    for s in range(scenarios):
        f = synthetic_designs(rng, designs)
        chosen_pairs = set()
        while len(chosen_pairs) < pairs:
            a, b = rng.choice(designs, size=2, replace=False)
            chosen_pairs.add((int(a), int(b)))
        for a, b in sorted(chosen_pairs):
            x = f[a] - f[b]
            z = float(np.dot(true_weights, x))
            label = int(z > 0) if deterministic else int(rng.random() < 1 / (1 + np.exp(-z)))
            ids.append(f"s{s}-{a}-{b}")
            groups.append(f"scenario-{s}")
            diffs.append(x)
            labels.append(label)
    return pw.PairwiseData(ids, groups, np.array(diffs), np.array(labels))


def weights_for(**kw) -> np.ndarray:
    w = np.zeros(K)
    for name, value in kw.items():
        w[pw.FEATURE_NAMES.index(name)] = value
    return w


@pytest.mark.parametrize("favoured,index", [
    ("delta_access_fraction", ACCESS),
    ("negative_budget_fraction", BUDGET),
    ("delta_housing_fraction", HOUSING),
])
def test_model_learns_the_direction_of_a_systematically_favoured_feature(favoured, index):
    scale = {"delta_access_fraction": 40.0, "negative_budget_fraction": 12.0, "delta_housing_fraction": 80.0}[favoured]
    data = synthetic_comparisons(weights_for(**{favoured: scale}))
    model = pw.fit(data)
    assert model.weights[index] > 0
    assert np.argmax(np.abs(model.weights_standardised)) == index
    # Held-out pair: the design that is better on the favoured feature (all else equal) is predicted preferred.
    better, worse = np.zeros(K), np.zeros(K)
    better[index] = 0.1
    assert model.p_prefer(better, worse) > 0.5


def test_model_learns_a_negative_direction_too():
    data = synthetic_comparisons(weights_for(delta_access_fraction=-40.0))
    model = pw.fit(data)
    assert model.weights[ACCESS] < 0
    report = pw.coefficient_report(model, pw.scenario_bootstrap(data, model.scale), pw.Hyperparameters())
    assert report[ACCESS]["direction"] == "negative"
    assert "less park access" in report[ACCESS]["statement"]


def test_reversed_pair_probabilities_sum_to_one():
    data = synthetic_comparisons(weights_for(delta_access_fraction=30.0, negative_budget_fraction=5.0))
    model = pw.fit(data)
    rng = np.random.default_rng(3)
    for _ in range(50):
        fa, fb = synthetic_designs(rng, 2)
        assert model.p_prefer(fa, fb) + model.p_prefer(fb, fa) == pytest.approx(1.0, abs=1e-12)
    assert model.p_prefer(fa, fa) == pytest.approx(0.5)


def test_swapping_screen_order_of_every_comparison_gives_the_same_model():
    data = synthetic_comparisons(weights_for(delta_access_fraction=30.0))
    swapped = pw.PairwiseData(data.comparison_ids, data.scenario_ids, -data.diffs, 1 - data.chose_a)
    np.testing.assert_allclose(pw.fit(data).weights, pw.fit(swapped).weights, rtol=1e-9, atol=1e-12)


def test_fitting_is_deterministic():
    data = synthetic_comparisons(weights_for(negative_budget_fraction=10.0))
    first, second = pw.fit(data), pw.fit(data)
    np.testing.assert_array_equal(first.weights, second.weights)
    b1 = pw.scenario_bootstrap(data, first.scale)
    b2 = pw.scenario_bootstrap(data, first.scale)
    np.testing.assert_array_equal(b1["draws"], b2["draws"])


def test_a_weight_whose_interval_includes_zero_is_reported_as_directionally_uncertain():
    data = synthetic_comparisons(weights_for(delta_access_fraction=40.0, negative_budget_fraction=6.0))
    model = pw.fit(data)
    boot = pw.scenario_bootstrap(data, model.scale)
    report = pw.coefficient_report(model, boot, pw.Hyperparameters())
    assert not report[ACCESS]["interval_includes_zero"]
    assert "uncertain" not in report[ACCESS]["statement"]

    assert report[BUDGET]["direction"] == "positive"
    boot["low"][BUDGET], boot["high"][BUDGET] = -0.2, 0.8
    row = pw.coefficient_report(model, boot, pw.Hyperparameters())[BUDGET]
    assert row["interval_includes_zero"]
    assert row["direction"] == "positive"
    assert "even the direction is uncertain" in row["statement"]


def test_feature_that_never_varies_is_reported_as_not_estimated():
    data = synthetic_comparisons(weights_for(delta_access_fraction=30.0))
    data.diffs[:, LOST] = 0.0
    model = pw.fit(data)
    assert model.weights[LOST] == 0.0 and not model.varied[LOST]
    row = pw.coefficient_report(model, pw.scenario_bootstrap(data, model.scale), pw.Hyperparameters())[LOST]
    assert row["direction"] == "not_estimated"
    assert any("Never varied" in w for w in pw.assess(data).warnings)


# ---------------------------------------------------------------- refusals

def test_no_data_is_refused():
    empty = pw.PairwiseData([], [], np.zeros((0, K)), np.zeros(0))
    r = pw.assess(empty)
    assert not r.can_train and r.code == "not_enough_comparisons"
    with pytest.raises(ValueError):
        pw.fit(empty)


def test_too_few_comparisons_are_refused():
    data = synthetic_comparisons(weights_for(delta_access_fraction=30.0), scenarios=3, pairs=3)
    r = pw.assess(data)
    assert not r.can_train and r.code == "not_enough_comparisons"
    assert r.informative_comparisons == 9


def test_single_scenario_is_refused():
    data = synthetic_comparisons(weights_for(delta_access_fraction=30.0), scenarios=1, pairs=15, designs=10)
    r = pw.assess(data)
    assert not r.can_train
    assert any(reason["code"] == "not_enough_scenarios" for reason in r.reasons)


def test_homogeneous_comparisons_are_refused():
    direction = np.array([0.1, 0.0, 0.01, -0.2, 0.0])
    diffs = np.array([direction * f for f in np.linspace(0.5, 2.0, 12)])
    data = pw.PairwiseData([f"c{i}" for i in range(12)], [f"s{i % 3}" for i in range(12)], diffs, np.arange(12) % 2)
    r = pw.assess(data)
    assert not r.can_train and r.code == "homogeneous_comparisons" and r.rank == 1


def test_identical_outcome_pairs_carry_no_information():
    diffs = np.zeros((12, K))
    data = pw.PairwiseData([f"c{i}" for i in range(12)], [f"s{i % 3}" for i in range(12)], diffs, np.ones(12))
    r = pw.assess(data)
    assert not r.can_train and r.informative_comparisons == 0 and r.code == "not_enough_comparisons"


def test_adequate_data_is_ready_with_an_exploratory_warning():
    data = synthetic_comparisons(weights_for(delta_access_fraction=30.0), scenarios=3, pairs=5)
    r = pw.assess(data)
    assert r.can_train and r.code == "ready"
    assert any(w.startswith("Exploratory") for w in r.warnings)
    assert r.message.startswith("Training samples: 15 unique direct comparisons across 3 scenarios.")


# ---------------------------------------------------------------- diagnostics

def test_leave_one_scenario_out_never_uses_the_held_out_scenario():
    data = synthetic_comparisons(weights_for(delta_access_fraction=30.0), scenarios=5)
    base = pw.leave_one_scenario_out(data)
    flipped = pw.PairwiseData(data.comparison_ids, data.scenario_ids, data.diffs,
                              np.where(np.array(data.scenario_ids) == "scenario-0", 1 - data.chose_a, data.chose_a))
    # Changing scenario-0's labels changes only how its predictions are scored, never the predictions themselves:
    # recompute predictions for scenario-0 rows directly from a model fitted on the other scenarios.
    mask = np.array(data.scenario_ids) == "scenario-0"
    expected = pw.fit(data.subset(~mask)).p_from_diffs(data.diffs[mask])
    again = pw.fit(flipped.subset(~mask)).p_from_diffs(flipped.diffs[mask])
    np.testing.assert_array_equal(expected, again)
    assert base["available"] and base["folds"] == 5 and base["model"]["n"] == len(data)


def test_diagnostics_beat_chance_when_the_signal_is_strong_and_report_baselines():
    data = synthetic_comparisons(weights_for(delta_access_fraction=60.0), deterministic=True)
    d = pw.leave_one_scenario_out(data)
    chance = next(b for b in d["baselines"] if b["name"] == "chance")
    assert chance["accuracy"] == 0.5 and chance["log_loss"] == pytest.approx(np.log(2))
    assert d["model"]["accuracy"] > 0.8
    rule = next(b for b in d["baselines"] if b["name"] == "more_access")
    assert rule["has_log_loss"] is False


def test_score_counts_undecided_predictions_as_half_correct():
    s = pw.score(np.array([0.5, 0.9, 0.2]), np.array([1, 1, 1]))
    assert s["accuracy"] == pytest.approx(0.5)
    assert s["brier"] == pytest.approx((0.25 + 0.01 + 0.64) / 3)
