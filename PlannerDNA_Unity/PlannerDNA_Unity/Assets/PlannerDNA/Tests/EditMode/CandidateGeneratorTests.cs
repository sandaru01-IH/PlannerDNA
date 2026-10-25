using System;
using System.Collections.Generic;
using NUnit.Framework;
using PlannerDNA.Core;
using PlannerDNA.Metrics;
using PlannerDNA.Storage;
using PlannerDNA.Suggestions;
using UnityEngine;

namespace PlannerDNA.Tests
{
    public class CandidateGeneratorTests
    {
        private static CandidateSet Generate(int seed, out PlanningSession session)
        {
            session = PlanningSession.Start(seed);
            return CandidateGenerator.Generate(session.Baseline, session.Scenario, session.BaselineMetrics, seed);
        }

        [TestCase(42)]
        [TestCase(7)]
        [TestCase(2026)]
        public void SameSeed_GivesIdenticalCandidates(int seed)
        {
            CandidateSet a = Generate(seed, out _), b = Generate(seed, out _);
            Assert.AreEqual(a.Candidates.Count, b.Candidates.Count);
            for (int i = 0; i < a.Candidates.Count; i++)
            {
                Assert.AreEqual(a.Candidates[i].CandidateId, b.Candidates[i].CandidateId);
                Assert.AreEqual(a.Candidates[i].StateHash, b.Candidates[i].StateHash);
                Assert.AreEqual(a.Candidates[i].Description, b.Candidates[i].Description);
            }
            Assert.AreEqual(Versions.CandidateGeneratorVersion, a.GeneratorVersion);
        }

        [TestCase(42)]
        [TestCase(7)]
        [TestCase(99)]
        public void Candidates_AreFeasibleDistinctChangedAndOnlyTouchEditableParcels(int seed)
        {
            CandidateSet set = Generate(seed, out PlanningSession s);
            Assert.GreaterOrEqual(set.Candidates.Count, 10, "enough candidates to rank");
            Assert.LessOrEqual(set.Candidates.Count, CandidateGenerator.DefaultCount);
            var hashes = new HashSet<string>();
            foreach (CandidateDesign c in set.Candidates)
            {
                Assert.IsTrue(c.Evaluation.Feasible, $"{c.CandidateId} infeasible: {string.Join(",", c.Evaluation.Constraints.FailedIds())}");
                Assert.IsTrue(hashes.Add(c.StateHash), "no exact duplicates");
                Assert.AreNotEqual(s.BaselineHash, c.StateHash, "never the unchanged baseline");
                Assert.IsNotEmpty(c.ChangedCellIds);
                Assert.IsFalse(string.IsNullOrEmpty(c.Description));
                for (int i = 0; i < c.State.CellCount; i++)
                {
                    CellData before = s.Baseline.Get(i), after = c.State.Get(i);
                    if (!before.Editable) Assert.AreEqual(before, after, $"{c.CandidateId} changed fixed parcel {before.Id}");
                }
            }
        }

        [Test]
        public void Candidates_HaveNoNearDuplicateFeatureVectors()
        {
            CandidateSet set = Generate(42, out _);
            for (int i = 0; i < set.Candidates.Count; i++)
            {
                var others = set.Candidates.GetRange(0, i);
                Assert.IsFalse(CandidateGenerator.IsNearDuplicate(set.Candidates[i].Features, others), set.Candidates[i].CandidateId);
            }
        }

        [Test]
        public void CandidateMetricsAndFeatures_MatchTheMetricEngine()
        {
            CandidateSet set = Generate(42, out PlanningSession s);
            foreach (CandidateDesign c in set.Candidates)
            {
                CityMetrics m = MetricEngine.Evaluate(c.State, s.Baseline, s.Scenario);
                Assert.AreEqual(m.HousingUnits, c.Metrics.HousingUnits);
                Assert.AreEqual(m.SpentCredits, c.Metrics.SpentCredits);
                Assert.AreEqual(m.AccessPct, c.Metrics.AccessPct, 1e-12);
                FeatureVectorV1 f = FeatureExtractor.Extract(m, s.BaselineMetrics, s.Scenario);
                Assert.AreEqual(f.delta_access_fraction, c.Features.delta_access_fraction, 1e-12);
                Assert.AreEqual(f.negative_budget_fraction, c.Features.negative_budget_fraction, 1e-12);
                Assert.LessOrEqual(c.Metrics.SpentCredits, s.Scenario.BudgetLimit);
            }
        }

