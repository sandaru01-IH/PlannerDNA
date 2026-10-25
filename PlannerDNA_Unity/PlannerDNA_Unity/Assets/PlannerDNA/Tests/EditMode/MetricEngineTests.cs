using NUnit.Framework;
using PlannerDNA.Core;
using PlannerDNA.Metrics;

namespace PlannerDNA.Tests
{
    public class MetricEngineTests
    {
        private static CityMetrics Eval(CityState s) => MetricEngine.EvaluateBaseline(s, TestGrids.Scenario);

        [Test]
        public void Housing_IsFloorsTimesUnitsPerFloor_ResidentialOnly()
        {
            var s = TestGrids.Build(new[] { "HCH", "RRR" }, defaultFloors: 3);
            Assert.AreEqual(2 * 3 * 4, Eval(s).HousingUnits);
        }

        [Test]
        public void ParkShare_DenominatorExcludesOnlyRoads()
        {
            // Non-road: H, P, V, X = 4 cells, 1 park -> 25%.
            var s = TestGrids.Build(new[] { "HPVX", "RRRR" });
            CityMetrics m = Eval(s);
            Assert.AreEqual(4, m.NonRoadCells);
            Assert.AreEqual(25.0, m.ParkSharePct, 1e-9);
        }

        [Test]
        public void Access_AtExactly400m_IsAccessible()
        {
            // House (0,0) -> road (1,0); park (0,8) -> road (1,8). 40 + 8*40 + 40 = 400 m.
            var s = TestGrids.Build(new[] { "HVVVVVVVP", "RRRRRRRRR" });
            CityMetrics m = Eval(s);
            Assert.AreEqual(400, m.ParkDistanceMetres[0]);
            Assert.IsTrue(m.AccessDefined);
            Assert.AreEqual(100.0, m.AccessPct, 1e-9);
        }

        [Test]
        public void Access_At440m_IsNotAccessible()
        {
            var s = TestGrids.Build(new[] { "HVVVVVVVVP", "RRRRRRRRRR" });
            CityMetrics m = Eval(s);
            Assert.AreEqual(440, m.ParkDistanceMetres[0]);
            Assert.AreEqual(0.0, m.AccessPct, 1e-9);
        }

        [Test]
        public void Access_AdjacentToSameRoadCell_Is80m()
        {
            var s = TestGrids.Build(new[] { "HP", "RV" });
            // House (0,0) touches road (1,0); park (0,1) does not touch road (1,0)? (0,1) neighbours: (1,1)=V, (0,0)=H -> no road.
            Assert.AreEqual(AccessibilityCalculator.Unreachable, Eval(s).ParkDistanceMetres[0]);

            var s2 = TestGrids.Build(new[] { "HV", "RP" });
            // Park (1,1) touches road (1,0); house (0,0) touches road (1,0): 40 + 0 + 40.
            Assert.AreEqual(80, Eval(s2).ParkDistanceMetres[0]);
        }

        [Test]
        public void Access_ParkWithoutRoadLink_DoesNotCount_NoEuclideanFallback()
        {
            // Park at (0,2) is enclosed by H and V; no road neighbour -> nobody reaches it even though it is 2 cells away.
            var s = TestGrids.Build(new[] { "HVPV", "RRVR" });
            CityMetrics m = Eval(s);
            Assert.AreEqual(AccessibilityCalculator.Unreachable, m.ParkDistanceMetres[0]);
            Assert.AreEqual(0.0, m.AccessPct, 1e-9);
            Assert.AreEqual(1, m.UnreachableResidentialCells);
        }

        [Test]
        public void Access_DisconnectedRoads_AreInaccessible()
        {
            // Road pieces (1,0) and (1,2) separated by vacant (1,1).
            var s = TestGrids.Build(new[] { "HVP", "RVR" });
            Assert.AreEqual(AccessibilityCalculator.Unreachable, Eval(s).ParkDistanceMetres[0]);
            Assert.IsFalse(AccessibilityCalculator.RoadNetworkConnected(s));
        }

