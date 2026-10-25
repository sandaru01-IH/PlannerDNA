using System.Globalization;
using PlannerDNA.App;
using PlannerDNA.Core;
using PlannerDNA.Metrics;
using PlannerDNA.Storage;
using UnityEngine.UIElements;

namespace PlannerDNA.UI
{
    /// <summary>Right panel: goal and rules, selected parcel, metric cards, explanations, learning status.</summary>
    public sealed class AnalysisPanel
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private readonly PlannerApp _app;
        private readonly Label _missionText, _missionHint, _feasibility, _learning;
        private readonly VisualElement _selection, _cards, _constraints, _explain;
        private readonly Button _explainButton;
        private string _openCard;

        /// <summary>Local decision counts from the most recent refresh, or null if they could not be read.</summary>
        public PreferenceStats LastStats { get; private set; }

        public AnalysisPanel(SandboxUi ui, PlannerApp app)
        {
            _app = app;
            _missionText = ui.Q<Label>("mission-text");
            _missionHint = ui.Q<Label>("mission-hint");
            _selection = ui.Q<VisualElement>("selection-panel");
            _cards = ui.Q<VisualElement>("metric-cards");
            _feasibility = ui.Q<Label>("feasibility-label");
            _constraints = ui.Q<VisualElement>("constraint-list");
            _explain = ui.Q<VisualElement>("explain-panel");
            _learning = ui.Q<Label>("learning-status");
            _explainButton = ui.Q<Button>("btn-explain");

            foreach (Explanation e in MetricDefinitions.Metrics)
            {
                _explain.Add(UiFormat.Text(e.Title, "explain-title"));
                _explain.Add(UiFormat.Text(e.Body, "explain-body"));
            }
            _explainButton.clicked += () =>
            {
                bool show = _explain.ClassListContains("hidden");
                _explain.EnableInClassList("hidden", !show);
                _explainButton.text = show ? "Hide the calculations" : "How are these calculated?";
            };
        }

        public void Refresh()
        {
            PlanningSession s = _app.Session;
            if (s == null)
            {
                _missionText.text = "Start a challenge from the Home screen.";
                _missionHint.text = "";
                _cards.Clear();
                _constraints.Clear();
                _feasibility.text = "";
                RefreshSelection();
                RefreshLearning();
                return;
            }

            _missionText.text = $"Get more homes within a {s.Scenario.AccessThresholdMetres} m walk of a park.";
            _missionHint.text = s.Scenario.MissionText;

            CityMetrics m = _app.DisplayedMetrics;
            CityMetrics b = s.BaselineMetrics;
            BuildCards(s, m, b);
            BuildConstraints(DisplayedConstraints());
            RefreshSelection();
            RefreshLearning();
        }

        private ConstraintReport DisplayedConstraints()
        {
            PlanningSession s = _app.Session;
            if (_app.PreviewSlot == AlternativeSlot.A && s.AlternativeA != null) return s.AlternativeA.Constraints;
            if (_app.PreviewSlot == AlternativeSlot.B && s.AlternativeB != null) return s.AlternativeB.Constraints;
            return s.CurrentConstraints;
        }

