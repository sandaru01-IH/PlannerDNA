using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using PlannerDNA.Core;
using PlannerDNA.Storage;
using UnityEngine;

namespace PlannerDNA.Tests
{
    public sealed class OutboxTests
    {
        private string _root;
        private DateTime _now;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "plannerdna-outbox-" + Guid.NewGuid().ToString("N"));
            _now = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private OutboundQueue NewQueue() => new OutboundQueue(_root, () => _now);

        /// <summary>A stand-in for the backend's id-based idempotency (201 new, 200 identical, 409 conflict).</summary>
        private sealed class FakeBackend
        {
            public readonly Dictionary<string, string> Stored = new Dictionary<string, string>();
            public readonly List<string> Received = new List<string>();
            public bool Online = true;
            public bool LoseNextAcknowledgement;
            public string RejectRecordId;

            public TransportResult Send(OutboundMessageDto m)
            {
                if (!Online) return TransportResult.Offline("Cannot connect to destination host");
                Received.Add(m.record_id);
                if (m.record_id == RejectRecordId) return new TransportResult(false, 422, "{\"error\":{\"code\":\"scenario_mismatch\"}}");
                string key = m.kind + "|" + m.record_id;
                long status;
                if (!Stored.TryGetValue(key, out string existing)) { Stored[key] = m.payload_json; status = 201; }
                else status = existing == m.payload_json ? 200 : 409;
                if (LoseNextAcknowledgement)
                {
                    LoseNextAcknowledgement = false;
                    return TransportResult.Offline("connection reset after the server stored the record");
                }
                return new TransportResult(false, status, "{}");
            }
        }

        private static void EnqueueThree(OutboundQueue q)
        {
            Assert.IsTrue(q.Enqueue(OutboxKinds.Scenario, "green-42", "{\"n\":1}"));
            Assert.IsTrue(q.Enqueue(OutboxKinds.Session, "session-abc", "{\"n\":2}"));
            Assert.IsTrue(q.Enqueue(OutboxKinds.Event, "e_1", "{\"n\":3}"));
        }

        [Test]
        public void PendingFilesRemovedOutsideTheApp_AreSkippedNotThrown()
        {
            OutboundQueue q = NewQueue();
            EnqueueThree(q);
            File.Delete(Directory.GetFiles(q.PendingDir, "*_scenario_*.json")[0]);
            OutboundMessageDto next = q.PeekNext();
            Assert.AreEqual("session-abc", next.record_id, "the vanished record is dropped, the next one is offered");
            Assert.AreEqual(2, q.PendingCount);
            Assert.IsFalse(q.IsPending(OutboxKinds.Scenario, "green-42"));
            Assert.IsTrue(q.IsPending(OutboxKinds.Session, "session-abc"));

            Directory.Delete(q.Root, true);
            Assert.IsNull(q.PeekNext(), "a deleted outbox folder means nothing to send");
            Assert.AreEqual(0, q.PendingCount);
        }

        [Test]
        public void QueuedMessagesSurviveRestartInOrder()
        {
            EnqueueThree(NewQueue());
            OutboundQueue reopened = NewQueue();
            Assert.AreEqual(3, reopened.PendingCount);
            List<OutboundMessageDto> pending = reopened.PendingMessages();
            CollectionAssert.AreEqual(new[] { "green-42", "session-abc", "e_1" }, pending.ConvertAll(m => m.record_id));
            Assert.Less(pending[0].sequence, pending[1].sequence);
            Assert.AreEqual("/api/v1/scenarios", pending[0].endpoint);
            Assert.AreEqual("{\"n\":1}", reopened.PeekNext().payload_json);
            Assert.AreEqual(OutboundQueue.Pending, pending[2].status);
        }

