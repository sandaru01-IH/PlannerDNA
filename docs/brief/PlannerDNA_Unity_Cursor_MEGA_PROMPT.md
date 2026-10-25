# PLANNERDNA — UNITY + PERSONALIZED ML
## CURSOR MASTER IMPLEMENTATION PROMPT / TECHNICAL CONTRACT

> **How to use:** Place this file at the repository root (beside `PlannerDNA_Unity/` and `backend/`). In Cursor Agent mode, first say: **"Read `PlannerDNA_Unity_Cursor_MEGA_PROMPT.md` and `PlannerDNA_Beginner_Technical_Guide_Unity.md` fully, inspect the workspace and Unity version, then implement Phase 0 and Phase 1 as a functional, tested vertical slice. Explain every manual Unity step. Do not jump to later phases before Phase 1 passes acceptance tests."** The complete document also defines Phases 2–5 for continued implementation.

## 0. SYSTEM ROLE AND DEFINITION OF DONE

You are an expert Unity technical lead, Unity C# engineer, urban GIS analyst, Python backend engineer, ML researcher, test engineer, and patient teacher. Build **PlannerDNA**, a Windows-first, fully digital, single-user, local-first urban planning game and *individual preference-learning research prototype*. The user knows planning concepts but is a beginner in coding. Explain setup, file locations, commands, failures and trade-offs simply.

**Mission:** The human edits synthetic urban neighbourhoods and compares fully specified design alternatives. A real statistical preference model learns from their *explicit A/B choices* and is then evaluated on *unseen synthetic scenarios* for its ability to predict the SAME user's later choices. The app may eventually suggest feasible designs. This is NOT a general model of professional planners, not a causal model of decision-making, and not an autonomous urban planning expert.

**Non-negotiable product constraints**

1. Everything DIGITAL. No cardboard, markers, DJI camera, outdoor filming, external datasets or additional humans required. Mouse + keyboard input first. Optional camera gestures are a late-stage plugin with explicit opt-in only.
2. **Unity** is the interactive environment, using C# for city state, edits, procedural generation, metric calculations, UI, alternative generation and rendering. Do NOT create a React/Three.js frontend instead.
3. **Python FastAPI + SQLite + scikit-learn** form the local backend. The two programs exchange versioned JSON over HTTP at `127.0.0.1:8765`.
4. The game MUST work offline, even when the Python server is down. Keep a durable locally queued log of events and retry idempotently; the app clearly displays sync state.
5. All displayed outcomes are SYNTHETIC, deterministic, and transparent. Costs are synthetic credits, housing is capacity-proxy units, walking access is a network-based proxy. Do not claim modelled traffic, flood depth, prices, carbon, health or temperature without validated specialist models.
6. A model's learned preferences are statistical associations from choice labels, NOT revealed internal motivations, values, expertise or causality.
7. NO fake AI, false metrics, placeholder buttons, or unexplained magic constants. An untrained model must say 'Not enough comparisons' rather than pretend to learn.
8. No cloud APIs, paid dependencies, accounts or LLM calls. AI must be actual fitted, testable machine learning.
9. Do not overwrite an existing Unity project or manually alter its `ProjectSettings` without inspection. Use the currently installed compatible Unity 6 LTS version (prefer 6.3 LTS if a new project is being created). Lock precise editor version in docs once detected. Avoid claiming an editor compile passed without running it.
10. Incremental quality gates: ship a REAL working vertical slice, test it, then extend. Do not flood the repository with empty scaffolding or hundreds of decorative assets.
11. Log and preserve all schema, algorithm, scenario-generator and ML-model versions for repeatability.
12. Provide human-readable documentation and a self-check report at each phase: implemented, manually tested, automatically tested, not tested, blockers, commands to run, next steps.

## 1. TARGET USER EXPERIENCE: ONE FINISHED CHALLENGE

Project title: **PlannerDNA — Personal Urban Decision Sandbox**.

Initial scenario: **The Neighbourhood Green-Space Dilemma**.

- Load a seeded, stylized 3D residential neighbourhood on a square grid.
- Mission: improve park access while protecting housing capacity, respecting an intervention budget and max floor height.
- Edit: add a park, change land use, demolish eligible building, increase/decrease floors (subject to maximum), undo, redo, reset, save.
- Planning dashboard: park access %, housing capacity units, park share %, intervention cost (synthetic credits), constraint pass/fail, changes since baseline.
- Preserve A and B as snapshots from SAME ORIGINAL scenario. Show *side-by-side results* and independent camera previews if feasible, but a functional alternation preview is okay for first milestone.
- Ask: **Which alternative would you choose?** Offer A, B, Tie, Skip, and optional reason. Do not infer the answer from activity/undo clicks.
- Record enough choices; provide a visible **Train my model** control and show count of independent comparisons.
- Later, rank unseen candidate designs under the same hard constraints and compare its predictions to the user's blinded real choices.
- Privacy: all data local, export JSON/CSV, delete/reset with clear confirmation.

