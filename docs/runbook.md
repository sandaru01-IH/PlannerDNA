# Runbook

All commands are Windows PowerShell, run from the repository root unless stated otherwise.
The Unity executable path below matches a default Unity Hub install of 6000.6.7f1. Adjust it if
yours differs.

```powershell
$Unity   = "C:\Program Files\Unity\Hub\Editor\6000.6.7f1\Editor\Unity.exe"
$Project = (Resolve-Path ".\PlannerDNA_Unity\PlannerDNA_Unity").Path
New-Item -ItemType Directory -Force .\TestResults | Out-Null
$Out     = (Resolve-Path ".\TestResults").Path
```

**Batch-mode commands require the project to be closed in the Unity Editor** (Unity locks the
project). In the Editor you can run the same tests from **Window > General > Test Runner**.

## Backend

```powershell
cd backend
.\.venv\Scripts\python.exe -m pytest -v                                      # tests
.\.venv\Scripts\python.exe -m uvicorn app.main:app --host 127.0.0.1 --port 8765   # run
```

## Figures and graphs from your data

Figures are made by a separate script from a research export, never from the running UI. Every
figure is written as PNG and SVG (PDF on request), with a CSV of the exact plotted numbers,
`captions.md` (ready-to-use captions) and `figures_manifest.json` (source, versions, skipped
figures and why, refit check).

```powershell
cd backend
uv pip install --python .\.venv\Scripts\python.exe -r requirements-analysis.txt   # once (adds matplotlib)
# or: .\.venv\Scripts\python.exe -m pip install -r requirements-analysis.txt

# From the running backend (loopback only):
.\.venv\Scripts\python.exe -m tools.make_figures --api http://127.0.0.1:8765 --out ..\figures
# From an export file (Settings > Export, or a backup), also with PDF:
.\.venv\Scripts\python.exe -m tools.make_figures --export C:\path\to\plannerdna-backend-export.json --out ..\figures --formats png,svg,pdf
# A model other than the active one:
.\.venv\Scripts\python.exe -m tools.make_figures --api http://127.0.0.1:8765 --out ..\figures --model pm-...
```

- `fig01`–`fig07` need a trained model. `fig08`–`fig13` need a **closed** blinded test (one set
  per test). Figures that cannot be drawn honestly are skipped and listed in the manifest.
- If any comparison's reason mentions "synthetic" (test rules), the figures carry a
  **SYNTHETIC TEST LABELS** watermark. Your own choices never do.
- The figure list and what each one shows: `docs/ml-protocol.md`, section 11.

## Build or repair the demo scene without opening the Editor

```powershell
Start-Process $Unity -ArgumentList "-batchmode -quit -projectPath `"$Project`" -executeMethod PlannerDNA.EditorTools.DemoSceneBuilder.BuildFromCommandLine -logFile `"$Out\build-scene.log`"" -Wait
Select-String "$Out\build-scene.log" -Pattern "\[PlannerDNA\]|error CS"
```

## Unity tests

```powershell
# EditMode: pure logic (generator, metrics, cost, constraints, commands, undo/redo, storage, outbox, contract, candidates)
Start-Process $Unity -ArgumentList "-batchmode -projectPath `"$Project`" -runTests -testPlatform EditMode -testResults `"$Out\editmode-results.xml`" -logFile `"$Out\editmode.log`"" -Wait

# PlayMode: loads PlannerDNA_Main and drives the real app, renderer, picker and UI
Start-Process $Unity -ArgumentList "-batchmode -projectPath `"$Project`" -runTests -testPlatform PlayMode -testResults `"$Out\playmode-results.xml`" -logFile `"$Out\playmode.log`"" -Wait

