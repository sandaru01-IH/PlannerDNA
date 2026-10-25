"""Personal pairwise preference model, version pairwise-logit-1.0.0.

For one explicit choice between feasible designs A and B the input is the feature
difference x = f(A) - f(B) (features-v1) and the label is 1 when A was chosen.
The model is P(A preferred to B) = sigmoid(w . x): logistic regression with no
intercept and L2 regularisation, fitted with scikit-learn.

* No intercept and a symmetric training set make P(A>B) + P(B>A) = 1 exactly.
* Each real comparison is also added reversed (-x with the opposite label). That
  balances the classes and keeps the fit symmetric; it is NOT extra evidence, and
  every reported count is of unique comparisons.
* Features are divided by a per-feature scale fitted on the training differences
  only (StandardScaler without centring, which keeps the sign symmetry). Scaling
  only changes how the L2 penalty treats each feature; reported raw weights are
  in the original fraction units.

Nothing here touches the database or the network, so every rule is unit-testable.
"""

from __future__ import annotations

from dataclasses import asdict, dataclass, field
from typing import Any
from warnings import catch_warnings, filterwarnings

import numpy as np
from scipy.optimize import OptimizeWarning
from scipy.special import expit
from sklearn.linear_model import LogisticRegression
from sklearn.preprocessing import StandardScaler

ML_VERSION = "pairwise-logit-1.0.0"
MODEL_KIND = "pairwise_logistic_l2_no_intercept"

FEATURE_NAMES: tuple[str, ...] = (
    "delta_access_fraction",
    "delta_housing_fraction",
    "delta_park_share_fraction",
    "negative_budget_fraction",
    "negative_lost_housing_fraction",
)
FEATURE_LABELS = {
    "delta_access_fraction": "Park access",
    "delta_housing_fraction": "Housing capacity",
    "delta_park_share_fraction": "Park share of land",
    "negative_budget_fraction": "Low spending",
    "negative_lost_housing_fraction": "Keeping original housing",
}
# What a higher value of each feature means, and its opposite, for plain-language statements.
HIGHER_MEANS = {
    "delta_access_fraction": ("more park access", "less park access"),
    "delta_housing_fraction": ("more housing capacity", "less housing capacity"),
    "delta_park_share_fraction": ("more park land", "less park land"),
    "negative_budget_fraction": ("lower cost", "higher cost"),
    "negative_lost_housing_fraction": ("fewer original housing units removed", "more original housing units removed"),
}

ZERO_TOLERANCE = 1e-12
# |standardised weight| below this is reported as "little or no association": a typical
# difference in that outcome changes the odds of a choice by less than about 10 % (e^0.1 = 1.105).
NEGLIGIBLE_STANDARDISED_WEIGHT = 0.1


@dataclass(frozen=True)
class Hyperparameters:
    C: float = 1.0                 # inverse L2 strength (scikit-learn convention)
    max_iter: int = 1000
    tolerance: float = 1e-8
    random_seed: int = 20261025
    bootstrap_resamples: int = 200
    interval_level: float = 0.9

    def to_dict(self) -> dict[str, Any]:
        d = asdict(self)
        d.update(solver="lbfgs", penalty="l2", fit_intercept=False, scaler="StandardScaler(with_mean=False) on training differences",
                 augmentation="each comparison added once as (x, y) and once as (-x, 1-y)")
        return d


@dataclass(frozen=True)
class Thresholds:
    """Heuristic data requirements. They are NOT statistical adequacy guarantees."""
    min_comparisons: int = 10        # at least two informative comparisons per feature
    min_scenarios: int = 2           # leave-one-scenario-out diagnostics need two
    min_rank: int = 2                # comparisons must vary along at least two independent directions
    suggested_comparisons: int = 30
    suggested_scenarios: int = 5

    def to_dict(self) -> dict[str, int]:
        return asdict(self)


