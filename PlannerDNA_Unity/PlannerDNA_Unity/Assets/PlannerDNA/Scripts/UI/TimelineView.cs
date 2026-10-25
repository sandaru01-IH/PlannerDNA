using PlannerDNA.App;
using PlannerDNA.Commands;
using PlannerDNA.Core;
using PlannerDNA.Storage;
using UnityEngine;
using UnityEngine.UIElements;

namespace PlannerDNA.UI
{
    /// <summary>
    /// Chronological event log: commands (with applied / undone / discarded state), undo, redo,
    /// alternative checkpoints and recorded decisions. Shows only what happened, no inferred motives.
    /// </summary>
    public sealed class TimelineView
    {
        private readonly PlannerApp _app;
        private readonly ScrollView _scroll;
        private readonly Label _summary;
        private int _renderedCount = -1;
        private int _renderedApplied = -1;

        public TimelineView(SandboxUi ui, PlannerApp app)
        {
            _app = app;
            _scroll = ui.Q<ScrollView>("timeline");
            _summary = ui.Q<Label>("timeline-summary");
            _scroll.contentContainer.style.flexDirection = FlexDirection.Row;
            _scroll.contentContainer.style.flexWrap = Wrap.NoWrap;
        }

        public int ChipCount => _scroll.contentContainer.childCount;

        public void Refresh()
        {
            PlanningSession s = _app.Session;
            if (s == null)
            {
                _scroll.Clear();
                _summary.text = "";
                _renderedCount = -1;
                return;
            }

            EditHistory h = s.History;
            int redo = h.StepCount - h.AppliedCount;
            _summary.text = $"{h.AppliedCount} edit{(h.AppliedCount == 1 ? "" : "s")}" +
                            (redo > 0 ? $", {redo} can be redone" : "") +
                            (s.RejectedAttempts.Count > 0 ? $", {s.RejectedAttempts.Count} refused (not counted)" : "");

            if (_renderedCount == s.Timeline.Count && _renderedApplied == h.AppliedCount) return;
            _renderedCount = s.Timeline.Count;
            _renderedApplied = h.AppliedCount;

            _scroll.Clear();
            if (s.Timeline.Count == 0)
            {
                _scroll.Add(UiFormat.Text("No edits yet. Each change you make appears here, in order.", "hint"));
                return;
            }
            foreach (TimelineEntryDto e in s.Timeline)
            {
                Label chip = UiFormat.Text($"#{e.sequence} {e.note}", "chip");
                chip.AddToClassList("chip--" + e.event_type);
                if (e.event_type == "command")
                {
                    StepStatus status = h.StatusOf(e.event_id);
                    if (status == StepStatus.Undone) { chip.AddToClassList("chip--undone"); chip.text += " (undone)"; }
                    else if (status == StepStatus.Discarded) { chip.AddToClassList("chip--discarded"); chip.text += " (discarded)"; }
                }
                chip.tooltip = $"{e.event_type} | {e.action} | {e.utc_time} | source: {e.input_source}";
                _scroll.Add(chip);
            }
            _scroll.schedule.Execute(() => _scroll.scrollOffset = new Vector2(float.MaxValue, 0)).StartingIn(30);
        }
    }
}
