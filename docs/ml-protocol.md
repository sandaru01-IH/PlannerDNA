# ML protocol: personal pairwise preference model (Phases 3 and 4)

Version `pairwise-logit-1.0.0`, feature set `features-v1`, candidate generator `candidates-1.0.0`,
blinded evaluation protocol `blinded-eval-1.0.0` with pair rule `evalpair-1.0.0`, figures `figures-1.0.0`.

This document says exactly what the model is, what it is fitted on, how it is checked, and what
it may and may not be used to claim. The model is a **statistical summary of associations in
your own explicit A/B choices in synthetic scenarios**. It does not learn your reasons and does
not understand planning. Read [research-limitations.md](research-limitations.md) before
interpreting any number it produces.

## 1. Question

> Given two feasible designs A and B for the same synthetic scenario, how strongly is each of
> five measured outcome differences associated with which one you chose?

## 2. Data

**Unit of analysis.** One explicit comparison is a `comparison` row in the backend database. Edits,
undo clicks, dwell time and timeline events are **never** labels.

A comparison is **training-eligible** only when all of these hold. Both Unity and the backend
check them, and the backend recomputes everything itself.

- The choice is `A` or `B`. Ties and skips are stored but not used.
- `mode = train`. Evaluation-mode choices (answers in the blinded test, section 10) are never
  training data, not even after their test is closed.
- Both designs are feasible: within budget, at least 90 % of baseline housing, at most 8 floors,
  and no fixed parcels edited.
- Both designs come from the same registered scenario and baseline.
- The comparison and both alternatives carry `feature_version = features-v1`.

**Features** are the server-recomputed values stored with each alternative, not the values sent
by Unity. Each is a difference from the scenario's baseline:

| Feature | Definition | Higher means |
|---|---|---|
| `delta_access_fraction` | (access % − baseline access %) / 100 | more housing within a 400 m walk of a park |
| `delta_housing_fraction` | (housing units − baseline) / baseline housing | more housing capacity |
| `delta_park_share_fraction` | (park share % − baseline) / 100 | more park land |
| `negative_budget_fraction` | −(credits spent / budget) | lower cost |
| `negative_lost_housing_fraction` | −(original units removed / baseline housing) | fewer original homes removed |

**Model input.** The input is x = f(A) − f(B) and the label is y = 1 when A was chosen.
Comparisons whose designs have identical measured outcomes (x = 0) are informative of nothing.
They are counted, reported as ignored, and never fitted.

## 3. Readiness (training refuses rather than guessing)

Training needs at least 10 informative comparisons from at least 2 scenarios. The differences must
also span at least 2 independent directions: the rank of the RMS-scaled difference matrix is ≥ 2.

| Refusal (HTTP 422) | When |
|---|---|
| `not_enough_comparisons` | 0, or fewer than 10 informative comparisons |
| `not_enough_scenarios` | all comparisons from one scenario (leave-one-scenario-out is impossible) |
| `homogeneous_comparisons` | every pair varies along the same single trade-off |

These thresholds are **heuristics**, not statistical adequacy guarantees. Below 30 comparisons
over 5 scenarios (also a heuristic), the model is labelled "Exploratory" everywhere it appears.

The Learning Lab shows `Training samples: N unique direct comparisons across S scenarios`, where N
counts unique informative comparisons. Reversed copies are never counted.

## 4. Model

\[ P(A \succ B) = \sigma\big(w \cdot (f(A) - f(B))\big) \]

- The model is scikit-learn `LogisticRegression` with `fit_intercept=False`, an L2 penalty,
  `C = 1.0`, the `lbfgs` solver, `max_iter = 1000`, `tol = 1e-8` and `random_state = 20261025`.
- **Symmetry.** Each comparison is added twice, as (x, y) and (−x, 1 − y). With no intercept this
  makes \(P(A \succ B) + P(B \succ A) = 1\) exactly, and the result independent of which design
  was shown as A. The duplicates balance the fit; they are not extra evidence.
- **Scaling.** `StandardScaler(with_mean=False)` is fitted on the augmented training differences
  only. Not centring keeps the symmetry. Scaling only changes how the L2 penalty treats each
  feature.