@dataclass
class PairwiseData:
    comparison_ids: list[str]
    scenario_ids: list[str]
    diffs: np.ndarray        # shape (n, k): f(A) - f(B)
    chose_a: np.ndarray      # shape (n,): 1 if A was chosen, else 0

    def __post_init__(self) -> None:
        self.diffs = np.asarray(self.diffs, dtype=float).reshape(-1, len(FEATURE_NAMES))
        self.chose_a = np.asarray(self.chose_a, dtype=int).reshape(-1)
        if not (len(self.comparison_ids) == len(self.scenario_ids) == len(self.diffs) == len(self.chose_a)):
            raise ValueError("PairwiseData columns have different lengths.")
        if not np.all(np.isfinite(self.diffs)):
            raise ValueError("Feature differences must be finite.")
        if not set(np.unique(self.chose_a)).issubset({0, 1}):
            raise ValueError("Labels must be 0 or 1.")

    def __len__(self) -> int:
        return len(self.comparison_ids)

    @property
    def informative(self) -> np.ndarray:
        """Comparisons whose designs differ in at least one feature (others carry no information)."""
        return np.any(np.abs(self.diffs) > ZERO_TOLERANCE, axis=1)

    def subset(self, mask_or_index: np.ndarray) -> "PairwiseData":
        idx = np.flatnonzero(mask_or_index) if np.asarray(mask_or_index).dtype == bool else np.asarray(mask_or_index)
        return PairwiseData([self.comparison_ids[i] for i in idx], [self.scenario_ids[i] for i in idx],
                            self.diffs[idx], self.chose_a[idx])


def feature_vector(features: dict[str, Any]) -> np.ndarray:
    return np.array([float(features[name]) for name in FEATURE_NAMES], dtype=float)


@dataclass
class FittedPairwiseModel:
    weights: np.ndarray               # raw units: log-odds per unit of each feature difference
    weights_standardised: np.ndarray  # log-odds per one typical (RMS) training difference
    scale: np.ndarray
    varied: np.ndarray                # feature varied in at least one training comparison
    iterations: int
    converged: bool

    def utility(self, features: np.ndarray) -> float:
        return float(np.dot(self.weights, features))

    def p_prefer(self, features_a: np.ndarray, features_b: np.ndarray) -> float:
        return float(expit(np.dot(self.weights, features_a - features_b)))

    def p_from_diffs(self, diffs: np.ndarray) -> np.ndarray:
        return expit(np.asarray(diffs, dtype=float) @ self.weights)


