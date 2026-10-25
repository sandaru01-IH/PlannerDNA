using System;
using System.Collections;
using System.IO;
using NUnit.Framework;
using PlannerDNA.App;
using PlannerDNA.Core;
using PlannerDNA.Networking;
using PlannerDNA.Storage;
using PlannerDNA.UI;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace PlannerDNA.Tests
{
    /// <summary>
    /// Phase 4 blinded test on the real scene. Offline uses a dead port; the live test runs only against a
    /// throwaway backend (PLANNERDNA_TEST_BACKEND_URL) and answers with a fixed SYNTHETIC rule (prefer more
    /// park access, then lower cost), never with real user choices.
    /// </summary>
    public class EvaluationPlayModeTests
    {
        private string _root;
        private PlannerApp _app;

        private IEnumerator LoadScene(string backendUrl)
        {
            if (_root == null) _root = Path.Combine(Path.GetTempPath(), "PlannerDNA_EvalTests_" + Guid.NewGuid().ToString("N"));
            PlannerApp.StoreRootOverride = _root;
            PlannerApp.BackendUrlOverride = backendUrl;
            SceneManager.LoadScene("PlannerDNA_Main");
            yield return null;
            yield return null;
            _app = UnityEngine.Object.FindAnyObjectByType<PlannerApp>();
            Assert.IsNotNull(_app, "PlannerApp missing from scene");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            // A failed live run must not leave a plan open on the throwaway backend (it would pause training for later tests).
            if (_app != null && _app.Learning != null && _app.Learning.Available)
            {
                bool done = false;
                _app.Learning.FetchEvaluation(r =>
                {
                    if (r.Ok && r.Body.HasOpenPlan) _app.Learning.ClosePlan(r.Body.open_plan_id, "test teardown", _ => done = true);
                    else done = true;
                });
                float until = Time.realtimeSinceStartup + 15f;
                while (!done && Time.realtimeSinceStartup < until) yield return null;
            }
            PlannerApp.StoreRootOverride = null;
            PlannerApp.BackendUrlOverride = null;
            AppScene.DestroyIfLoaded();
            yield return null;
            if (_root != null && Directory.Exists(_root)) Directory.Delete(_root, true);
            _root = null;
            _app = null;
        }

        private static IEnumerator WaitFor(Func<bool> condition, float seconds, Func<string> message) =>
            SyntheticComparisons.WaitFor(condition, seconds, message);

        [UnityTest, Timeout(60000)]
        public IEnumerator Offline_BlindedTest_ExplainsItNeedsTheBackendAndCannotStart()
        {
            yield return LoadScene(TestBackend.DeadPortUrl);
            yield return WaitFor(() => _app.Health.Status == BackendStatus.Offline, 8f, () => "health check did not resolve");
            Assert.IsTrue(_app.Ui.Home.Start(42));
            yield return null;
            _app.Ui.OpenLearningLab();
            StringAssert.Contains("blinded test", _app.Ui.Q<Label>("learning-eval-note").text);
            _app.Ui.OpenEvaluation();
            EvaluationView view = _app.Ui.Evaluation;
            Assert.IsTrue(view.IsOpen);
            StringAssert.Contains("needs the local backend", view.StatusText);
            Assert.IsFalse(view.StartEnabled, "no test can start without a backend and a model");
            Assert.IsNull(view.Plan);
            Assert.IsTrue(_app.Ui.Q<VisualElement>("eval-pair").ClassListContains("hidden"), "no pair is shown");
            StringAssert.Contains("sealed BEFORE you choose", _app.Ui.Q<Label>("eval-banner").text);
            view.StartPlan(EvaluationView.ShortScenarios, EvaluationView.ShortPairs, EvaluationView.ShortRepeats);
            Assert.IsNull(view.Plan);
            StringAssert.Contains("Train a model", view.LastError);
        }

        [UnityTest, Timeout(420000)]
        public IEnumerator Live_BlindedTest_SealsBeforeChoice_RevealsAtEnd_AndReportsReproducibly()
        {
            if (!TestBackend.Configured) Assert.Ignore(TestBackend.IgnoreReason);
            yield return LoadScene(TestBackend.Url.TrimEnd('/'));
            yield return WaitFor(() => _app.Health.StorageReady, 12f, () => "throwaway backend not ready: " + _app.Health.Detail);

            // 1. Synthetic training labels and a trained model to freeze.
            yield return SyntheticComparisons.Record(_app, new[] { 401, 402, 403 }, 5);
            yield return WaitFor(() => _app.Outbox.PendingCount == 0, 60f, () => $"{_app.Outbox.PendingCount} pending: {_app.Sync.LastError}");
            _app.Ui.OpenLearningLab();
            LearningLabView lab = _app.Ui.Learning;
            yield return WaitFor(() => !lab.IsBusy && lab.LastReadiness != null && lab.TrainEnabled, 20f, () => "cannot train: " + lab.TrainStatusText);
            lab.Train();
            yield return WaitFor(() => !lab.IsBusy && lab.LastCurrent != null && lab.LastCurrent.has_model && _app.Health.HasModel, 90f,
                () => "training did not finish: " + lab.TrainStatusText);
            string modelVersion = lab.LastCurrent.model.model_version;

            // 2. Pre-register a small test: 3 new areas x 2 pairs + 2 repeats.
            _app.Ui.OpenEvaluation();
            EvaluationView view = _app.Ui.Evaluation;
            yield return WaitFor(() => !view.IsBusy && view.StartEnabled, 15f, () => "start disabled: " + view.StatusText);
            view.StartPlan(3, 2, 2);
            yield return WaitFor(() => !view.IsBusy && view.Plan != null, 30f, () => "plan not created: " + view.LastError);
            EvaluationPlanDto plan = view.Plan;
            Assert.AreEqual(modelVersion, plan.model_version, "the active model is frozen");
            Assert.AreEqual(3, plan.evaluation_scenarios.Count);
            foreach (EvaluationScenarioDto s in plan.evaluation_scenarios)
                CollectionAssert.DoesNotContain(lab.LastCurrent.model.training_scenario_ids, s.scenario_id, "evaluation areas are unseen");
            yield return WaitFor(() => _app.Health.EvaluationOpen, 10f, () => "health did not report the open test");
            lab.UpdateButtons();
            Assert.IsFalse(lab.TrainEnabled, "training is paused while the test is open");
            StringAssert.Contains("blinded test is running", _app.Ui.Q<Label>("learning-eval-note").text);

            // 3. Answer every pre-registered pair. Nothing is revealed before the end.
            int answered = 0;
            for (int guard = 0; guard < 12 && view.Plan != null && view.Plan.next.action != "complete"; guard++)
            {
                yield return WaitFor(() => !view.IsBusy && view.NextVisible, 30f, () => "next pair unavailable: " + view.StatusText);
                view.PrepareNext();
                yield return WaitFor(() => !view.IsBusy && view.CurrentTrial != null && !view.CurrentAnswered, 60f,
                    () => "pair not registered: " + view.LastError);
                EvaluationTrialDto trial = view.CurrentTrial;
                Assert.AreEqual(64, trial.sealed_sha256.Length, "the seal is a SHA-256");
                Assert.IsFalse(trial.revealed);
                Assert.AreEqual(0, view.Reveals.Count, "no prediction is revealed during the test");
                Assert.IsFalse(_app.Ui.Q<VisualElement>("eval-pair").ClassListContains("hidden"));

                if (answered == 0)
                {
                    StoreResult practice = _app.SubmitPreference(_app.Session.NewComparisonId(), "A", "synthetic test fixture");
                    Assert.IsFalse(practice.Saved, "practice choices are refused on reserved areas");
                    StringAssert.Contains("reserved for the open blinded test", practice.Message);
                }

                AlternativeSnapshot left = view.LeftIsA ? _app.Session.AlternativeA : _app.Session.AlternativeB;
                AlternativeSnapshot right = view.LeftIsA ? _app.Session.AlternativeB : _app.Session.AlternativeA;
                double dl = left.Features.delta_access_fraction, dr = right.Features.delta_access_fraction;
                bool pickLeft = Math.Abs(dl - dr) > 1e-9 ? dl > dr : left.Metrics.SpentCredits <= right.Metrics.SpentCredits;
                view.Choose(pickLeft ? EvaluationView.Left : EvaluationView.Right);
                view.Confirm();
                yield return WaitFor(() => !view.IsBusy && view.CurrentAnswered && view.Plan != null &&
                                           view.Plan.progress.Answered == answered + 1, 60f,
                    () => "choice not recorded: " + view.ResultText + " " + view.LastError);
                StringAssert.Contains("stays hidden until the end", view.ResultText);
                answered++;
            }
            Assert.AreEqual(8, answered, "6 new pairs + 2 repeats");
            Assert.AreEqual("complete", view.Plan.next.action);

            // 4. Finish: every seal is opened and checked, then the backend scores and stores the report.
            view.Finish();
            yield return WaitFor(() => !view.IsBusy && view.Plan == null && view.LastReport != null, 90f, () => "report missing: " + view.LastError);
            Assert.AreEqual(8, view.Reveals.Count);
            Assert.AreEqual(8, view.SealsVerified, "each opened prediction matches the seal shown before the choice");
            Assert.AreEqual(0, view.SealFailures);
            StringAssert.Contains("All 8 seals checked", view.RevealText);

            EvaluationReportResponseDto response = view.LastReport;
            Assert.IsTrue(response.verification.reproducible, "stored report re-hashes and recomputes identically");
            EvaluationReportDto report = response.report;
            Assert.AreEqual("blinded-eval-1.0.0", report.protocol_version);
            Assert.AreEqual(modelVersion, report.model.model_version);
            Assert.AreEqual(6, report.counts.scored);
            Assert.AreEqual(8, report.counts.revealed);
            Assert.AreEqual(0, report.held_out_guarantee.evaluation_choices_in_training);
            Assert.AreEqual(0, report.held_out_guarantee.evaluation_scenarios_in_training.Count);
            Assert.AreEqual(6, report.metrics.model.n);
            Assert.AreEqual(5, report.metrics.baselines.Count, "chance, cheaper, more access, equal weights, left position");
            Assert.AreEqual(2, report.repeat_consistency.registered);
            Assert.IsTrue(report.bootstrap.available, "three scored areas allow clustered intervals");
            Assert.AreEqual(2, report.metrics.model.accuracy_ci.Count);
            StringAssert.Contains("Report verified", view.ReportNotesText);
            StringAssert.Contains("make_figures", view.ReportNotesText);
            StringAssert.StartsWith("The frozen model", report.summary);

            // 5. Training is available again and the evaluation choices are not training data.
            yield return WaitFor(() => !_app.Health.EvaluationOpen, 10f, () => "health still reports an open test");
            _app.Ui.OpenLearningLab();
            lab.Refresh();
            yield return WaitFor(() => !lab.IsBusy && lab.LastReadiness != null, 15f, () => "readiness not loaded");
            Assert.IsFalse(lab.LastReadiness.evaluation_open);
            Assert.GreaterOrEqual(lab.LastReadiness.excluded.evaluation_mode, 8, "the blinded choices are excluded from training");
            Assert.AreEqual(0, _app.Outbox.RejectedCount, "no record was refused by the backend");
        }
    }
}
