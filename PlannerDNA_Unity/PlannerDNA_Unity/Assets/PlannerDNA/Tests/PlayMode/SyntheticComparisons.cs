using System;
using System.Collections;
using NUnit.Framework;
using PlannerDNA.App;
using PlannerDNA.Commands;
using PlannerDNA.Core;
using PlannerDNA.Storage;
using PlannerDNA.Suggestions;
using UnityEngine;

namespace PlannerDNA.Tests
{
    /// <summary>
    /// Builds TEST FIXTURE comparisons through the real app: pairs of generated feasible designs whose
    /// label is decided by a fixed synthetic rule ("prefer more park access"). These are not user
    /// labels; they exist only to check that training recovers a known direction.
    /// </summary>
    public static class SyntheticComparisons
    {
        public const string Reason = "synthetic test fixture: prefer more park access";

        public static int Recorded { get; private set; }

        public static IEnumerator Record(PlannerApp app, int[] seeds, int pairsPerScenario)
        {
            Recorded = 0;
            foreach (int seed in seeds)
            {
                Assert.IsTrue(app.NewChallenge(seed, out string error), error);
                CandidateSet set = app.GenerateCandidates(seed);
                int pairs = 0;
                for (int i = 0; i + 1 < set.Candidates.Count && pairs < pairsPerScenario; i += 2)
                {
                    CandidateDesign x = set.Candidates[i], y = set.Candidates[i + 1];
                    double dx = x.Features.delta_access_fraction, dy = y.Features.delta_access_fraction;
                    if (Math.Abs(dx - dy) < 1e-9) continue;
                    bool betterFirst = pairs % 2 == 0;
                    CandidateDesign better = dx > dy ? x : y, worse = dx > dy ? y : x;
                    CandidateDesign a = betterFirst ? better : worse, b = betterFirst ? worse : better;
                    Load(app, a.State);
                    app.SaveAlternative(AlternativeSlot.A);
                    Load(app, b.State);
                    app.SaveAlternative(AlternativeSlot.B);
                    StoreResult r = app.SubmitPreference(app.Session.NewComparisonId(), betterFirst ? "A" : "B", Reason);
                    Assert.IsTrue(r.Saved, r.Message);
                    pairs++;
                    Recorded++;
                }
                Assert.GreaterOrEqual(pairs, Math.Min(3, pairsPerScenario), $"seed {seed} produced too few informative pairs");
                yield return null;
            }
        }

        private static void Load(PlannerApp app, CityState state)
        {
            if (app.Session.Current.ContentEquals(state)) return;
            CommandOutcome outcome = app.Session.ApplyLoadedDesign(state, InputSource.System, "synthetic test fixture design");
            Assert.IsTrue(outcome.Accepted, outcome.Message);
        }

        public static IEnumerator WaitFor(Func<bool> condition, float seconds, Func<string> message)
        {
            float deadline = Time.realtimeSinceStartup + seconds;
            while (!condition() && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.IsTrue(condition(), message());
        }
    }
}
