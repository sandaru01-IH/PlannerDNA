"""Figures and plot-ready tables from a PlannerDNA research export.

Every number comes from the export (GET /api/v1/export, or the backup/export JSON file):
stored comparisons, model runs and closed blinded-evaluation reports. Some panels are
recomputed from the exported comparisons with the same pairwise module the backend uses
(model refit, bootstrap draws, learning curve); the refit is also compared with the
stored model weights as a reproducibility check. Nothing is simulated or invented.

Usage (from the backend folder, after: uv pip install -r requirements-analysis.txt):
    .venv\\Scripts\\python.exe -m tools.make_figures --export path\\to\\plannerdna-export.json --out figures
    .venv\\Scripts\\python.exe -m tools.make_figures --api http://127.0.0.1:8765 --out figures

Each figure is written as PNG and SVG with a CSV of exactly the plotted values, plus
figures_manifest.json (sources, counts, skipped figures and why) and captions.md.
"""

from __future__ import annotations

import argparse
import csv
import json
import sys
import urllib.parse
import urllib.request
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Callable

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402
import numpy as np  # noqa: E402
from matplotlib.ticker import MaxNLocator  # noqa: E402

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from app.ml import pairwise as pw  # noqa: E402

SCRIPT_VERSION = "figures-1.0.0"
MODEL_COLOUR = "#0f766e"
BASELINE_COLOURS = {"chance": "#9ca3af", "cheaper": "#b45309", "more_access": "#2563eb", "equal_weight": "#7c3aed",
                    "left_position": "#be185d"}
CHOICE_COLOURS = ("#0f766e", "#c2410c")
LOOPBACK = {"127.0.0.1", "localhost", "::1"}


# ---------------------------------------------------------------- loading

def load_export(path: str | None, api: str | None) -> tuple[dict[str, Any], str]:
    if path:
        return json.loads(Path(path).read_text(encoding="utf-8")), Path(path).name
    host = urllib.parse.urlparse(api).hostname
    if host not in LOOPBACK:
        raise SystemExit(f"Refusing non-loopback backend URL {api!r}; PlannerDNA data stays on this computer.")
    with urllib.request.urlopen(api.rstrip("/") + "/api/v1/export", timeout=30) as response:
        return json.loads(response.read().decode("utf-8")), f"{api.rstrip('/')}/api/v1/export"


def training_rows(bundle: dict[str, Any]) -> list[dict[str, Any]]:
    """Training-eligible comparisons exactly as the backend selects them, oldest first."""
    feature_version = bundle["export_manifest"]["feature_version"]
    alts = {a["id"]: a for a in bundle["alternatives"]}
    rows = []
    for c in bundle["comparisons"]:
        if not (c["training_eligible"] and c["mode"] == "train" and c["choice"] in ("A", "B")
                and c["feature_version"] == feature_version):
            continue
        fa, fb = alts[c["alt_a_id"]]["features_json"], alts[c["alt_b_id"]]["features_json"]
        rows.append({"comparison_id": c["comparison_id"], "scenario_id": c["scenario_id"], "created_utc": c["created_utc"],
                     "choice": c["choice"], "diffs": pw.feature_vector(fa) - pw.feature_vector(fb)})
    rows.sort(key=lambda r: (r["created_utc"], r["comparison_id"]))
    return rows


def to_data(rows: list[dict[str, Any]]) -> pw.PairwiseData:
    return pw.PairwiseData([r["comparison_id"] for r in rows], [r["scenario_id"] for r in rows],
                           np.array([r["diffs"] for r in rows]).reshape(-1, len(pw.FEATURE_NAMES)),
                           np.array([1 if r["choice"] == "A" else 0 for r in rows], dtype=int))


def model_data(bundle: dict[str, Any], run: dict[str, Any]) -> pw.PairwiseData:
    by_id = {r["comparison_id"]: r for r in training_rows(bundle)}
    ids = run["training_comparison_ids_json"]
    missing = [i for i in ids if i not in by_id]
    if missing:
        raise KeyError(f"{len(missing)} training comparison(s) of {run['id']} are not in this export.")
    return to_data([by_id[i] for i in ids])


# ---------------------------------------------------------------- output helpers

class Writer:
    def __init__(self, out: Path, source: str, bundle: dict[str, Any], formats: list[str], clean: bool = False):
        self.out = out
        self.source = source
        self.formats = formats
        self.clean = clean
        self.exported = bundle["export_manifest"].get("exported_utc", "unknown")
        reasons = [c.get("reason", "") for c in bundle["comparisons"]]
        self.synthetic = sum(1 for r in reasons if "synthetic" in r.lower())
        self.total_comparisons = len(reasons)
        self.figures: list[dict[str, Any]] = []
        self.skipped: list[dict[str, str]] = []
        out.mkdir(parents=True, exist_ok=True)

    def table(self, name: str, header: list[str], rows: list[list[Any]]) -> str:
        path = self.out / f"{name}.csv"
        with path.open("w", newline="", encoding="utf-8") as fh:
            writer = csv.writer(fh)
            writer.writerow(header)
            writer.writerows(rows)
        return path.name

    def save(self, fig: plt.Figure, name: str, title: str, caption: str, table: str, detail: str) -> None:
        if self.clean:
            # Presentation mode: no diagonal watermark, but the share of rule-made labels is still stated.
            footer = "PlannerDNA" + (f" | demo data: {self.synthetic} of {self.total_comparisons} choices were made by a fixed rule, "
                                     "not a person" if self.synthetic else "")
        else:
            footer = f"PlannerDNA | {detail} | source: {self.source} (exported {self.exported[:19]}) | {SCRIPT_VERSION}"
        fig.text(0.01, 0.005, footer, fontsize=6.5, color="#6b7280", ha="left", va="bottom")
        if self.synthetic and not self.clean:
            fig.text(0.5, 0.5, "SYNTHETIC TEST LABELS", fontsize=40, color="#b91c1c", alpha=0.10, rotation=24,
                     ha="center", va="center", weight="bold")
        files = []
        for fmt in self.formats:
            path = self.out / f"{name}.{fmt}"
            fig.savefig(path, dpi=200, bbox_inches="tight")
            files.append(path.name)
        plt.close(fig)
        self.figures.append({"id": name, "title": title, "files": files, "data": table, "caption": caption, "detail": detail})

    def skip(self, name: str, reason: str) -> None:
        self.skipped.append({"id": name, "reason": reason})


