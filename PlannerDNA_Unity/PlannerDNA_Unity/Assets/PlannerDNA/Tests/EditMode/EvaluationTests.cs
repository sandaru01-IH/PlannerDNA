using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using PlannerDNA.Core;
using PlannerDNA.Evaluation;
using PlannerDNA.Generation;
using PlannerDNA.Storage;
using PlannerDNA.Suggestions;
using UnityEngine;

namespace PlannerDNA.Tests
{
    /// <summary>Phase 4: the pair rule, unseen-seed draw, seal check and evaluation records (no backend involved).</summary>
    public sealed class EvaluationTests
    {
        private static CandidateSet Candidates(PlanningSession s, int seed) =>
            CandidateGenerator.Generate(s.Baseline, s.Scenario, s.BaselineMetrics, seed);

        [Test]
        public void PairPicker_IsDeterministicFeasibleAndPrefersTradeOffs()
        {
            var session = PlanningSession.Start(42);
            int tradeOffs = 0;
            foreach (int seed in new[] { 1001, 1002, 1003, 1004, 1005 })
            {
                EvaluationPair first = EvaluationPairPicker.Pick(Candidates(session, seed), seed);
                EvaluationPair again = EvaluationPairPicker.Pick(Candidates(session, seed), seed);
                Assert.IsNotNull(first, $"seed {seed}");
                Assert.AreEqual(first.A.CandidateId, again.A.CandidateId, "same seed, same pair");
                Assert.AreEqual(first.B.CandidateId, again.B.CandidateId);
                Assert.IsTrue(first.A.Evaluation.Feasible && first.B.Evaluation.Feasible, "evaluation pairs are feasible");
                Assert.AreNotEqual(first.A.StateHash, first.B.StateHash);
                double[] d = EvaluationPairPicker.Difference(first.A.Features, first.B.Features);
                bool pos = false, neg = false;
                foreach (double v in d) { pos |= v > EvaluationPairPicker.ZeroTolerance; neg |= v < -EvaluationPairPicker.ZeroTolerance; }
                Assert.IsTrue(pos || neg, "the two designs differ in a measured outcome");
                Assert.AreEqual(first.TradeOffPairs > 0, first.TradeOff);
                if (first.TradeOff)
                {
                    Assert.IsTrue(pos && neg, "a trade-off pair: each design is better on something");
                    tradeOffs++;
                }
            }
            Assert.Greater(tradeOffs, 0, "the generator yields trade-off pairs for typical seeds");
        }

        [Test]
        public void PairPicker_ReturnsNullWithoutTwoDifferentFeasibleDesigns()
        {
            var session = PlanningSession.Start(42);
            CandidateSet set = Candidates(session, 7);
            var one = new CandidateSet { Seed = 7 };
            one.Candidates.Add(set.Candidates[0]);
            Assert.IsNull(EvaluationPairPicker.Pick(one, 7));
            var same = new CandidateSet { Seed = 7 };
            same.Candidates.Add(set.Candidates[0]);
            same.Candidates.Add(set.Candidates[0]);
            Assert.IsNull(EvaluationPairPicker.Pick(same, 7), "a design is never paired with itself");
        }

        [Test]
        public void UnseenSeeds_AreDistinctGeneratableAndAvoidExcludedScenarios()
        {
            var first = EvaluationProtocol.DrawUnseenSeeds(4, new HashSet<string>(), new System.Random(5));
            Assert.AreEqual(first, EvaluationProtocol.DrawUnseenSeeds(4, new HashSet<string>(), new System.Random(5)));
            Assert.AreEqual(4, new HashSet<int>(first).Count);
            var excluded = new HashSet<string> { GreenSpaceScenarioGenerator.ScenarioIdFor(first[0]) };
            List<int> other = EvaluationProtocol.DrawUnseenSeeds(4, excluded, new System.Random(5));
            CollectionAssert.DoesNotContain(other, first[0]);
            foreach (int seed in other)
            {
                Assert.GreaterOrEqual(seed, EvaluationProtocol.SeedRangeLow);
                Assert.DoesNotThrow(() => GreenSpaceScenarioGenerator.Generate(seed));
            }
        }

        [Test]
        public void Sha256_MatchesTheStandardVector()
        {
            Assert.AreEqual("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", EvaluationProtocol.Sha256Hex("abc"));
        }

        [Test]
        public void Seal_DetectsAnyChangeToTheRevealedPrediction()
        {
            // Same byte layout as the backend's canonical JSON (sorted keys, no spaces).
            string payload = "{\"alternative_a_id\":\"alt-a\",\"alternative_b_id\":\"alt-b\",\"model_version\":\"pm-1\"," +
                             "\"nonce\":\"00112233445566778899aabbccddeeff\",\"p_a\":0.73,\"plan_id\":\"eval-1\",\"trial_id\":\"trial-1\"}";
            string seal = EvaluationProtocol.Sha256Hex(payload);
            Assert.IsTrue(EvaluationProtocol.VerifySeal(payload, seal, "trial-1", "alt-a", "alt-b"));
            Assert.IsFalse(EvaluationProtocol.VerifySeal(payload.Replace("0.73", "0.27"), seal, "trial-1", "alt-a", "alt-b"), "changed probability");
            Assert.IsFalse(EvaluationProtocol.VerifySeal(payload, seal, "trial-2", "alt-a", "alt-b"), "different trial");
            Assert.IsFalse(EvaluationProtocol.VerifySeal(payload, seal, "trial-1", "alt-b", "alt-a"), "swapped pair");
            Assert.IsFalse(EvaluationProtocol.VerifySeal(payload, "", "trial-1", "alt-a", "alt-b"));
        }

