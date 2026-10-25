using System;
using System.Collections;
using System.IO;
using NUnit.Framework;
using PlannerDNA.App;
using PlannerDNA.Commands;
using PlannerDNA.Core;
using PlannerDNA.Metrics;
using PlannerDNA.Networking;
using PlannerDNA.Rendering;
using PlannerDNA.Storage;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace PlannerDNA.Tests
{
    /// <summary>
    /// Smoke tests on the real PlannerDNA_Main scene (built by PlannerDNA > Create or Repair Demo Scene).
    /// They drive the app through its public API and the raycast picker; physical mouse input
    /// and visual appearance still need a manual Play-mode check.
    /// </summary>
    [Timeout(60000)]
    public class SandboxPlayModeTests
    {
        private string _root;
        private PlannerApp _app;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "PlannerDNA_PlayTests_" + Guid.NewGuid().ToString("N"));
            PlannerApp.StoreRootOverride = _root;
            PlannerApp.BackendUrlOverride = TestBackend.UrlOrDeadPort;
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
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private IEnumerator StartDemo()
        {
            Assert.IsTrue(_app.Ui.Home.Start(42), "demo seed starts");
            yield return null;
            yield return null;
        }

        private CellData FindCell(Func<CellData, bool> predicate)
        {
            foreach (CellData c in _app.Session.Current.Cells)
                if (predicate(c)) return c;
            Assert.Fail("No matching cell");
            return default;
        }

        [UnityTest]
        public IEnumerator Scene_StartsOnHomeScreen_WithPlannerDnaBranding()
        {
            Assert.IsTrue(_app.Ui.IsBound);
            PanelSettings panel = _app.Ui.GetComponent<UIDocument>().panelSettings;
            Assert.IsNotNull(panel, "UIDocument has the PlannerDNA PanelSettings");
            Assert.IsNotNull(panel.themeStyleSheet, "PanelSettings has a theme");
            VisualElement home = _app.Ui.Q<VisualElement>("home-screen");
            Assert.IsFalse(home.ClassListContains("hidden"), "home screen visible");
            Assert.IsTrue(_app.Ui.IsOverlayOpen);
            Assert.IsNotNull(_app.Ui.Root.Q<Label>(className: "brand"));
            Assert.AreEqual("PlannerDNA", _app.Ui.Root.Q<Label>(className: "brand").text);
            Assert.IsNull(_app.Session);
            yield return null;
        }

        [UnityTest]
        public IEnumerator NewChallenge_RendersAllCells_AndDashboardMatchesMetricEngine()
        {
            yield return StartDemo();
            Assert.IsNotNull(_app.Session);
            Assert.IsFalse(_app.Ui.IsOverlayOpen);
            Assert.AreEqual(400, _app.Grid.GetComponentsInChildren<CellView>().Length);
            Assert.AreEqual("The Neighbourhood Green-Space Dilemma", _app.Ui.Q<Label>("scenario-title").text);
            StringAssert.Contains("seed 42", _app.Ui.Q<Label>("scenario-meta").text);

            CityMetrics m = _app.Session.CurrentMetrics;
            Assert.AreEqual(m.AccessDisplay, _app.Ui.Q<Label>("metric-access-value").text);
            Assert.AreEqual(m.HousingUnits.ToString(), _app.Ui.Q<Label>("metric-housing-value").text);
            Assert.AreEqual("0 / 100", _app.Ui.Q<Label>("metric-cost-value").text);
        }

        [UnityTest]
        public IEnumerator DefaultCamera_FramesWholeCityInsideTheVisibleViewport()
        {
            yield return StartDemo();
            Rect? view = _app.Ui.ViewportScreenRect();
            Assert.IsTrue(view.HasValue, "viewport laid out");
            Camera cam = _app.Picker.TargetCamera;
            Bounds b = _app.Grid.WorldBounds();
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = b.center + Vector3.Scale(b.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                Vector3 s = cam.WorldToScreenPoint(corner);
                Assert.IsTrue(view.Value.Contains(new Vector2(s.x, s.y)), $"corner {corner} at {s} outside viewport {view.Value}");
            }
        }

        private static bool OnScreen(Vector3 screen) =>
            screen.z > 0 && screen.x >= 0 && screen.y >= 0 && screen.x <= Screen.width && screen.y <= Screen.height;

        private static Bounds RenderedBounds(CellView view)
        {
            Renderer[] renderers = view.GetComponentsInChildren<Renderer>();
            Bounds b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
            return b;
        }

        [UnityTest]
        public IEnumerator RaycastPicking_RoofOfTallestBuilding_SelectsThatBuilding()
        {
            yield return StartDemo();
            yield return null;
            Camera cam = _app.Picker.TargetCamera;
            CellData tallest = FindCell(c => c.Use == LandUse.Residential);
            foreach (CellData c in _app.Session.Current.Cells)
                if (c.Use.IsBuilding() && _app.Grid.CellTop(c.Row, c.Col) > _app.Grid.CellTop(tallest.Row, tallest.Col))
                    tallest = c;

            // Nothing rises above the tallest roof, so a ray to its centre cannot be occluded.
            Vector3 roof = _app.Grid.CellCentre(tallest.Row, tallest.Col) + Vector3.up * (_app.Grid.CellTop(tallest.Row, tallest.Col) - 0.005f);
            Vector3 screen = cam.WorldToScreenPoint(roof);
            Assert.IsTrue(OnScreen(screen), "default camera frames the whole city");
            Assert.IsTrue(_app.Picker.TryPick(screen, out int r, out int col));
            Assert.AreEqual(tallest.Id, CellIds.Format(r, col));
        }

        [UnityTest]
        public IEnumerator RaycastPicking_GroundParcels_SelectTargetUnlessVisiblyOccluded()
        {
            yield return StartDemo();
            yield return null;
            Camera cam = _app.Picker.TargetCamera;
            int exact = 0, occluded = 0;
            foreach (CellData target in _app.Session.Current.Cells)
            {
                if (target.Use != LandUse.Vacant && target.Use != LandUse.Road) continue;
                Vector3 ground = _app.Grid.CellCentre(target.Row, target.Col) + Vector3.up * (_app.Grid.CellTop(target.Row, target.Col) - 0.005f);
                Vector3 screen = cam.WorldToScreenPoint(ground);
                if (!OnScreen(screen)) continue;
                Assert.IsTrue(_app.Picker.TryPick(screen, out int r, out int c), $"ray hits something for {target.Id}");
                if (CellIds.Format(r, c) == target.Id) { exact++; continue; }

                Ray ray = cam.ScreenPointToRay(screen);
                float targetDistance = Vector3.Distance(ray.origin, ground);
                Bounds blocker = RenderedBounds(_app.Grid.GetView(r, c));
                Assert.IsTrue(blocker.IntersectRay(ray, out float d) && d < targetDistance,
                    $"{target.Id} picked {CellIds.Format(r, c)}, but that parcel's visible geometry is not in front of it");
                occluded++;
            }
            Assert.Greater(exact, 50, $"most ground parcels pick directly (exact={exact}, occluded={occluded})");
        }

        [UnityTest]
        public IEnumerator Edit_UpdatesDashboard_UndoRestoresExactState()
        {
            yield return StartDemo();
            string json0 = CitySerializer.ToJson(_app.Session.Current);
            CellData vacant = FindCell(c => c.Use == LandUse.Vacant);
            _app.Select(vacant.Row, vacant.Col);
            yield return null;
            Assert.IsFalse(_app.Ui.Toolbar.ButtonFor(EditAction.SetPark).ClassListContains("tool--invalid"));
            Assert.IsTrue(_app.Ui.Toolbar.ButtonFor(EditAction.RaiseFloors).ClassListContains("tool--invalid"), "cannot raise a vacant site");

            Assert.IsTrue(_app.ApplyTool(EditAction.SetPark, InputSource.Mouse).Accepted);
            yield return null;
            Assert.AreEqual("20 / 100", _app.Ui.Q<Label>("metric-cost-value").text);
            Assert.AreEqual(LandUse.Park, _app.Grid.GetView(vacant.Row, vacant.Col).DrawnCell.Use, "view redrawn from state");
            Assert.AreEqual(1, _app.Grid.RedrawCountLastRender, "only the edited parcel was rebuilt");

            CommandOutcome refused = _app.ApplyTool(EditAction.RaiseFloors, InputSource.Mouse);
            Assert.IsFalse(refused.Accepted);
            Assert.IsTrue(_app.Ui.LastMessageWasError);
            StringAssert.Contains("Cannot raise", _app.Ui.LastMessage);

            _app.SetAccessOverlay(true);
            Assert.IsTrue(_app.Ui.Q<Toggle>("toggle-access").value, "toggle mirrors overlay state");
            Assert.AreEqual(CellView.TileTint.Normal, _app.Grid.GetView(vacant.Row, vacant.Col).DrawnTint, "only residential parcels are tinted");
            _app.SetAccessOverlay(false);
            Assert.IsFalse(_app.Ui.Q<Toggle>("toggle-access").value);

            _app.Undo(InputSource.Keyboard);
            yield return null;
            Assert.AreEqual(json0, CitySerializer.ToJson(_app.Session.Current));
            Assert.AreEqual("0 / 100", _app.Ui.Q<Label>("metric-cost-value").text);
            Assert.GreaterOrEqual(_app.Ui.Timeline.ChipCount, 2);
        }

        [UnityTest]
        public IEnumerator AlternativesCompareAndChoice_PersistToDisk_Once()
        {
            yield return StartDemo();
            CellData vacant = FindCell(c => c.Use == LandUse.Vacant);
            _app.Select(vacant.Row, vacant.Col);
            _app.ApplyTool(EditAction.SetPark, InputSource.Mouse);
            _app.SaveAlternative(AlternativeSlot.A);

            _app.ResetToBaseline(InputSource.Mouse);
            CellData res = FindCell(c => c.Use == LandUse.Residential && c.Floors < 8);
            _app.Select(res.Row, res.Col);
            _app.ApplyTool(EditAction.RaiseFloors, InputSource.Mouse);
            _app.SaveAlternative(AlternativeSlot.B);
            yield return null;

            Assert.IsTrue(File.Exists(Path.Combine(_root, "alternatives", _app.Session.AlternativeA.AlternativeId + ".json")));
            Assert.IsTrue(_app.Ui.Q<Button>("btn-compare").enabledSelf);

            _app.Ui.Compare.Open();
            Assert.IsNotNull(_app.Ui.Compare.CurrentComparisonId);
            _app.Ui.Compare.SelectForTest("B", "kept housing");
            _app.Ui.Compare.Confirm();
            _app.Ui.Compare.Confirm();
            yield return null;

            var store = new LocalDecisionStore(_root);
            var prefs = store.LoadAllPreferences();
            Assert.AreEqual(1, prefs.Count, "exactly one record despite double confirm");
            Assert.AreEqual("B", prefs[0].choice);
            Assert.AreEqual("kept housing", prefs[0].reason);
            Assert.IsTrue(prefs[0].training_eligible);
            Assert.IsTrue(_app.Ui.Compare.IsLocked);
            string learning = _app.Ui.Q<Label>("learning-status").text;
            if (_app.Health.HasModel) StringAssert.Contains(_app.Health.ActiveModelVersion, learning);
            else StringAssert.IsMatch("Model status unknown|No preference model has been trained", learning);
            StringAssert.Contains("nothing is trained until you press", _app.Ui.Q<Label>("compare-result").text);
        }

        [UnityTest]
        public IEnumerator PreviewAlternative_ShowsItsStateAndBlocksEditing()
        {
            yield return StartDemo();
            CellData vacant = FindCell(c => c.Use == LandUse.Vacant);
            _app.Select(vacant.Row, vacant.Col);
            _app.ApplyTool(EditAction.SetPark, InputSource.Mouse);
            _app.SaveAlternative(AlternativeSlot.A);
            _app.ResetToBaseline(InputSource.Mouse);
            yield return null;
            Assert.AreEqual(LandUse.Vacant, _app.Grid.GetView(vacant.Row, vacant.Col).DrawnCell.Use);

            _app.BeginPreview(AlternativeSlot.A);
            yield return null;
            Assert.AreEqual(LandUse.Park, _app.Grid.GetView(vacant.Row, vacant.Col).DrawnCell.Use, "preview renders A");
            Assert.IsFalse(_app.ApplyTool(EditAction.SetPark, InputSource.Mouse).Accepted, "editing blocked in preview");
            _app.EndPreview();
            yield return null;
            Assert.AreEqual(LandUse.Vacant, _app.Grid.GetView(vacant.Row, vacant.Col).DrawnCell.Use);
        }

        [UnityTest]
        public IEnumerator SaveAndResume_RestoresDesign()
        {
            yield return StartDemo();
            CellData vacant = FindCell(c => c.Use == LandUse.Vacant);
            _app.Select(vacant.Row, vacant.Col);
            _app.ApplyTool(EditAction.SetPark, InputSource.Mouse);
            string saved = CitySerializer.ToJson(_app.Session.Current);
            Assert.IsTrue(_app.SaveDesign(out string msg), msg);

            Assert.IsTrue(_app.NewChallenge(7, out _));
            Assert.IsTrue(_app.ResumeLatest(out string resumeMsg), resumeMsg);
            yield return null;
            Assert.AreEqual(saved, CitySerializer.ToJson(_app.Session.Current));
            Assert.AreEqual(20, _app.Session.CurrentMetrics.SpentCredits);
        }

        [UnityTest]
        public IEnumerator RebuildingViewsFromJsonSnapshot_LeavesMetricsUnchanged()
        {
            yield return StartDemo();
            string json = CitySerializer.ToJson(_app.Session.Current);
            CityMetrics before = _app.Session.CurrentMetrics;

            var go = new GameObject("SecondRenderer");
            GridRenderer second = go.AddComponent<GridRenderer>();
            CityState restored = CitySerializer.FromJson(json);
            CityMetrics after = MetricEngine.Evaluate(restored, _app.Session.Baseline, _app.Session.Scenario);
            second.Render(restored, _app.Session.Baseline, after, 400);
            yield return null;

            Assert.AreEqual(400, go.GetComponentsInChildren<CellView>().Length);
            Assert.AreEqual(before.AccessPct, after.AccessPct);
            Assert.AreEqual(before.HousingUnits, after.HousingUnits);
            UnityEngine.Object.Destroy(go);
        }

        [UnityTest]
        public IEnumerator BackendStatus_ResolvesWithoutBlockingTheSandbox()
        {
            yield return StartDemo();
            float deadline = Time.realtimeSinceStartup + 6f;
            while (_app.Health.Status == BackendStatus.Checking && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.AreNotEqual(BackendStatus.Checking, _app.Health.Status, "health check completed");
            StringAssert.DoesNotContain("checking", _app.Ui.Q<Label>("sync-status").text);

            CellData vacant = FindCell(c => c.Use == LandUse.Vacant);
            _app.Select(vacant.Row, vacant.Col);
            Assert.IsTrue(_app.ApplyTool(EditAction.SetPark, InputSource.Mouse).Accepted, "editing works regardless of backend");
        }

        [UnityTest]
        public IEnumerator BackendOnline_WhenHealthServerRuns_ShowsOnlineAndHonestModelStatus()
        {
            float deadline = Time.realtimeSinceStartup + 6f;
            while (_app.Health.Status == BackendStatus.Checking && Time.realtimeSinceStartup < deadline) yield return null;
            if (!TestBackend.Configured || _app.Health.Status != BackendStatus.Online)
                Assert.Ignore(TestBackend.IgnoreReason);
            StringAssert.StartsWith("Backend: Online", _app.Ui.Q<Label>("sync-status").text);
            // The shared throwaway backend may already hold a model trained on synthetic fixture labels by another test.
            if (_app.Health.HasModel)
            {
                StringAssert.Contains("model: trained " + _app.Health.ActiveModelVersion, _app.Health.Detail);
                StringAssert.StartsWith("pm-", _app.Health.ActiveModelVersion);
            }
            else
            {
                StringAssert.Contains("model: no_model", _app.Health.Detail);
                Assert.AreEqual(string.Empty, _app.Health.ActiveModelVersion);
            }
            StringAssert.Contains("storage: ready", _app.Health.Detail);
        }
    }
}