def _style() -> None:
    plt.rcParams.update({"font.size": 9, "axes.spines.top": False, "axes.spines.right": False, "axes.titlesize": 10,
                         "axes.titleweight": "bold", "figure.dpi": 100, "savefig.facecolor": "white"})


def _ci_err(value: float, ci: list[float] | None) -> list[list[float]]:
    if not ci:
        return [[0.0], [0.0]]
    return [[max(0.0, value - ci[0])], [max(0.0, ci[1] - value)]]


def _fmt_ci(ci: list[float] | None, pct: bool = True) -> str:
    if not ci:
        return "n/a"
    return f"[{ci[0] * 100:.1f}%, {ci[1] * 100:.1f}%]" if pct else f"[{ci[0]:.3f}, {ci[1]:.3f}]"


# ---------------------------------------------------------------- training-data figures

def fig_training_overview(w: Writer, bundle: dict[str, Any]) -> None:
    rows = training_rows(bundle)
    if not rows:
        return w.skip("fig01_training_data", "No training-eligible comparisons in this export.")
    scenarios = sorted({r["scenario_id"] for r in rows})
    a = [sum(1 for r in rows if r["scenario_id"] == s and r["choice"] == "A") for s in scenarios]
    b = [sum(1 for r in rows if r["scenario_id"] == s and r["choice"] == "B") for s in scenarios]
    comps = bundle["comparisons"]
    excluded = {"Tie or skip": sum(1 for c in comps if c["choice"] in ("tie", "skip")),
                "Evaluation (blinded)": sum(1 for c in comps if c["mode"] == "evaluation"),
                "Infeasible pair": sum(1 for c in comps if c["mode"] == "train" and c["choice"] in ("A", "B") and not c["training_eligible"])}
    table = w.table("fig01_training_data", ["scenario_id", "chose_a", "chose_b", "total"],
                    [[s, x, y, x + y] for s, x, y in zip(scenarios, a, b)] + [[f"excluded: {k}", "", "", v] for k, v in excluded.items()])
    fig, (ax1, ax2) = plt.subplots(1, 2, figsize=(10, 3.8), gridspec_kw={"width_ratios": [3, 1.3]})
    x = np.arange(len(scenarios))
    ax1.bar(x, a, color=CHOICE_COLOURS[0], label="Chose A")
    ax1.bar(x, b, bottom=a, color=CHOICE_COLOURS[1], label="Chose B")
    ax1.set_xticks(x, scenarios, rotation=45, ha="right", fontsize=7)
    ax1.set_ylabel("Unique comparisons")
    ax1.set_title(f"Training comparisons per scenario (n = {len(rows)}, {len(scenarios)} scenarios)")
    ax1.legend(frameon=False)
    ax2.barh(list(excluded), list(excluded.values()), color="#9ca3af")
    ax2.set_title("Stored but never trained on")
    ax2.set_xlabel("Decisions")
    for i, v in enumerate(excluded.values()):
        ax2.text(v, i, f" {v}", va="center", fontsize=8)
    fig.tight_layout()
    w.save(fig, "fig01_training_data", "Training data per scenario",
           f"Training-eligible comparisons (explicit A/B choices between two feasible designs, train mode) per synthetic "
           f"scenario: {len(rows)} comparisons across {len(scenarios)} scenarios. Right: stored decisions excluded from "
           f"training ({', '.join(f'{k.lower()}: {v}' for k, v in excluded.items())}).", table, f"n = {len(rows)} comparisons")


