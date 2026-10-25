using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using PlannerDNA.Core;
using PlannerDNA.Generation;
using PlannerDNA.Metrics;
using PlannerDNA.Suggestions;

namespace PlannerDNA.Evaluation
{
    /// <summary>The pair shown in one blinded trial: two feasible candidates of the same unseen scenario.</summary>
    public sealed class EvaluationPair
    {
        public CandidateDesign A;
        public CandidateDesign B;
        /// <summary>True when each design is better on at least one measured outcome (a genuine trade-off).</summary>
        public bool TradeOff;
        public int TradeOffPairs;
        public int InformativePairs;
    }

    /// <summary>
    /// Pair rule evalpair-1.0.0. It uses no model and no preference information, so the pairs a
    /// frozen model is tested on cannot be chosen to flatter it: among the feasible candidates of
    /// <see cref="CandidateGenerator"/> for the pre-registered candidate seed, keep the pairs that are
    /// trade-offs (neither design is at least as good on every feature), and draw one with a
    /// DeterministicRng seeded by the same candidate seed. Without any trade-off pair, any pair whose
    /// features differ is used. The same seed always gives the same pair.
    /// </summary>
    public static class EvaluationPairPicker
    {
        public const string PairRule = "evalpair-1.0.0";
        /// <summary>Same tolerance as the backend's pairwise.ZERO_TOLERANCE.</summary>
        public const double ZeroTolerance = 1e-12;

        public static EvaluationPair Pick(CandidateSet set, int candidateSeed)
        {
            if (set == null) throw new ArgumentNullException(nameof(set));
            var tradeOffs = new List<(int, int)>();
            var informative = new List<(int, int)>();
            List<CandidateDesign> c = set.Candidates;
            for (int i = 0; i < c.Count; i++)
            {
                if (!c[i].Evaluation.Feasible) continue;
                for (int j = i + 1; j < c.Count; j++)
                {
                    if (!c[j].Evaluation.Feasible || c[i].StateHash == c[j].StateHash) continue;
                    double[] d = Difference(c[i].Features, c[j].Features);
                    bool aBetter = false, bBetter = false;
                    foreach (double v in d)
                    {
                        if (v > ZeroTolerance) aBetter = true;
                        else if (v < -ZeroTolerance) bBetter = true;
                    }
                    if (!aBetter && !bBetter) continue;
                    informative.Add((i, j));
                    if (aBetter && bBetter) tradeOffs.Add((i, j));
                }
            }
            List<(int, int)> pool = tradeOffs.Count > 0 ? tradeOffs : informative;
            if (pool.Count == 0) return null;
            var rng = new DeterministicRng(unchecked((ulong)(uint)candidateSeed * 0xD1B54A32D192ED03UL ^ 0x5EA1EDUL));
            (int a, int b) = pool[rng.Range(0, pool.Count)];
            return new EvaluationPair
            {
                A = c[a], B = c[b], TradeOff = tradeOffs.Count > 0,
                TradeOffPairs = tradeOffs.Count, InformativePairs = informative.Count,
            };
        }

        public static double[] Difference(FeatureVectorV1 a, FeatureVectorV1 b) => new[]
        {
            a.delta_access_fraction - b.delta_access_fraction,
            a.delta_housing_fraction - b.delta_housing_fraction,
            a.delta_park_share_fraction - b.delta_park_share_fraction,
            a.negative_budget_fraction - b.negative_budget_fraction,
            a.negative_lost_housing_fraction - b.negative_lost_housing_fraction,
        };
    }

    public static class EvaluationProtocol
    {
        public const string ProtocolVersion = "blinded-eval-1.0.0";
        public const int SeedRangeLow = 100_000;
        public const int SeedRangeHigh = 1_000_000_000;

        /// <summary>
        /// Draws scenario seeds the person has never seen: not in the model's training set, without
        /// any locally recorded choice, and generatable. The backend re-checks against every stored
        /// choice and earlier evaluation, and refuses the plan if any scenario is not unseen.
        /// </summary>
        public static List<int> DrawUnseenSeeds(int count, ICollection<string> excludedScenarioIds, Random random)
        {
            if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
            if (random == null) throw new ArgumentNullException(nameof(random));
            var seeds = new List<int>();
            int attempts = 0;
            while (seeds.Count < count)
            {
                if (++attempts > count * 50) throw new InvalidOperationException("Could not find enough generatable unseen scenarios.");
                int seed = random.Next(SeedRangeLow, SeedRangeHigh);
                if (seeds.Contains(seed)) continue;
                if (excludedScenarioIds != null && excludedScenarioIds.Contains(GreenSpaceScenarioGenerator.ScenarioIdFor(seed))) continue;
                try { GreenSpaceScenarioGenerator.Generate(seed); }
                catch (GenerationException) { continue; }
                seeds.Add(seed);
            }
            return seeds;
        }

        public static string Sha256Hex(string text)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>
        /// True when the revealed payload hashes to the seal received before the choice and names this
        /// trial and pair, so the prediction cannot have been changed after the choice was made.
        /// </summary>
        public static bool VerifySeal(string sealedPayload, string sealReceivedBeforeChoice, string trialId, string altA, string altB)
        {
            if (string.IsNullOrEmpty(sealedPayload) || string.IsNullOrEmpty(sealReceivedBeforeChoice)) return false;
            if (!string.Equals(Sha256Hex(sealedPayload), sealReceivedBeforeChoice, StringComparison.Ordinal)) return false;
            return sealedPayload.Contains($"\"trial_id\":\"{trialId}\"")
                && sealedPayload.Contains($"\"alternative_a_id\":\"{altA}\"")
                && sealedPayload.Contains($"\"alternative_b_id\":\"{altB}\"");
        }
    }
}
