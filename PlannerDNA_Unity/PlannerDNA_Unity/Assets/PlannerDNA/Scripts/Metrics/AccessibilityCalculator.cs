using System.Collections.Generic;
using PlannerDNA.Core;

namespace PlannerDNA.Metrics
{
    /// <summary>
    /// Road-network walking distance from each parcel to the nearest park.
    ///
    /// Network rules (metrics-1.0.0):
    /// - Road cells form a 4-neighbour graph; each road-to-road edge = 1 cell length (40 m).
    /// - A parcel connects to each 4-adjacent road cell with a link of 1 cell length (centroid to road centre).
    /// - A park connects to each 4-adjacent road cell the same way.
    /// - Distance = parcel link + shortest road path + park link. No Euclidean or off-road shortcuts.
    /// - A parcel with no adjacent road, or no road path to a park entry, is unreachable (-1).
    /// </summary>
    public static class AccessibilityCalculator
    {
        public const int Unreachable = -1;

        /// <summary>Returns network distance in metres to the nearest park for every cell (Unreachable if none).</summary>
        public static int[] ParkDistancesMetres(CityState state)
        {
            int n = state.CellCount;
            var roadSteps = new int[n];
            for (int i = 0; i < n; i++) roadSteps[i] = Unreachable;

            var neighbours = new List<int>(4);
            var queue = new Queue<int>();

            // Multi-source BFS: every road cell touching a park is a park entry at road distance 0.
            for (int i = 0; i < n; i++)
            {
                if (state.Get(i).Use != LandUse.Road) continue;
                state.GetNeighbours(i, neighbours);
                foreach (int nb in neighbours)
                {
                    if (state.Get(nb).Use == LandUse.Park)
                    {
                        roadSteps[i] = 0;
                        queue.Enqueue(i);
                        break;
                    }
                }
            }

            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                state.GetNeighbours(current, neighbours);
                foreach (int nb in neighbours)
                {
                    if (roadSteps[nb] != Unreachable || state.Get(nb).Use != LandUse.Road) continue;
                    roadSteps[nb] = roadSteps[current] + 1;
                    queue.Enqueue(nb);
                }
            }

            int cell = state.CellMetres;
            var result = new int[n];
            for (int i = 0; i < n; i++)
            {
                result[i] = Unreachable;
                if (state.Get(i).Use == LandUse.Road) continue;
                state.GetNeighbours(i, neighbours);
                int best = int.MaxValue;
                foreach (int nb in neighbours)
                {
                    if (state.Get(nb).Use != LandUse.Road || roadSteps[nb] == Unreachable) continue;
                    int metres = cell + roadSteps[nb] * cell + cell;
                    if (metres < best) best = metres;
                }
                if (best != int.MaxValue) result[i] = best;
            }
            return result;
        }

        public static bool HasRoadAccess(CityState state, int index, List<int> scratch)
        {
            state.GetNeighbours(index, scratch);
            foreach (int nb in scratch)
                if (state.Get(nb).Use == LandUse.Road) return true;
            return false;
        }

        /// <summary>True when all road cells form one connected component (and at least one exists).</summary>
        public static bool RoadNetworkConnected(CityState state)
        {
            int first = -1, roadCount = 0;
            for (int i = 0; i < state.CellCount; i++)
            {
                if (state.Get(i).Use != LandUse.Road) continue;
                roadCount++;
                if (first < 0) first = i;
            }
            if (roadCount == 0) return false;

            var seen = new bool[state.CellCount];
            var queue = new Queue<int>();
            var neighbours = new List<int>(4);
            seen[first] = true;
            queue.Enqueue(first);
            int visited = 0;
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                visited++;
                state.GetNeighbours(current, neighbours);
                foreach (int nb in neighbours)
                {
                    if (seen[nb] || state.Get(nb).Use != LandUse.Road) continue;
                    seen[nb] = true;
                    queue.Enqueue(nb);
                }
            }
            return visited == roadCount;
        }
    }
}
