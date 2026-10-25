using System.Collections.Generic;

namespace PlannerDNA.Metrics
{
    /// <summary>
    /// Deterministic outcome indicators for one city state, relative to its baseline.
    /// All values are synthetic proxies (see MetricDefinitions).
    /// </summary>
    public sealed class CityMetrics
    {
        // State-only indicators
        public int HousingUnits;
        public int ResidentialCells;
        public int ParkCells;
        public int RoadCells;
        public int NonRoadCells;
        public double ParkSharePct;

        /// <summary>False when total housing is 0; AccessPct is then not applicable (never shown as 100%).</summary>
        public bool AccessDefined;
        public double AccessPct;
        public int AccessibleHousingUnits;
        public int AccessibleResidentialCells;
        /// <summary>Residential cells without any road path to a park (or without road access at all).</summary>
        public int UnreachableResidentialCells;

        // Baseline-relative indicators
        public int SpentCredits;
        public bool CostDefined;
        public int ChangedCells;
        public int LostOriginalHousingUnits;
        public int NetHousingChange;
        public readonly List<string> UndefinedTransitions = new List<string>();

        /// <summary>Per-cell park distance in metres (AccessibilityCalculator.Unreachable when none).</summary>
        public int[] ParkDistanceMetres;

        public string AccessDisplay => AccessDefined ? $"{AccessPct:0.0}%" : "N/A";
    }
}
