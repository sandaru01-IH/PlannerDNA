using System;
using System.Collections.Generic;
using System.Text;
using PlannerDNA.Commands;
using PlannerDNA.Core;
using PlannerDNA.Metrics;

namespace PlannerDNA.Suggestions
{
    /// <summary>One feasible design produced by the rule-based search, with its full evaluation.</summary>
    public sealed class CandidateDesign
    {
        public string CandidateId;
        public string Description;
        public AlternativeSnapshot Evaluation;

        public CityState State => Evaluation.State;
        public CityMetrics Metrics => Evaluation.Metrics;
        public FeatureVectorV1 Features => Evaluation.Features;
        public string StateHash => Evaluation.StateHash;
        public List<string> ChangedCellIds => Evaluation.ChangedCellIds;
    }

    public sealed class CandidateSet
    {
        public string GeneratorVersion;
        public int Seed;
        public int Attempts;
        public int RejectedEdits;
        public int Infeasible;
        public int Duplicates;
        public int NearDuplicates;
        public readonly List<CandidateDesign> Candidates = new List<CandidateDesign>();
    }

    /// <summary>
    /// Seeded, bounded rule-based search for plausible alternative designs (candidates-1.0.0).
    /// Each candidate applies 1-4 single-parcel edits to the BASELINE, each validated by the same
    /// ActionValidator as manual edits, and is evaluated by the same MetricEngine and constraints.
    /// Infeasible designs, exact duplicates and near-duplicates (every feature within 0.005) are
    /// dropped. Nothing here knows about preferences: ranking is the backend model's job.
    /// </summary>
    public static class CandidateGenerator
    {
        public const int DefaultCount = 30;
        public const int MaxEditsPerCandidate = 4;
        public const int MaxAttempts = 800;
        public const double NearDuplicateTolerance = 0.005;

        private enum MoveKind { ParkOnVacant, ParkOnBuilding, RaiseFloors, NewHousing, CommercialToHousing }

        private readonly struct Move
        {
            public readonly MoveKind Kind;
            public readonly int Row, Col;
            public readonly EditAction Action;

            public Move(MoveKind kind, int row, int col, EditAction action)
            {
                Kind = kind;
                Row = row;
                Col = col;
                Action = action;
            }
        }

        public static CandidateSet Generate(CityState baseline, ScenarioDefinition scenario, CityMetrics baselineMetrics, int seed,
            int count = DefaultCount)
        {
            if (baseline == null) throw new ArgumentNullException(nameof(baseline));
            if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));

            Dictionary<MoveKind, List<Move>> moves = MovesFor(baseline);
            var kinds = new List<MoveKind>();
            foreach (KeyValuePair<MoveKind, List<Move>> pair in moves)
                if (pair.Value.Count > 0) kinds.Add(pair.Key);
            kinds.Sort();

            var set = new CandidateSet { GeneratorVersion = Versions.CandidateGeneratorVersion, Seed = seed };
            if (kinds.Count == 0) return set;

            var rng = new DeterministicRng(unchecked((ulong)(uint)seed * 0x9E3779B97F4A7C15UL ^ 0xCA4D1DA7E5UL));
            var seen = new HashSet<string> { baseline.ContentHash() };
            var steps = new List<Move>();