# Summary
Select-String "$Out\*-results.xml" -Pattern "<test-run " | ForEach-Object { $_.Line.Substring(0,160) }
```

- PlayMode needs the scene, so run the scene builder first on a fresh checkout.
- PlayMode tests write to a temporary folder (`PlannerApp.StoreRootOverride`), never to your real
  data folder. They also **never talk to your real backend**: unless `PLANNERDNA_TEST_BACKEND_URL`
  is set, they point at a loopback port where nothing listens. The backend tests
  (`BackendOnline_…` and the `Live_…` tests) are then **Ignored**.
- `LearningPlayModeTests.Live_TrainOnSyntheticLabels_…` records 15 comparisons, labelled by the
  **synthetic test rule** "prefer more park access", across seeds 301–303. It then:
  - trains through the Learning Lab;
  - checks that the access weight is positive;
  - checks that a later pair's prediction is revealed only after the choice and sums to 1;
  - ranks and applies a suggestion.

  It leaves a trained model in the throwaway backend, which is why the backend must be throwaway.
- `EvaluationPlayModeTests.Live_BlindedTest_…` trains on synthetic labels (seeds 401–403), then
  runs a whole blinded test through the real screen: 3 unseen areas × 2 pairs + 2 repeats, answered
  by a fixed synthetic rule. It checks that training and practice choices are refused while the test
  is open, that all 8 seals verify, and that the closed report is reproducible, has zero held-out
  leaks and lists 5 baselines. It leaves a closed test in the throwaway backend.

### Assistant playthrough on your real data (only on request)

`AgentPlaythrough.PlayRestOfTheExercise` is `[Explicit]` and is skipped unless
`PLANNERDNA_AGENT_PLAY_URL` is set. It writes to **your** local folder and backend. It records
5 practice choices in each of the cities 7, 123, 2024 and 777, trains, and answers a short blinded
test. Every choice follows the rule "more park access, then lower cost" and carries the reason
"synthetic agent-played (not the user) ...", so figures built on them are watermarked. Back up
first (copy the local folder and `backend\data\plannerdna.db`).

```powershell
$env:PLANNERDNA_AGENT_PLAY_URL = "http://127.0.0.1:8765"
Start-Process $Unity -ArgumentList "-batchmode -projectPath `"$Project`" -runTests -testPlatform PlayMode -testFilter PlannerDNA.Tests.AgentPlaythrough -testResults `"$Out\agent-play.xml`" -logFile `"$Out\agent-play.log`"" -Wait
Remove-Item Env:PLANNERDNA_AGENT_PLAY_URL
```

### Live-sync PlayMode tests against a throwaway backend

```powershell
# Terminal 1: a temporary backend on port 8766 with its own empty data folder
cd backend
$env:PLANNERDNA_DATA_DIR = Join-Path $env:TEMP ("plannerdna-testbackend-" + [guid]::NewGuid().ToString("N"))
.\.venv\Scripts\python.exe -m uvicorn app.main:app --host 127.0.0.1 --port 8766

