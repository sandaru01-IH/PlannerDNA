using System;
using NUnit.Framework;
using PlannerDNA.Commands;
using PlannerDNA.Core;
using PlannerDNA.Generation;
using PlannerDNA.Storage;

namespace PlannerDNA.Tests
{
    public class SessionCommandTests
    {
        private PlanningSession _session;
        private int _ids;

        [SetUp]
        public void SetUp()
        {
            _ids = 0;
            _session = PlanningSession.Start(GreenSpaceScenarioGenerator.DemoSeed, "session-test",
                () => new DateTime(2026, 10, 25, 3, 0, 0, DateTimeKind.Utc), () => (++_ids).ToString("D6"));
        }

        private CellData Find(Func<CellData, bool> predicate)
        {
            foreach (CellData c in _session.Current.Cells)
                if (predicate(c)) return c;
            Assert.Fail("No matching cell in demo city");
            return default;
        }

        [Test]
        public void SetPark_OnVacant_ChangesParkCountAndCost()
        {
            CellData vacant = Find(c => c.Use == LandUse.Vacant);
            int parksBefore = _session.CurrentMetrics.ParkCells;
            CommandOutcome o = _session.Execute(EditAction.SetPark, vacant.Row, vacant.Col, InputSource.Mouse);
            Assert.IsTrue(o.Accepted, o.Message);
            Assert.AreEqual(LandUse.Park, _session.Current.Get(vacant.Row, vacant.Col).Use);
            Assert.AreEqual(parksBefore + 1, _session.CurrentMetrics.ParkCells);
            Assert.AreEqual(20, _session.CurrentMetrics.SpentCredits);
            Assert.GreaterOrEqual(_session.CurrentMetrics.AccessPct, _session.BaselineMetrics.AccessPct);
        }

        [Test]
        public void ExampleImprovingCell_RaisesAccess()
        {
            Assert.IsTrue(CellIds.TryParse(_session.ExampleImprovingCellId, out int r, out int c));
            _session.Execute(EditAction.SetPark, r, c, InputSource.Mouse);
            Assert.Greater(_session.CurrentMetrics.AccessPct, _session.BaselineMetrics.AccessPct);
            Assert.IsTrue(_session.CurrentConstraints.Feasible);
        }

        [Test]
        public void RaiseResidential_IncreasesHousingByUnitsPerFloorExactly()
        {
            CellData res = Find(c => c.Use == LandUse.Residential && c.Floors < 8);
            int before = _session.CurrentMetrics.HousingUnits;
            Assert.IsTrue(_session.Execute(EditAction.RaiseFloors, res.Row, res.Col, InputSource.Keyboard).Accepted);
            Assert.AreEqual(before + res.UnitsPerFloor, _session.CurrentMetrics.HousingUnits);
            Assert.AreEqual(4, res.UnitsPerFloor);
            Assert.AreEqual(5, _session.CurrentMetrics.SpentCredits);
        }

        [Test]
        public void CannotRaiseRoadParkOrVacant()
        {
            CellData road = Find(c => c.Use == LandUse.Road);
            CellData park = Find(c => c.Use == LandUse.Park);
            CellData vacant = Find(c => c.Use == LandUse.Vacant);
            foreach (CellData c in new[] { road, park, vacant })
            {
                CommandOutcome o = _session.Execute(EditAction.RaiseFloors, c.Row, c.Col, InputSource.Mouse);
                Assert.IsFalse(o.Accepted, c.ToString());
                Assert.IsFalse(string.IsNullOrEmpty(o.Message));
            }
            Assert.AreEqual(0, _session.History.StepCount);
            Assert.AreEqual(3, _session.RejectedAttempts.Count);
            Assert.AreEqual(0, _session.Timeline.Count, "rejected attempts are not logged as completed edits");
        }

