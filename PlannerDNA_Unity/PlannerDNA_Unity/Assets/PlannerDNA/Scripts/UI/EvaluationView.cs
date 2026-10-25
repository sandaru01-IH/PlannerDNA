using System;
using System.Collections.Generic;
using System.Globalization;
using PlannerDNA.App;
using PlannerDNA.Core;
using PlannerDNA.Evaluation;
using PlannerDNA.Generation;
using PlannerDNA.Metrics;
using PlannerDNA.Storage;
using PlannerDNA.Suggestions;
using UnityEngine.UIElements;

namespace PlannerDNA.UI
{
    /// <summary>
    /// The blinded test (protocol blinded-eval-1.0.0). The backend freezes the model, pre-registers new
    /// areas, randomises left/right and seals each prediction before the choice. This view only builds
    /// the pairs (rule evalpair-1.0.0, no model input), records choices and shows the backend's report.
    /// Predictions stay sealed until the person finishes, so they cannot steer later choices.
    /// </summary>
    public sealed class EvaluationView
    {
        public const int ShortScenarios = 3, ShortPairs = 4, ShortRepeats = 3;
        public const int FullScenarios = 5, FullPairs = 4, FullRepeats = 5;
        public const string Left = "left", Right = "right";

        private const string Banner =
            "A fair test: for each pair, your frozen model's prediction is computed and sealed BEFORE you choose, and stays hidden " +
            "until the end. The areas are new to you and are never used for training. Synthetic scenarios only: a good score means " +
            "the model summarises your own choices consistently, not that it understands planning.";

        private const string HowItWorks =
            "How it works:\n" +
            "1. PlannerDNA freezes your current model and picks new areas you have never practised on.\n" +
            "2. For each pair, choose the design you prefer. Which one appears on the left is decided at random.\n" +
            "3. A few pairs come back later with the sides swapped, to see how consistent your choices are.\n" +
            "4. At the end the sealed predictions are opened and compared with your choices and with simple rules of thumb.";

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private readonly SandboxUi _ui;
        private readonly PlannerApp _app;
        private readonly VisualElement _screen, _startRow, _pair, _table, _reportTable, _history;
        private readonly Label _status, _explain, _progress, _pairTitle, _seal, _result, _reveal, _reportTitle, _reportSummary,
            _reportNotes, _historyNote;
        private readonly Button _startShort, _startFull, _next, _finish, _confirm, _showLeft, _showRight;
        private readonly Dictionary<string, Button> _choices = new Dictionary<string, Button>();
        private readonly Dictionary<string, string> _comparisonIds = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _sealsSeen = new Dictionary<string, string>();

        private int _busy;
        private int _generation;
        private string _selected;
        private bool _answered, _closeArmed;

        public EvaluationOverviewDto LastOverview { get; private set; }
        /// <summary>The open plan, or null when no test is running.</summary>
        public EvaluationPlanDto Plan { get; private set; }
        public EvaluationTrialDto CurrentTrial { get; private set; }
        public EvaluationReportResponseDto LastReport { get; private set; }
        public readonly List<EvaluationRevealDto> Reveals = new List<EvaluationRevealDto>();
        public int SealsVerified { get; private set; }
        public int SealFailures { get; private set; }
        public string LastError { get; private set; }
        /// <summary>Optional note stored with each test choice (empty in the normal UI).</summary>
        public string Reason { get; set; } = string.Empty;
        public bool IsBusy => _busy > 0;
        public bool IsOpen => !_screen.ClassListContains("hidden");
        public bool CurrentAnswered => _answered;
        public string StatusText => _status.text;
        public string ResultText => _result.text;
        public string RevealText => _reveal.text;
        public string ReportNotesText => _reportNotes.text;
        public bool StartEnabled => _startShort.enabledSelf && !_startRow.ClassListContains("hidden");
        public bool NextVisible => !_next.ClassListContains("hidden");
        public bool LeftIsA => CurrentTrial != null && CurrentTrial.shown_left_alternative_id == CurrentTrial.alternative_a_id;

