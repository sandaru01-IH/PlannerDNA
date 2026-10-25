using System;
using System.Collections.Generic;
using PlannerDNA.App;
using PlannerDNA.Commands;
using PlannerDNA.Core;
using PlannerDNA.Metrics;
using PlannerDNA.Rendering;
using UnityEngine;
using UnityEngine.UIElements;

namespace PlannerDNA.UI
{
    /// <summary>
    /// Guided tour. Dims everything except one part of the screen, rings it and explains it in a
    /// callout. Some steps wait for the learner to do the thing (turn on the access overlay, pick a
    /// parcel, add a park, save Design A) and continue on their own once it has really happened.
    /// The spotlight hole stays clickable, so the real controls are used, not a simulation.
    /// </summary>
    public sealed class TutorialView
    {
        private const float Pad = 8f;
        private const float Margin = 14f;
        private const float AutoAdvanceSeconds = 1.1f;

        private sealed class Step
        {
            public string Key, Title, Body, Task;
            public Func<Rect?> Target;
            public Func<bool> Done;
            public Action Enter;
        }

        private readonly SandboxUi _ui;
        private readonly PlannerApp _app;
        private readonly VisualElement _layer, _ring, _card, _progress, _task;
        private readonly VisualElement[] _dims;
        private readonly Label _count, _title, _body, _taskText;
        private readonly Button _back, _next;
        private readonly List<Step> _steps = new List<Step>();
        private int _index = -1;
        private bool _taskDone;
        private float _advanceAt = -1f;
        private int _suggestedRow = -1, _suggestedCol = -1;

        public bool IsActive => _index >= 0;
        public int StepIndex => _index;
        public int StepCount => _steps.Count;
        public string CurrentStepKey => IsActive ? _steps[_index].Key : null;
        public bool IsWaitingForAction => IsActive && _steps[_index].Done != null && !_taskDone;
        public string SuggestedParcelId => _suggestedRow < 0 ? null : CellIds.Format(_suggestedRow, _suggestedCol);
        /// <summary>The spotlight hole in tour-layer coordinates, or null for a centred step.</summary>
        public Rect? CurrentHole { get; private set; }

        public TutorialView(SandboxUi ui, PlannerApp app)
        {
            _ui = ui;
            _app = app;
            _layer = ui.Q<VisualElement>("tour-layer");
            _dims = new[] { ui.Q<VisualElement>("tour-dim-top"), ui.Q<VisualElement>("tour-dim-bottom"), ui.Q<VisualElement>("tour-dim-left"), ui.Q<VisualElement>("tour-dim-right") };
            _ring = ui.Q<VisualElement>("tour-ring");
            _card = ui.Q<VisualElement>("tour-card");
            _progress = ui.Q<VisualElement>("tour-progress");
            _count = ui.Q<Label>("tour-count");
            _title = ui.Q<Label>("tour-title");
            _body = ui.Q<Label>("tour-body");
            _task = ui.Q<VisualElement>("tour-task");
            _taskText = ui.Q<Label>("tour-task-text");
            _back = ui.Q<Button>("btn-tour-back");
            _next = ui.Q<Button>("btn-tour-next");
            ui.Q<Button>("btn-tour-end").clicked += End;
            _back.clicked += Back;
            _next.clicked += Next;
            BuildSteps();
        }

        private Rect? Element(string name)
        {
            VisualElement e = _ui.Root.Q<VisualElement>(name);
            return e == null || e.resolvedStyle.display == DisplayStyle.None ? (Rect?)null : VisibleBound(e);
        }

        /// <summary>The part of an element that is actually on screen (clipped by enclosing scroll views).</summary>
        private static Rect VisibleBound(VisualElement e)
        {
            Rect r = e.worldBound;
            for (VisualElement p = e.parent; p != null; p = p.parent)
            {
                if (!(p is ScrollView scroll)) continue;
                Rect v = scroll.contentViewport.worldBound;
                r = Rect.MinMaxRect(Mathf.Max(r.xMin, v.xMin), Mathf.Max(r.yMin, v.yMin), Mathf.Min(r.xMax, v.xMax), Mathf.Min(r.yMax, v.yMax));
            }
            return r;
        }