        [Test]
        public void CannotExceedMaxFloors_OrGoBelowOne()
        {
            CellData res = Find(c => c.Use == LandUse.Residential);
            while (_session.Current.Get(res.Row, res.Col).Floors < 8)
                Assert.IsTrue(_session.Execute(EditAction.RaiseFloors, res.Row, res.Col, InputSource.Mouse).Accepted);
            CommandOutcome over = _session.Execute(EditAction.RaiseFloors, res.Row, res.Col, InputSource.Mouse);
            Assert.IsFalse(over.Accepted);
            StringAssert.Contains("maximum", over.Message);

            while (_session.Current.Get(res.Row, res.Col).Floors > 1)
                Assert.IsTrue(_session.Execute(EditAction.LowerFloors, res.Row, res.Col, InputSource.Mouse).Accepted);
            CommandOutcome under = _session.Execute(EditAction.LowerFloors, res.Row, res.Col, InputSource.Mouse);
            Assert.IsFalse(under.Accepted);
            StringAssert.Contains("Demolish", under.Message);
        }

        [Test]
        public void RoadAndCivicEdits_AreRejectedWithReason()
        {
            CellData road = Find(c => c.Use == LandUse.Road);
            CellData civic = Find(c => c.Use == LandUse.Civic);
            CommandOutcome r = _session.Execute(EditAction.SetPark, road.Row, road.Col, InputSource.Mouse);
            CommandOutcome c2 = _session.Execute(EditAction.Demolish, civic.Row, civic.Col, InputSource.Mouse);
            Assert.IsFalse(r.Accepted);
            StringAssert.Contains("road", r.Message);
            Assert.IsFalse(c2.Accepted);
            StringAssert.Contains("protected", c2.Message);
        }

        [Test]
        public void Commercial_RefusedOnResidentialOnlyZoning()
        {
            CellData res = Find(c => c.Use == LandUse.Residential && c.Zoning == Zoning.ResidentialOnly);
            CommandOutcome o = _session.Execute(EditAction.SetCommercial, res.Row, res.Col, InputSource.Mouse);
            Assert.IsFalse(o.Accepted);
            StringAssert.Contains("residential-only", o.Message);
        }

        [Test]
        public void Undo_RestoresExactCityJsonAndMetrics_Redo_ReappliesIt()
        {
            string json0 = CitySerializer.ToJson(_session.Current);
            double access0 = _session.CurrentMetrics.AccessPct;
            CellData vacant = Find(c => c.Use == LandUse.Vacant);
            _session.Execute(EditAction.SetPark, vacant.Row, vacant.Col, InputSource.Mouse);
            string json1 = CitySerializer.ToJson(_session.Current);
            int cost1 = _session.CurrentMetrics.SpentCredits;
            Assert.AreNotEqual(json0, json1);

            Assert.IsTrue(_session.Undo(InputSource.Keyboard).Accepted);
            Assert.AreEqual(json0, CitySerializer.ToJson(_session.Current));
            Assert.AreEqual(access0, _session.CurrentMetrics.AccessPct);
            Assert.AreEqual(0, _session.CurrentMetrics.SpentCredits);

            Assert.IsTrue(_session.Redo(InputSource.Keyboard).Accepted);
            Assert.AreEqual(json1, CitySerializer.ToJson(_session.Current));
            Assert.AreEqual(cost1, _session.CurrentMetrics.SpentCredits);
        }

        [Test]
        public void NewEditAfterUndo_DiscardsRedoBranch()
        {
            CellData vacant = Find(c => c.Use == LandUse.Vacant);
            CellData res = Find(c => c.Use == LandUse.Residential && c.Floors < 8);
            string first = _session.Execute(EditAction.SetPark, vacant.Row, vacant.Col, InputSource.Mouse).EventId;
            _session.Undo(InputSource.Mouse);
            Assert.IsTrue(_session.History.CanRedo);
            _session.Execute(EditAction.RaiseFloors, res.Row, res.Col, InputSource.Mouse);
            Assert.IsFalse(_session.History.CanRedo);
            Assert.AreEqual(StepStatus.Discarded, _session.History.StatusOf(first));
        }