        public EvaluationView(SandboxUi ui, PlannerApp app)
        {
            _ui = ui;
            _app = app;
            _screen = ui.Q<VisualElement>("evaluation-screen");
            _startRow = ui.Q<VisualElement>("eval-start-row");
            _pair = ui.Q<VisualElement>("eval-pair");
            _table = ui.Q<VisualElement>("eval-table");
            _reportTable = ui.Q<VisualElement>("eval-report-table");
            _history = ui.Q<VisualElement>("eval-history");
            _status = ui.Q<Label>("eval-status");
            _explain = ui.Q<Label>("eval-explain");
            _progress = ui.Q<Label>("eval-progress");
            _pairTitle = ui.Q<Label>("eval-pair-title");
            _seal = ui.Q<Label>("eval-seal");
            _result = ui.Q<Label>("eval-result");
            _reveal = ui.Q<Label>("eval-reveal");
            _reportTitle = ui.Q<Label>("eval-report-title");
            _reportSummary = ui.Q<Label>("eval-report-summary");
            _reportNotes = ui.Q<Label>("eval-report-notes");
            _historyNote = ui.Q<Label>("eval-history-note");
            _startShort = ui.Q<Button>("btn-eval-start-short");
            _startFull = ui.Q<Button>("btn-eval-start-full");
            _next = ui.Q<Button>("btn-eval-next");
            _finish = ui.Q<Button>("btn-eval-finish");
            _confirm = ui.Q<Button>("btn-eval-confirm");
            _showLeft = ui.Q<Button>("btn-eval-show-left");
            _showRight = ui.Q<Button>("btn-eval-show-right");
            _choices[Left] = ui.Q<Button>("eval-choice-left");
            _choices[Right] = ui.Q<Button>("eval-choice-right");
            _choices[PlanningSession.ChoiceTie] = ui.Q<Button>("eval-choice-tie");
            _choices[PlanningSession.ChoiceSkip] = ui.Q<Button>("eval-choice-skip");

            ui.Q<Label>("eval-banner").text = Banner;
            foreach (KeyValuePair<string, Button> pair in _choices)
            {
                string side = pair.Key;
                pair.Value.clicked += () => Choose(side);
            }
            _startShort.clicked += () => StartPlan(ShortScenarios, ShortPairs, ShortRepeats);
            _startFull.clicked += () => StartPlan(FullScenarios, FullPairs, FullRepeats);
            _next.clicked += PrepareNext;
            _finish.clicked += Finish;
            _confirm.clicked += Confirm;
            _showLeft.clicked += () => Preview(true);
            _showRight.clicked += () => Preview(false);
            ui.Q<Button>("btn-eval-refresh").clicked += Refresh;
            ui.Q<Button>("btn-eval-close").clicked += () => ui.CloseOverlay(_screen);
        }

        public void Open()
        {
            _ui.OpenOverlay(_screen);
            Refresh();
        }

        /// <summary>Re-shows the screen after a 3D preview without losing the pair or the selected choice.</summary>
        public void Reopen()
        {
            _ui.OpenOverlay(_screen);
            Render();
        }

        public void OnHealthChanged()
        {
            if (IsOpen && !IsBusy && (_app.Health != null && _app.Health.EvaluationOpen) != (Plan != null)) Refresh();
        }

        // ---- Loading ----

        public void Refresh()
        {
            int generation = ++_generation;
            LastError = null;
            if (!_app.Learning.Available)
            {
                LastOverview = null;
                Plan = null;
                Render();
                return;
            }
            _busy++;
            Render();
            _app.Learning.FetchEvaluation(r =>
            {
                _busy--;
                if (generation != _generation) return;
                if (!r.Ok)
                {
                    Fail(r.Code, r.Message);
                    Render();
                    return;
                }
                LastOverview = r.Body;
                Plan = r.Body.HasOpenPlan ? r.Body.open_plan : null;
                var reserved = new List<string>();
                if (Plan != null)
                {
                    foreach (EvaluationScenarioDto s in Plan.evaluation_scenarios) reserved.Add(s.scenario_id);
                    foreach (EvaluationTrialDto t in Plan.trials)
                        if (!_sealsSeen.ContainsKey(t.trial_id)) _sealsSeen[t.trial_id] = t.sealed_sha256;
                }
                _app.SetReservedScenarios(reserved);
                if (Plan != null && Plan.next != null && Plan.next.action == "answer" && (CurrentTrial == null || CurrentTrial.trial_id != Plan.next.trial_id))
                    ResumeAnswer(Plan.next);
                if (Plan == null) LoadLatestReport();
                Render();
            });
        }