- **Reported weights.**
  - *Standardised weight*: the change in log-odds for one typical (RMS) training difference in
    that feature. These are comparable across features.
  - *Raw weight*: the standardised weight divided by the scale, in the original fraction units.
    Utility and probabilities use raw weights.
  - A feature that never varied in training gets weight 0 and is reported as **not estimated**,
    never as "unimportant".
  - |standardised weight| < 0.1 is reported as **little or no association**: the odds change by
    less than about 10 %.
- **Utility of a design** is w · f(design), a score relative to the scenario baseline. It has no
  unit and is only meaningful for ordering designs.

## 5. Uncertainty and diagnostics

- **Scenario bootstrap.** Scenarios are resampled with replacement 200 times (seed 20261025),
  using a fixed feature scale. The model is refitted each time. The report gives a 90 % percentile
  interval for each standardised weight, plus the share of resamples whose sign agrees with the
  full fit. With fewer than 5 scenarios the intervals are rough, and the model carries a warning
  saying so.
- **Direction labels.** A weight below 0.1 in standardised units is labelled *negligible*. When a
  weight's interval includes zero, its statement adds that even the direction is uncertain
  (`interval_includes_zero`). The Learning Lab then also shows no exchange rate built from it.
- **Leave-one-scenario-out (LOSO) cross-validation.** Each scenario's comparisons are predicted by
  a model and scaler fitted **without any comparison from that scenario**. This is reported as
  accuracy (p = 0.5 counts as half correct), Brier score and log loss. The same comparisons are
  also scored by four transparent baselines:
  - chance (always 50 %);
  - always the cheaper design;
  - always the design with more park access;
  - the equal-weight sum of the five features.

  Log loss is shown only for probabilistic predictors.
- **Training fit** (accuracy on the same data) is shown and labelled optimistic. If it is 100 %,
  the model warns that the data are perfectly separable and the weights are directions, not
  strengths.
- LOSO is a **diagnostic inside the training data**. It is not the blinded, locked, pre-registered
  held-out evaluation of section 10. Only a closed blinded evaluation report may be described as a
  held-out test.

A model that does not beat the simple baselines is still shown, together with that fact. In the
synthetic-label snapshot run (15 comparisons, 3 scenarios), the labels followed "prefer more park
access" exactly. There the
"more park access" baseline scored 100 % and the fitted model 80 % LOSO. That is the expected,
honest outcome when one simple rule generates every label.

## 6. Training, versioning and storage

- Training happens **only** when you press **Train my model** in the Learning Lab (`POST
  /api/v1/models/train`). The button is disabled while the backend is offline, while local
  records are still syncing, or while readiness refuses.
- **Version id.** The id is `pm-<UTC timestamp>-<first 8 hex of the data fingerprint>`. The
  fingerprint is the SHA-256 of the canonical JSON of ML version, feature version,
  hyperparameters and every training row. Retraining on unchanged data returns the existing model
  (`200 unchanged`) and creates nothing.
- **Model file.** Each model is a JSON file (`<data dir>/models/<version>.json`, never pickle). It
  contains:
  - format and version;
  - feature names;
  - raw and standardised weights, and the feature scale;
  - hyperparameters and random seed;
  - training comparison ids and scenario ids;
  - counts;
  - data fingerprint;
  - package versions (Python, numpy, scipy, scikit-learn);
  - UTC timestamp.

  Its SHA-256 is stored in `model_run`. Every prediction reloads the file, checks the hash and
  format, and refuses with `409 model_artifact_invalid` if anything differs.
- **History.** Each training run is a `model_run` row. Exactly one is `active`; older ones become
  `superseded` and are kept for reproducibility. Deleting all data removes the rows and the
  `models/` folder.

## 7. Uses of a fitted model

| Use | Endpoint | Rules |
|---|---|---|
| Show weights and diagnostics | `GET /models/current`, `/models/history` | No model means no weights. Hard-coded weights are never shown. |
| Predict a recorded pair | `POST /models/predict-pair` | Uses the server's stored features for both alternatives. In Unity it is requested only **after** you confirm your own choice, and shown with a note saying whether that comparison was in the training data. Logged in `prediction` (`revealed_after_choice`, `in_training_set`). |
| Rank suggestions | `POST /models/rank-candidates` | See section 8. |