        [Test]
        public void CompleteManualRestoration_YieldsZeroNetCost()
        {
            CellData res = Find(c => c.Use == LandUse.Residential && c.Floors < 8 && c.Floors > 1);
            _session.Execute(EditAction.RaiseFloors, res.Row, res.Col, InputSource.Mouse);
            _session.Execute(EditAction.LowerFloors, res.Row, res.Col, InputSource.Mouse);
            Assert.AreEqual(0, _session.CurrentMetrics.SpentCredits);
            Assert.IsTrue(_session.IsAtBaseline);
        }

        [Test]
        public void RestoreBaseline_IsUndoable_AndZeroCost()
        {
            CellData vacant = Find(c => c.Use == LandUse.Vacant);
            _session.Execute(EditAction.SetPark, vacant.Row, vacant.Col, InputSource.Mouse);
            string edited = CitySerializer.ToJson(_session.Current);
            Assert.IsTrue(_session.RestoreBaseline(InputSource.Mouse).Accepted);
            Assert.IsTrue(_session.IsAtBaseline);
            Assert.AreEqual(0, _session.CurrentMetrics.SpentCredits);
            Assert.IsFalse(_session.RestoreBaseline(InputSource.Mouse).Accepted, "already at baseline");
            _session.Undo(InputSource.Mouse);
            Assert.AreEqual(edited, CitySerializer.ToJson(_session.Current));
        }

        [Test]
        public void Timeline_RecordsReversalLinks()
        {
            CellData vacant = Find(c => c.Use == LandUse.Vacant);
            string cmd = _session.Execute(EditAction.SetPark, vacant.Row, vacant.Col, InputSource.Mouse).EventId;
            string undo = _session.Undo(InputSource.Keyboard).EventId;
            _session.Redo(InputSource.Keyboard);
            Assert.AreEqual(3, _session.Timeline.Count);
            TimelineEntryDto e0 = _session.Timeline[0], e1 = _session.Timeline[1], e2 = _session.Timeline[2];
            Assert.AreEqual("command", e0.event_type);
            Assert.AreEqual("set_park", e0.action);
            Assert.AreEqual(vacant.Id, e0.cell_id);
            Assert.AreEqual("vacant", e0.before.land_use);
            Assert.AreEqual("park", e0.after.land_use);
            Assert.AreEqual("undo", e1.event_type);
            Assert.AreEqual(cmd, e1.reversal_of_event_id);
            Assert.AreEqual("park", e1.before.land_use);
            Assert.AreEqual("vacant", e1.after.land_use);
            Assert.AreEqual("redo", e2.event_type);
            Assert.AreEqual(undo, e2.reversal_of_event_id);
            Assert.AreEqual(cmd, e2.original_event_id);
            Assert.AreEqual(new[] { 1, 2, 3 }, new[] { e0.sequence, e1.sequence, e2.sequence });
        }

        [Test]
        public void OverBudgetDesign_IsInfeasible_AndNotTrainingEligible()
        {
            // Convert buildings to parks until over 100 credits (35 each, 3 = 105).
            int converted = 0;
            foreach (CellData c in _session.Baseline.Cells)
            {
                if (converted == 3) break;
                if (c.Use != LandUse.Commercial) continue;
                if (_session.Execute(EditAction.SetPark, c.Row, c.Col, InputSource.Mouse).Accepted) converted++;
            }
            Assert.AreEqual(3, converted);
            Assert.AreEqual(105, _session.CurrentMetrics.SpentCredits);
            Assert.IsFalse(_session.CurrentConstraints.Feasible);

            AlternativeSnapshot a = _session.SaveAlternative(AlternativeSlot.A);
            Assert.IsFalse(a.Feasible);
            _session.RestoreBaseline(InputSource.Mouse);
            CellData vacant = Find(c => c.Use == LandUse.Vacant);
            _session.Execute(EditAction.SetPark, vacant.Row, vacant.Col, InputSource.Mouse);
            _session.SaveAlternative(AlternativeSlot.B);

            ComparisonCheck check = _session.CheckComparison();
            Assert.IsTrue(check.CanCompare);
            Assert.IsFalse(check.BothFeasible);
            PreferenceDto pref = _session.CreatePreference("cmp-x", "A", "");
            Assert.IsFalse(pref.training_eligible);
            Assert.IsFalse(pref.a_feasible);
        }