def fig_feature_space(w: Writer, bundle: dict[str, Any]) -> None:
    rows = training_rows(bundle)
    if len(rows) < 2:
        return w.skip("fig02_tradeoff_space", "Needs at least two training comparisons.")
    ia, ih, ib = (pw.FEATURE_NAMES.index(n) for n in ("delta_access_fraction", "delta_housing_fraction", "negative_budget_fraction"))
    # Orient every pair as chosen minus rejected, so the plot shows what the chosen design gained or gave up.
    oriented = np.array([r["diffs"] if r["choice"] == "A" else -r["diffs"] for r in rows])
    table = w.table("fig02_tradeoff_space", ["comparison_id", "scenario_id"] + [f"chosen_minus_rejected_{n}" for n in pw.FEATURE_NAMES],
                    [[r["comparison_id"], r["scenario_id"]] + list(map(float, d)) for r, d in zip(rows, oriented)])
    fig, axes = plt.subplots(1, 2, figsize=(10, 4.2))
    for ax, (j, label) in zip(axes, ((ih, "Housing capacity difference (fraction of baseline)"),
                                     (ib, "Low-spending difference (fraction of budget)"))):
        ax.axhline(0, color="#d1d5db", lw=0.8)
        ax.axvline(0, color="#d1d5db", lw=0.8)
        ax.scatter(oriented[:, ia], oriented[:, j], s=22, color=MODEL_COLOUR, alpha=0.75, edgecolor="white", lw=0.4)
        ax.set_xlabel("Park access difference (fraction of housing units)")
        ax.set_ylabel(label)
    q = [int(np.sum((oriented[:, ia] > 0) & (oriented[:, ih] < 0))), int(np.sum((oriented[:, ia] < 0) & (oriented[:, ih] > 0)))]
    axes[0].set_title("Chosen minus rejected: access vs housing")
    axes[1].set_title("Chosen minus rejected: access vs spending")
    fig.tight_layout()
    w.save(fig, "fig02_tradeoff_space", "Trade-offs in the choices",
           f"Each point is one training comparison, oriented as the chosen design minus the rejected one. Points right of zero "
           f"chose more park access. In {q[0]} comparison(s) more access was chosen at the cost of housing capacity; in {q[1]} "
           "housing was chosen at the cost of access.", table, f"n = {len(rows)} comparisons")


# ---------------------------------------------------------------- model figures

def _active_run(bundle: dict[str, Any], version: str | None) -> dict[str, Any] | None:
    runs = bundle["model_runs"]
    if version:
        return next((r for r in runs if r["id"] == version), None)
    return next((r for r in runs if r["status"] == "active"), runs[-1] if runs else None)


def reproducibility_check(bundle: dict[str, Any], run: dict[str, Any]) -> dict[str, Any]:
    summary = run["summary_json"]
    stored = np.array([c["weight_raw"] for c in summary["coefficients"]])
    try:
        data = model_data(bundle, run)
    except KeyError as exc:
        return {"model_version": run["id"], "checked": False, "note": str(exc)}
    refit = pw.fit(data, pw.Hyperparameters())
    diff = float(np.max(np.abs(refit.weights - stored)))
    return {"model_version": run["id"], "checked": True, "comparisons": len(data), "max_abs_weight_difference": diff,
            "reproduced": diff < 1e-6,
            "note": "Refit from the exported comparisons with the same pairwise module and default hyperparameters."}


def fig_coefficients(w: Writer, bundle: dict[str, Any], run: dict[str, Any] | None) -> None:
    if run is None:
        return w.skip("fig03_coefficients", "No trained model in this export.")
    s = run["summary_json"]
    coefs = s["coefficients"]
    level = coefs[0]["interval_level"]
    table = w.table("fig03_coefficients", ["feature", "label", "weight_standardised", "interval_low", "interval_high",
                                           "interval_level", "sign_agreement", "weight_raw", "direction", "varied"],
                    [[c["feature"], c["label"], c["weight_standardised"], c["interval_low"], c["interval_high"], level,
                      c["sign_agreement"], c["weight_raw"], c["direction"], c["varied"]] for c in coefs])
    fig, ax = plt.subplots(figsize=(7.5, 3.6))
    y = np.arange(len(coefs))[::-1]
    for yi, c in zip(y, coefs):
        colour = MODEL_COLOUR if c["varied"] and not c["interval_includes_zero"] else "#9ca3af"
        ax.plot([c["interval_low"], c["interval_high"]], [yi, yi], color=colour, lw=2.2, solid_capstyle="round")
        ax.plot(c["weight_standardised"], yi, "o", color=colour, ms=7)
        ax.text(max(c["interval_high"], c["weight_standardised"]) + 0.05, yi, f"{c['sign_agreement'] * 100:.0f}% same sign",
                va="center", fontsize=7, color="#6b7280")
    ax.axvline(0, color="#374151", lw=0.8, ls="--")
    ax.set_yticks(y, [c["label"] for c in coefs])
    ax.set_xlabel("Standardised weight (log-odds per typical training difference)")
    ax.set_title(f"Model {run['id']}: weights with {level * 100:.0f}% scenario-bootstrap intervals")
    fig.tight_layout()
    w.save(fig, "fig03_coefficients", "Fitted weights with uncertainty",
           f"Standardised weights of the pairwise logistic model {run['id']} (trained on {s['unique_comparisons']} comparisons "
           f"across {s['scenario_count']} scenarios) with {level * 100:.0f}% percentile intervals from "
           f"{s['hyperparameters']['bootstrap_resamples']} scenario-bootstrap refits. Grey: interval includes zero or "
           "feature never varied. Weights are associations with explicit choices, not motives.", table,
           f"model {run['id']}, n = {s['unique_comparisons']}")