## 8. Suggestions (candidates-1.0.0)

1. **Generation in Unity.** `CandidateGenerator` uses a seeded random generator (the scenario
   seed) and makes at most 800 attempts, aiming for 30 candidates. Each candidate applies 1–4
   single-parcel edits to the **baseline**:
   - a park on vacant land;
   - a park replacing a building;
   - raising residential floors;
   - new housing on vacant land;
   - commercial converted to housing.

   Every edit passes the same `ActionValidator` as a manual edit.
2. **Filtering in Unity.** Infeasible designs are dropped, as are exact duplicates (same content
   hash) and near-duplicates (every feature within 0.005 of an accepted candidate).
3. **Ranking in the backend.** For every candidate the backend:
   - recomputes the hash, metrics, constraints and features from the snapshot;
   - refuses (`422`) tampered, infeasible, duplicate or unchanged candidates;
   - sorts by utility, breaking ties by id;
   - skips near-duplicates;
   - returns the top k with `p_over_next` = σ(utility difference to the next suggestion).

   The ranking is logged in `prediction` (kind `rank`).
4. **Display.** Every response carries the model version and the banner "Synthetic scenario …
   not optimal plans and not professional advice". "Use as my design" applies the candidate as
   one undoable edit, logged with input source `assistant_generated`.

## 9. Tests

The tests use **synthetic test-fixture labels** produced by a fixed rule (for example "choose the
design with more park access"). They never use real user labels.

| Property | Test |
|---|---|
| The fitted model recovers the direction the fixture rule favours (access, budget, housing, negative direction) | `backend/tests/test_pairwise.py`, `test_models_api.py`, Unity `LearningPlayModeTests` |
| No data, too few, one scenario, homogeneous and identical outcomes all refuse gracefully | `test_pairwise.py`, `test_models_api.py` |
| \(P(A \succ B) + P(B \succ A) = 1\); swapping A and B leaves the fit unchanged | `test_pairwise.py`, `test_models_api.py`, `LearningPlayModeTests` |
| Predictions come from the saved, checksummed file, survive a restart, and a tampered file is refused | `test_models_api.py` |
| LOSO never uses the held-out scenario for fitting or scaling | `test_pairwise.py` |
| Unity-generated candidates pass Python recomputation unchanged | `test_models_api.py::test_unity_generated_candidates_are_accepted_and_ranked` |
| Candidates are deterministic, feasible, distinct, touch only editable parcels and match the metric engine | Unity `CandidateGeneratorTests` |

## 10. Blinded held-out evaluation (Phase 4, `blinded-eval-1.0.0`)

The question is: **does the frozen model predict choices it has never seen, on areas it was never
trained on, better than simple rules of thumb?** The protocol is designed so that the answer cannot
be flattered by the software or by the participant.

### 10.1 Pre-registration (`POST /api/v1/evaluation/plans`)

The client sends the model version it expects, 2–20 evaluation scenarios (id and seed), the pairs
per scenario (1–10), the number of repeated pairs (0–20, at most one per pair) and the pair rule.
The backend:

1. **Freezes the model.** The model must be the active one. The plan stores its version and the
   SHA-256 of its JSON file. Every prediction in the test reloads that file and refuses if the hash
   differs (`409 model_artifact_invalid`).
2. **Checks the scenarios are unseen.** Each scenario is refused (`422 scenario_not_unseen`) if its
   id or seed is in the model's training set, if it already has any training-mode choice, or if an
   earlier plan used it. Unity draws fresh random seeds (100 000 to 10⁹), excludes everything it
   knows locally and retries with new seeds if the backend refuses.
3. **Draws the secrets.** The server draws `plan_seed` (screen sides and repeat selection) and
   `candidate_seed_base` (which designs are generated) with `secrets`. Neither the person nor Unity
   chooses them.
4. **Stores a registration fingerprint.** SHA-256 of the canonical JSON of everything above.

Only one plan can be open. While it is open:

| Refused | Code |
|---|---|
| Training any model | `409 evaluation_in_progress` (readiness also reports `can_train = false`) |
| Practice (train-mode) choices on an evaluation scenario | `409 scenario_reserved_for_evaluation` (Unity refuses locally too) |
| `predict-pair` or `rank-candidates` on an evaluation scenario | `409 sealed_for_evaluation` |
| A second plan | `409 evaluation_already_open` |

Exports made while a plan is open omit the probability and sealed payload of every trial that has
not been revealed, so an export taken mid-test cannot show predictions that might steer later
choices.

### 10.2 Trials (`POST /api/v1/evaluation/trials`)

Trials are strictly sequential: all first presentations, scenario by scenario, then the repeats.
The backend tells the client what comes next (`next.action` = `new_pair`, `repeat`, `answer`,
`complete`).

- **Pair rule `evalpair-1.0.0` (Unity, no model input).** For trial *i* the candidate seed is
  `candidate_seed_base + i`. Unity generates candidates with `CandidateGenerator` and keeps the
  feasible pairs that are genuine **trade-offs**: each design is better on at least one of the five
  features. It draws one with a `DeterministicRng` seeded by the candidate seed. Without any
  trade-off pair, any pair with different features is used. Because the rule never sees the model,
  the test pairs cannot be selected to suit it. The backend refuses wrong seeds, wrong rules,
  infeasible, identical or uninformative pairs and designs used in any earlier comparison or trial.
- **Sealing.** The backend computes P(A) with the frozen weights, then stores
  `sealed_payload` = canonical JSON {trial id, plan id, model version, alternative ids, `p_a`,
  32-hex random nonce} and its SHA-256. The response contains **only the SHA-256** (and the screen
  order), never the probability. A `prediction` row of kind `evaluation` records the same value.
- **Screen sides.** For first presentations, left is A when
  `random.Random(f"{plan_seed}:{trial_index}:side").random() < 0.5`. Unity shows **Left** and
  **Right**, never A and B.
- **Repeats.** Once every first presentation has an A/B answer, the repeated pairs are drawn with
  `random.Random(f"{plan_seed}:repeats").sample(...)`. A repeat shows the same two designs with the
  sides swapped and is registered as its own trial.

### 10.3 Choices and the reveal

- The answer is an ordinary preference record with `mode = evaluation`,
  `evaluation_trial_id`, `model_version_locked` (must equal the frozen model) and the
  backend's `shown_left_alternative_id`. Each consistency check (unknown trial, wrong pair, side or
  model, closed plan, already answered) has its own error code. `training_eligible` is false.
