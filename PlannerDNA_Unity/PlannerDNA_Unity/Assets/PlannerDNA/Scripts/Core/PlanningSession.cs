using System;
using System.Collections.Generic;
using PlannerDNA.Commands;
using PlannerDNA.Generation;
using PlannerDNA.Metrics;
using PlannerDNA.Storage;

namespace PlannerDNA.Core
{
    public readonly struct CommandOutcome
    {
        public readonly bool Accepted;
        public readonly string Message;
        public readonly string EventId;

        public CommandOutcome(bool accepted, string message, string eventId)
        {
            Accepted = accepted;
            Message = message;
            EventId = eventId;
        }
    }

    public sealed class RejectedAttempt
    {
        public string Action;
        public string CellId;
        public string Reason;
        public string InputSource;
        public string UtcTime;
    }

    /// <summary>
    /// One planning session on one scenario: baseline, edit history, live metrics, A/B alternatives
    /// and the chronological event log. Pure C# (no MonoBehaviour), so it is fully unit-testable.
    /// </summary>
    public sealed class PlanningSession
    {
        public const string ChoiceA = "A";
        public const string ChoiceB = "B";
        public const string ChoiceTie = "tie";
        public const string ChoiceSkip = "skip";

        private readonly Func<DateTime> _clock;
        private readonly Func<string> _newId;
        private readonly List<TimelineEntryDto> _timeline = new List<TimelineEntryDto>();
        private readonly List<RejectedAttempt> _rejected = new List<RejectedAttempt>();
        private int _sequence;

        public string SessionId { get; }
        public ScenarioDefinition Scenario { get; }
        public CityState Baseline { get; }
        public string BaselineHash { get; }
        public CityMetrics BaselineMetrics { get; }
        public int GeneratorAttempt { get; }
        public string ExampleImprovingCellId { get; }
        public EditHistory History { get; }

        public CityState Current => History.Current;
        public CityMetrics CurrentMetrics { get; private set; }
        public ConstraintReport CurrentConstraints { get; private set; }

        public AlternativeSnapshot AlternativeA { get; private set; }
        public AlternativeSnapshot AlternativeB { get; private set; }

        public IReadOnlyList<TimelineEntryDto> Timeline => _timeline;
        public IReadOnlyList<RejectedAttempt> RejectedAttempts => _rejected;

        /// <summary>Raised for every timeline entry so callers can persist it.</summary>
        public event Action<TimelineEntryDto> EventLogged;
        /// <summary>Raised whenever Current, metrics or alternatives change.</summary>
        public event Action Changed;

        public PlanningSession(GeneratedScenario scenario, string sessionId = null, Func<DateTime> clock = null, Func<string> newId = null)
        {
            if (scenario == null) throw new ArgumentNullException(nameof(scenario));
            _clock = clock ?? (() => DateTime.UtcNow);
            _newId = newId ?? (() => Guid.NewGuid().ToString("N"));
            SessionId = sessionId ?? "session-" + _newId();
            Scenario = scenario.Definition;
            Baseline = scenario.Baseline;
            BaselineHash = Baseline.ContentHash();
            GeneratorAttempt = scenario.Attempt;
            ExampleImprovingCellId = scenario.ExampleImprovingCellId;
            BaselineMetrics = MetricEngine.EvaluateBaseline(Baseline, Scenario);
            History = new EditHistory(Baseline);
            Recalculate();
        }

        public static PlanningSession Start(int seed, string sessionId = null, Func<DateTime> clock = null, Func<string> newId = null)
        {
            return new PlanningSession(GreenSpaceScenarioGenerator.Generate(seed), sessionId, clock, newId);
        }

        public bool IsAtBaseline => Current.ContentEquals(Baseline);

        public ValidationResult Preview(EditAction action, int row, int col)
        {
            return ActionValidator.Validate(Current, Baseline, Scenario, action, row, col);
        }