        private void LoadLatestReport()
        {
            if (LastOverview == null) return;
            EvaluationPlanSummaryDto latest = null;
            foreach (EvaluationPlanSummaryDto p in LastOverview.plans)
                if (p.has_report) { latest = p; break; }
            if (latest == null || (LastReport != null && LastReport.plan_id == latest.plan_id)) return;
            FetchReport(latest.plan_id);
        }

        private void FetchReport(string planId)
        {
            _busy++;
            _app.Learning.FetchReport(planId, r =>
            {
                _busy--;
                if (r.Ok) LastReport = r.Body; else Fail(r.Code, r.Message);
                Render();
            });
        }

        private void Fail(string code, string message)
        {
            LastError = $"{code}: {message}";
            _ui.ShowMessage($"Blinded test: {message} ({code})", true);
        }

        // ---- Starting a test ----

        public void StartPlan(int scenarios, int pairsPerScenario, int repeats)
        {
            if (IsBusy || Plan != null) return;
            if (!_app.Learning.BackendHasModel) { Fail("no_model", "Train a model in the Learning Lab first."); Render(); return; }
            _busy++;
            _status.text = "Preparing the test...";
            _app.Learning.FetchCurrent(current =>
            {
                if (!current.Ok || current.Body.model == null || !current.Body.has_model)
                {
                    _busy--;
                    Fail(current.Code ?? "no_model", current.Message ?? "No trained model is available to freeze.");
                    Render();
                    return;
                }
                var excluded = new HashSet<string>(current.Body.model.training_scenario_ids);
                try { foreach (PreferenceDto p in _app.Store.LoadAllPreferences()) excluded.Add(p.scenario_id); }
                catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException) { }
                if (_app.Session != null) excluded.Add(_app.Session.Baseline.ScenarioId);
                SubmitPlan(current.Body.model.model_version, scenarios, pairsPerScenario, repeats, excluded, 0);
            });
        }

        private void SubmitPlan(string modelVersion, int scenarios, int pairs, int repeats, HashSet<string> excluded, int attempt)
        {
            List<int> seeds;
            try { seeds = EvaluationProtocol.DrawUnseenSeeds(scenarios, excluded, new Random()); }
            catch (InvalidOperationException e)
            {
                _busy--;
                Fail("no_unseen_scenarios", e.Message);
                Render();
                return;
            }
            var request = new EvaluationPlanRequestDto
            {
                plan_id = "eval-" + Guid.NewGuid().ToString("N"),
                model_version = modelVersion,
                pairs_per_scenario = pairs,
                repeat_count = repeats,
                pair_rule = EvaluationPairPicker.PairRule,
                note = $"{scenarios} new areas x {pairs} pairs + {repeats} repeated pairs, started from the Unity app",
                created_utc = DateTime.UtcNow.ToString("o"),
            };
            foreach (int seed in seeds)
                request.evaluation_scenarios.Add(new EvaluationScenarioDto { scenario_id = GreenSpaceScenarioGenerator.ScenarioIdFor(seed), seed = seed });
            _app.Learning.CreatePlan(request, r =>
            {
                if (!r.Ok && r.Code == "scenario_not_unseen" && attempt < 2)
                {
                    foreach (EvaluationScenarioDto s in request.evaluation_scenarios) excluded.Add(s.scenario_id);
                    SubmitPlan(modelVersion, scenarios, pairs, repeats, excluded, attempt + 1);
                    return;
                }
                _busy--;
                if (!r.Ok) { Fail(r.Code, r.Message); Render(); return; }
                Plan = r.Body.plan;
                LastReport = null;
                Reveals.Clear();
                SealsVerified = SealFailures = 0;
                _ui.ShowMessage($"Blinded test started with frozen model {Plan.model_version}. Training is paused until you finish.", false);
                _app.Health?.RefreshNow();
                Refresh();
            });
        }

        // ---- Pairs ----

