using System;
using System.Collections.Generic;
using PlannerDNA.Core;
using PlannerDNA.Metrics;
using PlannerDNA.Suggestions;

namespace PlannerDNA.Storage
{
    // Requests and responses of the backend's /api/v1/models endpoints (backend/app/schemas/models.py and
    // app/services/models.py). Field names are identical on both sides; JsonUtility ignores extra fields.

    [Serializable]
    public sealed class PredictPairRequestDto
    {
        public string scenario_id;
        public string alternative_a_id;
        public string alternative_b_id;
        public string comparison_id = string.Empty;
        public string model_version = string.Empty;
    }

    [Serializable]
    public sealed class CandidateDto
    {
        public string candidate_id;
        public string description;
        public string state_hash;
        public bool feasible;
        public List<string> changed_cell_ids = new List<string>();
        public CityStateDto snapshot;
        public FeatureVectorV1 features;
    }

    [Serializable]
    public sealed class RankCandidatesRequestDto
    {
        public int schema_version = Versions.SchemaVersion;
        public string scenario_id;
        public string generator_version;
        public int generator_seed;
        public int top_k = 5;
        public string model_version = string.Empty;
        public List<CandidateDto> candidates = new List<CandidateDto>();

        public static RankCandidatesRequestDto From(CandidateSet set, string scenarioId, int topK)
        {
            var dto = new RankCandidatesRequestDto
            {
                scenario_id = scenarioId,
                generator_version = set.GeneratorVersion,
                generator_seed = set.Seed,
                top_k = topK,
            };
            foreach (CandidateDesign c in set.Candidates)
            {
                dto.candidates.Add(new CandidateDto
                {
                    candidate_id = c.CandidateId,
                    description = c.Description,
                    state_hash = c.StateHash,
                    feasible = c.Evaluation.Feasible,
                    changed_cell_ids = new List<string>(c.ChangedCellIds),
                    snapshot = CitySerializer.ToDto(c.State),
                    features = c.Features,
                });
            }
            return dto;
        }
    }

    [Serializable]
    public sealed class ApiErrorBodyDto
    {
        public string code;
        public string message;
    }

    [Serializable]
    public sealed class ApiErrorEnvelopeDto
    {
        public ApiErrorBodyDto error;
    }

    [Serializable]
    public sealed class ReadinessReasonDto
    {
        public string code;
        public string message;
    }

    [Serializable]
    public sealed class ExcludedCountsDto
    {
        public int all_comparisons;
        public int ties_and_skips;
        public int evaluation_mode;
        public int infeasible;
    }

    [Serializable]
    public sealed class ThresholdsDto
    {
        public int min_comparisons;
        public int min_scenarios;
        public int min_rank;
        public int suggested_comparisons;
        public int suggested_scenarios;
    }

    [Serializable]
    public sealed class ReadinessDto
    {
        public bool can_train;
        public string code;
        public string message;
        public List<ReadinessReasonDto> reasons = new List<ReadinessReasonDto>();
        public List<string> warnings = new List<string>();
        public int eligible_comparisons;
        public int informative_comparisons;
        public int scenarios;
        public int rank;
        public ThresholdsDto thresholds;
        public ExcludedCountsDto excluded;
        public string active_model_version;
        /// <summary>True while a blinded evaluation is open: training is paused so the evaluated model cannot change.</summary>
        public bool evaluation_open;
    }

    [Serializable]
    public sealed class CoefficientDto
    {
        public string feature;
        public string label;
        public double weight_standardised;
        public double weight_raw;
        public double scale;
        public double interval_low;
        public double interval_high;
        public bool interval_includes_zero;
        public double interval_level;
        public double sign_agreement;
        public double relative_importance;
        public bool varied;
        public string direction;
        public string statement;
    }

    [Serializable]
    public sealed class ScoreDto
    {
        public int n;
        public double accuracy;
        public double brier;
        public double log_loss;
        public bool has_log_loss;
    }

