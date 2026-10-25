namespace PlannerDNA.Core
{
    /// <summary>
    /// Every saved record carries these identifiers so results can be reproduced.
    /// Bump the matching string whenever the algorithm or rule set changes.
    /// </summary>
    public static class Versions
    {
        public const int SchemaVersion = 1;
        public const string GeneratorVersion = "green-gen-1.0.0";
        public const string MetricVersion = "metrics-1.0.0";
        public const string CostRulesVersion = "cost-rules-1.0.0";
        public const string FeatureVersion = "features-v1";
        public const string CandidateGeneratorVersion = "candidates-1.0.0";
        public const string AppVersion = "0.4.0-phase4";
    }
}
