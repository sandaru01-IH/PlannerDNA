using System;
using System.Collections.Generic;
using PlannerDNA.Commands;
using PlannerDNA.Core;
using PlannerDNA.Suggestions;
using UnityEngine;

namespace PlannerDNA.Storage
{
    [Serializable]
    public sealed class ContractFixtureCase
    {
        public int seed;
        public ScenarioRegistrationDto scenario;
        public SessionStartDto session;
        public List<TimelineEntryDto> events = new List<TimelineEntryDto>();
        public List<AlternativeDto> alternatives = new List<AlternativeDto>();
        public List<PreferenceDto> preferences = new List<PreferenceDto>();
        public RankCandidatesRequestDto rank_request;
    }

    [Serializable]
    public sealed class ContractFixtureFile
    {
        public string generated_by;
        public string note;
        public List<ContractFixtureCase> cases = new List<ContractFixtureCase>();
    }

    /// <summary>
    /// Deterministic records produced by the real Unity planning code (fixed clock and ids), serialized
    /// exactly as the outbox serializes them. The backend's pytest suite posts them to prove that the
    /// Python recomputation of hashes, metrics, constraints and features agrees with C#.
    /// Regenerate with PlannerDNA > Export Backend Contract Fixtures after changing any rule.
    /// </summary>
    public static class ContractFixtures
    {
        public static readonly int[] Seeds = { 42, 7, 2026 };
        public const string RelativePath = "backend/tests/fixtures/unity_metrics_fixtures.json";

        public static string BuildJson() => JsonUtility.ToJson(Build(), false) + "\n";

        public static ContractFixtureFile Build()
        {
            var file = new ContractFixtureFile
            {
                generated_by = "PlannerDNA.Storage.ContractFixtures",
                note = "Synthetic. Generated deterministically from the Unity planning code; do not edit by hand.",
            };
            foreach (int seed in Seeds) file.cases.Add(BuildCase(seed));
            return file;
        }

        private static ContractFixtureCase BuildCase(int seed)
        {
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            int tick = 0, id = 0;
            Func<DateTime> clock = () => start.AddSeconds(tick++);
            Func<string> newId = () => $"fx{seed}n{++id:D5}";

            PlanningSession session = PlanningSession.Start(seed, null, clock, newId);
            var c = new ContractFixtureCase
            {
                seed = seed,
                scenario = SyncPayloads.Scenario(session, start),
                session = SyncPayloads.Session(session, start),
            };
            session.EventLogged += e => c.events.Add(e);

            // A: the generator's example improving edit (a park on a vacant parcel).
            if (CellIds.TryParse(session.ExampleImprovingCellId, out int er, out int ec))
                session.Execute(EditAction.SetPark, er, ec, InputSource.Mouse);
            AlternativeSnapshot a = session.SaveAlternative(AlternativeSlot.A);
            c.alternatives.Add(a.ToDto());

            // B: undo, then add height to the first residential parcels that allow it.
            session.Undo(InputSource.Keyboard);
            ApplyToFirst(session, EditAction.RaiseFloors, LandUse.Residential, 2);
            ApplyToFirst(session, EditAction.SetResidential, LandUse.Vacant, 1);
            AlternativeSnapshot b = session.SaveAlternative(AlternativeSlot.B);
            c.alternatives.Add(b.ToDto());
            AddPreference(session, c, PlanningSession.ChoiceA);

            // An infeasible A: demolishing housing until the housing floor fails.
            session.RestoreBaseline(InputSource.Mouse);
            for (int i = 0; i < 12 && session.CurrentConstraints.Feasible; i++)
                ApplyToFirst(session, EditAction.Demolish, LandUse.Residential, 1);
            AlternativeSnapshot infeasible = session.SaveAlternative(AlternativeSlot.A);
            c.alternatives.Add(infeasible.ToDto());
            AddPreference(session, c, PlanningSession.ChoiceB);

            // A costly B: many conversions to parks, typically exceeding the budget.
            session.RestoreBaseline(InputSource.Mouse);
            for (int i = 0; i < 8; i++)
                ApplyToFirst(session, EditAction.SetPark, LandUse.Commercial, 1);
            AlternativeSnapshot costly = session.SaveAlternative(AlternativeSlot.B);
            c.alternatives.Add(costly.ToDto());
            AddPreference(session, c, PlanningSession.ChoiceTie);

            // Seeded candidates exactly as the Learning Lab sends them for ranking.
            CandidateSet candidates = CandidateGenerator.Generate(session.Baseline, session.Scenario, session.BaselineMetrics, seed, 8);
            c.rank_request = RankCandidatesRequestDto.From(candidates, session.Baseline.ScenarioId, 5);
            return c;
        }

        private static void AddPreference(PlanningSession session, ContractFixtureCase c, string choice)
        {
            if (!session.CheckComparison().CanCompare) return;
            PreferenceDto pref = session.CreatePreference(session.NewComparisonId(), choice, "contract fixture");
            c.preferences.Add(pref);
            session.LogDecision(pref);
        }

        /// <summary>Applies an action to the first parcels (row-major) of the given use where it is valid.</summary>
        private static void ApplyToFirst(PlanningSession session, EditAction action, LandUse use, int count)
        {
            int done = 0;
            CityState state = session.Current;
            for (int i = 0; i < state.CellCount && done < count; i++)
            {
                CellData cell = session.Current.Get(i);
                if (cell.Use != use) continue;
                if (session.Execute(action, cell.Row, cell.Col, InputSource.Mouse).Accepted) done++;
            }
        }
    }
}