        private void BuildCards(PlanningSession s, CityMetrics m, CityMetrics b)
        {
            _cards.Clear();
            ScenarioDefinition def = s.Scenario;

            double accessDelta = UiFormat.AccessOrZero(m) - UiFormat.AccessOrZero(b);
            AddCard("metric-access", "Homes near a park", UiFormat.Access(m),
                $"Start {UiFormat.Access(b)}, change {UiFormat.SignedPoints(accessDelta)}",
                $"{N(m.AccessibleHousingUnits)} of {N(m.HousingUnits)} housing units are within a {def.AccessThresholdMetres} m walk along roads. This is the main goal.",
                accessDelta > 0.05 ? ("better", "tag--toward") : accessDelta < -0.05 ? ("worse", "tag--tradeoff") : ("no change", ""));

            int minUnits = def.MinimumHousingUnits(b.HousingUnits);
            int housingDelta = m.HousingUnits - b.HousingUnits;
            (string, string) housingTag = housingDelta >= 0 ? ("kept", "tag--toward")
                : m.HousingUnits >= minUnits ? ("fewer, still allowed", "tag--tradeoff") : ("too few", "tag--tradeoff");
            AddCard("metric-housing", "Homes (housing units)", m.HousingUnits.ToString(Inv),
                $"Start {N(b.HousingUnits)}, change {UiFormat.Signed(housingDelta)}",
                $"Must stay at or above {N(minUnits)} ({def.MinHousingRatio * 100:0}% of the start). Counts room for households (floors x 4), not people.",
                housingTag);

            double shareDelta = m.ParkSharePct - b.ParkSharePct;
            AddCard("metric-park-share", "Land that is park", UiFormat.Pct(m.ParkSharePct),
                $"Start {UiFormat.Pct(b.ParkSharePct)}, change {UiFormat.SignedPoints(shareDelta)}",
                $"{m.ParkCells} park parcels out of {m.NonRoadCells} parcels that are not road.",
                shareDelta > 0.05 ? ("more green", "tag--toward") : shareDelta < -0.05 ? ("less green", "tag--tradeoff") : ("no change", ""));

            bool withinBudget = m.SpentCredits <= def.BudgetLimit;
            AddCard("metric-cost", "Budget used (credits)", $"{m.SpentCredits} / {def.BudgetLimit}",
                $"{m.ChangedCells} parcel{(m.ChangedCells == 1 ? "" : "s")} changed",
                "Credits are made up for this exercise and come from a fixed price list. Undoing a change gives the credits back.",
                withinBudget ? ($"{def.BudgetLimit - m.SpentCredits} left", "tag--toward") : ("over budget", "tag--tradeoff"));

            AddCard("metric-lost-housing", "Original homes removed", m.LostOriginalHousingUnits.ToString(Inv),
                $"Overall change {UiFormat.Signed(m.NetHousingChange)} units",
                "Housing units taken away from parcels that were homes at the start. A simple warning sign for displacement, not a count of real people.",
                m.LostOriginalHousingUnits == 0 ? ("none", "") : ("trade-off", "tag--tradeoff"));
        }

        private static string N(int value) => value.ToString(Inv);

        private void AddCard(string name, string title, string value, string detail, string context, (string text, string cls) tag)
        {
            var card = new VisualElement { name = name };
            card.AddToClassList("metric-card");
            card.EnableInClassList("metric-card--open", _openCard == name);
            card.RegisterCallback<ClickEvent>(_ =>
            {
                bool open = !card.ClassListContains("metric-card--open");
                foreach (VisualElement other in _cards.Children()) other.RemoveFromClassList("metric-card--open");
                card.EnableInClassList("metric-card--open", open);
                _openCard = open ? name : null;
            });

            var head = UiFormat.Box("metric-head");
            head.Add(UiFormat.Text(title, "metric-title"));
            Label tagLabel = UiFormat.Text(tag.text, "tag");
            UiFormat.AddClasses(tagLabel, tag.cls);
            head.Add(tagLabel);
            card.Add(head);

            var line = UiFormat.Box("metric-line");
            Label valueLabel = UiFormat.Text(value, "metric-value");
            valueLabel.name = name + "-value";
            line.Add(valueLabel);
            line.Add(UiFormat.Text(detail, "metric-detail"));
            card.Add(line);
            card.Add(UiFormat.Text(context, "metric-context"));
            _cards.Add(card);
        }

        private void BuildConstraints(ConstraintReport report)
        {
            _constraints.Clear();
            _feasibility.text = report.Feasible
                ? "All rules are met"
                : "A rule is broken. You can keep exploring, but this design cannot be a valid answer.";
            _feasibility.EnableInClassList("feasibility--ok", report.Feasible);
            _feasibility.EnableInClassList("feasibility--bad", !report.Feasible);
            foreach (ConstraintResult r in report.Results)
            {
                if (r.Id == ConstraintEvaluator.CostRulesDefined && r.Passed) continue;
                var row = UiFormat.Box("constraint-row");
                row.EnableInClassList("constraint-row--fail", !r.Passed);
                row.Add(UiFormat.Box(r.Passed ? "constraint-dot constraint-dot--pass" : "constraint-dot constraint-dot--fail"));
                row.Add(UiFormat.Text(FriendlyRule(r), "constraint-text"));
                _constraints.Add(row);
            }
        }