- `POST /evaluation/trials/{id}/reveal` works **only after** the choice has reached the backend
  (`409 choice_not_recorded` before). It returns `sealed_payload`, so anyone can check that its
  SHA-256 equals the seal shown before the choice.
- **Unity reveals nothing during the test.** Showing each prediction straight after each choice
  would let the person learn what the model expects and drift towards it, which inflates accuracy.
  So the Unity app opens every seal only when the person presses **Finish**, checks each one
  (SHA-256, trial id and pair), and then closes the plan.

### 10.4 Closing and the report (`POST /evaluation/plans/{id}/close`)

Closing is irreversible. A plan can be closed early; the report then states how many pairs were
planned and how many were answered. The report is computed once, stored as canonical JSON, and
hashed. `GET /evaluation/plans/{id}/report` re-hashes the stored report **and** recomputes it from
the stored rows. `verification.reproducible` is true only when all three hashes agree.

**What is scored.** First presentations answered A or B. Ties, skips, unanswered pairs and repeats
are counted but not scored, so the denominator is always stated.

| Report section | Content |
|---|---|
| `counts` | planned, registered, answered, unanswered, revealed, scored, ties, skips, A/B balance, scenarios scored |
| `metrics.model` | accuracy (p = 0.5 counts as half), Brier score, log loss, with intervals |
| `metrics.baselines` | chance; always cheaper; always more park access; equal-weight sum; **always the left design** (detects a side habit). Each also gives model minus baseline accuracy with an interval |
| `bootstrap` | 1000 resamples of **whole scenarios** (seed 20261025), 95 % percentile intervals; omitted with fewer than 3 scored scenarios |
| `position` | how often the left design was chosen, with an exact binomial test against 50 % |
| `repeat_consistency` | agreements on repeated pairs, with a Wilson 95 % interval. No model can be more reliable than the person's own consistency |
| `calibration` | bins of the favoured design's probability (0.5–1.0) against how often it was chosen; expected calibration error |
| `per_scenario`, `trials` | every row behind every number |
| `held_out_guarantee` | recomputed count of evaluation choices and evaluation scenarios in the frozen model's training data (zero by construction) |
| `warnings`, `caveats`, `summary` | small samples, model not beating a baseline, low consistency, side bias, early close |

