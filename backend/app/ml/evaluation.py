"""Scoring of a closed blinded evaluation (protocol blinded-eval-1.0.0).

Input: every trial of one plan with its sealed P(A) (frozen before the choice), the
feature differences of its two designs, the screen position and the recorded choice.
Output: exact counts, accuracy / Brier / log loss of the frozen model and of simple
baselines, scenario-clustered bootstrap intervals, position bias, repeat-choice
consistency, calibration bins, per-scenario rows and per-trial rows.

Only first presentations ("primary" trials) with an explicit A or B choice are scored.
Repeated pairs measure the person's consistency and are never counted as additional
unseen cases. Nothing here touches the database, so every rule is unit-testable.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Callable

import numpy as np
from scipy.stats import binomtest, norm

from app.ml import pairwise as pw

PROTOCOL_VERSION = "blinded-eval-1.0.0"
BOOTSTRAP_RESAMPLES = 1000
BOOTSTRAP_SEED = 20261025
INTERVAL_LEVEL = 0.95
MIN_SCENARIOS_FOR_INTERVALS = 3
SMALL_SAMPLE = 20
SUGGESTED_SCENARIOS = 5
LOW_CONSISTENCY = 0.75
CALIBRATION_EDGES = (0.5, 0.6, 0.7, 0.8, 0.9, 1.0)

EVALUATION_BASELINES = pw.BASELINES + (("left_position", "Always the design shown on the left", False),)

CAVEATS = [
    "All scenarios are synthetic and every choice comes from one person on this computer; the result says how well a "
    "statistical summary of that person's earlier choices predicted their later choices, nothing more.",
    "The model was frozen and every prediction was sealed before the choice was recorded; evaluation choices are never "
    "used for training.",
    "Repeated pairs measure how consistently the same person chooses; they are not extra unseen cases and are not scored.",
    "Intervals resample whole scenarios (choices within one city are not independent). With few scenarios they are "
    "rough, and a percentile bootstrap can be optimistic in small samples.",
    "Chance accuracy is 50% by construction (a 50% prediction counts as half correct). Brier 0.25 and log loss 0.693 "
    "are the matching chance reference values.",
]


@dataclass(frozen=True)
class TrialRecord:
    trial_id: str
    trial_index: int
    kind: str                       # primary | repeat
    scenario_id: str
    seed: int
    alt_a_id: str
    alt_b_id: str
    shown_left_is_a: bool
    p_a: float
    diffs: np.ndarray               # f(A) - f(B), features-v1
    choice: str | None              # A | B | tie | skip | None (never answered)
    repeat_of_trial_id: str | None
    comparison_id: str | None
    sealed_sha256: str
    registered_utc: str
    answered_utc: str | None
    revealed_utc: str | None


def _correct(p: np.ndarray, y: np.ndarray) -> np.ndarray:
    return np.where(p == 0.5, 0.5, ((p > 0.5) == (y == 1)).astype(float))


def _brier(p: np.ndarray, y: np.ndarray) -> float:
    return float(np.mean((p - y) ** 2))


def _log_loss(p: np.ndarray, y: np.ndarray) -> float:
    c = np.clip(p, 1e-15, 1 - 1e-15)
    return float(np.mean(-(y * np.log(c) + (1 - y) * np.log(1 - c))))


def _wilson(k: int, n: int, level: float = INTERVAL_LEVEL) -> list[float] | None:
    if n == 0:
        return None
    z = float(norm.ppf(0.5 + level / 2))
    phat = k / n
    denom = 1 + z * z / n
    centre = (phat + z * z / (2 * n)) / denom
    half = z * np.sqrt(phat * (1 - phat) / n + z * z / (4 * n * n)) / denom
    return [float(max(0.0, centre - half)), float(min(1.0, centre + half))]


def cluster_bootstrap(groups: list[str], statistic: Callable[[np.ndarray], dict[str, float]],
                      resamples: int = BOOTSTRAP_RESAMPLES, seed: int = BOOTSTRAP_SEED,
                      level: float = INTERVAL_LEVEL) -> dict[str, list[float]] | None:
    """Percentile intervals from resampling whole groups (scenarios) with replacement."""
    names = sorted(set(groups))
    if len(names) < MIN_SCENARIOS_FOR_INTERVALS:
        return None
    index = {g: np.flatnonzero(np.array(groups) == g) for g in names}
    rng = np.random.default_rng(seed)
    draws: dict[str, list[float]] = {}
    for _ in range(resamples):
        picked = rng.integers(0, len(names), size=len(names))
        idx = np.concatenate([index[names[i]] for i in picked])
        for key, value in statistic(idx).items():
            draws.setdefault(key, []).append(value)
    lo, hi = (1 - level) / 2 * 100, (1 + level) / 2 * 100
    return {key: [float(np.percentile(v, lo)), float(np.percentile(v, hi))] for key, v in draws.items()}


def _baseline_predictions(diffs: np.ndarray, left_is_a: np.ndarray) -> dict[str, np.ndarray]:
    preds = pw.baseline_predictions(diffs) if len(diffs) else {name: np.zeros(0) for name, _, _ in pw.BASELINES}
    preds["left_position"] = np.where(left_is_a, 1.0, 0.0)
    return preds


def evaluate(trials: list[TrialRecord], planned_scenarios: list[dict[str, Any]], pairs_per_scenario: int,
             repeat_count: int, model_version: str) -> dict[str, Any]:
    by_id = {t.trial_id: t for t in trials}
    primary = [t for t in trials if t.kind == "primary"]
    repeats = [t for t in trials if t.kind == "repeat"]
    answered = [t for t in trials if t.choice is not None]
    scored = [t for t in primary if t.choice in ("A", "B")]
    planned_primary = len(planned_scenarios) * pairs_per_scenario

    p = np.array([t.p_a for t in scored], dtype=float)
    y = np.array([1 if t.choice == "A" else 0 for t in scored], dtype=float)
    diffs = np.array([t.diffs for t in scored], dtype=float).reshape(-1, len(pw.FEATURE_NAMES))
    left_is_a = np.array([t.shown_left_is_a for t in scored], dtype=bool)
    groups = [t.scenario_id for t in scored]
    baselines = _baseline_predictions(diffs, left_is_a)

    counts = {
        "planned_scenarios": len(planned_scenarios),
        "planned_primary_pairs": planned_primary,
        "planned_repeats": repeat_count,
        "registered_trials": len(trials),
        "registered_primary": len(primary),
        "registered_repeats": len(repeats),
        "answered": len(answered),
        "unanswered": len(trials) - len(answered),
        "revealed": sum(1 for t in trials if t.revealed_utc),
        "scored": len(scored),
        "primary_ties": sum(1 for t in primary if t.choice == "tie"),
        "primary_skips": sum(1 for t in primary if t.choice == "skip"),
        "choices_for_a": int(y.sum()),
        "choices_for_b": int(len(y) - y.sum()),
        "scenarios_scored": len(set(groups)),
    }

    def metrics(idx: np.ndarray) -> dict[str, float]:
        pi, yi = p[idx], y[idx]
        model_acc = float(_correct(pi, yi).mean())
        out = {"model_accuracy": model_acc, "model_brier": _brier(pi, yi), "model_log_loss": _log_loss(pi, yi)}
        for name, _, _ in EVALUATION_BASELINES:
            acc = float(_correct(baselines[name][idx], yi).mean())
            out[f"{name}_accuracy"] = acc
            out[f"model_minus_{name}_accuracy"] = model_acc - acc
        return out

    intervals = cluster_bootstrap(groups, metrics) if len(scored) else None
    bootstrap_note = (
        f"{BOOTSTRAP_RESAMPLES} resamples of whole scenarios (seed {BOOTSTRAP_SEED}), {INTERVAL_LEVEL * 100:.0f}% percentile intervals."
        if intervals is not None else
        f"Intervals need scored choices from at least {MIN_SCENARIOS_FOR_INTERVALS} scenarios; none are reported.")

    def ci(key: str) -> list[float] | None:
        return intervals[key] if intervals is not None else None

    model_block: dict[str, Any] = {"model_version": model_version, **pw.score(p, y)}
    correct = _correct(p, y) if len(p) else np.zeros(0)
    model_block.update(correct=float(correct.sum()), undecided=int(np.sum(p == 0.5)),
                       accuracy_ci=ci("model_accuracy"), brier_ci=ci("model_brier"), log_loss_ci=ci("model_log_loss"))

    baseline_blocks = []
    for name, label, probabilistic in EVALUATION_BASELINES:
        b = baselines[name]
        block = {"name": name, "label": label, **pw.score(b, y, probabilistic)}
        block.update(correct=float(_correct(b, y).sum()) if len(y) else 0.0,
                     accuracy_ci=ci(f"{name}_accuracy"),
                     model_minus_baseline_accuracy=model_block["accuracy"] - block["accuracy"],
                     model_minus_baseline_accuracy_ci=ci(f"model_minus_{name}_accuracy"))
        baseline_blocks.append(block)

    # Position: every explicit A/B choice, first presentations and repeats alike.
    binary = [t for t in answered if t.choice in ("A", "B")]
    chose_left = sum(1 for t in binary if (t.choice == "A") == t.shown_left_is_a)
    position = {"n": len(binary), "chose_left": chose_left, "chose_right": len(binary) - chose_left,
                "left_share": chose_left / len(binary) if binary else 0.0,
                "binomial_p_value": float(binomtest(chose_left, len(binary), 0.5).pvalue) if binary else 1.0,
                "left_shown_as_a": int(left_is_a.sum()), "left_shown_as_b": int(len(left_is_a) - left_is_a.sum())}

    # Repeat consistency: same pair, sides swapped, asked again later.
    pairs = []
    for r in repeats:
        original = by_id.get(r.repeat_of_trial_id or "")
        if original is None:
            continue
        pairs.append((original, r))
    both = [(o, r) for o, r in pairs if o.choice is not None and r.choice is not None]
    binary_pairs = [(o, r) for o, r in both if o.choice in ("A", "B") and r.choice in ("A", "B")]
    agree = sum(1 for o, r in binary_pairs if o.choice == r.choice)
    same_side = sum(1 for o, r in binary_pairs
                    if ((o.choice == "A") == o.shown_left_is_a) == ((r.choice == "A") == r.shown_left_is_a))
    consistency = {
        "registered": len(repeats),
        "answered_both": len(both),
        "scored_pairs": len(binary_pairs),
        "agreements": agree,
        "disagreements": len(binary_pairs) - agree,
        "rate": agree / len(binary_pairs) if binary_pairs else None,
        "rate_ci": _wilson(agree, len(binary_pairs)),
        "rate_ci_method": f"Wilson score interval, {INTERVAL_LEVEL * 100:.0f}%",
        "chose_same_screen_side": same_side,
        "involving_tie_or_skip": len(both) - len(binary_pairs),
        "model_consistency_note": "The frozen model gives both presentations the same probability, so its own repeat "
                                  "consistency is 100% by construction; only the person's consistency is informative.",
    }

    # Calibration: confidence of the favoured design versus how often it was chosen.
    decided = p != 0.5
    confidence = np.maximum(p, 1 - p)[decided]
    favoured_chosen = ((p > 0.5) == (y == 1))[decided]
    bins = []
    ece = 0.0
    for k in range(len(CALIBRATION_EDGES) - 1):
        lo, hi = CALIBRATION_EDGES[k], CALIBRATION_EDGES[k + 1]
        mask = (confidence >= lo) & ((confidence < hi) if k < len(CALIBRATION_EDGES) - 2 else (confidence <= hi))
        n = int(mask.sum())
        mean_conf = float(confidence[mask].mean()) if n else None
        observed = float(favoured_chosen[mask].mean()) if n else None
        if n:
            ece += n / max(1, len(confidence)) * abs(observed - mean_conf)
        bins.append({"low": lo, "high": hi, "n": n, "mean_confidence": mean_conf, "observed_rate": observed})
    calibration = {"bins": bins, "expected_calibration_error": float(ece) if len(confidence) else None,
                   "n": int(len(confidence)),
                   "note": "Each bin groups scored choices by the frozen model's probability for the design it favoured; "
                           "observed_rate is how often that design was chosen. With few choices per bin this is noisy."}

    per_scenario = []
    for sc in planned_scenarios:
        sid = sc["scenario_id"]
        rows = [t for t in primary if t.scenario_id == sid]
        s_scored = [t for t in rows if t.choice in ("A", "B")]
        sp = np.array([t.p_a for t in s_scored], dtype=float)
        sy = np.array([1 if t.choice == "A" else 0 for t in s_scored], dtype=float)
        s_correct = float(_correct(sp, sy).sum()) if len(sp) else 0.0
        per_scenario.append({"scenario_id": sid, "seed": sc["seed"], "registered": len(rows), "scored": len(s_scored),
                             "correct": s_correct, "accuracy": s_correct / len(s_scored) if s_scored else None,
                             "ties": sum(1 for t in rows if t.choice == "tie"),
                             "skips": sum(1 for t in rows if t.choice == "skip"),
                             "unanswered": sum(1 for t in rows if t.choice is None)})

    trial_rows = [_trial_row(t) for t in sorted(trials, key=lambda t: t.trial_index)]

    warnings = _warnings(counts, model_block, baseline_blocks, consistency, position, intervals is not None)
    return {
        "counts": counts,
        "metrics": {"model": model_block, "baselines": baseline_blocks},
        "bootstrap": {"available": intervals is not None, "resamples": BOOTSTRAP_RESAMPLES if intervals else 0,
                      "seed": BOOTSTRAP_SEED, "level": INTERVAL_LEVEL, "clustered_by": "scenario", "note": bootstrap_note},
        "position": position,
        "repeat_consistency": consistency,
        "calibration": calibration,
        "per_scenario": per_scenario,
        "trials": trial_rows,
        "warnings": warnings,
        "caveats": CAVEATS,
        "summary": _summary(counts, model_block, baseline_blocks),
    }


def _trial_row(t: TrialRecord) -> dict[str, Any]:
    p_left = t.p_a if t.shown_left_is_a else 1 - t.p_a
    favoured = "neither" if t.p_a == 0.5 else ("A" if t.p_a > 0.5 else "B")
    chosen_side = None
    correct = None
    if t.choice in ("A", "B"):
        chosen_side = "left" if (t.choice == "A") == t.shown_left_is_a else "right"
        correct = 0.5 if favoured == "neither" else float(favoured == t.choice)
    row: dict[str, Any] = {
        "trial_id": t.trial_id, "trial_index": t.trial_index, "kind": t.kind, "scenario_id": t.scenario_id, "seed": t.seed,
        "alternative_a_id": t.alt_a_id, "alternative_b_id": t.alt_b_id, "shown_left": "A" if t.shown_left_is_a else "B",
        "p_a": t.p_a, "p_left": p_left, "model_favoured": favoured, "choice": t.choice, "chosen_side": chosen_side,
        "model_correct": correct, "scored": t.kind == "primary" and t.choice in ("A", "B"),
        "repeat_of_trial_id": t.repeat_of_trial_id, "comparison_id": t.comparison_id, "sealed_sha256": t.sealed_sha256,
        "registered_utc": t.registered_utc, "answered_utc": t.answered_utc, "revealed_utc": t.revealed_utc,
    }
    preds = _baseline_predictions(t.diffs.reshape(1, -1), np.array([t.shown_left_is_a]))
    for name, _, _ in EVALUATION_BASELINES:
        row[f"baseline_{name}_p_a"] = float(preds[name][0])
    for k, name in enumerate(pw.FEATURE_NAMES):
        row[f"diff_{name}"] = float(t.diffs[k])
    return row


def _pct(x: float) -> str:
    return f"{x * 100:.1f}%"


def _summary(counts: dict[str, Any], model: dict[str, Any], baselines: list[dict[str, Any]]) -> str:
    n = counts["scored"]
    if n == 0:
        return "No held-out choice was scored, so this evaluation has no result."
    correct = model["correct"]
    text = (f"The frozen model {model['model_version']} predicted {correct:g} of {n} held-out choices ({_pct(model['accuracy'])}) "
            f"across {counts['scenarios_scored']} unseen scenario(s)")
    if model["accuracy_ci"]:
        lo, hi = model["accuracy_ci"]
        text += f"; {INTERVAL_LEVEL * 100:.0f}% scenario-bootstrap interval {_pct(lo)} to {_pct(hi)}"
    rules = [b for b in baselines if b["name"] != "chance"]
    best = max(rules, key=lambda b: b["accuracy"])
    text += f". Chance: 50.0%. Best simple rule: {best['label'].lower()} {_pct(best['accuracy'])}."
    return text


def _warnings(counts, model, baselines, consistency, position, has_intervals) -> list[str]:
    out = []
    if counts["registered_primary"] < counts["planned_primary_pairs"]:
        out.append(f"The plan was closed after {counts['registered_primary']} of {counts['planned_primary_pairs']} "
                   "pre-registered pairs; the result covers only the pairs that were shown.")
    if counts["unanswered"]:
        out.append(f"{counts['unanswered']} registered trial(s) were never answered; they are listed but not scored.")
    if counts["scored"] == 0:
        out.append("No explicit A/B choice was recorded on a first presentation, so nothing could be scored.")
        return out
    if counts["scored"] < SMALL_SAMPLE:
        out.append(f"Only {counts['scored']} scored choice(s): one choice moves accuracy by "
                   f"{100 / counts['scored']:.1f} percentage points. Treat every number as exploratory.")
    if counts["scenarios_scored"] < SUGGESTED_SCENARIOS:
        out.append(f"Choices come from {counts['scenarios_scored']} scenario(s), fewer than the suggested {SUGGESTED_SCENARIOS}.")
    if not has_intervals:
        out.append(f"No interval is reported: that needs scored choices from at least {MIN_SCENARIOS_FOR_INTERVALS} scenarios.")
    elif model["accuracy_ci"][0] <= 0.5:
        out.append("The accuracy interval includes 50%, so this evaluation cannot distinguish the model from guessing.")
    rules = [b for b in baselines if b["name"] != "chance"]
    best = max(rules, key=lambda b: b["accuracy"])
    if best["accuracy"] >= model["accuracy"]:
        out.append(f"A simple rule ({best['label'].lower()}) was at least as accurate as the model on these choices.")
    if consistency["scored_pairs"] >= 3 and consistency["rate"] is not None and consistency["rate"] < LOW_CONSISTENCY:
        out.append(f"You chose differently in {consistency['disagreements']} of {consistency['scored_pairs']} repeated pairs. "
                   "Inconsistent choices limit how accurate any model of them can be; this is reported, not hidden.")
    if position["n"] >= 10 and position["binomial_p_value"] < 0.05:
        out.append(f"You chose the left design in {position['chose_left']} of {position['n']} choices (binomial p = "
                   f"{position['binomial_p_value']:.3f}), which suggests a screen-position effect.")
    return out
