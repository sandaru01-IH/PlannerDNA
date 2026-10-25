using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

namespace PlannerDNA.Networking
{
    public enum BackendStatus
    {
        Checking,
        Online,
        Offline,
    }

    [Serializable]
    public sealed class HealthResponseDto
    {
        public string status;
        public string service;
        public string api_version;
        public int schema_version;
        public int backend_phase;
        public string storage_status;
        public int db_schema_version;
        public string model_status;
        public string active_model_version;
        /// <summary>none | open (phase 4+): an open blinded evaluation pauses training.</summary>
        public string evaluation_status;
        public string open_evaluation_plan_id;
        public string server_time_utc;
    }

    /// <summary>
    /// Polls the local backend's /api/v1/health without blocking the main thread.
    /// The sandbox never depends on the result: when offline everything still works locally.
    /// </summary>
    public sealed class HealthMonitor : MonoBehaviour
    {
        [SerializeField] private string baseUrl = "http://127.0.0.1:8765";
        [SerializeField] private float intervalSeconds = 5f;
        [SerializeField] private int timeoutSeconds = 2;

        private int _generation;
        private Coroutine _poll;

        public BackendStatus Status { get; private set; } = BackendStatus.Checking;
        public string Detail { get; private set; } = "Checking local backend...";
        public HealthResponseDto LastResponse { get; private set; }
        public string BaseUrl => baseUrl.TrimEnd('/');
        public string HealthUrl => BaseUrl + "/api/v1/health";
        /// <summary>True when the backend is online and reports a ready research database (phase 2+).</summary>
        public bool StorageReady => Status == BackendStatus.Online && LastResponse != null && LastResponse.storage_status == "ready";
        /// <summary>True only when the backend reports a trained model; Unity never assumes one exists.</summary>
        public bool HasModel => StorageReady && LastResponse.model_status == "trained" && !string.IsNullOrEmpty(LastResponse.active_model_version);
        public string ActiveModelVersion => HasModel ? LastResponse.active_model_version : string.Empty;
        public bool EvaluationOpen => StorageReady && LastResponse.evaluation_status == "open";
        public string OpenEvaluationPlanId => EvaluationOpen ? LastResponse.open_evaluation_plan_id ?? string.Empty : string.Empty;
        public event Action Changed;

        /// <summary>Tests point the monitor at a throwaway backend.</summary>
        public void SetBaseUrl(string url)
        {
            baseUrl = url;
            _generation++;
            Status = BackendStatus.Checking;
            Detail = "Checking local backend...";
            LastResponse = null;
            if (_poll != null) StopCoroutine(_poll);
            _poll = isActiveAndEnabled ? StartCoroutine(PollLoop()) : null;
            Changed?.Invoke();
        }

        /// <summary>Checks immediately (for example right after training) instead of waiting for the next poll.</summary>
        public void RefreshNow()
        {
            if (isActiveAndEnabled) StartCoroutine(CheckOnce());
        }

        /// <summary>Only loopback addresses are allowed: research data never leaves this computer.</summary>
        public static bool IsLoopbackUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri)) return false;
            return uri.Scheme == Uri.UriSchemeHttp && (uri.Host == "127.0.0.1" || uri.Host == "localhost" || uri.Host == "[::1]");
        }

        private void OnEnable()
        {
            _poll = StartCoroutine(PollLoop());
        }

        private void OnDisable()
        {
            _poll = null;
        }

        private IEnumerator PollLoop()
        {
            while (enabled)
            {
                yield return CheckOnce();
                yield return new WaitForSecondsRealtime(intervalSeconds);
            }
        }

        public IEnumerator CheckOnce()
        {
            if (!IsLoopbackUrl(BaseUrl))
            {
                bool wasDifferent = Status != BackendStatus.Offline;
                Status = BackendStatus.Offline;
                Detail = $"Backend URL {BaseUrl} is not a loopback address; PlannerDNA only talks to 127.0.0.1.";
                LastResponse = null;
                if (wasDifferent) Changed?.Invoke();
                yield break;
            }
            int generation = _generation;
            using (UnityWebRequest request = UnityWebRequest.Get(HealthUrl))
            {
                request.timeout = timeoutSeconds;
                request.SetRequestHeader("Accept", "application/json");
                yield return request.SendWebRequest();
                if (generation != _generation) yield break; // the URL changed while this check was in flight

                BackendStatus status;
                string detail;
                HealthResponseDto body = null;
                if (request.result == UnityWebRequest.Result.Success)
                {
                    try { body = JsonUtility.FromJson<HealthResponseDto>(request.downloadHandler.text); }
                    catch (ArgumentException) { body = null; }

                    if (body != null && body.status == "ok")
                    {
                        status = BackendStatus.Online;
                        detail = $"Backend online (API {body.api_version}, phase {body.backend_phase}, storage: {body.storage_status}, model: {body.model_status}" +
                                 (string.IsNullOrEmpty(body.active_model_version) ? ")" : $" {body.active_model_version})") +
                                 (body.evaluation_status == "open" ? $"; blinded evaluation {body.open_evaluation_plan_id} open" : "");
                    }
                    else
                    {
                        status = BackendStatus.Offline;
                        detail = "Backend replied with an unexpected health response";
                    }
                }
                else
                {
                    status = BackendStatus.Offline;
                    detail = "Backend offline: working locally (" + request.error + ")";
                }

                bool changed = status != Status || detail != Detail;
                Status = status;
                Detail = detail;
                LastResponse = body;
                if (changed) Changed?.Invoke();
            }
        }
    }
}