### Presentation style

Elegant desktop urban-design lab. Clean neutral light base; readable typography, restrained single accent, orthographic/isometric camera default, clear tools and progress. Render simple but attractive geometry: parks with trees, residential blocks with floor bands, commercial buildings distinct, grey connected roads, selection highlight, helpful tooltips and a north-arrow/scale legend. Prioritize interaction accuracy over cinematic fidelity. Camera should orbit, pan, zoom, focus selected parcel and avoid accidental editing while dragging UI.

### Required Screens

1. **Home / New Challenge**: New seeded scenario; Resume; Learning Lab; About data.
2. **Urban Sandbox**: 3D scene, tool palette, instructions, current metrics, constraints, timeline, save/load, A/B checkpoint buttons.
3. **Compare**: Identical baseline/scenario verification; metric deltas and tradeoffs; explicit A/B/Tie/Skip choice; optional reason; same design must not compare to itself.
4. **Learning Lab**: number of unique comparisons, scenario coverage, whether model can train, version ID, simple coefficients, evaluation metrics, caveats.
5. **Prediction / Evaluation**: blind test flow: show candidate A/B, collect real choice FIRST, then reveal locked model prediction and log score. No training from test labels until assessment is closed.
6. **Settings / Data**: server status, local data path, export, full delete, generator/feature version, instructional definitions.

## 2. STACK AND UNITY PROJECT SETUP

**Choose** Unity 6.3 LTS or the actual installed compatible Unity 6 LTS, **3D URP**. Use standard Unity C#, primitive mesh generation (no paid assets), Unity Input System if installed and supported (fallback to documented classic mouse input, do NOT mix incompatible calls blindly), Unity UI Toolkit UXML/USS for runtime desktop UI. Avoid DOTS/ECS, networking services, Unity ML-Agents and model-inference packages during MVP.

**Why:** Unity for the sandbox; Python service for statistics and learning. Avoid embedding Python runtime into Unity and avoid requiring Unity's ML-Agents for simple supervised preference fitting.

**Repo layout (adapt to existing repo without destructive changes):**

```text
PlannerDNA/
├── PlannerDNA_Unity/                  # Unity Hub opens THIS folder
│   ├── Assets/
│   │   └── PlannerDNA/
│   │       ├── Scenes/
│   │       │   └── PlannerDNA_Main.unity
│   │       ├── Scripts/
│   │       │   ├── Core/            # models, city state, pure calculations
│   │       │   ├── Generation/      # deterministic seeded scenarios
│   │       │   ├── Commands/        # validate/apply/undo/redo
│   │       │   ├── Metrics/         # testable metric engine and contracts
│   │       │   ├── Rendering/       # world visualization, picking
│   │       │   ├── UI/              # UI Toolkit controllers
│   │       │   ├── Storage/         # local snapshots, queue and sync
│   │       │   ├── Networking/      # FastAPI DTOs and client
│   │       │   ├── Suggestions/     # feasible alternative search
│   │       │   └── Editor/          # optional 'Build Demo Scene' menu
│   │       ├── UI/                 # UXML + USS
│   │       ├── Materials/
│   │       ├── Prefabs/
│   │       ├── ScriptableObjects/
│   │       └── Tests/              # Unity EditMode/PlayMode tests
│   ├── Packages/
│   └── ProjectSettings/
├── backend/
│   ├── app/
│   │   ├── main.py
│   │   ├── api/
│   │   ├── schemas/
│   │   ├── storage/
│   │   ├── features/
│   │   ├── ml/
│   │   └── services/
│   ├── tests/
│   ├── requirements.txt
│   └── README.md
├── docs/
│   ├── START_HERE.md
│   ├── architecture.md
│   ├── scenario-and-metrics.md
│   ├── data-dictionary.md
│   ├── ml-protocol.md
│   ├── research-limitations.md
│   └── runbook.md
├── .gitignore
├── README.md
├── PlannerDNA_Beginner_Technical_Guide_Unity.md
└── PlannerDNA_Unity_Cursor_MEGA_PROMPT.md
```

