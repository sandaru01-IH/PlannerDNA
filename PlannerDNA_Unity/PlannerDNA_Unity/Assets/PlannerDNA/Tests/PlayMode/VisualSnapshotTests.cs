using System.Collections;
using System.IO;
using NUnit.Framework;
using PlannerDNA.App;
using PlannerDNA.Commands;
using PlannerDNA.Core;
using PlannerDNA.UI;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace PlannerDNA.Tests
{
    /// <summary>
    /// Opt-in: renders the 3D view and the UI panel into textures and writes PNGs to
    /// &lt;repo&gt;/TestResults/captures for human visual review. It asserts nothing about appearance.
    /// Run with -testFilter VisualSnapshotTests (explicit tests are skipped otherwise).
    /// </summary>
    [Explicit("Writes PNG snapshots for visual review")]
    public class VisualSnapshotTests
    {
        private const int Width = 1600, Height = 900;
        private string _storeRoot;

        private static string OutputDir =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "TestResults", "captures"));

        private static Texture2D Read(RenderTexture rt)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = previous;
            return tex;
        }

        // WaitForEndOfFrame never resumes in -batchmode, so frames are advanced with plain yields.
        private static IEnumerator Capture(PlannerApp app, string file)
        {
            Camera cam = app.Picker.TargetCamera;
            var worldRt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32);
            var uiRt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32);
            PanelSettings panel = app.Ui.GetComponent<UIDocument>().panelSettings;
            bool oldClear = panel.clearColor;
            Color oldClearValue = panel.colorClearValue;
            panel.targetTexture = uiRt;
            panel.clearColor = true;
            panel.colorClearValue = new Color(0, 0, 0, 0);
            cam.targetTexture = worldRt;
            float settle = Time.realtimeSinceStartup + 0.45f;
            while (Time.realtimeSinceStartup < settle) yield return null;
            for (int i = 0; i < 4; i++) yield return null;

            Texture2D world = Read(worldRt), ui = Read(uiRt);
            Color[] w = world.GetPixels(), u = ui.GetPixels();
            // The panel texture holds sRGB-encoded, linearly premultiplied colour; composite the same way the screen does.
            for (int i = 0; i < w.Length; i++)
            {
                Color ul = u[i].linear, wl = w[i].linear;
                float keep = 1f - u[i].a;
                w[i] = new Color(ul.r + wl.r * keep, ul.g + wl.g * keep, ul.b + wl.b * keep, 1f).gamma;
            }
            world.SetPixels(w);
            Directory.CreateDirectory(OutputDir);
            File.WriteAllBytes(Path.Combine(OutputDir, file), world.EncodeToPNG());

            cam.targetTexture = null;
            panel.targetTexture = null;
            panel.clearColor = oldClear;
            panel.colorClearValue = oldClearValue;
            worldRt.Release();
            uiRt.Release();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            PlannerApp.StoreRootOverride = null;
            PlannerApp.BackendUrlOverride = null;
            AppScene.DestroyIfLoaded();
            yield return null;
            if (_storeRoot != null && Directory.Exists(_storeRoot)) Directory.Delete(_storeRoot, true);
        }

        [UnityTest, Timeout(60000)]
        public IEnumerator CaptureDemoScreens()
        {
            _storeRoot = Path.Combine(Path.GetTempPath(), "PlannerDNA_Snapshots_" + System.Guid.NewGuid().ToString("N"));
            PlannerApp.StoreRootOverride = _storeRoot;
            PlannerApp.BackendUrlOverride = TestBackend.UrlOrDeadPort;
            SceneManager.LoadScene("PlannerDNA_Main");
            yield return null;
            yield return null;
            var app = Object.FindAnyObjectByType<PlannerApp>();
            app.Ui.SetTheme(false, false);
            yield return Capture(app, "01_home.png");

            Assert.IsTrue(app.Ui.Home.Start(42));
            yield return Capture(app, "02_city.png");

            CellData target = default;
            foreach (CellData c in app.Session.Current.Cells) if (c.Use == LandUse.Vacant) { target = c; break; }
            app.Select(target.Row, target.Col);
            app.ApplyTool(EditAction.SetPark, InputSource.Mouse);
            app.SetAccessOverlay(true);
            yield return Capture(app, "03_edit_overlay.png");

            app.SaveAlternative(AlternativeSlot.A);
            app.ResetToBaseline(InputSource.Mouse);
            foreach (CellData c in app.Session.Current.Cells) if (c.Use == LandUse.Residential && c.Floors < 8) { target = c; break; }
            app.Select(target.Row, target.Col);
            app.ApplyTool(EditAction.RaiseFloors, InputSource.Mouse);
            app.SaveAlternative(AlternativeSlot.B);
            app.Ui.OpenCompare();
            yield return Capture(app, "04_compare.png");

            app.Ui.CloseTopOverlay();
            app.Ui.Info.RefreshData();
            app.Ui.OpenOverlay(app.Ui.Q<VisualElement>("data-screen"));
            yield return Capture(app, "05_data_sync.png");

            app.Ui.Info.OpenDelete();
            yield return Capture(app, "06_delete_confirm.png");

            app.Ui.CloseTopOverlay();
            app.Ui.CloseTopOverlay();
            app.Ui.OpenLearningLab();
            yield return SyntheticComparisons.WaitFor(() => !app.Ui.Learning.IsBusy, 10f, () => "Learning Lab still loading");
            yield return Capture(app, "07_learning_lab.png");
        }

        [UnityTest, Timeout(90000)]
        public IEnumerator CaptureTourAndDarkMode()
        {
            _storeRoot = Path.Combine(Path.GetTempPath(), "PlannerDNA_Snapshots_" + System.Guid.NewGuid().ToString("N"));
            PlannerApp.StoreRootOverride = _storeRoot;
            PlannerApp.BackendUrlOverride = TestBackend.UrlOrDeadPort;
            SceneManager.LoadScene("PlannerDNA_Main");
            yield return null;
            yield return null;
            var app = Object.FindAnyObjectByType<PlannerApp>();
            app.Ui.SetTheme(false, false);

            Assert.IsTrue(app.Ui.Home.StartTour());
            yield return Capture(app, "12_tour_welcome.png");
            app.Ui.Tour.Next();
            yield return Capture(app, "13_tour_goal.png");
            app.Ui.Tour.Next();
            app.Ui.Tour.Next();
            app.SetAccessOverlay(true);
            yield return SyntheticComparisons.WaitFor(() => app.Ui.Tour.CurrentStepKey == "select", 5f, () => "tour at " + app.Ui.Tour.CurrentStepKey);
            yield return Capture(app, "14_tour_select_parcel.png");
            Assert.IsTrue(CellIds.TryParse(app.Ui.Tour.SuggestedParcelId, out int row, out int col));
            app.Select(row, col);
            yield return SyntheticComparisons.WaitFor(() => app.Ui.Tour.CurrentStepKey == "park", 5f, () => "tour at " + app.Ui.Tour.CurrentStepKey);
            yield return Capture(app, "15_tour_park_tool.png");
            app.ApplyTool(EditAction.SetPark, InputSource.Mouse);
            yield return SyntheticComparisons.WaitFor(() => app.Ui.Tour.CurrentStepKey == "metrics", 5f, () => "tour at " + app.Ui.Tour.CurrentStepKey);
            app.Ui.SetTheme(true, false);
            yield return Capture(app, "16_dark_tour_metrics.png");
            app.Ui.Tour.End();
            yield return Capture(app, "17_dark_city.png");

            app.SaveAlternative(AlternativeSlot.A);
            app.Undo(InputSource.Mouse);
            CellData target = default;
            foreach (CellData c in app.Session.Current.Cells) if (c.Use == LandUse.Residential && c.Floors < 8) { target = c; break; }
            app.Select(target.Row, target.Col);
            app.ApplyTool(EditAction.RaiseFloors, InputSource.Mouse);
            app.SaveAlternative(AlternativeSlot.B);
            app.Ui.OpenCompare();
            yield return Capture(app, "18_dark_compare.png");
            app.Ui.CloseTopOverlay();
            app.Ui.OpenOverlay(app.Ui.Q<VisualElement>("help-screen"));
            yield return Capture(app, "19_dark_help.png");
            app.Ui.CloseTopOverlay();
            app.Ui.ShowHome();
            yield return Capture(app, "20_dark_home.png");
            app.Ui.SetTheme(false, false);
        }

        /// <summary>Needs a throwaway backend: trains on synthetic fixture labels and captures the trained views.</summary>
        [UnityTest, Timeout(240000)]
        public IEnumerator CaptureTrainedLearningLab()
        {
            if (!TestBackend.Configured) Assert.Ignore(TestBackend.IgnoreReason);
            _storeRoot = Path.Combine(Path.GetTempPath(), "PlannerDNA_Snapshots_" + System.Guid.NewGuid().ToString("N"));
            PlannerApp.StoreRootOverride = _storeRoot;
            PlannerApp.BackendUrlOverride = TestBackend.Url.TrimEnd('/');
            SceneManager.LoadScene("PlannerDNA_Main");
            yield return null;
            yield return null;
            var app = Object.FindAnyObjectByType<PlannerApp>();
            yield return SyntheticComparisons.WaitFor(() => app.Health.StorageReady, 12f, () => "backend not ready: " + app.Health.Detail);

            yield return SyntheticComparisons.Record(app, new[] { 401, 402, 403 }, 5);
            yield return SyntheticComparisons.WaitFor(() => app.Outbox.PendingCount == 0, 60f, () => "records still pending");
            app.Ui.OpenLearningLab();
            var lab = app.Ui.Learning;
            yield return SyntheticComparisons.WaitFor(() => !lab.IsBusy && lab.TrainEnabled, 15f, () => "cannot train: " + lab.TrainStatusText);
            lab.Train();
            yield return SyntheticComparisons.WaitFor(() => !lab.IsBusy && lab.LastCurrent != null && lab.LastCurrent.has_model, 90f,
                () => "training: " + lab.TrainStatusText);
            yield return Capture(app, "08_learning_lab_trained.png");

            var scroll = app.Ui.Q<ScrollView>("learning-content");
            scroll.ScrollTo(app.Ui.Q<VisualElement>("learning-diagnostics"));
            yield return Capture(app, "09_learning_diagnostics.png");

            lab.Suggest();
            yield return SyntheticComparisons.WaitFor(() => !lab.IsBusy && lab.LastRank != null, 40f, () => "ranking: " + lab.SuggestStatusText);
            yield return null;
            scroll.ScrollTo(app.Ui.Q<VisualElement>("suggest-list"));
            yield return Capture(app, "10_suggestions.png");

            app.Ui.CloseTopOverlay();
            app.ResetToBaseline(InputSource.Mouse);
            CellData vacant = default;
            foreach (CellData c in app.Session.Current.Cells) if (c.Use == LandUse.Vacant) { vacant = c; break; }
            app.Select(vacant.Row, vacant.Col);
            app.ApplyTool(EditAction.SetResidential, InputSource.Mouse);
            app.SaveAlternative(AlternativeSlot.A);
            Assert.IsTrue(lab.ApplyRank(1), app.Ui.LastMessage);
            app.SaveAlternative(AlternativeSlot.B);
            app.Ui.OpenCompare();
            app.Ui.Compare.SelectForTest("B", SyntheticComparisons.Reason);
            app.Ui.Compare.Confirm();
            yield return SyntheticComparisons.WaitFor(() => !app.Ui.Compare.PredictionPending, 40f, () => "prediction pending");
            yield return Capture(app, "11_compare_prediction.png");
        }

        /// <summary>Needs a throwaway backend: a short blinded test answered by a synthetic rule, captured at each stage.</summary>
        [UnityTest, Timeout(300000)]
        public IEnumerator CaptureBlindedTest()
        {
            if (!TestBackend.Configured) Assert.Ignore(TestBackend.IgnoreReason);
            _storeRoot = Path.Combine(Path.GetTempPath(), "PlannerDNA_Snapshots_" + System.Guid.NewGuid().ToString("N"));
            PlannerApp.StoreRootOverride = _storeRoot;
            PlannerApp.BackendUrlOverride = TestBackend.Url.TrimEnd('/');
            SceneManager.LoadScene("PlannerDNA_Main");
            yield return null;
            yield return null;
            var app = Object.FindAnyObjectByType<PlannerApp>();
            app.Ui.SetTheme(false, false);
            yield return SyntheticComparisons.WaitFor(() => app.Health.StorageReady, 12f, () => "backend not ready: " + app.Health.Detail);
            yield return SyntheticComparisons.Record(app, new[] { 501, 502, 503 }, 5);
            yield return SyntheticComparisons.WaitFor(() => app.Outbox.PendingCount == 0, 60f, () => "records still pending");
            app.Ui.OpenLearningLab();
            var lab = app.Ui.Learning;
            yield return SyntheticComparisons.WaitFor(() => !lab.IsBusy && lab.TrainEnabled, 15f, () => "cannot train: " + lab.TrainStatusText);
            lab.Train();
            yield return SyntheticComparisons.WaitFor(() => !lab.IsBusy && app.Health.HasModel && lab.LastCurrent != null && lab.LastCurrent.has_model, 90f,
                () => "training: " + lab.TrainStatusText);

            app.Ui.OpenEvaluation();
            var view = app.Ui.Evaluation;
            yield return SyntheticComparisons.WaitFor(() => !view.IsBusy && view.StartEnabled, 15f, () => "start disabled: " + view.StatusText);
            yield return Capture(app, "21_eval_start.png");

            view.StartPlan(3, 1, 1);
            yield return SyntheticComparisons.WaitFor(() => !view.IsBusy && view.Plan != null, 30f, () => "plan: " + view.LastError);
            bool captured = false;
            for (int guard = 0; guard < 6 && view.Plan != null && view.Plan.next.action != "complete"; guard++)
            {
                yield return SyntheticComparisons.WaitFor(() => !view.IsBusy && view.NextVisible, 30f, () => "next: " + view.StatusText);
                view.PrepareNext();
                yield return SyntheticComparisons.WaitFor(() => !view.IsBusy && view.CurrentTrial != null && !view.CurrentAnswered, 60f, () => "pair: " + view.LastError);
                AlternativeSnapshot left = view.LeftIsA ? app.Session.AlternativeA : app.Session.AlternativeB;
                AlternativeSnapshot right = view.LeftIsA ? app.Session.AlternativeB : app.Session.AlternativeA;
                view.Choose(left.Features.delta_access_fraction >= right.Features.delta_access_fraction ? EvaluationView.Left : EvaluationView.Right);
                if (!captured)
                {
                    yield return Capture(app, "22_eval_pair.png");
                    captured = true;
                }
                int before = view.Plan.progress.Answered;
                view.Confirm();
                yield return SyntheticComparisons.WaitFor(() => !view.IsBusy && view.Plan != null && view.Plan.progress.Answered == before + 1, 60f,
                    () => "choice: " + view.ResultText);
            }
            view.Finish();
            yield return SyntheticComparisons.WaitFor(() => !view.IsBusy && view.LastReport != null && view.Plan == null, 90f, () => "report: " + view.LastError);
            yield return null;
            var scroll = app.Ui.Q<ScrollView>("eval-content");
            scroll.ScrollTo(app.Ui.Q<VisualElement>("eval-report-title"));
            yield return Capture(app, "23_eval_report.png");
            app.Ui.SetTheme(true, false);
            scroll.ScrollTo(app.Ui.Q<VisualElement>("eval-reveal"));
            yield return Capture(app, "24_dark_eval_reveals.png");
            app.Ui.SetTheme(false, false);
        }
    }
}
