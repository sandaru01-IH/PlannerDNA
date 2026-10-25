"""Builds a LinkedIn-style document (4:5 portrait pages) about PlannerDNA from live, local data.

    .media-venv\\Scripts\\python.exe tools\\make_linkedin_pdf.py [--api http://127.0.0.1:8765] [--plan eval-...]

Reads numbers from the local backend (loopback only), figures from deliverables/figures (made with
`make_figures --clean`) and screenshots from deliverables/screenshots (made by the Unity DemoVideoRecorder).
Output: deliverables/PlannerDNA_LinkedIn.pdf
"""
from __future__ import annotations

import argparse
import json
import urllib.parse
import urllib.request
from pathlib import Path

from reportlab.lib.colors import HexColor
from reportlab.lib.enums import TA_LEFT
from reportlab.lib.styles import ParagraphStyle
from reportlab.lib.utils import ImageReader
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.pdfgen import canvas
from reportlab.platypus import Paragraph

ROOT = Path(__file__).resolve().parents[1]
DELIV = ROOT / "deliverables"
FIGS = DELIV / "figures"
SHOTS = DELIV / "screenshots"
FONTS = ROOT / "PlannerDNA_Unity" / "PlannerDNA_Unity" / "Assets" / "PlannerDNA" / "Fonts"

PW, PH = 1080, 1350
M = 80
NAVY, INK, MUTED, LINE = HexColor("#0f172a"), HexColor("#0f172a"), HexColor("#475569"), HexColor("#e2e8f0")
GREEN, GREEN_DARK, PAPER, SOFT = HexColor("#10b981"), HexColor("#0f766e"), HexColor("#ffffff"), HexColor("#f1f5f9")

for weight in ("Regular", "SemiBold", "Bold"):
    pdfmetrics.registerFont(TTFont(f"PS-{weight}", str(FONTS / f"PublicSans-{weight}.ttf")))

BODY = ParagraphStyle("body", fontName="PS-Regular", fontSize=27, leading=38, textColor=INK, alignment=TA_LEFT)
SMALL = ParagraphStyle("small", parent=BODY, fontSize=20, leading=28, textColor=MUTED)
BULLET = ParagraphStyle("bullet", parent=BODY, leftIndent=34, bulletIndent=4, spaceBefore=6,
                        bulletFontName="PS-Bold", bulletFontSize=27, bulletColor=GREEN_DARK)
LIGHT = ParagraphStyle("light", parent=BODY, textColor=HexColor("#e2e8f0"))


def get(api: str, path: str):
    host = urllib.parse.urlparse(api).hostname
    if host not in {"127.0.0.1", "localhost", "::1"}:
        raise SystemExit("Loopback backends only.")
    with urllib.request.urlopen(api.rstrip("/") + path, timeout=30) as r:
        return json.loads(r.read().decode("utf-8"))


def pct(v: float) -> str:
    return f"{v * 100:.1f}%"


