using System;
using System.Collections.Generic;
using PlannerDNA.Core;

namespace PlannerDNA.Commands
{
    /// <summary>One committed edit with complete before/after snapshots (exact undo by construction).</summary>
    public sealed class HistoryStep
    {
        public string EventId;
        public EditAction Action;
        /// <summary>Empty for whole-city steps such as RestoreBaseline.</summary>
        public string CellId;
        public InputSource Source;
        public CityState Before;
        public CityState After;
        /// <summary>Event id of the most recent undo of this step (the redo reverses it).</summary>
        public string LastUndoEventId;
    }

    public enum StepStatus
    {
        Applied,
        Undone,
        Discarded,
    }

    /// <summary>
    /// Linear undo/redo stack of immutable city snapshots. Undo returns exactly the previous
    /// snapshot; a new edit after undo discards the redo branch.
    /// </summary>
    public sealed class EditHistory
    {
        private readonly List<HistoryStep> _steps = new List<HistoryStep>();
        private readonly HashSet<string> _discarded = new HashSet<string>();
        private int _applied;

        public EditHistory(CityState initial)
        {
            Initial = initial ?? throw new ArgumentNullException(nameof(initial));
        }

        public CityState Initial { get; }
        public CityState Current => _applied == 0 ? Initial : _steps[_applied - 1].After;
        public bool CanUndo => _applied > 0;
        public bool CanRedo => _applied < _steps.Count;
        public int AppliedCount => _applied;
        public int StepCount => _steps.Count;
        public HistoryStep NextUndo => CanUndo ? _steps[_applied - 1] : null;
        public HistoryStep NextRedo => CanRedo ? _steps[_applied] : null;

        public void Push(HistoryStep step)
        {
            if (step == null) throw new ArgumentNullException(nameof(step));
            if (!ReferenceEquals(step.Before, Current) && !step.Before.ContentEquals(Current))
                throw new InvalidOperationException("Step does not start from the current state.");
            for (int i = _steps.Count - 1; i >= _applied; i--)
            {
                _discarded.Add(_steps[i].EventId);
                _steps.RemoveAt(i);
            }
            _steps.Add(step);
            _applied++;
        }

        public HistoryStep Undo()
        {
            if (!CanUndo) return null;
            _applied--;
            return _steps[_applied];
        }

        public HistoryStep Redo()
        {
            if (!CanRedo) return null;
            HistoryStep step = _steps[_applied];
            _applied++;
            return step;
        }

        public StepStatus StatusOf(string eventId)
        {
            for (int i = 0; i < _steps.Count; i++)
                if (_steps[i].EventId == eventId) return i < _applied ? StepStatus.Applied : StepStatus.Undone;
            return StepStatus.Discarded;
        }

        public bool IsKnownStep(string eventId)
        {
            if (_discarded.Contains(eventId)) return true;
            foreach (HistoryStep s in _steps)
                if (s.EventId == eventId) return true;
            return false;
        }
    }
}