        /// <summary>Builds and registers the next pre-registered pair; the backend seals its prediction.</summary>
        public void PrepareNext()
        {
            if (IsBusy || Plan == null || Plan.next == null) return;
            EvaluationNextDto n = Plan.next;
            if (n.action != "new_pair" && n.action != "repeat") return;
            if (Plan.pair_rule != EvaluationPairPicker.PairRule)
            {
                Fail("pair_rule_mismatch", $"This test was planned with pair rule {Plan.pair_rule}; this build uses {EvaluationPairPicker.PairRule}.");
                Render();
                return;
            }
            CurrentTrial = null;
            _answered = false;
            _selected = null;
            _result.text = string.Empty;
            if (!_app.EnsureScenario(n.seed, out string error)) { Fail("scenario_failed", error); Render(); return; }
            if (_app.Session.Baseline.ScenarioId != n.scenario_id)
            {
                Fail("scenario_mismatch", $"Seed {n.seed} produced {_app.Session.Baseline.ScenarioId}, not {n.scenario_id}.");
                Render();
                return;
            }

            string message;
            bool loaded;
            if (n.action == "new_pair")
            {
                CandidateSet set = _app.GenerateCandidates(n.candidate_seed);
                EvaluationPair pair = EvaluationPairPicker.Pick(set, n.candidate_seed);
                if (pair == null)
                {
                    Fail("no_pair", "This area produced no two different feasible designs. Stop the test early; it will be reported honestly.");
                    Render();
                    return;
                }
                loaded = _app.LoadEvaluationPair(pair, n.candidate_seed, out message);
            }
            else loaded = _app.RestoreEvaluationPair(n.alternative_a_id, n.alternative_b_id, out message);
            if (!loaded) { Fail("pair_unavailable", message); Render(); return; }

            string scenarioId = n.scenario_id, aId = _app.Session.AlternativeA.AlternativeId, bId = _app.Session.AlternativeB.AlternativeId;
            var request = new EvaluationTrialRequestDto
            {
                trial_id = "trial-" + Guid.NewGuid().ToString("N"),
                plan_id = Plan.plan_id,
                trial_index = n.trial_index,
                scenario_id = scenarioId,
                alternative_a_id = aId,
                alternative_b_id = bId,
                repeat_of_trial_id = n.repeat_of_trial_id ?? string.Empty,
                candidate_seed = n.candidate_seed,
                pair_rule = Plan.pair_rule,
            };
            _busy++;
            _status.text = "Sending the pair to the local backend, which seals the model's prediction...";
            _app.Learning.WhenReady(
                () => _app.IsDelivered(OutboxKinds.Scenario, scenarioId) && _app.IsDelivered(OutboxKinds.Alternative, aId, bId),
                LearningLabView.SyncWaitSeconds,
                ready =>
                {
                    string refused = _app.RejectionReason(OutboxKinds.Alternative, aId) ?? _app.RejectionReason(OutboxKinds.Alternative, bId);
                    if (!ready || refused != null)
                    {
                        _busy--;
                        Fail(refused != null ? "alternative_refused" : "not_synced",
                            refused ?? "The pair has not reached the backend yet. Try again in a moment.");
                        Render();
                        return;
                    }
                    _app.Learning.RegisterTrial(request, r =>
                    {
                        _busy--;
                        if (!r.Ok) { Fail(r.Code, r.Message); Refresh(); return; }
                        ShowTrial(r.Body.trial);
                        Refresh();
                    });
                });
        }

        private void ResumeAnswer(EvaluationNextDto n)
        {
            EvaluationTrialDto trial = Plan.trials.Find(t => t.trial_id == n.trial_id);
            if (trial == null) return;
            if (!_app.EnsureScenario(n.seed, out string error)) { Fail("scenario_failed", error); return; }
            if (!_app.RestoreEvaluationPair(trial.alternative_a_id, trial.alternative_b_id, out string message)) { Fail("pair_unavailable", message); return; }
            ShowTrial(trial);
        }

        private void ShowTrial(EvaluationTrialDto trial)
        {
            CurrentTrial = trial;
            _sealsSeen[trial.trial_id] = trial.sealed_sha256;
            _answered = !string.IsNullOrEmpty(trial.comparison_id);
            _selected = null;
            _result.text = _answered ? "Your choice for this pair is already recorded." : string.Empty;
        }

        public void Choose(string side)
        {
            if (CurrentTrial == null || _answered || IsBusy) return;
            _selected = side;
            Render();
        }

