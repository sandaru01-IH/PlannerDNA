using System;
using System.IO;
using NUnit.Framework;
using PlannerDNA.Commands;
using PlannerDNA.Core;
using PlannerDNA.Generation;
using PlannerDNA.Metrics;
using PlannerDNA.Storage;
using UnityEngine;

namespace PlannerDNA.Tests
{
    public class StorageTests
    {
        private string _root;
        private LocalDecisionStore _store;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "PlannerDNA_Tests_" + Guid.NewGuid().ToString("N"));
            _store = new LocalDecisionStore(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        [Test]
        public void CityState_JsonRoundTrip_IsExact_AndMetricsUnchanged()
        {
            GeneratedScenario g = GreenSpaceScenarioGenerator.Generate(42);
            string json = CitySerializer.ToJson(g.Baseline);
            CityState back = CitySerializer.FromJson(json);
            Assert.IsTrue(back.ContentEquals(g.Baseline));
            Assert.AreEqual(json, CitySerializer.ToJson(back));

            CityMetrics m1 = MetricEngine.EvaluateBaseline(g.Baseline, g.Definition);
            CityMetrics m2 = MetricEngine.EvaluateBaseline(back, g.Definition);
            Assert.AreEqual(m1.AccessPct, m2.AccessPct);
            Assert.AreEqual(m1.HousingUnits, m2.HousingUnits);
            Assert.AreEqual(m1.ParkSharePct, m2.ParkSharePct);
        }

        [Test]
        public void CityState_UsesStringLandUseKeys()
        {
            string json = CitySerializer.ToJson(GreenSpaceScenarioGenerator.Generate(42).Baseline);
            StringAssert.Contains("\"land_use\":\"road\"", json);
            StringAssert.Contains("\"cell_id\":\"r00_c00\"", json);
        }

        [Test]
        public void TamperedCity_IsRejectedByHash()
        {
            CityStateDto dto = CitySerializer.ToDto(GreenSpaceScenarioGenerator.Generate(42).Baseline);
            dto.cells[25].floors += 1;
            Assert.Throws<FormatException>(() => CitySerializer.FromDto(dto));
        }

        [Test]
        public void Alternative_RoundTripsThroughDisk_WithRecomputedMetrics()
        {
            var session = PlanningSession.Start(42);
            foreach (CellData c in session.Current.Cells)
                if (c.Use == LandUse.Vacant) { session.Execute(EditAction.SetPark, c.Row, c.Col, InputSource.Mouse); break; }
            AlternativeSnapshot a = session.SaveAlternative(AlternativeSlot.A);
            _store.SaveAlternative(a.ToDto());

            AlternativeDto loaded = _store.LoadAlternative(a.AlternativeId);
            AlternativeSnapshot back = AlternativeSnapshot.FromDto(loaded, session.Baseline, session.BaselineMetrics, session.Scenario, out string problem);
            Assert.IsNull(problem);
            Assert.IsTrue(back.State.ContentEquals(a.State));
            Assert.AreEqual(a.Metrics.AccessPct, back.Metrics.AccessPct);
            Assert.AreEqual(a.Metrics.SpentCredits, back.Metrics.SpentCredits);
            Assert.AreEqual(a.StateHash, back.StateHash);
            Assert.AreEqual(loaded.metrics.spent_credits, back.Metrics.SpentCredits);
            Assert.AreEqual(Versions.FeatureVersion, loaded.features.feature_version);
        }

        [Test]
        public void Alternative_FromDifferentBaseline_IsRefused()
        {
            var s42 = PlanningSession.Start(42);
            var s7 = PlanningSession.Start(7);
            AlternativeDto dto = s7.SaveAlternative(AlternativeSlot.A).ToDto();
            Assert.IsNull(AlternativeSnapshot.FromDto(dto, s42.Baseline, s42.BaselineMetrics, s42.Scenario, out string problem));
            StringAssert.Contains("different baseline", problem);
        }

        private static PreferenceDto Pref(string id, string a, string b, string choice = "A", string scenario = "green-42")
        {
            return new PreferenceDto
            {
                schema_version = 1, comparison_id = id, alternative_a_id = a, alternative_b_id = b,
                choice = choice, scenario_id = scenario, session_id = "s", mode = "train",
            };
        }

        [Test]
        public void Preference_PersistsAndSurvivesNewStoreInstance()
        {
            StoreResult r = _store.SavePreference(Pref("cmp-1", "alt-a", "alt-b", "B"));
            Assert.IsTrue(r.Saved, r.Message);
            Assert.IsTrue(File.Exists(r.Path));

            var reopened = new LocalDecisionStore(_root);
            var all = reopened.LoadAllPreferences();
            Assert.AreEqual(1, all.Count);
            Assert.AreEqual("B", all[0].choice);
        }

        [TestCase("A")]
        [TestCase("B")]
        [TestCase("tie")]
        [TestCase("skip")]
        public void AllChoiceKinds_Persist(string choice)
        {
            Assert.IsTrue(_store.SavePreference(Pref("cmp-" + choice, "a1", "b1", choice)).Saved);
            Assert.AreEqual(choice, _store.LoadAllPreferences()[0].choice);
        }

        [Test]
        public void DuplicateComparisonId_IsNotSavedTwice()
        {
            Assert.IsTrue(_store.SavePreference(Pref("cmp-1", "a", "b", "A")).Saved);
            StoreResult again = _store.SavePreference(Pref("cmp-1", "a", "b", "B"));
            Assert.IsFalse(again.Saved);
            Assert.IsTrue(again.Duplicate);
            Assert.AreEqual(1, _store.LoadAllPreferences().Count);
            Assert.AreEqual("A", _store.LoadAllPreferences()[0].choice, "original record kept");
        }

        [Test]
        public void SamePairUnderNewId_IsRefused_EvenReversed()
        {
            Assert.IsTrue(_store.SavePreference(Pref("cmp-1", "a", "b")).Saved);
            Assert.IsTrue(_store.SavePreference(Pref("cmp-2", "b", "a")).Duplicate);
            Assert.AreEqual(1, _store.LoadAllPreferences().Count);
        }

        [Test]
        public void SelfComparison_AndInvalidChoice_AreRefused()
        {
            Assert.IsFalse(_store.SavePreference(Pref("cmp-1", "a", "a")).Saved);
            Assert.IsFalse(_store.SavePreference(Pref("cmp-2", "a", "b", "maybe")).Saved);
            Assert.AreEqual(0, _store.LoadAllPreferences().Count);
        }

        [Test]
        public void UnsafeIds_AreRejected()
        {
            Assert.Throws<ArgumentException>(() => _store.SavePreference(Pref("..\\evil", "a", "b")));
        }

        [Test]
        public void Stats_CountUniqueComparisonsAndScenarios()
        {
            _store.SavePreference(Pref("c1", "a1", "b1", "A", "green-1"));
            _store.SavePreference(Pref("c2", "a2", "b2", "tie", "green-1"));
            _store.SavePreference(Pref("c3", "a3", "b3", "B", "green-2"));
            PreferenceStats s = _store.ComputeStats();
            Assert.AreEqual(3, s.unique_comparisons);
            Assert.AreEqual(2, s.scenarios);
            Assert.AreEqual(1, s.choice_a);
            Assert.AreEqual(1, s.choice_b);
            Assert.AreEqual(1, s.ties);
        }

        [Test]
        public void SaveGame_RoundTrips_AndLatestPointerUpdates()
        {
            var session = PlanningSession.Start(42);
            var save = new SaveGameDto
            {
                schema_version = 1, scenario_id = "green-42", seed = 42, session_id = session.SessionId,
                baseline_hash = session.BaselineHash, current = CitySerializer.ToDto(session.Current),
            };
            _store.SaveGame(save);
            Assert.IsTrue(_store.HasLatestGame);
            SaveGameDto back = _store.LoadLatestGame();
            Assert.AreEqual(session.BaselineHash, back.baseline_hash);
            Assert.IsTrue(CitySerializer.FromDto(back.current).ContentEquals(session.Current));
            Assert.IsNotNull(_store.LoadGame("green-42"));
            Assert.IsNull(_store.LoadGame("green-999"));
        }

        [Test]
        public void EventLog_AppendsAndReloadsInOrder()
        {
            var session = PlanningSession.Start(42, "session-log");
            session.EventLogged += e => _store.AppendEvent(e);
            foreach (CellData c in session.Current.Cells)
                if (c.Use == LandUse.Vacant) { session.Execute(EditAction.SetPark, c.Row, c.Col, InputSource.Mouse); break; }
            session.Undo(InputSource.Keyboard);
            var events = _store.LoadEvents("session-log");
            Assert.AreEqual(2, events.Count);
            Assert.AreEqual("command", events[0].event_type);
            Assert.AreEqual("undo", events[1].event_type);
            Assert.AreEqual(events[0].event_id, events[1].reversal_of_event_id);
        }

        [Test]
        public void AtomicWrite_ReplacesExistingFile()
        {
            string path = Path.Combine(_root, "x", "file.json");
            AtomicFile.WriteAllText(path, "{\"v\":1}");
            AtomicFile.WriteAllText(path, "{\"v\":2}");
            Assert.AreEqual("{\"v\":2}", File.ReadAllText(path));
            Assert.IsFalse(File.Exists(path + ".tmp"));
        }

        [Test]
        public void PreferenceJson_HasSnakeCaseContractFields()
        {
            string json = JsonUtility.ToJson(Pref("cmp-1", "a", "b"));
            StringAssert.Contains("\"comparison_id\":\"cmp-1\"", json);
            StringAssert.Contains("\"alternative_a_id\":\"a\"", json);
            StringAssert.Contains("\"shown_left_alternative_id\"", json);
        }
    }
}
