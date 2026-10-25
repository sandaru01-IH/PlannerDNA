using System;
using System.Collections.Generic;
using System.IO;
using PlannerDNA.App;
using PlannerDNA.Core;
using PlannerDNA.Metrics;
using PlannerDNA.Networking;
using PlannerDNA.Storage;
using UnityEngine;
using UnityEngine.UIElements;

namespace PlannerDNA.UI
{
    /// <summary>Help / glossary, Data &amp; settings, and the confirmed-deletion overlay.</summary>
    public sealed class InfoViews
    {
        public const string DeleteConfirmWord = "DELETE";

        private readonly SandboxUi _ui;
        private readonly PlannerApp _app;
        private readonly ScrollView _data;
        private readonly VisualElement _deleteScreen;
        private readonly Toggle _deleteBackup, _deleteBackend;
        private readonly TextField _deleteField;
        private readonly Label _deleteWarning, _deleteResult;
        private readonly Button _deleteConfirm;
        private Label _syncLabel, _syncDetail, _serverStats, _actionResult;

        public InfoViews(SandboxUi ui, PlannerApp app)
        {
            _ui = ui;
            _app = app;
            ScrollView help = ui.Q<ScrollView>("help-content");
            _data = ui.Q<ScrollView>("data-content");

            help.Add(UiFormat.Text("Moving around the city", "section-title"));
            AddShortcut(help, "Left-click", "Select a parcel");
            AddShortcut(help, "Right-drag  or  Q / E", "Rotate the view");
            AddShortcut(help, "Middle-drag  or  W A S D", "Move the view");
            AddShortcut(help, "Mouse wheel", "Zoom in and out");
            AddShortcut(help, "F", "Zoom to the selected parcel");
            AddShortcut(help, "Home", "Reset the view");
            AddShortcut(help, "Esc", "Close a window, leave a preview, or deselect");
            help.Add(UiFormat.Text("Changing the selected parcel", "section-title section-gap"));
            AddShortcut(help, "P", "Make it a park");
            AddShortcut(help, "R", "Build homes");
            AddShortcut(help, "C", "Build shops");
            AddShortcut(help, "X  or  Delete", "Clear the land (vacant)");
            AddShortcut(help, "]  or  =", "Add a floor");
            AddShortcut(help, "[  or  -", "Remove a floor");
            AddShortcut(help, "Ctrl+Z  /  Ctrl+Y", "Undo / redo");
            AddShortcut(help, "Ctrl+S", "Save your design on this computer");
            help.Add(UiFormat.Text("Words used in PlannerDNA", "section-title section-gap"));
            foreach (Explanation e in MetricDefinitions.Glossary)
            {
                help.Add(UiFormat.Text(e.Title, "explain-title"));
                help.Add(UiFormat.Text(e.Body, "explain-body"));
            }
            help.Add(UiFormat.Text("How each number is calculated", "section-title section-gap"));
            foreach (Explanation e in MetricDefinitions.Metrics)
            {
                help.Add(UiFormat.Text(e.Title, "explain-title"));
                help.Add(UiFormat.Text(e.Body, "explain-body"));
            }

            ui.Q<Button>("btn-help-close").clicked += () => ui.CloseOverlay(ui.Q<VisualElement>("help-screen"));
            ui.Q<Button>("btn-help-tour").clicked += () =>
            {
                ui.CloseOverlay(ui.Q<VisualElement>("help-screen"));
                ui.Tour.Begin();
            };
            ui.Q<Button>("btn-data-close").clicked += () => ui.CloseOverlay(ui.Q<VisualElement>("data-screen"));
            ui.Q<Button>("btn-open-folder").clicked += OpenFolder;

            _deleteScreen = ui.Q<VisualElement>("delete-screen");
            _deleteBackup = ui.Q<Toggle>("delete-backup");
            _deleteBackend = ui.Q<Toggle>("delete-backend");
            _deleteField = ui.Q<TextField>("delete-confirm-field");
            _deleteWarning = ui.Q<Label>("delete-warning");
            _deleteResult = ui.Q<Label>("delete-result");
            _deleteConfirm = ui.Q<Button>("btn-delete-confirm");
            _deleteField.RegisterValueChangedCallback(_ => UpdateDeleteButton());
            _deleteConfirm.clicked += ConfirmDelete;
            ui.Q<Button>("btn-delete-cancel").clicked += () => ui.CloseOverlay(_deleteScreen);
        }

        private static void AddShortcut(VisualElement container, string keys, string action)
        {
            var row = UiFormat.Box("shortcut-row");
            row.Add(UiFormat.Text(keys, "shortcut-key"));
            row.Add(UiFormat.Text(action, "shortcut-action"));
            container.Add(row);
        }

        // ---- Data screen ----