        [Test]
        public void Access_ResidentialWithoutRoad_IsInaccessible()
        {
            var s = TestGrids.Build(new[] { "H", "V", "R", "P" });
            CityMetrics m = Eval(s);
            Assert.AreEqual(AccessibilityCalculator.Unreachable, m.ParkDistanceMetres[0]);
            Assert.AreEqual(0.0, m.AccessPct, 1e-9);
        }

        [Test]
        public void Access_MultipleParks_UsesNearest()
        {
            // Parks at both ends; house at col 1 is nearer the west park (col 0 road dist 1).
            var s = TestGrids.Build(new[] { "PHVVVVVVVVVP", "RRRRRRRRRRRR" });
            Assert.AreEqual(40 + 1 * 40 + 40, Eval(s).ParkDistanceMetres[1]);
        }

        [Test]
        public void Access_IsWeightedByHousingUnits()
        {
            // House A (0,0) 6 floors, 400 m from park -> accessible; House B (0,12) 2 floors, too far.
            var floors = new System.Collections.Generic.Dictionary<(int, int), int> { { (0, 0), 6 }, { (0, 12), 2 } };
            var s = TestGrids.Build(new[] { "HVVVVVVVPVVVH", "RRRRRRRRRVVVR" }, 2, floors);
            CityMetrics m = Eval(s);
            Assert.AreEqual(24 + 8, m.HousingUnits);
            Assert.AreEqual(24, m.AccessibleHousingUnits);
            Assert.AreEqual(100.0 * 24 / 32, m.AccessPct, 1e-9);
        }

        [Test]
        public void Access_ZeroHousing_IsNotApplicable_NotHundredPercent()
        {
            var s = TestGrids.Build(new[] { "CPV", "RRR" });
            CityMetrics m = Eval(s);
            Assert.IsFalse(m.AccessDefined);
            Assert.AreEqual("N/A", m.AccessDisplay);
        }

        [Test]
        public void Metrics_AreDeterministic()
        {
            var s = Generation.GreenSpaceScenarioGenerator.Generate(7).Baseline;
            CityMetrics a = Eval(s), b = Eval(s);
            Assert.AreEqual(a.AccessPct, b.AccessPct);
            Assert.AreEqual(a.HousingUnits, b.HousingUnits);
            CollectionAssert.AreEqual(a.ParkDistanceMetres, b.ParkDistanceMetres);
        }

        [Test]
        public void AddingReachablePark_RaisesAccess_UnreachableParkDoesNot()
        {
            // House at (0,0); far park at (0,11) beyond threshold. Vacant (0,3) touches road -> park there helps.
            var s = TestGrids.Build(new[] { "HVVVVVVVVVVP", "RRRRRRRRRRRR" });
            CityMetrics before = Eval(s);
            Assert.AreEqual(0.0, before.AccessPct, 1e-9);

            var withPark = s.WithCell(s.Get(0, 3).With(LandUse.Park, 0, 0));
            CityMetrics after = MetricEngine.Evaluate(withPark, s, TestGrids.Scenario);
            Assert.AreEqual(100.0, after.AccessPct, 1e-9);
            Assert.Greater(after.ParkSharePct, before.ParkSharePct);

            // A vacant cell with no road access: park there changes share but not access.
            var s2 = TestGrids.Build(new[] { "HRVVV", "VRVVV", "VRRRV", "VVVVV" });
            CityMetrics b2 = Eval(s2);
            var p2 = s2.WithCell(s2.Get(0, 4).With(LandUse.Park, 0, 0));
            CityMetrics a2 = MetricEngine.Evaluate(p2, s2, TestGrids.Scenario);
            Assert.Greater(a2.ParkSharePct, b2.ParkSharePct);
            Assert.AreEqual(b2.AccessPct, a2.AccessPct, 1e-9);
        }
    }
}
