using System;
using System.Collections.Generic;
using PlannerDNA.Core;
using PlannerDNA.Metrics;

namespace PlannerDNA.Storage
{
    [Serializable]
    public sealed class ScenarioConstraintsDto
    {
        public int budget_limit;
        public double min_housing_ratio;
        public int max_floors;
        public int access_threshold_metres;
        public int units_per_floor;
        public int new_building_floors;
    }

    /// <summary>Everything the backend needs to verify later records of this scenario independently.</summary>
    [Serializable]
    public sealed class ScenarioRegistrationDto
    {
        public int schema_version;
        public string scenario_id;
        public int seed;
        public string generator_version;
        public string metric_version;
        public string cost_rules_version;
        public string mission_id;
        public string baseline_hash;
        public string created_utc;
        public ScenarioConstraintsDto constraints;
        public CityStateDto baseline;
        public MetricsDto baseline_metrics;
    }

    [Serializable]
    public sealed class SessionStartDto
    {
        public int schema_version;
        public string session_id;
        public string scenario_id;
        public string baseline_hash;
        public string started_utc;
        public string app_version;
    }

    /// <summary>One queued record awaiting delivery to the local backend.</summary>
    [Serializable]
    public sealed class OutboundMessageDto
    {
        public string message_id;
        public long sequence;
        /// <summary>scenario | session | event | alternative | preference</summary>
        public string kind;
        public string record_id;
        public string endpoint;
        public string payload_json;
        public string created_utc;
        public int attempts;
        public string last_error;
        public string last_attempt_utc;
        /// <summary>pending | delivered | rejected</summary>
        public string status;
        public long http_status;
        public string delivered_utc;
    }

    /// <summary>A self-contained copy of everything stored locally, written by the Data screen.</summary>
    [Serializable]
    public sealed class LocalExportDto
    {
        public string format = "plannerdna-local-export";
        public int format_version = 1;
        public int schema_version;
        public string app_version;
        public string generator_version;
        public string metric_version;
        public string cost_rules_version;
        public string feature_version;
        public string exported_utc;
        public string note;
        public List<AlternativeDto> alternatives = new List<AlternativeDto>();
        public List<PreferenceDto> preferences = new List<PreferenceDto>();
        public List<TimelineEntryDto> events = new List<TimelineEntryDto>();
        public List<SaveGameDto> saves = new List<SaveGameDto>();
    }

    public static class SyncPayloads
    {
        public static ScenarioRegistrationDto Scenario(PlanningSession session, DateTime createdUtc)
        {
            ScenarioDefinition d = session.Scenario;
            CityState b = session.Baseline;
            return new ScenarioRegistrationDto
            {
                schema_version = Versions.SchemaVersion,
                scenario_id = b.ScenarioId,
                seed = b.Seed,
                generator_version = b.GeneratorVersion,
                metric_version = b.MetricVersion,
                cost_rules_version = Versions.CostRulesVersion,
                mission_id = d.MissionId,
                baseline_hash = session.BaselineHash,
                created_utc = createdUtc.ToString("o"),
                constraints = new ScenarioConstraintsDto
                {
                    budget_limit = d.BudgetLimit,
                    min_housing_ratio = d.MinHousingRatio,
                    max_floors = d.MaxFloors,
                    access_threshold_metres = d.AccessThresholdMetres,
                    units_per_floor = d.UnitsPerFloor,
                    new_building_floors = d.NewBuildingFloors,
                },
                baseline = CitySerializer.ToDto(b),
                baseline_metrics = CitySerializer.ToDto(session.BaselineMetrics),
            };
        }

        public static SessionStartDto Session(PlanningSession session, DateTime startedUtc)
        {
            return new SessionStartDto
            {
                schema_version = Versions.SchemaVersion,
                session_id = session.SessionId,
                scenario_id = session.Baseline.ScenarioId,
                baseline_hash = session.BaselineHash,
                started_utc = startedUtc.ToString("o"),
                app_version = Versions.AppVersion,
            };
        }
    }
}