`trials.csv` and `metrics.csv` give the same numbers in plotting-ready form.

**Interpreting it.** With 15–25 choices, intervals are wide; one test is evidence about one person
in synthetic scenarios, not a general result. A model that does not beat "always more park access"
is reported as such. Accuracy close to the person's repeat consistency is about as good as any
model of those choices can be.

## 11. Figures (`backend/tools/make_figures.py`, `figures-1.0.0`)

Figures are drawn **only** from a backend export (`GET /api/v1/export`, or `--api` to fetch it
from a loopback backend). Training-side panels are recomputed with the same `app.ml.pairwise`
module. Evaluation panels use the stored reports unchanged.

| File | Shows | Source |
|---|---|---|
| `fig01_training_data` | eligible comparisons per scenario, A versus B chosen | export comparisons |
| `fig02_tradeoff_space` | chosen minus rejected differences (access against housing and spending) | export alternatives |
| `fig03_coefficients` | standardised weights with 90 % scenario-bootstrap intervals | active model |
| `fig04_bootstrap_draws` | distribution of every bootstrap weight | recomputed (200 resamples, seed 20261025) |
| `fig05_loso_vs_baselines` | LOSO accuracy, Brier, log loss against the four baselines | active model diagnostics |
| `fig06_model_history` | comparisons and LOSO accuracy for every trained version | model history (needs 2 or more versions) |
| `fig07_learning_curve` | LOSO accuracy as the first n comparisons grow | recomputed |
| `fig08`–`fig13` (per closed test) | held-out accuracy, Brier, log loss against baselines; model minus baseline intervals; calibration; per-scenario accuracy; repeat consistency and side choice; every sealed probability against the choice | closed evaluation reports |

Every figure has a CSV with exactly the plotted numbers, a footer with model or plan id, n, source
and export time, and an entry in `figures_manifest.json`. The manifest also refits the active model
from the export and records whether the weights match within 1e-6 (`reproducibility_check`).
`captions.md` gives a caption draft for each figure. Figures whose data include any comparison
whose reason contains "synthetic" are watermarked **SYNTHETIC TEST LABELS**. Figures that need
missing data (for example a second model version) are skipped and listed as skipped with the
reason, never drawn empty or invented.

## 12. Tests for Phase 4

| Property | Test |
|---|---|
| Plan validation; practised, trained or reused scenarios refused | `backend/tests/test_evaluation_api.py` |
| Probability never returned before the choice; seal = SHA-256 of the revealed payload; repeats swap sides; sides follow the plan seed | `test_evaluation_api.py`, Unity `EvaluationPlayModeTests` |
| Training, practice choices, predictions and ranking refused on sealed scenarios while open | `test_evaluation_api.py`, `EvaluationPlayModeTests` |
| Report counts, baselines, intervals, consistency, calibration; stored report tamper detection; recomputation is byte-identical | `test_evaluation_api.py` |
| Pair rule deterministic, feasible, trade-off first; unseen seeds; seal check; evaluation records never training data | Unity `EvaluationTests` (EditMode) |
| Full flow in the real app against a throwaway backend, with all seals verified and a reproducible report | Unity `EvaluationPlayModeTests.Live_BlindedTest_...` |
| Figures come from the export, CSVs match the stored model and report, refit reproduces | `backend/tests/test_figures.py` |

## 13. Still not done

- No online or automatic retraining.
- No non-linear models.
- No use of reasons text, timing or edit history.
- No pooling across people: every model and every test is about one person's choices.
