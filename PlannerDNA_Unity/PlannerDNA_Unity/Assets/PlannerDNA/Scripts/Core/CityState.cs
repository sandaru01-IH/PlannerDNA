using System;
using System.Collections.Generic;

namespace PlannerDNA.Core
{
    /// <summary>
    /// Complete logical city at one moment. Immutable: <see cref="WithCell"/> returns a copy.
    /// This (not the rendered scene) is the source of truth for metrics and saves.
    /// </summary>
    public sealed class CityState
    {
        public string ScenarioId { get; }
        public int Seed { get; }
        public string GeneratorVersion { get; }
        public string MetricVersion { get; }
        public int SchemaVersion { get; }
        public int Width { get; }
        public int Height { get; }
        /// <summary>Logical edge length of one cell in metres (independent of Unity world units).</summary>
        public int CellMetres { get; }

        private readonly CellData[] _cells;

        public CityState(string scenarioId, int seed, string generatorVersion, string metricVersion, int schemaVersion,
            int width, int height, int cellMetres, IReadOnlyList<CellData> cells)
        {
            if (width <= 0 || height <= 0) throw new ArgumentException("Grid size must be positive.");
            if (cells == null || cells.Count != width * height)
                throw new ArgumentException($"Expected {width * height} cells, got {cells?.Count ?? 0}.");

            ScenarioId = scenarioId;
            Seed = seed;
            GeneratorVersion = generatorVersion;
            MetricVersion = metricVersion;
            SchemaVersion = schemaVersion;
            Width = width;
            Height = height;
            CellMetres = cellMetres;

            _cells = new CellData[cells.Count];
            for (int i = 0; i < cells.Count; i++)
            {
                CellData c = cells[i];
                if (c.Row != i / width || c.Col != i % width)
                    throw new ArgumentException($"Cell at index {i} has coordinates ({c.Row},{c.Col}); cells must be row-major.");
                _cells[i] = c;
            }
        }

        private CityState(CityState source, CellData[] cells)
        {
            ScenarioId = source.ScenarioId;
            Seed = source.Seed;
            GeneratorVersion = source.GeneratorVersion;
            MetricVersion = source.MetricVersion;
            SchemaVersion = source.SchemaVersion;
            Width = source.Width;
            Height = source.Height;
            CellMetres = source.CellMetres;
            _cells = cells;
        }

        public int CellCount => _cells.Length;
        public IReadOnlyList<CellData> Cells => _cells;

        public bool InBounds(int row, int col) => row >= 0 && row < Height && col >= 0 && col < Width;
        public int IndexOf(int row, int col) => row * Width + col;
        public CellData Get(int row, int col) => _cells[IndexOf(row, col)];
        public CellData Get(int index) => _cells[index];

        public bool TryGet(string cellId, out CellData cell)
        {
            cell = default;
            if (!CellIds.TryParse(cellId, out int r, out int c) || !InBounds(r, c)) return false;
            cell = Get(r, c);
            return true;
        }

        public CityState WithCell(CellData cell)
        {
            if (!InBounds(cell.Row, cell.Col)) throw new ArgumentOutOfRangeException(nameof(cell));
            var copy = (CellData[])_cells.Clone();
            copy[IndexOf(cell.Row, cell.Col)] = cell;
            return new CityState(this, copy);
        }

        /// <summary>4-neighbour indices in fixed N, E, S, W order (deterministic iteration).</summary>
        public void GetNeighbours(int index, List<int> result)
        {
            result.Clear();
            int row = index / Width, col = index % Width;
            if (row > 0) result.Add(index - Width);
            if (col < Width - 1) result.Add(index + 1);
            if (row < Height - 1) result.Add(index + Width);
            if (col > 0) result.Add(index - 1);
        }

        public bool SameScenarioAs(CityState other)
        {
            return other != null && ScenarioId == other.ScenarioId && Seed == other.Seed
                && GeneratorVersion == other.GeneratorVersion && MetricVersion == other.MetricVersion
                && SchemaVersion == other.SchemaVersion && Width == other.Width && Height == other.Height
                && CellMetres == other.CellMetres;
        }

        /// <summary>True when every header field and every cell is identical.</summary>
        public bool ContentEquals(CityState other)
        {
            if (!SameScenarioAs(other)) return false;
            for (int i = 0; i < _cells.Length; i++)
                if (_cells[i] != other._cells[i]) return false;
            return true;
        }

        public List<string> ChangedCellIds(CityState other)
        {
            var ids = new List<string>();
            for (int i = 0; i < _cells.Length; i++)
                if (_cells[i] != other._cells[i]) ids.Add(_cells[i].Id);
            return ids;
        }

        /// <summary>Stable FNV-1a hash of the canonical content; identical cities always share it.</summary>
        public string ContentHash()
        {
            var h = new StableHash();
            h.Add(ScenarioId).Add(Seed).Add(GeneratorVersion).Add(MetricVersion).Add(SchemaVersion)
                .Add(Width).Add(Height).Add(CellMetres);
            foreach (CellData c in _cells)
            {
                h.Add((int)c.Use).Add(c.Floors).Add(c.UnitsPerFloor).Add(c.MaxFloors)
                    .Add(c.Editable ? 1 : 0).Add((int)c.Zoning);
            }
            return h.ToHex();
        }
    }
}
