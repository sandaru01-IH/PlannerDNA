using System;
using System.Collections;
using NUnit.Framework;
using PlannerDNA.App;
using PlannerDNA.Commands;
using PlannerDNA.Core;
using PlannerDNA.Networking;
using PlannerDNA.Storage;
using PlannerDNA.Suggestions;
using PlannerDNA.UI;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace PlannerDNA.Tests
{
    /// <summary>
    /// NOT a test of correctness and never part of a normal run. When the user explicitly asks the assistant to
    /// "play the rest", this drives the real app against the user's OWN local data folder and backend: it records
    /// practice choices in new cities, trains, and answers a short blinded test. Every choice it makes follows a
    /// fixed rule and carries <see cref="Reason"/> (contains "synthetic"), so exports and figures mark them as
    /// not the user's. Runs only when PLANNERDNA_AGENT_PLAY_URL is set to a loopback backend URL.
    /// </summary>
    [Explicit("Writes to the user's real PlannerDNA data; run only on request")]
    public class AgentPlaythrough
    {
        public const string EnvVar = "PLANNERDNA_AGENT_PLAY_URL";
        public const string Reason = "synthetic agent-played (not the user): prefer more park access, then lower cost";
        private static readonly int[] PracticeSeeds = { 7, 123, 2024, 777 };
        private const int PairsPerCity = 5;

        private PlannerApp _app;

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            PlannerApp.BackendUrlOverride = null;
            AppScene.DestroyIfLoaded();
            yield return null;
            _app = null;
        }

        private static IEnumerator WaitFor(Func<bool> condition, float seconds, Func<string> message) =>
            SyntheticComparisons.WaitFor(condition, seconds, message);

        private static bool PreferFirst(double accessX, double accessY, double spentX, double spentY) =>
            Math.Abs(accessX - accessY) > 1e-9 ? accessX > accessY : spentX <= spentY;

        [UnityTest, Timeout(900000)]
        public IEnumerator PlayRestOfTheExercise()
        {
            string url = Environment.GetEnvironmentVariable(EnvVar);
            if (string.IsNullOrEmpty(url)) Assert.Ignore($"Set {EnvVar} (e.g. http://127.0.0.1:8765) to play on the user's data.");
            Assert.IsTrue(HealthMonitor.IsLoopbackUrl(url), "loopback only");

            PlannerApp.StoreRootOverride = null; // the user's real local folder
            PlannerApp.BackendUrlOverride = url.TrimEnd('/');
            SceneManager.LoadScene(AppScene.Name);
            yield return null;
            yield return null;
            _app = UnityEngine.Object.FindAnyObjectByType<PlannerApp>();
            Assert.IsNotNull(_app);
            yield return WaitFor(() => _app.Health.StorageReady, 15f, () => "backend not ready: " + _app.Health.Detail);
            yield return WaitFor(() => _app.Outbox.PendingCount == 0, 60f, () => $"{_app.Outbox.PendingCount} pending before start");
            Debug.Log($"[AgentPlay] store={_app.Store.RootDirectory} backend={_app.Health.BaseUrl}");

            // 1. Practice choices in new cities (the user's own choices are in green-42).
            int recorded = 0;
            foreach (int seed in PracticeSeeds)
            {
                Assert.IsTrue(_app.NewChallenge(seed, out string error), error);
                CandidateSet set = _app.GenerateCandidates(seed);
                int pairs = 0;
                for (int i = 0; i + 1 < set.Candidates.Count && pairs < PairsPerCity; i += 2)
                {
                    CandidateDesign x = set.Candidates[i], y = set.Candidates[i + 1];
                    if (x.State.ContentEquals(y.State)) continue;
                    Load(x.State);
                    _app.SaveAlternative(AlternativeSlot.A);
                    Load(y.State);
                    _app.SaveAlternative(AlternativeSlot.B);
                    AlternativeSnapshot a = _app.Session.AlternativeA, b = _app.Session.AlternativeB;
                    bool chooseA = PreferFirst(a.Features.delta_access_fraction, b.Features.delta_access_fraction,
                                               a.Metrics.SpentCredits, b.Metrics.SpentCredits);
                    StoreResult r = _app.SubmitPreference(_app.Session.NewComparisonId(), chooseA ? "A" : "B", Reason);
                    Assert.IsTrue(r.Saved, r.Message);
                    pairs++;
                    recorded++;
                }
                Debug.Log($"[AgentPlay] city green-{seed}: {pairs} choices");
                yield return null;
            }
            yield return WaitFor(() => _app.Outbox.PendingCount == 0, 120f, () => $"{_app.Outbox.PendingCount} pending: {_app.Sync.LastError}");
            Assert.AreEqual(0, _app.Outbox.RejectedCount, "backend refused a record");

            // 2. Train through the Learning Lab.
            _app.Ui.OpenLearningLab();
            LearningLabView lab = _app.Ui.Learning;
            yield return WaitFor(() => !lab.IsBusy && lab.LastReadiness != null && lab.TrainEnabled, 30f, () => "cannot train: " + lab.TrainStatusText);
            Debug.Log("[AgentPlay] readiness: " + lab.LastReadiness.message);
            lab.Train();
            yield return WaitFor(() => !lab.IsBusy && lab.LastCurrent != null && lab.LastCurrent.has_model && _app.Health.HasModel, 120f,
                () => "training did not finish: " + lab.TrainStatusText);
            Debug.Log($"[AgentPlay] trained {lab.LastCurrent.model.model_version} on {lab.LastCurrent.model.training_scenario_ids.Count} cities");

            // 3. A short blinded test, exactly as the buttons run it.
            _app.Ui.OpenEvaluation();
            EvaluationView view = _app.Ui.Evaluation;
            yield return WaitFor(() => !view.IsBusy && view.StartEnabled, 20f, () => "start disabled: " + view.StatusText);
            view.StartPlan(EvaluationView.ShortScenarios, EvaluationView.ShortPairs, EvaluationView.ShortRepeats);
            yield return WaitFor(() => !view.IsBusy && view.Plan != null, 60f, () => "plan not created: " + view.LastError);
            Debug.Log($"[AgentPlay] test {view.Plan.plan_id} on model {view.Plan.model_version}");

            view.Reason = Reason;
            int answered = 0;
            for (int guard = 0; guard < 40 && view.Plan != null && view.Plan.next.action != "complete"; guard++)
            {
                yield return WaitFor(() => !view.IsBusy && view.NextVisible, 60f, () => "next pair unavailable: " + view.StatusText);
                view.PrepareNext();
                yield return WaitFor(() => !view.IsBusy && view.CurrentTrial != null && !view.CurrentAnswered, 90f, () => "pair not registered: " + view.LastError);
                AlternativeSnapshot left = view.LeftIsA ? _app.Session.AlternativeA : _app.Session.AlternativeB;
                AlternativeSnapshot right = view.LeftIsA ? _app.Session.AlternativeB : _app.Session.AlternativeA;
                bool pickLeft = PreferFirst(left.Features.delta_access_fraction, right.Features.delta_access_fraction,
                                            left.Metrics.SpentCredits, right.Metrics.SpentCredits);
                view.Choose(pickLeft ? EvaluationView.Left : EvaluationView.Right);
                view.Confirm();
                int expected = answered + 1;
                yield return WaitFor(() => !view.IsBusy && view.CurrentAnswered && view.Plan != null && view.Plan.progress.Answered == expected, 90f,
                    () => "choice not recorded: " + view.ResultText + " " + view.LastError);
                answered++;
            }
            Assert.AreEqual("complete", view.Plan.next.action);

            view.Finish();
            yield return WaitFor(() => !view.IsBusy && view.Plan == null && view.LastReport != null, 120f, () => "report missing: " + view.LastError);
            Assert.AreEqual(0, view.SealFailures, "every opened prediction matched its seal");
            Assert.IsTrue(view.LastReport.verification.reproducible, "report reproducible");
            Debug.Log($"[AgentPlay] practice choices={recorded}, test choices={answered}, seals verified={view.SealsVerified}");
            Debug.Log("[AgentPlay] " + view.LastReport.report.summary);
            Assert.AreEqual(0, _app.Outbox.RejectedCount, "backend refused a record");
        }

        private void Load(CityState state)
        {
            if (_app.Session.Current.ContentEquals(state)) return;
            CommandOutcome outcome = _app.Session.ApplyLoadedDesign(state, InputSource.System, "agent-played design (generated candidate)");
            Assert.IsTrue(outcome.Accepted, outcome.Message);
        }
    }
}
