# Start here

This guide gets the PlannerDNA sandbox running on Windows, step by step. You need no
programming knowledge to play it.

## 1. What you need

| Tool | Version | Needed for |
|---|---|---|
| Unity Hub + Unity Editor | **6000.6.7f1** exactly | the 3D sandbox (required) |
| Python | 3.12 (64-bit) | the optional local backend and Learning Lab only |

Other Unity 6 versions may work, but this is the version the project was built and tested with.
Opening it in a different version makes Unity offer to upgrade the project. Decline unless you
have a backup.

## 2. Open the project in Unity

1. Open **Unity Hub** → **Projects** → **Add** → **Add project from disk**.
2. Choose the **inner** folder:
   `...\PlannerDNA\PlannerDNA_Unity\PlannerDNA_Unity`
   (the one that contains `Assets`, `Packages` and `ProjectSettings`).
3. Open it. The first import takes a few minutes. The Console should show no red errors.

## 3. Create the demo scene (one time)

1. In the Unity menu bar, click **PlannerDNA > Create or Repair Demo Scene**.
2. A dialog lists what was created. The scene `Assets/PlannerDNA/Scenes/PlannerDNA_Main.unity`
   opens and is added to Build Settings.

Running the menu again is safe. It only adds missing objects and references and keeps your own
changes. **PlannerDNA > Open Demo Scene** reopens the scene later.

## 4. (Optional) Start the local backend

The sandbox works completely offline and always saves on this computer first. While the backend
runs, your sessions, alternatives and choices are also copied, in order, into a local SQLite
database (`backend\data\plannerdna.db`). Anything made while it was off is sent when it starts.

```powershell
cd <path-to>\PlannerDNA\backend
# first time only:
py -3.12 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
# every time:
.\.venv\Scripts\python.exe -m uvicorn app.main:app --host 127.0.0.1 --port 8765
```

Leave that window open. Check <http://127.0.0.1:8765/api/v1/health> in a browser.

## 5. Play

1. Press **Play** (▶) at the top of the Unity Editor.
2. Maximise the Game view (or set it to 1920×1080 / 16:9) for the intended layout.
3. The **Home** screen appears. It explains the challenge in one sentence and shows the four steps
   of the exercise: **Explore → Design A & B → Compare and choose → Learn**.
4. First time? Click **Start with a guided tour** (see 5b). Otherwise click **Start without the tour**.

### Screen layout

- **Header.** The brand, the challenge name, and the *journey line*. The journey line is a small
  transit-style line whose current stop shows where you are in the exercise. *Learn n/10* counts
  usable choices toward the 10 needed to train. Clicking **Compare and choose** (once A and B are
  saved) or **Learn** opens that screen. On the right are **Home**, **Save**, **Learning Lab**,
  **Help and tour**, **Settings**, the **Dark mode / Light mode** switch and the backend status pill.
- **Left panel.** *Change the selected parcel* tools (each with the colour it gives the map) and
  *History* (Undo, Redo, Start over, Zoom to selected).
- **City view.** **Show park access** sits in the top-left chip. The colour key, compass and scale
  are in the bottom-left corner.
- **Right panel.** *Your goal* card with the rules as green or red dots, *Selected parcel*,
  *How the neighbourhood is doing*, and *Learning from your choices*. Click any metric card for
  a plain-language explanation.
- **Bottom.** *Your edits so far* (timeline), the **Design A** and **Design B** cards and the big
  **Compare A and B** button.

### 5b. Guided tour

The tour has 13 short steps. Each step dims the screen except one area, rings it and explains it.
On four steps you do the action yourself, and the tour continues once it has really happened:

1. switch on **Show park access**;
2. click the highlighted vacant parcel, which the ring follows when you move the camera;
3. click **Make it a park**;
4. click **Save as A**.

**Skip this step** moves on without doing the action. **Back**, **End tour** and **Esc** work at any time.
Restart the tour from **Help and tour → Start the tour** or from Home. The tour stores nothing,
and the edit it makes is a normal, undoable edit.

### Dark mode

**Dark mode** in the header switches the whole interface and the city background to a dark
theme. The choice is remembered on this computer (Unity PlayerPrefs) for the next launch.

### Controls