            while (set.Attempts < MaxAttempts && set.Candidates.Count < count)
            {
                set.Attempts++;
                CityState state = baseline;
                steps.Clear();
                int edits = rng.Range(1, MaxEditsPerCandidate + 1);
                for (int e = 0; e < edits; e++)
                {
                    List<Move> pool = moves[kinds[rng.Range(0, kinds.Count)]];
                    Move move = pool[rng.Range(0, pool.Count)];
                    ValidationResult v = ActionValidator.Validate(state, baseline, scenario, move.Action, move.Row, move.Col);
                    if (!v.Ok) { set.RejectedEdits++; continue; }
                    state = state.WithCell(v.Proposed);
                    steps.Add(move);
                }
                if (steps.Count == 0) continue;
                if (!seen.Add(state.ContentHash())) { set.Duplicates++; continue; }

                string id = $"cand-{seed}-{set.Candidates.Count + 1:D2}";
                AlternativeSnapshot evaluation = AlternativeSnapshot.Create(AlternativeSlot.A, id, string.Empty, DateTime.MinValue,
                    state, baseline, baselineMetrics, scenario);
                if (!evaluation.Feasible) { set.Infeasible++; continue; }
                if (IsNearDuplicate(evaluation.Features, set.Candidates)) { set.NearDuplicates++; continue; }

                set.Candidates.Add(new CandidateDesign { CandidateId = id, Description = Describe(steps), Evaluation = evaluation });
            }
            return set;
        }

        private static Dictionary<MoveKind, List<Move>> MovesFor(CityState baseline)
        {
            var moves = new Dictionary<MoveKind, List<Move>>();
            foreach (MoveKind kind in Enum.GetValues(typeof(MoveKind))) moves[kind] = new List<Move>();
            for (int i = 0; i < baseline.CellCount; i++)
            {
                CellData c = baseline.Get(i);
                if (!c.Editable || c.Zoning == Zoning.Protected) continue;
                switch (c.Use)
                {
                    case LandUse.Vacant:
                        moves[MoveKind.ParkOnVacant].Add(new Move(MoveKind.ParkOnVacant, c.Row, c.Col, EditAction.SetPark));
                        moves[MoveKind.NewHousing].Add(new Move(MoveKind.NewHousing, c.Row, c.Col, EditAction.SetResidential));
                        break;
                    case LandUse.Residential:
                        moves[MoveKind.RaiseFloors].Add(new Move(MoveKind.RaiseFloors, c.Row, c.Col, EditAction.RaiseFloors));
                        moves[MoveKind.ParkOnBuilding].Add(new Move(MoveKind.ParkOnBuilding, c.Row, c.Col, EditAction.SetPark));
                        break;
                    case LandUse.Commercial:
                        moves[MoveKind.ParkOnBuilding].Add(new Move(MoveKind.ParkOnBuilding, c.Row, c.Col, EditAction.SetPark));
                        moves[MoveKind.CommercialToHousing].Add(new Move(MoveKind.CommercialToHousing, c.Row, c.Col, EditAction.SetResidential));
                        break;
                }
            }
            return moves;
        }

        public static bool IsNearDuplicate(FeatureVectorV1 f, List<CandidateDesign> accepted)
        {
            foreach (CandidateDesign other in accepted)
            {
                FeatureVectorV1 g = other.Features;
                if (Math.Abs(f.delta_access_fraction - g.delta_access_fraction) <= NearDuplicateTolerance
                    && Math.Abs(f.delta_housing_fraction - g.delta_housing_fraction) <= NearDuplicateTolerance
                    && Math.Abs(f.delta_park_share_fraction - g.delta_park_share_fraction) <= NearDuplicateTolerance
                    && Math.Abs(f.negative_budget_fraction - g.negative_budget_fraction) <= NearDuplicateTolerance
                    && Math.Abs(f.negative_lost_housing_fraction - g.negative_lost_housing_fraction) <= NearDuplicateTolerance)
                    return true;
            }
            return false;
        }

        private static string Describe(List<Move> steps)
        {
            var order = new List<string>();
            var counts = new Dictionary<string, int>();
            foreach (Move m in steps)
            {
                string key = $"{m.Action.DisplayName()} on {CellIds.Format(m.Row, m.Col)}";
                if (!counts.ContainsKey(key)) { counts[key] = 0; order.Add(key); }
                counts[key]++;
            }
            var sb = new StringBuilder();
            foreach (string key in order)
            {
                if (sb.Length > 0) sb.Append("; ");
                sb.Append(key);
                if (counts[key] > 1) sb.Append($" (x{counts[key]})");
            }
            return sb.ToString();
        }
    }
}
