using System;
using System.Collections.Generic;
using PlannerDNA.Core;

namespace PlannerDNA.Storage
{
    // Requests and responses of the backend's /api/v1/evaluation endpoints (backend/app/schemas/evaluation.py and
    // app/services/evaluation.py, protocol blinded-eval-1.0.0). Fields the backend may send as JSON null
    // (an undefined accuracy, an unrevealed probability) are deliberately not declared here.

    [Serializable]
    public sealed class EvaluationScenarioDto
    {
        public string scenario_id;
        public int seed;
    }

    [Serializable]
    public sealed class EvaluationPlanRequestDto
    {
        public int schema_version = Versions.SchemaVersion;
        public string plan_id;
        public string model_version;
        public List<EvaluationScenarioDto> evaluation_scenarios = new List<EvaluationScenarioDto>();
        public int pairs_per_scenario;
        public int repeat_count;
        public string pair_rule;
        public string note = string.Empty;
        public string app_version = Versions.AppVersion;
        public string created_utc;
    }

    [Serializable]
    public sealed class EvaluationTrialRequestDto
    {
        public int schema_version = Versions.SchemaVersion;
        public string trial_id;
        public string plan_id;
        public int trial_index;
        public string scenario_id;
        public string alternative_a_id;
        public string alternative_b_id;
        public string repeat_of_trial_id = string.Empty;
        public int candidate_seed;
        public string pair_rule;
    }

    [Serializable]
    public sealed class ClosePlanRequestDto
    {
        public string reason = string.Empty;
    }

    [Serializable]
    public sealed class EvaluationProgressDto
    {
        public int planned_primary;
        public int registered_primary;
        public int answered_primary;
        public int planned_repeats;
        public int registered_repeats;
        public int answered_repeats;
        public int awaiting_choice;

        public int Planned => planned_primary + planned_repeats;
        public int Answered => answered_primary + answered_repeats;
    }

    /// <summary>What the protocol expects next: answer | new_pair | repeat | complete | closed.</summary>
    [Serializable]
    public sealed class EvaluationNextDto
    {
        public string action;
        public string kind;
        public string trial_id;
        public int trial_index;
        public string scenario_id;
        public int seed;
        public int candidate_seed;
        public string alternative_a_id;
        public string alternative_b_id;
        public string shown_left_alternative_id;
        public string repeat_of_trial_id;
        public string message;
    }

    /// <summary>A trial as the backend shows it before the reveal: the seal and the screen order, never the probability.</summary>
    [Serializable]
    public sealed class EvaluationTrialDto
    {
        public string trial_id;
        public string plan_id;
        public int trial_index;
        public string kind;
        public string scenario_id;
        public string alternative_a_id;
        public string alternative_b_id;
        public string shown_left_alternative_id;
        public string shown_right_alternative_id;
        public string repeat_of_trial_id;
        public int candidate_seed;
        public string model_version_locked;
        public string sealed_sha256;
        public string status;
        public string comparison_id;
        public string registered_utc;
        public bool revealed;
    }

    [Serializable]
    public sealed class EvaluationPlanDto
    {
        public string protocol_version;
        public string plan_id;
        public string model_version;
        public string model_artifact_sha256;
        public List<string> training_scenario_ids = new List<string>();
        public int training_comparison_count;
        public List<EvaluationScenarioDto> evaluation_scenarios = new List<EvaluationScenarioDto>();
        public int pairs_per_scenario;
        public int repeat_count;
        public int plan_seed;
        public int candidate_seed_base;
        public string pair_rule;
        public string created_utc;
        public string registered_utc;
        public string status;
        public string registration_sha256;
        public string note;
        public string closed_utc;
        public string close_reason;
        public EvaluationProgressDto progress;
        public List<EvaluationTrialDto> trials = new List<EvaluationTrialDto>();
        public EvaluationNextDto next;
        public bool report_available;
        public string report_summary;
        public string report_sha256;
    }

    [Serializable]
    public sealed class EvaluationPlanResponseDto
    {
        public string status;
        public EvaluationPlanDto plan;
    }

    [Serializable]
    public sealed class EvaluationTrialResponseDto
    {
        public string status;
        public EvaluationTrialDto trial;
    }

    [Serializable]
    public sealed class EvaluationPlanSummaryDto
    {
        public string plan_id;
        public string status;
        public string model_version;
        public string registered_utc;
        public string closed_utc;
        public int scored;
        public double accuracy;
        public bool has_report;
        public string summary;
    }

    [Serializable]
    public sealed class EvaluationOverviewDto
    {
        public string protocol_version;
        /// <summary>Empty when no plan is open; check this rather than open_plan (JsonUtility never leaves objects null).</summary>
        public string open_plan_id;
        public EvaluationPlanDto open_plan;
        public int count;
        public List<EvaluationPlanSummaryDto> plans = new List<EvaluationPlanSummaryDto>();

        public bool HasOpenPlan => !string.IsNullOrEmpty(open_plan_id) && open_plan != null && open_plan.plan_id == open_plan_id;
    }

