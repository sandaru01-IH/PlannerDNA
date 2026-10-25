using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace PlannerDNA.Rendering
{
    /// <summary>
    /// Orthographic isometric-style camera. Right-drag orbits, middle-drag or WASD/arrows pans,
    /// scroll zooms, Q/E rotates 45 degrees, F focuses the selection, Home resets.
    /// Input that starts over the UI is ignored so panels never move the camera by accident.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public sealed class CameraRig : MonoBehaviour
    {
        [SerializeField] private float defaultYaw = 45f;
        [SerializeField] private float defaultPitch = 52f;
        [Tooltip("Used only until a city has been framed.")]
        [SerializeField] private float initialOrthoSize = 12.5f;
        [Tooltip("Extra space around the city when framing (1 = edges touch the viewport).")]
        [SerializeField] private float framePadding = 1.06f;
        [Tooltip("Zoom level used when focusing on a selected parcel (half-height in world units; 1 unit = 1 parcel).")]
        [SerializeField] private float focusOrthoSize = 7f;
        [SerializeField] private float minOrthoSize = 3f;
        [SerializeField] private float maxOrthoSize = 30f;
        [SerializeField] private float distance = 60f;
        [SerializeField] private float orbitSpeed = 0.25f;
        [SerializeField] private float keyPanSpeed = 1.2f;

        private Camera _camera;
        private Vector3 _pivot, _defaultPivot;
        private Bounds? _frameBounds;
        private float _yaw, _pitch, _targetYaw, _orthoSize;
        private bool _orbiting, _panning;

        /// <summary>Returns true when the given screen position (pixels, origin bottom-left) is over UI.</summary>
        public Func<Vector2, bool> IsPointerOverUi;
        /// <summary>True while a text field has keyboard focus (camera keys are then ignored).</summary>
        public Func<bool> IsTextInputFocused;
        /// <summary>Visible 3D viewport in screen pixels (origin bottom-left); the pivot is drawn at its centre.</summary>
        public Func<Rect?> ViewportRect;

        public Camera Camera => _camera != null ? _camera : (_camera = GetComponent<Camera>());
        public float Yaw => _yaw;
        public float OrthoSize => _orthoSize;
        public Vector3 Pivot => _pivot;

        private void Awake()
        {
            _camera = GetComponent<Camera>();
            _camera.orthographic = true;
            _yaw = _targetYaw = defaultYaw;
            _pitch = defaultPitch;
            _orthoSize = initialOrthoSize;
            Apply();
        }

        public void Frame(Bounds bounds)
        {
            _frameBounds = bounds;
            _defaultPivot = bounds.center;
            ResetView();
        }

        public void ResetView()
        {
            _pivot = _defaultPivot;
            _yaw = _targetYaw = defaultYaw;
            _pitch = defaultPitch;
            _orthoSize = _frameBounds.HasValue ? FitOrthoSize(_frameBounds.Value) : initialOrthoSize;
            Apply();
        }

        /// <summary>Sets the orbit angle and zoom directly (scripted views, e.g. a recorded demo).</summary>
        public void SetView(float yaw, float orthoSize)
        {
            _yaw = _targetYaw = yaw;
            _orthoSize = Mathf.Clamp(orthoSize, minOrthoSize, maxOrthoSize);
            Apply();
        }

        public void Focus(Vector3 worldPoint)
        {
            _pivot = new Vector3(worldPoint.x, _defaultPivot.y, worldPoint.z);
            _orthoSize = Mathf.Min(_orthoSize, focusOrthoSize);
            Apply();
        }

        /// <summary>
        /// Smallest orthographic half-height at the default angle that shows the whole bounds inside
        /// the visible viewport (not the full screen, which is partly covered by panels).
        /// </summary>
        public float FitOrthoSize(Bounds bounds)
        {
            Quaternion rotation = Quaternion.Euler(defaultPitch, defaultYaw, 0);
            Vector3 right = rotation * Vector3.right, up = rotation * Vector3.up;
            float halfWidth = 0f, halfHeight = 0f;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = bounds.center + Vector3.Scale(bounds.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                Vector3 d = corner - bounds.center;
                halfWidth = Mathf.Max(halfWidth, Mathf.Abs(Vector3.Dot(d, right)));
                halfHeight = Mathf.Max(halfHeight, Mathf.Abs(Vector3.Dot(d, up)));
            }

            float screenW = Mathf.Max(1, Screen.width), screenH = Mathf.Max(1, Screen.height);
            Rect view = ViewportRect?.Invoke() ?? new Rect(0, 0, screenW, screenH);
            if (view.width < 1 || view.height < 1) view = new Rect(0, 0, screenW, screenH);

            // Half-height h spans screenH pixels, so the viewport shows h * view.height / screenH world units vertically.
            float needed = Mathf.Max(halfHeight * screenH / view.height, halfWidth * screenH / view.width);
            return Mathf.Clamp(needed * framePadding, minOrthoSize, maxOrthoSize);
        }

        private void Update()
        {
            Mouse mouse = Mouse.current;
            Keyboard keyboard = Keyboard.current;
            float dt = Time.unscaledDeltaTime;

            if (mouse != null)
            {
                Vector2 pos = mouse.position.ReadValue();
                bool overUi = IsPointerOverUi != null && IsPointerOverUi(pos);

                if (mouse.rightButton.wasPressedThisFrame && !overUi) _orbiting = true;
                if (!mouse.rightButton.isPressed) _orbiting = false;
                if (mouse.middleButton.wasPressedThisFrame && !overUi) _panning = true;
                if (!mouse.middleButton.isPressed) _panning = false;

                Vector2 delta = mouse.delta.ReadValue();
                if (_orbiting)
                {
                    _yaw += delta.x * orbitSpeed;
                    _targetYaw = _yaw;
                    _pitch = Mathf.Clamp(_pitch - delta.y * orbitSpeed * 0.8f, 25f, 85f);
                }
                if (_panning) PanByPixels(-delta);

                float scroll = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(scroll) > 0.01f && !overUi)
                    _orthoSize = Mathf.Clamp(_orthoSize * (scroll > 0 ? 0.9f : 1.1f), minOrthoSize, maxOrthoSize);
            }

            bool typing = IsTextInputFocused != null && IsTextInputFocused();
            if (keyboard != null && !typing && !keyboard.ctrlKey.isPressed)
            {
                Vector2 move = Vector2.zero;
                if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) move.y += 1;
                if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) move.y -= 1;
                if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) move.x += 1;
                if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) move.x -= 1;
                if (move != Vector2.zero) PanByWorld(move.normalized * keyPanSpeed * _orthoSize * dt);

                if (keyboard.qKey.wasPressedThisFrame) _targetYaw -= 45f;
                if (keyboard.eKey.wasPressedThisFrame) _targetYaw += 45f;
                if (keyboard.homeKey.wasPressedThisFrame) ResetView();
            }

            _yaw = Mathf.MoveTowardsAngle(_yaw, _targetYaw, 360f * dt);
            Apply();
        }

        private void PanByPixels(Vector2 pixels)
        {
            float worldPerPixel = 2f * _orthoSize / Mathf.Max(1, Screen.height);
            PanByWorld(pixels * worldPerPixel);
        }

        private void PanByWorld(Vector2 amount)
        {
            Quaternion yawOnly = Quaternion.Euler(0, _yaw, 0);
            Vector3 right = yawOnly * Vector3.right;
            Vector3 forward = yawOnly * Vector3.forward;
            _pivot += right * amount.x + forward * amount.y / Mathf.Sin(_pitch * Mathf.Deg2Rad);
        }

        private void Apply()
        {
            if (_camera == null) _camera = GetComponent<Camera>();
            Quaternion rotation = Quaternion.Euler(_pitch, _yaw, 0);
            Vector3 position = _pivot - rotation * Vector3.forward * distance;

            // Shift so the pivot appears at the centre of the visible viewport rather than the screen.
            Rect? view = ViewportRect?.Invoke();
            if (view.HasValue && Screen.height > 0)
            {
                float worldPerPixel = 2f * _orthoSize / Screen.height;
                Vector2 offset = view.Value.center - new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
                position -= (rotation * Vector3.right) * offset.x * worldPerPixel + (rotation * Vector3.up) * offset.y * worldPerPixel;
            }

            transform.SetPositionAndRotation(position, rotation);
            _camera.orthographicSize = _orthoSize;
            _camera.nearClipPlane = 0.1f;
            _camera.farClipPlane = distance * 3f;
        }
    }
}
