using NUnit.Framework;
using PlannerDNA.Core;
using PlannerDNA.Metrics;

namespace PlannerDNA.Tests
{
    public class CostAndConstraintTests
    {
        private static readonly CostRules Rules = CostRules.Default;

        private static CellData Cell(LandUse use, int floors = 0)
        {
            return new CellData(0, 0, use, floors, use == LandUse.Residential ? 4 : 0, 8, true, Zoning.Mixed);
        }

        [Test]
        public void Unchanged_CostsZero()
        {
            Assert.AreEqual(0, Rules.Evaluate(Cell(LandUse.Residential, 3), Cell(LandUse.Residential, 3)).Credits);
            Assert.AreEqual(0, Rules.Evaluate(Cell(LandUse.Vacant), Cell(LandUse.Vacant)).Credits);
        }

        [TestCase(LandUse.Vacant, 0, LandUse.Park, 0, 20)]
        [TestCase(LandUse.Vacant, 0, LandUse.Residential, 2, 35)]
        [TestCase(LandUse.Vacant, 0, LandUse.Commercial, 3, 40)]
        [TestCase(LandUse.Park, 0, LandUse.Residential, 2, 35)]
        [TestCase(LandUse.Residential, 3, LandUse.Park, 0, 35)]
        [TestCase(LandUse.Commercial, 2, LandUse.Park, 0, 35)]
        [TestCase(LandUse.Residential, 3, LandUse.Vacant, 0, 15)]
        [TestCase(LandUse.Park, 0, LandUse.Vacant, 0, 10)]
        [TestCase(LandUse.Residential, 3, LandUse.Commercial, 3, 20)]
        [TestCase(LandUse.Residential, 3, LandUse.Commercial, 5, 30)]
        [TestCase(LandUse.Commercial, 4, LandUse.Residential, 2, 26)]
        [TestCase(LandUse.Residential, 3, LandUse.Residential, 5, 10)]
        [TestCase(LandUse.Residential, 5, LandUse.Residential, 3, 6)]
        public void TransitionTable(LandUse from, int fromFloors, LandUse to, int toFloors, int expected)
        {
            CostRules.CostLine line = Rules.Evaluate(Cell(from, fromFloors), Cell(to, toFloors));
            Assert.IsTrue(line.Defined, line.Rule);
            Assert.AreEqual(expected, line.Credits, line.Rule);
            Assert.GreaterOrEqual(line.Credits, 0);
        }

        [Test]
        public void RoadAndCivicTransitions_AreUndefined()
        {
            Assert.IsFalse(Rules.Evaluate(Cell(LandUse.Road), Cell(LandUse.Park)).Defined);
            Assert.IsFalse(Rules.Evaluate(Cell(LandUse.Civic, 2), Cell(LandUse.Vacant)).Defined);
            Assert.IsFalse(Rules.Evaluate(Cell(LandUse.Vacant), Cell(LandUse.Road)).Defined);
        }

        [Test]
        public void CityCost_IsNetDifferenceFromBaseline()
        {
            var s = TestGrids.Build(new[] { "HVV", "RRR" }, defaultFloors: 3);
            var edited = s.WithCell(s.Get(0, 1).With(LandUse.Park, 0, 0))   // +20
                          .WithCell(s.Get(0, 0).WithFloors(4));               // +5
            Assert.AreEqual(25, MetricEngine.Evaluate(edited, s, TestGrids.Scenario).SpentCredits);

            var restored = edited.WithCell(s.Get(0, 1)).WithCell(s.Get(0, 0));
            CityMetrics m = MetricEngine.Evaluate(restored, s, TestGrids.Scenario);
            Assert.AreEqual(0, m.SpentCredits);
            Assert.AreEqual(0, m.ChangedCells);
        }

        [Test]
        public void LostOriginalHousing_CountsOnlyBaselineResidentialLosses()
        {
            var s = TestGrids.Build(new[] { "HHV", "RRR" }, defaultFloors: 3);
            var edited = s.WithCell(s.Get(0, 0).With(LandUse.Park, 0, 0))              // lose 12
                          .WithCell(s.Get(0, 1).WithFloors(5))                         // +8 (not a loss)
                          .WithCell(s.Get(0, 2).With(LandUse.Residential, 2, 4));      // +8 new
            CityMetrics m = MetricEngine.Evaluate(edited, s, TestGrids.Scenario);
            Assert.AreEqual(12, m.LostOriginalHousingUnits);
            Assert.AreEqual(-12 + 8 + 8, m.NetHousingChange);
        }

