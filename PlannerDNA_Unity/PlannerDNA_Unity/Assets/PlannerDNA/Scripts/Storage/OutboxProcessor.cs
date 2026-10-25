using System;

namespace PlannerDNA.Storage
{
    public enum SyncDisposition
    {
        Delivered,
        Rejected,
        Retry,
    }

    public readonly struct TransportResult
    {
        public readonly bool NetworkError;
        public readonly long HttpStatus;
        public readonly string Body;

        public TransportResult(bool networkError, long httpStatus, string body)
        {
            NetworkError = networkError;
            HttpStatus = httpStatus;
            Body = body ?? string.Empty;
        }

        public static TransportResult Offline(string error) => new TransportResult(true, 0, error);
    }

    public static class SyncPolicy
    {
        public const double MaxBackoffSeconds = 60;

        /// <summary>
        /// 200 (already stored) and 201 (created) are acknowledgements. 400/409/422 mean the backend
        /// examined this record and refused it, so resending cannot help. Anything else (offline,
        /// timeout, 404 from a backend without these routes, 429, 5xx) is retried later.
        /// </summary>
        public static SyncDisposition Classify(TransportResult result)
        {
            if (result.NetworkError) return SyncDisposition.Retry;
            long s = result.HttpStatus;
            if (s == 200 || s == 201) return SyncDisposition.Delivered;
            if (s == 400 || s == 409 || s == 413 || s == 422) return SyncDisposition.Rejected;
            return SyncDisposition.Retry;
        }

        public static double BackoffSeconds(int consecutiveFailures)
        {
            if (consecutiveFailures <= 0) return 0;
            return Math.Min(MaxBackoffSeconds, Math.Pow(2, Math.Min(consecutiveFailures, 10)));
        }
    }

    /// <summary>
    /// Delivers queued messages strictly in order, one at a time, without any networking code of
    /// its own: callers send the message and report the outcome. This keeps the offline/online
    /// logic fully testable in EditMode.
    /// </summary>
    public sealed class OutboxProcessor
    {
        private readonly OutboundQueue _queue;
        private readonly Func<DateTime> _clock;
        private OutboundMessageDto _inFlight;

        public OutboxProcessor(OutboundQueue queue, Func<DateTime> clock = null)
        {
            _queue = queue ?? throw new ArgumentNullException(nameof(queue));
            _clock = clock ?? (() => DateTime.UtcNow);
        }

        public OutboundQueue Queue => _queue;
        public int ConsecutiveFailures { get; private set; }
        public DateTime NextAttemptUtc { get; private set; } = DateTime.MinValue;
        public bool Busy => _inFlight != null;
        public string LastError { get; private set; } = string.Empty;
        public string LastOutcome { get; private set; } = string.Empty;

        /// <summary>Clears the backoff, e.g. when the backend comes back online or the user presses Sync now.</summary>
        public void ResetBackoff()
        {
            NextAttemptUtc = DateTime.MinValue;
        }

        public bool TryBegin(out OutboundMessageDto message)
        {
            message = null;
            if (_inFlight != null || _clock() < NextAttemptUtc) return false;
            message = _queue.PeekNext();
            if (message == null) return false;
            _inFlight = message;
            return true;
        }

        public SyncDisposition Complete(OutboundMessageDto message, TransportResult result)
        {
            if (message == null || _inFlight == null || message.sequence != _inFlight.sequence)
                throw new InvalidOperationException("Complete() must be called with the message returned by TryBegin().");
            _inFlight = null;
            SyncDisposition disposition = SyncPolicy.Classify(result);
            switch (disposition)
            {
                case SyncDisposition.Delivered:
                    _queue.MarkDelivered(message, result.HttpStatus);
                    ConsecutiveFailures = 0;
                    NextAttemptUtc = DateTime.MinValue;
                    LastError = string.Empty;
                    LastOutcome = $"{message.kind} {message.record_id}: " + (result.HttpStatus == 201 ? "stored" : "already stored");
                    break;
                case SyncDisposition.Rejected:
                    _queue.MarkRejected(message, result.HttpStatus, Truncate(result.Body));
                    ConsecutiveFailures = 0;
                    NextAttemptUtc = DateTime.MinValue;
                    LastError = $"{message.kind} {message.record_id} refused (HTTP {result.HttpStatus})";
                    LastOutcome = LastError;
                    break;
                default:
                    string error = result.NetworkError ? result.Body : $"HTTP {result.HttpStatus}: {Truncate(result.Body)}";
                    _queue.RecordFailure(message, result.HttpStatus, error);
                    ConsecutiveFailures++;
                    NextAttemptUtc = _clock().AddSeconds(SyncPolicy.BackoffSeconds(ConsecutiveFailures));
                    LastError = error;
                    LastOutcome = $"Will retry {message.kind} {message.record_id} (attempt {message.attempts})";
                    break;
            }
            return disposition;
        }

        /// <summary>Synchronous drain for tests and tools: stops at the first transient failure.</summary>
        public int Drain(Func<OutboundMessageDto, TransportResult> send, int maxMessages = int.MaxValue)
        {
            int processed = 0;
            while (processed < maxMessages && TryBegin(out OutboundMessageDto message))
            {
                processed++;
                if (Complete(message, send(message)) == SyncDisposition.Retry) break;
            }
            return processed;
        }

        private static string Truncate(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Length <= 2000 ? text : text.Substring(0, 2000) + "...";
        }
    }
}