        [Test]
        public void AlternativesFromSameBaseline_CanBeCompared_IdenticalCannot()
        {
            _session.SaveAlternative(AlternativeSlot.A);
            _session.SaveAlternative(AlternativeSlot.B);
            ComparisonCheck same = _session.CheckComparison();
            Assert.IsFalse(same.CanCompare);
            StringAssert.Contains("identical", same.Problems[0]);

            CellData vacant = Find(c => c.Use == LandUse.Vacant);
            _session.Execute(EditAction.SetPark, vacant.Row, vacant.Col, InputSource.Mouse);
            _session.SaveAlternative(AlternativeSlot.B);
            ComparisonCheck ok = _session.CheckComparison();
            Assert.IsTrue(ok.CanCompare, string.Join(";", ok.Problems));
            Assert.IsTrue(ok.BothFeasible);

            PreferenceDto pref = _session.CreatePreference(_session.NewComparisonId(), "B", "more park access");
            Assert.AreEqual("B", pref.choice);
            Assert.IsTrue(pref.training_eligible);
            Assert.AreEqual(_session.AlternativeA.AlternativeId, pref.alternative_a_id);
            Assert.AreEqual("local_only", pref.sync_status);
        }

        [Test]
        public void TieAndSkip_AreStoredButNotTrainingEligible()
        {
            _session.SaveAlternative(AlternativeSlot.A);
            CellData vacant = Find(c => c.Use == LandUse.Vacant);
            _session.Execute(EditAction.SetPark, vacant.Row, vacant.Col, InputSource.Mouse);
            _session.SaveAlternative(AlternativeSlot.B);
            Assert.IsFalse(_session.CreatePreference("c1", "tie", "").training_eligible);
            Assert.IsFalse(_session.CreatePreference("c2", "skip", "").training_eligible);
            Assert.Throws<ArgumentException>(() => _session.CreatePreference("c3", "maybe", ""));
        }

        [Test]
        public void ComparisonAcrossDifferentScenarios_IsBlocked()
        {
            _session.SaveAlternative(AlternativeSlot.A);
            var other = PlanningSession.Start(7);
            CellData vacant = default;
            foreach (CellData c in other.Current.Cells) if (c.Use == LandUse.Vacant) { vacant = c; break; }
            other.Execute(EditAction.SetPark, vacant.Row, vacant.Col, InputSource.Mouse);
            AlternativeSnapshot foreign = other.SaveAlternative(AlternativeSlot.B);

            Assert.Throws<InvalidOperationException>(() => _session.SetAlternative(foreign));
            ComparisonCheck check = ComparisonRules.Check(_session.AlternativeA, foreign, _session.Baseline);
            Assert.IsFalse(check.CanCompare);
        }

        [Test]
        public void LoadedDesign_IsAppliedAsOneUndoableStep()
        {
            CellData vacant = Find(c => c.Use == LandUse.Vacant);
            _session.Execute(EditAction.SetPark, vacant.Row, vacant.Col, InputSource.Mouse);
            CityState saved = CitySerializer.FromJson(CitySerializer.ToJson(_session.Current));

            var fresh = PlanningSession.Start(GreenSpaceScenarioGenerator.DemoSeed);
            Assert.IsTrue(fresh.ApplyLoadedDesign(saved, InputSource.System).Accepted);
            Assert.IsTrue(fresh.Current.ContentEquals(saved));
            Assert.AreEqual(20, fresh.CurrentMetrics.SpentCredits);
            fresh.Undo(InputSource.Mouse);
            Assert.IsTrue(fresh.IsAtBaseline);

            var otherScenario = PlanningSession.Start(8);
            Assert.IsFalse(otherScenario.ApplyLoadedDesign(saved, InputSource.System).Accepted);
        }
    }
}