        private string FriendlyRule(ConstraintResult r)
        {
            ScenarioDefinition def = _app.Session.Scenario;
            CityMetrics m = _app.DisplayedMetrics;
            switch (r.Id)
            {
                case ConstraintEvaluator.Budget:
                    return $"Spend at most {def.BudgetLimit} credits ({m.SpentCredits} used)";
                case ConstraintEvaluator.Housing:
                    return $"Keep at least {def.MinHousingRatio * 100:0}% of the homes ({N(m.HousingUnits)} of at least {N(def.MinimumHousingUnits(_app.Session.BaselineMetrics.HousingUnits))})";
                case ConstraintEvaluator.MaxFloors:
                    return r.Passed ? $"No building taller than {def.MaxFloors} floors" : $"No building taller than {def.MaxFloors} floors: {r.Detail}";
                default:
                    return $"{r.Label}: {r.Detail}";
            }
        }

        public void RefreshSelection()
        {
            _selection.Clear();
            PlanningSession s = _app.Session;
            if (s == null || !_app.HasSelection)
            {
                _selection.Add(UiFormat.Text("Nothing selected. Click a parcel in the city to see what it is.", "hint"));
                return;
            }

            CityState state = _app.DisplayedState;
            int row = _app.SelectedRow, col = _app.SelectedCol;
            CellData cell = state.Get(row, col);
            CellData baseCell = s.Baseline.Get(row, col);
            CityMetrics m = _app.DisplayedMetrics;

            _selection.Add(UiFormat.KeyValue("Parcel", cell.Id));
            _selection.Add(UiFormat.KeyValue("What it is", UiFormat.CellSummary(cell)));
            if (cell.Use == LandUse.Residential || cell.Use == LandUse.Commercial)
                _selection.Add(UiFormat.KeyValue("Floors (limit)", $"{cell.Floors} ({System.Math.Min(cell.MaxFloors, s.Scenario.MaxFloors)})"));
            _selection.Add(UiFormat.KeyValue("Zoning", cell.Zoning.DisplayName()));
            if (cell.Use == LandUse.Residential)
            {
                _selection.Add(UiFormat.KeyValue("Housing units", $"{cell.HousingUnits} ({cell.Floors} floors x {cell.UnitsPerFloor})"));
                _selection.Add(UiFormat.KeyValue("Walk to a park",
                    UiFormat.ParkWalk(m.ParkDistanceMetres[state.IndexOf(row, col)], s.Scenario.AccessThresholdMetres)));
            }
            if (baseCell != cell)
            {
                _selection.Add(UiFormat.KeyValue("At the start", UiFormat.CellSummary(baseCell)));
                CostRules.CostLine line = CostRules.Default.Evaluate(baseCell, cell);
                _selection.Add(UiFormat.KeyValue("Cost of change", line.Defined ? $"{line.Credits} credits ({line.Rule})" : "undefined"));
            }
            if (!cell.Editable)
                _selection.Add(UiFormat.Text("This parcel is fixed in this challenge and cannot be changed.", "hint"));
        }

        public void RefreshLearning()
        {
            PreferenceStats stats;
            try { stats = _app.Store.ComputeStats(); }
            catch (System.Exception e) when (e is System.IO.IOException || e is System.UnauthorizedAccessException)
            {
                LastStats = null;
                _learning.text = "Could not read local decisions: " + e.Message;
                return;
            }
            LastStats = stats;
            _learning.text =
                $"You have compared {stats.unique_comparisons} pair{(stats.unique_comparisons == 1 ? "" : "s")} of designs " +
                $"({stats.training_eligible} usable for learning).\n" + ModelStatus();
        }

        private string ModelStatus()
        {
            var health = _app.Health;
            if (health == null || !health.StorageReady)
                return "Model status unknown: the local backend is offline. A model exists only after you press \"Train my model\" in the Learning Lab.";
            if (!health.HasModel)
                return "No preference model has been trained yet, so nothing has been learned and no predictions are shown.";
            return $"Active model: {health.ActiveModelVersion}. It summarises patterns in your past choices; the Learning Lab shows its uncertainty and limits.";
        }
    }
}
