using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PlannerDNA.Storage
{
    public static class OutboxKinds
    {
        public const string Scenario = "scenario";
        public const string Session = "session";
        public const string Event = "event";
        public const string Alternative = "alternative";
        public const string Preference = "preference";

        public static string EndpointFor(string kind)
        {
            switch (kind)
            {
                case Scenario: return "/api/v1/scenarios";
                case Session: return "/api/v1/sessions";
                case Event: return "/api/v1/events";
                case Alternative: return "/api/v1/alternatives";
                case Preference: return "/api/v1/preferences";
                default: throw new ArgumentException($"Unknown outbox kind '{kind}'.");
            }
        }
    }

    /// <summary>
    /// Durable, ordered outbound queue. Each message is its own JSON file, written atomically:
    ///   outbox/pending/&lt;sequence&gt;_&lt;kind&gt;_&lt;record_id&gt;.json   waiting for acknowledgement
    ///   outbox/delivered/...   acknowledged by the backend (201 created or 200 duplicate)
    ///   outbox/rejected/...    permanently refused by the backend (kept for inspection)
    /// A record (kind + record_id) is enqueued at most once, so a restart or repeated save never
    /// queues it twice; the backend's id-based idempotency covers resends of the same message.
    /// </summary>
    public sealed class OutboundQueue
    {
        public const string Pending = "pending";
        public const string Delivered = "delivered";
        public const string Rejected = "rejected";

        private readonly Func<DateTime> _clock;
        private readonly HashSet<string> _keys = new HashSet<string>(StringComparer.Ordinal);
        private readonly SortedDictionary<long, string> _pending = new SortedDictionary<long, string>();
        private long _nextSequence = 1;
        private int _deliveredCount, _rejectedCount;

        public OutboundQueue(string storeRoot, Func<DateTime> clock = null)
        {
            if (string.IsNullOrEmpty(storeRoot)) throw new ArgumentException("Store root is required.");
            _clock = clock ?? (() => DateTime.UtcNow);
            Root = Path.Combine(storeRoot, "outbox");
            Reload();
        }

        public string Root { get; }
        public string PendingDir => Path.Combine(Root, Pending);
        public string DeliveredDir => Path.Combine(Root, Delivered);
        public string RejectedDir => Path.Combine(Root, Rejected);
        private string CounterPath => Path.Combine(Root, "next_sequence.txt");

        public int PendingCount => _pending.Count;
        public int DeliveredCount => _deliveredCount;
        public int RejectedCount => _rejectedCount;

        public event Action Changed;

        /// <summary>Re-reads the folders (also repairs a crash between "write delivered" and "delete pending").</summary>
        public void Reload()
        {
            _keys.Clear();
            _pending.Clear();
            long maxSequence = 0;
            var finished = new HashSet<string>(StringComparer.Ordinal);
            _deliveredCount = ScanFinished(DeliveredDir, finished, ref maxSequence);
            _rejectedCount = ScanFinished(RejectedDir, finished, ref maxSequence);

            if (Directory.Exists(PendingDir))
            {
                foreach (string file in Directory.GetFiles(PendingDir, "*.json"))
                {
                    string name = Path.GetFileName(file);
                    if (!TryParseName(name, out long seq, out string kind, out string recordId)) continue;
                    maxSequence = Math.Max(maxSequence, seq);
                    if (finished.Contains(name)) { File.Delete(file); continue; }
                    _keys.Add(Key(kind, recordId));
                    _pending[seq] = file;
                }
            }

            long stored = 0;
            if (File.Exists(CounterPath)) long.TryParse(File.ReadAllText(CounterPath).Trim(), out stored);
            _nextSequence = Math.Max(stored, maxSequence + 1);
            if (_nextSequence < 1) _nextSequence = 1;
        }

        private int ScanFinished(string dir, HashSet<string> names, ref long maxSequence)
        {
            if (!Directory.Exists(dir)) return 0;
            int count = 0;
            foreach (string file in Directory.GetFiles(dir, "*.json"))
            {
                string name = Path.GetFileName(file);
                if (!TryParseName(name, out long seq, out string kind, out string recordId)) continue;
                names.Add(name);
                _keys.Add(Key(kind, recordId));
                maxSequence = Math.Max(maxSequence, seq);
                count++;
            }
            return count;
        }

        private static string Key(string kind, string recordId) => kind + "|" + recordId;

        public static string FileName(long sequence, string kind, string recordId) => $"{sequence:D12}_{kind}_{recordId}.json";

        public static bool TryParseName(string fileName, out long sequence, out string kind, out string recordId)
        {
            sequence = 0;
            kind = recordId = null;
            if (!fileName.EndsWith(".json", StringComparison.Ordinal)) return false;
            string stem = fileName.Substring(0, fileName.Length - 5);
            int first = stem.IndexOf('_');
            if (first <= 0) return false;
            int second = stem.IndexOf('_', first + 1);
            if (second <= first + 1 || second == stem.Length - 1) return false;
            if (!long.TryParse(stem.Substring(0, first), out sequence)) return false;
            kind = stem.Substring(first + 1, second - first - 1);
            recordId = stem.Substring(second + 1);
            return true;
        }

        public bool Contains(string kind, string recordId) => _keys.Contains(Key(kind, recordId));

        /// <summary>True while the record is queued but not yet acknowledged (or rejected) by the backend.</summary>
        public bool IsPending(string kind, string recordId)
        {
            foreach (string path in _pending.Values)
                if (TryParseName(Path.GetFileName(path), out _, out string k, out string id) && k == kind && id == recordId)
                    return true;
            return false;
        }

        /// <summary>Queues a record once. Returns false (and does nothing) if this record was queued before.</summary>
        public bool Enqueue(string kind, string recordId, string payloadJson)
        {
            string endpoint = OutboxKinds.EndpointFor(kind);
            RequireSafeId(recordId);
            if (string.IsNullOrEmpty(payloadJson)) throw new ArgumentException("Payload is required.");
            if (Contains(kind, recordId)) return false;

            long sequence = _nextSequence++;
            AtomicFile.WriteAllText(CounterPath, _nextSequence.ToString());
            var message = new OutboundMessageDto
            {
                message_id = Guid.NewGuid().ToString("N"),
                sequence = sequence,
                kind = kind,
                record_id = recordId,
                endpoint = endpoint,
                payload_json = payloadJson,
                created_utc = _clock().ToString("o"),
                attempts = 0,
                last_error = string.Empty,
                last_attempt_utc = string.Empty,
                status = Pending,
                http_status = 0,
                delivered_utc = string.Empty,
            };
            string path = Path.Combine(PendingDir, FileName(sequence, kind, recordId));
            AtomicFile.WriteAllText(path, JsonUtility.ToJson(message));
            _keys.Add(Key(kind, recordId));
            _pending[sequence] = path;
            Changed?.Invoke();
            return true;
        }

        private static void RequireSafeId(string id)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("record_id is required.");
            foreach (char ch in id)
                if (!(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_'))
                    throw new ArgumentException($"record_id '{id}' contains characters not allowed in a file name.");
        }

        /// <summary>The oldest undelivered message, or null when everything has been acknowledged.</summary>
        public OutboundMessageDto PeekNext()
        {
            while (_pending.Count > 0)
            {
                KeyValuePair<long, string> first = default;
                foreach (KeyValuePair<long, string> entry in _pending) { first = entry; break; }
                try { return Read(first.Value); }
                catch (Exception e) when (e is FileNotFoundException || e is DirectoryNotFoundException)
                {
                    // Removed outside the app (for example the data folder was deleted): nothing is left to send.
                    _pending.Remove(first.Key);
                    if (TryParseName(Path.GetFileName(first.Value), out _, out string kind, out string recordId))
                        _keys.Remove(Key(kind, recordId));
                    Changed?.Invoke();
                }
            }
            return null;
        }

        public List<OutboundMessageDto> PendingMessages()
        {
            var list = new List<OutboundMessageDto>(_pending.Count);
            foreach (string path in _pending.Values) list.Add(Read(path));
            return list;
        }

        public List<OutboundMessageDto> RejectedMessages() => ReadAll(RejectedDir);
        public List<OutboundMessageDto> DeliveredMessages() => ReadAll(DeliveredDir);

        private static List<OutboundMessageDto> ReadAll(string dir)
        {
            var list = new List<OutboundMessageDto>();
            if (!Directory.Exists(dir)) return list;
            string[] files = Directory.GetFiles(dir, "*.json");
            Array.Sort(files, StringComparer.Ordinal);
            foreach (string file in files) list.Add(Read(file));
            return list;
        }

        private static OutboundMessageDto Read(string path) => JsonUtility.FromJson<OutboundMessageDto>(File.ReadAllText(path));

        public void MarkDelivered(OutboundMessageDto message, long httpStatus)
        {
            message.status = Delivered;
            message.http_status = httpStatus;
            message.attempts++;
            message.last_attempt_utc = message.delivered_utc = _clock().ToString("o");
            message.last_error = string.Empty;
            MoveTo(message, DeliveredDir);
            _deliveredCount++;
            Changed?.Invoke();
        }

        public void MarkRejected(OutboundMessageDto message, long httpStatus, string error)
        {
            message.status = Rejected;
            message.http_status = httpStatus;
            message.attempts++;
            message.last_attempt_utc = _clock().ToString("o");
            message.last_error = error ?? string.Empty;
            MoveTo(message, RejectedDir);
            _rejectedCount++;
            Changed?.Invoke();
        }

        /// <summary>Records a transient failure; the message stays first in line.</summary>
        public void RecordFailure(OutboundMessageDto message, long httpStatus, string error)
        {
            if (!_pending.TryGetValue(message.sequence, out string path)) return;
            message.attempts++;
            message.http_status = httpStatus;
            message.last_attempt_utc = _clock().ToString("o");
            message.last_error = error ?? string.Empty;
            AtomicFile.WriteAllText(path, JsonUtility.ToJson(message));
            Changed?.Invoke();
        }

        private void MoveTo(OutboundMessageDto message, string dir)
        {
            if (!_pending.TryGetValue(message.sequence, out string pendingPath))
                throw new InvalidOperationException($"Message {message.sequence} is not pending.");
            // Write the finished copy first: a crash in between leaves both, which Reload() repairs.
            AtomicFile.WriteAllText(Path.Combine(dir, Path.GetFileName(pendingPath)), JsonUtility.ToJson(message));
            File.Delete(pendingPath);
            _pending.Remove(message.sequence);
        }

        /// <summary>Removes the whole outbox (used by confirmed local data deletion).</summary>
        public void DeleteAll()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
            Reload();
            Changed?.Invoke();
        }
    }
}
