using System;
using System.Collections;
using System.Text;
using PlannerDNA.Storage;
using UnityEngine;
using UnityEngine.Networking;

namespace PlannerDNA.Networking
{
    /// <summary>Outcome of one backend call: the parsed body, or the backend's structured error.</summary>
    public sealed class ApiResult<T> where T : class
    {
        public bool Ok;
        public long Status;
        public string Code;
        public string Message;
        public T Body;

        public static ApiResult<T> Fail(long status, string code, string message) =>
            new ApiResult<T> { Ok = false, Status = status, Code = code, Message = message };
    }

    /// <summary>
    /// Talks to the preference-model endpoints of the local backend. All model work (training,
    /// prediction, ranking) happens in Python; Unity never computes or invents a prediction.
    /// Requests run as coroutines on the host, so planning is never blocked.
    /// </summary>
    public sealed class LearningClient
    {
        public const int TrainTimeoutSeconds = 120;
        public const int RankTimeoutSeconds = 60;
        public const int ReadTimeoutSeconds = 10;

        private readonly MonoBehaviour _host;
        private readonly HealthMonitor _health;

        public LearningClient(MonoBehaviour host, HealthMonitor health)
        {
            _host = host;
            _health = health;
        }

        public bool Available => _health != null && _health.StorageReady && HealthMonitor.IsLoopbackUrl(_health.BaseUrl);
        public bool BackendHasModel => Available && _health.HasModel;

        public string UnavailableReason =>
            _health == null ? "No backend is configured; models are trained by the local backend."
            : !_health.StorageReady ? "The local backend is offline (or has no ready database). Start it to train or use a model."
            : string.Empty;

        public void FetchReadiness(Action<ApiResult<ReadinessDto>> done) => Get("/api/v1/models/readiness", ReadTimeoutSeconds, done);
        public void FetchCurrent(Action<ApiResult<CurrentModelDto>> done) => Get("/api/v1/models/current", ReadTimeoutSeconds, done);
        public void FetchHistory(Action<ApiResult<ModelHistoryDto>> done) => Get("/api/v1/models/history", ReadTimeoutSeconds, done);
        public void Train(Action<ApiResult<TrainResponseDto>> done) => Send("POST", "/api/v1/models/train", "{}", TrainTimeoutSeconds, done);

        public void PredictPair(PredictPairRequestDto request, Action<ApiResult<PairPredictionDto>> done) =>
            Send("POST", "/api/v1/models/predict-pair", JsonUtility.ToJson(request), ReadTimeoutSeconds, done);

        public void RankCandidates(RankCandidatesRequestDto request, Action<ApiResult<RankResponseDto>> done) =>
            Send("POST", "/api/v1/models/rank-candidates", JsonUtility.ToJson(request), RankTimeoutSeconds, done);

        // ---- Blinded evaluation (Phase 4) ----

        public void FetchEvaluation(Action<ApiResult<EvaluationOverviewDto>> done) => Get("/api/v1/evaluation", ReadTimeoutSeconds, done);

        public void FetchPlan(string planId, Action<ApiResult<EvaluationPlanDto>> done) =>
            Get("/api/v1/evaluation/plans/" + Uri.EscapeDataString(planId), ReadTimeoutSeconds, done);

        public void CreatePlan(EvaluationPlanRequestDto request, Action<ApiResult<EvaluationPlanResponseDto>> done) =>
            Send("POST", "/api/v1/evaluation/plans", JsonUtility.ToJson(request), ReadTimeoutSeconds, done);

        public void RegisterTrial(EvaluationTrialRequestDto request, Action<ApiResult<EvaluationTrialResponseDto>> done) =>
            Send("POST", "/api/v1/evaluation/trials", JsonUtility.ToJson(request), ReadTimeoutSeconds, done);

        public void Reveal(string trialId, Action<ApiResult<EvaluationRevealDto>> done) =>
            Send("POST", "/api/v1/evaluation/trials/" + Uri.EscapeDataString(trialId) + "/reveal", "{}", ReadTimeoutSeconds, done);

        public void ClosePlan(string planId, string reason, Action<ApiResult<ClosePlanResponseDto>> done) =>
            Send("POST", "/api/v1/evaluation/plans/" + Uri.EscapeDataString(planId) + "/close",
                JsonUtility.ToJson(new ClosePlanRequestDto { reason = reason ?? string.Empty }), TrainTimeoutSeconds, done);

        public void FetchReport(string planId, Action<ApiResult<EvaluationReportResponseDto>> done) =>
            Get("/api/v1/evaluation/plans/" + Uri.EscapeDataString(planId) + "/report", TrainTimeoutSeconds, done);

        /// <summary>Waits (without blocking) until <paramref name="ready"/> is true or the timeout passes.</summary>
        public void WhenReady(Func<bool> ready, float timeoutSeconds, Action<bool> done)
        {
            _host.StartCoroutine(WaitRoutine(ready, timeoutSeconds, done));
        }

        private static IEnumerator WaitRoutine(Func<bool> ready, float timeoutSeconds, Action<bool> done)
        {
            float until = Time.realtimeSinceStartup + timeoutSeconds;
            while (!ready() && Time.realtimeSinceStartup < until) yield return new WaitForSecondsRealtime(0.2f);
            done(ready());
        }

        private void Get<T>(string path, int timeout, Action<ApiResult<T>> done) where T : class => Send("GET", path, null, timeout, done);

        private void Send<T>(string method, string path, string body, int timeout, Action<ApiResult<T>> done) where T : class
        {
            if (!Available)
            {
                done(ApiResult<T>.Fail(0, "backend_unavailable", UnavailableReason));
                return;
            }
            _host.StartCoroutine(Request(method, path, body, timeout, done));
        }

        private IEnumerator Request<T>(string method, string path, string body, int timeout, Action<ApiResult<T>> done) where T : class
        {
            using (var request = new UnityWebRequest(_health.BaseUrl + path, method))
            {
                if (body != null)
                {
                    request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                    request.SetRequestHeader("Content-Type", "application/json");
                }
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Accept", "application/json");
                request.timeout = timeout;
                yield return request.SendWebRequest();
                done(Parse<T>(request));
            }
        }

        private static ApiResult<T> Parse<T>(UnityWebRequest request) where T : class
        {
            string text = request.downloadHandler != null ? request.downloadHandler.text : string.Empty;
            if (request.result == UnityWebRequest.Result.ConnectionError || request.responseCode == 0)
                return ApiResult<T>.Fail(0, "backend_unavailable", "Could not reach the local backend: " + request.error);
            if (request.responseCode >= 200 && request.responseCode < 300)
            {
                T parsed = null;
                try { parsed = JsonUtility.FromJson<T>(text); }
                catch (ArgumentException) { }
                return parsed != null
                    ? new ApiResult<T> { Ok = true, Status = request.responseCode, Body = parsed }
                    : ApiResult<T>.Fail(request.responseCode, "unreadable_response", "The backend sent a response Unity could not read.");
            }
            return ApiResult<T>.Fail(request.responseCode, ErrorField(text, true) ?? "http_error",
                ErrorField(text, false) ?? $"Backend error {request.responseCode}: {request.error}");
        }

        private static string ErrorField(string text, bool code)
        {
            try
            {
                ApiErrorEnvelopeDto envelope = JsonUtility.FromJson<ApiErrorEnvelopeDto>(text);
                if (envelope?.error == null) return null;
                string value = code ? envelope.error.code : envelope.error.message;
                return string.IsNullOrEmpty(value) ? null : value;
            }
            catch (ArgumentException) { return null; }
        }
    }
}