        public void Confirm()
        {
            EvaluationTrialDto trial = CurrentTrial;
            if (trial == null || _answered || _selected == null || IsBusy) return;
            string choice = _selected == Left ? (LeftIsA ? PlanningSession.ChoiceA : PlanningSession.ChoiceB)
                : _selected == Right ? (LeftIsA ? PlanningSession.ChoiceB : PlanningSession.ChoiceA)
                : _selected;
            if (!_comparisonIds.TryGetValue(trial.trial_id, out string comparisonId))
                _comparisonIds[trial.trial_id] = comparisonId = _app.Session.NewComparisonId();
            StoreResult saved;
            try { saved = _app.SubmitEvaluationPreference(comparisonId, choice, trial, Reason); }
            catch (Exception e) when (e is InvalidOperationException || e is ArgumentException)
            {
                Fail("choice_invalid", e.Message);
                Render();
                return;
            }
            if (!saved.Saved && !saved.Duplicate) { Fail("choice_not_saved", saved.Message); Render(); return; }
            _answered = true;
            _busy++;
            _result.text = "Saving your choice...";
            Render();
            _app.Learning.WhenReady(() => _app.IsDelivered(OutboxKinds.Preference, comparisonId), LearningLabView.SyncWaitSeconds, ready =>
            {
                _busy--;
                string refused = _app.RejectionReason(OutboxKinds.Preference, comparisonId);
                if (refused != null) { _result.text = "The backend refused this choice: " + refused; Fail("choice_refused", refused); }
                else if (!ready) _result.text = "Saved on this computer; it will reach the backend shortly. Press Refresh before continuing.";
                else _result.text = "Choice recorded. The sealed prediction stays hidden until the end, so it cannot influence your next choices.";
                Refresh();
            });
        }

        private void Preview(bool left)
        {
            if (CurrentTrial == null) return;
            bool showA = left == LeftIsA;
            _ui.BeginPreviewFromEvaluation(showA ? AlternativeSlot.A : AlternativeSlot.B, left ? "left design" : "right design");
        }

        // ---- Finishing ----

        /// <summary>Opens every sealed prediction (checking each seal), then closes the plan so the backend stores the report.</summary>
        public void Finish()
        {
            if (Plan == null || IsBusy) return;
            bool complete = Plan.next != null && Plan.next.action == "complete";
            if (!complete && !_closeArmed)
            {
                _closeArmed = true;
                Render();
                return;
            }
            _closeArmed = false;
            string planId = Plan.plan_id;
            _busy++;
            _status.text = "Opening the sealed predictions...";
            _app.Learning.FetchPlan(planId, r =>
            {
                if (!r.Ok) { _busy--; Fail(r.Code, r.Message); Render(); return; }
                var answered = new List<EvaluationTrialDto>();
                foreach (EvaluationTrialDto t in r.Body.trials)
                {
                    if (!_sealsSeen.ContainsKey(t.trial_id)) _sealsSeen[t.trial_id] = t.sealed_sha256;
                    if (!string.IsNullOrEmpty(t.comparison_id)) answered.Add(t);
                }
                Reveals.Clear();
                SealsVerified = SealFailures = 0;
                RevealNext(answered, 0, () => Close(planId, complete ? "completed" : "stopped early by the participant"));
            });
        }

        private void RevealNext(List<EvaluationTrialDto> trials, int index, Action done)
        {
            if (index >= trials.Count) { done(); return; }
            EvaluationTrialDto t = trials[index];
            _app.Learning.Reveal(t.trial_id, r =>
            {
                if (!r.Ok) { _busy--; Fail(r.Code, r.Message); Render(); return; }
                Reveals.Add(r.Body);
                if (EvaluationProtocol.VerifySeal(r.Body.sealed_payload, _sealsSeen[t.trial_id], t.trial_id, t.alternative_a_id, t.alternative_b_id))
                    SealsVerified++;
                else SealFailures++;
                RevealNext(trials, index + 1, done);
            });
        }

        private void Close(string planId, string reason)
        {
            _status.text = "Scoring the frozen model...";
            _app.Learning.ClosePlan(planId, reason, r =>
            {
                if (!r.Ok) { _busy--; Fail(r.Code, r.Message); Refresh(); return; }
                _app.Learning.FetchReport(planId, report =>
                {
                    _busy--;
                    if (report.Ok) LastReport = report.Body; else Fail(report.Code, report.Message);
                    CurrentTrial = null;
                    Plan = null;
                    _app.SetReservedScenarios(null);
                    _app.Health?.RefreshNow();
                    _ui.ShowMessage("Blinded test finished. Training is available again.", false);
                    Refresh();
                });
            });
        }

