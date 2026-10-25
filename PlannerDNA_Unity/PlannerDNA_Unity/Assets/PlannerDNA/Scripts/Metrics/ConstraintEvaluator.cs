using System.Collections.Generic;
using PlannerDNA.Core;

namespace PlannerDNA.Metrics
{
    public sealed class ConstraintResult
    {
        public string Id;
        public string Label;
        public bool Passed;
        public string Detail;
    }

    public sealed class ConstraintReport
    {
        public readonly List<ConstraintResult> Results = new List<ConstraintResult>();

        public bool Feasible
        {
            get
            {
                foreach (ConstraintResult r in Results)
                    if (!r.Passed) return false;
                return true;
            }
        }

        public List<string> FailedIds()
        {
            var ids = new List<string>();
            foreach (ConstraintResult r in Results)
                if (!r.Passed) ids.Add(r.Id);
            return ids;
        }
    }

    /// <summary>
    /// Hard constraints of the Green-Space Dilemma. Manual exploration may temporarily violate
    /// them (shown as warnings); a design is "feasible" only when every constraint passes.
    /// </summary>
    public static class ConstraintEvaluator
    {
        public const string Budget = "budget";
        public const string Housing = "housing_floor";
        public const string MaxFloors = "max_floors";
        public const string CostRulesDefined = "cost_rules_defined";

        public static ConstraintReport Evaluate(CityState current, CityMetrics metrics, CityMetrics baselineMetrics, ScenarioDefinition scenario)
        {
            var report = new ConstraintReport();

            report.Results.Add(new ConstraintResult
            {
                Id = Budget,
                Label = $"Budget <= {scenario.BudgetLimit} credits",
                Passed = metrics.SpentCredits <= scenario.BudgetLimit,
                Detail = $"{metrics.SpentCredits} of {scenario.BudgetLimit} synthetic credits used",
            });

            int minUnits = scenario.MinimumHousingUnits(baselineMetrics.HousingUnits);
            report.Results.Add(new ConstraintResult
            {
                Id = Housing,
                Label = $"Housing >= {scenario.MinHousingRatio * 100:0}% of baseline",
                Passed = metrics.HousingUnits >= minUnits,
                Detail = $"{metrics.HousingUnits} units (minimum {minUnits} of baseline {baselineMetrics.HousingUnits})",
            });

            int worstCell = -1, worstFloors = 0, violations = 0;
            for (int i = 0; i < current.CellCount; i++)
            {
                CellData c = current.Get(i);
                if (!c.Use.IsBuilding()) continue;
                int limit = c.MaxFloors < scenario.MaxFloors ? c.MaxFloors : scenario.MaxFloors;
                if (c.Floors > limit)
                {
                    violations++;
                    if (c.Floors > worstFloors) { worstFloors = c.Floors; worstCell = i; }
                }
            }
            report.Results.Add(new ConstraintResult
            {
                Id = MaxFloors,
                Label = $"Every building <= {scenario.MaxFloors} floors",
                Passed = violations == 0,
                Detail = violations == 0
                    ? "No building exceeds its floor limit"
                    : $"{violations} building(s) too tall; tallest {current.Get(worstCell).Id} at {worstFloors} floors",
            });

            report.Results.Add(new ConstraintResult
            {
                Id = CostRulesDefined,
                Label = "Every change has a defined cost rule",
                Passed = metrics.CostDefined,
                Detail = metrics.CostDefined ? "All changes are priced" : string.Join("; ", metrics.UndefinedTransitions),
            });

            return report;
        }
    }
}
