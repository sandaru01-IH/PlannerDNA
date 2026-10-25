using System;
using System.Collections.Generic;
using System.Globalization;
using PlannerDNA.App;
using PlannerDNA.Core;
using PlannerDNA.Metrics;
using PlannerDNA.Storage;
using UnityEngine.UIElements;

namespace PlannerDNA.UI
{
    /// <summary>
    /// Side-by-side comparison of A and B (same baseline verified), with an explicit
    /// A / B / Tie / Skip choice and optional reason. One confirmed record per pair.
    /// </summary>
    public sealed class CompareView
    {
        private readonly SandboxUi _ui;
        private readonly PlannerApp _app;
        private readonly Label _verify, _problems, _tradeoffs, _eligibility, _result, _prediction;
        private readonly VisualElement _table;
        private readonly Button _previewA, _previewB, _confirm;
        private readonly Dictionary<string, Button> _choiceButtons = new Dictionary<string, Button>();
        private readonly TextField _reason;
        private readonly Dictionary<string, string> _comparisonIds = new Dictionary<string, string>();

        private string _pairKey;
        private string _selectedChoice;
        private bool _locked;

        public string CurrentComparisonId => _pairKey != null && _comparisonIds.TryGetValue(_pairKey, out string id) ? id : null;
        public string SelectedChoice => _selectedChoice;
        public bool IsLocked => _locked;
        public PairPredictionDto LastPrediction { get; private set; }
        public bool PredictionPending { get; private set; }
        public string PredictionText => _prediction.text;

        public CompareView(SandboxUi ui, PlannerApp app)
        {
            _ui = ui;
            _app = app;
            _verify = ui.Q<Label>("compare-verify");
            _problems = ui.Q<Label>("compare-problems");
            _table = ui.Q<VisualElement>("compare-table");
            _tradeoffs = ui.Q<Label>("compare-tradeoffs");
            _eligibility = ui.Q<Label>("compare-eligibility");
            _result = ui.Q<Label>("compare-result");
            _prediction = ui.Q<Label>("compare-prediction");
            _previewA = ui.Q<Button>("btn-preview-a");
            _previewB = ui.Q<Button>("btn-preview-b");
            _confirm = ui.Q<Button>("btn-confirm-choice");
            _reason = ui.Q<TextField>("reason-field");

            _choiceButtons[PlanningSession.ChoiceA] = ui.Q<Button>("choice-a");
            _choiceButtons[PlanningSession.ChoiceB] = ui.Q<Button>("choice-b");
            _choiceButtons[PlanningSession.ChoiceTie] = ui.Q<Button>("choice-tie");
            _choiceButtons[PlanningSession.ChoiceSkip] = ui.Q<Button>("choice-skip");
            foreach (KeyValuePair<string, Button> pair in _choiceButtons)
            {
                string choice = pair.Key;
                pair.Value.clicked += () => SelectChoice(choice);
            }

            _previewA.clicked += () => ui.BeginPreviewFromCompare(AlternativeSlot.A);
            _previewB.clicked += () => ui.BeginPreviewFromCompare(AlternativeSlot.B);
            _confirm.clicked += Confirm;
            ui.Q<Button>("btn-compare-close").clicked += () => ui.CloseOverlay(ui.Q<VisualElement>("compare-screen"));
        }

