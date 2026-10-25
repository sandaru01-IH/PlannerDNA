using System;
using System.Collections.Generic;
using PlannerDNA.Core;
using PlannerDNA.Metrics;

namespace PlannerDNA.Generation
{
    public sealed class GenerationException : Exception
    {
        public GenerationException(string message) : base(message) { }
    }

    public sealed class GeneratedScenario
    {
        public ScenarioDefinition Definition;
        public CityState Baseline;
        /// <summary>Which deterministic attempt (0-based) satisfied all generator requirements.</summary>
        public int Attempt;
        /// <summary>A vacant parcel where adding a park improves access within budget (proves feasibility).</summary>
        public string ExampleImprovingCellId;
    }

    /// <summary>
    /// Deterministic 20x20 synthetic neighbourhood (green-gen-1.0.0).
    ///
    /// Layout rules:
    /// - Horizontal roads every 3rd row (0, 3, ..., 18) so every parcel touches a road.
    /// - Three vertical connector roads; their columns are chosen from fixed bands by the seed.
    /// - One or two small parks placed in one quadrant so baseline access is incomplete.
    /// - Commercial parcels cluster along the middle connector ("main street").
    /// - Vacant parcels and 1-2 protected civic facilities are scattered.
    ///
    /// The same seed and generator version always produce the same city. Candidate layouts that
    /// fail the requirements are regenerated deterministically (bounded attempts).
    /// </summary>
    public static class GreenSpaceScenarioGenerator
    {
        public const int Width = 20;
        public const int Height = 20;
        public const int CellMetres = 40;
        public const int MaxAttempts = 32;
        public const int DemoSeed = 42;
        /// <summary>Baseline access must be below this so the mission has room to improve.</summary>
        public const double MaxBaselineAccessPct = 75.0;
        public const int MinVacantCells = 3;

        public static string ScenarioIdFor(int seed) => $"green-{seed}";

        public static GeneratedScenario Generate(int seed, ScenarioDefinition definition = null)
        {
            definition = definition ?? ScenarioDefinition.GreenSpaceDilemma();
            var failures = new List<string>();
            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                CityState candidate = BuildCandidate(seed, attempt, definition);
                string failure = Validate(candidate, definition, out string improvingCell);
                if (failure == null)
                {
                    return new GeneratedScenario
                    {
                        Definition = definition,
                        Baseline = candidate,
                        Attempt = attempt,
                        ExampleImprovingCellId = improvingCell,
                    };
                }
                failures.Add($"attempt {attempt}: {failure}");
            }
            throw new GenerationException(
                $"Could not generate a valid Green-Space scenario for seed {seed} after {MaxAttempts} attempts. " +
                $"Try another seed. Last reason: {failures[failures.Count - 1]}");
        }

        private static CityState BuildCandidate(int seed, int attempt, ScenarioDefinition def)
        {
            DeterministicRng rng = DeterministicRng.ForAttempt(seed, attempt);
            var uses = new LandUse[Width * Height];
            var floors = new int[Width * Height];
            var zoning = new Zoning[Width * Height];

            int[] roadCols =
            {
                rng.Range(2, 5),   // west connector
                rng.Range(8, 12),  // main street
                rng.Range(15, 18), // east connector
            };
            int mainStreet = roadCols[1];

            for (int r = 0; r < Height; r++)
            {
                for (int c = 0; c < Width; c++)
                {
                    int i = r * Width + c;
                    bool road = r % 3 == 0 || Array.IndexOf(roadCols, c) >= 0;
                    if (road)
                    {
                        uses[i] = LandUse.Road;
                        zoning[i] = Zoning.Protected;
                        continue;
                    }

                    bool nearMainStreet = Math.Abs(c - mainStreet) == 1;
                    double roll = rng.NextDouble();
                    if (roll < 0.10)
                    {
                        uses[i] = LandUse.Vacant;
                        zoning[i] = rng.Chance(0.3) ? Zoning.ResidentialOnly : Zoning.Mixed;
                    }
                    else if (roll < (nearMainStreet ? 0.60 : 0.18))
                    {
                        uses[i] = LandUse.Commercial;
                        floors[i] = rng.Range(1, 5);
                        zoning[i] = Zoning.Mixed;
                    }
                    else
                    {
                        uses[i] = LandUse.Residential;
                        floors[i] = ResidentialFloors(rng);
                        zoning[i] = rng.Chance(0.35) ? Zoning.ResidentialOnly : Zoning.Mixed;
                    }
                }
            }

            PlaceParks(rng, uses, floors, zoning);
            PlaceCivic(rng, uses, floors, zoning);

            var cells = new CellData[Width * Height];
            for (int i = 0; i < cells.Length; i++)
            {
                int r = i / Width, c = i % Width;
                LandUse use = uses[i];
                bool editable = use != LandUse.Road && use != LandUse.Civic;
                int maxFloors = use == LandUse.Road ? 0 : def.MaxFloors;
                int upf = use == LandUse.Residential ? def.UnitsPerFloor : 0;
                cells[i] = new CellData(r, c, use, floors[i], upf, maxFloors, editable, editable ? zoning[i] : Zoning.Protected);
            }

            return new CityState(ScenarioIdFor(seed), seed, Versions.GeneratorVersion, Versions.MetricVersion,
                Versions.SchemaVersion, Width, Height, CellMetres, cells);
        }