| Action | Mouse | Keyboard |
|---|---|---|
| Select parcel | Left-click (selecting never edits) | – |
| Orbit | Right-drag | Q / E rotate 45° |
| Pan | Middle-drag | W A S D / arrows |
| Zoom | Scroll | – |
| Reset camera | – | Home |
| Zoom to selected | History button | F |
| Make it a park / Build homes / Build shops | tool buttons | P / R / C |
| Clear the land (vacant) | tool button | X or Delete |
| Add a floor / Remove a floor | tool buttons | = or ] / − or [ |
| Undo / Redo | History buttons | Ctrl+Z / Ctrl+Y (or Ctrl+Shift+Z) |
| Save design | header | Ctrl+S |
| End tour / close dialog / clear selection | – | Esc |

## 6. Demo walkthrough (about 5 minutes)

All values below are for **demo seed 42** and were observed in automated Play-mode runs.

1. **Start.** On Home, click **Start without the tour** (or **Start with a guided tour**). A 20×20
   city appears. The dashboard shows *Homes near a park 25.6%*, *Homes (housing units) 2080*,
   *Budget used 0 / 100*.
2. **Read the goal** (right panel, green card). Get more homes within a 400 m walk of a park
   while keeping at least 1872 housing units (90%), spending at most 100 credits and staying at or
   below 8 floors. Each rule shows a green dot while it is kept.
3. **See who lacks access.** Tick **Show park access** (top-left of the city). The ground under
   each home turns teal (park within 400 m by road) or rose (too far), and a small key appears
   next to the switch.
4. **Select a parcel.** Hover to see parcel ids, then left-click a sand-coloured **vacant**
   parcel near rose housing. For seed 42, parcel **r01_c08** (near the north corner) is a good
   one. The guided tour suggests a different, also valid one (**r04_c17**). The *Selected parcel*
   panel shows its details.
5. **Add a park.** Click **Make it a park** (or press P). Expected for r01_c08: access **42.5%
   (+16.9 pts)**, budget **20 / 100**. An orange frame marks the changed parcel and the timeline
   shows `#1 Add park on r01_c08`.
6. **Try a refused edit.** With the park still selected, click **Add a floor**. A red message
   explains why (only buildings have floors). Refused attempts are counted in the timeline
   summary, not added as edits.
7. **Undo / Redo.** Press Ctrl+Z: the city and every metric return exactly to baseline (cost 0).
   Press Ctrl+Y to redo.
8. **Save Design A.** Click **Save as A** on the *Design A* card (bottom right). The card turns
   green-edged and shows the design's access, homes and credits.
9. **Start over.** Click **Start over** under *History*. This is itself undoable.
10. **Build Design B.** Select a residential building under 8 floors and click **Add a floor**.
    Housing rises by exactly 4 units (one floor × 4 units per floor); cost rises by 5 credits.
    Click **Save as B**. The journey line moves to *Compare and choose*.
11. **Compare.** Click **Compare A and B**. Both designs are checked against the same baseline,
    and a table shows Baseline / A / B / A minus B with a plain-language trade-off sentence.
    **Show A** / **Show B** preview either design in 3D (editing is locked while previewing).
12. **Choose.** Pick **A**, **B**, **Tie** or **Skip**, optionally type a reason, then click
    **Confirm choice**. The decision is written to disk once, and the same pair cannot be recorded
    twice. Only a feasible A-vs-B choice is marked *training-eligible*. Tie and Skip are stored but
    not eligible.
13. **Check what was stored.** Click **Settings** in the header to see the storage folder,
    counts and versions. **Open local data folder** shows the JSON files, and **Load my saved
    design** reloads your last Ctrl+S save.
14. **Watch the sync.** The header pill shows *Backend: Online*, or *Backend: Offline, saving
    locally | N to sync* while records are waiting. With the backend stopped, N grows with every
    edit, save and choice. Start the backend, and within a few seconds the count disappears.
    **Settings** then shows *Queue: 0 waiting | … synced (online)* and server statistics such
    as *Training samples: 1 unique direct comparisons across 1 scenarios*.
15. **Export.** In **Settings**:
    - **Export local data (JSON)** works offline.
    - **Export backend data (JSON + CSV)** works while the backend runs.

    Both files appear in the `exports` folder.
16. **Save and resume.** Press Ctrl+S, stop Play mode, press Play again, and click **Resume my
    saved design** on Home. Nothing already synced is sent twice.
17. **Learning from your choices** (right panel) says *No preference model has been trained yet,
    so nothing has been learned* until you train one yourself (next section).

## 6b. Learning Lab: your personal preference model (optional, needs the backend)

The Learning Lab fits a small statistical model to **your own** A-vs-B choices. It estimates how
strongly each measured outcome went with the designs you picked. It does not understand
cities, and with few choices its results are unstable. Read
[research-limitations.md](research-limitations.md) first.

1. **Collect choices.** Keep the backend running, then repeat steps 8–12 to record comparisons
   that each trade outcomes off differently. Examples:
   - more parks against more cost;
   - fewer homes against more access.

   Use **at least two challenges**: on Home, click **More options**, type another number into
   **City seed** and click **Start this city**. Only feasible **A** or **B** choices count; Tie and
   Skip don't.
2. **Open the Learning Lab.** Click **Learning Lab** in the header or on Home, the **Learn** stop
   on the journey line, or **Open the Learning Lab** in the right panel.
   - The first line reads *Training samples: N unique direct comparisons across S scenarios*.
   - Below it, the screen says what is still missing. Training needs at least 10 informative
     comparisons in 2 scenarios that don't all vary along a single trade-off.
   - Until you train, **no weights, predictions or suggestions are shown**.
3. **Train.** Click **Train my model**. The button is enabled only when the data allow it, and the
   reason is shown otherwise. The screen then shows:
   - the model version;
   - one weight per outcome, with a 90% interval and how often its sign held when scenarios were
     resampled;
   - a trade-off line;
   - diagnostic accuracy on held-out scenarios, compared with simple rules such as "always prefer
     more access";
   - the model history.

   Below 30 comparisons over 5 scenarios it is labelled *exploratory*. Pressing Train again with no
   new choices returns the same model.
4. **Prediction after your choice.** Make another A/B comparison and click **Confirm choice**.
   Only then does the compare screen show what the saved model would have predicted (P(A) +
   P(B) = 100%), whether it agreed with you, and the two outcomes that drove it most.
5. **Suggestions.** With a challenge open, scroll to *Suggestions for the current scenario* in the
   Learning Lab and click **Generate and rank candidates**.
   - Unity generates up to 30 random but valid designs from this challenge's baseline. Infeasible
     ones and duplicates are discarded.
   - The model ranks them, and you see the top five with their metrics.
   - They are labelled *synthetic scenario* and are **not optimal plans**, only the designs your
     current model scores highest among those tried.
   - **Use as my design (undoable)** loads one into the city. Ctrl+Z reverts it.

## 6c. Blinded test: does the model really predict you? (optional, needs a trained model)

The Learning Lab's accuracy is checked on your old choices, which you made before the model existed
and some of which it was built from. The blinded test is the fair check: new areas, a frozen
model, and predictions you cannot see until the end.

1. In the Learning Lab, scroll to **Test your model fairly** and click **Open the blinded test**.
2. Click **Start a short test (15 choices)**: 3 new areas × 4 pairs, plus 3 pairs shown again
   later. **Start a full test (25 choices)** uses 5 areas × 4 pairs + 5 repeats.
   - Your current model is frozen; **Train my model** is paused until you finish.
   - The new areas were never trained on or practised. Practice choices in them are refused
     during the test.
3. For each pair:
   - the screen shows *Today*, *Left* and *Right* with their numbers, and a **sealed prediction**
     fingerprint (a hash). The prediction already exists but is hidden;
   - **Show left** / **Show right** previews a design in 3D;
   - choose **Left**, **Right**, **Tie** or **Skip** and click **Confirm choice**, then
     **Show the next pair**.

   Which design is on the left was drawn by the backend at random. Some pairs come back later with
   the sides swapped, to check how consistent your own choices are.
4. Click **Finish the test** after the last pair (or earlier: click twice to stop early). The app
   opens every sealed prediction, checks each against its fingerprint, and then shows the report:
   - how many of your choices the frozen model predicted, with a 95% interval;
   - the same score for simple rules (coin flip, always cheaper, always more park access, equal
     weights, always the left design);
   - how often you repeated your own choice, and whether you favoured the left side.
5. Test choices are stored as `evaluation` choices and are **never** used for training, even later.

With only 12–20 scored choices the interval is wide. A model that does not beat "always more park
access" has not shown that it captures more than that rule.

## 6d. Graphs and figures for a report

All figures come from your stored data, through a separate script (`backend\tools\make_figures.py`).
With the backend running:

```powershell
cd <path-to>\PlannerDNA\backend
# first time only (if the venv has no pip, e.g. it was made with uv:
#   uv pip install --python .\.venv\Scripts\python.exe -r requirements-analysis.txt):
.\.venv\Scripts\python.exe -m pip install -r requirements-analysis.txt
# every time:
.\.venv\Scripts\python.exe -m tools.make_figures --api http://127.0.0.1:8765 --out ..\figures
```

The `figures` folder then holds up to 13 figures (PNG and SVG), a CSV of the numbers behind each,
and `captions.md` with a caption for each figure. Figures 1–7 describe your training data and
model; 8–13 appear after a finished blinded test. What each shows:
[ml-protocol.md](ml-protocol.md#11-figures).

## 7. Where your data goes

- **Unity**: `%USERPROFILE%\AppData\LocalLow\DefaultCompany\PlannerDNA_Unity\PlannerDNA\`
  (Unity's `Application.persistentDataPath`), as local JSON files plus the outbox queue.
- **Backend** (only if you run it): `backend\data\plannerdna.db`.

Both stay on this computer; the only network traffic is to `127.0.0.1`. See
[data-dictionary.md](data-dictionary.md).

**Deleting everything:** open **Settings > Delete all data…** and type `DELETE`.

- A backup is made first unless you untick it.
- While the backend runs, you can also empty its database.

## 8. If something goes wrong

See [runbook.md](runbook.md#troubleshooting).