    [Serializable]
    public sealed class EvaluationRevealDto
    {
        public string trial_id;
        public string plan_id;
        public int trial_index;
        public string kind;
        public string model_version;
        public double p_a;
        public double p_b;
        public double p_left;
        public double p_right;
        public string shown_left_alternative_id;
        /// <summary>A | B | neither</summary>
        public string favoured;
        /// <summary>left | right | neither</summary>
        public string favoured_side;
        public string choice;
        /// <summary>left | right, or empty for tie/skip</summary>
        public string chosen_side;
        public string comparison_id;
        public string sealed_payload;
        public string sealed_sha256;
        public string registered_utc;
        public string answered_utc;
        public string revealed_utc;
        public List<ContributionDto> contributions = new List<ContributionDto>();
        public string explanation;
        public string caveat;

        public bool Scored => (choice == "A" || choice == "B") && favoured != "neither";
        public bool Matched => Scored && favoured == choice;
    }

    [Serializable]
    public sealed class EvaluationCountsDto
    {
        public int planned_scenarios;
        public int planned_primary_pairs;
        public int planned_repeats;
        public int registered_trials;
        public int registered_primary;
        public int registered_repeats;
        public int answered;
        public int unanswered;
        public int revealed;
        public int scored;
        public int primary_ties;
        public int primary_skips;
        public int choices_for_a;
        public int choices_for_b;
        public int scenarios_scored;
    }

    [Serializable]
    public sealed class EvaluationModelScoreDto
    {
        public string model_version;
        public int n;
        public double accuracy;
        public double brier;
        public double log_loss;
        public double correct;
        public int undecided;
        public List<double> accuracy_ci = new List<double>();
    }

    [Serializable]
    public sealed class EvaluationBaselineScoreDto
    {
        public string name;
        public string label;
        public int n;
        public double accuracy;
        public double brier;
        public double log_loss;
        public bool has_log_loss;
        public double correct;
        public List<double> accuracy_ci = new List<double>();
        public double model_minus_baseline_accuracy;
        public List<double> model_minus_baseline_accuracy_ci = new List<double>();
    }

    [Serializable]
    public sealed class EvaluationMetricsDto
    {
        public EvaluationModelScoreDto model;
        public List<EvaluationBaselineScoreDto> baselines = new List<EvaluationBaselineScoreDto>();
    }

    [Serializable]
    public sealed class EvaluationBootstrapDto
    {
        public bool available;
        public int resamples;
        public int seed;
        public double level;
        public string clustered_by;
        public string note;
    }

    [Serializable]
    public sealed class EvaluationPositionDto
    {
        public int n;
        public int chose_left;
        public int chose_right;
        public double left_share;
        public double binomial_p_value;
    }

    [Serializable]
    public sealed class EvaluationConsistencyDto
    {
        public int registered;
        public int answered_both;
        public int scored_pairs;
        public int agreements;
        public int disagreements;
        public List<double> rate_ci = new List<double>();
        public int chose_same_screen_side;
        public int involving_tie_or_skip;
    }

    [Serializable]
    public sealed class EvaluationHeldOutDto
    {
        public int evaluation_choices_in_training;
        public List<string> evaluation_scenarios_in_training = new List<string>();
        public string note;
    }

    [Serializable]
    public sealed class EvaluationReportModelDto
    {
        public string model_version;
        public string trained_utc;
        public string artifact_sha256;
        public string ml_version;
        public string feature_version;
        public int random_seed;
        public string kind;
        public List<string> features = new List<string>();
    }

    [Serializable]
    public sealed class EvaluationScenarioRowDto
    {
        public string scenario_id;
        public int seed;
        public int registered;
        public int scored;
        public double correct;
        public int ties;
        public int skips;
        public int unanswered;
    }

    [Serializable]
    public sealed class EvaluationReportDto
    {
        public string format;
        public int format_version;
        public string protocol_version;
        public EvaluationPlanDto plan;
        public EvaluationReportModelDto model;
        public EvaluationHeldOutDto held_out_guarantee;
        public EvaluationCountsDto counts;
        public EvaluationMetricsDto metrics;
        public EvaluationBootstrapDto bootstrap;
        public EvaluationPositionDto position;
        public EvaluationConsistencyDto repeat_consistency;
        public List<EvaluationScenarioRowDto> per_scenario = new List<EvaluationScenarioRowDto>();
        public List<string> warnings = new List<string>();
        public List<string> caveats = new List<string>();
        public string summary;
    }

    [Serializable]
    public sealed class EvaluationVerificationDto
    {
        public string stored_sha256;
        public string recomputed_sha256;
        public bool stored_report_unchanged;
        public bool reproducible;
        public string note;
    }

    [Serializable]
    public sealed class EvaluationReportResponseDto
    {
        public string plan_id;
        public string report_sha256;
        public EvaluationVerificationDto verification;
        public EvaluationReportDto report;
    }

    [Serializable]
    public sealed class ClosePlanResponseDto
    {
        public string status;
        public string report_sha256;
        public EvaluationReportDto report;
    }
}