        [Test]
        public void RankRequest_SerialisesEveryCandidateWithSnapshotAndFeatures()
        {
            CandidateSet set = Generate(42, out PlanningSession s);
            RankCandidatesRequestDto dto = RankCandidatesRequestDto.From(set, s.Baseline.ScenarioId, 5);
            string json = JsonUtility.ToJson(dto);
            RankCandidatesRequestDto back = JsonUtility.FromJson<RankCandidatesRequestDto>(json);
            Assert.AreEqual(set.Candidates.Count, back.candidates.Count);
            Assert.AreEqual(Versions.CandidateGeneratorVersion, back.generator_version);
            Assert.AreEqual(42, back.generator_seed);
            Assert.AreEqual(s.Baseline.ScenarioId, back.scenario_id);
            CandidateDto first = back.candidates[0];
            Assert.IsTrue(first.feasible);
            Assert.AreEqual(set.Candidates[0].StateHash, first.state_hash);
            Assert.AreEqual(set.Candidates[0].StateHash, CitySerializer.FromDto(first.snapshot).ContentHash());
            Assert.AreEqual(Versions.FeatureVersion, first.features.feature_version);
        }

        [Test]
        public void ModelResponses_ParseFromBackendJson()
        {
            const string current =
                "{\"has_model\":true,\"message\":\"m\",\"readiness\":{\"can_train\":true,\"code\":\"ready\",\"message\":\"Training samples: 12 unique direct comparisons across 3 scenarios.\"," +
                "\"reasons\":[],\"warnings\":[\"w\"],\"eligible_comparisons\":12,\"informative_comparisons\":12,\"scenarios\":3,\"rank\":5," +
                "\"thresholds\":{\"min_comparisons\":10,\"min_scenarios\":2,\"min_rank\":2,\"suggested_comparisons\":30,\"suggested_scenarios\":5}," +
                "\"excluded\":{\"all_comparisons\":13,\"ties_and_skips\":1,\"evaluation_mode\":0,\"infeasible\":0},\"active_model_version\":\"pm-x\"}," +
                "\"model\":{\"model_version\":\"pm-x\",\"status\":\"active\",\"unique_comparisons\":12,\"scenario_count\":3," +
                "\"coefficients\":[{\"feature\":\"delta_access_fraction\",\"label\":\"Park access\",\"weight_standardised\":1.2,\"varied\":true,\"direction\":\"positive\"}]," +
                "\"diagnostics\":{\"available\":true,\"model\":{\"n\":12,\"accuracy\":0.75,\"has_log_loss\":true}," +
                "\"baselines\":[{\"name\":\"chance\",\"label\":\"Chance\",\"n\":12,\"accuracy\":0.5}]},\"warnings\":[],\"caveats\":[\"c\"]}}";
            CurrentModelDto dto = JsonUtility.FromJson<CurrentModelDto>(current);
            Assert.IsTrue(dto.has_model);
            Assert.AreEqual(10, dto.readiness.thresholds.min_comparisons);
            Assert.AreEqual(1, dto.readiness.excluded.ties_and_skips);
            Assert.AreEqual(13, dto.readiness.excluded.all_comparisons);
            Assert.AreEqual("positive", dto.model.coefficients[0].direction);
            Assert.AreEqual(0.75, dto.model.diagnostics.model.accuracy, 1e-12);
            Assert.AreEqual("Chance", dto.model.diagnostics.baselines[0].label);

            ApiErrorEnvelopeDto error = JsonUtility.FromJson<ApiErrorEnvelopeDto>(
                "{\"error\":{\"code\":\"not_enough_comparisons\",\"message\":\"Need 10\",\"details\":{}}}");
            Assert.AreEqual("not_enough_comparisons", error.error.code);
        }
    }
}