        public CommandOutcome Execute(EditAction action, int row, int col, InputSource source)
        {
            ValidationResult v = Preview(action, row, col);
            string cellId = Current.InBounds(row, col) ? CellIds.Format(row, col) : string.Empty;
            if (!v.Ok)
            {
                _rejected.Add(new RejectedAttempt
                {
                    Action = action.ToKey(), CellId = cellId, Reason = v.Reason,
                    InputSource = source.ToKey(), UtcTime = _clock().ToString("o"),
                });
                return new CommandOutcome(false, v.Reason, null);
            }

            CellData before = Current.Get(row, col);
            CityState next = Current.WithCell(v.Proposed);
            return Commit(action, cellId, source, next, before, v.Proposed, true,
                $"{action.DisplayName()} on {cellId}");
        }

        public CommandOutcome RestoreBaseline(InputSource source)
        {
            if (IsAtBaseline) return new CommandOutcome(false, "The design is already identical to the baseline.", null);
            int changed = Current.ChangedCellIds(Baseline).Count;
            return Commit(EditAction.RestoreBaseline, string.Empty, source, Baseline, default, default, false,
                $"Restored baseline ({changed} parcel(s) reverted)");
        }

        /// <summary>Applies a previously saved design as one undoable step after verifying it matches this scenario.</summary>
        public CommandOutcome ApplyLoadedDesign(CityState design, InputSource source, string note = "Loaded saved design")
        {
            if (design == null || !design.SameScenarioAs(Baseline))
                return new CommandOutcome(false, "The saved design belongs to a different scenario or version.", null);
            for (int i = 0; i < design.CellCount; i++)
            {
                CellData b = Baseline.Get(i), d = design.Get(i);
                if (b.Row != d.Row || b.Col != d.Col || b.Editable != d.Editable || b.Zoning != d.Zoning || b.MaxFloors != d.MaxFloors)
                    return new CommandOutcome(false, $"Saved design alters fixed parcel properties at {d.Id}.", null);
                if (!b.Editable && b != d)
                    return new CommandOutcome(false, $"Saved design changes protected parcel {d.Id}.", null);
            }
            if (design.ContentEquals(Current)) return new CommandOutcome(false, "The saved design is identical to the current design.", null);
            return Commit(EditAction.LoadDesign, string.Empty, source, design, default, default, false, note);
        }

        private CommandOutcome Commit(EditAction action, string cellId, InputSource source, CityState next,
            CellData before, CellData after, bool hasCellChange, string note)
        {
            CityMetrics metricsBefore = CurrentMetrics;
            bool feasibleBefore = CurrentConstraints.Feasible;
            var step = new HistoryStep
            {
                EventId = _newId(),
                Action = action,
                CellId = cellId,
                Source = source,
                Before = Current,
                After = next,
            };
            History.Push(step);
            Recalculate();

            var entry = NewEntry("command", action.ToKey(), cellId, source, note);
            entry.event_id = step.EventId;
            FillChange(entry, hasCellChange, before, after, metricsBefore, feasibleBefore);
            Log(entry);
            Changed?.Invoke();
            return new CommandOutcome(true, note, step.EventId);
        }

        public CommandOutcome Undo(InputSource source)
        {
            if (!History.CanUndo) return new CommandOutcome(false, "Nothing to undo.", null);
            CityMetrics metricsBefore = CurrentMetrics;
            bool feasibleBefore = CurrentConstraints.Feasible;
            HistoryStep step = History.Undo();
            Recalculate();

            var entry = NewEntry("undo", step.Action.ToKey(), step.CellId, source, $"Undo: {step.Action.DisplayName()}" + Suffix(step.CellId));
            entry.reversal_of_event_id = step.EventId;
            entry.original_event_id = step.EventId;
            bool single = !string.IsNullOrEmpty(step.CellId);
            CellData before = default, after = default;
            if (single && CellIds.TryParse(step.CellId, out int r, out int c))
            {
                before = step.After.Get(r, c);
                after = step.Before.Get(r, c);
            }
            FillChange(entry, single, before, after, metricsBefore, feasibleBefore);
            step.LastUndoEventId = entry.event_id;
            Log(entry);
            Changed?.Invoke();
            return new CommandOutcome(true, entry.note, entry.event_id);
        }