# Terminal 2: PlayMode with live sync enabled
$env:PLANNERDNA_TEST_BACKEND_URL = "http://127.0.0.1:8766"
Start-Process $Unity -ArgumentList "-batchmode -projectPath `"$Project`" -runTests -testPlatform PlayMode -testResults `"$Out\playmode-live.xml`" -logFile `"$Out\playmode-live.log`"" -Wait
Remove-Item Env:PLANNERDNA_TEST_BACKEND_URL
```

Stop the temporary backend with `Ctrl+C` and delete its folder afterwards.

### Backend contract fixtures

After changing any generator, metric, cost, constraint or feature rule, regenerate the records that
the backend tests replay, then re-run pytest. The EditMode test `BackendFixtureFileIsUpToDate`
fails until you do.

```powershell
Start-Process $Unity -ArgumentList "-batchmode -quit -projectPath `"$Project`" -executeMethod PlannerDNA.EditorTools.ContractFixtureExporter.ExportFromCommandLine -logFile `"$Out\fixtures.log`"" -Wait
```

### Visual snapshots (opt-in)

Renders PNGs to `TestResults\captures\` for human review. Nothing about appearance is asserted.

- **Always:** `01`–`07`: home, city, edit with overlay, compare, Settings, delete
  confirmation, and the Learning Lab (light theme).
- **Always:** `12`–`20`: guided-tour steps (welcome, goal card, the highlighted parcel, the park
  tool, metrics in dark mode), then the dark theme for the city, compare, help and home screens.
- **Only with a throwaway backend** (`PLANNERDNA_TEST_BACKEND_URL` set, as above): `08`–`11`:
  - the trained Learning Lab;
  - diagnostics;
  - suggestions;
  - the compare screen with the after-choice prediction;
  - `21`–`24`: the blinded test (start, a pair, the report, opened predictions in dark mode).

  These use synthetic test labels.

```powershell
Start-Process $Unity -ArgumentList "-batchmode -projectPath `"$Project`" -runTests -testPlatform PlayMode -testFilter VisualSnapshotTests -testResults `"$Out\snapshots.xml`" -logFile `"$Out\snapshots.log`"" -Wait
```

## Troubleshooting

| Symptom | Fix |
|---|---|
| Header says *Backend: Offline, saving locally \| N to sync* | Expected if uvicorn is not running. Everything still works and saves locally. The N queued records are sent automatically once the backend starts. |
| Header says *Backend: Online (no database)* | An old Phase 0 backend is running. Restart it from the current code. |
| Header shows *N refused* | The backend refused N records. **Settings** lists the first ones with the backend's reason, and the files are in `outbox\rejected\`. Refused records stay saved locally. |
| Queue never drains while online | Open **Settings** for the last problem. Press **Sync now** to retry immediately instead of waiting for the backoff. |
| The guided tour's ring is in the wrong place | It follows the target every frame; if a window was resized mid-step, press **Back** then **Next**. **Esc** ends the tour. |
| The interface is dark (or light) and you want the other theme | Click **Light mode** / **Dark mode** in the header. The choice is stored in Unity PlayerPrefs (`PlannerDNA.Theme`). |
| uvicorn: `address already in use` | Another server is on 8765. Stop it, or use another port and update **Base Url** on `HealthMonitor`. |
| Menu **PlannerDNA** missing | Scripts did not compile. Check the Console for red errors, then **Assets > Refresh**. |
| Play shows a blank or grey screen | Run **PlannerDNA > Create or Repair Demo Scene** and make sure `PlannerDNA_Main` is the open scene. |
| UI looks squashed or cut off | Set the Game view to 16:9 (1920×1080 recommended). Press **Home** to re-frame the camera after resizing. |
| Batch run exits immediately with code 1 | The project is open in the Editor, or a crashed Unity left a lock. Close the Editor, end stray `Unity.exe` processes for this project, and retry. |
| Batch run: "Tundra build failed", "Permission denied" on `Library/Bee` | A transient race after package changes. Re-run the same command. |
| "Resume last saved design" disabled | No save yet. Press Ctrl+S during a challenge first. |
| A saved alternative or design refused on load | Its baseline or version differs from the current challenge. This is deliberate. |
| **Train my model** is greyed out | Its reason is shown next to it. Typical reasons: the backend is offline; local records are still syncing; fewer than 10 informative comparisons; only one scenario; or every comparison varied along one trade-off. Compare designs that trade off different outcomes, in more scenarios. |
| Learning Lab says "Exploratory" | Fewer than 30 comparisons over 5 scenarios (a heuristic). The model still works, but its weights are unstable. |
| `model_artifact_invalid` | A file in `backend\data\models\` was changed or removed, and its checksum no longer matches. Pressing Train on the same data returns that same broken model. Record at least one more comparison and train, which writes a new file, or delete all data. |
| Suggestions button disabled | You need a challenge open and a trained model. Without a model, suggestions are deliberately not shown. |
| **Train my model** says a blinded test is running | Training is paused until the test is finished, so test answers cannot leak into the model. Open **Learning Lab > Test your model fairly** and press **Finish** (or finish early). |
| A practice choice is refused: "reserved for the open blinded test" | That area belongs to the running test. Finish the test, or practise in another area. |
| Blinded test **Start** is disabled | It needs the backend online and a trained model. The reason is shown at the top of the test screen. |
| Blinded test: "WARNING: N of M opened prediction(s) did not match the seal" | A stored prediction changed after it was sealed. The report is still stored, but do not report that test; keep the export for inspection. |
| `make_figures` says matplotlib is missing | Install `requirements-analysis.txt` (see "Figures and graphs from your data"). |
| `make_figures --api` refused | Only `http://127.0.0.1:<port>` or `http://localhost:<port>` is accepted. |
| pip error on install | The pinned scientific packages need 64-bit Python 3.12. Use `py -3.12`. |
| Start over with no data | Use **Settings > Delete all data…** (type `DELETE`). The option to also delete the backend database is available while it runs. |
