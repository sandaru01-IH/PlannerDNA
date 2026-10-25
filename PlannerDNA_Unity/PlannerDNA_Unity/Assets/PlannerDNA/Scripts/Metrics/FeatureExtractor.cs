using System;
using PlannerDNA.Core;

namespace PlannerDNA.Metrics
{
    /// <summary>
    /// Feature schema v1: a fixed, versioned numeric description of an alternative's outcomes
    /// relative to its baseline. Stored with every saved alternative so later phases can use it.
    /// The signs only define input direction; they are not preference weights.
    /// </summary>
    [Serializable]
    public sealed class FeatureVectorV1
    {
        public string feature_version = Versions.FeatureVersion;
        public double delta_access_fraction;
        public double delta_housing_fraction;
        public double delta_park_share_fraction;
        public double negative_budget_fraction;
        public double negative_lost_housing_fraction;
    }

    public static class FeatureExtractor
    {
        public static FeatureVectorV1 Extract(CityMetrics current, CityMetrics baseline, ScenarioDefinition scenario)
        {
            double baselineAccess = baseline.AccessDefined ? baseline.AccessPct : 0.0;
            double currentAccess = current.AccessDefined ? current.AccessPct : 0.0;
            double housingDenominator = Math.Max(1, baseline.HousingUnits);
            return new FeatureVectorV1
            {
                delta_access_fraction = (currentAccess - baselineAccess) / 100.0,
                delta_housing_fraction = (current.HousingUnits - baseline.HousingUnits) / housingDenominator,
                delta_park_share_fraction = (current.ParkSharePct - baseline.ParkSharePct) / 100.0,
                negative_budget_fraction = -(current.SpentCredits / (double)Math.Max(1, scenario.BudgetLimit)),
                negative_lost_housing_fraction = -(current.LostOriginalHousingUnits / housingDenominator),
            };
        }
    }
}
