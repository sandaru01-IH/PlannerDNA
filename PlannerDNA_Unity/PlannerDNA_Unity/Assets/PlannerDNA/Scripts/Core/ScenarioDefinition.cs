namespace PlannerDNA.Core
{
    /// <summary>
    /// Mission text and hard-constraint parameters. All quantities are synthetic.
    /// </summary>
    public sealed class ScenarioDefinition
    {
        public string MissionId { get; }
        public string Title { get; }
        public string MissionText { get; }
        /// <summary>Synthetic credits (not currency).</summary>
        public int BudgetLimit { get; }
        /// <summary>Current housing capacity must stay at or above this fraction of baseline.</summary>
        public double MinHousingRatio { get; }
        public int MaxFloors { get; }
        public int AccessThresholdMetres { get; }
        public int UnitsPerFloor { get; }
        /// <summary>Floors given to a building created on a vacant or park parcel.</summary>
        public int NewBuildingFloors { get; }

        public ScenarioDefinition(string missionId, string title, string missionText, int budgetLimit,
            double minHousingRatio, int maxFloors, int accessThresholdMetres, int unitsPerFloor, int newBuildingFloors)
        {
            MissionId = missionId;
            Title = title;
            MissionText = missionText;
            BudgetLimit = budgetLimit;
            MinHousingRatio = minHousingRatio;
            MaxFloors = maxFloors;
            AccessThresholdMetres = accessThresholdMetres;
            UnitsPerFloor = unitsPerFloor;
            NewBuildingFloors = newBuildingFloors;
        }

        public static ScenarioDefinition GreenSpaceDilemma(int budgetLimit = 100)
        {
            const double minHousingRatio = 0.9;
            const int maxFloors = 8;
            const int accessThreshold = 400;
            string missionText =
                $"Improve the share of housing capacity within a {accessThreshold} m road-network walk of a park, " +
                $"while keeping housing capacity at or above {minHousingRatio * 100:0}% of the baseline, " +
                $"spending no more than {budgetLimit} synthetic credits and never exceeding {maxFloors} floors.";
            return new ScenarioDefinition(
                missionId: "green-space-dilemma-v1",
                title: "The Neighbourhood Green-Space Dilemma",
                missionText: missionText,
                budgetLimit: budgetLimit,
                minHousingRatio: minHousingRatio,
                maxFloors: maxFloors,
                accessThresholdMetres: accessThreshold,
                unitsPerFloor: 4,
                newBuildingFloors: 2);
        }

        /// <summary>Smallest integer housing capacity that satisfies the ratio.</summary>
        public int MinimumHousingUnits(int baselineHousingUnits)
        {
            // Integer arithmetic on ratio*1000 avoids floating-point edge errors at the boundary.
            long ratioPerMille = (long)System.Math.Round(MinHousingRatio * 1000.0);
            long numerator = ratioPerMille * baselineHousingUnits;
            return (int)((numerator + 999) / 1000);
        }
    }
}
