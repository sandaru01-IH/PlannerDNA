using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using NUnit.Framework;
using PlannerDNA.App;
using PlannerDNA.Commands;
using PlannerDNA.Core;
using PlannerDNA.Rendering;
using PlannerDNA.UI;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace PlannerDNA.Tests
{
    /// <summary>
    /// Opt-in: plays a scripted demo of the whole app and writes 1920x1080 JPEG frames plus an ffmpeg concat
    /// list (TestResults/video_frames) and full-size PNG stills (deliverables/screenshots). It needs
    /// PLANNERDNA_TEST_BACKEND_URL (a throwaway backend, e.g. one started on a COPY of the user's database) and
    /// PLANNERDNA_DEMO_STORE (a copy of the local data folder). Choices in the recording are scripted.
    /// </summary>
    [Explicit("Records demo video frames")]
    public class DemoVideoRecorder
    {
        private const int Width = 1920, Height = 1080;
        private const double FrameSeconds = 1.0 / 30.0;
        private const string ScriptedReason = "demo video (scripted choice)";

        private static string Repo => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", ".."));
        private static string FrameDir => Path.Combine(Repo, "TestResults", "video_frames");
        private static string StillDir => Path.Combine(Repo, "deliverables", "screenshots");

        private PlannerApp _app;
        private CameraRig _rig;
        private RenderTexture _worldRt, _uiRt;
        private Texture2D _worldTex, _uiTex;
        private PanelSettings _panel;
        private VisualElement _captionHost;
        private Label _caption;
        private readonly float[] _toLinear = new float[256];
        private readonly byte[] _toSrgb = new byte[4096];
        private readonly List<KeyValuePair<string, double>> _shots = new List<KeyValuePair<string, double>>();
        private int _frame;

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_app != null)
            {
                if (_rig != null) _rig.Camera.targetTexture = null;
                if (_panel != null) _panel.targetTexture = null;
            }
            _worldRt?.Release();
            _uiRt?.Release();
            PlannerApp.StoreRootOverride = null;
            PlannerApp.BackendUrlOverride = null;
            AppScene.DestroyIfLoaded();
            yield return null;
        }

        private static IEnumerator WaitFor(Func<bool> condition, float seconds, Func<string> message) =>
            SyntheticComparisons.WaitFor(condition, seconds, message);

        [UnityTest, Timeout(1800000)]
        public IEnumerator RecordFullDemo()
        {
            string store = Environment.GetEnvironmentVariable("PLANNERDNA_DEMO_STORE");
            if (!TestBackend.Configured || string.IsNullOrEmpty(store))
                Assert.Ignore("Needs PLANNERDNA_TEST_BACKEND_URL (throwaway backend) and PLANNERDNA_DEMO_STORE (copy of the local data).");

            if (Directory.Exists(FrameDir)) Directory.Delete(FrameDir, true);
            Directory.CreateDirectory(FrameDir);
            Directory.CreateDirectory(StillDir);
            for (int i = 0; i < 256; i++) _toLinear[i] = Mathf.GammaToLinearSpace(i / 255f);
            for (int i = 0; i < 4096; i++) _toSrgb[i] = (byte)Mathf.RoundToInt(Mathf.LinearToGammaSpace(i / 4095f) * 255f);

            Screen.SetResolution(Width, Height, FullScreenMode.Windowed);
            PlannerApp.StoreRootOverride = store;
            PlannerApp.BackendUrlOverride = TestBackend.Url.TrimEnd('/');
            SceneManager.LoadScene(AppScene.Name);
            yield return null;
            yield return null;
            _app = UnityEngine.Object.FindAnyObjectByType<PlannerApp>();
            Assert.IsNotNull(_app);
            _app.Ui.SetTheme(false, false);
            SetUpCapture();
            yield return WaitFor(() => _app.Health.StorageReady && _app.Health.HasModel, 20f, () => "backend copy not ready: " + _app.Health.Detail);
            yield return WaitFor(() => _app.Outbox.PendingCount == 0, 60f, () => "outbox not empty");

            // 1. Home
            yield return Hold(4.5, "PlannerDNA: a 3D sandbox where you design a neighbourhood, compare two plans and choose", "01_home");

            // 2. Guided tour on the real city
            Assert.IsTrue(_app.Ui.Home.StartTour());
            yield return Hold(4, "A guided tour shows first-year planning students what each part of the screen does", "02_tour_welcome");
            _app.Ui.Tour.Next();
            yield return Hold(4.5, "The Green-Space Dilemma: more homes within a 400 m walk of a park, within budget and building rules", "03_tour_goal");
            _app.Ui.Tour.Next();
            _app.Ui.Tour.Next();
            _app.SetAccessOverlay(true);
            yield return WaitFor(() => _app.Ui.Tour.CurrentStepKey == "select", 5f, () => "tour at " + _app.Ui.Tour.CurrentStepKey);
            yield return Hold(4.5, "Park-access overlay: teal homes reach a park within 400 m by road, rose homes do not", "04_tour_access");
            Assert.IsTrue(CellIds.TryParse(_app.Ui.Tour.SuggestedParcelId, out int row, out int col));
            _app.Select(row, col);
            yield return WaitFor(() => _app.Ui.Tour.CurrentStepKey == "park", 5f, () => "tour at " + _app.Ui.Tour.CurrentStepKey);
            yield return Hold(3, "Select a vacant parcel, then choose what to build on it", "05_tour_tool");
            _app.ApplyTool(EditAction.SetPark, InputSource.Mouse);
            yield return WaitFor(() => _app.Ui.Tour.CurrentStepKey == "metrics", 5f, () => "tour at " + _app.Ui.Tour.CurrentStepKey);
            yield return Hold(4.5, "Every edit instantly recomputes access, housing, budget and the rules, with exact undo and redo", "06_tour_metrics");
            _app.Ui.Tour.End();

            // 3. The city
            yield return Orbit(6, 90f, 0.82f, "A deterministic synthetic city: 20 x 20 parcels of roads, homes, shops, parks and civic land");

            // 4. Two alternatives
            _app.SaveAlternative(AlternativeSlot.A);
            yield return Hold(3, "Design A: a new park. Saved as a complete snapshot with its metrics", "07_design_a");
            _app.ResetToBaseline(InputSource.Mouse);
            CellData target = default;
            foreach (CellData c in _app.Session.Current.Cells) if (c.Use == LandUse.Residential && c.Floors < 8) { target = c; break; }
            _app.Select(target.Row, target.Col);
            _app.ApplyTool(EditAction.RaiseFloors, InputSource.Mouse);
            _app.SaveAlternative(AlternativeSlot.B);
            yield return Hold(3, "Design B: start over from the same baseline and add a floor of homes instead", "08_design_b");

            // 5. Compare and choose; the model's prediction appears only afterwards
            _app.Ui.OpenCompare();
            yield return Hold(5, "Compare A and B against the same baseline, with a plain-language trade-off", "09_compare");
            _app.Ui.Compare.SelectForTest("A", ScriptedReason);
            _app.Ui.Compare.Confirm();
            yield return WaitFor(() => !_app.Ui.Compare.PredictionPending, 40f, () => "prediction pending");
            yield return Hold(5, "Only after you choose does the trained model show what it would have predicted", "10_compare_prediction");
            _app.Ui.CloseTopOverlay();

            // 6. Learning Lab
            _app.Ui.OpenLearningLab();
            LearningLabView lab = _app.Ui.Learning;
            yield return WaitFor(() => !lab.IsBusy && lab.LastCurrent != null, 20f, () => "Learning Lab loading");
            yield return Hold(5, "Learning Lab: a pairwise logistic model, trained only when the student presses Train", "11_learning_lab");
            ScrollView labScroll = _app.Ui.Q<ScrollView>("learning-content");
            labScroll.ScrollTo(_app.Ui.Q<VisualElement>("learning-diagnostics"));
            yield return Hold(5, "Weights with scenario-bootstrap intervals, and cross-validation against simple rules of thumb", "12_learning_diagnostics");
            lab.Suggest();
            yield return WaitFor(() => !lab.IsBusy && lab.LastRank != null, 40f, () => "ranking: " + lab.SuggestStatusText);
            yield return null;
            labScroll.ScrollTo(_app.Ui.Q<VisualElement>("suggest-list"));
            yield return Hold(4, "Suggestions: generated feasible designs ranked by the model (synthetic candidates, never 'optimal plans')", "13_suggestions");

            // 7. Blinded test
            _app.Ui.OpenEvaluation();
            EvaluationView view = _app.Ui.Evaluation;
            yield return WaitFor(() => !view.IsBusy && view.StartEnabled, 20f, () => "start disabled: " + view.StatusText);
            yield return Hold(5, "The fair check: a blinded test of the frozen model on areas it has never seen", "14_eval_start");
            view.Reason = ScriptedReason;
            view.StartPlan(EvaluationView.ShortScenarios, EvaluationView.ShortPairs, EvaluationView.ShortRepeats);
            yield return WaitFor(() => !view.IsBusy && view.Plan != null, 60f, () => "plan: " + view.LastError);
            int answered = 0;
            for (int guard = 0; guard < 40 && view.Plan != null && view.Plan.next.action != "complete"; guard++)
            {
                yield return WaitFor(() => !view.IsBusy && view.NextVisible, 60f, () => "next: " + view.StatusText);
                view.PrepareNext();
                yield return WaitFor(() => !view.IsBusy && view.CurrentTrial != null && !view.CurrentAnswered, 90f, () => "pair: " + view.LastError);
                AlternativeSnapshot left = view.LeftIsA ? _app.Session.AlternativeA : _app.Session.AlternativeB;
                AlternativeSnapshot right = view.LeftIsA ? _app.Session.AlternativeB : _app.Session.AlternativeA;
                double dl = left.Features.delta_access_fraction, dr = right.Features.delta_access_fraction;
                bool pickLeft = Math.Abs(dl - dr) > 1e-9 ? dl > dr : left.Metrics.SpentCredits <= right.Metrics.SpentCredits;
                if (answered == 0)
                    yield return Hold(5.5, "Each prediction is computed and sealed (SHA-256) before the choice; left and right are randomised", "15_eval_pair");
                view.Choose(pickLeft ? EvaluationView.Left : EvaluationView.Right);
                if (answered == 0) yield return Hold(2.5, "Choose, confirm, next pair. The prediction stays hidden", null);
                view.Confirm();
                int expected = answered + 1;
                yield return WaitFor(() => !view.IsBusy && view.CurrentAnswered && view.Plan != null && view.Plan.progress.Answered == expected, 90f,
                    () => "choice: " + view.ResultText + " " + view.LastError);
                answered++;
                if (answered > 1) yield return Hold(0.6, $"Fast-forward: choice {answered} of 15 (some pairs come back with sides swapped)", null);
            }
            view.Finish();
            yield return WaitFor(() => !view.IsBusy && view.Plan == null && view.LastReport != null, 120f, () => "report: " + view.LastError);
            yield return null;
            ScrollView evalScroll = _app.Ui.Q<ScrollView>("eval-content");
            evalScroll.ScrollTo(_app.Ui.Q<VisualElement>("eval-reveal"));
            yield return Hold(5, "At the end every sealed prediction is opened and checked against its fingerprint", "16_eval_reveals");
            evalScroll.ScrollTo(_app.Ui.Q<VisualElement>("eval-report-title"));
            yield return Hold(6, "A reproducible report: accuracy, Brier score and log loss against chance and rules of thumb", "17_eval_report");

            // 8. Dark mode and close
            _app.Ui.CloseTopOverlay();
            _app.Ui.CloseTopOverlay();
            _app.Ui.SetTheme(true, false);
            yield return Orbit(5, -90f, 0.9f, "Light and dark themes. Everything runs locally: Unity, a small FastAPI server and SQLite");
            yield return Hold(3, "Figures for reports are generated from the exported data with one command", "18_dark_city");
            _app.Ui.SetTheme(false, false);

            WriteConcatList();
            Debug.Log($"[DemoVideo] frames={_frame} seconds={Total():F1} test choices={answered}");
        }

        // ---- capture ----

        private void SetUpCapture()
        {
            _rig = _app.Picker.TargetCamera.GetComponent<CameraRig>();
            _worldRt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            _uiRt = new RenderTexture(Width, Height, 24, RenderTextureFormat.ARGB32);
            _worldTex = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
            _uiTex = new Texture2D(Width, Height, TextureFormat.RGBA32, false);
            _panel = _app.Ui.GetComponent<UIDocument>().panelSettings;
            _panel.targetTexture = _uiRt;
            _panel.clearColor = true;
            _panel.colorClearValue = new Color(0, 0, 0, 0);
            _rig.Camera.targetTexture = _worldRt;

            _captionHost = new VisualElement { pickingMode = PickingMode.Ignore };
            IStyle h = _captionHost.style;
            h.position = Position.Absolute;
            h.left = 0;
            h.right = 0;
            h.bottom = 26;
            h.alignItems = Align.Center;
            _caption = new Label { pickingMode = PickingMode.Ignore };
            IStyle s = _caption.style;
            s.backgroundColor = new Color(0.06f, 0.09f, 0.16f, 0.9f);
            s.color = Color.white;
            s.fontSize = 24;
            s.unityFontStyleAndWeight = FontStyle.Bold;
            s.paddingLeft = s.paddingRight = 26;
            s.paddingTop = s.paddingBottom = 12;
            s.borderTopLeftRadius = s.borderTopRightRadius = s.borderBottomLeftRadius = s.borderBottomRightRadius = 12;
            s.maxWidth = 1500;
            s.whiteSpace = WhiteSpace.Normal;
            s.unityTextAlign = TextAnchor.MiddleCenter;
            _captionHost.Add(_caption);
            _app.Ui.GetComponent<UIDocument>().rootVisualElement.Add(_captionHost);
        }

        private void SetCaption(string text)
        {
            _caption.text = text ?? string.Empty;
            _captionHost.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
            _captionHost.BringToFront();
        }

        private IEnumerator Hold(double seconds, string caption, string still)
        {
            SetCaption(caption);
            float settle = Time.realtimeSinceStartup + 0.4f;
            while (Time.realtimeSinceStartup < settle) yield return null;
            for (int i = 0; i < 3; i++) yield return null;
            Grab(seconds, still);
        }

        private IEnumerator Orbit(double seconds, float yawDelta, float zoom, string caption)
        {
            SetCaption(caption);
            _rig.ResetView();
            for (int i = 0; i < 3; i++) yield return null;
            float yaw0 = _rig.Yaw, size0 = _rig.OrthoSize;
            int frames = Mathf.RoundToInt((float)(seconds / FrameSeconds));
            for (int k = 0; k < frames; k++)
            {
                float t = k / (float)(frames - 1);
                float e = t * t * (3f - 2f * t);
                float z = 1f + (zoom - 1f) * Mathf.Sin(Mathf.PI * t);
                _rig.SetView(yaw0 + yawDelta * e, size0 * z);
                yield return null;
                Grab(FrameSeconds, null);
            }
            _rig.ResetView();
        }

        private void Grab(double seconds, string still)
        {
            Read(_worldRt, _worldTex);
            Read(_uiRt, _uiTex);
            Color32[] w = _worldTex.GetPixels32(), u = _uiTex.GetPixels32();
            for (int i = 0; i < w.Length; i++)
            {
                float keep = 1f - u[i].a / 255f;
                w[i] = new Color32(Blend(u[i].r, w[i].r, keep), Blend(u[i].g, w[i].g, keep), Blend(u[i].b, w[i].b, keep), 255);
            }
            _worldTex.SetPixels32(w);
            _worldTex.Apply();
            string file = $"frame_{_frame++:D5}.jpg";
            File.WriteAllBytes(Path.Combine(FrameDir, file), _worldTex.EncodeToJPG(93));
            if (still != null) File.WriteAllBytes(Path.Combine(StillDir, still + ".png"), _worldTex.EncodeToPNG());
            _shots.Add(new KeyValuePair<string, double>(file, seconds));
        }

        private byte Blend(byte ui, byte world, float keep)
        {
            float linear = _toLinear[ui] + _toLinear[world] * keep;
            return _toSrgb[Mathf.Clamp(Mathf.RoundToInt(linear * 4095f), 0, 4095)];
        }

        private static void Read(RenderTexture rt, Texture2D into)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = rt;
            into.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            into.Apply();
            RenderTexture.active = previous;
        }

        private double Total()
        {
            double t = 0;
            foreach (var s in _shots) t += s.Value;
            return t;
        }

        private void WriteConcatList()
        {
            var sb = new StringBuilder();
            foreach (var s in _shots)
                sb.Append("file '").Append(s.Key).Append("'\nduration ").Append(s.Value.ToString("0.#####", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("file '").Append(_shots[_shots.Count - 1].Key).Append("'\n");
            File.WriteAllText(Path.Combine(FrameDir, "concat.txt"), sb.ToString());
        }
    }
}
