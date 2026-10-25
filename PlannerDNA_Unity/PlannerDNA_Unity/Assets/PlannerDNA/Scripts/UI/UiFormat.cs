using System.Globalization;
using PlannerDNA.Core;
using PlannerDNA.Metrics;
using UnityEngine.UIElements;

namespace PlannerDNA.UI
{
    public static class UiFormat
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Signed(int value) => value > 0 ? "+" + value : value.ToString(Inv);

        public static string SignedPoints(double value)
        {
            double rounded = System.Math.Round(value, 1);
            if (rounded == 0) return "0.0 pts";
            return (rounded > 0 ? "+" : "") + rounded.ToString("0.0", Inv) + " pts";
        }

        public static string Pct(double value) => value.ToString("0.0", Inv) + "%";

        public static string Access(CityMetrics m) => m.AccessDefined ? Pct(m.AccessPct) : "N/A";

        public static double AccessOrZero(CityMetrics m) => m.AccessDefined ? m.AccessPct : 0.0;

        public static string CellSummary(CellData c)
        {
            switch (c.Use)
            {
                case LandUse.Residential:
                case LandUse.Commercial:
                case LandUse.Civic:
                    return $"{c.Use.DisplayName()}, {c.Floors} floor{(c.Floors == 1 ? "" : "s")}";
                default:
                    return c.Use.DisplayName();
            }
        }

        public static string ParkWalk(int metres, int threshold)
        {
            if (metres == AccessibilityCalculator.Unreachable) return "No road path to any park";
            return metres <= threshold ? $"{metres} m (within {threshold} m)" : $"{metres} m (beyond {threshold} m)";
        }

        /// <summary>A label with one or more space-separated USS classes.</summary>
        public static Label Text(string text, string classNames)
        {
            var label = new Label(text);
            AddClasses(label, classNames);
            return label;
        }

        public static void AddClasses(VisualElement element, string classNames)
        {
            if (string.IsNullOrEmpty(classNames)) return;
            foreach (string c in classNames.Split(' '))
                if (c.Length > 0) element.AddToClassList(c);
        }

        public static VisualElement Box(string classNames)
        {
            var box = new VisualElement();
            AddClasses(box, classNames);
            return box;
        }

        public static VisualElement KeyValue(string key, string value)
        {
            var row = new VisualElement();
            row.AddToClassList("kv-row");
            row.Add(Text(key, "kv-key"));
            row.Add(Text(value, "kv-value"));
            return row;
        }
    }
}