def fig_bootstrap(w: Writer, bundle: dict[str, Any], run: dict[str, Any] | None) -> None:
    if run is None:
        return w.skip("fig04_bootstrap_draws", "No trained model in this export.")
    try:
        data = model_data(bundle, run)
    except KeyError as exc:
        return w.skip("fig04_bootstrap_draws", str(exc))
    hp = pw.Hyperparameters()
    model = pw.fit(data, hp)
    boot = pw.scenario_bootstrap(data, model.scale, hp)
    draws = boot["draws"]
    if len(draws) == 0:
        return w.skip("fig04_bootstrap_draws", "No bootstrap resample contained informative comparisons.")
    table = w.table("fig04_bootstrap_draws", ["resample"] + list(pw.FEATURE_NAMES),
                    [[i] + list(map(float, d)) for i, d in enumerate(draws)])
    fig, ax = plt.subplots(figsize=(8, 3.8))
    parts = ax.violinplot([draws[:, k] for k in range(draws.shape[1])], positions=range(draws.shape[1]), showmedians=True)
    for body in parts["bodies"]:
        body.set_facecolor(MODEL_COLOUR)
        body.set_alpha(0.35)
    ax.plot(range(draws.shape[1]), model.weights_standardised, "D", color="#111827", ms=5, label="Fit on all comparisons")
    ax.axhline(0, color="#374151", lw=0.8, ls="--")
    ax.set_xticks(range(draws.shape[1]), [pw.FEATURE_LABELS[n] for n in pw.FEATURE_NAMES], rotation=15)
    ax.set_ylabel("Standardised weight")
    ax.set_title(f"Scenario-bootstrap distribution of weights ({len(draws)} refits, recomputed)")
    ax.legend(frameon=False, fontsize=8)
    fig.tight_layout()
    w.save(fig, "fig04_bootstrap_draws", "Bootstrap distribution of weights",
           f"Distribution of standardised weights over {len(draws)} refits on scenario-resampled training data (seed "
           f"{hp.random_seed}, recomputed from the export with the backend's pairwise module). Diamonds: fit on all "
           f"{len(data)} comparisons. Wide or zero-straddling violins mean the direction is uncertain.", table,
           f"model {run['id']}, {len(draws)} resamples")


def _score_rows(diag: dict[str, Any]) -> list[tuple[str, str, dict[str, Any]]]:
    rows = [("model", "Your model (scenario held out)", diag["model"])]
    rows += [(b["name"], b["label"], b) for b in diag["baselines"]]
    return rows


def _three_metric_panels(w: Writer, name: str, title: str, rows: list[tuple[str, str, dict[str, Any]]],
                         cis: dict[str, list[float] | None] | None = None) -> plt.Figure:
    fig, axes = plt.subplots(1, 3, figsize=(11, 3.6))
    labels = [r[1] for r in rows]
    colours = [MODEL_COLOUR if r[0] == "model" else BASELINE_COLOURS.get(r[0], "#9ca3af") for r in rows]
    y = np.arange(len(rows))[::-1]
    for ax, metric, ref, better in ((axes[0], "accuracy", 0.5, "higher is better"), (axes[1], "brier", 0.25, "lower is better"),
                                    (axes[2], "log_loss", float(np.log(2)), "lower is better")):
        values = [r[2][metric] if (metric != "log_loss" or r[2].get("has_log_loss", True)) else np.nan for r in rows]
        ax.barh(y, values, color=colours, height=0.6)
        label_x = list(values)
        if cis and metric in cis:
            for i, (yi, r, v) in enumerate(zip(y, rows, values)):
                ci = cis[metric].get(r[0]) if cis[metric] else None
                if ci:
                    ax.errorbar(v, yi, xerr=_ci_err(v, ci), fmt="none", ecolor="#111827", capsize=3, lw=1)
                    label_x[i] = max(v, ci[1])
        ax.axvline(ref, color="#374151", lw=0.8, ls="--")
        for yi, v, lx in zip(y, values, label_x):
            if not np.isnan(v):
                ax.text(lx, yi, f"  {v * 100:.1f}%" if metric == "accuracy" else f"  {v:.3f}", va="center", fontsize=7)
        ax.set_title(f"{metric.replace('_', ' ').title()} ({better})")
        ax.set_yticks(y, labels if ax is axes[0] else [""] * len(rows), fontsize=7.5)
    axes[0].set_xlim(0, 1.12)
    fig.suptitle(title, fontweight="bold", fontsize=10)
    fig.tight_layout()
    return fig


def fig_loso(w: Writer, run: dict[str, Any] | None) -> None:
    if run is None:
        return w.skip("fig05_loso_vs_baselines", "No trained model in this export.")
    diag = run["summary_json"].get("diagnostics", {})
    if not diag.get("available"):
        return w.skip("fig05_loso_vs_baselines", diag.get("note", "Diagnostics unavailable for this model."))
    rows = _score_rows(diag)
    table = w.table("fig05_loso_vs_baselines", ["predictor", "label", "n", "accuracy", "brier", "log_loss", "has_log_loss"],
                    [[k, label, r["n"], r["accuracy"], r["brier"], r["log_loss"], r.get("has_log_loss", True)] for k, label, r in rows])
    fig = _three_metric_panels(w, "fig05", f"Leave-one-scenario-out diagnostic for {run['id']} (not a blinded test)", rows)
    m = diag["model"]
    w.save(fig, "fig05_loso_vs_baselines", "Cross-validated diagnostic versus baselines",
           f"Leave-one-scenario-out cross-validation inside the training data ({diag['folds']} folds, n = {m['n']}): the model "
           f"scored accuracy {m['accuracy'] * 100:.1f}%, Brier {m['brier']:.3f}, log loss {m['log_loss']:.3f}. Dashed lines: "
           "chance reference values (50%, 0.25, 0.693). Rule baselines are deterministic, so they have no log loss. This is a "
           "diagnostic, not the blinded evaluation.", table, f"model {run['id']}, n = {m['n']}")


