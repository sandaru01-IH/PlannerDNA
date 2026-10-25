# PlannerDNA demo video: narration script

For `video/PlannerDNA_demo.mp4` (1:56.9, 1920 × 1080, no audio track).

The timings below are the exact section boundaries of the video. Each block is written for a calm
pace of about 2.3 to 2.5 words per second, so it fits its window with a short breath at the end.
If you run long, cut the sentences marked *(optional)* first.

---

## Script with timings

### 1. Opening (0:00 – 0:09)

*Screen: title card, then the Home screen.*

> This is PlannerDNA, a small 3D sandbox I built for urban-planning students.
> You design a neighbourhood, compare two plans, and choose.

### 2. The challenge (0:09 – 0:22)

*Screen: guided tour, the goal card, then the park-access overlay.*

> A guided tour explains the screen. The challenge: more homes within a four-hundred-metre walk of
> a park, within budget and the building rules. Teal homes reach a park. Rose homes don't.

### 3. Editing the city (0:22 – 0:35)

*Screen: a parcel is selected and turned into a park, the metrics update, then the camera orbits.*

> Pick a vacant parcel, make it a park, and every number updates instantly: access, housing, cost
> and the rules, with full undo. The city is generated from a seed, so it is always reproducible.

### 4. Two designs, one choice (0:35 – 0:51)

*Screen: Design A saved, Design B saved, the comparison table, then the prediction after the choice.*

> Save that as Design A. Start again from the same baseline, add a floor of homes instead, and
> save Design B. Compare them side by side, then choose. Only after you choose does the model
> reveal what it predicted.

### 5. The Learning Lab (0:51 – 1:05)

*Screen: Learning Lab, weights and diagnostics, then ranked suggestions.*

> In the Learning Lab, a pairwise logistic regression learns from your explicit choices, and only
> when you press Train. It shows every weight with its uncertainty, checks itself against simple
> rules, and ranks suggested designs.

### 6. The fair test (1:05 – 1:18)

*Screen: blinded-test start screen, then the first sealed pair.*

> Then the fair test. The model is frozen and tried on brand-new areas. Each prediction is sealed
> with a SHA-256 fingerprint before you choose, and left and right are randomised.

### 7. Fast-forward (1:18 – 1:27)

*Screen: the test choices fast-forward.*

> Fifteen choices later. Some pairs come back with the sides swapped, to measure how consistent
> you are.

### 8. The reveal and the report (1:27 – 1:38)

*Screen: every seal opened and checked, then the report.*

> At the end, every seal is opened and verified. The report scores the model against chance and
> simple rules of thumb, and it is fully reproducible.

### 9. Runs locally (1:38 – 1:46)

*Screen: dark-mode orbit, then the figures caption.*

> Light and dark themes. Everything runs on one computer: Unity, a small FastAPI server and
> SQLite. *(optional)* Figures come straight from the exported data.

### 10. Close (1:46 – 1:57)

*Screen: closing card, "Runs entirely on one computer".*

> In my own blinded test, the model got eight of nine right, exactly as many as the rule
> "pick more park access". Reporting that honestly is the point.

---

## Full read-through (no timings)

This is PlannerDNA, a small 3D sandbox I built for urban-planning students. You design a
neighbourhood, compare two plans, and choose.

A guided tour explains the screen. The challenge: more homes within a four-hundred-metre walk of a
park, within budget and the building rules. Teal homes reach a park. Rose homes don't.

Pick a vacant parcel, make it a park, and every number updates instantly: access, housing, cost and
the rules, with full undo. The city is generated from a seed, so it is always reproducible.

Save that as Design A. Start again from the same baseline, add a floor of homes instead, and save
Design B. Compare them side by side, then choose. Only after you choose does the model reveal what
it predicted.

In the Learning Lab, a pairwise logistic regression learns from your explicit choices, and only
when you press Train. It shows every weight with its uncertainty, checks itself against simple
rules, and ranks suggested designs.

Then the fair test. The model is frozen and tried on brand-new areas. Each prediction is sealed
with a SHA-256 fingerprint before you choose, and left and right are randomised.

Fifteen choices later. Some pairs come back with the sides swapped, to measure how consistent you
are.

At the end, every seal is opened and verified. The report scores the model against chance and
simple rules of thumb, and it is fully reproducible.

Light and dark themes. Everything runs on one computer: Unity, a small FastAPI server and SQLite.
Figures come straight from the exported data.

In my own blinded test, the model got eight of nine right, exactly as many as the rule "pick more
park access". Reporting that honestly is the point.

---

## Honesty notes for the post

- **Scripted choices.** The choices in the video were scripted on a copy of the data, and the
  opening card says so. The report shown on screen (9 of 12) is from that scripted run. The
  "eight of nine" in the closing line is your real blinded test.
- **Model data.** Of the 30 training comparisons behind the model, 10 are yours and 20 were made
  by a fixed rule ("more park access, then lower cost"). If you mention the model's accuracy in the
  post text, say this too.
- **Synthetic setting.** Cities, housing units, credits and access are synthetic proxies: a learning
  tool, not planning advice.

## Adding the voice-over to the video

Record the narration as one file (WAV or M4A), starting at 0:00 with the title card. Then merge it
without re-encoding the picture. The ffmpeg binary is already in the media environment:

```powershell
cd PlannerDNA   # the repository root
$ff = .\.media-venv\Scripts\python.exe -c "import imageio_ffmpeg; print(imageio_ffmpeg.get_ffmpeg_exe())"
& $ff -i deliverables\video\PlannerDNA_demo.mp4 -i narration.wav `
      -map 0:v -map 1:a -c:v copy -c:a aac -b:a 192k -movflags +faststart `
      deliverables\video\PlannerDNA_demo_narrated.mp4
```

Keep the recording at or under 1:57 so it ends with the closing card. Any video editor (CapCut, Clipchamp, DaVinci Resolve) works just as well: place the audio on
the timeline at 0:00 and use the block timings above as markers.
