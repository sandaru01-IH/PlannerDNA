using System;
using System.Collections.Generic;
using System.IO;
using PlannerDNA.Commands;
using PlannerDNA.Core;
using PlannerDNA.Evaluation;
using PlannerDNA.Generation;
using PlannerDNA.Metrics;
using PlannerDNA.Networking;
using PlannerDNA.Rendering;
using PlannerDNA.Storage;
using PlannerDNA.Suggestions;
using PlannerDNA.UI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PlannerDNA.App
{
    /// <summary>
    /// Persistent world controller. Owns the current PlanningSession and connects it to the
    /// renderer, input, local storage and UI. Planning logic lives in PlanningSession, not here.
    /// </summary>
    public sealed class PlannerApp : MonoBehaviour
    {
        [SerializeField] private GridRenderer grid;
        [SerializeField] private CameraRig cameraRig;
        [SerializeField] private ParcelPicker picker;
        [SerializeField] private SandboxUi ui;
        [SerializeField] private HealthMonitor health;
        [SerializeField] private SyncService sync;

        /// <summary>Tests point storage at a temporary folder by setting this before the scene loads.</summary>
        public static string StoreRootOverride;
        /// <summary>Tests point the backend at a throwaway server by setting this before the scene loads.</summary>
        public static string BackendUrlOverride;

        public PlanningSession Session { get; private set; }
        public LocalDecisionStore Store { get; private set; }
        public OutboundQueue Outbox { get; private set; }
        public SyncService Sync => sync;
        public LearningClient Learning { get; private set; }
        public GridRenderer Grid => grid;
        public CameraRig CameraRig => cameraRig;
        public ParcelPicker Picker => picker;
        public SandboxUi Ui => ui;
        public HealthMonitor Health => health;

        public int SelectedRow { get; private set; } = -1;
        public int SelectedCol { get; private set; } = -1;
        public bool HasSelection => Session != null && SelectedRow >= 0;
        /// <summary>Non-null while an alternative is shown read-only in the 3D view.</summary>
        public AlternativeSlot? PreviewSlot { get; private set; }

        public event Action SessionChanged;
        public event Action SelectionChanged;

        private void Awake()
        {
            if (grid == null) grid = FindAnyObjectByType<GridRenderer>();
            if (cameraRig == null) cameraRig = FindAnyObjectByType<CameraRig>();
            if (picker == null) picker = FindAnyObjectByType<ParcelPicker>();
            if (ui == null) ui = FindAnyObjectByType<SandboxUi>();
            if (health == null) health = FindAnyObjectByType<HealthMonitor>();
            if (sync == null) sync = FindAnyObjectByType<SyncService>();
            if (health != null && !string.IsNullOrEmpty(BackendUrlOverride)) health.SetBaseUrl(BackendUrlOverride);
            Store = new LocalDecisionStore(string.IsNullOrEmpty(StoreRootOverride) ? LocalDecisionStore.DefaultRoot : StoreRootOverride);
            Outbox = new OutboundQueue(Store.RootDirectory);
            if (sync != null) sync.Initialize(Outbox, health);
            Learning = new LearningClient(this, health);
        }

        private void Start()
        {
            if (picker != null)
            {
                picker.TargetCamera = cameraRig != null ? cameraRig.Camera : Camera.main;
                picker.IsPointerOverUi = IsPointerBlocked;
                picker.CellClicked += (r, c) => Select(r, c);
                picker.CellHovered += (r, c) => { if (grid != null) grid.SetHover(r, c); ui?.ShowHover(r, c); };
            }
            if (cameraRig != null && ui != null)
            {
                cameraRig.IsPointerOverUi = IsPointerBlocked;
                cameraRig.IsTextInputFocused = ui.IsTextInputFocused;
                cameraRig.ViewportRect = ui.ViewportScreenRect;
            }
            if (health != null && ui != null) health.Changed += ui.RefreshSync;
            if (sync != null && ui != null) sync.Changed += ui.RefreshSync;

            if (ui != null)
            {
                ui.Bind(this);
                ui.ShowHome();
            }
        }

        private bool IsPointerBlocked(Vector2 screenPosition)
        {
            if (ui == null) return false;
            return ui.IsOverlayOpen || ui.IsPointerOverUi(screenPosition);
        }

        // ---- Session lifecycle ----

        public bool NewChallenge(int seed, out string error)
        {
            error = null;
            PlanningSession session;
            try
            {
                session = PlanningSession.Start(seed);
            }
            catch (GenerationException e)
            {
                error = e.Message;
                return false;
            }
            AttachSession(session);
            return true;
        }

        private void AttachSession(PlanningSession session)
        {
            DetachSession();
            Session = session;
            Session.Changed += OnSessionChanged;
            Session.EventLogged += OnEventLogged;
            DateTime now = DateTime.UtcNow;
            Enqueue(OutboxKinds.Scenario, session.Baseline.ScenarioId, JsonUtility.ToJson(SyncPayloads.Scenario(session, now)));
            Enqueue(OutboxKinds.Session, session.SessionId, JsonUtility.ToJson(SyncPayloads.Session(session, now)));
            PreviewSlot = null;
            SelectedRow = SelectedCol = -1;
            if (grid != null)
            {
                RenderCurrent();
                grid.SetSelection(-1, -1);
                if (cameraRig != null) cameraRig.Frame(grid.WorldBounds());
            }
            SessionChanged?.Invoke();
            SelectionChanged?.Invoke();
        }

        private void DetachSession()
        {
            if (Session == null) return;
            Session.Changed -= OnSessionChanged;
            Session.EventLogged -= OnEventLogged;
            Session = null;
        }

        /// <summary>Ends the current session (nothing is deleted) and returns to an empty state.</summary>
        public void CloseSession()
        {
            DetachSession();
            PreviewSlot = null;
            SelectedRow = SelectedCol = -1;
            if (grid != null) grid.SetSelection(-1, -1);
            SessionChanged?.Invoke();
            SelectionChanged?.Invoke();
        }

        private void OnEventLogged(TimelineEntryDto entry)
        {
            try { Store.AppendEvent(entry); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                ui?.ShowMessage("Could not write the local event log: " + e.Message, true);
            }
            Enqueue(OutboxKinds.Event, entry.event_id, JsonUtility.ToJson(entry));
        }

        /// <summary>Queues a record for the backend. Failure to queue never blocks planning; it is reported.</summary>
        private void Enqueue(string kind, string recordId, string json)
        {
            try { Outbox.Enqueue(kind, recordId, json); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
            {
                ui?.ShowMessage($"Could not queue {kind} for the backend (it is still saved locally): {e.Message}", true);
            }
        }

        private void OnSessionChanged()
        {
            RenderCurrent();
            SessionChanged?.Invoke();
            SelectionChanged?.Invoke();
        }

        public CityState DisplayedState
        {
            get
            {
                if (Session == null) return null;
                if (PreviewSlot == AlternativeSlot.A && Session.AlternativeA != null) return Session.AlternativeA.State;
                if (PreviewSlot == AlternativeSlot.B && Session.AlternativeB != null) return Session.AlternativeB.State;
                return Session.Current;
            }
        }

        public CityMetrics DisplayedMetrics
        {
            get
            {
                if (Session == null) return null;
                if (PreviewSlot == AlternativeSlot.A && Session.AlternativeA != null) return Session.AlternativeA.Metrics;
                if (PreviewSlot == AlternativeSlot.B && Session.AlternativeB != null) return Session.AlternativeB.Metrics;
                return Session.CurrentMetrics;
            }
        }

        private void RenderCurrent()
        {
            if (grid == null || Session == null) return;
            grid.Render(DisplayedState, Session.Baseline, DisplayedMetrics, Session.Scenario.AccessThresholdMetres);
        }

        // ---- Selection and edits ----

        public void Select(int row, int col)
        {
            if (Session == null || !Session.Current.InBounds(row, col)) row = col = -1;
            SelectedRow = row;
            SelectedCol = col;
            if (grid != null) grid.SetSelection(row, col);
            SelectionChanged?.Invoke();
        }

        public void ClearSelection() => Select(-1, -1);

        public ValidationResult PreviewTool(EditAction action)
        {
            if (Session == null) return ValidationResult.Reject("Start a challenge first.");
            if (PreviewSlot.HasValue) return ValidationResult.Reject($"Previewing Alternative {PreviewSlot}. Return to your design to edit.");
            if (!HasSelection) return ValidationResult.Reject("Select a parcel first (left-click it in the 3D view).");
            return Session.Preview(action, SelectedRow, SelectedCol);
        }

        public CommandOutcome ApplyTool(EditAction action, InputSource source)
        {
            ValidationResult preview = PreviewTool(action);
            if (!preview.Ok && (Session == null || PreviewSlot.HasValue || !HasSelection))
                return Report(new CommandOutcome(false, preview.Reason, null));
            return Report(Session.Execute(action, SelectedRow, SelectedCol, source));
        }

        public CommandOutcome Undo(InputSource source) => Session == null ? default : Report(EnsureEditing() ?? Session.Undo(source));
        public CommandOutcome Redo(InputSource source) => Session == null ? default : Report(EnsureEditing() ?? Session.Redo(source));
        public CommandOutcome ResetToBaseline(InputSource source) => Session == null ? default : Report(EnsureEditing() ?? Session.RestoreBaseline(source));

        private CommandOutcome? EnsureEditing()
        {
            if (!PreviewSlot.HasValue) return null;
            return new CommandOutcome(false, $"Previewing Alternative {PreviewSlot}. Return to your design to edit.", null);
        }

        private CommandOutcome Report(CommandOutcome outcome)
        {
            if (!string.IsNullOrEmpty(outcome.Message)) ui?.ShowMessage(outcome.Message, !outcome.Accepted);
            return outcome;
        }

        public void FocusSelection()
        {
            if (!HasSelection || cameraRig == null || grid == null) return;
            cameraRig.Focus(grid.CellCentre(SelectedRow, SelectedCol));
        }

        public bool AccessOverlay => grid != null && grid.AccessOverlay;

        public void SetAccessOverlay(bool enabled)
        {
            if (grid != null) grid.SetAccessOverlay(enabled);
            if (ui != null && ui.IsBound) ui.Toolbar.SyncAccessToggle();
        }

        // ---- Alternatives and decisions ----

        public AlternativeSnapshot SaveAlternative(AlternativeSlot slot)
        {
            if (Session == null) return null;
            if (PreviewSlot.HasValue) EndPreview();
            AlternativeSnapshot snapshot = Session.SaveAlternative(slot);
            AlternativeDto dto = snapshot.ToDto();
            Enqueue(OutboxKinds.Alternative, dto.alternative_id, JsonUtility.ToJson(dto));
            try
            {
                Store.SaveAlternative(dto);
                ui?.ShowMessage($"Alternative {slot} saved" + (snapshot.Feasible ? " (feasible)." : " - it violates constraints, so it is not a feasible candidate."), !snapshot.Feasible);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                ui?.ShowMessage($"Alternative {slot} kept in memory but could not be written to disk: {e.Message}", true);
            }
            return snapshot;
        }

        public void BeginPreview(AlternativeSlot slot)
        {
            if (Session == null) return;
            if ((slot == AlternativeSlot.A ? Session.AlternativeA : Session.AlternativeB) == null) return;
            PreviewSlot = slot;
            RenderCurrent();
            SessionChanged?.Invoke();
        }

        public void EndPreview()
        {
            if (!PreviewSlot.HasValue) return;
            PreviewSlot = null;
            RenderCurrent();
            SessionChanged?.Invoke();
        }

        public StoreResult SubmitPreference(string comparisonId, string choice, string reason)
        {
            if (Session != null && ReservedScenarioIds.Contains(Session.Baseline.ScenarioId))
                return new StoreResult(false, false,
                    "This area is reserved for the open blinded test, so practice choices cannot be recorded on it. Answer it in the blinded test instead.", null);
            return Persist(Session.CreatePreference(comparisonId, choice, reason));
        }

        private StoreResult Persist(PreferenceDto pref)
        {
            StoreResult result;
            try { result = Store.SavePreference(pref); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return new StoreResult(false, false, "Could not write the decision to disk: " + e.Message, null);
            }
            if (result.Saved)
            {
                Enqueue(OutboxKinds.Preference, pref.comparison_id, JsonUtility.ToJson(pref));
                Session.LogDecision(pref);
            }
            return result;
        }

        /// <summary>True once the backend has acknowledged (or rejected) every listed record of this kind.</summary>
        public bool IsDelivered(string kind, params string[] recordIds)
        {
            foreach (string id in recordIds)
                if (string.IsNullOrEmpty(id) || !Outbox.Contains(kind, id) || Outbox.IsPending(kind, id)) return false;
            return true;
        }

        /// <summary>The backend's reason for refusing a record, or null if it was not refused.</summary>
        public string RejectionReason(string kind, string recordId)
        {
            foreach (OutboundMessageDto m in Outbox.RejectedMessages())
                if (m.kind == kind && m.record_id == recordId) return string.IsNullOrEmpty(m.last_error) ? $"HTTP {m.http_status}" : m.last_error;
            return null;
        }

        // ---- Blinded evaluation (Phase 4) ----

        /// <summary>Scenarios sealed by the open blinded evaluation; practice choices on them are refused locally too.</summary>
        public readonly HashSet<string> ReservedScenarioIds = new HashSet<string>();

        public void SetReservedScenarios(IEnumerable<string> scenarioIds)
        {
            ReservedScenarioIds.Clear();
            if (scenarioIds != null) foreach (string id in scenarioIds) ReservedScenarioIds.Add(id);
        }

        /// <summary>Opens the given scenario unless it is already the current one (keeps the session for consecutive pairs).</summary>
        public bool EnsureScenario(int seed, out string error)
        {
            error = null;
            if (Session != null && Session.Baseline.Seed == seed && Session.Baseline.GeneratorVersion == Versions.GeneratorVersion) return true;
            if (Session != null && !Session.IsAtBaseline && SaveDesign(out string saved)) ui?.ShowMessage("Your design was saved first. " + saved, false);
            return NewChallenge(seed, out error);
        }

        /// <summary>Loads a blinded pair into slots A/B, saves both locally and queues them for the backend.</summary>
        public bool LoadEvaluationPair(EvaluationPair pair, int candidateSeed, out string message)
        {
            message = null;
            if (Session == null || pair == null) { message = "No evaluation scenario is open."; return false; }
            if (PreviewSlot.HasValue) EndPreview();
            Session.LoadEvaluationPair(pair.A.State, pair.B.State,
                $"Blinded pair from candidates {pair.A.CandidateId} and {pair.B.CandidateId} ({EvaluationPairPicker.PairRule}, seed {candidateSeed})");
            foreach (AlternativeSnapshot snapshot in new[] { Session.AlternativeA, Session.AlternativeB })
            {
                AlternativeDto dto = snapshot.ToDto();
                Enqueue(OutboxKinds.Alternative, dto.alternative_id, JsonUtility.ToJson(dto));
                try { Store.SaveAlternative(dto); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    message = "The pair could not be written to disk: " + e.Message;
                    return false;
                }
            }
            return true;
        }

        /// <summary>Puts an earlier blinded pair back into slots A/B (for a repeat, or after a restart while a pair awaits a choice).</summary>
        public bool RestoreEvaluationPair(string alternativeAId, string alternativeBId, out string message)
        {
            message = null;
            if (Session == null) { message = "No evaluation scenario is open."; return false; }
            if (PreviewSlot.HasValue) EndPreview();
            foreach (string id in new[] { alternativeAId, alternativeBId })
            {
                AlternativeDto dto = Store.LoadAlternative(id);
                if (dto == null) { message = $"Design {id} of this pair is not saved on this computer, so it cannot be shown again."; return false; }
                AlternativeSnapshot snapshot = AlternativeSnapshot.FromDto(dto, Session.Baseline, Session.BaselineMetrics, Session.Scenario, out string problem);
                if (snapshot == null) { message = $"Design {id} could not be restored: {problem}"; return false; }
                Session.SetAlternative(snapshot);
            }
            if (Session.AlternativeA.AlternativeId != alternativeAId || Session.AlternativeB.AlternativeId != alternativeBId)
            {
                message = "The stored designs are not in the registered A/B slots.";
                return false;
            }
            return true;
        }

        public StoreResult SubmitEvaluationPreference(string comparisonId, string choice, EvaluationTrialDto trial, string reason = "")
        {
            if (Session == null || trial == null) return new StoreResult(false, false, "No blinded pair is open.", null);
            if (Session.AlternativeA?.AlternativeId != trial.alternative_a_id || Session.AlternativeB?.AlternativeId != trial.alternative_b_id)
                return new StoreResult(false, false, "The designs on screen are not the registered pair.", null);
            PreferenceDto pref = Session.CreateEvaluationPreference(comparisonId, choice, reason ?? string.Empty, trial.trial_id,
                trial.model_version_locked, trial.shown_left_alternative_id);
            return Persist(pref);
        }

        // ---- Suggestions (Phase 3) ----

        /// <summary>Seeded rule-based candidates for the current scenario; feasibility is checked here, ranking by the backend.</summary>
        public CandidateSet GenerateCandidates(int seed)
        {
            if (Session == null) return null;
            return CandidateGenerator.Generate(Session.Baseline, Session.Scenario, Session.BaselineMetrics, seed);
        }

        /// <summary>Loads a suggested design as an undoable edit, logged with input source "assistant_generated".</summary>
        public CommandOutcome ApplySuggestion(CandidateDesign candidate, string modelVersion)
        {
            if (Session == null || candidate == null) return Report(new CommandOutcome(false, "Start a challenge first.", null));
            if (PreviewSlot.HasValue) EndPreview();
            string note = $"Applied suggestion {candidate.CandidateId} ranked by model {modelVersion}";
            CommandOutcome outcome = Session.ApplyLoadedDesign(candidate.State, InputSource.AssistantGenerated, note);
            return Report(outcome.Accepted
                ? new CommandOutcome(true, $"Suggestion {candidate.CandidateId} applied as your current design. Undo (Ctrl+Z) restores your previous design.", outcome.EventId)
                : outcome);
        }

        // ---- Export and deletion ----

        public bool ExportLocal(out string message)
        {
            try
            {
                message = "Local export saved: " + Store.WriteExport(DateTime.UtcNow);
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                message = "Local export failed: " + e.Message;
                return false;
            }
        }

        /// <summary>
        /// Permanently deletes every local PlannerDNA record (decisions, alternatives, saves, event logs,
        /// outbox, exports) after an optional backup. The current session is closed first so nothing
        /// is written again afterwards.
        /// </summary>
        public bool DeleteLocalData(bool backup, out string message)
        {
            CloseSession();
            string backupPath = null;
            try
            {
                if (backup) backupPath = Store.Backup(DateTime.UtcNow);
                Store.DeleteAllData();
                Outbox.DeleteAll();
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Outbox.Reload();
                message = "Deletion did not complete: " + e.Message + (backupPath != null ? " Backup: " + backupPath : "");
                return false;
            }
            message = "All local PlannerDNA data was deleted." + (backupPath != null ? " Backup kept at " + backupPath : " No backup was made.");
            return true;
        }

        // ---- Save / load ----

        public bool SaveDesign(out string message)
        {
            message = null;
            if (Session == null) { message = "Nothing to save yet."; return false; }
            var save = new SaveGameDto
            {
                schema_version = Versions.SchemaVersion,
                app_version = Versions.AppVersion,
                saved_utc = DateTime.UtcNow.ToString("o"),
                session_id = Session.SessionId,
                scenario_id = Session.Baseline.ScenarioId,
                seed = Session.Baseline.Seed,
                generator_version = Session.Baseline.GeneratorVersion,
                metric_version = Session.Baseline.MetricVersion,
                baseline_hash = Session.BaselineHash,
                alternative_a_id = Session.AlternativeA?.AlternativeId ?? string.Empty,
                alternative_b_id = Session.AlternativeB?.AlternativeId ?? string.Empty,
                current = CitySerializer.ToDto(Session.Current),
            };
            try
            {
                string path = Store.SaveGame(save);
                message = "Design saved locally: " + path;
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                message = "Save failed: " + e.Message;
                return false;
            }
        }

        public bool LoadDesign(out string message)
        {
            if (Session == null) { message = "Start a challenge first."; return false; }
            SaveGameDto save = Store.LoadGame(Session.Baseline.ScenarioId);
            if (save == null) { message = $"No saved design for {Session.Baseline.ScenarioId} yet."; return false; }
            return ApplySave(save, out message);
        }

        public bool ResumeLatest(out string message)
        {
            SaveGameDto save = Store.LoadLatestGame();
            if (save == null) { message = "No saved design found."; return false; }
            if (save.generator_version != Versions.GeneratorVersion)
            {
                message = $"The saved design used generator {save.generator_version}; this build uses {Versions.GeneratorVersion}.";
                return false;
            }
            if (!NewChallenge(save.seed, out string error)) { message = error; return false; }
            return ApplySave(save, out message);
        }

        private bool ApplySave(SaveGameDto save, out string message)
        {
            if (save.baseline_hash != Session.BaselineHash)
            {
                message = "The saved design was made from a different baseline (generator or seed mismatch).";
                return false;
            }
            CityState design;
            try { design = CitySerializer.FromDto(save.current); }
            catch (Exception e) when (e is FormatException || e is ArgumentException)
            {
                message = "The save file is invalid: " + e.Message;
                return false;
            }

            if (PreviewSlot.HasValue) EndPreview();
            string note = "";
            if (!design.ContentEquals(Session.Current))
            {
                CommandOutcome outcome = Session.ApplyLoadedDesign(design, InputSource.System);
                if (!outcome.Accepted) { message = outcome.Message; return false; }
            }
            note += RestoreAlternative(save.alternative_a_id);
            note += RestoreAlternative(save.alternative_b_id);
            message = $"Loaded saved design for {save.scenario_id} (saved {save.saved_utc}).{note}";
            ui?.ShowMessage(message, false);
            return true;
        }

        private string RestoreAlternative(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            AlternativeDto dto = Store.LoadAlternative(id);
            AlternativeSnapshot snapshot = AlternativeSnapshot.FromDto(dto, Session.Baseline, Session.BaselineMetrics, Session.Scenario, out string problem);
            if (snapshot == null) return $" Alternative {id} not restored: {problem}";
            Session.SetAlternative(snapshot);
            return $" Restored Alternative {snapshot.Slot}.";
        }

        // ---- Keyboard shortcuts ----

        private void Update()
        {
            Keyboard k = Keyboard.current;
            if (k == null || ui == null) return;
            if (ui.IsTextInputFocused()) return;

            if (k.escapeKey.wasPressedThisFrame)
            {
                if (ui.Tour != null && ui.Tour.IsActive) ui.Tour.End();
                else if (ui.IsOverlayOpen) ui.CloseTopOverlay();
                else if (PreviewSlot.HasValue) ui.ReturnFromPreview();
                else ClearSelection();
                return;
            }
            if (ui.IsOverlayOpen || Session == null) return;

            bool ctrl = k.ctrlKey.isPressed;
            if (ctrl)
            {
                if (k.zKey.wasPressedThisFrame && k.shiftKey.isPressed) Redo(InputSource.Keyboard);
                else if (k.zKey.wasPressedThisFrame) Undo(InputSource.Keyboard);
                else if (k.yKey.wasPressedThisFrame) Redo(InputSource.Keyboard);
                else if (k.sKey.wasPressedThisFrame) { SaveDesign(out string m); ui.ShowMessage(m, false); }
                return;
            }

            if (k.pKey.wasPressedThisFrame) ApplyTool(EditAction.SetPark, InputSource.Keyboard);
            else if (k.rKey.wasPressedThisFrame) ApplyTool(EditAction.SetResidential, InputSource.Keyboard);
            else if (k.cKey.wasPressedThisFrame) ApplyTool(EditAction.SetCommercial, InputSource.Keyboard);
            else if (k.xKey.wasPressedThisFrame || k.deleteKey.wasPressedThisFrame) ApplyTool(EditAction.Demolish, InputSource.Keyboard);
            else if (k.equalsKey.wasPressedThisFrame || k.numpadPlusKey.wasPressedThisFrame || k.rightBracketKey.wasPressedThisFrame)
                ApplyTool(EditAction.RaiseFloors, InputSource.Keyboard);
            else if (k.minusKey.wasPressedThisFrame || k.numpadMinusKey.wasPressedThisFrame || k.leftBracketKey.wasPressedThisFrame)
                ApplyTool(EditAction.LowerFloors, InputSource.Keyboard);
            else if (k.fKey.wasPressedThisFrame) FocusSelection();
        }
    }
}