        public CommandOutcome Redo(InputSource source)
        {
            if (!History.CanRedo) return new CommandOutcome(false, "Nothing to redo.", null);
            CityMetrics metricsBefore = CurrentMetrics;
            bool feasibleBefore = CurrentConstraints.Feasible;
            HistoryStep step = History.Redo();
            Recalculate();

            var entry = NewEntry("redo", step.Action.ToKey(), step.CellId, source, $"Redo: {step.Action.DisplayName()}" + Suffix(step.CellId));
            entry.reversal_of_event_id = step.LastUndoEventId ?? string.Empty;
            entry.original_event_id = step.EventId;
            bool single = !string.IsNullOrEmpty(step.CellId);
            CellData before = default, after = default;
            if (single && CellIds.TryParse(step.CellId, out int r, out int c))
            {
                before = step.Before.Get(r, c);
                after = step.After.Get(r, c);
            }
            FillChange(entry, single, before, after, metricsBefore, feasibleBefore);
            Log(entry);
            Changed?.Invoke();
            return new CommandOutcome(true, entry.note, entry.event_id);
        }

        private static string Suffix(string cellId) => string.IsNullOrEmpty(cellId) ? string.Empty : $" on {cellId}";

        // ---- Alternatives and comparison ----

        public AlternativeSnapshot SaveAlternative(AlternativeSlot slot)
        {
            var snapshot = AlternativeSnapshot.Create(slot, "alt-" + _newId(), SessionId, _clock(), Current, Baseline, BaselineMetrics, Scenario);
            SetAlternative(snapshot);
            var entry = NewEntry("checkpoint", slot == AlternativeSlot.A ? "save_alternative_a" : "save_alternative_b", string.Empty,
                InputSource.Mouse, $"Saved Alternative {slot} ({(snapshot.Feasible ? "feasible" : "violates constraints")})");
            entry.original_event_id = snapshot.AlternativeId;
            entry.metrics_after = CitySerializer.ToSummary(snapshot.Metrics, snapshot.Feasible);
            Log(entry);
            Changed?.Invoke();
            return snapshot;
        }

        /// <summary>Restores a previously saved alternative into its slot (e.g. after loading a save).</summary>
        public void SetAlternative(AlternativeSnapshot snapshot)
        {
            if (snapshot == null) return;
            if (snapshot.BaselineHash != BaselineHash)
                throw new InvalidOperationException("Alternative was derived from a different baseline.");
            if (snapshot.Slot == AlternativeSlot.A) AlternativeA = snapshot;
            else AlternativeB = snapshot;
            Changed?.Invoke();
        }

        public ComparisonCheck CheckComparison() => ComparisonRules.Check(AlternativeA, AlternativeB, Baseline);

        /// <summary>
        /// Builds the explicit preference record. Only the user's stated choice is recorded;
        /// nothing is inferred from edits, undo clicks or timing.
        /// </summary>
        public PreferenceDto CreatePreference(string comparisonId, string choice, string reason)
        {
            if (choice != ChoiceA && choice != ChoiceB && choice != ChoiceTie && choice != ChoiceSkip)
                throw new ArgumentException($"Choice must be A, B, tie or skip (got '{choice}').");
            ComparisonCheck check = CheckComparison();
            if (!check.CanCompare) throw new InvalidOperationException(string.Join(" ", check.Problems));

            bool binary = choice == ChoiceA || choice == ChoiceB;
            string note = check.EligibilityNote;
            if (!binary) note = "Tie/skip is stored but excluded from binary training. " + note;
            return new PreferenceDto
            {
                schema_version = Versions.SchemaVersion,
                app_version = Versions.AppVersion,
                comparison_id = comparisonId,
                session_id = SessionId,
                scenario_id = Baseline.ScenarioId,
                alternative_a_id = AlternativeA.AlternativeId,
                alternative_b_id = AlternativeB.AlternativeId,
                choice = choice,
                reason = reason ?? string.Empty,
                mode = "train",
                shown_left_alternative_id = AlternativeA.AlternativeId,
                model_version_locked = string.Empty,
                a_feasible = AlternativeA.Feasible,
                b_feasible = AlternativeB.Feasible,
                training_eligible = binary && check.BothFeasible,
                eligibility_note = note,
                feature_version = Versions.FeatureVersion,
                created_utc = _clock().ToString("o"),
                sync_status = "local_only",
            };
        }

