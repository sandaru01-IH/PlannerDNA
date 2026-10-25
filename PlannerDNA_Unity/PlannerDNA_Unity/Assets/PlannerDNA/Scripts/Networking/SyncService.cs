using System;
using System.Collections;
using System.IO;
using System.Text;
using PlannerDNA.Storage;
using UnityEngine;
using UnityEngine.Networking;

namespace PlannerDNA.Networking
{
    [Serializable]
    public sealed class ServerStatsDto
    {
        public int unique_comparisons;
        public int scenarios;
        public int choice_a;
        public int choice_b;
        public int ties;
        public int skips;
        public int train_mode;
        public int evaluation_mode;
        public int training_eligible;
        public int training_eligible_scenarios;
        public string summary;
    }

    /// <summary>
    /// Sends the outbound queue to the local backend in order, only while the backend reports a
    /// ready database. Planning never waits for it: when offline, records simply stay queued.
    /// </summary>
    public sealed class SyncService : MonoBehaviour
    {
        public const string DeleteConfirmationPhrase = "DELETE ALL PLANNERDNA DATA";

        [SerializeField] private HealthMonitor health;
        [SerializeField] private int requestTimeoutSeconds = 5;
        [SerializeField] private float pollSeconds = 0.25f;

        private OutboxProcessor _processor;
        private Coroutine _loop;
        private bool _wasReady;

        public OutboundQueue Queue => _processor?.Queue;
        public OutboxProcessor Processor => _processor;
        public HealthMonitor Health => health;
        public bool IsConfigured => _processor != null && health != null;
        public string LastOutcome => _processor?.LastOutcome ?? string.Empty;
        public string LastError => _processor?.LastError ?? string.Empty;
        public event Action Changed;

        public void Initialize(OutboundQueue queue, HealthMonitor monitor)
        {
            if (_processor != null) _processor.Queue.Changed -= RaiseChanged;
            health = monitor != null ? monitor : health;
            _processor = new OutboxProcessor(queue);
            queue.Changed += RaiseChanged;
            if (isActiveAndEnabled && _loop == null) _loop = StartCoroutine(Loop());
            RaiseChanged();
        }

        private void OnEnable()
        {
            if (_processor != null && _loop == null) _loop = StartCoroutine(Loop());
        }

        private void OnDisable()
        {
            if (_loop != null) StopCoroutine(_loop);
            _loop = null;
        }

        private void RaiseChanged() => Changed?.Invoke();

        public string StatusText
        {
            get
            {
                if (!IsConfigured) return "Sync: not configured";
                OutboundQueue q = _processor.Queue;
                string state = health.StorageReady ? "online" : "offline";
                string text = $"Queue: {q.PendingCount} waiting | {q.DeliveredCount} synced";
                if (q.RejectedCount > 0) text += $" | {q.RejectedCount} refused";
                return text + $" ({state})";
            }
        }

        /// <summary>Clears the retry backoff and re-checks health; the loop sends as soon as the backend is ready.</summary>
        public void SyncNow()
        {
            _processor?.ResetBackoff();
            if (health != null && isActiveAndEnabled) StartCoroutine(health.CheckOnce());
        }

        private IEnumerator Loop()
        {
            var wait = new WaitForSecondsRealtime(pollSeconds);
            while (true)
            {
                bool ready = health != null && health.StorageReady && HealthMonitor.IsLoopbackUrl(health.BaseUrl);
                if (ready && !_wasReady) _processor.ResetBackoff();
                _wasReady = ready;
                OutboundMessageDto message = null;
                bool begun = false;
                if (ready)
                {
                    try { begun = _processor.TryBegin(out message); }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                    {
                        Debug.LogWarning("PlannerDNA outbox could not read the next record; will retry: " + e.Message);
                    }
                }
                if (begun) yield return Send(message);
                else yield return wait;
            }
        }

