using System.Collections.Generic;
using UnityEngine;

namespace BetterSpriteEditor.Tools
{
    /// <summary>
    /// Handle names use TEXTURE orientation (Top = texture yMax), which is the view rect's
    /// top edge on screen — the y-flip is contained entirely inside this class.
    /// </summary>
    public enum RectHandle
    {
        None,
        Body,
        TopLeft,
        TopRight,
        BottomRight,
        BottomLeft,
        Top,
        Right,
        Bottom,
        Left
    }

    public static class RectHandles
    {
        // Corners listed before edges so corner grabs win when they overlap on tiny rects.
        private static readonly RectHandle[] handleOrder =
        {
            RectHandle.TopLeft, RectHandle.TopRight, RectHandle.BottomRight, RectHandle.BottomLeft,
            RectHandle.Top, RectHandle.Right, RectHandle.Bottom, RectHandle.Left
        };

        public static Vector2 GetHandleViewPoint(Rect viewRect, RectHandle handle)
        {
            return handle switch
            {
                RectHandle.TopLeft => new Vector2(viewRect.xMin, viewRect.yMin),
                RectHandle.Top => new Vector2(viewRect.center.x, viewRect.yMin),
                RectHandle.TopRight => new Vector2(viewRect.xMax, viewRect.yMin),
                RectHandle.Right => new Vector2(viewRect.xMax, viewRect.center.y),
                RectHandle.BottomRight => new Vector2(viewRect.xMax, viewRect.yMax),
                RectHandle.Bottom => new Vector2(viewRect.center.x, viewRect.yMax),
                RectHandle.BottomLeft => new Vector2(viewRect.xMin, viewRect.yMax),
                RectHandle.Left => new Vector2(viewRect.xMin, viewRect.center.y),
                _ => viewRect.center
            };
        }

        public static IEnumerable<RectHandle> AllHandles => handleOrder;

        /// <summary>
        /// Corners win first (cornerRadius), then anywhere along an edge within edgeRadius —
        /// the edge buffer is what makes resizing win over selecting a neighboring region.
        /// </summary>
        public static RectHandle HitTest(Rect viewRect, Vector2 viewPos, float cornerRadius, float edgeRadius)
        {
            float sqrCornerRadius = cornerRadius * cornerRadius;
            foreach (var corner in new[]
                     { RectHandle.TopLeft, RectHandle.TopRight, RectHandle.BottomRight, RectHandle.BottomLeft })
            {
                if ((GetHandleViewPoint(viewRect, corner) - viewPos).sqrMagnitude <= sqrCornerRadius)
                    return corner;
            }

            bool inHorizontalSpan = viewPos.x >= viewRect.xMin - edgeRadius && viewPos.x <= viewRect.xMax + edgeRadius;
            bool inVerticalSpan = viewPos.y >= viewRect.yMin - edgeRadius && viewPos.y <= viewRect.yMax + edgeRadius;
            if (inHorizontalSpan && Mathf.Abs(viewPos.y - viewRect.yMin) <= edgeRadius)
                return RectHandle.Top;
            if (inHorizontalSpan && Mathf.Abs(viewPos.y - viewRect.yMax) <= edgeRadius)
                return RectHandle.Bottom;
            if (inVerticalSpan && Mathf.Abs(viewPos.x - viewRect.xMin) <= edgeRadius)
                return RectHandle.Left;
            if (inVerticalSpan && Mathf.Abs(viewPos.x - viewRect.xMax) <= edgeRadius)
                return RectHandle.Right;

            return viewRect.Contains(viewPos) ? RectHandle.Body : RectHandle.None;
        }

        /// <summary>
        /// Applies a texture-space drag delta to the edges the handle controls.
        /// Result is integer-snapped, at least 1x1 and inside the texture. No edge flipping.
        /// </summary>
        public static Rect ResizeTextureRect(
            Rect startRect, RectHandle handle, Vector2 textureDelta, int textureWidth, int textureHeight)
        {
            bool left = handle is RectHandle.TopLeft or RectHandle.Left or RectHandle.BottomLeft;
            bool right = handle is RectHandle.TopRight or RectHandle.Right or RectHandle.BottomRight;
            bool top = handle is RectHandle.TopLeft or RectHandle.Top or RectHandle.TopRight;
            bool bottom = handle is RectHandle.BottomLeft or RectHandle.Bottom or RectHandle.BottomRight;

            float xMin = startRect.xMin + (left ? textureDelta.x : 0f);
            float xMax = startRect.xMax + (right ? textureDelta.x : 0f);
            float yMin = startRect.yMin + (bottom ? textureDelta.y : 0f);
            float yMax = startRect.yMax + (top ? textureDelta.y : 0f);

            xMin = Mathf.Clamp(Mathf.Round(xMin), 0f, textureWidth);
            xMax = Mathf.Clamp(Mathf.Round(xMax), 0f, textureWidth);
            yMin = Mathf.Clamp(Mathf.Round(yMin), 0f, textureHeight);
            yMax = Mathf.Clamp(Mathf.Round(yMax), 0f, textureHeight);

            if (left)
                xMin = Mathf.Min(xMin, xMax - 1f);
            if (right)
                xMax = Mathf.Max(xMax, xMin + 1f);
            if (bottom)
                yMin = Mathf.Min(yMin, yMax - 1f);
            if (top)
                yMax = Mathf.Max(yMax, yMin + 1f);

            return Rect.MinMaxRect(
                Mathf.Max(0f, xMin), Mathf.Max(0f, yMin),
                Mathf.Min(textureWidth, xMax), Mathf.Min(textureHeight, yMax));
        }
    }
}
