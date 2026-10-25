# Data dictionary (schema version 1)

Everything stays on this computer. There are two stores:

1. **Unity local files** (always written first, work offline) under `Application.persistentDataPath`:
   `%USERPROFILE%\AppData\LocalLow\DefaultCompany\PlannerDNA_Unity\PlannerDNA\`
2. **Backend SQLite database** (Phase 2, optional), `backend\data\plannerdna.db`, filled from
   Unity's outbox while the local backend runs.

```
PlannerDNA/
  alternatives/<alternative_id>.json   saved Alternative A/B (full snapshot + metrics + features)
  decisions/<comparison_id>.json       one explicit choice per file (A | B | tie | skip)
  saves/<scenario_id>.json             latest saved design for that scenario
  saves/latest.json                    the most recent save (used by "Resume")
  events/<session_id>.jsonl            append-only timeline, one JSON object per line
  outbox/pending/*.json                records waiting to be acknowledged by the backend
  outbox/delivered/*.json              records the backend acknowledged (201 created / 200 duplicate)
  outbox/rejected/*.json               records the backend refused, with its error, kept for inspection
  outbox/next_sequence.txt             queue order counter
  exports/                             files written by Settings > Export
PlannerDNA_backups/backup-<time>/      copies made before a confirmed deletion (outside the data folder)
```

- Files are written atomically: a temporary file is written, flushed, then swapped in, so a crash
  cannot leave half a file.
- File names accept only letters, digits, `-` and `_`.
- Field names are snake_case and identical in the Unity DTOs and the backend's Pydantic models.
  The backend rejects unknown fields.
- Unity's `JsonUtility` cannot write `null` for strings, so an **empty string means "none"**. The
  backend stores those as SQL `NULL`.

## City state (`CityStateDto`)

| Field | Meaning |
|---|---|
| `schema_version` | 1 |
| `scenario_id` | `green-<seed>` |
| `seed`, `generator_version`, `metric_version` | reproducibility metadata |
| `width`, `height`, `cell_metres` | 20, 20, 40 |
| `content_hash` | FNV-1a 64-bit hash of the cell contents (same algorithm in C# and Python). A city whose hash does not match is refused. |
| `cells[]` | `cell_id` (`r07_c12`), `row`, `col`, `land_use` (`road`, `residential`, `commercial`, `park`, `vacant`, `civic`), `floors`, `units_per_floor`, `max_floors`, `editable`, `zoning` (`mixed`, `residential_only`, `protected`) |

Edits may change `land_use`, `floors` and `units_per_floor`. Everything else is fixed, and roads,
civic and non-editable parcels never change.

## Scenario registration (`ScenarioRegistrationDto`, outbox kind `scenario`)

`scenario_id`, `seed`, versions, `mission_id`, `baseline_hash`, `created_utc`, `constraints`
(`budget_limit`, `min_housing_ratio`, `max_floors`, `access_threshold_metres`, `units_per_floor`,
`new_building_floors`), `baseline` (city state) and `baseline_metrics`. The backend recomputes the
baseline metrics before storing. Re-registering the same scenario later is a harmless duplicate
(`created_utc` is ignored for that comparison).

## Session (`SessionStartDto`, outbox kind `session`)

`session_id`, `scenario_id`, `baseline_hash`, `started_utc`, `app_version`. Refused if the
scenario is unknown or its baseline differs.

## Alternative (`alternatives/*.json`, outbox kind `alternative`)

`alternative_id`, `slot` (A/B), `session_id`, `scenario_id`, `seed`, versions, `baseline_hash`,
`state_hash`, `created_utc`, `feasible`, `failed_constraints[]`, `changed_cell_ids[]`, `snapshot`
(city state), `metrics` (all metric values), `constraints[]` (`id`, `label`, `passed`, `detail`) and
`features` (feature vector v1).

Unity recomputes the metrics on load and checks the baseline hash. The backend does the same on
receipt: it recomputes metrics, constraints, feasibility, features and changed cells, and refuses any
difference (tolerance 1e-9).

## Decision (`decisions/*.json`, outbox kind `preference`)

| Field | Meaning |
|---|---|
| `comparison_id` | unique id; a second write with the same id is refused |
| `alternative_a_id`, `alternative_b_id` | the pair. The same pair, even reversed, cannot be recorded twice (backend: UNIQUE `pair_key` + `repeat_index`). |
| `choice` | `A`, `B`, `tie` or `skip` |
| `reason` | optional free text typed by the user |
| `mode` | `train` (practice choices) or `evaluation` (a blinded-test choice, never training-eligible) |
| `shown_left_alternative_id` | which alternative was displayed on the left (backend: `display_order_json`). In a test it must equal the side drawn by the server. |
| `model_version_locked` | empty for practice choices (any value is refused). In a test: the frozen model's version. |
| `evaluation_trial_id` | empty for practice choices. In a test: the blinded trial this choice answers. A pair may be decided twice only as two different test trials (a repeat). |
| `a_feasible`, `b_feasible` | constraint results; the backend checks them against its stored alternatives |
| `training_eligible` | true only for a train-mode A or B choice where both designs are feasible; the backend refuses a wrong value |
| `eligibility_note` | plain-language reason when not eligible |
| `feature_version`, `app_version`, `created_utc` | metadata |
| `sync_status` | historical field from Phase 1 (`local_only`); actual delivery state lives in the outbox |

## Timeline event (`events/*.jsonl`, outbox kind `event`)

| Field | Meaning |
|---|---|
| `event_id`, `session_id`, `scenario_id`, `sequence` | identity and order (backend: UNIQUE `session_id` + `sequence`) |
| `event_type` | `command`, `undo`, `redo`, `checkpoint` (alternative saved) or `decision` |
| `action` | edits: `set_park`, `set_residential`, `set_commercial`, `demolish`, `raise_floors`, `lower_floors`, `restore_baseline`, `load_design`; checkpoint: `save_alternative_a/b`, `load_evaluation_pair` (a blinded-test pair was placed in A/B by the system, `input_source = system`); decision: `record_preference` |
| `cell_id`, `has_cell_change`, `before`, `after` | the parcel affected (`before`/`after` are meaningful only when `has_cell_change`) |
| `metrics_before`, `metrics_after` | summary: housing, access, park share, credits, feasible |
| `input_source` | `mouse`, `keyboard`, `system`, … |
| `reversal_of_event_id` | for undo: the command undone (required); for redo: the undo reversed |
| `original_event_id` | for undo/redo: the original command; checkpoint: the alternative id; decision: the comparison id |
| `note`, `utc_time` | description and time |

Undo and redo are recorded as new events that point back to what they reverse, so history is never
rewritten. Refused attempts are counted in the session and are not written as edits. **Edits are
never treated as preferences**; only explicit decisions are labels.

## Outbox message (`outbox/*/*.json`)

`message_id`, `sequence` (creation order), `kind`, `record_id`, `endpoint`, `payload_json` (the
exact record JSON), `created_utc`, `attempts`, `last_error`, `last_attempt_utc`, `status`
(`pending` | `delivered` | `rejected`), `http_status`, `delivered_utc`.

- A record (kind + id) is queued once.
- Messages are sent oldest first, one at a time, and only while the backend reports a ready database.
- Delivery outcomes:
  - `200` or `201`: the message is moved to `delivered/`.
  - `400`, `409`, `413` or `422`: the backend examined the record and refused it, so the message
    moves to `rejected/` and the queue continues.
  - Offline, timeout, `404`, `429` or `5xx`: the message stays first in line and is retried with
    backoff of 2, 4, 8 … up to 60 seconds.
- A resend after a lost acknowledgement is answered `200 duplicate`.

## Save game (`saves/*.json`)

`schema_version`, `app_version`, `saved_utc`, `session_id`, `scenario_id`, `seed`, versions,
`baseline_hash`, `alternative_a_id`, `alternative_b_id`, `current` (city state). Resuming
regenerates the baseline from the seed and refuses the save if the baseline hash differs. Saves stay
local and are not sent to the backend.

## Backend tables (SQLite, migrations 1–3)

| Table | Key and constraints | Contents |
|---|---|---|
| `schema_migration` | `version` PK | applied migrations |
| `scenario` | `id` PK | baseline city JSON, constraints JSON, recomputed baseline metrics |
| `session` | `id` PK, FK scenario | start time, status, app version |
| `event` | `event_id` PK, FK session + scenario, UNIQUE(`session_id`, `sequence`), CHECK event type | timeline |
| `alternative` | `id` PK, FK scenario + session, CHECK slot | snapshot, recomputed metrics/features/constraints, `feasible` |
| `comparison` | `comparison_id` PK, FKs, CHECK choice/mode, CHECK a ≠ b, UNIQUE(`pair_key`, `repeat_index`) | decisions |
| `model_run` | `id` PK (= model version `pm-<UTC>-<fingerprint8>`), UNIQUE `data_fingerprint`, at most one `status = 'active'` | one row per training run; see below |
| `prediction` | `id` PK, FK `model_run`, scenario, optional comparison; CHECK 0 ≤ probability ≤ 1 | every pair prediction and ranked suggestion |
| `evaluation_plan` | `id` PK, FK `model_run`, CHECK status, at most one `status = 'open'` | one pre-registered blinded test; see below |
| `evaluation_trial` | `id` PK, FKs plan, scenario, both alternatives, prediction, model; UNIQUE(`plan_id`, `trial_index`); CHECK a ≠ b, left ∈ {a, b}; one repeat per trial; a pair is a first presentation only once; UNIQUE `comparison_id` | one test pair with its sealed prediction |

Migration 3 also adds `comparison.evaluation_trial_id` (unique when set), which links a test
choice to its trial.

`evaluation_plan` columns:

- `protocol_version` (`blinded-eval-1.0.0`), `model_run_id` and `model_artifact_sha256` (the frozen
  model, checked again when each prediction is sealed).
- `training_scenario_ids_json`, `training_comparison_count`: what the frozen model was trained on.
- `evaluation_scenarios_json` (`scenario_id`, `seed`), `pairs_per_scenario`, `repeat_count`,
  `pair_rule` (`evalpair-1.0.0`).
- `plan_seed` and `candidate_seed_base`: drawn by the server with `secrets` after the scenarios are
  fixed; they decide the screen sides, the repeated pairs and the candidate seeds. The client
  cannot choose them, and the server refuses any choice whose sides differ.
- `registration_sha256`: hash of the canonical registration, fixed before any test choice.
- `status` (`open`, `closed`), `closed_utc`, `close_reason` (free text, for example why a test was
  stopped early), `report_json` and `report_sha256`.

`evaluation_trial` columns:

- `trial_index`, `kind` (`primary` or `repeat`), `repeat_of_trial_id`, `scenario_id`,
  `alt_a_id`, `alt_b_id`, `pair_key`, `shown_left_alternative_id`, `candidate_seed`, `pair_rule`.
- `prediction_id`, `model_run_id`, `p_a`: the frozen model's P(A preferred), never returned before
  the reveal.
- `sealed_payload` and `sealed_sha256`: the canonical JSON {trial, plan, model, alternatives,
  `p_a`, random nonce} and its SHA-256. Only the hash is returned at registration; the payload is
  returned at reveal so anyone can re-hash it.
- `status` (`awaiting_choice`, `answered`, `revealed`), `comparison_id`, `registered_utc`,
  `answered_utc`, `revealed_utc`.

`model_run` columns (migration 2 added the last seven):

- `trained_utc`, `model_kind` (`pairwise_logistic_l2_no_intercept`) and `ml_version`
  (`pairwise-logit-1.0.0`).
- `feature_version` (`features-v1`) and `random_seed` (20261025).
- `model_artifact_path` (`models/<version>.json`, relative to the data directory) and
  `artifact_sha256`, which is checked on every load.
- `training_scenario_ids_json`, `training_comparison_ids_json` and `unique_comparison_count`.
- `hyperparameters_json` and `package_versions_json` (Python, numpy, scipy, scikit-learn).
- `data_fingerprint`: the SHA-256 of ML version, feature version, hyperparameters and training
  rows. Identical data never trains twice.
- `evaluation_json` (leave-one-scenario-out diagnostics) and `summary_json` (the full report shown
  in the Learning Lab).
- `status`: `active` or `superseded`.

`prediction` columns:

- `prediction_kind`: `pair` or `rank`.
- `predicted_probability`. For `pair` this is P(A preferred). For `rank` it is σ(utility), the
  probability of preferring the candidate to the baseline.
- `candidate_features_json`: the server-recomputed features used.
- `comparison_id`, set for pair predictions of a recorded comparison.
- `revealed_after_choice`: 1 when the prediction was requested for an already recorded choice.
- `in_training_set`: 1 when that comparison was part of the model's training data.
- `created_utc`.

**Model file** (`<data dir>\models\<version>.json`). It holds:

- `format` (`plannerdna-pairwise-model`), `format_version` (1) and `model_version`;
- `model_kind`, `ml_version`, `feature_version` and `feature_names`;
- `weights_raw`, `weights_standardised` and `feature_scale`;
- `hyperparameters` and `random_seed`;
- `training` (comparison ids, scenario ids, counts) and `data_fingerprint`;
- `package_versions` and `trained_utc`.

It is plain JSON and never pickled. Details: [ml-protocol.md](ml-protocol.md).

Every record table (scenario to comparison) also stores `payload_sha256` (canonical JSON of the
request, used for idempotency) and `received_utc`. All SQL is parameterized. Foreign keys are enforced, and every write runs in one
transaction.

## Exports

- **Local** (Settings, works offline): `exports/plannerdna-local-export-<time>.json`,
  containing all alternatives (with full snapshots), decisions, events and saves.
- **Backend** (Settings when online, or `GET /api/v1/export`):
  `exports/plannerdna-backend-export-<time>.json`, with an `export_manifest` (versions, counts,
  notes), plus `plannerdna-comparisons-<time>.csv`. Since Phase 3 the JSON export also lists
  `model_runs` (with their full summaries) and `predictions`. Since Phase 4 it also lists
  `evaluation_plans` (with stored reports) and `evaluation_trials`. While a test is open, a
  prediction that has not been revealed is exported without its probability and sealed payload.
- **Figures**: `backend\tools\make_figures.py` reads such an export (see `docs/runbook.md`).
- Each alternative's snapshot reproduces its city exactly. Metrics can be recomputed from it with
  the versions in the manifest, and the backend tests do exactly that.

## Deleting your data

Use **Settings > Delete all data…**:

- Type `DELETE` to enable the button.
- **Make a backup first** is on by default. It copies the local folders to `PlannerDNA_backups\`,
  which sits beside the data folder, never inside it.
- **Also delete the local backend database** is available while the backend is online. It sends
  `DELETE /api/v1/data` with the confirmation phrase, and with the backup option the backend first
  writes `backend\data\backups\plannerdna-backup-<time>.json`. This also removes every stored
  model file (`backend\data\models\`) and the model history.
- The current challenge is closed, and all local decisions, alternatives, saves, event logs, outbox
  and exports are removed.

To remove backups as well, delete those backup folders yourself.
