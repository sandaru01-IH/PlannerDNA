using System;
using System.Collections.Generic;
using System.Globalization;
using PlannerDNA.App;
using PlannerDNA.Core;
using PlannerDNA.Networking;
using PlannerDNA.Rendering;
using PlannerDNA.Storage;
using UnityEngine;
using UnityEngine.UIElements;

namespace PlannerDNA.UI
{
    /// <summary>
    /// Runtime UI Toolkit controller. Binds PlannerDNA_Main.uxml to the PlannerApp and delegates
    /// each region to a focused view class. Holds no planning state of its own.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class SandboxUi : MonoBehaviour
    {
        private const float MessageSeconds = 7f;
        public const string ThemePrefKey = "PlannerDNA.Theme";
        public static readonly Color LightSky = new Color(0.86f, 0.89f, 0.875f);
        public static readonly Color DarkSky = new Color(0.043f, 0.071f, 0.082f);

        private UIDocument _document;
        private VisualElement _root, _appRoot;
        private PlannerApp _app;
        private readonly HashSet<VisualElement> _transparent = new HashSet<VisualElement>();
        private readonly List<VisualElement> _openOverlays = new List<VisualElement>();

        private Label _scenarioTitle, _scenarioMeta, _sync, _hoverLabel, _messageBar, _scaleLabel, _previewText, _altA, _altB;
        private Label _compareHint, _learnStationLabel, _toolLead;
        private VisualElement _viewport, _previewBanner, _scaleBar, _slotA, _slotB, _accessKey;
        private VisualElement[] _stations, _stationLines;
        private Button _saveA, _saveB, _compareButton, _themeButton;
        private CompassElement _compass;
        private float _messageHideAt = -1f;

        public ToolbarView Toolbar { get; private set; }
        public AnalysisPanel Analysis { get; private set; }
        public TimelineView Timeline { get; private set; }
        public CompareView Compare { get; private set; }
        public HomeView Home { get; private set; }
        public InfoViews Info { get; private set; }
        public LearningLabView Learning { get; private set; }
        public EvaluationView Evaluation { get; private set; }
        public TutorialView Tour { get; private set; }

        /// <summary>Where "Back" leads after a 3D preview, and how the previewed design is named on the banner.</summary>
        private bool _previewFromEvaluation;
        private string _previewName;

        public VisualElement Root => _root;
        public bool IsBound => _app != null;
        public bool IsOverlayOpen => _openOverlays.Count > 0;
        public bool DarkMode { get; private set; }
        /// <summary>0 Explore, 1 Design A and B, 2 Compare. Drives the header journey line.</summary>
        public int JourneyStage { get; private set; }
        public string LastMessage { get; private set; }
        public bool LastMessageWasError { get; private set; }

        public T Q<T>(string name) where T : VisualElement
        {
            T element = _root.Q<T>(name);
            if (element == null) throw new InvalidOperationException($"UI element '{name}' ({typeof(T).Name}) is missing from PlannerDNA_Main.uxml.");
            return element;
        }

        public void Bind(PlannerApp app)
        {
            _app = app;
            _document = GetComponent<UIDocument>();
            _root = _document.rootVisualElement;
            if (_root == null) throw new InvalidOperationException("UIDocument has no root element. Assign PanelSettings and the PlannerDNA_Main.uxml asset.");
            _root.pickingMode = PickingMode.Ignore;

            _appRoot = Q<VisualElement>("app-root");
            _transparent.Clear();
            _transparent.Add(_root);
            if (_root.parent != null) _transparent.Add(_root.parent);
            _transparent.Add(_appRoot);
            _transparent.Add(Q<VisualElement>("body"));
            _viewport = Q<VisualElement>("viewport");
            _transparent.Add(_viewport);

            _scenarioTitle = Q<Label>("scenario-title");
            _scenarioMeta = Q<Label>("scenario-meta");
            _sync = Q<Label>("sync-status");
            _hoverLabel = Q<Label>("hover-label");
            _messageBar = Q<Label>("message-bar");
            _scaleLabel = Q<Label>("scale-label");
            _scaleBar = Q<VisualElement>("scale-bar");
            _previewBanner = Q<VisualElement>("preview-banner");
            _previewText = Q<Label>("preview-text");
            _altA = Q<Label>("alt-a-status");
            _altB = Q<Label>("alt-b-status");
            _slotA = Q<VisualElement>("slot-a");
            _slotB = Q<VisualElement>("slot-b");
            _saveA = Q<Button>("btn-save-a");
            _saveB = Q<Button>("btn-save-b");
            _compareButton = Q<Button>("btn-compare");
            _compareHint = Q<Label>("compare-hint");
            _themeButton = Q<Button>("btn-theme");
            _learnStationLabel = Q<Label>("station-learn-label");
            _toolLead = Q<Label>("tool-lead");
            _accessKey = Q<VisualElement>("access-key");
            _stations = new[] { Q<VisualElement>("station-explore"), Q<VisualElement>("station-design"), Q<VisualElement>("station-compare"), Q<VisualElement>("station-learn") };
            _stationLines = new[] { Q<VisualElement>("line-1"), Q<VisualElement>("line-2"), Q<VisualElement>("line-3") };

            _compass = new CompassElement();
            Q<VisualElement>("compass-host").Add(_compass);
            BuildLegend(Q<VisualElement>("legend-items"));

            Toolbar = new ToolbarView(this, app);
            Analysis = new AnalysisPanel(this, app);
            Timeline = new TimelineView(this, app);
            Compare = new CompareView(this, app);
            Home = new HomeView(this, app);
            Info = new InfoViews(this, app);
            Learning = new LearningLabView(this, app);
            Evaluation = new EvaluationView(this, app);
            Tour = new TutorialView(this, app);

            Q<Button>("btn-home").clicked += ShowHome;
            Q<Button>("btn-learning").clicked += OpenLearningLab;
            Q<Button>("btn-home-learning").clicked += OpenLearningLab;
            Q<Button>("btn-open-learning").clicked += OpenLearningLab;
            Q<Button>("btn-help").clicked += () => OpenOverlay(Q<VisualElement>("help-screen"));
            Q<Button>("btn-data").clicked += () => { Info.RefreshData(); OpenOverlay(Q<VisualElement>("data-screen")); };
            Q<Button>("btn-save").clicked += () => { app.SaveDesign(out string m); ShowMessage(m, false); };
            Q<Button>("btn-load").clicked += () =>
            {
                if (!app.LoadDesign(out string m)) { ShowMessage(m, true); return; }
                CloseOverlay(Q<VisualElement>("data-screen"));
            };
            Q<Button>("btn-preview-back").clicked += ReturnFromPreview;
            _themeButton.clicked += () => SetTheme(!DarkMode, true);
            _saveA.clicked += () => app.SaveAlternative(AlternativeSlot.A);
            _saveB.clicked += () => app.SaveAlternative(AlternativeSlot.B);
            _compareButton.clicked += OpenCompare;
            _stations[2].RegisterCallback<ClickEvent>(_ => { if (_compareButton.enabledSelf) OpenCompare(); });
            _stations[3].RegisterCallback<ClickEvent>(_ => OpenLearningLab());

            SetTheme(PlayerPrefs.GetInt(ThemePrefKey, 0) == 1, false);

            app.SessionChanged += Refresh;
            app.SelectionChanged += RefreshSelection;
            Refresh();
            RefreshSync();
        }

        private static void BuildLegend(VisualElement container)
        {
            void Item(string text, Color color)
            {
                var item = new VisualElement();
                item.AddToClassList("legend-item");
                var swatch = new VisualElement();
                swatch.AddToClassList("legend-swatch");
                swatch.style.backgroundColor = color;
                item.Add(swatch);
                item.Add(UiFormat.Text(text, "legend-text"));
                container.Add(item);
            }
            Item("Homes", PaletteColors.Residential);
            Item("Shops and offices", PaletteColors.Commercial);
            Item("Park", PaletteColors.Park);
            Item("Vacant land", PaletteColors.Vacant);
            Item("Road", PaletteColors.Road);
            Item("School or clinic (fixed)", PaletteColors.Civic);
            Item("You changed it", PaletteColors.Changed);
            Item("Selected", PaletteColors.Accent);
        }

        // ---- Theme ----

        /// <summary>Switches light/dark. <paramref name="persist"/> stores the choice for the next launch.</summary>
        public void SetTheme(bool dark, bool persist)
        {
            DarkMode = dark;
            _appRoot.EnableInClassList("theme-dark", dark);
            _themeButton.text = dark ? "Light mode" : "Dark mode";
            Camera cam = _app.CameraRig != null ? _app.CameraRig.Camera : Camera.main;
            if (cam != null) cam.backgroundColor = dark ? DarkSky : LightSky;
            if (persist)
            {
                PlayerPrefs.SetInt(ThemePrefKey, dark ? 1 : 0);
                PlayerPrefs.Save();
            }
        }

        // ---- Overlays ----

        public void OpenOverlay(VisualElement overlay)
        {
            overlay.RemoveFromClassList("hidden");
            if (!_openOverlays.Contains(overlay)) _openOverlays.Add(overlay);
        }

        public void CloseOverlay(VisualElement overlay)
        {
            overlay.AddToClassList("hidden");
            _openOverlays.Remove(overlay);
        }

        public void CloseAllOverlays()
        {
            foreach (VisualElement overlay in _openOverlays.ToArray()) CloseOverlay(overlay);
        }

        public void CloseTopOverlay()
        {
            if (_openOverlays.Count == 0) return;
            VisualElement top = _openOverlays[_openOverlays.Count - 1];
            if (top.name == "home-screen" && _app.Session == null) return;
            CloseOverlay(top);
        }

        public void ShowHome()
        {
            Tour?.End();
            Home.Refresh();
            OpenOverlay(Q<VisualElement>("home-screen"));
        }

        public void OpenCompare()
        {
            Tour?.End();
            Compare.Open();
            OpenOverlay(Q<VisualElement>("compare-screen"));
        }

        public void OpenLearningLab()
        {
            Tour?.End();
            Learning.Open();
        }

        public void OpenEvaluation()
        {
            Tour?.End();
            Evaluation.Open();
        }

        public void ReturnFromPreview()
        {
            bool toEvaluation = _previewFromEvaluation;
            _previewFromEvaluation = false;
            _previewName = null;
            _app.EndPreview();
            if (toEvaluation) Evaluation.Reopen();
            else OpenCompare();
        }

        public void BeginPreviewFromCompare(AlternativeSlot slot)
        {
            _previewFromEvaluation = false;
            _previewName = null;
            CloseOverlay(Q<VisualElement>("compare-screen"));
            _app.BeginPreview(slot);
        }

        /// <summary>Shows one design of a blinded pair in 3D, named by its screen side rather than its A/B slot.</summary>
        public void BeginPreviewFromEvaluation(AlternativeSlot slot, string name)
        {
            _previewFromEvaluation = true;
            _previewName = name;
            CloseOverlay(Q<VisualElement>("learning-screen"));
            CloseOverlay(Q<VisualElement>("evaluation-screen"));
            _app.BeginPreview(slot);
        }

        // ---- Refresh ----

        public void Refresh()
        {
            if (_app == null) return;
            PlanningSession s = _app.Session;
            if (s == null)
            {
                _scenarioTitle.text = "No challenge loaded";
                _scenarioMeta.text = "Start from the Home screen";
            }
            else
            {
                _scenarioTitle.text = s.Scenario.Title;
                _scenarioMeta.text = $"Imaginary city, seed {s.Baseline.Seed.ToString(CultureInfo.InvariantCulture)}";
            }

            bool previewing = _app.PreviewSlot.HasValue;
            _previewBanner.EnableInClassList("hidden", !previewing);
            if (previewing)
                _previewText.text = $"You are looking at {(_previewName != null ? "the " + _previewName : "Design " + _app.PreviewSlot)} (read-only). The right panel shows its numbers.";

            bool bothSaved = s != null && s.AlternativeA != null && s.AlternativeB != null;
            _saveA.SetEnabled(s != null);
            _saveB.SetEnabled(s != null);
            _saveA.text = s?.AlternativeA != null ? "Replace" : "Save as A";
            _saveB.text = s?.AlternativeB != null ? "Replace" : "Save as B";
            _compareButton.SetEnabled(bothSaved);
            _compareHint.text = s == null ? "" : bothSaved ? "Both designs are ready" : "Save both designs first";
            _altA.text = AltStatus(s?.AlternativeA);
            _altB.text = AltStatus(s?.AlternativeB);
            _slotA.EnableInClassList("ab-slot--saved", s?.AlternativeA != null);
            _slotB.EnableInClassList("ab-slot--saved", s?.AlternativeB != null);
            SyncAccessKey();
            RefreshToolLead();

            Toolbar.Refresh();
            Analysis.Refresh();
            Timeline.Refresh();
            Learning.UpdateButtons();
            RefreshJourney();
        }

        private static string AltStatus(AlternativeSnapshot alt)
        {
            if (alt == null) return "Not saved yet";
            CultureInfo inv = CultureInfo.InvariantCulture;
            return $"Park access {UiFormat.Access(alt.Metrics)}\n{alt.Metrics.HousingUnits.ToString(inv)} homes, {alt.Metrics.SpentCredits.ToString(inv)} credits" +
                   (alt.Feasible ? "" : "\nBreaks a rule");
        }

        private void RefreshJourney()
        {
            PlanningSession s = _app.Session;
            bool anySaved = s != null && (s.AlternativeA != null || s.AlternativeB != null);
            bool bothSaved = s != null && s.AlternativeA != null && s.AlternativeB != null;
            JourneyStage = s == null || (!anySaved && s.IsAtBaseline) ? 0 : !bothSaved ? 1 : 2;

            PreferenceStats stats = Analysis.LastStats;
            bool trained = _app.Health != null && _app.Health.HasModel;
            int choices = stats != null ? stats.training_eligible : 0;
            _learnStationLabel.text = trained ? "Learn" : $"Learn {Math.Min(choices, 10)}/10";

            for (int i = 0; i < 3; i++)
            {
                _stations[i].EnableInClassList("station--done", i < JourneyStage);
                _stations[i].EnableInClassList("station--current", i == JourneyStage);
                _stationLines[i].EnableInClassList("station-line--done", i < JourneyStage);
            }
            _stations[2].EnableInClassList("station--link", bothSaved);
            _stations[3].AddToClassList("station--link");
            _stations[3].EnableInClassList("station--done", trained);
            _stations[3].EnableInClassList("station--current", !trained && choices >= 10);
        }

        private void RefreshSelection()
        {
            RefreshToolLead();
            Toolbar?.Refresh();
            Analysis?.RefreshSelection();
        }

        private void RefreshToolLead()
        {
            PlanningSession s = _app.Session;
            _toolLead.text = s == null ? "Start a challenge first."
                : _app.PreviewSlot.HasValue ? "Editing is paused while you look at a saved design."
                : _app.HasSelection ? $"Parcel {CellIds.Format(_app.SelectedRow, _app.SelectedCol)} is selected."
                : "Click a parcel in the city first.";
        }

        public void SyncAccessKey() => _accessKey.EnableInClassList("hidden", !_app.AccessOverlay);

        public void RefreshSync()
        {
            HealthMonitor health = _app != null ? _app.Health : null;
            _sync.RemoveFromClassList("sync-checking");
            _sync.RemoveFromClassList("sync-online");
            _sync.RemoveFromClassList("sync-offline");
            if (health == null)
            {
                _sync.text = "Backend: not configured";
                _sync.AddToClassList("sync-offline");
                return;
            }
            switch (health.Status)
            {
                case BackendStatus.Online:
                    _sync.text = health.StorageReady ? "Backend: Online" : "Backend: Online (no database)";
                    _sync.AddToClassList("sync-online");
                    break;
                case BackendStatus.Offline:
                    _sync.text = "Backend: Offline, saving locally";
                    _sync.AddToClassList("sync-offline");
                    break;
                default:
                    _sync.text = "Backend: checking";
                    _sync.AddToClassList("sync-checking");
                    break;
            }
            OutboundQueue queue = _app.Outbox;
            if (queue != null)
            {
                if (queue.PendingCount > 0) _sync.text += $" | {queue.PendingCount} to sync";
                if (queue.RejectedCount > 0) _sync.text += $" | {queue.RejectedCount} refused";
            }
            Info?.RefreshSyncSection();
            Analysis?.RefreshLearning();
            if (Analysis != null) RefreshJourney();
            Learning?.OnHealthChanged();
            Evaluation?.OnHealthChanged();
        }

        public void ShowMessage(string text, bool isError)
        {
            if (_messageBar == null || string.IsNullOrEmpty(text)) return;
            LastMessage = text;
            LastMessageWasError = isError;
            _messageBar.text = text;
            _messageBar.RemoveFromClassList("hidden");
            _messageBar.EnableInClassList("message-bar--error", isError);
            _messageHideAt = Time.unscaledTime + MessageSeconds;
        }

        public void ShowHover(int row, int col)
        {
            PlanningSession s = _app.Session;
            CityState state = _app.DisplayedState;
            if (s == null || state == null || !state.InBounds(row, col))
            {
                _hoverLabel.AddToClassList("hidden");
                return;
            }
            CellData cell = state.Get(row, col);
            string text = $"{cell.Id}: {UiFormat.CellSummary(cell)}";
            if (cell.Use == LandUse.Residential)
                text += ". Walk to park: " + UiFormat.ParkWalk(_app.DisplayedMetrics.ParkDistanceMetres[state.IndexOf(row, col)], s.Scenario.AccessThresholdMetres);
            _hoverLabel.text = text;
            _hoverLabel.RemoveFromClassList("hidden");
        }

        // ---- Input helpers used by camera, picker and the tour ----

        public bool IsTextInputFocused()
        {
            Focusable focused = _root?.panel?.focusController?.focusedElement;
            for (var e = focused as VisualElement; e != null; e = e.parent)
                if (e is TextField) return true;
            return false;
        }

        public bool IsPointerOverUi(Vector2 screenPosition)
        {
            IPanel panel = _root?.panel;
            if (panel == null) return false;
            Vector2 panelPos = RuntimePanelUtils.ScreenToPanel(panel, new Vector2(screenPosition.x, Screen.height - screenPosition.y));
            VisualElement picked = panel.Pick(panelPos);
            return picked != null && !_transparent.Contains(picked);
        }

        /// <summary>A world point in panel coordinates (origin top-left), or null when behind the camera.</summary>
        public Vector2? WorldToPanel(Vector3 world)
        {
            Camera cam = _app.CameraRig != null ? _app.CameraRig.Camera : Camera.main;
            if (_root == null || cam == null) return null;
            Rect rootRect = _root.worldBound;
            if (rootRect.width <= 1 || rootRect.height <= 1) return null;
            // Viewport coordinates stay correct when the camera renders into a texture of another size.
            Vector3 v = cam.WorldToViewportPoint(world);
            if (v.z <= 0) return null;
            return new Vector2(rootRect.xMin + v.x * rootRect.width, rootRect.yMin + (1f - v.y) * rootRect.height);
        }

        /// <summary>The 3D viewport area in screen pixels (origin bottom-left), or null before layout.</summary>
        public Rect? ViewportScreenRect()
        {
            if (_viewport == null || _root == null) return null;
            Rect rootRect = _root.worldBound;
            Rect view = _viewport.worldBound;
            if (rootRect.width <= 1 || rootRect.height <= 1 || float.IsNaN(view.width) || view.width <= 1 || view.height <= 1) return null;
            float sx = Screen.width / rootRect.width, sy = Screen.height / rootRect.height;
            return new Rect(view.xMin * sx, Screen.height - view.yMax * sy, view.width * sx, view.height * sy);
        }

        private void Update()
        {
            if (_app == null) return;
            if (_messageHideAt > 0 && Time.unscaledTime > _messageHideAt)
            {
                _messageBar.AddToClassList("hidden");
                _messageHideAt = -1f;
            }
            UpdateCompassAndScale();
            Tour?.Tick();
        }

        private void UpdateCompassAndScale()
        {
            CameraRig rig = _app.CameraRig;
            GridRenderer grid = _app.Grid;
            if (rig == null || grid == null || _root == null || Screen.height <= 0) return;
            _compass.SetCameraYaw(rig.Yaw);

            int cellMetres = _app.Session != null ? _app.Session.Baseline.CellMetres : 40;
            float metresPerScreenPixel = 2f * rig.OrthoSize / Screen.height * (cellMetres / grid.CellSize);
            float rootWidth = _root.worldBound.width;
            if (rootWidth <= 1 || metresPerScreenPixel <= 0) return;
            float panelPerScreenPixel = rootWidth / Screen.width;

            int[] candidates = { 40, 80, 120, 200, 400, 800 };
            int chosen = candidates[candidates.Length - 1];
            foreach (int c in candidates)
            {
                if (c / metresPerScreenPixel * panelPerScreenPixel >= 50f) { chosen = c; break; }
            }
            float width = chosen / metresPerScreenPixel * panelPerScreenPixel;
            _scaleBar.style.width = Mathf.Clamp(width, 10f, 180f);
            _scaleLabel.text = $"{chosen} m (left to right)";
        }
    }
}
