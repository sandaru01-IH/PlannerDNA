using System;

namespace PlannerDNA.Core
{
    public enum LandUse
    {
        Road = 0,
        Residential = 1,
        Commercial = 2,
        Park = 3,
        Vacant = 4,
        Civic = 5,
    }

    /// <summary>Per-parcel planning restriction.</summary>
    public enum Zoning
    {
        /// <summary>Residential, commercial or park permitted.</summary>
        Mixed = 0,
        /// <summary>Residential or park permitted; commercial refused.</summary>
        ResidentialOnly = 1,
        /// <summary>Fixed in Phase 1 (roads, civic facilities).</summary>
        Protected = 2,
    }

    public static class LandUseNames
    {
        public static string ToKey(this LandUse use)
        {
            switch (use)
            {
                case LandUse.Road: return "road";
                case LandUse.Residential: return "residential";
                case LandUse.Commercial: return "commercial";
                case LandUse.Park: return "park";
                case LandUse.Vacant: return "vacant";
                case LandUse.Civic: return "civic";
                default: throw new ArgumentOutOfRangeException(nameof(use), use, null);
            }
        }

        public static LandUse ParseLandUse(string key)
        {
            switch (key)
            {
                case "road": return LandUse.Road;
                case "residential": return LandUse.Residential;
                case "commercial": return LandUse.Commercial;
                case "park": return LandUse.Park;
                case "vacant": return LandUse.Vacant;
                case "civic": return LandUse.Civic;
                default: throw new FormatException($"Unknown land use '{key}'.");
            }
        }

        public static string ToKey(this Zoning zoning)
        {
            switch (zoning)
            {
                case Zoning.Mixed: return "mixed";
                case Zoning.ResidentialOnly: return "residential_only";
                case Zoning.Protected: return "protected";
                default: throw new ArgumentOutOfRangeException(nameof(zoning), zoning, null);
            }
        }

        public static Zoning ParseZoning(string key)
        {
            switch (key)
            {
                case "mixed": return Zoning.Mixed;
                case "residential_only": return Zoning.ResidentialOnly;
                case "protected": return Zoning.Protected;
                default: throw new FormatException($"Unknown zoning '{key}'.");
            }
        }

        public static string DisplayName(this LandUse use)
        {
            switch (use)
            {
                case LandUse.Road: return "Road";
                case LandUse.Residential: return "Residential";
                case LandUse.Commercial: return "Commercial";
                case LandUse.Park: return "Park";
                case LandUse.Vacant: return "Vacant site";
                case LandUse.Civic: return "Civic facility";
                default: return use.ToString();
            }
        }

        public static string DisplayName(this Zoning zoning)
        {
            switch (zoning)
            {
                case Zoning.Mixed: return "Mixed use (residential, commercial, park)";
                case Zoning.ResidentialOnly: return "Residential only (no commercial)";
                case Zoning.Protected: return "Protected (not editable)";
                default: return zoning.ToString();
            }
        }

        public static bool IsBuilding(this LandUse use)
        {
            return use == LandUse.Residential || use == LandUse.Commercial || use == LandUse.Civic;
        }
    }
}
