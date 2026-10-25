using System;
using System.Collections;
using System.IO;
using NUnit.Framework;
using PlannerDNA.App;
using PlannerDNA.Commands;
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
    /// Learning Lab, compare-screen prediction and suggestions on the real scene. Offline tests use a
    /// dead port; live tests run only against a throwaway backend (PLANNERDNA_TEST_BACKEND_URL) and use
    /// synthetic test-fixture labels, never real user choices.
    /// </summary>
    public class LearningPlayModeTests
    {
        private string _root;
        private PlannerApp _app;

        private IEnumerator LoadScene(string backendUrl)
        {
            if (_root == null) _root = Path.Combine(Path.GetTempPath(), "PlannerDNA_LearnTests_" + Guid.NewGuid().ToString("N"));
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
            PlannerApp.StoreRootOverride = null;
            PlannerApp.BackendUrlOverride = null;
            AppScene.DestroyIfLoaded();
            yield return null;
            if (_root != null && Directory.Exists(_root)) Directory.Delete(_root, true);
            _root = null;
        }

        private static IEnumerator WaitFor(Func<bool> condition, float seconds, Func<string> message) =>
            SyntheticComparisons.WaitFor(condition, seconds, message);

        [UnityTest, Timeout(60000)]
        public IEnumerator Offline_LearningLab_ShowsNoModelNoWeightsAndNoPrediction()
        {
            yield return LoadScene(TestBackend.DeadPortUrl);
            yield return WaitFor(() => _app.Health.Status == BackendStatus.Offline, 8f, () => "health check did not resolve");
            Assert.IsTrue(_app.Ui.Home.Start(42));
            yield return null;

            StringAssert.Contains("Model status unknown", _app.Ui.Q<Label>("learning-status").text);
            Assert.IsFalse(_app.Health.HasModel);

            _app.Ui.OpenLearningLab();
            LearningLabView lab = _app.Ui.Learning;
            Assert.IsTrue(lab.IsOpen);
            StringAssert.StartsWith("Training samples: unknown", lab.SamplesText);
            Assert.IsFalse(lab.TrainEnabled, "cannot train without the backend");
            StringAssert.Contains("offline", lab.TrainStatusText);
            Assert.IsFalse(lab.SuggestEnabled);
            Assert.AreEqual(0, lab.WeightRows, "no weights are ever shown without a fitted model");
            Assert.IsTrue(_app.Ui.Q<VisualElement>("learning-weights").ClassListContains("hidden"));
            StringAssert.Contains("Synthetic scenarios only", _app.Ui.Q<Label>("learning-banner").text);

            _app.Ui.CloseTopOverlay();
            CellData vacant = default;
            foreach (CellData c in _app.Session.Current.Cells) if (c.Use == LandUse.Vacant) { vacant = c; break; }
            _app.Select(vacant.Row, vacant.Col);
            _app.ApplyTool(EditAction.SetPark, InputSource.Mouse);
            _app.SaveAlternative(AlternativeSlot.A);
            _app.ResetToBaseline(InputSource.Mouse);
            _app.Select(vacant.Row, vacant.Col);
            _app.ApplyTool(EditAction.SetResidential, InputSource.Mouse);
            _app.SaveAlternative(AlternativeSlot.B);
            _app.Ui.OpenCompare();
            _app.Ui.Compare.SelectForTest("A", null);
            _app.Ui.Compare.Confirm();
            yield return null;
            Assert.IsNull(_app.Ui.Compare.LastPrediction);
            StringAssert.Contains("not available while the local backend is offline", _app.Ui.Compare.PredictionText);
        }

        [UnityTest, Timeout(240000)]
        public IEnumerator Live_TrainOnSyntheticLabels_PredictAfterChoice_AndRankFeasibleSuggestions()
        {
            if (!TestBackend.Configured) Assert.Ignore(TestBackend.IgnoreReason);
            string url = TestBackend.Url.TrimEnd('/');
            yield return LoadScene(url);
            yield return WaitFor(() => _app.Health.StorageReady, 12f, () => "throwaway backend not ready: " + _app.Health.Detail);

            // 1. Synthetic fixture labels across three scenarios, delivered to the backend.
            yield return SyntheticComparisons.Record(_app, new[] { 301, 302, 303 }, 5);
            Assert.GreaterOrEqual(SyntheticComparisons.Recorded, 10);
            yield return WaitFor(() => _app.Outbox.PendingCount == 0, 60f, () => $"{_app.Outbox.PendingCount} pending: {_app.Sync.LastError}");
            Assert.AreEqual(0, _app.Outbox.RejectedCount, "no record refused");

            // 2. Learning Lab readiness and explicit training.
            _app.Ui.OpenLearningLab();
            LearningLabView lab = _app.Ui.Learning;
            yield return WaitFor(() => !lab.IsBusy && lab.LastReadiness != null, 15f, () => "readiness not loaded: " + lab.LastError);
            StringAssert.StartsWith("Training samples: ", lab.SamplesText);
            StringAssert.Contains("unique direct comparisons across", lab.SamplesText);
            Assert.IsTrue(lab.LastReadiness.can_train, lab.LastReadiness.message);
            Assert.IsTrue(lab.TrainEnabled, lab.TrainStatusText);

            lab.Train();
            yield return WaitFor(() => !lab.IsBusy && lab.LastCurrent != null && lab.LastCurrent.has_model, 90f,
                () => "training did not finish: " + lab.TrainStatusText + " " + lab.LastError);
            ModelSummaryDto model = lab.LastCurrent.model;
            StringAssert.StartsWith("pm-", model.model_version);
            Assert.AreEqual(5, lab.WeightRows, "one row per feature");
            CoefficientDto access = model.coefficients.Find(k => k.feature == "delta_access_fraction");
            Assert.AreEqual("positive", access.direction, "fitted model learns the direction of the synthetic rule");
            Assert.Greater(access.weight_standardised, 0);
            StringAssert.Contains(model.model_version, lab.ModelText);
            yield return WaitFor(() => _app.Health.HasModel, 10f, () => "health did not report the model");
            StringAssert.Contains(model.model_version, _app.Ui.Q<Label>("learning-status").text);

            // 3. A new comparison: the prediction is revealed only after the choice, from the saved model.
            _app.Ui.CloseTopOverlay();
            Assert.IsTrue(_app.NewChallenge(304, out string err), err);
            var set = _app.GenerateCandidates(304);
            Assert.GreaterOrEqual(set.Candidates.Count, 2);
            var hi = set.Candidates[0];
            var lo = set.Candidates[0];
            foreach (var c in set.Candidates)
            {
                if (c.Features.delta_access_fraction > hi.Features.delta_access_fraction) hi = c;
                if (c.Features.delta_access_fraction < lo.Features.delta_access_fraction) lo = c;
            }
            Assert.AreNotSame(hi, lo);
            _app.Session.ApplyLoadedDesign(lo.State, InputSource.System, "fixture");
            _app.SaveAlternative(AlternativeSlot.A);
            _app.Session.ApplyLoadedDesign(hi.State, InputSource.System, "fixture");
            _app.SaveAlternative(AlternativeSlot.B);
            _app.Ui.OpenCompare();
            Assert.AreEqual(string.Empty, _app.Ui.Compare.PredictionText, "nothing is predicted before the choice");
            _app.Ui.Compare.SelectForTest("B", SyntheticComparisons.Reason);
            _app.Ui.Compare.Confirm();
            yield return WaitFor(() => !_app.Ui.Compare.PredictionPending, 40f, () => "prediction never resolved");
            PairPredictionDto p = _app.Ui.Compare.LastPrediction;
            Assert.IsNotNull(p, _app.Ui.Compare.PredictionText);
            Assert.AreEqual(model.model_version, p.model_version);
            Assert.AreEqual(1.0, p.p_a + p.p_b, 1e-9, "probabilities of the pair sum to 1");
            Assert.Greater(p.p_b, 0.5, "the design with more park access is favoured");
            Assert.IsFalse(p.comparison_in_training_set);
            StringAssert.Contains("Revealed after your choice", _app.Ui.Compare.PredictionText);
            _app.Ui.CloseTopOverlay();

            // 4. Suggestions: generated and filtered here, ranked by the backend model, applied as an undoable edit.
            _app.Ui.OpenLearningLab();
            yield return WaitFor(() => !lab.IsBusy && lab.SuggestEnabled, 15f, () => "suggest disabled: " + lab.SuggestStatusText);
            lab.Suggest();
            yield return WaitFor(() => !lab.IsBusy && (lab.LastRank != null || lab.LastError != null), 40f, () => "ranking: " + lab.SuggestStatusText);
            RankResponseDto rank = lab.LastRank;
            Assert.IsNotNull(rank, lab.SuggestStatusText);
            Assert.AreEqual(model.model_version, rank.model_version);
            Assert.Greater(rank.returned, 0);
            Assert.AreEqual(rank.returned, lab.SuggestionCards);
            StringAssert.StartsWith("Synthetic scenario.", rank.banner);
            StringAssert.Contains("not optimal plans", rank.banner);
            StringAssert.Contains(model.model_version, rank.banner);
            for (int i = 1; i < rank.suggestions.Count; i++)
                Assert.GreaterOrEqual(rank.suggestions[i - 1].utility, rank.suggestions[i].utility, "ordered by model score");

            _app.ResetToBaseline(InputSource.Mouse);
            string before = _app.Session.Current.ContentHash();
            Assert.IsTrue(lab.ApplyRank(1), _app.Ui.LastMessage);
            Assert.AreEqual(rank.suggestions[0].state_hash, _app.Session.Current.ContentHash());
            Assert.IsTrue(_app.Session.CurrentConstraints.Feasible, "suggestions are feasible");
            TimelineEntryDto last = _app.Session.Timeline[_app.Session.Timeline.Count - 1];
            Assert.AreEqual("assistant_generated", last.input_source);
            _app.Undo(InputSource.Keyboard);
            Assert.AreEqual(before, _app.Session.Current.ContentHash(), "undo restores the previous design");
        }
    }
}
