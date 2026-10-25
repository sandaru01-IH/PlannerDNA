using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PlannerDNA.Rendering
{
    /// <summary>
    /// Raycast parcel selection. A click selects only if the mouse barely moved between press and
    /// release (so orbit/pan drags never select) and the press did not start over the UI.
    /// Selecting never edits; edits happen only through explicit tool buttons or shortcuts.
    /// </summary>
    public sealed class ParcelPicker : MonoBehaviour
    {
        [SerializeField] private Camera targetCamera;
        [SerializeField] private float clickTolerancePixels = 6f;

        public Func<Vector2, bool> IsPointerOverUi;
        public event Action<int, int> CellClicked;
        public event Action<int, int> CellHovered;

        private Vector2 _pressPosition;
        private bool _pressValid;
        private int _hoverRow = -1, _hoverCol = -1;

        public Camera TargetCamera
        {
            get => targetCamera != null ? targetCamera : Camera.main;
            set => targetCamera = value;
        }

        public bool TryPick(Vector2 screenPosition, out int row, out int col)
        {
            row = col = -1;
            Camera cam = TargetCamera;
            if (cam == null) return false;
            Ray ray = cam.ScreenPointToRay(screenPosition);
            if (!Physics.Raycast(ray, out RaycastHit hit, 1000f)) return false;
            CellView view = hit.collider.GetComponentInParent<CellView>();
            if (view == null) return false;
            row = view.Row;
            col = view.Col;
            return true;
        }

        private void Update()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null) return;
            Vector2 pos = mouse.position.ReadValue();
            bool overUi = IsPointerOverUi != null && IsPointerOverUi(pos);

            if (mouse.leftButton.wasPressedThisFrame)
            {
                _pressPosition = pos;
                _pressValid = !overUi;
            }
            if (mouse.leftButton.wasReleasedThisFrame)
            {
                if (_pressValid && !overUi && (pos - _pressPosition).magnitude <= clickTolerancePixels && TryPick(pos, out int r, out int c))
                    CellClicked?.Invoke(r, c);
                _pressValid = false;
            }

            int hr = -1, hc = -1;
            if (!overUi) TryPick(pos, out hr, out hc);
            if (hr != _hoverRow || hc != _hoverCol)
            {
                _hoverRow = hr;
                _hoverCol = hc;
                CellHovered?.Invoke(hr, hc);
            }
        }
    }
}
