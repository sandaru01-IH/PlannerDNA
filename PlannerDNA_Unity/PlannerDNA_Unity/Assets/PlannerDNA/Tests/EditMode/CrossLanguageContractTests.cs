using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using PlannerDNA.Core;
using PlannerDNA.Storage;
using UnityEngine;

namespace PlannerDNA.Tests
{
    /// <summary>Pins the C# side of the contract that backend/tests verify from Python.</summary>
    public sealed class CrossLanguageContractTests
    {
        /// <summary>The same city and value are pinned in backend/tests/test_domain.py.</summary>
        [Test]
        public void HashVectorMatchesPython()
        {
            var cells = new List<CellData>
            {
                new CellData(0, 0, LandUse.Road, 0, 0, 0, false, Zoning.Mixed),
                new CellData(0, 1, LandUse.Residential, 3, 4, 8, true, Zoning.ResidentialOnly),
                new CellData(0, 2, LandUse.Park, 0, 0, 0, true, Zoning.Protected),
                new CellData(1, 0, LandUse.Commercial, 2, 0, 6, true, Zoning.Mixed),
                new CellData(1, 1, LandUse.Vacant, 0, 0, 8, true, Zoning.Mixed),
                new CellData(1, 2, LandUse.Civic, 1, 0, 2, false, Zoning.Mixed),
            };
            var city = new CityState("hash-vector", 7, "gen-x", "metrics-1.0.0", 1, 3, 2, 40, cells);
            Assert.AreEqual("98a58308d3a8af2f", city.ContentHash());
        }

        private static string FixturePath =>
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", ContractFixtures.RelativePath));

        [Test]
        public void BackendFixtureFileIsUpToDate()
        {
            Assert.IsTrue(File.Exists(FixturePath), "Missing " + FixturePath + ". Run PlannerDNA > Export Backend Contract Fixtures.");
            string onDisk = File.ReadAllText(FixturePath).Replace("\r\n", "\n");
            Assert.AreEqual(ContractFixtures.BuildJson(), onDisk,
                "The planning rules changed since the fixtures were exported. Re-export them and re-run the backend tests.");
        }

        [Test]
        public void FixturesAreDeterministicAndCoverDecisions()
        {
            Assert.AreEqual(ContractFixtures.BuildJson(), ContractFixtures.BuildJson());
            ContractFixtureFile file = ContractFixtures.Build();
            Assert.AreEqual(ContractFixtures.Seeds.Length, file.cases.Count);
            foreach (ContractFixtureCase c in file.cases)
            {
                Assert.GreaterOrEqual(c.alternatives.Count, 4);
                Assert.GreaterOrEqual(c.preferences.Count, 2, $"seed {c.seed}");
                Assert.Greater(c.events.Count, 5);
            }
        }
    }
}