**Important Unity editor integration:**
- Unity serializes scenes/prefabs with GUIDs and companion `.meta` files. Keep them in version control. Never delete meta files or regenerate GUIDs without reason.
- Scripts written in Cursor don't automatically create a scene with wired components. When possible, write an Editor menu command `PlannerDNA > Create/Repair Demo Scene` that constructs cameras, lighting, required components, UI, materials and saves the scene. A menu command must preserve intentional user scene changes and preferably target a generated scene path only.
- If automated editor scene creation is unavailable, provide exact numbered Unity clicks to construct the scene. Never claim that editing `.cs` alone makes a playable game.
- Recompile in Unity, inspect Console, and report compile errors/fixes. Distinguish tests run from tests merely written.
- Prefer transparent well-named scripts; avoid massive single-file managers, hidden third-party dependencies and unexplained reflection.

**Windows environment steps to document:** Unity Hub project creation; folder opened by Cursor; Python 3.11/3.12 venv; `pip install -r requirements.txt`; `python -m uvicorn app.main:app --host 127.0.0.1 --port 8765 --reload` from `backend`; test `http://127.0.0.1:8765/docs`; Unity Play mode and Windows standalone build.

## 3. SOURCE-OF-TRUTH SIMULATION DATA MODEL

### 3.1 City definition

Start with **20×20 cells**, logical **40 m** each (800×800 m conceptual coverage). For Unity rendering, use **one or a few Unity world units per cell** and store metres independently. Never derive real physical distances from arbitrary visual mesh scale.

- `CityState`: schemaVersion, generatorVersion, metricVersion, scenarioId, integer seed, width=20, height=20, cellMetres=40, immutable-or-copied `List<CellData>`, scenario constraints, baseline metadata.
- `CellData`: `id=rXX_cYY`, row, col, `landUse=road|residential|commercial|park|vacant|civic`, `floors` (0 for non-buildings), `unitsPerFloor` (for housing), `maxFloors`, editable flag, optional zoning restriction. Avoid Unity `GameObject` references in logical serializable state.
- Use a deterministic PRNG `System.Random(seed)`, deterministic iteration order and generator version. Same seed, generator version, and parameters MUST result in identical state and metrics.
- Initial layout must guarantee a **connected walkable road graph** and valid building access to roads. Generate roads on a connected grid pattern (e.g., every 4th or 5th row/column plus connectors), residential/commercial parcels, some vacant sites, and at least one park. Seeds must avoid already-perfect accessibility when mission asks for improvement: regenerate deterministic candidates with a bounded number of attempts and display friendly error when none satisfy generator requirements.
- The initial challenge should be feasible. Validate that at least one candidate edit can improve access within constraints; add a fixture seed for guaranteed demo.
- `ScenarioDefinition`: mission ID, mission text, score/proxy definitions, budgetLimit=100 synthetic credits (fixture adjustable), minimum housing ratio 0.9, maximum building floors 8, walking access threshold 400 m.
- Roads fixed for first scenario. Road editing and network connectivity challenges can be later expansions; do not make first-phase complexity explode.

### 3.2 Strict model/view separation

**Only logical state drives metrics and saves.** `GridRenderer` creates/upgrades primitive meshes from `CityState`. The Unity view is NOT a database. If scene objects are destroyed and recreated from a JSON snapshot, data and metrics should remain identical. For JSON serialization avoid assuming `JsonUtility` supports dictionaries; use simple serializable DTOs with arrays/lists or a tested documented serializer. Add round-trip tests.

### 3.3 Commands / transactions

Commands: `SetPark`, `SetResidential`, `SetCommercial` (where zoning allows), `Demolish`, `RaiseFloors`, `LowerFloors`, `RestoreBaseline`, `Undo`, `Redo`. Every command includes target cell, source type (`mouse`, `keyboard`, `gesture_optional`, `assistant_generated`), unique command/event ID, timestamp and sequence. Validate before applying. Snapshots/commands should support reliable undo/redo and action-log replay.

Key cases:
- Cannot raise a road/park/vacant plot.
- Cannot exceed zoned max floors or go below valid floor bounds.
- Cannot convert protected/road cells in Phase 1.
- Rejected action gives visible explanation; optionally log invalid attempt separately, NOT as a completed edit or model label.
- Undo/redo create an accurate timeline with explicit reversal semantics. When an action is undone and later re-applied, do not count it as independent preference training.
- A changed parcel then restored to baseline carries **zero net-change cost** if no other differences remain. Cost comes from the final delta vs baseline, not cumulative click count.
- Every saved A/B alternative must derive from the same `scenarioId`, original baseline, generator version, and metric version; comparison must block mismatches.

## 4. METRIC ENGINE: REPRODUCIBLE AND HONEST