        /// <summary>
        /// Puts two system-generated designs into slots A and B for a blinded evaluation trial. They are
        /// fresh alternatives (new ids) so they have never been compared before; the current design is untouched.
        /// </summary>
        public void LoadEvaluationPair(CityState designA, CityState designB, string note)
        {
            if (designA == null || designB == null) throw new ArgumentNullException(designA == null ? nameof(designA) : nameof(designB));
            DateTime now = _clock();
            var a = AlternativeSnapshot.Create(AlternativeSlot.A, "alt-" + _newId(), SessionId, now, designA, Baseline, BaselineMetrics, Scenario);
            var b = AlternativeSnapshot.Create(AlternativeSlot.B, "alt-" + _newId(), SessionId, now, designB, Baseline, BaselineMetrics, Scenario);
            AlternativeA = a;
            AlternativeB = b;
            var entry = NewEntry("checkpoint", "load_evaluation_pair", string.Empty, InputSource.System, note);
            entry.original_event_id = a.AlternativeId;
            Log(entry);
            Changed?.Invoke();
        }

        /// <summary>
        /// The answer to one pre-registered blinded trial. Mode 'evaluation', locked to the frozen model and
        /// never training data; the screen order is the one the backend randomised for this trial.
        /// </summary>
        public PreferenceDto CreateEvaluationPreference(string comparisonId, string choice, string reason, string trialId,
            string lockedModelVersion, string shownLeftAlternativeId)
        {
            if (string.IsNullOrEmpty(trialId)) throw new ArgumentException("An evaluation choice must answer a registered trial.");
            if (string.IsNullOrEmpty(lockedModelVersion)) throw new ArgumentException("An evaluation choice must name the frozen model.");
            PreferenceDto pref = CreatePreference(comparisonId, choice, reason);
            if (shownLeftAlternativeId != pref.alternative_a_id && shownLeftAlternativeId != pref.alternative_b_id)
                throw new ArgumentException("The left design must be one of the two compared designs.");
            pref.mode = "evaluation";
            pref.evaluation_trial_id = trialId;
            pref.model_version_locked = lockedModelVersion;
            pref.shown_left_alternative_id = shownLeftAlternativeId;
            pref.training_eligible = false;
            pref.eligibility_note = "Blinded evaluation choice: scored against the sealed prediction, never used for training.";
            return pref;
        }

        public void LogDecision(PreferenceDto pref)
        {
            string what = pref.mode == "evaluation" ? $"blinded trial {pref.evaluation_trial_id}" : "A vs B";
            var entry = NewEntry("decision", "record_preference", string.Empty, InputSource.Mouse,
                $"Recorded choice '{pref.choice}' for {what}");
            entry.original_event_id = pref.comparison_id;
            Log(entry);
            Changed?.Invoke();
        }

        public string NewComparisonId() => "cmp-" + _newId();

        // ---- Helpers ----

        private void Recalculate()
        {
            CurrentMetrics = MetricEngine.Evaluate(Current, Baseline, Scenario);
            CurrentConstraints = ConstraintEvaluator.Evaluate(Current, CurrentMetrics, BaselineMetrics, Scenario);
        }

        private TimelineEntryDto NewEntry(string type, string action, string cellId, InputSource source, string note)
        {
            return new TimelineEntryDto
            {
                event_id = _newId(),
                session_id = SessionId,
                scenario_id = Baseline.ScenarioId,
                sequence = ++_sequence,
                event_type = type,
                action = action,
                cell_id = cellId ?? string.Empty,
                input_source = source.ToKey(),
                utc_time = _clock().ToString("o"),
                reversal_of_event_id = string.Empty,
                original_event_id = string.Empty,
                note = note,
            };
        }

        private void FillChange(TimelineEntryDto entry, bool hasCellChange, CellData before, CellData after,
            CityMetrics metricsBefore, bool feasibleBefore)
        {
            entry.has_cell_change = hasCellChange;
            if (hasCellChange)
            {
                entry.before = CitySerializer.ToDto(before);
                entry.after = CitySerializer.ToDto(after);
            }
            entry.metrics_before = CitySerializer.ToSummary(metricsBefore, feasibleBefore);
            entry.metrics_after = CitySerializer.ToSummary(CurrentMetrics, CurrentConstraints.Feasible);
        }

        private void Log(TimelineEntryDto entry)
        {
            _timeline.Add(entry);
            EventLogged?.Invoke(entry);
        }
    }
}