        private void BuildSteps()
        {
            _steps.Clear();
            _steps.Add(new Step
            {
                Key = "welcome",
                Title = "Welcome to PlannerDNA",
                Body = "This is a practice space for one planning question: where should a neighbourhood add parks when land, " +
                       "money and homes are limited? In about two minutes you will learn the screen by making one real change. " +
                       "Leave any time with End tour or Esc.",
            });
            _steps.Add(new Step
            {
                Key = "goal",
                Title = "Your goal and the rules",
                Body = "Planners rarely get everything they want. Your goal is to give more homes a park within a short walk. " +
                       "The dots underneath are the rules your plan has to keep: a budget, enough homes and a height limit. " +
                       "Green means the rule is kept, red means it is broken.",
                Target = () => Element("goal-card"),
                Enter = () => _ui.Q<ScrollView>("analysis").scrollOffset = Vector2.zero,
            });
            _steps.Add(new Step
            {
                Key = "map",
                Title = "This is your imaginary neighbourhood",
                Body = "Each square is a 40 m parcel of land. Its colour shows what is on it; the key is in the bottom-left corner. " +
                       "Right-drag to rotate, use the mouse wheel to zoom, and middle-drag or W A S D to move around.",
                Target = () => Element("viewport"),
            });
            _steps.Add(new Step
            {
                Key = "access",
                Title = "Who lives too far from a park?",
                Body = "This map layer colours every home. Teal homes can walk to a park within 400 m along the roads. " +
                       "Rose homes are too far away, and they are the people your plan should help.",
                Task = "Switch on \"Show park access\"",
                Target = () => Element("map-tools"),
                Done = () => _app.AccessOverlay,
            });
            _steps.Add(new Step
            {
                Key = "select",
                Title = "Pick a place for a new park",
                Body = "Parks can only go on land that can change. Sand-coloured parcels are vacant. The highlighted one sits close " +
                       "to homes that are too far from a park, so it is a good place to try.",
                Task = "Click the highlighted parcel",
                Target = ParcelHole,
                Done = SelectedParcelIsVacant,
                Enter = SuggestParcel,
            });
            _steps.Add(new Step
            {
                Key = "park",
                Title = "Change what is on the parcel",
                Body = "These tools change the selected parcel. A tool that cannot be used here looks faded; clicking it tells you why. " +
                       "Each change costs credits from your budget.",
                Task = "Click \"Make it a park\" (or press P)",
                Target = () => VisibleBound(_ui.Toolbar.ButtonFor(EditAction.SetPark)),
                Done = SelectedParcelIsNewPark,
            });
            _steps.Add(new Step
            {
                Key = "metrics",
                Title = "See the effect straight away",
                Body = "The numbers update after every change. More homes are now near a park, but some budget is spent. " +
                       "That is a trade-off, and noticing trade-offs is what this exercise is about. Click any card for a plain explanation.",
                Target = () => Element("metric-cards"),
            });
            _steps.Add(new Step
            {
                Key = "history",
                Title = "Experiment freely",
                Body = "Nothing is permanent. Undo, Redo and Start over are always here, and every change is listed along the bottom of the screen.",
                Target = () => Element("history-buttons"),
            });
            _steps.Add(new Step
            {
                Key = "save-a",
                Title = "Keep this plan as Design A",
                Body = "Saving takes a snapshot of the whole city with its numbers. You can keep editing afterwards; Design A stays as it was.",
                Task = "Click \"Save as A\"",
                Target = () => Element("slot-a"),
                Done = () => _app.Session != null && _app.Session.AlternativeA != null,
            });
            _steps.Add(new Step
            {
                Key = "compare",
                Title = "Make a second plan, then compare",
                Body = "Next, change the city in a different way (for example, keep more homes or spend less) and save it as Design B. " +
                       "Then press Compare to see both plans side by side and choose the one you prefer.",
                Target = () => Element("ab-section"),
            });
            _steps.Add(new Step
            {
                Key = "journey",
                Title = "Your route through the exercise",
                Body = "This line shows where you are: Explore, Design A & B, Compare, then Learn. The count shows how many usable " +
                       "choices you have made. Training needs about 10, from at least two different cities.",
                Target = () => Element("journey"),
            });
            _steps.Add(new Step
            {
                Key = "learning",
                Title = "The Learning Lab",
                Body = "Nothing is learned in the background. A model is trained only when you press \"Train my model\" there. " +
                       "It then shows which outcomes your choices tended to favour, and how uncertain that summary is.",
                Target = () => Element("btn-learning"),
            });
            _steps.Add(new Step
            {
                Key = "help",
                Title = "Help is always one click away",
                Body = "\"Help and tour\" has every shortcut, a glossary of planning terms, and this tour. \"Dark mode\" switches to a darker screen. " +
                       "Now try making your Design B.",
                Target = () => Element("header-actions"),
            });
        }

        // ---- Lifecycle ----