    [Serializable]
    public sealed class BaselineScoreDto
    {
        public string name;
        public string label;
        public int n;
        public double accuracy;
        public double brier;
        public double log_loss;
        public bool has_log_loss;
    }

    [Serializable]
    public sealed class DiagnosticsDto
    {
        public bool available;
        public string note;
        public int folds;
        public int skipped_folds;
        public int scenarios;
        public ScoreDto model;
        public List<BaselineScoreDto> baselines = new List<BaselineScoreDto>();
    }

    [Serializable]
    public sealed class ModelSummaryDto
    {
        public string model_version;
        public string status;
        public string model_kind;
        public string ml_version;
        public string feature_version;
        public string trained_utc;
        public int unique_comparisons;
        public int scenario_count;
        public List<string> training_scenario_ids = new List<string>();
        public int choices_for_a;
        public int choices_for_b;
        public int augmented_rows;
        public int ignored_uninformative;
        public string summary;
        public List<CoefficientDto> coefficients = new List<CoefficientDto>();
        public ScoreDto training_fit;
        public DiagnosticsDto diagnostics;
        public List<string> warnings = new List<string>();
        public List<string> caveats = new List<string>();
        public int random_seed;
        public string artifact_sha256;
    }

    [Serializable]
    public sealed class CurrentModelDto
    {
        public bool has_model;
        public string message;
        public ReadinessDto readiness;
        public ModelSummaryDto model;
    }

    [Serializable]
    public sealed class TrainResponseDto
    {
        public string status;
        public string detail;
        public ModelSummaryDto model;
    }

    [Serializable]
    public sealed class ModelHistoryItemDto
    {
        public string model_version;
        public string status;
        public string trained_utc;
        public int unique_comparisons;
        public int scenario_count;
        public double diagnostic_accuracy;
        public int diagnostic_n;
        public bool diagnostic_available;
        public string ml_version;
        public string feature_version;
        public int warning_count;
    }

    [Serializable]
    public sealed class ModelHistoryDto
    {
        public int count;
        public List<ModelHistoryItemDto> models = new List<ModelHistoryItemDto>();
    }

    [Serializable]
    public sealed class ContributionDto
    {
        public string feature;
        public string label;
        public double a_value;
        public double b_value;
        public double difference;
        public double value;
        public double weight_raw;
        public double contribution;
    }

    [Serializable]
    public sealed class PairPredictionDto
    {
        public string prediction_id;
        public string model_version;
        public string trained_utc;
        public string scenario_id;
        public string alternative_a_id;
        public string alternative_b_id;
        public double p_a;
        public double p_b;
        public double utility_a;
        public double utility_b;
        public string favoured;
        public List<ContributionDto> contributions = new List<ContributionDto>();
        public string comparison_id;
        public bool comparison_in_training_set;
        public bool scenario_in_training_set;
        public string explanation;
        public string caveat;
    }

    [Serializable]
    public sealed class SuggestionDto
    {
        public int rank;
        public string candidate_id;
        public string description;
        public double utility;
        public string state_hash;
        public List<string> changed_cell_ids = new List<string>();
        public double access_pct;
        public bool access_defined;
        public int housing_units;
        public double park_share_pct;
        public int spent_credits;
        public int lost_original_housing_units;
        public List<ContributionDto> contributions = new List<ContributionDto>();
        public int near_duplicates_skipped;
        public bool has_next;
        public double p_over_next;
    }

    [Serializable]
    public sealed class RankResponseDto
    {
        public string model_version;
        public string trained_utc;
        public string scenario_id;
        public bool scenario_in_training_set;
        public string generator_version;
        public int generator_seed;
        public int received;
        public int ranked;
        public int skipped_near_duplicates;
        public int returned;
        public List<SuggestionDto> suggestions = new List<SuggestionDto>();
        public string banner;
        public string caveat;
    }
}
