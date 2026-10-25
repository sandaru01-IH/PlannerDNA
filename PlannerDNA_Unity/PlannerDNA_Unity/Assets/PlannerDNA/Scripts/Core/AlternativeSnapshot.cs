using System;
using System.Collections.Generic;
using PlannerDNA.Metrics;
using PlannerDNA.Storage;

namespace PlannerDNA.Core
{
    public enum AlternativeSlot
    {
        A,
        B,
    }

    /// <summary>A complete saved design plus everything needed to verify and reproduce it.</summary>
    public sealed class AlternativeSnapshot
    {
        public string AlternativeId;
        public AlternativeSlot Slot;
        public string SessionId;
        public string BaselineHash;
        public string StateHash;
        public DateTime CreatedUtc;
        public CityState State;
        public CityMetrics Metrics;
        public ConstraintReport Constraints;
        public FeatureVectorV1 Features;
        public List<string> ChangedCellIds;

        public bool Feasible => Constraints.Feasible;

        public static AlternativeSnapshot Create(AlternativeSlot slot, string alternativeId, string sessionId, DateTime createdUtc,
            CityState state, CityState baseline, CityMetrics baselineMetrics, ScenarioDefinition scenario)
        {
            CityMetrics metrics = MetricEngine.Evaluate(state, baseline, scenario);
            return new AlternativeSnapshot
            {
                AlternativeId = alternativeId,
                Slot = slot,
                SessionId = sessionId,
                BaselineHash = baseline.ContentHash(),
                StateHash = state.ContentHash(),
                CreatedUtc = createdUtc,
                State = state,
                Metrics = metrics,
                Constraints = ConstraintEvaluator.Evaluate(state, metrics, baselineMetrics, scenario),
                Features = FeatureExtractor.Extract(metrics, baselineMetrics, scenario),
                ChangedCellIds = state.ChangedCellIds(baseline),
            };
        }

        public AlternativeDto ToDto()
        {
            return new AlternativeDto
            {
                schema_version = Versions.SchemaVersion,
                alternative_id = AlternativeId,
                slot = Slot.ToString(),
                session_id = SessionId,
                scenario_id = State.ScenarioId,
                seed = State.Seed,
                generator_version = State.GeneratorVersion,
                metric_version = State.MetricVersion,
                cost_rules_version = Versions.CostRulesVersion,
                baseline_hash = BaselineHash,
                state_hash = StateHash,
                created_utc = CreatedUtc.ToString("o"),
                feasible = Feasible,
                failed_constraints = Constraints.FailedIds(),
                changed_cell_ids = new List<string>(ChangedCellIds),
                snapshot = CitySerializer.ToDto(State),
                metrics = CitySerializer.ToDto(Metrics),
                constraints = CitySerializer.ToDto(Constraints),
                features = Features,
            };
        }

        /// <summary>
        /// Rebuilds a snapshot from disk. Metrics are recomputed from the stored cells (never trusted
        /// blindly) and the stored baseline hash must match the session's baseline.
        /// </summary>
        public static AlternativeSnapshot FromDto(AlternativeDto dto, CityState baseline, CityMetrics baselineMetrics,
            ScenarioDefinition scenario, out string problem)
        {
            problem = null;
            if (dto == null) { problem = "Alternative file not found."; return null; }
            if (dto.schema_version != Versions.SchemaVersion) { problem = $"Alternative schema {dto.schema_version} is not supported."; return null; }
            if (dto.baseline_hash != baseline.ContentHash()) { problem = "Alternative was made from a different baseline."; return null; }

            CityState state;
            try { state = CitySerializer.FromDto(dto.snapshot); }
            catch (Exception e) { problem = "Alternative snapshot is invalid: " + e.Message; return null; }

            if (!state.SameScenarioAs(baseline)) { problem = "Alternative belongs to another scenario or version."; return null; }

            Enum.TryParse(dto.slot, out AlternativeSlot slot);
            DateTime.TryParse(dto.created_utc, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime created);
            return Create(slot, dto.alternative_id, dto.session_id, created, state, baseline, baselineMetrics, scenario);
        }
    }

    public sealed class ComparisonCheck
    {
        public bool CanCompare;
        public readonly List<string> Problems = new List<string>();
        public bool BothFeasible;
        public string EligibilityNote;
    }

    public static class ComparisonRules
    {
        /// <summary>Blocks comparisons whose alternatives do not share scenario, baseline and versions, or are identical.</summary>
        public static ComparisonCheck Check(AlternativeSnapshot a, AlternativeSnapshot b, CityState baseline)
        {
            var check = new ComparisonCheck();
            if (a == null) check.Problems.Add("Alternative A has not been saved.");
            if (b == null) check.Problems.Add("Alternative B has not been saved.");
            if (a == null || b == null) return check;

            string baselineHash = baseline.ContentHash();
            if (a.State.ScenarioId != b.State.ScenarioId) check.Problems.Add("A and B come from different scenarios.");
            if (a.BaselineHash != b.BaselineHash || a.BaselineHash != baselineHash)
                check.Problems.Add("A and B were not derived from the same original baseline.");
            if (a.State.GeneratorVersion != b.State.GeneratorVersion) check.Problems.Add("A and B use different generator versions.");
            if (a.State.MetricVersion != b.State.MetricVersion) check.Problems.Add("A and B use different metric versions.");
            if (a.State.SchemaVersion != b.State.SchemaVersion) check.Problems.Add("A and B use different schema versions.");
            if (a.AlternativeId == b.AlternativeId) check.Problems.Add("A and B are the same saved alternative.");
            else if (a.StateHash == b.StateHash) check.Problems.Add("A and B are identical designs. Change one of them before comparing.");

            check.CanCompare = check.Problems.Count == 0;
            check.BothFeasible = a.Feasible && b.Feasible;
            if (!check.BothFeasible)
            {
                var failing = new List<string>();
                if (!a.Feasible) failing.Add("A violates: " + string.Join(", ", a.Constraints.FailedIds()));
                if (!b.Feasible) failing.Add("B violates: " + string.Join(", ", b.Constraints.FailedIds()));
                check.EligibilityNote = "Not eligible as a feasible training comparison. " + string.Join("; ", failing);
            }
            else
            {
                check.EligibilityNote = "Both alternatives satisfy every hard constraint.";
            }
            return check;
        }
    }
}
