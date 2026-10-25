using PlannerDNA.Core;
using PlannerDNA.Metrics;
using UnityEngine;

namespace PlannerDNA.Rendering
{
    /// <summary>
    /// Draws a CityState with primitive meshes. Only cells whose logical data (or marker state)
    /// changed are rebuilt. World layout: column -> +X (east), row 0 at the north edge (+Z).
    /// One cell = <see cref="cellSize"/> Unity units; real distances always come from CityState.CellMetres.
    /// </summary>
    public sealed class GridRenderer : MonoBehaviour
    {
        [SerializeField] private MaterialPalette palette;
        [SerializeField] private float cellSize = 1f;
        [SerializeField] private float floorHeight = 0.26f;

        private CellView[] _views;
        private int _width, _height;
        private GameObject _ground, _selectionFrame, _hoverFrame;
        private bool _accessOverlay;
        private CityState _lastState, _lastBaseline;
        private CityMetrics _lastMetrics;
        private int _lastThreshold;

        public float CellSize => cellSize;
        public bool AccessOverlay => _accessOverlay;
        public int Width => _width;
        public int Height => _height;
        public int RedrawCountLastRender { get; private set; }

        public MaterialPalette Palette
        {
            get
            {
                if (palette == null || !palette.IsComplete) palette = MaterialPalette.CreateRuntimeDefault();
                return palette;
            }
            set => palette = value;
        }

        public Vector3 CellCentre(int row, int col)
        {
            return transform.TransformPoint(new Vector3((col - (_width - 1) * 0.5f) * cellSize, 0, ((_height - 1) * 0.5f - row) * cellSize));
        }

        public float CellTop(int row, int col)
        {
            CellView v = GetView(row, col);
            return v != null ? v.TopY : 0f;
        }

        public CellView GetView(int row, int col)
        {
            if (_views == null || row < 0 || col < 0 || row >= _height || col >= _width) return null;
            return _views[row * _width + col];
        }

        public Bounds WorldBounds()
        {
            float top = 0.1f;
            if (_views != null)
                foreach (CellView v in _views)
                    if (v != null) top = Mathf.Max(top, v.TopY);
            return new Bounds(transform.position + Vector3.up * (top * 0.5f), new Vector3(_width * cellSize, top, _height * cellSize));
        }

        public void Render(CityState state, CityState baseline, CityMetrics metrics, int accessThresholdMetres)
        {
            _lastState = state;
            _lastBaseline = baseline;
            _lastMetrics = metrics;
            _lastThreshold = accessThresholdMetres;

            EnsureGrid(state.Width, state.Height);
            MaterialPalette p = Palette;
            int redraws = 0;
            for (int i = 0; i < state.CellCount; i++)
            {
                CellData cell = state.Get(i);
                bool changed = baseline != null && baseline.Get(i) != cell;
                CellView.TileTint tint = CellView.TileTint.Normal;
                if (_accessOverlay && cell.Use == LandUse.Residential && metrics != null)
                {
                    int d = metrics.ParkDistanceMetres[i];
                    tint = d != AccessibilityCalculator.Unreachable && d <= accessThresholdMetres
                        ? CellView.TileTint.AccessOk : CellView.TileTint.AccessNo;
                }

                CellView view = _views[i];
                if (!view.NeedsRedraw(cell, changed, tint)) continue;
                view.Draw(cell, changed, tint, p, cellSize, floorHeight);
                redraws++;
            }
            RedrawCountLastRender = redraws;
            Physics.SyncTransforms();
        }

        public void SetAccessOverlay(bool enabled)
        {
            if (_accessOverlay == enabled) return;
            _accessOverlay = enabled;
            if (_lastState != null) Render(_lastState, _lastBaseline, _lastMetrics, _lastThreshold);
        }

        public void SetSelection(int row, int col) => PlaceFrame(_selectionFrame, row, col, 0.09f);
        public void SetHover(int row, int col) => PlaceFrame(_hoverFrame, row, col, 0.085f);

        private void PlaceFrame(GameObject frame, int row, int col, float y)
        {
            if (frame == null) return;
            bool visible = row >= 0 && col >= 0 && row < _height && col < _width;
            frame.SetActive(visible);
            if (visible) frame.transform.position = CellCentre(row, col) + Vector3.up * y;
        }

        private void EnsureGrid(int width, int height)
        {
            if (_views != null && width == _width && height == _height) return;
            Clear();
            _width = width;
            _height = height;
            MaterialPalette p = Palette;

            _ground = PrimitiveFactory.Box(transform, "Ground", new Vector3(0, -0.03f, 0),
                new Vector3((width + 1.5f) * cellSize, 0.04f, (height + 1.5f) * cellSize), p.ground);

            _views = new CellView[width * height];
            for (int r = 0; r < height; r++)
            {
                for (int c = 0; c < width; c++)
                {
                    var go = new GameObject(CellIds.Format(r, c));
                    go.transform.SetParent(transform, false);
                    go.transform.position = CellCentre(r, c);
                    CellView view = go.AddComponent<CellView>();
                    view.Row = r;
                    view.Col = c;
                    _views[r * width + c] = view;
                }
            }

            _selectionFrame = PrimitiveFactory.Frame(transform, "SelectionFrame", cellSize * 1.04f, cellSize * 0.08f, 0.05f, p.selection);
            _hoverFrame = PrimitiveFactory.Frame(transform, "HoverFrame", cellSize * 0.98f, cellSize * 0.035f, 0.03f, p.hover);
            _selectionFrame.SetActive(false);
            _hoverFrame.SetActive(false);
        }

        private void Clear()
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                GameObject child = transform.GetChild(i).gameObject;
                child.SetActive(false);
                Destroy(child);
            }
            _views = null;
        }
    }
}