        [Test]
        public void SameRecordIsQueuedOnlyOnceEvenAfterRestartAndDelivery()
        {
            OutboundQueue q = NewQueue();
            EnqueueThree(q);
            Assert.IsFalse(q.Enqueue(OutboxKinds.Scenario, "green-42", "{\"n\":99}"));
            new OutboxProcessor(q, () => _now).Drain(new FakeBackend().Send);
            OutboundQueue reopened = NewQueue();
            Assert.IsFalse(reopened.Enqueue(OutboxKinds.Scenario, "green-42", "{\"n\":1}"));
            Assert.IsTrue(reopened.Enqueue(OutboxKinds.Alternative, "green-42", "{}"), "Same id under another kind is a different record.");
            Assert.AreEqual(3, reopened.DeliveredCount);
        }

        [Test]
        public void SequenceContinuesAfterRestartEvenWithoutCounterFile()
        {
            OutboundQueue q = NewQueue();
            EnqueueThree(q);
            long last = q.PendingMessages()[2].sequence;
            File.Delete(Path.Combine(q.Root, "next_sequence.txt"));
            OutboundQueue reopened = NewQueue();
            reopened.Enqueue(OutboxKinds.Event, "e_2", "{}");
            Assert.Greater(reopened.PendingMessages()[3].sequence, last);
        }

        [TestCase(200, SyncDisposition.Delivered)]
        [TestCase(201, SyncDisposition.Delivered)]
        [TestCase(400, SyncDisposition.Rejected)]
        [TestCase(409, SyncDisposition.Rejected)]
        [TestCase(422, SyncDisposition.Rejected)]
        [TestCase(404, SyncDisposition.Retry)]
        [TestCase(408, SyncDisposition.Retry)]
        [TestCase(429, SyncDisposition.Retry)]
        [TestCase(500, SyncDisposition.Retry)]
        [TestCase(503, SyncDisposition.Retry)]
        public void StatusCodesAreClassified(int status, SyncDisposition expected)
        {
            Assert.AreEqual(expected, SyncPolicy.Classify(new TransportResult(false, status, "")));
        }

        [Test]
        public void NetworkErrorsAreRetriedWithCappedBackoff()
        {
            Assert.AreEqual(SyncDisposition.Retry, SyncPolicy.Classify(TransportResult.Offline("refused")));
            Assert.AreEqual(0, SyncPolicy.BackoffSeconds(0));
            Assert.AreEqual(2, SyncPolicy.BackoffSeconds(1));
            Assert.AreEqual(4, SyncPolicy.BackoffSeconds(2));
            Assert.AreEqual(32, SyncPolicy.BackoffSeconds(5));
            Assert.AreEqual(60, SyncPolicy.BackoffSeconds(6));
            Assert.AreEqual(60, SyncPolicy.BackoffSeconds(1000));
        }

        [Test]
        public void OfflineThenOnlineDeliversEverythingInOrderOnce()
        {
            OutboundQueue q = NewQueue();
            EnqueueThree(q);
            var backend = new FakeBackend { Online = false };
            var processor = new OutboxProcessor(q, () => _now);

            Assert.AreEqual(1, processor.Drain(backend.Send), "Stops at the first transient failure.");
            Assert.AreEqual(3, q.PendingCount);
            Assert.AreEqual(1, q.PeekNext().attempts);
            StringAssert.Contains("Cannot connect", q.PeekNext().last_error);
            Assert.AreEqual(0, processor.Drain(backend.Send), "Backoff blocks an immediate retry.");

            backend.Online = true;
            _now = _now.AddSeconds(3);
            Assert.AreEqual(3, processor.Drain(backend.Send));
            Assert.AreEqual(0, q.PendingCount);
            Assert.AreEqual(3, q.DeliveredCount);
            CollectionAssert.AreEqual(new[] { "green-42", "session-abc", "e_1" }, backend.Received);
            Assert.AreEqual(3, backend.Stored.Count);
        }

