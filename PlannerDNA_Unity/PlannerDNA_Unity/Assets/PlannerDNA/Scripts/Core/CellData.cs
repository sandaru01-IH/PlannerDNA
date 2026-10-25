using System;

namespace PlannerDNA.Core
{
    /// <summary>
    /// Logical state of one grid parcel. Immutable: edits create a new value.
    /// Holds no Unity objects, so it can be serialized, compared and tested freely.
    /// </summary>
    public readonly struct CellData : IEquatable<CellData>
    {
        public readonly int Row;
        public readonly int Col;
        public readonly LandUse Use;
        /// <summary>0 for road, park and vacant cells.</summary>
        public readonly int Floors;
        /// <summary>Housing capacity units per floor; non-zero only for residential.</summary>
        public readonly int UnitsPerFloor;
        public readonly int MaxFloors;
        public readonly bool Editable;
        public readonly Zoning Zoning;

        public CellData(int row, int col, LandUse use, int floors, int unitsPerFloor, int maxFloors, bool editable, Zoning zoning)
        {
            Row = row;
            Col = col;
            Use = use;
            Floors = floors;
            UnitsPerFloor = unitsPerFloor;
            MaxFloors = maxFloors;
            Editable = editable;
            Zoning = zoning;
        }

        public string Id => CellIds.Format(Row, Col);

        public int HousingUnits => Use == LandUse.Residential ? Floors * UnitsPerFloor : 0;

        public CellData With(LandUse use, int floors, int unitsPerFloor)
        {
            return new CellData(Row, Col, use, floors, unitsPerFloor, MaxFloors, Editable, Zoning);
        }

        public CellData WithFloors(int floors)
        {
            return new CellData(Row, Col, Use, floors, UnitsPerFloor, MaxFloors, Editable, Zoning);
        }

        public bool Equals(CellData other)
        {
            return Row == other.Row && Col == other.Col && Use == other.Use && Floors == other.Floors
                && UnitsPerFloor == other.UnitsPerFloor && MaxFloors == other.MaxFloors
                && Editable == other.Editable && Zoning == other.Zoning;
        }

        public override bool Equals(object obj) => obj is CellData other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = Row;
                h = h * 397 ^ Col;
                h = h * 397 ^ (int)Use;
                h = h * 397 ^ Floors;
                h = h * 397 ^ UnitsPerFloor;
                h = h * 397 ^ MaxFloors;
                h = h * 397 ^ (Editable ? 1 : 0);
                h = h * 397 ^ (int)Zoning;
                return h;
            }
        }

        public static bool operator ==(CellData a, CellData b) => a.Equals(b);
        public static bool operator !=(CellData a, CellData b) => !a.Equals(b);

        public override string ToString()
        {
            return Use.IsBuilding() ? $"{Id} {Use.ToKey()} {Floors}F" : $"{Id} {Use.ToKey()}";
        }
    }

    public static class CellIds
    {
        public static string Format(int row, int col) => $"r{row:00}_c{col:00}";

        public static bool TryParse(string id, out int row, out int col)
        {
            row = col = -1;
            if (string.IsNullOrEmpty(id) || id.Length < 7 || id[0] != 'r') return false;
            int sep = id.IndexOf("_c", StringComparison.Ordinal);
            if (sep < 2) return false;
            return int.TryParse(id.Substring(1, sep - 1), out row) && int.TryParse(id.Substring(sep + 2), out col);
        }
    }
}