        /// <summary>Starts the tour on the current challenge. Needs a loaded challenge.</summary>
        public bool Begin()
        {
            if (_app.Session == null) return false;
            _ui.CloseAllOverlays();
            if (_app.PreviewSlot.HasValue) _app.EndPreview();
            _progress.Clear();
            for (int i = 0; i < _steps.Count; i++) _progress.Add(UiFormat.Box("tour-pip"));
            _layer.RemoveFromClassList("hidden");
            Show(0);
            return true;
        }

        public void End()
        {
            if (!IsActive) return;
            _index = -1;
            _advanceAt = -1f;
            CurrentHole = null;
            _layer.AddToClassList("hidden");
        }

        public void Next()
        {
            if (!IsActive) return;
            if (_index >= _steps.Count - 1) { End(); return; }
            Show(_index + 1);
        }

        public void Back()
        {
            if (IsActive && _index > 0) Show(_index - 1);
        }

        private void Show(int index)
        {
            _index = index;
            _advanceAt = -1f;
            Step step = _steps[index];
            step.Enter?.Invoke();
            _taskDone = step.Done != null && step.Done();

            _count.text = $"Step {index + 1} of {_steps.Count}";
            _title.text = step.Title;
            _body.text = step.Body;
            _task.EnableInClassList("hidden", step.Task == null);
            _back.SetEnabled(index > 0);
            int i = 0;
            foreach (VisualElement pip in _progress.Children()) pip.EnableInClassList("tour-pip--done", i++ <= index);
            RefreshTaskState();
            Layout();
        }

        private void RefreshTaskState()
        {
            Step step = _steps[_index];
            bool last = _index == _steps.Count - 1;
            if (step.Task != null) _taskText.text = _taskDone ? "Done" : step.Task;
            _task.EnableInClassList("tour-task--done", _taskDone);
            _ring.EnableInClassList("tour-ring--waiting", step.Done != null && !_taskDone);
            _next.text = last ? "Finish" : step.Done != null && !_taskDone ? "Skip this step" : "Next";
        }

        /// <summary>Called every frame by SandboxUi: follows moving targets and detects completed tasks.</summary>
        public void Tick()
        {
            if (!IsActive) return;
            Step step = _steps[_index];
            if (step.Done != null && !_taskDone && step.Done())
            {
                _taskDone = true;
                RefreshTaskState();
                _advanceAt = Time.unscaledTime + AutoAdvanceSeconds;
            }
            if (_advanceAt > 0 && Time.unscaledTime >= _advanceAt)
            {
                Next();
                return;
            }
            Layout();
        }

        // ---- Geometry ----

        private void Layout()
        {
            float w = _layer.layout.width, h = _layer.layout.height;
            if (float.IsNaN(w) || w <= 1 || float.IsNaN(h) || h <= 1) return;

            Rect? world = _steps[_index].Target?.Invoke();
            Rect? hole = null;
            if (world.HasValue && world.Value.width > 0 && world.Value.height > 0)
            {
                Rect local = _layer.WorldToLocal(world.Value);
                float xMin = Mathf.Clamp(local.xMin - Pad, 0, w), yMin = Mathf.Clamp(local.yMin - Pad, 0, h);
                float xMax = Mathf.Clamp(local.xMax + Pad, 0, w), yMax = Mathf.Clamp(local.yMax + Pad, 0, h);
                if (xMax - xMin > 2 && yMax - yMin > 2) hole = Rect.MinMaxRect(xMin, yMin, xMax, yMax);
            }
            CurrentHole = hole;

            Rect r = hole ?? new Rect(w / 2, h / 2, 0, 0);
            Place(_dims[0], 0, 0, w, r.yMin);
            Place(_dims[1], 0, r.yMax, w, h - r.yMax);
            Place(_dims[2], 0, r.yMin, r.xMin, r.height);
            Place(_dims[3], r.xMax, r.yMin, w - r.xMax, r.height);
            _ring.style.opacity = hole.HasValue ? 1f : 0f;
            if (hole.HasValue) Place(_ring, r.xMin - 3, r.yMin - 3, r.width + 6, r.height + 6);

            float cw = _card.layout.width, ch = _card.layout.height;
            if (float.IsNaN(cw) || cw <= 1) cw = 380;
            if (float.IsNaN(ch) || ch <= 1) ch = 240;
            Vector2 pos = CardPosition(hole, w, h, cw, ch);
            _card.style.left = pos.x;
            _card.style.top = pos.y;
        }