class Doc:
    def __init__(self, path: Path, total_pages: int):
        self.c = canvas.Canvas(str(path), pagesize=(PW, PH))
        self.c.setTitle("PlannerDNA: learning from explicit planning choices, tested fairly")
        self.c.setAuthor("PlannerDNA")
        self.page = 0
        self.total = total_pages

    def new(self, dark: bool = False) -> None:
        if self.page:
            self.c.showPage()
        self.page += 1
        self.c.setFillColor(NAVY if dark else PAPER)
        self.c.rect(0, 0, PW, PH, stroke=0, fill=1)
        if not dark:
            self.c.setFillColor(GREEN)
            self.c.rect(0, PH - 14, PW, 14, stroke=0, fill=1)
            self.c.setFont("PS-SemiBold", 20)
            self.c.setFillColor(MUTED)
            self.c.drawString(M, 46, "PlannerDNA")
            self.c.drawRightString(PW - M, 46, f"{self.page} / {self.total}")

    def heading(self, kicker: str, title: str, y: float = PH - 110) -> float:
        self.c.setFont("PS-Bold", 22)
        self.c.setFillColor(GREEN_DARK)
        self.c.drawString(M, y, kicker.upper())
        return self.para(title, M, y - 22, PW - 2 * M, ParagraphStyle("h", parent=BODY, fontName="PS-Bold", fontSize=54, leading=62)) - 18

    def para(self, text: str, x: float, top: float, width: float, style: ParagraphStyle = BODY) -> float:
        p = Paragraph(text, style)
        _, h = p.wrapOn(self.c, width, PH)
        p.drawOn(self.c, x, top - h)
        return top - h

    def bullets(self, items: list[str], top: float, width: float = PW - 2 * M, style: ParagraphStyle = BULLET) -> float:
        for item in items:
            top = self.para(f"<bullet>&bull;</bullet>{item}", M, top, width, style) - 8
        return top

    def image(self, path: Path, top: float, width: float = PW - 2 * M, x: float = M, border: bool = True) -> float:
        img = ImageReader(str(path))
        iw, ih = img.getSize()
        h = width * ih / iw
        max_h = top - 100
        if h > max_h:
            x += (width - width * max_h / h) / 2
            width, h = width * max_h / h, max_h
        if border:
            self.c.setFillColor(SOFT)
            self.c.roundRect(x - 6, top - h - 6, width + 12, h + 12, 10, stroke=0, fill=1)
        self.c.drawImage(img, x, top - h, width, h, mask="auto")
        return top - h - 16

    def stat(self, x: float, top: float, w: float, value: str, label: str) -> None:
        self.c.setFillColor(SOFT)
        self.c.roundRect(x, top - 150, w, 150, 14, stroke=0, fill=1)
        self.c.setFillColor(GREEN_DARK)
        self.c.setFont("PS-Bold", 52)
        self.c.drawString(x + 24, top - 74, value)
        self.para(label, x + 24, top - 88, w - 40, ParagraphStyle("s", parent=SMALL, textColor=INK))

    def save(self) -> None:
        self.c.save()


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--api", default="http://127.0.0.1:8765")
    ap.add_argument("--plan", default=None, help="Blinded test to feature (default: the most recent closed one).")
    ap.add_argument("--out", default=str(DELIV / "PlannerDNA_LinkedIn.pdf"))
    args = ap.parse_args(argv)

    current = get(args.api, "/api/v1/models/current")["model"]
    plans = [p for p in get(args.api, "/api/v1/evaluation")["plans"] if p["status"] == "closed"]
    plan_id = args.plan or max(plans, key=lambda p: p["closed_utc"])["plan_id"]
    rep_resp = get(args.api, f"/api/v1/evaluation/plans/{plan_id}/report")
    rep = rep_resp["report"]
    export = get(args.api, "/api/v1/export")

    trained_ids = set(next(r for r in export["model_runs"] if r["id"] == current["model_version"])["training_comparison_ids_json"])
    trained = [c for c in export["comparisons"] if c["comparison_id"] in trained_ids]
    rule_made = sum(1 for c in trained if "synthetic" in (c.get("reason") or "").lower())
    test_ids = {t["comparison_id"] for t in rep["trials"] if t.get("comparison_id")}
    test_rule_made = sum(1 for c in export["comparisons"] if c["comparison_id"] in test_ids and "synthetic" in (c.get("reason") or "").lower())

    manifest = json.loads((FIGS / "figures_manifest.json").read_text(encoding="utf-8"))
    order = [e["plan_id"] for e in manifest["closed_evaluations"]]
    tag = "" if len(order) == 1 else f"_{order.index(plan_id) + 1}"

    m = rep["metrics"]["model"]
    base = {b["name"]: b for b in rep["metrics"]["baselines"]}
    rc, pos, counts = rep["repeat_consistency"], rep["position"], rep["counts"]
    diag = current["diagnostics"]
    diag_rules = {b["name"]: b for b in diag["baselines"]}
    ci = m.get("accuracy_ci") or [None, None]

    doc = Doc(Path(args.out), 11)
    W = PW - 2 * M

    # 1 cover
    doc.new(dark=True)
    c = doc.c
    c.setStrokeColor(GREEN)
    c.setLineWidth(9)
    c.circle(M + 26, PH - 130, 24, stroke=1, fill=0)
    c.setFillColor(HexColor("#f8fafc"))
    c.setFont("PS-Bold", 96)
    c.drawString(M, PH - 280, "PlannerDNA")
    top = doc.para("A 3D urban-design sandbox that learns from a student's explicit A/B planning choices, "
                   "and then tests that model fairly.", M, PH - 320, W, ParagraphStyle("c", parent=LIGHT, fontSize=36, leading=48))
    top = doc.image(SHOTS / "18_dark_city.png", top - 40, W, border=False)
    doc.para("Unity 6 &nbsp;|&nbsp; FastAPI + SQLite &nbsp;|&nbsp; scikit-learn &nbsp;|&nbsp; runs entirely on one computer",
             M, top - 20, W, ParagraphStyle("t", parent=LIGHT, fontName="PS-SemiBold", fontSize=26, textColor=GREEN))
    doc.para("Synthetic city and synthetic metrics. A learning and research tool, not planning advice.",
             M, 150, W, ParagraphStyle("f", parent=SMALL, textColor=HexColor("#94a3b8")))

    # 2 idea
    doc.new()
    top = doc.heading("The idea", "Make the trade-offs of planning visible, then ask what a person actually prefers")
    top = doc.para("The <b>Green-Space Dilemma</b>: give more homes a park within a 400 m walk, while keeping at least 90% "
                   "of the housing, spending at most 100 credits and building no higher than 8 floors.", M, top, W) - 22
    top = doc.bullets(["<b>Explore</b> a generated 20 x 20 neighbourhood.",
                       "<b>Design A and B</b> from the same starting point.",
                       "<b>Compare and choose</b>: every explicit choice is a data point.",
                       "<b>Learn</b>: a small, explainable model summarises the choices, only when asked."], top)
    doc.image(SHOTS / "03_tour_goal.png", top - 24)

    # 3 sandbox
    doc.new()
    top = doc.heading("The sandbox", "Transparent rules, recomputed on every edit")
    top = doc.bullets(["<b>Park access</b>: share of homes with a park within 400 m along the road network (breadth-first search).",
                       "<b>Housing capacity</b>: floors x units per floor; <b>park share</b> of non-road land.",
                       "<b>Cost</b> from the difference to the baseline, so restoring a parcel always costs 0.",
                       "Hard rules shown as green or red dots. Exact undo and redo; edits are logged, never treated as preferences."], top)
    top = doc.image(SHOTS / "04_tour_access.png", top - 18)
    doc.para("A 13-step guided tour points at each part of the screen and waits for the student to do the real action. "
             "Written for first-year urban-planning undergraduates.", M, top - 6, W, SMALL)

    # 4 compare
    doc.new()
    top = doc.heading("Compare and choose", "The prediction is shown only after the choice")
    top = doc.para("Both designs are checked against the same baseline. The student picks A, B, tie or skip and may type a reason. "
                   "Only an explicit A or B choice between two feasible designs counts as a training example.", M, top, W) - 22
    top = doc.image(SHOTS / "10_compare_prediction.png", top)
    doc.para("Showing the model's guess first would anchor the choice. Here it is revealed afterwards, with the outcomes "
             "that drove it and whether the pair was in the training data.", M, top - 6, W, SMALL)

    # 5 model
    doc.new()
    top = doc.heading("The model", "Pairwise logistic regression: small enough to explain line by line")
    top = doc.para("P(A chosen) = sigmoid( w &middot; [ f(A) &minus; f(B) ] ), with five outcome differences f: park access, "
                   "housing capacity, park share of land, low spending, keeping original housing.", M, top, W) - 18
    top = doc.bullets(["No intercept, and every choice is also added with A and B swapped: screen position cannot be learned.",
                       "L2-regularised (C = 1), standardised features, fixed seed, trained only on an explicit button press.",
                       "200 scenario-bootstrap refits give 90% intervals; leave-one-city-out cross-validation is compared with rules of thumb.",
                       "Stored as checksummed JSON (never pickle); identical data always give the identical model."], top)
    doc.image(SHOTS / "12_learning_diagnostics.png", top - 18)

    # 6 weights
    doc.new()
    top = doc.heading("What it picked up", "Weights are associations with choices, not motives")
    top = doc.image(FIGS / "fig03_coefficients.png", top - 6)
    access = next(k for k in current["coefficients"] if k["feature"] == "delta_access_fraction")
    top = doc.para(f"Trained on <b>{current['unique_comparisons']} comparisons across {current['scenario_count']} synthetic cities</b>. "
                   f"Park access dominates (standardised weight {access['weight_standardised']:.2f}, "
                   f"90% interval {access['interval_low']:.2f} to {access['interval_high']:.2f}). "
                   f"Leave-one-city-out accuracy {pct(diag['model']['accuracy'])}, the same as the rule "
                   f"&quot;always more park access&quot; ({pct(diag_rules['more_access']['accuracy'])}).", M, top - 6, W) - 16
    doc.para(f"Data note: {current['unique_comparisons'] - rule_made} of these comparisons were made by me; {rule_made} were generated by a "
             "fixed rule (&quot;more park access, then lower cost&quot;) to complete the demo, which is why the figures say so.", M, top, W, SMALL)

    # 7 protocol
    doc.new()
    top = doc.heading("The fair test", "A pre-registered, blinded evaluation")
    top = doc.bullets(["<b>Freeze</b> the model by version and file checksum; training is refused until the test ends.",
                       "<b>New areas only</b>: cities never trained on or practised, checked on the server.",
                       "<b>Seal before the choice</b>: the server stores the prediction and shows only its SHA-256 fingerprint.",
                       "<b>Randomise</b> left and right; <b>repeat</b> some pairs later with the sides swapped.",
                       "<b>Reveal at the end</b>: every seal is checked; the report is stored with its hash and recomputed on read."], top)
    doc.image(SHOTS / "15_eval_pair.png", top - 18)

    # 8 result
    doc.new()
    top = doc.heading("Result of my blinded test", "It matched my choices, but so did a one-line rule")
    sw = (W - 40) / 3
    doc.stat(M, top, sw, f"{m['correct']:g} / {m['n']}", "held-out choices predicted by the frozen model")
    doc.stat(M + sw + 20, top, sw, pct(base["more_access"]["accuracy"]), "rule: always the design with more park access")
    doc.stat(M + 2 * (sw + 20), top, sw, pct(base["chance"]["accuracy"]), "chance")
    top = doc.image(FIGS / f"fig08_evaluation_vs_baselines{tag}.png", top - 180)
    ci_text = f"95% interval {pct(ci[0])} to {pct(ci[1])} (bootstrap over whole cities)" if ci[0] is not None else "too few cities for an interval"
    top = doc.para(f"{counts['answered']} answers in {counts['scenarios_scored']} new cities: {m['n']} scored A/B choices "
                   f"({counts['primary_ties']} ties and {counts['registered_repeats']} repeats are not scored). Model accuracy "
                   f"{pct(m['accuracy'])}, {ci_text}; Brier {m['brier']:.3f}, log loss {m['log_loss']:.3f}.", M, top - 4, W) - 12
    doc.para("Honest reading: with this little data, the model has not shown that it captures more than &quot;prefer more park access&quot;."
             + ("" if not test_rule_made else f" ({test_rule_made} of the test answers were rule-made.)"), M, top, W, SMALL)

    # 9 consistency
    doc.new()
    top = doc.heading("How consistent was I?", "Inconsistent choices cap any model's accuracy")
    top = doc.image(FIGS / f"fig12_consistency_and_position{tag}.png", top - 6)
    top = doc.para(f"Repeated pairs (sides swapped): same choice in <b>{rc['agreements']} of {rc['scored_pairs']}</b>. "
                   f"Left-hand design chosen in {pos['chose_left']} of {pos['n']} (binomial p = {pos['binomial_p_value']:.2f}: "
                   "no evidence of a side bias).", M, top - 4, W) - 14
    doc.image(FIGS / f"fig13_sealed_predictions{tag}.png", top)

    # 10 engineering
    doc.new()
    top = doc.heading("Engineering", "Offline-first, reproducible, tested")
    top = doc.bullets(["<b>Unity 6</b> (URP, UI Toolkit, Input System): immutable city state, deterministic SplitMix64 generation, "
                       "snapshot undo/redo, light and dark themes.",
                       "<b>Local backend</b>: FastAPI + SQLite with migrations, foreign keys and transactions; binds to 127.0.0.1 only.",
                       "Every record is written locally first and synced in order through an outbox with idempotent retries.",
                       "The server recomputes every metric and hash from the snapshot and refuses any mismatch.",
                       "Figures are generated from exported data with the numbers in a CSV beside each one; "
                       "the model is refitted from the export to check it reproduces exactly.",
                       "Automated tests: 88 backend, 130 Unity EditMode and 24 Unity PlayMode tests (incl. a full blinded test)."], top)
    doc.image(SHOTS / "17_eval_report.png", top - 18)

    # 11 limits
    doc.new()
    top = doc.heading("Limits and takeaways", "What this can and cannot say")
    top = doc.bullets(["One person, synthetic cities, five proxy metrics: nothing here generalises to real places or other people.",
                       "Weights describe associations in the pairs I chose to compare, not values or expertise.",
                       "Small samples give wide intervals; a model that ties a one-line rule has not shown more than that rule.",
                       "The value is the workflow: making trade-offs explicit, recording real choices, and testing a model honestly "
                       "before believing it."], top)
    top = doc.para("The most useful number on these pages is the one where the simple rule did just as well.",
                   M, top - 30, W, ParagraphStyle("q", parent=BODY, fontName="PS-SemiBold", fontSize=34, leading=46, textColor=GREEN_DARK))
    doc.image(SHOTS / "13_suggestions.png", top - 30)

    doc.save()
    print(f"Wrote {args.out} (blinded test {plan_id}, figures tag '{tag}')")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
