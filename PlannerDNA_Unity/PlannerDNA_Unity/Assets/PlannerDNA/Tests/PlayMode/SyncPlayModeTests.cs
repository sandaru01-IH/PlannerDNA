using System;
using System.Collections;
using System.IO;
using System.Text;
using NUnit.Framework;
using PlannerDNA.App;
using PlannerDNA.Commands;
using PlannerDNA.Core;
using PlannerDNA.Networking;
using PlannerDNA.Storage;
using PlannerDNA.UI;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace PlannerDNA.Tests
{
    /// <summary>
    /// Outbox, sync and deletion on the real scene. Tests marked "live" need a throwaway backend
    /// (see TestBackend); the others run fully offline.
    /// </summary>
    [Timeout(120000)]
    public class SyncPlayModeTests
    {
        private string _root;
        private PlannerApp _app;

        [Serializable]
        private sealed class TimelineCountDto
        {
            public string session_id;
            public int event_count;
        }

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "PlannerDNA_SyncTests_" + Guid.NewGuid().ToString("N"));
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            PlannerApp.StoreRootOverride = null;
            PlannerApp.BackendUrlOverride = null;
            AppScene.DestroyIfLoaded();
            yield return null;
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
            if (Directory.Exists(_root + "_backups")) Directory.Delete(_root + "_backups", true);
        }

        private IEnumerator LoadScene(string backendUrl)
        {
            PlannerApp.StoreRootOverride = _root;
            PlannerApp.BackendUrlOverride = backendUrl;
            SceneManager.LoadScene("PlannerDNA_Main");
            yield return null;
            yield return null;
            _app = UnityEngine.Object.FindAnyObjectByType<PlannerApp>();
            Assert.IsNotNull(_app, "PlannerApp missing from scene");
            Assert.IsNotNull(_app.Sync, "SyncService missing: run PlannerDNA > Create or Repair Demo Scene");
        }

        private static IEnumerator WaitFor(Func<bool> condition, float seconds, Func<string> failure)
        {
            float deadline = Time.realtimeSinceStartup + seconds;
            while (!condition() && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(condition(), failure());
        }

        private IEnumerator StartAndEdit(int seed)
        {
            Assert.IsTrue(_app.NewChallenge(seed, out string error), error);
            yield return null;
            foreach (CellData c in _app.Session.Current.Cells)
            {
                if (c.Use != LandUse.Vacant) continue;
                _app.Select(c.Row, c.Col);
                if (_app.ApplyTool(EditAction.SetPark, InputSource.Mouse).Accepted) break;
            }
            Assert.AreEqual(1, _app.Session.History.AppliedCount, "one edit applied");
            _app.SaveAlternative(AlternativeSlot.A);
        }

        private string RejectedSummary()
        {
            var sb = new StringBuilder();
            foreach (OutboundMessageDto m in _app.Outbox.RejectedMessages())
                sb.Append($"{m.kind} {m.record_id}: HTTP {m.http_status} {m.last_error}\n");
            return sb.ToString();
        }

        private static IEnumerator Get(string url, Action<long, string> done)
        {
            using (UnityWebRequest r = UnityWebRequest.Get(url))
            {
                r.timeout = 10;
                yield return r.SendWebRequest();
                done(r.responseCode, r.downloadHandler.text);
            }
        }

        // ---- Offline ----

        [UnityTest]
        public IEnumerator Offline_RecordsAreQueuedInOrderAndSurviveRestart()
        {
            yield return LoadScene(TestBackend.DeadPortUrl);
            yield return StartAndEdit(42);
            yield return WaitFor(() => _app.Health.Status == BackendStatus.Offline, 8f, () => "health check did not resolve");

            // scenario, session, command event, checkpoint event, alternative
            Assert.AreEqual(5, _app.Outbox.PendingCount);
            Assert.AreEqual(0, _app.Outbox.DeliveredCount);
            StringAssert.Contains("5 to sync", _app.Ui.Q<Label>("sync-status").text);
            string sessionId = _app.Session.SessionId;

            yield return LoadScene(TestBackend.DeadPortUrl);
            Assert.AreEqual(5, _app.Outbox.PendingCount, "queue survives an app restart");
            var kinds = _app.Outbox.PendingMessages().ConvertAll(m => m.kind);
            CollectionAssert.AreEqual(new[] { "scenario", "session", "event", "event", "alternative" }, kinds);
            Assert.AreEqual(sessionId, _app.Outbox.PendingMessages()[1].record_id);
            Assert.IsNull(_app.Session, "a restart begins on the Home screen");
        }

        [UnityTest]
        public IEnumerator DeleteAllData_RequiresTypedConfirmation_AndKeepsABackup()
        {
            yield return LoadScene(TestBackend.DeadPortUrl);
            yield return StartAndEdit(42);
            InfoViews info = _app.Ui.Info;
            info.OpenDelete();
            Assert.IsFalse(_app.Ui.Q<VisualElement>("delete-screen").ClassListContains("hidden"));
            Assert.IsTrue(_app.Ui.Q<Toggle>("delete-backup").value, "backup is on by default");
            Assert.IsFalse(_app.Ui.Q<Toggle>("delete-backend").enabledSelf, "backend deletion unavailable while offline");
            StringAssert.Contains("cannot be undone", _app.Ui.Q<Label>("delete-warning").text);
            Assert.IsFalse(info.DeleteButtonEnabled);

            TextField field = _app.Ui.Q<TextField>("delete-confirm-field");
            field.value = "delete";
            yield return null;
            Assert.IsFalse(info.DeleteButtonEnabled);
            info.ConfirmDelete();
            Assert.IsNotNull(_app.Session, "wrong confirmation word deletes nothing");
            Assert.IsTrue(_app.Store.HasAnyData());

            field.value = InfoViews.DeleteConfirmWord;
            yield return null;
            Assert.IsTrue(info.DeleteButtonEnabled);
            info.ConfirmDelete();
            yield return null;

            Assert.IsNull(_app.Session);
            Assert.IsFalse(_app.Store.HasAnyData());
            Assert.AreEqual(0, _app.Outbox.PendingCount);
            Assert.IsFalse(_app.Ui.Q<VisualElement>("home-screen").ClassListContains("hidden"));
            string[] backups = Directory.GetDirectories(_app.Store.BackupsDir);
            Assert.AreEqual(1, backups.Length);
            Assert.AreEqual(1, Directory.GetFiles(Path.Combine(backups[0], "alternatives")).Length);
            Assert.AreEqual(5, Directory.GetFiles(Path.Combine(backups[0], "outbox", "pending")).Length);
        }

        // ---- Live (throwaway backend only) ----

        [UnityTest]
        public IEnumerator Live_RecordsReachTheBackend_AndRestartCreatesNoDuplicates()
        {
            if (!TestBackend.Configured) Assert.Ignore(TestBackend.IgnoreReason);
            string url = TestBackend.Url.TrimEnd('/');
            yield return LoadScene(url);
            yield return WaitFor(() => _app.Health.StorageReady, 12f, () => "throwaway backend not ready at " + url + ": " + _app.Health.Detail);

            yield return StartAndEdit(42);
            _app.Undo(InputSource.Keyboard);
            _app.SaveAlternative(AlternativeSlot.B);
            StoreResult saved = _app.SubmitPreference(_app.Session.NewComparisonId(), "A", "live sync test");
            Assert.IsTrue(saved.Saved, saved.Message);
            string sessionId = _app.Session.SessionId;
            int timeline = _app.Session.Timeline.Count;

            yield return WaitFor(() => _app.Outbox.PendingCount == 0, 30f, () => $"{_app.Outbox.PendingCount} still pending: {_app.Sync.LastError}");
            Assert.AreEqual(0, _app.Outbox.RejectedCount, RejectedSummary());

            TimelineCountDto serverTimeline = null;
            yield return Get($"{url}/api/v1/sessions/{sessionId}/events", (code, body) =>
            {
                Assert.AreEqual(200, code, body);
                serverTimeline = JsonUtility.FromJson<TimelineCountDto>(body);
            });
            Assert.AreEqual(timeline, serverTimeline.event_count, "every timeline event reached the backend once");

            ServerStatsDto before = null;
            yield return Get($"{url}/api/v1/preferences/stats", (code, body) => before = JsonUtility.FromJson<ServerStatsDto>(body));
            Assert.GreaterOrEqual(before.unique_comparisons, 1);

            // Resend one acknowledged record exactly: the backend answers "duplicate".
            OutboundMessageDto delivered = _app.Outbox.DeliveredMessages()[2];
            using (var post = new UnityWebRequest(url + delivered.endpoint, UnityWebRequest.kHttpVerbPOST))
            {
                post.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(delivered.payload_json));
                post.downloadHandler = new DownloadHandlerBuffer();
                post.SetRequestHeader("Content-Type", "application/json");
                yield return post.SendWebRequest();
                Assert.AreEqual(200, post.responseCode, post.downloadHandler.text);
                StringAssert.Contains("duplicate", post.downloadHandler.text);
            }

            // Restart: the new app instance resumes from disk, re-sends nothing that was acknowledged.
            int deliveredCount = _app.Outbox.DeliveredCount;
            yield return LoadScene(url);
            Assert.AreEqual(0, _app.Outbox.PendingCount);
            Assert.AreEqual(deliveredCount, _app.Outbox.DeliveredCount);
            yield return WaitFor(() => _app.Health.StorageReady, 12f, () => "backend not ready after restart");
            Assert.IsTrue(_app.NewChallenge(42, out _));
            Assert.AreEqual(1, _app.Outbox.PendingCount, "only the new session is queued; the scenario was already delivered");
            yield return WaitFor(() => _app.Outbox.PendingCount == 0, 30f, () => "new session not delivered: " + _app.Sync.LastError);

            ServerStatsDto after = null;
            yield return Get($"{url}/api/v1/preferences/stats", (code, body) => after = JsonUtility.FromJson<ServerStatsDto>(body));
            Assert.AreEqual(before.unique_comparisons, after.unique_comparisons, "no duplicate decisions after restart");
        }

        [UnityTest]
        public IEnumerator Live_QueueBuiltOffline_IsDeliveredWhenTheBackendAppears()
        {
            if (!TestBackend.Configured) Assert.Ignore(TestBackend.IgnoreReason);
            string url = TestBackend.Url.TrimEnd('/');
            yield return LoadScene(TestBackend.DeadPortUrl);
            yield return StartAndEdit(7);
            yield return WaitFor(() => _app.Health.Status == BackendStatus.Offline, 8f, () => "health check did not resolve");
            int queued = _app.Outbox.PendingCount;
            Assert.GreaterOrEqual(queued, 5);
            Assert.AreEqual(0, _app.Outbox.DeliveredCount);

            _app.Health.SetBaseUrl(url);
            _app.Sync.SyncNow();
            yield return WaitFor(() => _app.Outbox.PendingCount == 0, 30f, () => $"{_app.Outbox.PendingCount} still pending: {_app.Sync.LastError}");
            Assert.AreEqual(0, _app.Outbox.RejectedCount, RejectedSummary());
            Assert.AreEqual(queued, _app.Outbox.DeliveredCount);

            TimelineCountDto serverTimeline = null;
            yield return Get($"{url}/api/v1/sessions/{_app.Session.SessionId}/events", (code, body) =>
            {
                Assert.AreEqual(200, code, body);
                serverTimeline = JsonUtility.FromJson<TimelineCountDto>(body);
            });
            Assert.AreEqual(_app.Session.Timeline.Count, serverTimeline.event_count);
        }
    }
}