        private static Vector2 CardPosition(Rect? hole, float w, float h, float cw, float ch)
        {
            if (!hole.HasValue) return new Vector2((w - cw) / 2, (h - ch) / 2);
            Rect r = hole.Value;
            float gap = Margin + 6;
            float midY = Mathf.Clamp(r.center.y - ch / 2, Margin, h - ch - Margin);
            float midX = Mathf.Clamp(r.center.x - cw / 2, Margin, w - cw - Margin);
            if (r.xMax + gap + cw <= w - Margin) return new Vector2(r.xMax + gap, midY);
            if (r.xMin - gap - cw >= Margin) return new Vector2(r.xMin - gap - cw, midY);
            if (r.yMax + gap + ch <= h - Margin) return new Vector2(midX, r.yMax + gap);
            if (r.yMin - gap - ch >= Margin) return new Vector2(midX, r.yMin - gap - ch);
            return new Vector2(Mathf.Clamp(r.xMax - cw - 24, Margin, w - cw - Margin), Mathf.Clamp(r.yMin + 64, Margin, h - ch - Margin));
        }

        private static void Place(VisualElement e, float x, float y, float width, float height)
        {
            e.style.left = x;
            e.style.top = y;
            e.style.width = Mathf.Max(0, width);
            e.style.height = Mathf.Max(0, height);
        }

        // ---- The parcel step ----

        /// <summary>
        /// Picks a vacant, editable parcel near the most housing that is beyond the walking threshold.
        /// Deterministic for a given city state; it is a teaching hint, not a recommendation engine.
        /// </summary>
        private void SuggestParcel()
        {
            _suggestedRow = _suggestedCol = -1;
            PlanningSession s = _app.Session;
            if (s == null) return;
            CityState state = s.Current;
            CityMetrics m = s.CurrentMetrics;
            int threshold = s.Scenario.AccessThresholdMetres;
            int best = -1;
            foreach (CellData c in state.Cells)
            {
                if (c.Use != LandUse.Vacant || !c.Editable) continue;
                int score = 0;
                for (int dr = -3; dr <= 3; dr++)
                for (int dc = -3; dc <= 3; dc++)
                {
                    int r = c.Row + dr, col = c.Col + dc;
                    if (!state.InBounds(r, col)) continue;
                    CellData n = state.Get(r, col);
                    if (n.Use != LandUse.Residential) continue;
                    int walk = m.ParkDistanceMetres[state.IndexOf(r, col)];
                    if (walk == AccessibilityCalculator.Unreachable || walk > threshold) score += n.HousingUnits;
                }
                if (score > best) { best = score; _suggestedRow = c.Row; _suggestedCol = c.Col; }
            }
        }

        private Rect? ParcelHole()
        {
            GridRenderer grid = _app.Grid;
            if (_suggestedRow < 0 || grid == null) return Element("viewport");
            Vector3 centre = grid.CellCentre(_suggestedRow, _suggestedCol);
            float half = grid.CellSize * 0.5f;
            float y = grid.CellTop(_suggestedRow, _suggestedCol);
            float xMin = float.MaxValue, yMin = float.MaxValue, xMax = float.MinValue, yMax = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                Vector3 corner = centre + new Vector3((i & 1) == 0 ? -half : half, 0, (i & 2) == 0 ? -half : half);
                corner.y = y;
                Vector2? p = _ui.WorldToPanel(corner);
                if (!p.HasValue) return Element("viewport");
                xMin = Mathf.Min(xMin, p.Value.x); yMin = Mathf.Min(yMin, p.Value.y);
                xMax = Mathf.Max(xMax, p.Value.x); yMax = Mathf.Max(yMax, p.Value.y);
            }
            const float minSize = 30f;
            float cx = (xMin + xMax) / 2, cy = (yMin + yMax) / 2;
            float sw = Mathf.Max(minSize, xMax - xMin), sh = Mathf.Max(minSize, yMax - yMin);
            return new Rect(cx - sw / 2, cy - sh / 2, sw, sh);
        }

        private bool SelectedParcelIsVacant()
        {
            if (_app.Session == null || !_app.HasSelection) return false;
            CellData c = _app.Session.Current.Get(_app.SelectedRow, _app.SelectedCol);
            return c.Use == LandUse.Vacant && c.Editable;
        }

        private bool SelectedParcelIsNewPark()
        {
            PlanningSession s = _app.Session;
            if (s == null || !_app.HasSelection) return false;
            return s.Current.Get(_app.SelectedRow, _app.SelectedCol).Use == LandUse.Park
                && s.Baseline.Get(_app.SelectedRow, _app.SelectedCol).Use != LandUse.Park;
        }
    }
}