Write pure, unit-tested C# classes. Add a **Metrics Explained** panel: formulas, denominators, synthetic status, units, and limitations.

**Housing capacity units**

`housingUnits = SUM(residential cell.floors * residential cell.unitsPerFloor)`.

For prototype use consistent `unitsPerFloor=4` for residential cells; intentionally label a synthetic housing capacity proxy, NOT housing population. Show baseline, current, difference and percent change.

**Park area share**

`parkSharePct = 100 * parkCellCount / nonRoadCellCount` (denominator excludes only road cells; describe it). Cells are equal area in first scenario.

**Road-network accessibility**

- Roads form a 4-neighbour adjacency graph; every edge between neighbouring road centres costs 40 m.
- A residential cell connects to adjacent road cells with a documented centroid-to-centre connection of 40 m (or consistent explicit rule). A park connects to adjacent road cells similarly.
- For each residential parcel, calculate shortest road-path distance to any park entry with BFS (equal edge lengths). Add parcel entry/exit links to route distance.
- If no road access to parcel or park, mark that residential parcel inaccessible. No Euclidean fallback.
- Park access for that residential cell is true if shortest network distance is ≤400 m.
- `accessPct = 100 * accessibleHousingUnits / totalHousingUnits`; if denominator 0, N/A (not 100%). Display that housing capacity is a proxy weighting, not observed household populations.
- Multiple parks and multiple accessible road entry nodes must be handled. Validate with hand-computed test grids and disconnected roads.

**Synthetic intervention cost**

Define first-prototype, plainly fictional, editable `CostRules` (e.g., conversion from vacant to park 20 credits; residential/commercial demolition 15 credits; park creation on cleared building site 20 additional credits; each new residential floor 5 credits). Crucial: derive net costs from baseline parcel state to final parcel state in a deterministic method, not click histories. For example demolition+park is charged as a documented composite cost. Define a full explicit transition table for every permitted baseline-to-current land-use pair: vacant→park, vacant→residential/commercial, built→park, built→vacant, built→a different use, plus net floor increases/decreases; disallow any transition without a defined cost rule. A reasonable initial *fictional* example is vacant→park 20, vacant→residential 25 + 5 per floor, built→park 35, built→vacant 15, and residential floor additions 5 per added floor (all synthetic credits). Prevent restoring existing buildings from earning negative budget. Treat demolition/displacement as a separately reported output. If a building is restored exactly to baseline, cost 0 for that cell. Costs are NOT LKR, finance or construction estimates.

**Displacement proxy**

`lostOriginalHousingUnits` = housing units lost on originally residential cells after considering their current capacity; also report total net housing change separately. Don't assert real residents are displaced.

**Hard constraints**

- `spentCredits <= budgetLimit`.
- `housingUnits >= 0.90 * baselineHousingUnits`.
- `floors <= maxFloors` for each cell.
- All comparisons intended for AI recommendation require both candidates be feasible; manual exploration can temporarily violate constraints, visually warned.
- Future tasks may define new rules, but each rule is versioned and tested.

**Tests:** exact toy grids for park distance = threshold, distance > threshold, missing park road link, zero housing, inaccessible cells, floor adjustment, constraint just at boundary, undo and reset, identical seed. No hidden random influence on metrics.

## 5. USER INTERFACE AND GAMEPLAY

### Sandbox composition

- Fixed header: project name; scenario title; seed/version; Save/Load; Online/Offline sync.
- Main viewport: central/left 3D city (~65-75% desktop width), orbit/pan/zoom camera, clickable parcel, focused selection with parcel ID and land-use, hover highlight.
- Left toolbar: Select, Add Park, Set Residential, Set Commercial, Demolish, Height +, Height −, Undo, Redo, Reset.
- Right analysis panel: Mission, metric cards, original/current/delta values, constraints, explanations; show positive/negative trade-offs with **appropriate context**, not one simplistic goodness colour.
- Bottom timeline: chronological actions with Undo state and scenario checkpoints; no fake inferred motivations.
- Two explicit 'Save A' and 'Save B' controls with success feedback; compare only after both saved.
- Accessible labels, keyboard shortcuts shown, clear invalid action errors, recoverable server error, understandable loading state, scalable desktop UI.
-- A **Helper/Glossary** panel must explain synthetic credits, housing proxy, network park access, comparison, training, held-out tests and prediction uncertainty.

### Alternatives and deliberate labels

