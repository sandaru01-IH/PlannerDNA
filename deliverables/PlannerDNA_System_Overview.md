# PlannerDNA: system overview

PlannerDNA is a local, single-user 3D urban-design sandbox built in Unity. A student edits a
synthetic neighbourhood, compares two of their own designs, and records which one they prefer.
When asked, a small and explainable statistical model is fitted to those explicit choices. A
pre-registered, blinded test then checks whether that frozen model predicts new choices better
than simple rules of thumb.

Everything runs on one computer: Unity plus an optional FastAPI + SQLite backend bound to
`127.0.0.1`. There are no cloud services and no telemetry. All cities, metrics and credits are
synthetic. This is a learning and research tool, not planning advice.

This document describes the finished system (Phases 0–4). No further development is planned.

---

## 1. What the student does

The exercise is the **Neighbourhood Green-Space Dilemma**:

> Improve the share of housing capacity within a 400 m road-network walk of a park, while keeping
> housing capacity at or above 90% of the baseline, spending no more than 100 synthetic credits and
> never exceeding 8 floors.

The screen shows a four-step journey line at the top:

1. **Explore.** A generated 20 × 20 neighbourhood (40 m parcels, 800 m × 800 m) is shown in 3D.
   A Home screen explains the challenge, and a 13-step guided tour spotlights each part of the
   screen and waits for the student to perform the real action.
2. **Design A and B.** Starting from the same baseline, the student makes parks, builds homes or
   shops, clears land, and adds or removes floors. Metrics update after every edit. Exact undo and
   redo are available. Two complete designs are saved as Alternative A and Alternative B.
3. **Compare and choose.** Both designs are shown side by side against the baseline. The student
   picks A, B, tie or skip and may type a reason. Only after the choice is confirmed does the
   trained model (if any) reveal what it would have predicted.
4. **Learn.** In the Learning Lab, the student presses **Train my model**. The lab shows the
   weights with uncertainty, cross-validation against rules of thumb, and ranked suggestions.
   It also offers the blinded test.

Light and dark themes are available. The text is written for first-year urban-planning
undergraduates.

## 2. The sandbox: city, rules and metrics

**City generation** is deterministic. The seed fully determines the city, using a SplitMix64
generator whose output is pinned by a unit test.

- Every third row is a road, plus three seed-chosen vertical roads, so every parcel touches a road.
- Vacant land, commercial strips, residential blocks (2–6 floors), one or two small parks and
  protected civic parcels are placed.
- A city is accepted only if:
  - the road network is connected;
  - baseline park access is below 75%;
  - at least one vacant-to-park edit is feasible and improves access.

  Otherwise generation retries, up to 32 times. Seed 42 is the demo city.

**Metrics** (`metrics-1.0.0`) are recomputed from the full city state after every edit:

| Metric | Definition |
|---|---|
| Housing capacity | Sum over residential parcels of floors × 4 units (a capacity proxy, not people) |
| Park access | Share of housing units whose road walk to a park is at most 400 m (multi-source breadth-first search over road cells, with no diagonals or shortcuts) |
| Park share | Park parcels as a share of non-road parcels |
| Cost | Sum over parcels of a fixed baseline-to-current cost table, so restoring a parcel costs 0 |
| Lost original housing | Units removed from parcels that were residential at baseline (a displacement proxy) |

**Hard rules** are budget ≤ 100 credits, housing ≥ 90% of baseline, at most 8 floors, and no edits
to roads or civic parcels. Each rule is shown as a green or red dot. Refused edits explain why and
are logged separately. Infeasible designs can be saved but are never used as training data.

The full formulas and the cost table are in
[`docs/scenario-and-metrics.md`](../docs/scenario-and-metrics.md).

## 3. What counts as data

The **only labels** are explicit A/B choices between two feasible designs from the same city.
Edits, undo clicks, dwell time and screen activity are never treated as preferences.

Each design is summarised by five features (`features-v1`), each measured as a difference from the
baseline:

| Feature | Higher means |
|---|---|
| `delta_access_fraction` | more homes within a 400 m walk of a park |
| `delta_housing_fraction` | more housing capacity |
| `delta_park_share_fraction` | more park land |
| `negative_budget_fraction` | lower cost |
| `negative_lost_housing_fraction` | fewer original homes removed |

The backend never trusts Unity's numbers. It rebuilds every design from its saved snapshot,
recomputes the metrics, features and content hash, and refuses any mismatch.

## 4. The model

The model is a **pairwise logistic regression** (Bradley–Terry style):

\[ P(A \text{ chosen over } B) = \sigma\big(w \cdot (f(A) - f(B))\big) \]

- It is scikit-learn `LogisticRegression` with no intercept, L2 penalty `C = 1`, the `lbfgs`
  solver and a fixed seed.
- Every choice is also added with A and B swapped, so \(P(A \succ B) + P(B \succ A) = 1\) and
  the screen position of a design cannot be learned.