        private IEnumerator Send(OutboundMessageDto message)
        {
            string url = health.BaseUrl + message.endpoint;
            using (var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(message.payload_json));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Accept", "application/json");
                request.timeout = requestTimeoutSeconds;
                yield return request.SendWebRequest();
                try { _processor.Complete(message, ToResult(request)); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    Debug.LogError("PlannerDNA outbox could not record a delivery result: " + e.Message);
                }
            }
            RaiseChanged();
        }

        private static TransportResult ToResult(UnityWebRequest request)
        {
            string body = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
            if (request.result == UnityWebRequest.Result.ConnectionError || request.responseCode == 0)
                return TransportResult.Offline(request.error ?? "connection failed");
            return new TransportResult(false, request.responseCode, body);
        }

        // ---- Backend reads and administration (Data screen) ----

        private bool RequireReady(Action<bool, string> done)
        {
            if (health != null && health.StorageReady && HealthMonitor.IsLoopbackUrl(health.BaseUrl)) return true;
            done(false, "The local backend is offline or has no ready database. Start it and try again.");
            return false;
        }

        public void FetchStats(Action<ServerStatsDto, string> done)
        {
            if (!RequireReady((ok, m) => done(null, m))) return;
            StartCoroutine(Get("/api/v1/preferences/stats", (ok, text) =>
            {
                if (!ok) { done(null, text); return; }
                ServerStatsDto stats = null;
                try { stats = JsonUtility.FromJson<ServerStatsDto>(text); }
                catch (ArgumentException) { }
                done(stats, stats == null ? "The backend sent unreadable statistics." : null);
            }));
        }

        public void DownloadExport(string directory, Action<bool, string> done)
        {
            if (!RequireReady(done)) return;
            StartCoroutine(Get("/api/v1/export", (ok, json) =>
            {
                if (!ok) { done(false, json); return; }
                StartCoroutine(Get("/api/v1/export/comparisons.csv", (okCsv, csv) =>
                {
                    try
                    {
                        string stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
                        string jsonPath = Path.Combine(directory, $"plannerdna-backend-export-{stamp}.json");
                        AtomicFile.WriteAllText(jsonPath, json);
                        string message = "Backend export saved: " + jsonPath;
                        if (okCsv)
                        {
                            string csvPath = Path.Combine(directory, $"plannerdna-comparisons-{stamp}.csv");
                            AtomicFile.WriteAllText(csvPath, csv);
                            message += " and " + Path.GetFileName(csvPath);
                        }
                        done(true, message);
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                    {
                        done(false, "Could not write the export: " + e.Message);
                    }
                }));
            }));
        }

        public void DeleteBackendData(bool backup, Action<bool, string> done)
        {
            if (!RequireReady(done)) return;
            StartCoroutine(DeleteRoutine(backup, done));
        }

        private IEnumerator DeleteRoutine(bool backup, Action<bool, string> done)
        {
            string body = "{\"confirm\":\"" + DeleteConfirmationPhrase + "\",\"backup\":" + (backup ? "true" : "false") + "}";
            using (var request = new UnityWebRequest(health.BaseUrl + "/api/v1/data", UnityWebRequest.kHttpVerbDELETE))
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.timeout = 30;
                yield return request.SendWebRequest();
                bool ok = request.result == UnityWebRequest.Result.Success && request.responseCode == 200;
                done(ok, ok ? "Backend data deleted." + (backup ? " A backup was written to the backend's data/backups folder." : "")
                            : $"Backend deletion failed: {request.error} {request.downloadHandler?.text}");
            }
        }

        private IEnumerator Get(string path, Action<bool, string> done)
        {
            using (UnityWebRequest request = UnityWebRequest.Get(health.BaseUrl + path))
            {
                request.timeout = 30;
                yield return request.SendWebRequest();
                bool ok = request.result == UnityWebRequest.Result.Success && request.responseCode == 200;
                done(ok, ok ? request.downloadHandler.text : $"Request {path} failed: {request.error}");
            }
        }
    }
}