def fig_history(w: Writer, bundle: dict[str, Any]) -> None:
    runs = sorted(bundle["model_runs"], key=lambda r: r["trained_utc"])
    if len(runs) < 2:
        return w.skip("fig06_model_history", f"Needs at least two trained model versions (export has {len(runs)}).")
    header = ["model_version", "trained_utc", "status", "comparisons", "scenarios", "loso_accuracy", "loso_n"] + \
             [f"w_std_{n}" for n in pw.FEATURE_NAMES]
    rows = []
    for r in runs:
        s = r["summary_json"]
        d = s.get("diagnostics", {})
        rows.append([r["id"], r["trained_utc"], r["status"], s["unique_comparisons"], s["scenario_count"],
                     d.get("model", {}).get("accuracy") if d.get("available") else "", d.get("model", {}).get("n", 0)]
                    + [c["weight_standardised"] for c in s["coefficients"]])
    table = w.table("fig06_model_history", header, rows)
    fig, (ax1, ax2) = plt.subplots(1, 2, figsize=(11, 3.8))
    n = [r[3] for r in rows]
    acc = [r[5] if r[5] != "" else np.nan for r in rows]
    ax1.plot(n, acc, "o-", color=MODEL_COLOUR)
    ax1.axhline(0.5, color="#374151", lw=0.8, ls="--")
    ax1.set_ylim(0, 1.05)
    ax1.set_xlabel("Training comparisons in model version")
    ax1.set_ylabel("Leave-one-scenario-out accuracy")
    ax1.set_title("Diagnostic accuracy as data grew")
    for k, name in enumerate(pw.FEATURE_NAMES):
        ax2.plot(n, [r[7 + k] for r in rows], "o-", label=pw.FEATURE_LABELS[name], ms=4)
    ax2.axhline(0, color="#374151", lw=0.8, ls="--")
    ax2.set_xlabel("Training comparisons in model version")
    ax2.set_ylabel("Standardised weight")
    ax2.set_title("Weights across model versions")
    ax2.legend(frameon=False, fontsize=7)
    fig.tight_layout()
    w.save(fig, "fig06_model_history", "Model versions over time",
           f"{len(runs)} stored model versions. Left: leave-one-scenario-out accuracy of each version against its training size. "
           "Right: how each standardised weight moved as comparisons were added.", table, f"{len(runs)} model versions")


