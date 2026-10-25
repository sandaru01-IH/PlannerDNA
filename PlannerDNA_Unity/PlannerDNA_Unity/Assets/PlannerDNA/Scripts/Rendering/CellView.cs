using PlannerDNA.Core;
using UnityEngine;

namespace PlannerDNA.Rendering
{
    /// <summary>
    /// Visual for one parcel. Holds only the coordinates it represents; the logical CellData lives
    /// in CityState. Destroying and rebuilding views never changes data or metrics.
    /// </summary>
    public sealed class CellView : MonoBehaviour
    {
        public int Row;
        public int Col;

        public enum TileTint
        {
            Normal,
            AccessOk,
            AccessNo,
        }

        // What is currently drawn, so the renderer redraws only cells that changed.
        public CellData DrawnCell { get; private set; }
        public bool DrawnChanged { get; private set; }
        public TileTint DrawnTint { get; private set; }
        public bool HasDrawn { get; private set; }
        public float TopY { get; private set; }

        private BoxCollider _groundCollider;
        private BoxCollider _massCollider;

        public bool NeedsRedraw(CellData cell, bool changed, TileTint tint)
        {
            return !HasDrawn || DrawnCell != cell || DrawnChanged != changed || DrawnTint != tint;
        }

        public void Draw(CellData cell, bool changed, TileTint tint, MaterialPalette p, float cellSize, float floorHeight)
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                GameObject child = transform.GetChild(i).gameObject;
                child.SetActive(false);
                Destroy(child);
            }

            float tileTop;
            if (cell.Use == LandUse.Road)
            {
                tileTop = 0.02f;
                PrimitiveFactory.Box(transform, "Road", new Vector3(0, tileTop * 0.5f, 0), new Vector3(cellSize, tileTop, cellSize), p.road);
            }
            else
            {
                tileTop = 0.06f;
                Material tileMat = TileMaterial(cell.Use, tint, p);
                float tile = cellSize * 0.94f;
                PrimitiveFactory.Box(transform, "Tile", new Vector3(0, tileTop * 0.5f, 0), new Vector3(tile, tileTop, tile), tileMat);
            }

            float top = tileTop;
            Vector2 massFootprint = Vector2.zero;
            switch (cell.Use)
            {
                case LandUse.Residential:
                    massFootprint = new Vector2(cellSize * 0.70f, cellSize * 0.70f);
                    top = Building(cell.Floors, massFootprint.x, massFootprint.y, tileTop, floorHeight, p.residential, p.residentialBand);
                    break;
                case LandUse.Commercial:
                    massFootprint = new Vector2(cellSize * 0.80f, cellSize * 0.80f);
                    top = Building(cell.Floors, massFootprint.x, massFootprint.y, tileTop, floorHeight, p.commercial, p.commercialBand);
                    break;
                case LandUse.Civic:
                    massFootprint = new Vector2(cellSize * 0.80f, cellSize * 0.58f);
                    top = Building(cell.Floors, massFootprint.x, massFootprint.y, tileTop, floorHeight * 1.25f, p.civic, p.civicBand);
                    break;
                case LandUse.Park:
                    massFootprint = new Vector2(cellSize * 0.80f, cellSize * 0.80f);
                    top = Trees(cell, cellSize, tileTop, p);
                    break;
            }

            if (changed)
                PrimitiveFactory.Frame(transform, "ChangedMarker", cellSize * 0.94f, cellSize * 0.06f, 0.02f, p.changed)
                    .transform.localPosition = new Vector3(0, tileTop + 0.01f, 0);

            // Colliders follow the visible shapes: the whole parcel at ground level plus the building or
            // tree mass, so a ray never selects a parcel through empty space beside a tall building.
            if (_groundCollider == null) _groundCollider = gameObject.AddComponent<BoxCollider>();
            float groundHeight = Mathf.Max(tileTop, 0.05f);
            _groundCollider.center = new Vector3(0, groundHeight * 0.5f, 0);
            _groundCollider.size = new Vector3(cellSize, groundHeight, cellSize);

            if (_massCollider == null) _massCollider = gameObject.AddComponent<BoxCollider>();
            _massCollider.enabled = massFootprint != Vector2.zero;
            _massCollider.center = new Vector3(0, (tileTop + top) * 0.5f, 0);
            _massCollider.size = new Vector3(massFootprint.x, Mathf.Max(top - tileTop, 0.01f), massFootprint.y);

            DrawnCell = cell;
            DrawnChanged = changed;
            DrawnTint = tint;
            HasDrawn = true;
            TopY = top;
        }

        private static Material TileMaterial(LandUse use, TileTint tint, MaterialPalette p)
        {
            if (use == LandUse.Residential && tint == TileTint.AccessOk) return p.accessOk;
            if (use == LandUse.Residential && tint == TileTint.AccessNo) return p.accessNo;
            switch (use)
            {
                case LandUse.Park: return p.park;
                case LandUse.Vacant: return p.vacant;
                default: return p.lot;
            }
        }

        private float Building(int floors, float width, float depth, float baseY, float floorHeight, Material body, Material band)
        {
            float height = Mathf.Max(1, floors) * floorHeight;
            PrimitiveFactory.Box(transform, "Building", new Vector3(0, baseY + height * 0.5f, 0), new Vector3(width, height, depth), body);
            for (int f = 1; f < floors; f++)
            {
                PrimitiveFactory.Box(transform, "FloorBand", new Vector3(0, baseY + f * floorHeight, 0),
                    new Vector3(width + 0.02f, 0.025f, depth + 0.02f), band);
            }
            PrimitiveFactory.Box(transform, "Roof", new Vector3(0, baseY + height + 0.0125f, 0),
                new Vector3(width * 0.9f, 0.025f, depth * 0.9f), band);
            return baseY + height + 0.025f;
        }

        // Fixed tree layouts chosen by parcel coordinates, so parks look varied but deterministic.
        private static readonly Vector2[][] TreeLayouts =
        {
            new[] { new Vector2(-0.22f, -0.2f), new Vector2(0.2f, 0.18f) },
            new[] { new Vector2(-0.25f, 0.2f), new Vector2(0.22f, 0.22f), new Vector2(0f, -0.22f) },
            new[] { new Vector2(0.24f, -0.24f), new Vector2(-0.2f, -0.05f), new Vector2(0.12f, 0.25f) },
        };

        private float Trees(CellData cell, float cellSize, float baseY, MaterialPalette p)
        {
            Vector2[] layout = TreeLayouts[(cell.Row * 31 + cell.Col * 17) % TreeLayouts.Length];
            float top = baseY;
            for (int i = 0; i < layout.Length; i++)
            {
                Vector3 pos = new Vector3(layout[i].x * cellSize, 0, layout[i].y * cellSize);
                float trunkHeight = 0.14f * cellSize;
                float crown = (0.26f + 0.04f * ((cell.Row + cell.Col + i) % 3)) * cellSize;
                PrimitiveFactory.Cylinder(transform, "Trunk", pos + new Vector3(0, baseY + trunkHeight * 0.5f, 0), 0.05f * cellSize, trunkHeight, p.treeTrunk);
                PrimitiveFactory.Sphere(transform, "Crown", pos + new Vector3(0, baseY + trunkHeight + crown * 0.4f, 0), crown, p.treeCrown);
                top = Mathf.Max(top, baseY + trunkHeight + crown * 0.9f);
            }
            return top;
        }
    }
}