- Features are scaled without centring. Standardised weights give the change in log-odds for one
  typical training difference, so they are comparable across features.
- **Readiness.** Training refuses rather than guessing. It needs at least 10 informative
  comparisons from at least 2 cities, spanning at least 2 independent trade-off directions.
  Below 30 comparisons over 5 cities the model is labelled *Exploratory*.
- **Uncertainty.** 200 city-level bootstrap refits give a 90% interval and a sign-agreement share
  for every weight. A weight whose interval includes zero is reported as directionally uncertain.
- **Leave-one-city-out cross-validation** compares the model with four transparent rules:
  - chance;
  - always the cheaper design;
  - always the design with more park access;
  - the equal-weight sum of the features.

  This is a diagnostic inside the training data, not a held-out test.
- **Storage.** Each model is a checksummed JSON file, never pickle. Its version id embeds a
  fingerprint of the training data, so identical data always give the identical model. Every
  prediction reloads the file and verifies its hash.
- **Suggestions.** Unity generates up to 30 feasible candidate designs by seeded random edits to
  the baseline. The backend re-verifies each one and ranks them by the model's utility. They are
  labelled "synthetic candidates, never optimal plans".

Weights describe **statistical associations with the pairs that were compared**. They are not
motives, values or expertise.

## 5. The blinded test (`blinded-eval-1.0.0`)

The question is: does the frozen model predict choices it has never seen, in cities it was never
trained on, better than simple rules?

1. **Pre-register.** The plan records the model version, the model file's SHA-256, the new city
   seeds, the number of pairs and the primary metric. Training is refused while a test is open.
2. **New areas only.** The server rejects any city that was used in training or practice.
3. **Seal before the choice.** For each pair (a genuine trade-off between two feasible designs),
   the server computes the prediction, stores it, and shows only its SHA-256 fingerprint.
4. **Randomise and repeat.** Left and right are randomised. Some pairs return later with the sides
   swapped, to measure the student's own consistency.
5. **Reveal and report.** At the end every seal is checked against its stored prediction. The
   report gives:
   - accuracy, Brier score and log loss with whole-city bootstrap intervals;
   - the same metrics for chance, cheaper, more park access, equal weight and "always left";
   - repeat consistency;
   - a side-bias test.

   The report is hashed and recomputed every time it is read.

Answers in a blinded test are never used for training, not even after the test closes.

## 6. Results on the current data

Data split, stated plainly: the active model (`pm-20261025T114017Z-f8497a43`) was trained on
**30 comparisons across 5 cities**.

- **10 were made by the user** (city seed 42).
- **20 were generated by a fixed rule**: "prefer more park access, then lower cost". They were
  added to complete the demo and are marked as synthetic in the data.

Across the whole database, 35 of 61 choices are rule-made. The clean figures state this in their
footer.

**Weights** (standardised, 90% bootstrap interval):

| Feature | Weight | Interval | Reading |
|---|---|---|---|
| Park access | 2.69 | 2.13 to 2.93 | Strongest association, same sign in 100% of resamples |
| Housing capacity | −0.60 | −1.17 to −0.05 | Associated with less housing |
| Park share of land | 0.10 | −0.43 to 1.10 | Little or no association |
| Low spending | −0.79 | −0.99 to −0.48 | Associated with *higher* cost, likely because more park access usually costs more |
| Keeping original housing | 0.47 | −0.18 to 0.97 | Direction uncertain |

Leave-one-city-out accuracy was **96.7%**, exactly the same as the rule "always the design with more
park access".

**The user's own blinded test** (`eval-11f5b0d7657a45d0b4b62cf05e9c6b26`) had 15 answers in 3 new
cities. 9 scored A/B choices remained after removing 3 ties and 3 repeats.

| Predictor | Accuracy |
|---|---|
| Frozen model | **8 / 9 = 88.9%** (95% interval 50% to 100%), Brier 0.111, log loss 0.661 |
| Always more park access | 88.9% |
| Always the design on the left | 66.7% |
| Chance | 50.0% |
| Equal-weight sum | 44.4% |
| Always the cheaper design | 22.2% |

- **Consistency.** The same choice was made in 2 of 3 repeated pairs.
- **Side bias.** The left design was chosen 7 of 12 times (binomial p = 0.77), which is no
  evidence of a side bias.
- **Integrity.** All seals verified, and the report is reproducible.

A second closed test, answered by the rule-following agent, scored 12 / 12. It mainly confirms that
the pipeline works end to end.

**Honest reading.** The model matched the user's choices, but so did a one-line rule. With this
little data, it has not shown that it captures more than "prefer more park access".

## 7. Architecture

