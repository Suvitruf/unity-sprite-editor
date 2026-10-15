using BetterSpriteEditor.Model;
using BetterSpriteEditor.Operations;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace BetterSpriteEditor.Tools
{
    /// <summary>
    /// Drag on the texture to create a new region (rubber-band preview, integer-snapped,
    /// at least 1x1). The new region becomes the selection.
    /// </summary>
    public class CreateTool : IViewportTool
    {
        private static readonly Color previewFillColor = new(0.3f, 0.8f, 1f, 0.12f);
        private static readonly Color previewStrokeColor = new(0.3f, 0.8f, 1f, 0.9f);

        // A plain click must not create a region — the drag has to travel at least this far.
        private const float minDragDistance = 5f;

        private readonly ToolContext context;

        private bool dragging;
        private bool passedDragThreshold;
        private Vector2 startViewPos;
        private Vector2 startTexturePos;
        private Rect previewRect;

        public CreateTool(ToolContext context)
        {
            this.context = context;
        }

        public bool OnPointerDown(Vector2 viewPos, PointerDownEvent evt)
        {
            if (!context.Document.CanChangeRegions)
                return false;
            startViewPos = viewPos;
            startTexturePos = ClampToTexture(context.Transform.ViewToTexture(viewPos));
            previewRect = SnapRect(startTexturePos, startTexturePos);
            dragging = true;
            passedDragThreshold = false;
            context.Repaint();
            return true;
        }

        public void OnPointerDrag(Vector2 viewPos)
        {
            if (!dragging)
                return;
            if (!passedDragThreshold && (viewPos - startViewPos).magnitude >= minDragDistance)
                passedDragThreshold = true;
            var current = ClampToTexture(context.Transform.ViewToTexture(viewPos));
            previewRect = SnapRect(startTexturePos, current);
            context.Repaint();
        }

        public void OnPointerUp(Vector2 viewPos, PointerUpEvent evt)
        {
            if (!dragging)
                return;
            dragging = false;

            if (passedDragThreshold && previewRect.width >= 1f && previewRect.height >= 1f)
            {
                var region = new SpriteRegion
                {
                    Id = GUID.Generate(),
                    Name = RegionValidator.GenerateUniqueName(context.Document, context.Document.Texture.name),
                    Rect = previewRect,
                    Alignment = SpriteAlignment.Center,
                    Pivot = new Vector2(0.5f, 0.5f),
                    Border = Vector4.zero,
                    CustomData = string.Empty
                };
                context.History.Do(new CreateRegionCommand(context.Document, region));
                context.Selection.Set(region.Id);
            }
            context.Repaint();
        }

        public bool OnKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode == KeyCode.Escape && dragging)
            {
                dragging = false;
                context.Repaint();
                return true;
            }
            return false;
        }

        public void OnDeactivate()
        {
            dragging = false;
        }

        public ViewportCursor GetCursor(Vector2 viewPos)
        {
            return ViewportCursor.Default;
        }

        public void DrawOverlay(Painter2D painter)
        {
            if (!dragging || !passedDragThreshold)
                return;

            var viewRect = context.Transform.TextureRectToView(previewRect);
            painter.fillColor = previewFillColor;
            painter.strokeColor = previewStrokeColor;
            painter.lineWidth = 1f;
            painter.BeginPath();
            painter.MoveTo(new Vector2(viewRect.xMin, viewRect.yMin));
            painter.LineTo(new Vector2(viewRect.xMax, viewRect.yMin));
            painter.LineTo(new Vector2(viewRect.xMax, viewRect.yMax));
            painter.LineTo(new Vector2(viewRect.xMin, viewRect.yMax));
            painter.ClosePath();
            painter.Fill();
            painter.Stroke();
        }

        private Vector2 ClampToTexture(Vector2 texturePos)
        {
            return new Vector2(
                Mathf.Clamp(texturePos.x, 0f, context.Document.TextureWidth),
                Mathf.Clamp(texturePos.y, 0f, context.Document.TextureHeight));
        }

        /// <summary>Floor the min corner, ceil the max — any visible drag yields at least 1x1.</summary>
        private static Rect SnapRect(Vector2 a, Vector2 b)
        {
            float xMin = Mathf.Floor(Mathf.Min(a.x, b.x));
            float yMin = Mathf.Floor(Mathf.Min(a.y, b.y));
            float xMax = Mathf.Ceil(Mathf.Max(a.x, b.x));
            float yMax = Mathf.Ceil(Mathf.Max(a.y, b.y));
            return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
        }
    }
}
