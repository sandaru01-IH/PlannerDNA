using System;
using PlannerDNA.Core;

namespace PlannerDNA.Metrics
{
    /// <summary>
    /// Fictional intervention cost table (cost-rules-1.0.0), in synthetic credits.
    /// Costs are NOT currency, finance or construction estimates.
    ///
    /// Cost is always derived from the baseline parcel compared with the current parcel, never
    /// from click history. A parcel restored exactly to its baseline costs 0. No transition
    /// produces negative cost. Transitions not listed here are undefined and therefore refused.
    /// </summary>
    public sealed class CostRules
    {
        public int VacantToPark { get; }
        public int NewBuildingBase { get; }
        public int PerNewFloor { get; }
        public int BuiltToPark { get; }
        public int Demolition { get; }
        public int ParkClearance { get; }
        public int UseConversion { get; }
        public int PerAddedFloor { get; }
        public int PerRemovedFloor { get; }

        public CostRules(int vacantToPark, int newBuildingBase, int perNewFloor, int builtToPark, int demolition,
            int parkClearance, int useConversion, int perAddedFloor, int perRemovedFloor)
        {
            VacantToPark = vacantToPark;
            NewBuildingBase = newBuildingBase;
            PerNewFloor = perNewFloor;
            BuiltToPark = builtToPark;
            Demolition = demolition;
            ParkClearance = parkClearance;
            UseConversion = useConversion;
            PerAddedFloor = perAddedFloor;
            PerRemovedFloor = perRemovedFloor;
        }

        public static CostRules Default { get; } = new CostRules(
            vacantToPark: 20,
            newBuildingBase: 25,
            perNewFloor: 5,
            builtToPark: 35,
            demolition: 15,
            parkClearance: 10,
            useConversion: 20,
            perAddedFloor: 5,
            perRemovedFloor: 3);

        public readonly struct CostLine
        {
            public readonly bool Defined;
            public readonly int Credits;
            public readonly string Rule;

            public CostLine(bool defined, int credits, string rule)
            {
                Defined = defined;
                Credits = credits;
                Rule = rule;
            }

            public static CostLine Undefined(string rule) => new CostLine(false, 0, rule);
        }

        private static bool IsEditableUse(LandUse use)
        {
            return use == LandUse.Residential || use == LandUse.Commercial || use == LandUse.Park || use == LandUse.Vacant;
        }

        private static bool IsBuilt(LandUse use) => use == LandUse.Residential || use == LandUse.Commercial;

        /// <summary>Net cost of moving one parcel from its baseline state to its current state.</summary>
        public CostLine Evaluate(CellData baseline, CellData current)
        {
            if (baseline.Row != current.Row || baseline.Col != current.Col)
                throw new ArgumentException("Baseline and current cells must refer to the same parcel.");

            LandUse from = baseline.Use, to = current.Use;

            if (from == to && baseline.Floors == current.Floors)
                return new CostLine(true, 0, "unchanged");

            if (!IsEditableUse(from) || !IsEditableUse(to))
                return CostLine.Undefined($"{from.ToKey()} -> {to.ToKey()} is not an editable transition");

            if (from == to)
            {
                // Same built use; only the floor count differs (parks/vacant have no floors).
                if (!IsBuilt(from)) return CostLine.Undefined($"floor change on {from.ToKey()}");
                int delta = current.Floors - baseline.Floors;
                return delta > 0
                    ? new CostLine(true, delta * PerAddedFloor, $"+{delta} floor(s) x {PerAddedFloor}")
                    : new CostLine(true, -delta * PerRemovedFloor, $"{delta} floor(s) x {PerRemovedFloor}");
            }

            if (from == LandUse.Vacant && to == LandUse.Park)
                return new CostLine(true, VacantToPark, "vacant -> park");

            if ((from == LandUse.Vacant || from == LandUse.Park) && IsBuilt(to))
            {
                int credits = NewBuildingBase + PerNewFloor * current.Floors;
                return new CostLine(true, credits, $"new {to.ToKey()} building: {NewBuildingBase} + {PerNewFloor} x {current.Floors} floor(s)");
            }

            if (IsBuilt(from) && to == LandUse.Park)
                return new CostLine(true, BuiltToPark, $"{from.ToKey()} -> park (demolish and landscape)");

            if (IsBuilt(from) && to == LandUse.Vacant)
                return new CostLine(true, Demolition, $"demolish {from.ToKey()}");

            if (from == LandUse.Park && to == LandUse.Vacant)
                return new CostLine(true, ParkClearance, "park -> vacant (clearance)");

            if (IsBuilt(from) && IsBuilt(to))
            {
                int delta = current.Floors - baseline.Floors;
                int floorPart = delta > 0 ? delta * PerAddedFloor : -delta * PerRemovedFloor;
                return new CostLine(true, UseConversion + floorPart,
                    $"convert {from.ToKey()} -> {to.ToKey()}: {UseConversion}" + (delta != 0 ? $" + floor change {delta:+0;-0}" : ""));
            }

            return CostLine.Undefined($"{from.ToKey()} -> {to.ToKey()} has no cost rule");
        }
    }
}