        public void Open()
        {
            PlanningSession s = _app.Session;
            ComparisonCheck check = s.CheckComparison();
            AlternativeSnapshot a = s.AlternativeA, b = s.AlternativeB;

            string newKey = a != null && b != null ? a.AlternativeId + "|" + b.AlternativeId : null;
            if (newKey != _pairKey)
            {
                _pairKey = newKey;
                _selectedChoice = null;
                _locked = false;
                _reason.value = string.Empty;
                _result.text = string.Empty;
                SetPrediction(null);
                LastPrediction = null;
            }
            if (_pairKey != null && !_comparisonIds.ContainsKey(_pairKey)) _comparisonIds[_pairKey] = s.NewComparisonId();

            _problems.EnableInClassList("hidden", check.CanCompare);
            _problems.text = string.Join("\n", check.Problems);
            _verify.text = check.CanCompare
                ? $"Verified: both from scenario {s.Baseline.ScenarioId}, identical baseline (hash {s.BaselineHash.Substring(0, 8)}), " +
                  $"{s.Baseline.GeneratorVersion}, {s.Baseline.MetricVersion}."
                : "This comparison cannot be recorded until the problems below are fixed.";

            _table.Clear();
            if (a != null && b != null) BuildTable(s, a, b);
            _tradeoffs.text = a != null && b != null && check.CanCompare ? Tradeoffs(s, a, b) : string.Empty;
            _eligibility.text = check.CanCompare ? check.EligibilityNote : string.Empty;

            if (check.CanCompare && !_locked) CheckAlreadyRecorded(a, b);

            _previewA.SetEnabled(a != null);
            _previewB.SetEnabled(b != null);
            UpdateChoiceState(check.CanCompare);
        }

        private void CheckAlreadyRecorded(AlternativeSnapshot a, AlternativeSnapshot b)
        {
            List<PreferenceDto> existing;
            try { existing = _app.Store.LoadAllPreferences(); }
            catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException) { return; }
            foreach (PreferenceDto p in existing)
            {
                bool samePair = (p.alternative_a_id == a.AlternativeId && p.alternative_b_id == b.AlternativeId)
                                || (p.alternative_a_id == b.AlternativeId && p.alternative_b_id == a.AlternativeId);
                if (!samePair) continue;
                _locked = true;
                _selectedChoice = p.choice;
                _result.text = $"Already recorded for this pair: '{p.choice}' ({p.comparison_id}). Save a new A or B to compare again.";
                return;
            }
        }

        private void SelectChoice(string choice)
        {
            if (_locked) return;
            _selectedChoice = choice;
            UpdateChoiceState(true);
        }

        private void UpdateChoiceState(bool canCompare)
        {
            foreach (KeyValuePair<string, Button> pair in _choiceButtons)
            {
                pair.Value.EnableInClassList("choice-button--selected", pair.Key == _selectedChoice);
                pair.Value.SetEnabled(canCompare && !_locked);
            }
            _reason.SetEnabled(canCompare && !_locked);
            _confirm.SetEnabled(canCompare && !_locked && _selectedChoice != null);
        }

        public void Confirm()
        {
            if (_locked || _selectedChoice == null || CurrentComparisonId == null) return;
            StoreResult result;
            try
            {
                result = _app.SubmitPreference(CurrentComparisonId, _selectedChoice, _reason.value);
            }
            catch (InvalidOperationException e)
            {
                _result.text = e.Message;
                return;
            }
            if (result.Saved || result.Duplicate) _locked = true;
            _result.text = result.Saved
                ? $"Saved locally ({_selectedChoice}). Stored as your explicit choice; nothing is trained until you press \"Train my model\". File: {result.Path}"
                : result.Message;
            _ui.ShowMessage(result.Saved ? "Decision saved locally." : result.Message, !result.Saved);
            UpdateChoiceState(true);
            if (result.Saved) RevealPrediction(CurrentComparisonId, _selectedChoice);
        }