        // ---- Rendering ----

        private void Render()
        {
            bool available = _app.Learning.Available;
            bool hasModel = _app.Learning.BackendHasModel;
            Toggle(_startRow, available && Plan == null);
            _startShort.SetEnabled(available && hasModel && Plan == null && !IsBusy);
            _startFull.SetEnabled(available && hasModel && Plan == null && !IsBusy);

            if (!available)
                _status.text = "The blinded test needs the local backend. " + _app.Learning.UnavailableReason;
            else if (IsBusy && LastOverview == null)
                _status.text = "Checking the local backend...";
            else if (Plan != null)
                _status.text = $"Test running on frozen model {Plan.model_version}. Training is paused until you finish.";
            else if (!hasModel)
                _status.text = "Train a model in the Learning Lab first: the test needs a model to freeze and check.";
            else
                _status.text = $"No test running. Your model {_app.Health.ActiveModelVersion} can be tested on new areas.";
            if (!string.IsNullOrEmpty(LastError) && !IsBusy) _status.text += "\nLast problem: " + LastError;

            _explain.text = Plan == null ? HowItWorks
                : "New areas in this test: " + Areas(Plan) + ". The backend drew the left/right order and the repeated pairs; " +
                  "this app builds each pair with a fixed rule that never looks at the model.";

            RenderProgress();
            RenderPair();
            RenderReveals();
            RenderReport();
            RenderHistory();
        }

        private void RenderProgress()
        {
            if (Plan == null || Plan.progress == null)
            {
                _progress.text = string.Empty;
                Hide(_next);
                Hide(_finish);
                return;
            }
            EvaluationProgressDto p = Plan.progress;
            string action = Plan.next != null ? Plan.next.action : string.Empty;
            _progress.text = $"Answered {p.Answered} of {p.Planned} choices ({p.answered_primary} of {p.planned_primary} new pairs, " +
                             $"{p.answered_repeats} of {p.planned_repeats} repeated pairs).";
            bool canNext = (action == "new_pair" || action == "repeat") && (CurrentTrial == null || _answered);
            Toggle(_next, canNext);
            _next.SetEnabled(!IsBusy);
            _next.text = p.Answered == 0 && CurrentTrial == null ? "Show the first pair" : "Show the next pair";
            Show(_finish);
            _finish.SetEnabled(!IsBusy && (CurrentTrial == null || _answered));
            _finish.text = action == "complete" ? "Finish and see the report"
                : _closeArmed ? "Click again to stop early (unanswered pairs are reported as unanswered)"
                : "Stop the test early";
            _finish.EnableInClassList("primary-button", action == "complete");
            _finish.EnableInClassList("secondary-button", action != "complete");
        }

        private void RenderPair()
        {
            EvaluationTrialDto t = CurrentTrial;
            PlanningSession s = _app.Session;
            bool show = t != null && Plan != null && s != null && s.AlternativeA != null && s.AlternativeB != null
                        && s.AlternativeA.AlternativeId == t.alternative_a_id && s.AlternativeB.AlternativeId == t.alternative_b_id;
            Toggle(_pair, show);
            if (!show) return;
            AlternativeSnapshot left = LeftIsA ? s.AlternativeA : s.AlternativeB, right = LeftIsA ? s.AlternativeB : s.AlternativeA;
            int planned = Plan.progress != null ? Plan.progress.Planned : 0;
            _pairTitle.text = $"Pair {t.trial_index + 1} of {planned}";
            _seal.text = $"Sealed prediction {Short(t.sealed_sha256)}: computed by the frozen model before you choose, opened only at the end.";
            _table.Clear();
            CityMetrics b = s.BaselineMetrics, l = left.Metrics, r = right.Metrics;
            Row(_table, true, "Indicator", "Today", "Left", "Right");
            Row(_table, false, "Homes within a 400 m walk of a park", UiFormat.Access(b), UiFormat.Access(l), UiFormat.Access(r));
            Row(_table, false, "Homes (housing capacity)", b.HousingUnits.ToString(Inv), l.HousingUnits.ToString(Inv), r.HousingUnits.ToString(Inv));
            Row(_table, false, "Park share of land", UiFormat.Pct(b.ParkSharePct), UiFormat.Pct(l.ParkSharePct), UiFormat.Pct(r.ParkSharePct));
            Row(_table, false, "Cost (credits)", "0", l.SpentCredits.ToString(Inv), r.SpentCredits.ToString(Inv));
            Row(_table, false, "Existing homes removed", "0", l.LostOriginalHousingUnits.ToString(Inv), r.LostOriginalHousingUnits.ToString(Inv));
            Row(_table, false, "Parcels changed", "0", l.ChangedCells.ToString(Inv), r.ChangedCells.ToString(Inv));
            foreach (KeyValuePair<string, Button> pair in _choices)
            {
                pair.Value.EnableInClassList("choice-button--selected", pair.Key == _selected);
                pair.Value.SetEnabled(!_answered && !IsBusy);
            }
            _confirm.SetEnabled(!_answered && _selected != null && !IsBusy);
            _showLeft.SetEnabled(!IsBusy);
            _showRight.SetEnabled(!IsBusy);
        }