        public void RefreshData()
        {
            _data.Clear();
            LocalDecisionStore store = _app.Store;
            _data.Add(UiFormat.Text("Stored on this computer", "section-title"));
            _data.Add(UiFormat.Text("Data folder: " + store.RootDirectory, "body-text"));
            _data.Add(UiFormat.Text(
                "alternatives/ saved A/B snapshots | decisions/ your explicit choices | saves/ design saves | events/ edit log (JSON lines) | " +
                "outbox/ records waiting for (or already acknowledged by) the local backend | exports/ files you exported",
                "hint"));
            try
            {
                PreferenceStats s = store.ComputeStats();
                _data.Add(UiFormat.KeyValue("Unique comparisons", s.unique_comparisons.ToString()));
                _data.Add(UiFormat.KeyValue("Scenarios covered", s.scenarios.ToString()));
                _data.Add(UiFormat.KeyValue("Feasible A/B labels", s.training_eligible.ToString()));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                _data.Add(UiFormat.Text("Could not read decisions: " + e.Message, "error-text"));
            }

            _data.Add(UiFormat.Text("Sync with the local backend", "section-title section-gap"));
            _data.Add(UiFormat.KeyValue("Backend", _app.Health != null ? $"{_app.Health.Status} ({_app.Health.BaseUrl})" : "not configured"));
            _data.Add(UiFormat.Text(_app.Health != null ? _app.Health.Detail : "", "hint"));
            _syncLabel = UiFormat.Text("", "body-text");
            _syncDetail = UiFormat.Text("", "sync-detail");
            _data.Add(_syncLabel);
            _data.Add(_syncDetail);
            _data.Add(UiFormat.Text(
                "Every record is saved on this computer first and queued in order. When the backend at 127.0.0.1 is running, " +
                "the queue is sent automatically; each record has a unique id, so a resend never creates a duplicate. " +
                "Nothing is sent anywhere else.", "hint"));

            _data.Add(UiFormat.Text("Backend statistics", "section-title section-gap"));
            _serverStats = UiFormat.Text("", "body-text");
            _data.Add(_serverStats);

            _data.Add(UiFormat.Text("Actions", "section-title section-gap"));
            var actions = new VisualElement();
            actions.AddToClassList("data-actions");
            actions.Add(MakeButton("Sync now", "secondary-button", SyncNow));
            actions.Add(MakeButton("Refresh server statistics", "secondary-button", FetchServerStats));
            actions.Add(MakeButton("Export local data (JSON)", "secondary-button", ExportLocal));
            actions.Add(MakeButton("Export backend data (JSON + CSV)", "secondary-button", ExportBackend));
            actions.Add(MakeButton("Delete all data...", "danger-button", OpenDelete));
            _data.Add(actions);
            _actionResult = UiFormat.Text("", "sync-detail");
            _data.Add(_actionResult);

            _data.Add(UiFormat.Text("Versions (for reproducibility)", "section-title section-gap"));
            _data.Add(UiFormat.KeyValue("App", Versions.AppVersion));
            _data.Add(UiFormat.KeyValue("Schema", Versions.SchemaVersion.ToString()));
            _data.Add(UiFormat.KeyValue("Generator", Versions.GeneratorVersion));
            _data.Add(UiFormat.KeyValue("Metrics", Versions.MetricVersion));
            _data.Add(UiFormat.KeyValue("Cost rules", Versions.CostRulesVersion));
            _data.Add(UiFormat.KeyValue("Feature schema", Versions.FeatureVersion));
            _data.Add(UiFormat.KeyValue("Unity", Application.unityVersion));
            if (_app.Session != null)
            {
                _data.Add(UiFormat.KeyValue("Session", _app.Session.SessionId));
                _data.Add(UiFormat.KeyValue("Baseline hash", _app.Session.BaselineHash));
                _data.Add(UiFormat.KeyValue("Generator attempt", _app.Session.GeneratorAttempt.ToString()));
            }

            RefreshSyncSection();
            FetchServerStats();
        }

        private static Button MakeButton(string text, string className, Action onClick)
        {
            var button = new Button(onClick) { text = text };
            button.AddToClassList(className);
            return button;
        }

        /// <summary>Updates the live sync lines (called whenever the queue or health changes).</summary>
        public void RefreshSyncSection()
        {
            if (_syncLabel == null) return;
            SyncService sync = _app.Sync;
            if (sync == null || !sync.IsConfigured)
            {
                _syncLabel.text = "Sync service is not present in this scene; records are kept locally only.";
                _syncDetail.text = "";
                return;
            }
            _syncLabel.text = sync.StatusText;
            var lines = new List<string>();
            if (!string.IsNullOrEmpty(sync.LastOutcome)) lines.Add("Last: " + sync.LastOutcome);
            if (!string.IsNullOrEmpty(sync.LastError)) lines.Add("Last problem: " + sync.LastError);
            if (sync.Queue.RejectedCount > 0)
            {
                lines.Add($"{sync.Queue.RejectedCount} record(s) were refused by the backend and are kept in outbox/rejected for inspection:");
                List<OutboundMessageDto> rejected = sync.Queue.RejectedMessages();
                for (int i = 0; i < rejected.Count && i < 3; i++)
                    lines.Add($"  {rejected[i].kind} {rejected[i].record_id}: HTTP {rejected[i].http_status} {Shorten(rejected[i].last_error)}");
            }
            _syncDetail.text = string.Join("\n", lines);
        }

