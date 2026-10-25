using System.Collections.Generic;
using PlannerDNA.Core;

namespace PlannerDNA.Tests
{
    /// <summary>
    /// Builds small hand-checkable cities from ASCII maps.
    /// R = road, H = residential (floors given separately, default 2), C = commercial,
    /// P = park, V = vacant, X = civic, h = residential-only residential.
    /// </summary>
    public static class TestGrids
    {
        public static readonly ScenarioDefinition Scenario = ScenarioDefinition.GreenSpaceDilemma();

        public static CityState Build(string[] rows, int defaultFloors = 2, Dictionary<(int, int), int> floors = null)
        {
            int height = rows.Length, width = rows[0].Length;
            var cells = new List<CellData>(width * height);
            for (int r = 0; r < height; r++)
            {
                for (int c = 0; c < width; c++)
                {
                    char ch = rows[r][c];
                    int f = floors != null && floors.TryGetValue((r, c), out int custom) ? custom : defaultFloors;
                    switch (ch)
                    {
                        case 'R': cells.Add(new CellData(r, c, LandUse.Road, 0, 0, 0, false, Zoning.Protected)); break;
                        case 'H': cells.Add(new CellData(r, c, LandUse.Residential, f, 4, 8, true, Zoning.Mixed)); break;
                        case 'h': cells.Add(new CellData(r, c, LandUse.Residential, f, 4, 8, true, Zoning.ResidentialOnly)); break;
                        case 'C': cells.Add(new CellData(r, c, LandUse.Commercial, f, 0, 8, true, Zoning.Mixed)); break;
                        case 'P': cells.Add(new CellData(r, c, LandUse.Park, 0, 0, 8, true, Zoning.Mixed)); break;
                        case 'V': cells.Add(new CellData(r, c, LandUse.Vacant, 0, 0, 8, true, Zoning.Mixed)); break;
                        case 'X': cells.Add(new CellData(r, c, LandUse.Civic, 2, 0, 8, false, Zoning.Protected)); break;
                        default: throw new System.ArgumentException($"Unknown map char '{ch}'");
                    }
                }
            }
            return new CityState("test-grid", 1, Versions.GeneratorVersion, Versions.MetricVersion, Versions.SchemaVersion,
                width, height, 40, cells);
        }
    }
}
