using System;
using System.Collections.Generic;
using PlannerDNA.Metrics;

namespace PlannerDNA.Storage
{
    // Versioned JSON contracts (schema_version 1). Field names are snake_case so they map
    // one-to-one onto the future Pydantic models. Unity's JsonUtility cannot write null for
    // strings or nested objects: an empty string means "none".

    [Serializable]
    public sealed class CellDto
    {
        public string cell_id;
        public int row;
        public int col;
        public string land_use;
        public int floors;
        public int units_per_floor;
        public int max_floors;
        public bool editable;
        public string zoning;
    }

    [Serializable]
    public sealed class CityStateDto
    {
        public int schema_version;
        public string scenario_id;
        public int seed;
        public string generator_version;
        public string metric_version;
        public int width;
        public int height;
        public int cell_metres;
        public string content_hash;
        public List<CellDto> cells = new List<CellDto>();
    }

    [Serializable]
    public sealed class MetricsDto
    {
        public string metric_version;
        public string cost_rules_version;
        public int housing_units;
        public int residential_cells;
        public int park_cells;
        public int road_cells;
        public int non_road_cells;
        public double park_share_pct;
        public bool access_defined;
        public double access_pct;
        public int accessible_housing_units;
        public int accessible_residential_cells;
        public int unreachable_residential_cells;
        public int spent_credits;
        public bool cost_defined;
        public int changed_cells;
        public int lost_original_housing_units;
        public int net_housing_change;
    }

    [Serializable]
    public sealed class MetricsSummaryDto
    {
        public int housing_units;
        public bool access_defined;
        public double access_pct;
        public double park_share_pct;
        public int spent_credits;
        public bool feasible;
    }

    [Serializable]
    public sealed class ConstraintDto
    {
        public string id;
        public string label;
        public bool passed;
        public string detail;
    }

    [Serializable]
    public sealed class TimelineEntryDto
    {
        public string event_id;
        public string session_id;
        public string scenario_id;
        public int sequence;
        /// <summary>command | undo | redo | checkpoint | decision</summary>
        public string event_type;
        public string action;
        public string cell_id;
        public bool has_cell_change;
        public CellDto before;
        public CellDto after;
        public MetricsSummaryDto metrics_before;
        public MetricsSummaryDto metrics_after;
        public string input_source;
        public string utc_time;
        public string reversal_of_event_id;
        public string original_event_id;
        public string note;
    }

    [Serializable]
    public sealed class AlternativeDto
    {
        public int schema_version;
        public string alternative_id;
        public string slot;
        public string session_id;
        public string scenario_id;
        public int seed;
        public string generator_version;
        public string metric_version;
        public string cost_rules_version;
        public string baseline_hash;
        public string state_hash;
        public string created_utc;
        public bool feasible;
        public List<string> failed_constraints = new List<string>();
        public List<string> changed_cell_ids = new List<string>();
        public CityStateDto snapshot;
        public MetricsDto metrics;
        public List<ConstraintDto> constraints = new List<ConstraintDto>();
        public FeatureVectorV1 features;
    }

    [Serializable]
    public sealed class PreferenceDto
    {
        public int schema_version;
        public string app_version;
        public string comparison_id;
        public string session_id;
        public string scenario_id;
        public string alternative_a_id;
        public string alternative_b_id;
        /// <summary>A | B | tie | skip</summary>
        public string choice;
        public string reason;
        /// <summary>train | evaluation</summary>
        public string mode;
        public string shown_left_alternative_id;
        public string model_version_locked;
        /// <summary>Set only for evaluation-mode choices: the pre-registered blinded trial this choice answers.</summary>
        public string evaluation_trial_id = string.Empty;
        public bool a_feasible;
        public bool b_feasible;
        public bool training_eligible;
        public string eligibility_note;
        public string feature_version;
        public string created_utc;
        /// <summary>Always local_only (kept for schema 1); delivery state lives in the outbox.</summary>
        public string sync_status;
    }

    [Serializable]
    public sealed class SaveGameDto
    {
        public int schema_version;
        public string app_version;
        public string saved_utc;
        public string session_id;
        public string scenario_id;
        public int seed;
        public string generator_version;
        public string metric_version;
        public string baseline_hash;
        public string alternative_a_id;
        public string alternative_b_id;
        public CityStateDto current;
    }

    [Serializable]
    public sealed class PreferenceStats
    {
        public int unique_comparisons;
        public int scenarios;
        public int choice_a;
        public int choice_b;
        public int ties;
        public int skips;
        public int training_eligible;
    }
}