        private static string Shorten(string text) => string.IsNullOrEmpty(text) || text.Length <= 220 ? text : text.Substring(0, 220) + "...";

        private void ShowAction(string message, bool isError)
        {
            if (_actionResult != null)
            {
                _actionResult.text = message;
                _actionResult.EnableInClassList("error-text", isError);
            }
            _ui.ShowMessage(message, isError);
        }

        private void SyncNow()
        {
            if (_app.Sync == null) { ShowAction("No sync service in this scene.", true); return; }
            _app.Sync.SyncNow();
            ShowAction("Checking the backend; queued records are sent as soon as it reports a ready database.", false);
        }

        private void FetchServerStats()
        {
            if (_serverStats == null) return;
            if (_app.Sync == null || _app.Health == null || !_app.Health.StorageReady)
            {
                _serverStats.text = "Backend offline: server statistics unavailable. Local counts are shown above.";
                return;
            }
            _serverStats.text = "Loading...";
            _app.Sync.FetchStats((stats, error) =>
            {
                if (_serverStats == null) return;
                _serverStats.text = stats == null
                    ? error
                    : $"{stats.summary}\nA {stats.choice_a} | B {stats.choice_b} | tie {stats.ties} | skip {stats.skips} | " +
                      $"train mode {stats.train_mode} | evaluation mode {stats.evaluation_mode}";
            });
        }

        private void ExportLocal()
        {
            bool ok = _app.ExportLocal(out string message);
            ShowAction(message, !ok);
        }

        private void ExportBackend()
        {
            if (_app.Sync == null) { ShowAction("No sync service in this scene.", true); return; }
            _app.Sync.DownloadExport(_app.Store.ExportsDir, (ok, message) => ShowAction(message, !ok));
        }

        private void OpenFolder()
        {
            string root = _app.Store.RootDirectory;
            Directory.CreateDirectory(root);
            Application.OpenURL("file:///" + root.Replace('\\', '/'));
        }

        // ---- Confirmed deletion ----

        public void OpenDelete()
        {
            _deleteField.value = string.Empty;
            _deleteBackup.value = true;
            bool backendReady = _app.Health != null && _app.Health.StorageReady;
            _deleteBackend.value = false;
            _deleteBackend.SetEnabled(backendReady);
            _deleteResult.AddToClassList("hidden");
            int pending = _app.Outbox != null ? _app.Outbox.PendingCount : 0;
            _deleteWarning.text =
                "This permanently deletes every decision, saved alternative, design save, event log, export and queued record " +
                $"in {_app.Store.RootDirectory}. It cannot be undone. The current challenge will be closed." +
                (pending > 0 ? $"\n{pending} record(s) have not reached the backend yet and will be lost unless you make a backup." : "") +
                (backendReady ? "\nTick the second option to also empty the backend database (it writes its own backup when the first option is ticked)."
                              : "\nThe backend is offline, so only local data can be deleted now.") +
                $"\nBackups are written to {_app.Store.BackupsDir}.";
            UpdateDeleteButton();
            _ui.OpenOverlay(_deleteScreen);
        }

        private void UpdateDeleteButton() => _deleteConfirm.SetEnabled(_deleteField.value == DeleteConfirmWord);

        public bool DeleteButtonEnabled => _deleteConfirm.enabledSelf;

        /// <summary>The Delete button's action. Does nothing unless the confirmation word was typed exactly.</summary>
        public void ConfirmDelete()
        {
            if (_deleteField.value != DeleteConfirmWord) return;
            bool backup = _deleteBackup.value;
            bool alsoBackend = _deleteBackend.value && _app.Health != null && _app.Health.StorageReady && _app.Sync != null;

            if (alsoBackend)
            {
                _deleteConfirm.SetEnabled(false);
                _app.Sync.DeleteBackendData(backup, (ok, message) =>
                {
                    if (!ok)
                    {
                        ShowDeleteError(message + " Local data was not touched.");
                        UpdateDeleteButton();
                        return;
                    }
                    FinishLocalDelete(backup, message + " ");
                });
                return;
            }
            FinishLocalDelete(backup, string.Empty);
        }

        private void FinishLocalDelete(bool backup, string prefix)
        {
            bool ok = _app.DeleteLocalData(backup, out string message);
            if (!ok) { ShowDeleteError(prefix + message); return; }
            _ui.CloseOverlay(_deleteScreen);
            _ui.CloseOverlay(_ui.Q<VisualElement>("data-screen"));
            _ui.ShowHome();
            _ui.ShowMessage(prefix + message, false);
        }

        private void ShowDeleteError(string message)
        {
            _deleteResult.text = message;
            _deleteResult.RemoveFromClassList("hidden");
        }
    }
}
