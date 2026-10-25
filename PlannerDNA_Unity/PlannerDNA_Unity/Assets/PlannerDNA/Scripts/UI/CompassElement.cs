using UnityEngine;
using UnityEngine.UIElements;

namespace PlannerDNA.UI
{
    /// <summary>North arrow drawn with Painter2D; rotated to match the camera so it always points to world north (+Z).</summary>
    public sealed class CompassElement : VisualElement
    {
        private static readonly Color North = new Color(0.85f, 0.38f, 0.17f);
        private static readonly Color South = new Color(0.62f, 0.60f, 0.57f);

        public CompassElement()
        {
            pickingMode = PickingMode.Ignore;
            style.width = 44;
            style.height = 44;
            generateVisualContent += Draw;

            var label = new Label("N");
            label.pickingMode = PickingMode.Ignore;
            label.style.position = Position.Absolute;
            label.style.left = 17;
            label.style.top = -3;
            label.style.fontSize = 10;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.color = North;
            Add(label);
        }

        public void SetCameraYaw(float yawDegrees)
        {
            style.rotate = new StyleRotate(new Rotate(new Angle(-yawDegrees, AngleUnit.Degree)));
        }

        private void Draw(MeshGenerationContext ctx)
        {
            float w = contentRect.width, h = contentRect.height;
            if (w <= 0 || h <= 0) return;
            var c = new Vector2(w * 0.5f, h * 0.55f);
            Painter2D p = ctx.painter2D;

            p.fillColor = North;
            p.BeginPath();
            p.MoveTo(c + new Vector2(0, -h * 0.36f));
            p.LineTo(c + new Vector2(w * 0.15f, 0));
            p.LineTo(c + new Vector2(-w * 0.15f, 0));
            p.ClosePath();
            p.Fill();

            p.fillColor = South;
            p.BeginPath();
            p.MoveTo(c + new Vector2(0, h * 0.36f));
            p.LineTo(c + new Vector2(w * 0.15f, 0));
            p.LineTo(c + new Vector2(-w * 0.15f, 0));
            p.ClosePath();
            p.Fill();
        }
    }
}