        /// <summary>
        /// After (never before) the choice is confirmed, asks the backend's saved model what it would have
        /// predicted for this pair. Shows nothing when there is no trained model.
        /// </summary>
        private void RevealPrediction(string comparisonId, string choice)
        {
            PlanningSession s = _app.Session;
            AlternativeSnapshot a = s?.AlternativeA, b = s?.AlternativeB;
            LastPrediction = null;
            if (a == null || b == null) return;
            if (!_app.Learning.Available)
            {
                SetPrediction("Model prediction: not available while the local backend is offline.");
                return;
            }
            if (!_app.Learning.BackendHasModel)
            {
                SetPrediction("Model prediction: none. No preference model has been trained, so nothing is predicted.");
                return;
            }
            string key = _pairKey, scenarioId = s.Baseline.ScenarioId, aId = a.AlternativeId, bId = b.AlternativeId;
            PredictionPending = true;
            SetPrediction("Model prediction: waiting for this comparison to reach the local backend...");
            _app.Learning.WhenReady(
                () => _app.IsDelivered(OutboxKinds.Alternative, aId, bId) && _app.IsDelivered(OutboxKinds.Preference, comparisonId),
                LearningLabView.SyncWaitSeconds,
                ready =>
                {
                    if (key != _pairKey) { PredictionPending = false; return; }
                    if (!ready)
                    {
                        PredictionPending = false;
                        SetPrediction("Model prediction: not shown, because this comparison has not reached the backend yet. Your choice is saved.");
                        return;
                    }
                    var request = new PredictPairRequestDto
                    {
                        scenario_id = scenarioId, alternative_a_id = aId, alternative_b_id = bId, comparison_id = comparisonId,
                    };
                    _app.Learning.PredictPair(request, r =>
                    {
                        PredictionPending = false;
                        if (key != _pairKey) return;
                        if (!r.Ok)
                        {
                            SetPrediction($"Model prediction unavailable ({r.Code}): {r.Message}");
                            return;
                        }
                        LastPrediction = r.Body;
                        SetPrediction(FormatPrediction(r.Body, choice));
                    });
                });
        }

        private static string FormatPrediction(PairPredictionDto p, string choice)
        {
            string favoured = p.favoured == "neither" ? "had no clear preference" : $"favoured {p.favoured}";
            string agreement = choice == PlanningSession.ChoiceA || choice == PlanningSession.ChoiceB
                ? (p.favoured == "neither" ? "" : p.favoured == choice ? " It agrees with your choice." : " It differs from your choice.")
                : " You chose tie/skip, which the model does not predict.";
            string training = p.comparison_in_training_set
                ? " Note: this comparison is in the model's training data, so this is not an independent test."
                : " This comparison was not used to train this model.";
            return $"Revealed after your choice. Model {p.model_version} {favoured}: P(A) {Pct(p.p_a)}, P(B) {Pct(p.p_b)}.{agreement}{training}\n" +
                   Contributions(p) + " " + p.caveat;
        }

        /// <summary>The two measured differences that moved the model's score most (weight x (A - B)).</summary>
        private static string Contributions(PairPredictionDto p)
        {
            var sorted = new List<ContributionDto>(p.contributions);
            sorted.Sort((x, y) => Math.Abs(y.contribution).CompareTo(Math.Abs(x.contribution)));
            var parts = new List<string>();
            foreach (ContributionDto c in sorted)
            {
                if (parts.Count == 2 || Math.Abs(c.contribution) < 1e-6) break;
                parts.Add($"{c.label} ({c.contribution.ToString("+0.00;-0.00", Inv)}, toward {(c.contribution > 0 ? "A" : "B")})");
            }
            return parts.Count == 0
                ? "No measured difference between A and B moved the model's score."
                : "Largest contributions: " + string.Join("; ", parts) + ".";
        }

        /// <summary>A logistic model never gives exactly 0 or 1, so rounded extremes are shown as bounds.</summary>
        private static string Pct(double v) =>
            v < 0.005 ? "<1%" : v > 0.995 ? ">99%" : (v * 100).ToString("0", Inv) + "%";

        private void SetPrediction(string text)
        {
            _prediction.text = text ?? string.Empty;
            _prediction.EnableInClassList("hidden", string.IsNullOrEmpty(text));
        }

        public void SelectForTest(string choice, string reason)
        {
            SelectChoice(choice);
            _reason.value = reason ?? string.Empty;
        }

