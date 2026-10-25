using PlannerDNA.Core;
using PlannerDNA.Metrics;

namespace PlannerDNA.Commands
{
    public readonly struct ValidationResult
    {
        public readonly bool Ok;
        public readonly string Reason;
        public readonly CellData Proposed;

        private ValidationResult(bool ok, string reason, CellData proposed)
        {
            Ok = ok;
            Reason = reason;
            Proposed = proposed;
        }

        public static ValidationResult Accept(CellData proposed) => new ValidationResult(true, null, proposed);
        public static ValidationResult Reject(string reason) => new ValidationResult(false, reason, default);
    }

    /// <summary>
    /// Decides whether a single-parcel edit is permitted and what the parcel becomes.
    /// Pure function of (current city, baseline, scenario); never mutates anything.
    /// </summary>
    public static class ActionValidator
    {
        public static ValidationResult Validate(CityState current, CityState baseline, ScenarioDefinition scenario,
            EditAction action, int row, int col, CostRules costRules = null)
        {
            costRules = costRules ?? CostRules.Default;
            if (!current.InBounds(row, col)) return ValidationResult.Reject("No parcel is selected.");

            CellData cell = current.Get(row, col);
            if (cell.Use == LandUse.Road) return ValidationResult.Reject($"{cell.Id} is a road. Roads are fixed in this scenario.");
            if (cell.Use == LandUse.Civic || cell.Zoning == Zoning.Protected || !cell.Editable)
                return ValidationResult.Reject($"{cell.Id} is a protected {cell.Use.DisplayName().ToLowerInvariant()} and cannot be edited.");

            int maxFloors = cell.MaxFloors < scenario.MaxFloors ? cell.MaxFloors : scenario.MaxFloors;
            CellData proposed;

            switch (action)
            {
                case EditAction.SetPark:
                    if (cell.Use == LandUse.Park) return ValidationResult.Reject($"{cell.Id} is already a park.");
                    proposed = cell.With(LandUse.Park, 0, 0);
                    break;

                case EditAction.SetResidential:
                    if (cell.Use == LandUse.Residential) return ValidationResult.Reject($"{cell.Id} is already residential.");
                    {
                        // Converting a building keeps its floors; a new building starts at the scenario default.
                        int floors = cell.Use == LandUse.Commercial ? cell.Floors : scenario.NewBuildingFloors;
                        proposed = cell.With(LandUse.Residential, floors, scenario.UnitsPerFloor);
                    }
                    break;

                case EditAction.SetCommercial:
                    if (cell.Use == LandUse.Commercial) return ValidationResult.Reject($"{cell.Id} is already commercial.");
                    if (cell.Zoning == Zoning.ResidentialOnly)
                        return ValidationResult.Reject($"{cell.Id} is zoned residential-only; commercial use is not permitted.");
                    {
                        int floors = cell.Use == LandUse.Residential ? cell.Floors : scenario.NewBuildingFloors;
                        proposed = cell.With(LandUse.Commercial, floors, 0);
                    }
                    break;

                case EditAction.Demolish:
                    if (cell.Use != LandUse.Residential && cell.Use != LandUse.Commercial)
                        return ValidationResult.Reject($"{cell.Id} is a {cell.Use.DisplayName().ToLowerInvariant()}; only residential or commercial buildings can be demolished.");
                    proposed = cell.With(LandUse.Vacant, 0, 0);
                    break;

                case EditAction.RaiseFloors:
                    if (cell.Use != LandUse.Residential && cell.Use != LandUse.Commercial)
                        return ValidationResult.Reject($"Cannot raise a {cell.Use.DisplayName().ToLowerInvariant()}; only buildings have floors.");
                    if (cell.Floors >= maxFloors)
                        return ValidationResult.Reject($"{cell.Id} is already at the maximum of {maxFloors} floors.");
                    proposed = cell.WithFloors(cell.Floors + 1);
                    break;

                case EditAction.LowerFloors:
                    if (cell.Use != LandUse.Residential && cell.Use != LandUse.Commercial)
                        return ValidationResult.Reject($"Cannot lower a {cell.Use.DisplayName().ToLowerInvariant()}; only buildings have floors.");
                    if (cell.Floors <= 1)
                        return ValidationResult.Reject($"{cell.Id} has 1 floor. Use Demolish to remove the building.");
                    proposed = cell.WithFloors(cell.Floors - 1);
                    break;

                default:
                    return ValidationResult.Reject($"{action.DisplayName()} is not a single-parcel edit.");
            }

            CostRules.CostLine line = costRules.Evaluate(baseline.Get(row, col), proposed);
            if (!line.Defined) return ValidationResult.Reject($"No cost rule is defined for this change ({line.Rule}).");

            return ValidationResult.Accept(proposed);
        }
    }
}
