# PlannerDNA backend (Phase 4: research notebook, personal preference model, blinded test)

A FastAPI server that stores your planning sessions, A/B alternatives and explicit choices in a
local SQLite file. When you explicitly ask (**Train my model** in the Unity Learning Lab), it can
also fit a small pairwise logistic model to your training-eligible choices. Until then
`model_status` is `no_model`, and nothing is predicted. The method is in
[`docs/ml-protocol.md`](../docs/ml-protocol.md); read
[`docs/research-limitations.md`](../docs/research-limitations.md) before interpreting results.

The Unity sandbox works fully without the backend. Records made while it is off wait in Unity's
outbox and are sent, in order, once it is running.

It binds only to `127.0.0.1` and makes no outbound network calls (a test enforces both).

## Setup (Windows PowerShell, once)

Requires Python 3.12. From the repository root:

```powershell
cd backend
py -3.12 -m venv .venv          # or: uv venv --python 3.12 .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
```

## Run

```powershell
cd backend
.\.venv\Scripts\python.exe -m uvicorn app.main:app --host 127.0.0.1 --port 8765
```

- Data lives in `backend\data\plannerdna.db` (git-ignored). Set `PLANNERDNA_DATA_DIR` to use
  another folder.
- The database and its tables are created or migrated automatically at start-up. Applied
  migrations are recorded in `schema_migration`.
- Check it at <http://127.0.0.1:8765/api/v1/health> or open <http://127.0.0.1:8765/docs>.
- Stop it with `Ctrl+C`. Nothing is lost: every write is committed before the reply is sent.

Never use `--host 0.0.0.0`. If you change the port, also change **Base Url** on the
`HealthMonitor` component (GameObject `PlannerDNA`) in the Unity scene.

## Endpoints (`/api/v1`)

| Method | Path | Purpose |
|---|---|---|
| GET | `/health` | status, `storage_status` (`ready`), `db_schema_version` (3), `model_status` (`no_model` or `trained`), `active_model_version`, `evaluation_status` (`none` or `open`), `open_evaluation_plan_id` |
| POST | `/scenarios` | register a scenario: baseline city, constraint parameters, baseline metrics |
| POST | `/sessions` | start a session on a registered scenario |
| POST | `/events` | one timeline event (command, undo, redo, checkpoint, decision) |
| POST | `/alternatives` | one saved A/B design snapshot |
| POST | `/preferences` | one explicit choice (`A`, `B`, `tie`, `skip`) |
| GET | `/preferences/stats` | counts of decisions and training-eligible comparisons |
| GET | `/sessions/{id}/events` | the session timeline, ordered by `sequence` |
| GET | `/export` | everything as one JSON document with an `export_manifest` |
| GET | `/export/comparisons.csv` | one row per decision with both feature vectors |
| DELETE | `/data` | delete all records and stored models; body `{"confirm": "DELETE ALL PLANNERDNA DATA", "backup": true}` |
| GET | `/models/readiness` | "Training samples: N unique direct comparisons across S scenarios", whether training is possible, reasons, warnings |
| POST | `/models/train` | fit a new model (`201 created`), or return the existing one for unchanged data (`200 unchanged`); `422` when data are inadequate |
| GET | `/models/current` | active model: weights with bootstrap intervals, diagnostics against baselines, warnings, caveats |
| GET | `/models/history` | every model run, newest first (`active` or `superseded`) |
| POST | `/models/predict-pair` | P(A preferred) for two stored alternatives, from the saved model file |
| POST | `/models/rank-candidates` | rank feasible candidate designs (recomputed and validated) by the model's utility |
| GET | `/evaluation` | the open blinded test (with its next pre-registered step), and every earlier plan |
| POST | `/evaluation/plans` | pre-register unseen scenarios, freeze the active model; training is refused until it closes |
| GET | `/evaluation/plans/{id}` | plan status, progress and next step |
| POST | `/evaluation/trials` | register the next pair; returns the left/right side and only a SHA-256 seal of the prediction |
| POST | `/evaluation/trials/{id}/reveal` | open a sealed prediction, allowed only after that choice is stored |
| POST | `/evaluation/plans/{id}/close` | close (finished or stopped early), build and store the report and its SHA-256 |
| GET | `/evaluation/plans/{id}/report` | stored report, recomputed from the database; `reproducible` when all hashes agree |
| GET | `/evaluation/plans/{id}/trials.csv`, `/metrics.csv` | the report as tables |

### Model rules

- Only explicit `A`/`B` choices between two feasible designs in `train` mode are used.
- Features come from the server's own recomputation.
- No data, too few comparisons, a single scenario, or comparisons varying along one direction
  only are refused with `422` (`not_enough_comparisons`, `not_enough_scenarios`,
  `homogeneous_comparisons`).