```
Unity (C#, URP, UI Toolkit, Input System)
  core: immutable CityState, generator, metric engine, validator, cost rules, undo/redo
  app : PlannerApp state machine, views (Home, tour, compare, Learning Lab, blinded test)
  data: LocalStore (JSON files, written first) -> ordered outbox -> HTTP sync
        |
        |  HTTP, 127.0.0.1 only, idempotent requests
        v
Backend (Python 3.12, FastAPI, SQLite)
  records : sessions, events, alternatives, comparisons (server recomputes everything)
  models  : readiness, training, JSON model files, predictions, candidate ranking
  evaluation: plans, sealed trials, reveal, reports
  export / delete-all
        |
        v
backend/tools/make_figures.py -> figures (PNG/SVG/PDF) + one CSV per figure + manifest
```

- **Offline-first.** The game works without the backend. Every record is saved locally first, then
  synced in order through an outbox with idempotent retries. Nothing is lost if the backend is off.
- **Data stores.**
  - Local Unity store: `%USERPROFILE%\AppData\LocalLow\DefaultCompany\PlannerDNA_Unity\PlannerDNA`.
  - Backend: `backend\data\plannerdna.db`, with models in `backend\data\models\`.
- **Database.** SQLite with migrations, foreign keys and transactions. Export produces one JSON
  file. Delete-all removes the database rows and the model files after confirmation.
- **Versioning.** Every record carries generator, metric, cost-rule, feature and schema versions.

Details: [`docs/architecture.md`](../docs/architecture.md) and
[`docs/data-dictionary.md`](../docs/data-dictionary.md).

## 8. Testing

- **Backend:** 88 pytest tests. They cover recomputation and tamper refusal, readiness refusals,
  model symmetry, leave-one-city-out isolation, artifact checksums, candidate verification, the
  evaluation protocol, sealed export and figures.
- **Unity EditMode:** 130 tests. They cover the generator reference values, metrics, walk
  distances, cost table, validator, undo/redo, candidate generator and local store.
- **Unity PlayMode:** 24 tests (5 explicit ones skipped by default). They include offline play,
  live sync against a throwaway backend, Learning Lab training and a full blinded test.
- The tests never touch the real database. Live tests start a temporary backend on port 8766 with
  its own data folder.

Commands are in [`docs/runbook.md`](../docs/runbook.md).

## 9. How to run it

```powershell
# 1. Optional backend (the game works without it)
cd PlannerDNA\backend
.\.venv\Scripts\python.exe -m uvicorn app.main:app --host 127.0.0.1 --port 8765

# 2. Unity: open PlannerDNA_Unity\PlannerDNA_Unity in Unity Hub (Editor 6000.6.7f1),
#    menu PlannerDNA > Create or Repair Demo Scene, then press Play.

# 3. Figures from your stored data (add --clean for the version without the watermark)
.\.venv\Scripts\python.exe tools\make_figures.py --api http://127.0.0.1:8765 --out ..\figures
```

Beginner steps are in [`docs/START_HERE.md`](../docs/START_HERE.md).

## 10. Deliverables in this folder

| Item | Path | Notes |
|---|---|---|
| Demo video | `video/PlannerDNA_demo.mp4` | 1:57, 1920 × 1080, 30 fps, H.264. Recorded on a copy of the data; the choices shown are scripted, as the intro card says |
| Video poster | `video/PlannerDNA_demo_poster.jpg` | Thumbnail for the post |
| LinkedIn carousel | `PlannerDNA_LinkedIn.pdf` | 11 pages, 1080 × 1350 (4:5): idea, sandbox, compare, model, weights, protocol, result, consistency, engineering, limits |
| Screenshots | `screenshots/01_home.png` … `18_dark_city.png` | 1920 × 1080 stills from the recording |
| Figures | `figures/` | PNG, SVG and PDF with the CSV behind each, `captions.md` and `figures_manifest.json`. Suffix `_2` is the user's blinded test and `_1` the agent's |

There is no `fig06` because only one model has been trained.

**To regenerate:**

- **Figures:** run `backend/tools/make_figures.py --clean --formats png,svg,pdf`.
- **Video:**
  1. Run the explicit `DemoVideoRecorder` PlayMode test against a throwaway backend.
  2. Run `tools/make_demo_video.py`, which needs the `.media-venv` with ffmpeg.
- **PDF:** run `tools/make_linkedin_pdf.py --api http://127.0.0.1:8765`.

## 11. Limits

- There is one person, synthetic cities and five proxy metrics. Nothing generalises to real places,
  real residents or other people.
- Two-thirds of the training comparisons were generated by a rule, so the weights mostly reflect
  that rule.
- Small samples give wide intervals: the user's blinded test has a 95% interval of 50% to 100%.
- A model that ties a one-line rule has not shown more than that rule.
- The value of the project is the workflow: making trade-offs explicit, recording real choices,
  and testing a model honestly before believing it.

More detail: [`docs/research-limitations.md`](../docs/research-limitations.md) and
[`docs/ml-protocol.md`](../docs/ml-protocol.md).