        [Test]
        public void LostAcknowledgementIsResentAndAnsweredAsDuplicate()
        {
            OutboundQueue q = NewQueue();
            EnqueueThree(q);
            var backend = new FakeBackend { LoseNextAcknowledgement = true };
            var processor = new OutboxProcessor(q, () => _now);

            processor.Drain(backend.Send);
            Assert.AreEqual(3, q.PendingCount, "Without an acknowledgement the record stays queued.");
            Assert.AreEqual(1, backend.Stored.Count, "...although the server already stored it.");

            processor.ResetBackoff();
            processor.Drain(backend.Send);
            Assert.AreEqual(0, q.PendingCount);
            Assert.AreEqual(3, backend.Stored.Count, "The resend did not create a duplicate.");
            Assert.AreEqual(200, q.DeliveredMessages()[0].http_status);
        }

        [Test]
        public void RestartMidwayResumesWithTheRemainingMessages()
        {
            OutboundQueue q = NewQueue();
            EnqueueThree(q);
            var backend = new FakeBackend();
            new OutboxProcessor(q, () => _now).Drain(backend.Send, 1);

            OutboundQueue reopened = NewQueue();
            Assert.AreEqual(2, reopened.PendingCount);
            new OutboxProcessor(reopened, () => _now).Drain(backend.Send);
            CollectionAssert.AreEqual(new[] { "green-42", "session-abc", "e_1" }, backend.Received);
            Assert.AreEqual(3, reopened.DeliveredCount);
        }

        [Test]
        public void RefusedRecordIsSetAsideAndTheQueueContinues()
        {
            OutboundQueue q = NewQueue();
            EnqueueThree(q);
            var backend = new FakeBackend { RejectRecordId = "session-abc" };
            var processor = new OutboxProcessor(q, () => _now);
            Assert.AreEqual(2, processor.Drain(backend.Send, 2));
            StringAssert.Contains("refused", processor.LastError);
            Assert.AreEqual(1, processor.Drain(backend.Send), "a refusal does not block later records");
            Assert.AreEqual(1, q.RejectedCount);
            Assert.AreEqual(2, q.DeliveredCount);
            OutboundMessageDto rejected = NewQueue().RejectedMessages()[0];
            Assert.AreEqual(422, rejected.http_status);
            StringAssert.Contains("scenario_mismatch", rejected.last_error);
        }

        [Test]
        public void CrashBetweenDeliveredWriteAndPendingDeleteIsRepaired()
        {
            OutboundQueue q = NewQueue();
            EnqueueThree(q);
            string pendingFile = Directory.GetFiles(q.PendingDir)[0];
            Directory.CreateDirectory(q.DeliveredDir);
            File.Copy(pendingFile, Path.Combine(q.DeliveredDir, Path.GetFileName(pendingFile)));

            OutboundQueue reopened = NewQueue();
            Assert.AreEqual(2, reopened.PendingCount);
            Assert.AreEqual(1, reopened.DeliveredCount);
            Assert.IsFalse(File.Exists(pendingFile));
        }

        [Test]
        public void FileNamesRoundTripIdsWithUnderscores()
        {
            string name = OutboundQueue.FileName(17, OutboxKinds.Event, "a_b_c");
            Assert.IsTrue(OutboundQueue.TryParseName(name, out long seq, out string kind, out string id));
            Assert.AreEqual(17, seq);
            Assert.AreEqual("event", kind);
            Assert.AreEqual("a_b_c", id);
            Assert.IsFalse(OutboundQueue.TryParseName("junk.json", out _, out _, out _));
        }

        [Test]
        public void UnsafeRecordIdsAndUnknownKindsAreRefused()
        {
            OutboundQueue q = NewQueue();
            Assert.Throws<ArgumentException>(() => q.Enqueue(OutboxKinds.Event, "../evil", "{}"));
            Assert.Throws<ArgumentException>(() => q.Enqueue("telemetry", "x", "{}"));
            Assert.AreEqual(0, q.PendingCount);
        }

        [Test]
        public void DeleteAllEmptiesTheOutbox()
        {
            OutboundQueue q = NewQueue();
            EnqueueThree(q);
            q.DeleteAll();
            Assert.AreEqual(0, q.PendingCount);
            Assert.AreEqual(0, NewQueue().PendingCount);
            Assert.IsTrue(q.Enqueue(OutboxKinds.Scenario, "green-42", "{}"), "A fresh outbox accepts the record again.");
        }

