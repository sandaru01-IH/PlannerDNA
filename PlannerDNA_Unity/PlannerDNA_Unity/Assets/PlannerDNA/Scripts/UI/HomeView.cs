using System;
using System.Globalization;
using PlannerDNA.App;
using PlannerDNA.Core;
using PlannerDNA.Generation;
using PlannerDNA.Storage;
using UnityEngine.UIElements;

namespace PlannerDNA.UI
{
    /// <summary>Home / New Challenge screen: demo seed, custom or random seed, resume, about the data.</summary>
    public sealed class HomeView
    {
        private const string AboutText =
            "Everything in PlannerDNA is synthetic and generated on this computer. A seed (an integer) deterministically " +
            "creates a 20 x 20 grid neighbourhood with 40 m cells: fixed roads, residential and commercial parcels, vacant " +
            "sites, protected civic facilities and one or two small parks. The same seed and generator version always give " +
            "the same city.\n\n" +
            "Metrics are transparent proxies: housing capacity units (floors x 4), park share of non-road land, capacity-weighted " +
            "park access within a 400 m road-network walk, and fictional intervention credits. They are not traffic, price, " +
            "population, health, carbon or temperature models.\n\n" +
            "Your explicit A/B choices are stored as JSON files on this computer. If you run the optional local backend, they " +
            "are also copied into its database on this same computer (127.0.0.1). No data leaves the machine and no account " +
            "is needed.\n\n" +
            "A preference model exists only after you press \"Train my model\" in the Learning Lab. It is a small logistic " +
            "model fitted by the local backend to your own A/B choices: a statistical summary of associations in synthetic " +
            "scenarios, not an understanding of planning and not validated until a later blinded evaluation.";

        private readonly SandboxUi _ui;
        private readonly PlannerApp _app;
        private readonly TextField _seed;
        private readonly Label _error, _stats, _about, _mission;
        private readonly Button _resume, _close, _tour, _newDemo;
        private readonly VisualElement _screen, _advanced;

        public HomeView(SandboxUi ui, PlannerApp app)
        {
            _ui = ui;
            _app = app;
            _screen = ui.Q<VisualElement>("home-screen");
            _seed = ui.Q<TextField>("seed-field");
            _error = ui.Q<Label>("home-error");
            _stats = ui.Q<Label>("home-stats");
            _about = ui.Q<Label>("about-text");
            _mission = ui.Q<Label>("home-mission");
            _resume = ui.Q<Button>("btn-resume");
            _close = ui.Q<Button>("btn-home-close");
            _tour = ui.Q<Button>("btn-start-tour");
            _newDemo = ui.Q<Button>("btn-new-demo");
            _advanced = ui.Q<VisualElement>("home-advanced");

            _about.text = AboutText;
            ScenarioDefinition def = ScenarioDefinition.GreenSpaceDilemma();
            _mission.text = $"Get more homes within a {def.AccessThresholdMetres} m walk of a park, without losing more than " +
                            $"{(1 - def.MinHousingRatio) * 100:0}% of the homes, spending over {def.BudgetLimit} credits " +
                            $"or building above {def.MaxFloors} floors.";
            _seed.value = GreenSpaceScenarioGenerator.DemoSeed.ToString(CultureInfo.InvariantCulture);

            _tour.clicked += () => StartTour();
            _newDemo.clicked += () => Start(GreenSpaceScenarioGenerator.DemoSeed);
            ui.Q<Button>("btn-home-more").clicked += () => _advanced.EnableInClassList("hidden", !_advanced.ClassListContains("hidden"));
            ui.Q<Button>("btn-new-seed").clicked += StartFromField;
            ui.Q<Button>("btn-random-seed").clicked += () =>
            {
                int seed = new Random().Next(1, 1_000_000);
                _seed.value = seed.ToString(CultureInfo.InvariantCulture);
                ShowError(null);
            };
            _resume.clicked += () =>
            {
                if (app.ResumeLatest(out string message)) { Close(); ui.ShowMessage(message, false); }
                else ShowError(message);
            };
            ui.Q<Button>("btn-about").clicked += () => _about.EnableInClassList("hidden", !_about.ClassListContains("hidden"));
            _close.clicked += Close;
        }

        /// <summary>The "guided tour" button: starts the demo city if none is loaded, then the tour.</summary>
        public bool StartTour()
        {
            if (_app.Session == null && !Start(GreenSpaceScenarioGenerator.DemoSeed)) return false;
            Close();
            return _ui.Tour.Begin();
        }

        private void StartFromField()
        {
            if (!int.TryParse(_seed.value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int seed))
            {
                ShowError("The seed must be a whole number, for example 42.");
                return;
            }
            Start(seed);
        }

        public bool Start(int seed)
        {
            if (!_app.NewChallenge(seed, out string error))
            {
                ShowError(error);
                return false;
            }
            Close();
            _ui.ShowMessage($"New challenge: seed {seed}. Select a parcel to begin.", false);
            return true;
        }

        private void Close()
        {
            ShowError(null);
            _ui.CloseOverlay(_screen);
        }

        private void ShowError(string message)
        {
            _error.text = message ?? string.Empty;
            _error.EnableInClassList("hidden", string.IsNullOrEmpty(message));
        }

        public void Refresh()
        {
            bool hasSession = _app.Session != null;
            _resume.EnableInClassList("hidden", !_app.Store.HasLatestGame);
            _close.EnableInClassList("hidden", !hasSession);
            _tour.text = hasSession ? "Take the guided tour" : "Start with a guided tour";
            _newDemo.text = hasSession ? "Start a fresh demo city" : "Start without the tour";
            try
            {
                PreferenceStats s = _app.Store.ComputeStats();
                _stats.text = s.unique_comparisons == 0
                    ? "Everything stays on this computer."
                    : $"{s.unique_comparisons} comparison{(s.unique_comparisons == 1 ? "" : "s")} saved on this computer.";
            }
            catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException)
            {
                _stats.text = "Could not read local decisions: " + e.Message;
            }
        }
    }
}