def augment(diffs: np.ndarray, chose_a: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    return np.vstack([diffs, -diffs]), np.concatenate([chose_a, 1 - chose_a])


def fit_scale(diffs: np.ndarray) -> np.ndarray:
    augmented, _ = augment(diffs, np.zeros(len(diffs), dtype=int))
    return StandardScaler(with_mean=False).fit(augmented).scale_.copy()


def fit(data: PairwiseData, hp: Hyperparameters = Hyperparameters(), scale: np.ndarray | None = None) -> FittedPairwiseModel:
    if len(data) == 0:
        raise ValueError("Cannot fit a model without comparisons.")
    scale = fit_scale(data.diffs) if scale is None else np.asarray(scale, dtype=float)
    x, y = augment(data.diffs, data.chose_a)
    model = LogisticRegression(fit_intercept=False, C=hp.C, solver="lbfgs", max_iter=hp.max_iter, tol=hp.tolerance,
                               random_state=hp.random_seed)
    with catch_warnings():
        # scikit-learn 1.6 passes the deprecated lbfgs "iprint" option; SciPy 1.18 ignores it and warns.
        filterwarnings("ignore", message="Unknown solver options: iprint", category=OptimizeWarning)
        model.fit(x / scale, y)
    varied = np.any(np.abs(data.diffs) > ZERO_TOLERANCE, axis=0)
    w_std = np.where(varied, model.coef_[0], 0.0)
    iterations = int(model.n_iter_[0])
    return FittedPairwiseModel(weights=w_std / scale, weights_standardised=w_std, scale=scale, varied=varied,
                               iterations=iterations, converged=iterations < hp.max_iter)


# ---------------------------------------------------------------- readiness

@dataclass
class Readiness:
    can_train: bool
    code: str
    message: str
    reasons: list[dict[str, str]] = field(default_factory=list)
    warnings: list[str] = field(default_factory=list)
    eligible_comparisons: int = 0
    informative_comparisons: int = 0
    scenarios: int = 0
    rank: int = 0
    thresholds: dict[str, int] = field(default_factory=dict)

    def to_dict(self) -> dict[str, Any]:
        return asdict(self)


def _standardised(diffs: np.ndarray) -> np.ndarray:
    rms = np.sqrt(np.mean(diffs ** 2, axis=0)) if len(diffs) else np.ones(diffs.shape[1])
    return diffs / np.where(rms > ZERO_TOLERANCE, rms, 1.0)


def assess(data: PairwiseData, thresholds: Thresholds = Thresholds()) -> Readiness:
    informative = data.informative
    informative_diffs = data.diffs[informative]
    n_inf = int(informative.sum())
    scenarios = len({s for s, keep in zip(data.scenario_ids, informative) if keep})
    rank = int(np.linalg.matrix_rank(_standardised(informative_diffs), tol=1e-9)) if n_inf else 0

    reasons: list[dict[str, str]] = []
    if len(data) == 0:
        reasons.append({"code": "not_enough_comparisons", "message":
                        "No training-eligible comparisons yet. Only an explicit A or B choice between two feasible designs "
                        "in training mode counts; ties, skips, infeasible designs and evaluation choices are stored but not used."})
    elif n_inf < thresholds.min_comparisons:
        reasons.append({"code": "not_enough_comparisons", "message":
                        f"Not enough comparisons: {n_inf} informative of the {thresholds.min_comparisons} required "
                        f"(at least two per measured outcome)."})
    if 0 < len(data) and scenarios < thresholds.min_scenarios:
        reasons.append({"code": "not_enough_scenarios", "message":
                        f"Comparisons come from {scenarios} scenario(s); at least {thresholds.min_scenarios} are required so "
                        "the model can be checked on a scenario it was not fitted on."})
    if n_inf > 0 and rank < thresholds.min_rank:
        reasons.append({"code": "homogeneous_comparisons", "message":
                        "Your comparisons are too similar: every informative pair differs along the same single trade-off, "
                        "so the model cannot tell the measured outcomes apart. Compare designs that trade off different outcomes."})

    warnings: list[str] = []
    if len(data) - n_inf:
        warnings.append(f"{len(data) - n_inf} comparison(s) between designs with identical measured outcomes carry no "
                        "information for this model and are ignored.")
    if n_inf < thresholds.suggested_comparisons or scenarios < thresholds.suggested_scenarios:
        warnings.append(f"Exploratory: below the suggested {thresholds.suggested_comparisons} comparisons over "
                        f"{thresholds.suggested_scenarios} scenarios (a heuristic, not a statistical guarantee). "
                        "Weights may change substantially as you add choices.")
    never = [FEATURE_LABELS[name] for name, v in zip(FEATURE_NAMES, np.any(np.abs(data.diffs) > ZERO_TOLERANCE, axis=0)) if not v]
    if never and len(data):
        warnings.append("Never varied in your comparisons, so not estimated: " + ", ".join(never) + ".")

    can_train = not reasons
    summary = f"Training samples: {n_inf} unique direct comparisons across {scenarios} scenarios."
    return Readiness(
        can_train=can_train,
        code="ready" if can_train else reasons[0]["code"],
        message=summary + (" Ready to train." if can_train else " " + reasons[0]["message"]),
        reasons=reasons, warnings=warnings, eligible_comparisons=len(data), informative_comparisons=n_inf,
        scenarios=scenarios, rank=rank, thresholds=thresholds.to_dict(),
    )


# ---------------------------------------------------------------- scoring

def score(p: np.ndarray, y: np.ndarray, probabilistic: bool = True) -> dict[str, Any]:
    """Accuracy counts p = 0.5 as half correct. Log loss only for genuinely probabilistic predictors."""
    p = np.asarray(p, dtype=float)
    y = np.asarray(y, dtype=float)
    n = len(p)
    if n == 0:
        return {"n": 0, "accuracy": 0.0, "brier": 0.0, "log_loss": 0.0, "has_log_loss": False}
    correct = np.where(p == 0.5, 0.5, ((p > 0.5) == (y == 1)).astype(float))
    result = {"n": n, "accuracy": float(correct.mean()), "brier": float(np.mean((p - y) ** 2)),
              "log_loss": 0.0, "has_log_loss": probabilistic}
    if probabilistic:
        clipped = np.clip(p, 1e-15, 1 - 1e-15)
        result["log_loss"] = float(np.mean(-(y * np.log(clipped) + (1 - y) * np.log(1 - clipped))))
    return result


def _rule(values: np.ndarray) -> np.ndarray:
    return np.where(values > ZERO_TOLERANCE, 1.0, np.where(values < -ZERO_TOLERANCE, 0.0, 0.5))


BASELINES = (
    ("chance", "Chance (always 50 %)", True),
    ("cheaper", "Always the cheaper design", False),
    ("more_access", "Always the design with more park access", False),
    ("equal_weight", "Equal-weight sum of the five features", False),
)


def baseline_predictions(diffs: np.ndarray) -> dict[str, np.ndarray]:
    i_access, i_budget = FEATURE_NAMES.index("delta_access_fraction"), FEATURE_NAMES.index("negative_budget_fraction")
    return {
        "chance": np.full(len(diffs), 0.5),
        "cheaper": _rule(diffs[:, i_budget]),
        "more_access": _rule(diffs[:, i_access]),
        "equal_weight": _rule(diffs.sum(axis=1)),
    }


def leave_one_scenario_out(data: PairwiseData, hp: Hyperparameters = Hyperparameters()) -> dict[str, Any]:
    """Each scenario's comparisons are predicted by a model (and scaler) fitted on the other scenarios only."""
    scenarios = sorted(set(data.scenario_ids))
    note = ("Leave-one-scenario-out cross-validation inside the training data: every comparison is predicted by a model "
            "fitted without any comparison from its scenario. This is a diagnostic, not the blinded held-out evaluation "
            "of Phase 4.")
    if len(scenarios) < 2:
        return {"available": False, "note": "Needs comparisons from at least two scenarios.", "folds": 0, "skipped_folds": 0,
                "scenarios": len(scenarios), "model": score(np.array([]), np.array([])), "baselines": []}
    groups = np.array(data.scenario_ids)
    p = np.full(len(data), np.nan)
    skipped = 0
    for s in scenarios:
        test = groups == s
        train = data.subset(~test)
        if not np.any(train.informative):
            skipped += 1
            continue
        p[test] = fit(train, hp).p_from_diffs(data.diffs[test])
    done = ~np.isnan(p)
    y = data.chose_a[done]
    baselines = baseline_predictions(data.diffs[done])
    return {
        "available": bool(done.any()),
        "note": note,
        "folds": len(scenarios) - skipped,
        "skipped_folds": skipped,
        "scenarios": len({s for s, d in zip(data.scenario_ids, done) if d}),
        "model": score(p[done], y),
        "baselines": [dict(name=name, label=label, **score(baselines[name], y, probabilistic))
                      for name, label, probabilistic in BASELINES],
    }


def scenario_bootstrap(data: PairwiseData, scale: np.ndarray, hp: Hyperparameters = Hyperparameters()) -> dict[str, Any]:
    """Resamples whole scenarios (choices within a scenario are not independent) and refits with the fixed scale."""
    scenarios = sorted(set(data.scenario_ids))
    groups = {s: np.flatnonzero(np.array(data.scenario_ids) == s) for s in scenarios}
    rng = np.random.default_rng(hp.random_seed)
    draws = []
    for _ in range(hp.bootstrap_resamples):
        picked = rng.integers(0, len(scenarios), size=len(scenarios))
        idx = np.concatenate([groups[scenarios[i]] for i in picked])
        sample = data.subset(idx)
        if not np.any(sample.informative):
            continue
        draws.append(fit(sample, hp, scale=scale).weights_standardised)
    w = np.array(draws) if draws else np.zeros((0, len(FEATURE_NAMES)))
    lo_q, hi_q = (1 - hp.interval_level) / 2 * 100, (1 + hp.interval_level) / 2 * 100
    return {
        "resamples": len(draws),
        "low": np.percentile(w, lo_q, axis=0) if len(w) else np.zeros(len(FEATURE_NAMES)),
        "high": np.percentile(w, hi_q, axis=0) if len(w) else np.zeros(len(FEATURE_NAMES)),
        "draws": w,
    }


def coefficient_report(model: FittedPairwiseModel, bootstrap: dict[str, Any], hp: Hyperparameters) -> list[dict[str, Any]]:
    total = float(np.sum(np.abs(model.weights_standardised))) or 1.0
    draws = bootstrap["draws"]
    rows = []
    for i, name in enumerate(FEATURE_NAMES):
        w_std = float(model.weights_standardised[i])
        higher, lower = HIGHER_MEANS[name]
        sign = int(np.sign(w_std)) if abs(w_std) > ZERO_TOLERANCE else 0
        agreement = float(np.mean(np.sign(draws[:, i]) == sign)) if len(draws) and sign != 0 else 0.0
        low, high = float(bootstrap["low"][i]), float(bootstrap["high"][i])
        includes_zero = bootstrap["resamples"] == 0 or (low <= 0.0 <= high)
        if not model.varied[i]:
            direction, statement = "not_estimated", f"Not estimated: none of your training comparisons differed in {FEATURE_LABELS[name].lower()}."
        elif abs(w_std) < NEGLIGIBLE_STANDARDISED_WEIGHT:
            direction, statement = "negligible", f"Little or no association between {FEATURE_LABELS[name].lower()} and your choices."
        else:
            direction = "positive" if w_std > 0 else "negative"
            statement = (f"Your choices were associated with {higher if w_std > 0 else lower} "
                         f"(same sign in {agreement * 100:.0f}% of {bootstrap['resamples']} scenario resamples).")
            if includes_zero:
                statement += (f" The {hp.interval_level * 100:.0f}% interval includes zero, so even the direction "
                              "is uncertain.")
        rows.append({
            "feature": name,
            "label": FEATURE_LABELS[name],
            "weight_standardised": w_std,
            "weight_raw": float(model.weights[i]),
            "scale": float(model.scale[i]),
            "interval_low": low,
            "interval_high": high,
            "interval_includes_zero": bool(includes_zero),
            "interval_level": hp.interval_level,
            "sign_agreement": agreement,
            "relative_importance": abs(w_std) / total,
            "varied": bool(model.varied[i]),
            "direction": direction,
            "statement": statement,
        })
    return rows


def contributions(weights: np.ndarray, features_a: np.ndarray, features_b: np.ndarray) -> list[dict[str, Any]]:
    out = []
    for i, name in enumerate(FEATURE_NAMES):
        diff = float(features_a[i] - features_b[i])
        out.append({"feature": name, "label": FEATURE_LABELS[name], "a_value": float(features_a[i]),
                    "b_value": float(features_b[i]), "difference": diff, "weight_raw": float(weights[i]),
                    "contribution": float(weights[i] * diff)})
    return out