        [Test]
        public void Budget_AtLimitPasses_AboveLimitFails()
        {
            // Five vacant->park = 100 credits; six = 120.
            var s = TestGrids.Build(new[] { "HVVVVVV", "RRRRRRR" });
            CityState five = s;
            for (int c = 1; c <= 5; c++) five = five.WithCell(five.Get(0, c).With(LandUse.Park, 0, 0));
            CityState six = five.WithCell(five.Get(0, 6).With(LandUse.Park, 0, 0));

            CityMetrics baseline = MetricEngine.EvaluateBaseline(s, TestGrids.Scenario);
            CityMetrics m5 = MetricEngine.Evaluate(five, s, TestGrids.Scenario);
            CityMetrics m6 = MetricEngine.Evaluate(six, s, TestGrids.Scenario);
            Assert.AreEqual(100, m5.SpentCredits);
            Assert.AreEqual(120, m6.SpentCredits);
            Assert.IsTrue(ConstraintEvaluator.Evaluate(five, m5, baseline, TestGrids.Scenario).Feasible);
            ConstraintReport r6 = ConstraintEvaluator.Evaluate(six, m6, baseline, TestGrids.Scenario);
            Assert.IsFalse(r6.Feasible);
            CollectionAssert.Contains(r6.FailedIds(), ConstraintEvaluator.Budget);
        }

        [Test]
        public void HousingFloor_ExactlyNinetyPercentPasses_BelowFails()
        {
            // Baseline 10 residential x 1 floor x 4 = 40 units; 90% = 36 units.
            var s = TestGrids.Build(new[] { "HHHHHHHHHH", "RRRRRRRRRR" }, defaultFloors: 1);
            CityMetrics baseline = MetricEngine.EvaluateBaseline(s, TestGrids.Scenario);
            Assert.AreEqual(36, TestGrids.Scenario.MinimumHousingUnits(baseline.HousingUnits));

            CityState oneLost = s.WithCell(s.Get(0, 0).With(LandUse.Vacant, 0, 0));          // 36 units
            CityState twoLost = oneLost.WithCell(oneLost.Get(0, 1).With(LandUse.Vacant, 0, 0)); // 32 units
            CityMetrics m1 = MetricEngine.Evaluate(oneLost, s, TestGrids.Scenario);
            CityMetrics m2 = MetricEngine.Evaluate(twoLost, s, TestGrids.Scenario);
            Assert.IsTrue(ConstraintEvaluator.Evaluate(oneLost, m1, baseline, TestGrids.Scenario).Feasible);
            CollectionAssert.Contains(ConstraintEvaluator.Evaluate(twoLost, m2, baseline, TestGrids.Scenario).FailedIds(),
                ConstraintEvaluator.Housing);
        }

        [Test]
        public void MinimumHousing_RoundsUp()
        {
            Assert.AreEqual(0, TestGrids.Scenario.MinimumHousingUnits(0));
            Assert.AreEqual(9, TestGrids.Scenario.MinimumHousingUnits(10));
            Assert.AreEqual(10, TestGrids.Scenario.MinimumHousingUnits(11)); // 9.9 -> 10
            Assert.AreEqual(239, TestGrids.Scenario.MinimumHousingUnits(265)); // 238.5 -> 239
        }

        [Test]
        public void FloorLimit_ViolationDetected()
        {
            var floors = new System.Collections.Generic.Dictionary<(int, int), int> { { (0, 0), 9 } };
            var s = TestGrids.Build(new[] { "HV", "RR" }, 2, floors);
            CityMetrics m = MetricEngine.EvaluateBaseline(s, TestGrids.Scenario);
            ConstraintReport r = ConstraintEvaluator.Evaluate(s, m, m, TestGrids.Scenario);
            CollectionAssert.Contains(r.FailedIds(), ConstraintEvaluator.MaxFloors);
        }

        [Test]
        public void Features_FollowDocumentedFormulas()
        {
            var s = TestGrids.Build(new[] { "HVVVVVVVVVVP", "RRRRRRRRRRRR" }, defaultFloors: 5);
            CityMetrics baseline = MetricEngine.EvaluateBaseline(s, TestGrids.Scenario);
            var edited = s.WithCell(s.Get(0, 3).With(LandUse.Park, 0, 0));
            CityMetrics m = MetricEngine.Evaluate(edited, s, TestGrids.Scenario);
            FeatureVectorV1 f = FeatureExtractor.Extract(m, baseline, TestGrids.Scenario);
            Assert.AreEqual(1.0, f.delta_access_fraction, 1e-12);
            Assert.AreEqual(0.0, f.delta_housing_fraction, 1e-12);
            Assert.AreEqual((2.0 / 12 - 1.0 / 12) * 100 / 100.0, f.delta_park_share_fraction, 1e-12);
            Assert.AreEqual(-0.2, f.negative_budget_fraction, 1e-12);
            Assert.AreEqual(0.0, f.negative_lost_housing_fraction, 1e-12);
            Assert.AreEqual(Versions.FeatureVersion, f.feature_version);
        }
    }
}