        private static int ResidentialFloors(DeterministicRng rng)
        {
            double roll = rng.NextDouble();
            if (roll < 0.25) return 2;
            if (roll < 0.55) return 3;
            if (roll < 0.80) return 4;
            if (roll < 0.95) return 5;
            return 6;
        }

        private static void PlaceParks(DeterministicRng rng, LandUse[] uses, int[] floors, Zoning[] zoning)
        {
            // Pick one quadrant; parks there leave the opposite side of the neighbourhood under-served.
            int quadrant = rng.Range(0, 4);
            int rowMin = quadrant < 2 ? 1 : 10, colMin = quadrant % 2 == 0 ? 0 : 10;
            int parkCount = rng.Chance(0.5) ? 2 : 1;
            int placed = 0, guard = 0;
            while (placed < parkCount && guard++ < 200)
            {
                int r = rowMin + rng.Range(0, 9);
                int c = colMin + rng.Range(0, 10);
                if (r >= Height || c >= Width) continue;
                int i = r * Width + c;
                if (uses[i] == LandUse.Road || uses[i] == LandUse.Park) continue;
                SetPark(uses, floors, zoning, i);
                // Extend to the horizontal neighbour so each park is a 2-cell green.
                int j = c + 1 < Width ? i + 1 : i - 1;
                if (uses[j] != LandUse.Road) SetPark(uses, floors, zoning, j);
                placed++;
            }
        }

        private static void SetPark(LandUse[] uses, int[] floors, Zoning[] zoning, int i)
        {
            uses[i] = LandUse.Park;
            floors[i] = 0;
            zoning[i] = Zoning.Mixed;
        }

        private static void PlaceCivic(DeterministicRng rng, LandUse[] uses, int[] floors, Zoning[] zoning)
        {
            int count = rng.Range(1, 3);
            int placed = 0, guard = 0;
            while (placed < count && guard++ < 200)
            {
                int i = rng.Range(0, Width * Height);
                if (uses[i] != LandUse.Residential && uses[i] != LandUse.Commercial) continue;
                uses[i] = LandUse.Civic;
                floors[i] = 2;
                zoning[i] = Zoning.Protected;
                placed++;
            }
        }

        /// <summary>Returns null when the candidate satisfies every requirement, else the reason.</summary>
        public static string Validate(CityState state, ScenarioDefinition def, out string improvingCellId)
        {
            improvingCellId = null;
            if (!AccessibilityCalculator.RoadNetworkConnected(state)) return "road network is disconnected";

            var scratch = new List<int>(4);
            int vacant = 0, parksWithRoad = 0;
            for (int i = 0; i < state.CellCount; i++)
            {
                CellData c = state.Get(i);
                if (c.Use == LandUse.Road) continue;
                if (!AccessibilityCalculator.HasRoadAccess(state, i, scratch)) return $"parcel {c.Id} has no road access";
                if (c.Use == LandUse.Vacant) vacant++;
                if (c.Use == LandUse.Park) parksWithRoad++;
            }
            if (parksWithRoad == 0) return "no park with road access";
            if (vacant < MinVacantCells) return $"only {vacant} vacant parcel(s); need {MinVacantCells}";

            CityMetrics baseline = MetricEngine.EvaluateBaseline(state, def);
            if (baseline.HousingUnits <= 0) return "no housing capacity";
            if (baseline.AccessPct >= MaxBaselineAccessPct)
                return $"baseline access {baseline.AccessPct:0.0}% leaves too little room to improve";

            // Feasibility proof: some single vacant->park edit improves access and satisfies all constraints.
            for (int i = 0; i < state.CellCount; i++)
            {
                CellData c = state.Get(i);
                if (c.Use != LandUse.Vacant || !c.Editable) continue;
                CityState edited = state.WithCell(c.With(LandUse.Park, 0, 0));
                CityMetrics m = MetricEngine.Evaluate(edited, state, def);
                if (m.AccessPct <= baseline.AccessPct) continue;
                if (!ConstraintEvaluator.Evaluate(edited, m, baseline, def).Feasible) continue;
                improvingCellId = c.Id;
                return null;
            }
            return "no single vacant-to-park edit improves access within constraints";
        }
    }
}