        // ---- Table ----

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private void BuildTable(PlanningSession s, AlternativeSnapshot a, AlternativeSnapshot b)
        {
            CityMetrics bm = s.BaselineMetrics, am = a.Metrics, mb = b.Metrics;
            Row(true, "Indicator", "Baseline", "A", "B", "A minus B");
            Row(false, "Park access (% housing within 400 m walk)", UiFormat.Access(bm), UiFormat.Access(am), UiFormat.Access(mb),
                UiFormat.SignedPoints(UiFormat.AccessOrZero(am) - UiFormat.AccessOrZero(mb)));
            Row(false, "Housing capacity (units)", bm.HousingUnits.ToString(Inv), am.HousingUnits.ToString(Inv), mb.HousingUnits.ToString(Inv),
                UiFormat.Signed(am.HousingUnits - mb.HousingUnits));
            Row(false, "Park share of non-road land", UiFormat.Pct(bm.ParkSharePct), UiFormat.Pct(am.ParkSharePct), UiFormat.Pct(mb.ParkSharePct),
                UiFormat.SignedPoints(am.ParkSharePct - mb.ParkSharePct));
            Row(false, "Cost (synthetic credits)", "0", am.SpentCredits.ToString(Inv), mb.SpentCredits.ToString(Inv),
                UiFormat.Signed(am.SpentCredits - mb.SpentCredits));
            Row(false, "Lost original housing (units)", "0", am.LostOriginalHousingUnits.ToString(Inv), mb.LostOriginalHousingUnits.ToString(Inv),
                UiFormat.Signed(am.LostOriginalHousingUnits - mb.LostOriginalHousingUnits));
            Row(false, "Changed parcels vs baseline", "0", am.ChangedCells.ToString(Inv), mb.ChangedCells.ToString(Inv),
                UiFormat.Signed(am.ChangedCells - mb.ChangedCells));
            Row(false, "All hard constraints", "pass", Feasibility(a), Feasibility(b), "");
        }

        private static string Feasibility(AlternativeSnapshot alt)
        {
            return alt.Feasible ? "pass" : "FAIL: " + string.Join(", ", alt.Constraints.FailedIds());
        }

        private void Row(bool header, string label, string baseline, string a, string b, string diff)
        {
            var row = new VisualElement();
            row.AddToClassList("ctable-row");
            if (header) row.AddToClassList("ctable-header");
            row.Add(UiFormat.Text(label, "ctable-label"));
            row.Add(UiFormat.Text(baseline, "ctable-cell"));
            row.Add(UiFormat.Text(a, "ctable-cell"));
            row.Add(UiFormat.Text(b, "ctable-cell"));
            row.Add(UiFormat.Text(diff, "ctable-diff"));
            _table.Add(row);
        }

        private static string Tradeoffs(PlanningSession s, AlternativeSnapshot a, AlternativeSnapshot b)
        {
            var parts = new List<string>();
            CityMetrics am = a.Metrics, bm = b.Metrics;
            double access = UiFormat.AccessOrZero(am) - UiFormat.AccessOrZero(bm);
            if (Math.Abs(access) >= 0.05)
                parts.Add($"{(access > 0 ? "A" : "B")} gives {Math.Abs(access).ToString("0.0", Inv)} pts more park access");
            int housing = am.HousingUnits - bm.HousingUnits;
            if (housing != 0) parts.Add($"{(housing > 0 ? "A" : "B")} keeps {Math.Abs(housing)} more housing units");
            int cost = am.SpentCredits - bm.SpentCredits;
            if (cost != 0) parts.Add($"{(cost < 0 ? "A" : "B")} costs {Math.Abs(cost)} fewer credits");
            int lost = am.LostOriginalHousingUnits - bm.LostOriginalHousingUnits;
            if (lost != 0) parts.Add($"{(lost < 0 ? "A" : "B")} removes {Math.Abs(lost)} fewer original housing units");
            if (parts.Count == 0) return "A and B have identical headline metrics but differ in layout (see the 3D previews).";
            return "Trade-offs: " + string.Join("; ", parts) + ". These are synthetic proxies; the choice is yours.";
        }
    }
}
