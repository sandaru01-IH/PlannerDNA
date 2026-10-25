using System;
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
    /// Guided tour, light/dark theme and the header journey line on the real scene. The tour's
    /// waiting steps are completed through the same public actions the buttons call, and the
    /// spotlight geometry is checked against the raycast picker.
    /// </summary>
    [Timeout(90000)]
    public class GuidedTourPlayModeTests
    {
        private string _root;
        private PlannerApp _app;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "PlannerDNA_TourTests_" + Guid.NewGuid().ToString("N"));
            PlannerApp.StoreRootOverride = _root;
            PlannerApp.BackendUrlOverride = TestBackend.DeadPortUrl;
            SceneManager.LoadScene(AppScene.Name);
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

        private TutorialView Tour => _app.Ui.Tour;

        private IEnumerator Frames(int n)
        {
            for (int i = 0; i < n; i++) yield return null;
        }

        private IEnumerator WaitForStep(string key, float seconds = 5f)
        {
            float deadline = Time.realtimeSinceStartup + seconds;
            while (Tour.CurrentStepKey != key && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.AreEqual(key, Tour.CurrentStepKey, $"tour reached step '{key}'");
            float settle = Time.realtimeSinceStartup + 0.5f;
            while (Time.realtimeSinceStartup < settle) yield return null;
        }

        private Vector2 HoleCentreOnScreen()
        {
            Rect hole = Tour.CurrentHole.Value;
            VisualElement layer = _app.Ui.Q<VisualElement>("tour-layer");
            Vector2 panel = layer.LocalToWorld(hole.center);
            Rect root = _app.Ui.Root.worldBound;
            return new Vector2(panel.x * Screen.width / root.width, Screen.height - panel.y * Screen.height / root.height);
        }

        private string PickedAt(Vector2 screen)
        {
            IPanel panel = _app.Ui.Root.panel;
            Vector2 p = RuntimePanelUtils.ScreenToPanel(panel, new Vector2(screen.x, Screen.height - screen.y));
            VisualElement e = panel.Pick(p);
            return e == null ? "nothing" : $"'{e.name}' ({string.Join(" ", e.GetClasses())}) at {p}, hole {Tour.CurrentHole}, screen {Screen.width}x{Screen.height}, root {_app.Ui.Root.worldBound}";
        }

        [UnityTest]
        public IEnumerator Tour_FromHome_WalksTheRealWorkflow_AndWaitsForEachAction()
        {
            int startPref = PlayerPrefs.GetInt(SandboxUi.ThemePrefKey, 0);
            Assert.IsTrue(_app.Ui.Home.StartTour(), "tour starts from Home");
            yield return Frames(3);
            Assert.IsNotNull(_app.Session, "the demo city was created");
            Assert.IsTrue(Tour.IsActive);
            Assert.AreEqual("welcome", Tour.CurrentStepKey);
            Assert.IsFalse(_app.Ui.IsOverlayOpen, "Home closed; shortcuts keep working during the tour");
            Assert.IsFalse(_app.Ui.Q<VisualElement>("tour-layer").ClassListContains("hidden"));
            Assert.IsNull(Tour.CurrentHole, "the welcome step is centred");

            Tour.Next();
            yield return WaitForStep("goal");
            Rect goal = _app.Ui.Q<VisualElement>("tour-layer").WorldToLocal(_app.Ui.Q<VisualElement>("goal-card").worldBound);
            Assert.IsTrue(Tour.CurrentHole.HasValue);
            Assert.IsTrue(Tour.CurrentHole.Value.Contains(goal.center), "spotlight sits on the goal card");

            Tour.Next();
            yield return WaitForStep("map");
            Tour.Next();
            yield return WaitForStep("access");
            Assert.IsTrue(Tour.IsWaitingForAction);
            _app.SetAccessOverlay(true);
            yield return WaitForStep("select");

            string suggested = Tour.SuggestedParcelId;
            Assert.IsNotNull(suggested, "a vacant parcel is suggested");
            Assert.IsTrue(CellIds.TryParse(suggested, out int row, out int col));
            CellData cell = _app.Session.Current.Get(row, col);
            Assert.AreEqual(LandUse.Vacant, cell.Use);
            Assert.IsTrue(cell.Editable);
            Assert.Less(Tour.CurrentHole.Value.width, 220f, "the spotlight frames one parcel, not the whole map");

            Vector2 screen = HoleCentreOnScreen();
            Assert.IsFalse(_app.Ui.IsPointerOverUi(screen), "the spotlight hole lets clicks reach the city; picked " + PickedAt(screen));
            Assert.IsTrue(_app.Picker.TryPick(screen, out int pr, out int pc));
            Assert.AreEqual(suggested, CellIds.Format(pr, pc), "the ring is drawn over the suggested parcel");
            Assert.IsTrue(_app.Ui.IsPointerOverUi(new Vector2(Screen.width * 0.5f, 4f)), "the dimmed area blocks the header");

            _app.Select(row, col);
            yield return WaitForStep("park");
            Assert.IsTrue(_app.ApplyTool(EditAction.SetPark, InputSource.Mouse).Accepted);
            yield return WaitForStep("metrics");
            Assert.AreEqual(1, _app.Ui.JourneyStage, "an edit moves the journey to Design A and B");

            Tour.Next();
            yield return WaitForStep("history");
            Tour.Next();
            yield return WaitForStep("save-a");
            _app.SaveAlternative(AlternativeSlot.A);
            yield return WaitForStep("compare");
            Tour.Next();
            yield return WaitForStep("journey");
            Tour.Next();
            yield return WaitForStep("learning");
            Tour.Next();
            yield return WaitForStep("help");
            Assert.AreEqual("Finish", _app.Ui.Q<Button>("btn-tour-next").text);
            Tour.Next();
            yield return null;
            Assert.IsFalse(Tour.IsActive);
            Assert.IsTrue(_app.Ui.Q<VisualElement>("tour-layer").ClassListContains("hidden"));
            Assert.AreEqual(startPref, PlayerPrefs.GetInt(SandboxUi.ThemePrefKey, 0), "the tour stores nothing");
        }

        [UnityTest]
        public IEnumerator Tour_WaitingStepsCanBeSkipped_AndBackWorks()
        {
            Assert.IsFalse(Tour.Begin(), "no tour without a challenge");
            Assert.IsTrue(_app.Ui.Home.Start(42));
            yield return Frames(2);
            Assert.IsTrue(Tour.Begin());
            for (int i = 0; i < 3; i++) Tour.Next();
            yield return WaitForStep("access");
            Assert.AreEqual("Skip this step", _app.Ui.Q<Button>("btn-tour-next").text);
            Tour.Next();
            yield return WaitForStep("select");
            Assert.IsFalse(_app.AccessOverlay, "skipping does not perform the action");
            Tour.Back();
            yield return WaitForStep("access");
            _app.Ui.ShowHome();
            Assert.IsFalse(Tour.IsActive, "opening another screen ends the tour");
        }

        [UnityTest]
        public IEnumerator DarkMode_SwitchesColoursAndCamera_WithoutPersistingInTests()
        {
            Assert.IsTrue(_app.Ui.Home.Start(42));
            yield return Frames(2);
            int startPref = PlayerPrefs.GetInt(SandboxUi.ThemePrefKey, 0);
            VisualElement appRoot = _app.Ui.Q<VisualElement>("app-root");
            VisualElement header = _app.Ui.Q<VisualElement>("header");

            _app.Ui.SetTheme(false, false);
            yield return Frames(2);
            Color lightHeader = header.resolvedStyle.backgroundColor;
            Assert.IsFalse(appRoot.ClassListContains("theme-dark"));
            Assert.AreEqual("Dark mode", _app.Ui.Q<Button>("btn-theme").text);
            Assert.AreEqual(SandboxUi.LightSky, _app.CameraRig.Camera.backgroundColor);

            _app.Ui.SetTheme(true, false);
            yield return Frames(2);
            Color darkHeader = header.resolvedStyle.backgroundColor;
            Assert.IsTrue(appRoot.ClassListContains("theme-dark"));
            Assert.AreEqual("Light mode", _app.Ui.Q<Button>("btn-theme").text);
            Assert.AreEqual(SandboxUi.DarkSky, _app.CameraRig.Camera.backgroundColor);
            Assert.Greater(lightHeader.grayscale, 0.8f, "light header is light");
            Assert.Less(darkHeader.grayscale, 0.25f, "dark header is dark (theme variables resolved)");

            _app.Ui.SetTheme(false, false);
            Assert.AreEqual(startPref, PlayerPrefs.GetInt(SandboxUi.ThemePrefKey, 0), "test theme switches are not persisted");
        }

        [UnityTest]
        public IEnumerator JourneyLine_FollowsTheSessionState()
        {
            Assert.IsTrue(_app.Ui.Home.Start(42));
            yield return Frames(2);
            VisualElement explore = _app.Ui.Q<VisualElement>("station-explore");
            VisualElement compare = _app.Ui.Q<VisualElement>("station-compare");
            Assert.AreEqual(0, _app.Ui.JourneyStage);
            Assert.IsTrue(explore.ClassListContains("station--current"));
            StringAssert.Contains("0/10", _app.Ui.Q<Label>("station-learn-label").text);

            CellData vacant = default;
            foreach (CellData c in _app.Session.Current.Cells)
                if (c.Use == LandUse.Vacant && c.Editable) { vacant = c; break; }
            _app.Select(vacant.Row, vacant.Col);
            _app.ApplyTool(EditAction.SetPark, InputSource.Mouse);
            _app.SaveAlternative(AlternativeSlot.A);
            Assert.AreEqual(1, _app.Ui.JourneyStage);
            Assert.IsTrue(explore.ClassListContains("station--done"));
            Assert.AreEqual("Not saved yet", _app.Ui.Q<Label>("alt-b-status").text);
            StringAssert.StartsWith("Park access", _app.Ui.Q<Label>("alt-a-status").text);

            _app.Undo(InputSource.Mouse);
            _app.SaveAlternative(AlternativeSlot.B);
            yield return null;
            Assert.AreEqual(2, _app.Ui.JourneyStage);
            Assert.IsTrue(compare.ClassListContains("station--current"));
            Assert.IsTrue(compare.ClassListContains("station--link"), "the Compare station opens the comparison");
            Assert.IsTrue(_app.Ui.Q<Button>("btn-compare").enabledSelf);
        }
    }
}