        [Test]
        public void ScenarioAndSessionPayloadsDescribeTheSession()
        {
            PlanningSession s = PlanningSession.Start(42);
            ScenarioRegistrationDto reg = SyncPayloads.Scenario(s, _now);
            Assert.AreEqual(s.Baseline.ScenarioId, reg.scenario_id);
            Assert.AreEqual(s.BaselineHash, reg.baseline_hash);
            Assert.AreEqual(s.BaselineHash, reg.baseline.content_hash);
            Assert.AreEqual(s.Scenario.BudgetLimit, reg.constraints.budget_limit);
            Assert.AreEqual(s.BaselineMetrics.HousingUnits, reg.baseline_metrics.housing_units);
            string json = JsonUtility.ToJson(SyncPayloads.Session(s, _now));
            StringAssert.Contains("\"session_id\":\"" + s.SessionId + "\"", json);
        }
    }

    public sealed class LocalExportAndDeletionTests
    {
        private string _root;

        [SetUp]
        public void SetUp() => _root = Path.Combine(Path.GetTempPath(), "plannerdna-del-" + Guid.NewGuid().ToString("N"), "PlannerDNA");

        [TearDown]
        public void TearDown()
        {
            string parent = Path.GetDirectoryName(_root);
            if (Directory.Exists(parent)) Directory.Delete(parent, true);
        }

        private LocalDecisionStore StoreWithOneDecision(out PlanningSession session)
        {
            var store = new LocalDecisionStore(_root);
            session = PlanningSession.Start(42);
            Assert.IsTrue(CellIds.TryParse(session.ExampleImprovingCellId, out int r, out int c));
            session.Execute(Commands.EditAction.SetPark, r, c, Commands.InputSource.Mouse);
            store.SaveAlternative(session.SaveAlternative(AlternativeSlot.A).ToDto());
            session.Undo(Commands.InputSource.Mouse);
            session.RestoreBaseline(Commands.InputSource.Mouse);
            store.SaveAlternative(session.SaveAlternative(AlternativeSlot.B).ToDto());
            return store;
        }

        [Test]
        public void ExportContainsReproducibleAlternatives()
        {
            LocalDecisionStore store = StoreWithOneDecision(out PlanningSession session);
            string path = store.WriteExport(DateTime.UtcNow);
            var export = JsonUtility.FromJson<LocalExportDto>(File.ReadAllText(path));
            Assert.AreEqual("plannerdna-local-export", export.format);
            Assert.AreEqual(2, export.alternatives.Count);
            foreach (AlternativeDto alt in export.alternatives)
            {
                CityState rebuilt = CitySerializer.FromDto(alt.snapshot);
                Assert.AreEqual(alt.state_hash, rebuilt.ContentHash());
                Assert.AreEqual(session.BaselineHash, alt.baseline_hash);
            }
        }

        [Test]
        public void BackupThenDeleteRemovesDataButKeepsBackup()
        {
            LocalDecisionStore store = StoreWithOneDecision(out _);
            new OutboundQueue(_root).Enqueue(OutboxKinds.Scenario, "green-42", "{}");
            Assert.IsTrue(store.HasAnyData());

            string backup = store.Backup(DateTime.UtcNow);
            store.DeleteAllData();

            Assert.IsFalse(store.HasAnyData());
            Assert.IsFalse(Directory.Exists(store.AlternativesDir));
            Assert.IsFalse(Directory.Exists(store.OutboxDir));
            Assert.AreEqual(2, Directory.GetFiles(Path.Combine(backup, "alternatives")).Length);
            Assert.IsTrue(Directory.Exists(Path.Combine(backup, "outbox")));
            StringAssert.StartsWith(store.BackupsDir, backup);
            Assert.IsFalse(backup.StartsWith(store.RootDirectory + Path.DirectorySeparatorChar), "Backups must live outside the deleted folder.");
        }
    }
}