- Models are JSON files in `<data dir>\models\`, never pickle. Their SHA-256 is stored in the
  database and checked on every load. A modified file is refused (`409 model_artifact_invalid`).
- `predict-pair` and `rank-candidates` refuse with `409 no_model` until a model exists. They never
  fall back to fixed weights.
- Every prediction and ranking is logged in the `prediction` table with its model version.

### Blinded test rules

Protocol details: [`docs/ml-protocol.md`](../docs/ml-protocol.md), section 10.

- Only one plan can be open. While it is open, `POST /models/train` returns
  `409 evaluation_in_progress`; predictions and rankings in a test scenario return
  `409 sealed_for_evaluation`; practice choices in a test scenario return
  `409 scenario_reserved_for_evaluation`.
- Test scenarios must be new to the frozen model (neither id nor seed in its training data).
- A choice in a test is a preference with `mode: "evaluation"`, its `evaluation_trial_id`, the
  frozen model in `model_version_locked` and the left design in `shown_left_alternative_id`. Any
  mismatch is refused with its own code. Such choices are never training-eligible.
- A train-mode choice with `model_version_locked` or `evaluation_trial_id` is refused
  (`model_lock_requires_evaluation`, `trial_requires_evaluation_mode`).
- Reveals must be done before closing: a reveal after closing would change the report.

## Figures for a report or thesis

`tools/make_figures.py` turns an export into figures (PNG and SVG), a CSV with the plotted numbers
for each figure, `captions.md` and `figures_manifest.json`. It needs matplotlib, which the
server itself does not:

```powershell
cd backend
uv pip install --python .\.venv\Scripts\python.exe -r requirements-analysis.txt   # or: .\.venv\Scripts\python.exe -m pip install -r requirements-analysis.txt
.\.venv\Scripts\python.exe -m tools.make_figures --api http://127.0.0.1:8765 --out ..\figures
.\.venv\Scripts\python.exe -m tools.make_figures --export C:\path\to\plannerdna-export.json --out ..\figures --formats png,svg,pdf
```

- `--api` accepts only a loopback URL. `--model <version>` picks a model other than the active one.
- Each closed blinded test gets figures 08–13 (suffix `_1`, `_2`, ... when there are several).
- The script refits the model from the export and checks the weights match the stored file
  (`reproducibility_check` in the manifest).
- Figures built on comparisons whose reason mentions "synthetic" carry a
  **SYNTHETIC TEST LABELS** watermark.
- A figure that cannot be drawn honestly (for example, model history with one model) is skipped,
  and the reason is listed in the manifest.

### Write rules

- **Idempotent by id.** Each record carries its own id. The first write returns `201 created`. An
  identical resend returns `200 duplicate` and changes nothing. The same id with different
  content returns `409 id_conflict`, and the original is kept.
- **Nothing is trusted.** Every city is re-hashed (FNV-1a, identical to Unity). Each
  alternative's metrics, constraint results, feasibility and features are recomputed from its
  snapshot with the published rules, and any mismatch is refused with `422`.
- **Refusals.** The server refuses:
  - a choice other than `A`, `B`, `tie` or `skip`;
  - a mode other than `train` or `evaluation`;
  - alternatives that are unknown or come from another scenario;
  - a design compared with itself, or two identical designs;
  - a pair that has already been decided;
  - a wrong `training_eligible` flag (only a train-mode A/B choice between two feasible designs is
    eligible);
  - a session whose baseline differs from the registered scenario;
  - edits to fixed parcels (roads, civic, protected).
- **Errors** are always `{"error": {"code", "message", "details"}}`. A failed write is never
  reported as success.
- **Deletion** needs the exact phrase. With `"backup": true`, a full JSON export is first written
  to `<data dir>\backups\`. Stored models (`<data dir>\models\`) are removed too.

## Tests

```powershell
cd backend
.\.venv\Scripts\python.exe -m pytest -v
```

Every test uses its own temporary data directory, so your real database is never touched.
`tests/test_unity_fixtures.py` replays records generated by the real Unity code
(`tests/fixtures/unity_metrics_fixtures.json`). This proves that the Python recomputation agrees
with C#. The fixture also contains a rank request built by Unity's `CandidateGenerator`, which
`test_models_api.py` ranks unchanged. Regenerate the file from Unity with **PlannerDNA > Export
Backend Contract Fixtures** whenever a rule changes. A Unity EditMode test fails if it is stale.

Model, evaluation and figure tests (`test_pairwise.py`, `test_models_api.py`,
`test_evaluation_api.py`, `test_figures.py`) use **synthetic test-fixture labels** from fixed
rules, never real choices.

Dependencies are pinned in `requirements.txt`: FastAPI, uvicorn, pydantic, numpy 2.2.1,
scipy 1.18.1 and scikit-learn 1.6.1.
