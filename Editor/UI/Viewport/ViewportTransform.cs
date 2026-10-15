using UnityEngine;

namespace BetterSpriteEditor.UI.Viewport
{
    /// <summary>
    /// Maps texture space (pixels, bottom-left origin — same as sprite rects) to view space
    /// (element pixels, top-left origin). Pure math, no UI dependencies.
    /// </summary>
    public class ViewportTransform
    {
        public const float MinZoom = 0.02f;
        public const float MaxZoom = 100f;

        /// <summary>View pixels per texture pixel.</summary>
        public float Zoom { get; private set; } = 1f;

        /// <summary>View-space position of the texture's top-left corner.</summary>
        public Vector2 Offset { get; private set; }

        public int TextureWidth { get; private set; }
        public int TextureHeight { get; private set; }

        public void SetTextureSize(int width, int height)
        {
            TextureWidth = width;
            TextureHeight = height;
        }

        public Vector2 TextureToView(Vector2 texturePoint)
        {
            return new Vector2(
                Offset.x + texturePoint.x * Zoom,
                Offset.y + (TextureHeight - texturePoint.y) * Zoom);
        }

        public Vector2 ViewToTexture(Vector2 viewPoint)
        {
            return new Vector2(
                (viewPoint.x - Offset.x) / Zoom,
                TextureHeight - (viewPoint.y - Offset.y) / Zoom);
        }

        public Rect TextureRectToView(Rect textureRect)
        {
            var viewTopLeft = TextureToView(new Vector2(textureRect.xMin, textureRect.yMax));
            return new Rect(viewTopLeft, new Vector2(textureRect.width * Zoom, textureRect.height * Zoom));
        }

        public Rect TextureBoundsInView()
        {
            return TextureRectToView(new Rect(0f, 0f, TextureWidth, TextureHeight));
        }

        public void Pan(Vector2 viewDelta)
        {
            Offset += viewDelta;
        }

        /// <summary>Changes zoom while keeping the texture point under viewPoint stationary.</summary>
        public void ZoomAround(Vector2 viewPoint, float newZoom)
        {
            newZoom = Mathf.Clamp(newZoom, MinZoom, MaxZoom);
            var anchor = ViewToTexture(viewPoint);
            Zoom = newZoom;
            Offset += viewPoint - TextureToView(anchor);
        }

        /// <summary>Zooms to one texture rect, centered, with a view-space margin.</summary>
        public void Frame(Rect textureRect, Vector2 viewSize, float margin)
        {
            if (textureRect.width <= 0f || textureRect.height <= 0f
                || viewSize.x <= margin * 2f || viewSize.y <= margin * 2f)
                return;

            Zoom = Mathf.Clamp(
                Mathf.Min((viewSize.x - margin * 2f) / textureRect.width,
                    (viewSize.y - margin * 2f) / textureRect.height),
                MinZoom, MaxZoom);
            var centre = textureRect.center;
            Offset = new Vector2(
                viewSize.x * 0.5f - centre.x * Zoom,
                viewSize.y * 0.5f - (TextureHeight - centre.y) * Zoom);
        }

        /// <summary>Fits the whole texture into the view with a small margin, centered.</summary>
        public void Fit(Vector2 viewSize)
        {
            if (TextureWidth <= 0 || TextureHeight <= 0 || viewSize.x <= 0f || viewSize.y <= 0f)
                return;

            float fitZoom = Mathf.Min(viewSize.x / TextureWidth, viewSize.y / TextureHeight) * 0.95f;
            Zoom = Mathf.Clamp(fitZoom, MinZoom, MaxZoom);
            Offset = new Vector2(
                (viewSize.x - TextureWidth * Zoom) * 0.5f,
                (viewSize.y - TextureHeight * Zoom) * 0.5f);
        }
    }
}
