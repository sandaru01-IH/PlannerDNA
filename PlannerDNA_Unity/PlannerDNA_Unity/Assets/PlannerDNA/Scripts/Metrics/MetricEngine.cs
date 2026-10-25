using System;
using PlannerDNA.Core;

namespace PlannerDNA.Metrics
{
    /// <summary>
    /// Pure metric calculation (metrics-1.0.0). Same inputs always give the same outputs;
    /// there is no randomness, time dependence or Unity scene dependence.
    /// </summary>
    public static class MetricEngine
    {
        public static CityMetrics Evaluate(CityState current, CityState baseline, ScenarioDefinition scenario, CostRules costRules = null)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            if (baseline == null) throw new ArgumentNullException(nameof(baseline));
            if (scenario == null) throw new ArgumentNullException(nameof(scenario));
            if (!current.SameScenarioAs(baseline))
                throw new ArgumentException("Current state and baseline belong to different scenarios or versions.");
            costRules = costRules ?? CostRules.Default;

            var m = new CityMetrics();
            m.ParkDistanceMetres = AccessibilityCalculator.ParkDistancesMetres(current);

            for (int i = 0; i < current.CellCount; i++)
            {
                CellData c = current.Get(i);
                switch (c.Use)
                {
                    case LandUse.Road:
                        m.RoadCells++;
                        break;
                    case LandUse.Park:
                        m.ParkCells++;
                        break;
                    case LandUse.Residential:
                        m.ResidentialCells++;
                        int units = c.HousingUnits;
                        m.HousingUnits += units;
                        int d = m.ParkDistanceMetres[i];
                        if (d != AccessibilityCalculator.Unreachable && d <= scenario.AccessThresholdMetres)
                        {
                            m.AccessibleHousingUnits += units;
                            m.AccessibleResidentialCells++;
                        }
                        else if (d == AccessibilityCalculator.Unreachable)
                        {
                            m.UnreachableResidentialCells++;
                        }
                        break;
                }
            }

            m.NonRoadCells = current.CellCount - m.RoadCells;
            m.ParkSharePct = m.NonRoadCells > 0 ? 100.0 * m.ParkCells / m.NonRoadCells : 0.0;
            m.AccessDefined = m.HousingUnits > 0;
            m.AccessPct = m.AccessDefined ? 100.0 * m.AccessibleHousingUnits / m.HousingUnits : 0.0;

            int baselineHousing = 0;
            m.CostDefined = true;
            for (int i = 0; i < current.CellCount; i++)
            {
                CellData b = baseline.Get(i);
                CellData c = current.Get(i);
                baselineHousing += b.HousingUnits;
                if (b == c) continue;

                m.ChangedCells++;
                CostRules.CostLine line = costRules.Evaluate(b, c);
                if (line.Defined) m.SpentCredits += line.Credits;
                else
                {
                    m.CostDefined = false;
                    m.UndefinedTransitions.Add($"{c.Id}: {line.Rule}");
                }

                if (b.Use == LandUse.Residential)
                    m.LostOriginalHousingUnits += Math.Max(0, b.HousingUnits - c.HousingUnits);
            }
            m.NetHousingChange = m.HousingUnits - baselineHousing;
            return m;
        }

        /// <summary>Metrics of the baseline compared with itself (zero cost, zero change).</summary>
        public static CityMetrics EvaluateBaseline(CityState baseline, ScenarioDefinition scenario)
        {
            return Evaluate(baseline, baseline, scenario);
        }
    }
}