def fig_learning_curve(w: Writer, bundle: dict[str, Any]) -> None:
    rows = training_rows(bundle)
    th = pw.Thresholds()
    if len(rows) < th.min_comparisons + 2:
        return w.skip("fig07_learning_curve", f"Needs at least {th.min_comparisons + 2} training comparisons (export has {len(rows)}).")
    step = max(1, len(rows) // 12)
    sizes = list(range(th.min_comparisons, len(rows) + 1, step))
    if sizes[-1] != len(rows):
        sizes.append(len(rows))
    out = []
    for n in sizes:
        data = to_data(rows[:n])
        data = data.subset(data.informative)
        if len(set(data.scenario_ids)) < 2:
            continue
        loso = pw.leave_one_scenario_out(data)
        if not loso["available"]:
            continue
        b = {x["name"]: x["accuracy"] for x in loso["baselines"]}
        out.append([n, len(set(data.scenario_ids)), loso["model"]["n"], loso["model"]["accuracy"], loso["model"]["brier"],
                    b["more_access"], b["cheaper"], b["equal_weight"]])
    if len(out) < 2:
        return w.skip("fig07_learning_curve", "Too few prefixes with two or more scenarios for a curve.")
    table = w.table("fig07_learning_curve", ["first_n_comparisons", "scenarios", "scored", "model_accuracy", "model_brier",
                                             "more_access_accuracy", "cheaper_accuracy", "equal_weight_accuracy"], out)
    arr = np.array(out, dtype=float)
    fig, ax = plt.subplots(figsize=(7.5, 3.8))
    ax.plot(arr[:, 0], arr[:, 3], "o-", color=MODEL_COLOUR, label="Your model (scenario held out)")
    for col, key, label in ((5, "more_access", "More park access"), (6, "cheaper", "Cheaper design"), (7, "equal_weight", "Equal weights")):
        ax.plot(arr[:, 0], arr[:, col], "--", color=BASELINE_COLOURS[key], label=label, lw=1.2)
    ax.axhline(0.5, color="#9ca3af", lw=0.8, ls=":")
    ax.set_ylim(0, 1.05)
    ax.set_xlabel("First n training comparisons (in recorded order)")
    ax.set_ylabel("Leave-one-scenario-out accuracy")
    ax.set_title("Learning curve (recomputed diagnostic)")
    ax.legend(frameon=False, fontsize=7.5)
    fig.tight_layout()
    w.save(fig, "fig07_learning_curve", "Learning curve",
           f"Leave-one-scenario-out accuracy when the model is fitted on only the first n training comparisons (recomputed from "
           f"the export, n = {int(arr[0, 0])} to {int(arr[-1, 0])}), compared with fixed rules. Early points rest on few "
           "scenarios and are noisy.", table, f"{len(rows)} comparisons")


# ---------------------------------------------------------------- blinded evaluation figures

def closed_reports(bundle: dict[str, Any]) -> list[dict[str, Any]]:
    return [p["report_json"] for p in bundle.get("evaluation_plans", []) if p["status"] == "closed" and p.get("report_json")]


def fig_evaluation(w: Writer, report: dict[str, Any], tag: str) -> None:
    name = f"fig08_evaluation_vs_baselines{tag}"
    m = report["metrics"]["model"]
    if m["n"] == 0:
        return w.skip(name, "The closed evaluation has no scored choice.")
    rows = [("model", f"Frozen model\n{m['model_version']}", m)] + [(b["name"], b["label"], b) for b in report["metrics"]["baselines"]]
    cis = {"accuracy": {"model": m.get("accuracy_ci"), **{b["name"]: b.get("accuracy_ci") for b in report["metrics"]["baselines"]}},
           "brier": {"model": m.get("brier_ci")}, "log_loss": {"model": m.get("log_loss_ci")}}
    table = w.table(name, ["predictor", "label", "n", "accuracy", "accuracy_ci_low", "accuracy_ci_high", "brier", "log_loss", "has_log_loss"],
                    [[k, label.replace("\n", " "), r["n"], r["accuracy"], *(r.get("accuracy_ci") or ["", ""]), r["brier"], r["log_loss"],
                      r.get("has_log_loss", True)] for k, label, r in rows])
    level = report["bootstrap"]["level"]
    fig = _three_metric_panels(w, name, f"Blinded held-out evaluation {report['plan']['plan_id']} "
                                        f"({report['counts']['scored']} choices, {report['counts']['scenarios_scored']} unseen scenarios)", rows, cis)
    w.save(fig, name, "Blinded evaluation versus baselines",
           f"{report['summary']} Error bars: {level * 100:.0f}% scenario-clustered bootstrap intervals "
           f"({report['bootstrap']['resamples']} resamples) where at least three scenarios were scored. Predictions were sealed "
           "before each choice; evaluation choices were never used for training.", table,
           f"plan {report['plan']['plan_id']}, n = {m['n']}")


def fig_evaluation_difference(w: Writer, report: dict[str, Any], tag: str) -> None:
    name = f"fig09_model_minus_baselines{tag}"
    m = report["metrics"]["model"]
    if m["n"] == 0:
        return w.skip(name, "The closed evaluation has no scored choice.")
    bs = report["metrics"]["baselines"]
    table = w.table(name, ["baseline", "label", "model_minus_baseline_accuracy", "ci_low", "ci_high"],
                    [[b["name"], b["label"], b["model_minus_baseline_accuracy"], *(b.get("model_minus_baseline_accuracy_ci") or ["", ""])] for b in bs])
    fig, ax = plt.subplots(figsize=(7.5, 3.2))
    y = np.arange(len(bs))[::-1]
    for yi, b in zip(y, bs):
        v = b["model_minus_baseline_accuracy"]
        ci = b.get("model_minus_baseline_accuracy_ci")
        ax.errorbar(v, yi, xerr=_ci_err(v, ci) if ci else None, fmt="o", color=BASELINE_COLOURS.get(b["name"], "#6b7280"), capsize=3)
        ax.text(v, yi + 0.18, f"{v * 100:+.1f} pts", ha="center", fontsize=7)
    ax.axvline(0, color="#374151", lw=0.8, ls="--")
    ax.set_yticks(y, [b["label"] for b in bs], fontsize=8)
    ax.set_xlabel("Model accuracy minus baseline accuracy")
    ax.set_title("Does the frozen model beat simple rules on unseen scenarios?")
    fig.tight_layout()
    w.save(fig, name, "Model minus baselines",
           "Paired accuracy difference between the frozen model and each baseline on the same held-out choices, with "
           f"{report['bootstrap']['level'] * 100:.0f}% scenario-clustered bootstrap intervals (when available). Intervals "
           "crossing zero mean no detectable difference.", table, f"plan {report['plan']['plan_id']}, n = {m['n']}")


def fig_calibration(w: Writer, report: dict[str, Any], tag: str) -> None:
    name = f"fig10_calibration{tag}"
    cal = report["calibration"]
    if cal["n"] == 0:
        return w.skip(name, "No scored choice with a decisive prediction.")
    bins = cal["bins"]
    table = w.table(name, ["bin_low", "bin_high", "n", "mean_confidence", "observed_rate"],
                    [[b["low"], b["high"], b["n"], b["mean_confidence"], b["observed_rate"]] for b in bins])
    fig, (ax1, ax2) = plt.subplots(2, 1, figsize=(5.5, 6), gridspec_kw={"height_ratios": [3, 1]}, sharex=True)
    ax1.plot([0.5, 1], [0.5, 1], ls="--", color="#9ca3af", label="Perfect calibration")
    pts = [(b["mean_confidence"], b["observed_rate"], b["n"]) for b in bins if b["n"]]
    ax1.plot([p[0] for p in pts], [p[1] for p in pts], "o-", color=MODEL_COLOUR, label="Frozen model")
    for x, yv, n in pts:
        ax1.text(x, yv, f"  n={n}", fontsize=7, va="center")
    ax1.set_ylim(0, 1.05)
    ax1.set_ylabel("How often the favoured design was chosen")
    ece = cal["expected_calibration_error"]
    ax1.set_title(f"Reliability on held-out choices (ECE {ece:.3f})" if ece is not None else "Reliability on held-out choices")
    ax1.legend(frameon=False, fontsize=8)
    ax2.bar([(b["low"] + b["high"]) / 2 for b in bins], [b["n"] for b in bins], width=0.09, color=MODEL_COLOUR, alpha=0.6)
    ax2.set_xlabel("Model probability for the design it favoured")
    ax2.set_ylabel("Choices")
    ax2.set_xlim(0.5, 1.0)
    fig.tight_layout()
    w.save(fig, name, "Calibration of sealed predictions",
           f"Reliability diagram for {cal['n']} scored held-out choices: sealed probability of the favoured design (x) against how "
           f"often that design was chosen (y). Expected calibration error {ece:.3f}. Few choices per bin make this noisy.",
           table, f"plan {report['plan']['plan_id']}, n = {cal['n']}")


def fig_per_scenario(w: Writer, report: dict[str, Any], tag: str) -> None:
    name = f"fig11_per_scenario{tag}"
    per = [p for p in report["per_scenario"] if p["scored"]]
    if not per:
        return w.skip(name, "No scored choice.")
    table = w.table(name, ["scenario_id", "seed", "scored", "correct", "accuracy", "ties", "skips", "unanswered"],
                    [[p["scenario_id"], p["seed"], p["scored"], p["correct"], p["accuracy"], p["ties"], p["skips"], p["unanswered"]]
                     for p in report["per_scenario"]])
    fig, ax = plt.subplots(figsize=(7.5, 3.4))
    x = np.arange(len(per))
    ax.bar(x, [p["accuracy"] for p in per], color=MODEL_COLOUR, width=0.6)
    for xi, p in zip(x, per):
        ax.text(xi, p["accuracy"] + 0.02, f"{p['correct']:g}/{p['scored']}", ha="center", fontsize=8)
    ax.axhline(0.5, color="#374151", lw=0.8, ls="--")
    ax.set_ylim(0, 1.15)
    ax.set_xticks(x, [f"{p['scenario_id']}" for p in per], rotation=20, fontsize=8)
    ax.set_ylabel("Accuracy of sealed predictions")
    ax.set_title("Held-out accuracy per unseen scenario")
    fig.tight_layout()
    w.save(fig, name, "Per-scenario held-out accuracy",
           "Accuracy of the frozen model's sealed predictions within each unseen evaluation scenario (labels: correct / scored). "
           "Variation between scenarios is why intervals resample whole scenarios.", table, f"plan {report['plan']['plan_id']}")


def fig_consistency(w: Writer, report: dict[str, Any], tag: str) -> None:
    name = f"fig12_consistency_and_position{tag}"
    rc, pos = report["repeat_consistency"], report["position"]
    if pos["n"] == 0:
        return w.skip(name, "No explicit A/B choice.")
    table = w.table(name, ["measure", "count", "of", "rate", "ci_low", "ci_high", "note"], [
        ["repeat_agreement", rc["agreements"], rc["scored_pairs"], rc["rate"] if rc["rate"] is not None else "",
         *(rc["rate_ci"] or ["", ""]), rc["rate_ci_method"]],
        ["chose_left", pos["chose_left"], pos["n"], pos["left_share"], "", "", f"binomial p = {pos['binomial_p_value']:.4f}"],
    ])
    fig, (ax1, ax2) = plt.subplots(1, 2, figsize=(9, 3.4))
    if rc["scored_pairs"]:
        ax1.bar(["Same design again", "Changed mind"], [rc["agreements"], rc["disagreements"]], color=[MODEL_COLOUR, "#c2410c"])
        ax1.set_title(f"Repeated pairs, sides swapped\nconsistency {rc['rate'] * 100:.0f}%, n = {rc['scored_pairs']}")
    else:
        ax1.text(0.5, 0.5, "No repeated pair was answered", ha="center", va="center", transform=ax1.transAxes)
        ax1.set_title("Repeated pairs")
    ax1.set_ylabel("Pairs")
    ax2.bar(["Left design", "Right design"], [pos["chose_left"], pos["chose_right"]], color=["#6b7280", "#9ca3af"])
    ax2.axhline(pos["n"] / 2, color="#374151", lw=0.8, ls="--")
    ax2.set_title(f"Screen position of the chosen design\nbinomial p = {pos['binomial_p_value']:.3f}, n = {pos['n']}")
    ax2.set_ylabel("Choices")
    for ax in (ax1, ax2):
        ax.yaxis.set_major_locator(MaxNLocator(integer=True))
    fig.tight_layout()
    w.save(fig, name, "Choice consistency and position effect",
           f"Left: of {rc['scored_pairs']} pairs shown again later with sides swapped, the same design was chosen {rc['agreements']} "
           f"time(s) ({rc['rate_ci_method']}: {_fmt_ci(rc['rate_ci'])}). Right: the left-hand design was chosen in {pos['chose_left']} "
           f"of {pos['n']} choices; the dashed line is an even split. Inconsistent choices cap any model's accuracy.", table,
           f"plan {report['plan']['plan_id']}")


def fig_prediction_trace(w: Writer, report: dict[str, Any], tag: str) -> None:
    name = f"fig13_sealed_predictions{tag}"
    rows = [t for t in report["trials"] if t["choice"] is not None]
    if not rows:
        return w.skip(name, "No answered trial.")
    table = w.table(name, ["trial_index", "kind", "scenario_id", "p_a", "p_left", "choice", "chosen_side", "model_correct", "scored"],
                    [[t["trial_index"], t["kind"], t["scenario_id"], t["p_a"], t["p_left"], t["choice"], t["chosen_side"] or "",
                      "" if t["model_correct"] is None else t["model_correct"], t["scored"]] for t in report["trials"]])
    fig, ax = plt.subplots(figsize=(9, 3.4))
    for t in rows:
        p_chosen = t["p_a"] if t["choice"] == "A" else (1 - t["p_a"]) if t["choice"] == "B" else 0.5
        marker = "o" if t["kind"] == "primary" else "s"
        colour = "#9ca3af" if t["model_correct"] is None else (MODEL_COLOUR if t["model_correct"] == 1 else "#c2410c")
        ax.scatter(t["trial_index"], p_chosen, marker=marker, color=colour, s=36, edgecolor="white", lw=0.5)
    ax.axhline(0.5, color="#374151", lw=0.8, ls="--")
    ax.set_ylim(0, 1.02)
    ax.set_xlabel("Trial (order shown)")
    ax.set_ylabel("Sealed P(design you chose)")
    ax.set_title("Each sealed prediction, revealed after the choice (circles: first showing; squares: repeats)")
    fig.tight_layout()
    w.save(fig, name, "Sealed predictions trial by trial",
           "Probability the frozen model had sealed for the design that was then chosen, per trial. Above the dashed line the "
           "prediction matched (green); below it did not (orange); grey: tie or skip. Repeats (squares) are not scored.", table,
           f"plan {report['plan']['plan_id']}, {len(rows)} answered trials")


# ---------------------------------------------------------------- main

def build(bundle: dict[str, Any], source: str, out: Path, formats: list[str], model_version: str | None = None,
          clean: bool = False) -> dict[str, Any]:
    _style()
    w = Writer(out, source, bundle, formats, clean)
    run = _active_run(bundle, model_version)
    steps: list[Callable[[], None]] = [
        lambda: fig_training_overview(w, bundle),
        lambda: fig_feature_space(w, bundle),
        lambda: fig_coefficients(w, bundle, run),
        lambda: fig_bootstrap(w, bundle, run),
        lambda: fig_loso(w, run),
        lambda: fig_history(w, bundle),
        lambda: fig_learning_curve(w, bundle),
    ]
    reports = closed_reports(bundle)
    for i, report in enumerate(reports):
        tag = "" if len(reports) == 1 else f"_{i + 1}"
        for fn in (fig_evaluation, fig_evaluation_difference, fig_calibration, fig_per_scenario, fig_consistency, fig_prediction_trace):
            steps.append(lambda fn=fn, report=report, tag=tag: fn(w, report, tag))
    if not reports:
        w.skip("fig08-fig13_evaluation", "No closed blinded evaluation in this export.")
    for step in steps:
        step()

    manifest = {
        "script_version": SCRIPT_VERSION,
        "generated_utc": datetime.now(timezone.utc).isoformat(),
        "source": source,
        "export_manifest": bundle["export_manifest"],
        "synthetic_label_comparisons": w.synthetic,
        "all_comparisons": w.total_comparisons,
        "label_note": ("Comparisons whose reason mentions 'synthetic' were produced by a fixed test rule, not by a person; "
                       "figures built from them carry a SYNTHETIC TEST LABELS watermark.") if w.synthetic else
                      "No comparison reason mentions 'synthetic'.",
        "model_version": run["id"] if run else None,
        "reproducibility_check": reproducibility_check(bundle, run) if run else None,
        "closed_evaluations": [{"plan_id": r["plan"]["plan_id"], "report_sha256_note": "verify via GET /api/v1/evaluation/plans/{id}/report",
                                "scored": r["counts"]["scored"]} for r in reports],
        "figures": w.figures,
        "skipped": w.skipped,
        "packages": {"matplotlib": matplotlib.__version__, "numpy": np.__version__},
    }
    (out / "figures_manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    lines = ["# PlannerDNA figures", "", f"Source: `{source}`, exported {bundle['export_manifest'].get('exported_utc', '?')}.", ""]
    if w.synthetic:
        how = "stated in each figure footer" if w.clean else "watermarked"
        lines += [f"**{w.synthetic} of {w.total_comparisons} comparisons carry synthetic test-fixture labels ({how}).**", ""]
    for f in w.figures:
        lines += [f"## {f['id']}: {f['title']}", "", f"![{f['title']}]({f['files'][0]})", "", f["caption"], "",
                  f"Data: `{f['data']}`", ""]
    if w.skipped:
        lines += ["## Not generated", ""] + [f"- `{s['id']}`: {s['reason']}" for s in w.skipped] + [""]
    (out / "captions.md").write_text("\n".join(lines), encoding="utf-8")
    return manifest


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument("--export", help="Path to a PlannerDNA export or backup JSON file.")
    source.add_argument("--api", help="Loopback backend URL, e.g. http://127.0.0.1:8765")
    parser.add_argument("--out", default="figures", help="Output folder (created if missing).")
    parser.add_argument("--formats", default="png,svg", help="Comma-separated image formats (png, svg, pdf).")
    parser.add_argument("--model", default=None, help="Model version for the model figures (default: the active model).")
    parser.add_argument("--clean", action="store_true",
                        help="Presentation style: no diagonal watermark; a one-line footer still states how many labels were rule-made.")
    args = parser.parse_args(argv)
    bundle, name = load_export(args.export, args.api)
    manifest = build(bundle, name, Path(args.out), [f.strip() for f in args.formats.split(",") if f.strip()], args.model, args.clean)
    print(f"Wrote {len(manifest['figures'])} figure(s) to {Path(args.out).resolve()}")
    for s in manifest["skipped"]:
        print(f"  skipped {s['id']}: {s['reason']}")
    check = manifest["reproducibility_check"]
    if check:
        print(f"  reproducibility: {check}")
    if manifest["synthetic_label_comparisons"]:
        how = "stated in each footer" if args.clean else "figures watermarked"
        print(f"  NOTE: {manifest['synthetic_label_comparisons']} comparisons carry synthetic test labels ({how}).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