- Alternative A/B snapshots are different, same baseline, same metric schema. Show changes from baseline and difference A vs B.
- Choice A, B, Tie, Skip, optional user-written reason; reason is stored for qualitative analysis but NOT used by numeric classifier in MVP.
- Confirm a labelled choice once; add idempotency UUID so retries don't create duplicates.
- Do not silently turn a comparison into training if it was designated blinded testing.
- For repeated-choice experiments, optionally present same pair again with changed screen order, but mark linked pair IDs and don't count repeats as independent unseen cases.
- Display "Training samples: N unique direct comparisons across S scenarios" (do not double counts through augmentation).

## 6. PHASED SERVER AND STORAGE CONTRACT

Backend: Python 3.11/3.12; FastAPI; Pydantic models; SQLite (`sqlite3` or SQLAlchemy with migrations); NumPy; scikit-learn; joblib; pytest. Bind ONLY to loopback (`127.0.0.1`), no public server, no cloud dependency. Use consistent content-type `application/json`; explicit CORS only if required (Unity desktop normally doesn't need browser CORS).

### Minimum API endpoints (versioned)

| Method | URL | Role |
|---|---|---|
| GET | `/api/v1/health` | Backend ready, schema version, model status |
| POST | `/api/v1/scenarios` | Register generated scenario baseline, metadata |
| POST | `/api/v1/sessions` | Start/resume session |
| POST | `/api/v1/events` | Idempotently record edit/undo/redo events |
| POST | `/api/v1/alternatives` | Save snapshot and validated metrics/features |
| POST | `/api/v1/preferences` | Save explicit A/B/Tie/Skip choice, training vs evaluation mode |
| GET | `/api/v1/preferences/stats` | Unique labels and scenario coverage |
| POST | `/api/v1/models/train` | Fit a model if data adequate; store model version |
| POST | `/api/v1/models/predict-pair` | Predict P(A chosen over B) from features and saved model |
| POST | `/api/v1/models/rank-candidates` | Rank constraint-feasible candidate vector set |
| GET | `/api/v1/models/history` | Show model versions and evaluation metadata |
| GET | `/api/v1/export` | Produce portable reproducible JSON/CSV data |
| DELETE | `/api/v1/data` | Explicit confirmed local data deletion; cannot be invoked casually |

Additional endpoints only where needed. Return structured errors with clear messages, validation with Pydantic, no silent false success. Include `/docs` automatic interactive documentation and sample calls. Pydantic ensures request models have expected keys and data types.

### Example preference request

```json
{
  "comparison_id": "b06d210b-7a95-4422-85fe-d1da2aa18704",
  "session_id": "session-42",
  "scenario_id": "green-42",
  "alternative_a_id": "alt-a-42",
  "alternative_b_id": "alt-b-42",
  "choice": "A",
  "reason": "more park access was worth the extra cost",
  "mode": "train",
  "shown_left_alternative_id": "alt-b-42",
  "model_version_locked": null
}
```

### Data schema and IDs

Tables (SQLite migrations from v1):

- `scenario(id PRIMARY KEY, seed, generator_version, metric_version, city_baseline_json, constraints_json, created_utc)`
- `session(id, scenario_id FK, started_utc, ended_utc, session_status)`
- `event(event_id UNIQUE, session_id FK, sequence, event_type, action, cell_id, before_json, after_json, metrics_before_json, metrics_after_json, input_source, utc_time, reversal_of_event_id NULLABLE)`
- `alternative(id, scenario_id FK, session_id FK, snapshot_json, metrics_json, features_json, feature_version, constraint_state_json, created_utc)`
- `comparison(comparison_id UNIQUE, scenario_id FK, alt_a_id FK, alt_b_id FK, choice, reason, mode, display_order_json, repeated_pair_id NULLABLE, model_version_locked NULLABLE, created_utc)`
- `model_run(id, trained_utc, model_kind, model_artifact_path, feature_version, training_scenario_ids_json, unique_comparison_count, hyperparameters_json, evaluation_json, random_seed)`
- `prediction(id, model_run_id FK, scenario_id FK, comparison_id NULLABLE, candidate_features_json, predicted_probability, revealed_after_choice, created_utc)`
- `schema_migration(version, applied_at_utc)`

Do not store raw video. Optional future local gesture processing should store only consented derived commands, not recording by default. Use parameterized database operations, uniqueness constraints, transactions, check foreign keys, and store all data locally. Never load pickled models from untrusted sources; own joblib models are generated and loaded only from local application storage.

### Robust offline queuing

Unity must execute actions locally and immediately. Write an append-only or atomic JSON-backed outbound queue under `Application.persistentDataPath`, with unique message ID, creation order and delivery status. When Python is online, send in order, await acknowledged response, and delete/mark delivered idempotently. Backend deduplicates IDs using UNIQUE constraints. Queue must survive Unity process restarts and network errors. Prevent duplicate comparisons, despite retries. Build tests for offline-to-online transition.

## 7. GENUINE MACHINE LEARNING: PERSONAL PAIRWISE PREFERENCES

### Model philosophy

Fit a **regularized, interpretable pairwise logistic regression** using scikit-learn. This is preferable to deep neural networks initially because one person will supply few labels. Never imply "thinking" or a complete personality model. Real comparison labels, not action frequency, determine the supervised targets.

### Feature schema v1 (define exact consistent normalization)

Use a small set, all with declared directions and reproducible scaling:

1. `delta_access_fraction`: (accessPct - baselineAccessPct) / 100 (unitless fraction).
2. `delta_housing_fraction`: (housingUnits - baselineHousingUnits) / max(1, baselineHousingUnits).
3. `delta_park_share_fraction`: (parkSharePct - baselineParkSharePct) / 100.
4. `negative_budget_fraction`: -(spentCredits / max(1, budgetLimit)).
5. `negative_lost_housing_fraction`: -(lostOriginalHousingUnits / max(1, baselineHousingUnits)).

These directions only establish input semantics, not the user's learned weights. Monitor correlated features, coefficient instability and fairness of candidate diversity. Store feature vector AND version with every alternative. Never fit scaling on held-out data. Standardization should preserve pair ordering symmetry (e.g. `StandardScaler(with_mean=False)` on pairwise differences fitted to training only, or fixed versioned scales).

### Comparison formulation

For comparable alternatives A and B:

`x = f(A) - f(B)`.

`P(prefer A to B) = sigmoid(w @ x)`.

- Create a single real record per comparison with outcome A/B. Ties/skips retained in DB but excluded from binary training v1.
- Internally form reversed rows if necessary for balanced classes, with no-intercept logistic regression, L2 regularization, a constant random seed and documented fixed/scaled features. This augmentation is NOT additional independent evidence.
- After fitting, ensure approximately `P(A>B) + P(B>A) = 1` for each test pair.
- Show no prediction if no trained model exists. Never silently use hardcoded weights while labelling them a fitted model.
- If too little or too homogeneous preference data exists, reject train request with meaningful explanation and continue collecting comparisons. Suggested UI warning threshold: 30 unique choices over at least 5 varied scenario seeds; treat threshold as heuristic, NOT statistical adequacy. For credible evaluation aim for 60–100 comparisons over more diverse scenarios.
- Save model file, features, schema, exact dataset scenario IDs, timestamp, package versions and random seed.
- Display explanatory differences between A/B and fitted feature contributions, with a warning that these are statistical correlations and may not be stable.

### Candidate plans and learning

- Unity creates 10–50 plausible candidate **city states** from bounded rule-driven perturbations: add eligible parks, increase residential heights, optionally move land uses subject to zoning and mission.
- Compute the same metrics as manual edits using `MetricEngine`.
- Remove any candidate violating constraints, and deduplicate identical snapshots.
- Ensure candidates differ meaningfully; provide not just trivial near duplicates. Use seeded candidate generation for reproducibility.
- Backend ranks **only feasible** candidates with fitted preference utility `w · featureVector`, filters excessive near duplicates for variety. No suggestion is "optimal" unless a carefully defined objective/search procedure justifies that label.
- Display model version and 'synthetic scenario' banner in suggestion interface.

### Research evaluation (critical)

Avoid data leakage: split by ENTIRE scenario seeds, not by row, event or randomly within the same scenario. Prefer prospective held-out testing after the model is frozen:

1. Pre-register the training scenario list and unseen evaluation scenario seeds.
2. Fit model using only training comparisons.
3. For each new evaluation pair, freeze/store `P(A)` and exact model version BEFORE recording the user's choice.
4. Randomize candidate left/right presentation.
5. Ask the human choice BEFORE revealing the prediction; once chosen show model prediction and explanation.
6. Never retrain based on evaluation choices until evaluation is formally closed and a new model/run has been created.
7. Score accuracy, log loss, Brier score, unique evaluation scenario count and paired-choice repeat consistency. Report raw counts as prominently as the percentages. Confidence intervals should recognize clustering by scenario where feasible; do not compute misleading precision from mirrored training rows.
8. Compare against chance (P=0.5), cheapest-option, access-maximizing rule, and static equal-weight score baselines. State that chance benchmark accuracy depends on balanced evaluation, not automatically 50% on unbalanced choices.
9. If consistency is low, report it, as a limitation for predicting a particular human choice; do not declare the algorithm failed or succeeded without acknowledging ambiguous labels.
10. Produce transparent JSON/CSV `research_report` with split IDs, seed lists, metrics, warnings and model version. No fabricated performance scores.

Only consider sequence learning or inverse reinforcement learning after enough authentic interaction traces exist, and clearly document identifiability limits. Unity ML-Agents is optional for future *autonomous agents* exploring simulated cities, but is not a dependency for Phases 1–4.

## 8. TECHNICAL QUALITY RULES

**Unity C#:** Strong typing; focused classes; core logic independent of MonoBehaviour; deterministic tests; no external assets needed; avoid expensive full-world rebuilding every frame; redraw changed tiles and optionally recalculate metrics after committed actions. Use safe lifecycle management and avoid blocking main thread during HTTP calls. Use coroutines or async supported safely by the selected Unity version.

**JSON:** Stable versioned DTO fields with same casing and types expected by Pydantic. JSON utility limitations are handled deliberately. Round-trip tests for CityState, snapshots, saved alternatives and requests. Do not send runtime mesh objects or Unity types to Python.

**Python:** Typed models and functions, clear validation errors, no internet dependencies during runtime, `pytest` coverage of endpoints, database migrations, model train/predict, no-data, tie/skip, incorrect feature version, sign-symmetry, train/test separation and repeated idempotent events.

**Testing:** Unit tests for metrics and state engine; Unity PlayMode smoke test for selection/UI; Python integration tests for save/retrieve/sync/train/predict; Windows launch procedure. If Unity Editor cannot be invoked by Cursor, say so honestly and supply exact manual test instructions. Do not mark manual tests passed unless user confirms or observable logs prove it.

**Security & privacy:** Local bind, no exposed ports, no arbitrary SQL, clear disk paths, safe exports, confirmation for destructive actions. Data deletion must remove database data and locally stored fitted models, with warning and backup option. No hidden telemetry.

**Performance:** 400 primitive tiles plus building meshes should work comfortably. Focus on logical correctness and approachable UX; avoid premature heavy systems such as ECS, procedural high-resolution city assets, deep nets or VR integration.

**Git:** Use a proper Unity `.gitignore`, include `.meta`, scenes and package manifests; exclude `Library/`, `Temp/`, `Logs/`, `Obj/`, `Builds/`, `.venv/`, databases/models with personal data, and secrets. Provide example config without binding to an inaccessible absolute Windows path.

## 9. IMPLEMENTATION PHASES AND ACCEPTANCE TESTS

### Phase 0 — Environment and plumbing (FIRST)

Tasks:
- Inspect repository without damaging existing work; verify Unity version and installed input/UI packages if known, else ask user to check Unity Hub/Package Manager without blocking code preparation.
- Set up Unity folder and Python backend folder (if Unity Editor can't be created headlessly, provide Unity Hub steps and create scripts separately).
- Backend `/api/v1/health`, `/docs`, repo README, `.gitignore`, `docs/START_HERE.md`, Windows PowerShell commands, clear app online/offline indicator.
- Unity can enter Play mode with a simple camera/light and a persistent world controller, no compile errors, a clearly labelled "PlannerDNA" UI.

Acceptance: `pytest` passes; Python `uvicorn` returns valid health JSON; Unity scene manually tested in Play mode or marked "requires manual test"; documentation tells a beginner exactly how to start everything.

### Phase 1 — Real playable Green-Space Dilemma (PRIORITY)

Tasks:
- Deterministic 20×20 synthetic city, accessible camera, connected roads, parcels/buildings/parks; generator unit tests.
- Pure logical city model, grid renderer, raycast parcel selection, responsive toolbar, edit validators, six+ meaningful actions, undo/redo, reset, save/load.
- Accurate defined housing/park/access/cost metrics and hard constraints. Explain calculation via UI.
- Create A and B from a common baseline, compare deltas, label preference locally, persist to durable on-disk JSON if backend is not implemented/online.
- Mission guidance and all critical buttons functional.

Acceptance:
- Fixed demo seed creates identical city twice.
- Adding a park at eligible vacant parcel changes park metric predictably and may change road access if reachable; no access improvement if unreachable.
- Raising residential floors increases housing by `unitsPerFloor` exactly.
- Undo restores exact city JSON and metrics; complete restoration yields zero net cost.
- Invalid height/road edit shows reason; over-budget candidate cannot be submitted as feasible.
- Save Alternative A, restore original baseline, create B, compare; choosing A/B/Tie/Skip persists.
- Unity cannot be blocked by Python being offline. The UI does not display fake trained results.

### Phase 2 — Durable local research notebook and synchronization

Tasks:
- SQLite tables and migrations, Pydantic DTOs, API endpoints for scenarios, sessions, events, alternatives and preference choices.
- Offline Unity queue with retries and server idempotency; restarted Unity and backend recover correctly.
- Local exports, deletion confirmation, data dictionary, timeline, comparison stats.

Acceptance: all event/choice records survive process restart; retrying exact ID does not create duplicates; invalid preference or mismatched scenario is refused; JSON export can reproduce city alternatives; data remains local.

### Phase 3 — Actual personal AI

Tasks:
- Versioned feature extraction and storage; explicit training button; symmetric regularized logistic model with tests.
- Model history, diagnostic evaluation, candidate generator and feasible ranking.
- Interface displays adequate-data warning, weights, trade-offs, model version, limitations.

Acceptance: with synthetic **test fixture labels** (NOT user labels) that systematically favor one feature, tests show the fitted model learns the correct direction; no-data and homogeneous-data states gracefully refuse; reversed pair probability sums to 1; genuine user predictions based on saved model.

### Phase 4 — Honest blinded evaluation

Tasks:
- Lock train/test scenario IDs, model freeze, collect labels before revealing predictions, randomize candidate position, repeat-choice consistency, evaluation report and baseline comparisons.

Acceptance: held-out labels never enter training model during locked evaluation; test report contains exact counts, seeds, model version, accuracy/Brier/log loss plus caveats; report export reproducible.

### Phase 5 — Optional later modules (NOT REQUIRED FOR FIRST RELEASE)

- Hand landmark recognition and gesture-to-command interface using DJI-compatible webcam input, with explicit source choice; do not mix camera video with planning preferences by default.
- Decision-sequence modelling after enough real traces, clearly separated from pairwise choice labels.
- Richer urban modules (transit, shading, vertical access, mixed land use), each with its own validated metric engine and independent research question.
- Optional Unity ML-Agents for independently trained game agents (not the personal choice model).

## 10. CURSOR WORK RULES AND REQUIRED REPORT AT END OF EACH PHASE

1. **Read this entire specification and the beginner guide**; inspect existing files, project version and potential conflicts.
2. Before editing, provide a 4–8 sentence implementation summary and identify the selected phase.
3. Implement real code and assets; do not give only explanations or pseudocode when the phase is authorized.
4. For complex tasks, progress in small coherent file sets; verify after each set. Never replace working code with attractive empty components.
5. If Unity editor build/Play tests can't run in Cursor, make the required menu scene bootstrap and give exactly the Unity clicks to test; label tests pending rather than claiming success.
6. Fix observed Python syntax, unit tests and static errors before handoff. Record untested platform-dependent work.
7. If a package or API has changed, inspect actual installed version / official docs, don't invent names or method signatures.
8. At end report: **Files created/changed**, **How to start the backend**, **How to open Play mode in Unity**, **Exact functional demo steps**, **Tests passed**, **Tests requiring human verification**, **Known limitations**, **Next authorized phase**.
9. Default first invocation implements **Phase 0 and Phase 1 only**. This is not avoiding later work; it prevents the core simulator from being buried under unverified complexity. Wait for explicit `Continue with Phase 2` and so on. If the user explicitly asks for all phases, work sequentially and stop clearly if an integration gate fails.
10. Never suggest the user film pedestrians, construct a physical model, invite other participants, or purchase hardware; single user in one room is a hard requirement.

## 11. FINAL WORKING DEMO SCRIPT (THE USER MUST BE ABLE TO FOLLOW)

1. Start `backend` and open health docs; optionally stop it later to demonstrate offline functionality.
2. Open `PlannerDNA_Unity` using Unity Hub; open generated scene; hit Play.
3. Click **New Challenge** with fixed demo seed. Orbit the 3D world and inspect initial metric card explanations.
4. Select an eligible vacant plot, click **Add Park**; verify correct parcel changes and cost/park access recalculates.
5. Select a residential parcel, click **Height +**; verify housing units increase exactly as defined.
6. Undo and redo once, verify metrics match snapshots.
7. Save **Alternative A**; restore common baseline; build a different feasible design; save **Alternative B**.
8. Open comparison screen and select preferred alternative; provide optional reason.
9. Open History/Data and verify preference saved to SQLite (or local queue if server stopped).
10. Repeat on varied seeded scenarios. Once suitable labels exist, train model in Learning Lab.
11. Open a new held-out scenario in evaluation mode; choose preferred pair BEFORE model reveals its prediction; examine performance report.
12. Export reproducible research data; confirm no data left computer without explicit action.

**Remember:** A real, reliable, measurable single-user planning experiment is more valuable than a visually sophisticated demo that fakes machine learning or makes unverifiable urban predictions.
