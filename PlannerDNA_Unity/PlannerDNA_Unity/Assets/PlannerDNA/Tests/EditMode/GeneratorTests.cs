using System.Collections.Generic;
using NUnit.Framework;
using PlannerDNA.Core;
using PlannerDNA.Generation;
using PlannerDNA.Metrics;
using PlannerDNA.Storage;

namespace PlannerDNA.Tests
{
    public class GeneratorTests
    {
        [Test]
        public void DemoSeed_GeneratesIdenticalCityTwice()
        {
            var a = GreenSpaceScenarioGenerator.Generate(GreenSpaceScenarioGenerator.DemoSeed);
            var b = GreenSpaceScenarioGenerator.Generate(GreenSpaceScenarioGenerator.DemoSeed);
            Assert.IsTrue(a.Baseline.ContentEquals(b.Baseline));
            Assert.AreEqual(a.Baseline.ContentHash(), b.Baseline.ContentHash());
            Assert.AreEqual(CitySerializer.ToJson(a.Baseline), CitySerializer.ToJson(b.Baseline));
            Assert.AreEqual(a.Attempt, b.Attempt);

            CityMetrics ma = MetricEngine.EvaluateBaseline(a.Baseline, a.Definition);
            CityMetrics mb = MetricEngine.EvaluateBaseline(b.Baseline, b.Definition);
            Assert.AreEqual(ma.AccessPct, mb.AccessPct);
            Assert.AreEqual(ma.HousingUnits, mb.HousingUnits);
        }

        [Test]
        public void DifferentSeeds_ProduceDifferentCities()
        {
            var a = GreenSpaceScenarioGenerator.Generate(1).Baseline;
            var b = GreenSpaceScenarioGenerator.Generate(2).Baseline;
            Assert.AreNotEqual(a.ContentHash(), b.ContentHash());
        }

        [Test]
        public void Generated_HasExpectedDimensionsAndVersions()
        {
            var s = GreenSpaceScenarioGenerator.Generate(42).Baseline;
            Assert.AreEqual(20, s.Width);
            Assert.AreEqual(20, s.Height);
            Assert.AreEqual(40, s.CellMetres);
            Assert.AreEqual("green-42", s.ScenarioId);
            Assert.AreEqual(Versions.GeneratorVersion, s.GeneratorVersion);
            Assert.AreEqual(Versions.MetricVersion, s.MetricVersion);
        }

        [TestCase(1)]
        [TestCase(42)]
        [TestCase(1234)]
        [TestCase(99999)]
        [TestCase(-5)]
        public void Generated_SatisfiesAllRequirements(int seed)
        {
            GeneratedScenario g = GreenSpaceScenarioGenerator.Generate(seed);
            CityState s = g.Baseline;
            Assert.IsTrue(AccessibilityCalculator.RoadNetworkConnected(s), "roads connected");

            var scratch = new List<int>();
            int parks = 0, vacant = 0, civic = 0;
            for (int i = 0; i < s.CellCount; i++)
            {
                CellData c = s.Get(i);
                if (c.Use == LandUse.Road) { Assert.IsFalse(c.Editable); continue; }
                Assert.IsTrue(AccessibilityCalculator.HasRoadAccess(s, i, scratch), $"{c.Id} has road access");
                if (c.Use == LandUse.Park) parks++;
                if (c.Use == LandUse.Vacant) vacant++;
                if (c.Use == LandUse.Civic) { civic++; Assert.IsFalse(c.Editable); }
                if (c.Use.IsBuilding()) Assert.LessOrEqual(c.Floors, g.Definition.MaxFloors);
                if (c.Use == LandUse.Residential) Assert.AreEqual(4, c.UnitsPerFloor);
                else Assert.AreEqual(0, c.UnitsPerFloor);
            }
            Assert.GreaterOrEqual(parks, 1);
            Assert.GreaterOrEqual(vacant, GreenSpaceScenarioGenerator.MinVacantCells);
            Assert.GreaterOrEqual(civic, 1);

            CityMetrics m = MetricEngine.EvaluateBaseline(s, g.Definition);
            Assert.Greater(m.HousingUnits, 0);
            Assert.Less(m.AccessPct, GreenSpaceScenarioGenerator.MaxBaselineAccessPct);
            Assert.IsNotNull(g.ExampleImprovingCellId);
        }

        [Test]
        public void ExampleImprovingCell_ActuallyImprovesAccessWithinConstraints()
        {
            GeneratedScenario g = GreenSpaceScenarioGenerator.Generate(42);
            Assert.IsTrue(g.Baseline.TryGet(g.ExampleImprovingCellId, out CellData cell));
            Assert.AreEqual(LandUse.Vacant, cell.Use);
            CityMetrics baseline = MetricEngine.EvaluateBaseline(g.Baseline, g.Definition);
            CityState edited = g.Baseline.WithCell(cell.With(LandUse.Park, 0, 0));
            CityMetrics m = MetricEngine.Evaluate(edited, g.Baseline, g.Definition);
            Assert.Greater(m.AccessPct, baseline.AccessPct);
            Assert.IsTrue(ConstraintEvaluator.Evaluate(edited, m, baseline, g.Definition).Feasible);
        }

        [Test]
        public void ManySeeds_GenerateWithoutException()
        {
            for (int seed = 0; seed < 60; seed++)
                Assert.DoesNotThrow(() => GreenSpaceScenarioGenerator.Generate(seed), $"seed {seed}");
        }

        [Test]
        public void Rng_IsReproducibleAndSeedSensitive()
        {
            var a = DeterministicRng.ForAttempt(42, 0);
            var b = DeterministicRng.ForAttempt(42, 0);
            var c = DeterministicRng.ForAttempt(42, 1);
            for (int i = 0; i < 100; i++)
            {
                ulong va = a.NextUInt64();
                Assert.AreEqual(va, b.NextUInt64());
                Assert.AreNotEqual(va, c.NextUInt64());
            }
        }

        [Test]
        public void Rng_SplitMix64_MatchesReferenceSequence()
        {
            // Reference values for SplitMix64 with state 0 (published test vector).
            var rng = new DeterministicRng(0);
            Assert.AreEqual(0xE220A8397B1DCDAFUL, rng.NextUInt64());
            Assert.AreEqual(0x6E789E6AA1B965F4UL, rng.NextUInt64());
            Assert.AreEqual(0x06C45D188009454FUL, rng.NextUInt64());
        }
    }
}