        private void RenderReveals()
        {
            if (Reveals.Count == 0)
            {
                _reveal.text = string.Empty;
                Hide(_reveal);
                return;
            }
            var lines = new List<string>
            {
                SealFailures == 0
                    ? $"{Reveals.Count} sealed prediction(s) opened. All {SealsVerified} seals checked: each opened record has exactly the SHA-256 fingerprint shown before your choice."
                    : $"WARNING: {SealFailures} of {Reveals.Count} opened prediction(s) did not match the seal shown before the choice.",
            };
            foreach (EvaluationRevealDto r in Reveals)
            {
                string you = string.IsNullOrEmpty(r.chosen_side) ? r.choice : r.chosen_side;
                string model = r.favoured == "neither" ? "no preference (50%)"
                    : $"{r.favoured_side} ({Probability(Math.Max(r.p_a, r.p_b))})";
                string verdict = !r.Scored ? "not scored" : r.Matched ? "match" : "no match";
                lines.Add($"Pair {r.trial_index + 1}{(r.kind == "repeat" ? " (repeat)" : "")}: you chose {you}; the model favoured {model}: {verdict}.");
            }
            _reveal.text = string.Join("\n", lines);
            Show(_reveal);
        }

        private void RenderReport()
        {
            EvaluationReportResponseDto response = LastReport;
            EvaluationReportDto rep = response?.report;
            bool show = Plan == null && rep != null && rep.metrics != null && rep.metrics.model != null;
            Toggle(_reportTitle, show);
            Toggle(_reportTable, show);
            _reportTable.Clear();
            if (!show)
            {
                _reportSummary.text = string.Empty;
                _reportNotes.text = string.Empty;
                return;
            }
            _reportTitle.text = $"Test report ({response.plan_id})";
            _reportSummary.text = rep.summary;
            Row(_reportTable, true, "Predictor", "Scored", "Accuracy", "95% interval");
            EvaluationModelScoreDto m = rep.metrics.model;
            Row(_reportTable, false, "Your frozen model " + m.model_version, m.n.ToString(Inv), Pct(m.accuracy), Interval(m.accuracy_ci));
            foreach (EvaluationBaselineScoreDto b in rep.metrics.baselines)
                Row(_reportTable, false, "Rule of thumb: " + b.label, b.n.ToString(Inv), Pct(b.accuracy), Interval(b.accuracy_ci));

            EvaluationCountsDto c = rep.counts;
            var notes = new List<string>
            {
                $"Choices: {c.answered} answered of {c.registered_trials} shown ({c.scored} scored new pairs; {c.primary_ties} ties and " +
                $"{c.primary_skips} skips are not scored; {c.unanswered} unanswered). Model quality: Brier score {m.brier.ToString("0.000", Inv)} " +
                $"and log loss {m.log_loss.ToString("0.000", Inv)} (lower is better; always guessing 50% gives 0.250 and 0.693).",
            };
            EvaluationPositionDto pos = rep.position;
            if (pos != null && pos.n > 0)
                notes.Add($"Screen side: you chose the left design {pos.chose_left} of {pos.n} times (binomial p = {pos.binomial_p_value.ToString("0.00", Inv)}; " +
                          "a small p would suggest a habit of picking one side).");
            EvaluationConsistencyDto rc = rep.repeat_consistency;
            if (rc != null)
                notes.Add(rc.scored_pairs > 0
                    ? $"Consistency: on {rc.scored_pairs} repeated pair(s) you made the same choice {rc.agreements} time(s)" +
                      (rc.rate_ci != null && rc.rate_ci.Count == 2 ? $" (95% interval {Interval(rc.rate_ci)})." : ".") +
                      " No model can predict choices more reliably than you repeat them."
                    : "Consistency: no repeated pair was answered with Left/Right both times.");
            if (rep.held_out_guarantee != null)
                notes.Add($"Held-out check: {rep.held_out_guarantee.evaluation_choices_in_training} test choice(s) and " +
                          $"{rep.held_out_guarantee.evaluation_scenarios_in_training.Count} test area(s) are in the model's training data (both should be 0).");
            if (rep.bootstrap != null) notes.Add("Intervals: " + rep.bootstrap.note);
            if (response.verification != null)
                notes.Add(response.verification.reproducible
                    ? $"Report verified: stored and recomputed SHA-256 are identical ({Short(response.report_sha256)})."
                    : "WARNING: the stored report could not be reproduced from the stored data.");
            foreach (string w in rep.warnings) notes.Add("Warning: " + w);
            foreach (string cv in rep.caveats) notes.Add(cv);
            notes.Add("Figures for a write-up: in the backend folder run  .venv\\Scripts\\python.exe -m tools.make_figures --api http://127.0.0.1:8765  " +
                      "(see docs/runbook.md). Every figure is drawn from these stored records, with its data saved as CSV.");
            _reportNotes.text = "- " + string.Join("\n- ", notes);
        }