        [Test]
        public void EvaluationPreference_IsLockedBlindedAndNeverTrainingData()
        {
            var session = PlanningSession.Start(42);
            CandidateSet set = Candidates(session, 11);
            EvaluationPair pair = EvaluationPairPicker.Pick(set, 11);
            var logged = new List<TimelineEntryDto>();
            session.EventLogged += logged.Add;
            session.LoadEvaluationPair(pair.A.State, pair.B.State, "blinded pair");
            Assert.IsTrue(session.IsAtBaseline, "the current design is untouched");
            Assert.AreEqual("load_evaluation_pair", logged[logged.Count - 1].action);
            Assert.AreEqual("system", logged[logged.Count - 1].input_source);
            Assert.AreEqual(pair.A.StateHash, session.AlternativeA.StateHash);
            Assert.AreEqual(pair.B.StateHash, session.AlternativeB.StateHash);

            string b = session.AlternativeB.AlternativeId;
            PreferenceDto pref = session.CreateEvaluationPreference("cmp-1", "B", "", "trial-1", "pm-1", b);
            Assert.AreEqual("evaluation", pref.mode);
            Assert.AreEqual("trial-1", pref.evaluation_trial_id);
            Assert.AreEqual("pm-1", pref.model_version_locked);
            Assert.AreEqual(b, pref.shown_left_alternative_id, "the backend's randomised screen order is recorded");
            Assert.IsFalse(pref.training_eligible);

            Assert.Throws<ArgumentException>(() => session.CreateEvaluationPreference("cmp-2", "A", "", "", "pm-1", b));
            Assert.Throws<ArgumentException>(() => session.CreateEvaluationPreference("cmp-2", "A", "", "trial-1", "", b));
            Assert.Throws<ArgumentException>(() => session.CreateEvaluationPreference("cmp-2", "A", "", "trial-1", "pm-1", "alt-other"));

            PreferenceDto train = session.CreatePreference("cmp-3", "A", "");
            Assert.AreEqual("train", train.mode);
            Assert.AreEqual(string.Empty, train.evaluation_trial_id);
            StringAssert.DoesNotContain("evaluation_trial_id\":null", JsonUtility.ToJson(train));
        }

        [Test]
        public void LocalStore_AllowsARepeatOnlyAsAnotherEvaluationTrial()
        {
            string root = Path.Combine(Path.GetTempPath(), "PlannerDNA_EvalTests_" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new LocalDecisionStore(root);
                PreferenceDto Eval(string id, string trial, string a, string b) => new PreferenceDto
                {
                    comparison_id = id, alternative_a_id = a, alternative_b_id = b, choice = "A", mode = "evaluation",
                    evaluation_trial_id = trial, scenario_id = "green-9",
                };
                Assert.IsTrue(store.SavePreference(Eval("cmp-1", "trial-1", "x", "y")).Saved);
                Assert.IsTrue(store.SavePreference(Eval("cmp-2", "trial-2", "x", "y")).Saved, "repeat with sides swapped is its own trial");
                StoreResult sameTrial = store.SavePreference(Eval("cmp-3", "trial-1", "y", "x"));
                Assert.IsFalse(sameTrial.Saved);
                Assert.IsTrue(sameTrial.Duplicate);
                PreferenceDto train = Eval("cmp-4", "", "x", "y");
                train.mode = "train";
                Assert.IsFalse(store.SavePreference(train).Saved, "a practice choice cannot reuse an evaluation pair");
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Test]
        public void BackendJson_WithNullsParsesIntoTheDtos()
        {
            const string json = "{\"plan_id\":\"eval-1\",\"report_sha256\":\"ab\",\"verification\":{\"reproducible\":true}," +
                                "\"report\":{\"summary\":\"s\",\"counts\":{\"scored\":0},\"metrics\":{\"model\":{\"n\":0,\"accuracy\":0.0," +
                                "\"accuracy_ci\":null},\"baselines\":[{\"name\":\"chance\",\"accuracy_ci\":null," +
                                "\"model_minus_baseline_accuracy_ci\":null}]},\"repeat_consistency\":{\"rate\":null,\"rate_ci\":null}," +
                                "\"per_scenario\":[{\"scenario_id\":\"green-1\",\"accuracy\":null}],\"warnings\":[],\"caveats\":[\"c\"]}}";
            var dto = JsonUtility.FromJson<EvaluationReportResponseDto>(json);
            Assert.IsTrue(dto.verification.reproducible);
            Assert.AreEqual("s", dto.report.summary);
            Assert.IsTrue(dto.report.metrics.model.accuracy_ci == null || dto.report.metrics.model.accuracy_ci.Count == 0);
            Assert.AreEqual("chance", dto.report.metrics.baselines[0].name);
            Assert.AreEqual("green-1", dto.report.per_scenario[0].scenario_id);

            const string trial = "{\"status\":\"created\",\"trial\":{\"trial_id\":\"t\",\"p_a\":null,\"revealed\":false,\"sealed_sha256\":\"ff\"}}";
            var t = JsonUtility.FromJson<EvaluationTrialResponseDto>(trial);
            Assert.AreEqual("ff", t.trial.sealed_sha256);
            Assert.IsFalse(t.trial.revealed);

            var overview = JsonUtility.FromJson<EvaluationOverviewDto>("{\"open_plan_id\":\"\",\"open_plan\":null,\"count\":0,\"plans\":[]}");
            Assert.IsFalse(overview.HasOpenPlan);
        }
    }
}