        private void RenderHistory()
        {
            _history.Clear();
            if (LastOverview == null || LastOverview.count == 0)
            {
                _historyNote.text = _app.Learning.Available ? "No blinded test has been run yet." : string.Empty;
                Hide(_history);
                return;
            }
            _historyNote.text = $"{LastOverview.count} test(s), newest first. Each keeps its frozen model, seeds and report.";
            Row(_history, true, "Test", "Status", "Scored", "Model accuracy");
            foreach (EvaluationPlanSummaryDto p in LastOverview.plans)
                Row(_history, false, p.plan_id + " (" + p.model_version + ")", p.status, p.scored.ToString(Inv),
                    p.has_report && p.scored > 0 ? Pct(p.accuracy) : "-");
            Show(_history);
        }

        // ---- Helpers ----

        private static string Areas(EvaluationPlanDto plan)
        {
            var parts = new List<string>();
            foreach (EvaluationScenarioDto s in plan.evaluation_scenarios) parts.Add("seed " + s.seed.ToString(Inv));
            return string.Join(", ", parts);
        }

        private static string Short(string sha) => string.IsNullOrEmpty(sha) ? "-" : sha.Substring(0, Math.Min(12, sha.Length));
        private static string Pct(double fraction) => (fraction * 100).ToString("0.0", Inv) + "%";
        /// <summary>A logistic model never gives exactly 0 or 1, so rounded extremes are shown as bounds.</summary>
        private static string Probability(double v) => v < 0.005 ? "<1%" : v > 0.995 ? ">99%" : (v * 100).ToString("0", Inv) + "%";
        private static string Interval(List<double> ci) =>
            ci != null && ci.Count == 2 ? $"{Pct(ci[0])} to {Pct(ci[1])}" : "too few areas";

        private static void Show(VisualElement e) => e.RemoveFromClassList("hidden");
        private static void Hide(VisualElement e) => e.AddToClassList("hidden");
        private static void Toggle(VisualElement e, bool visible) => e.EnableInClassList("hidden", !visible);

        private static void Row(VisualElement table, bool header, string name, string a, string b, string c)
        {
            var row = new VisualElement();
            row.AddToClassList("ctable-row");
            if (header) row.AddToClassList("ctable-header");
            row.Add(UiFormat.Text(name, "ltable-name"));
            row.Add(UiFormat.Text(a, "ltable-num"));
            row.Add(UiFormat.Text(b, "ltable-num"));
            row.Add(UiFormat.Text(c, "ltable-text"));
            table.Add(row);
        }
    }
}
