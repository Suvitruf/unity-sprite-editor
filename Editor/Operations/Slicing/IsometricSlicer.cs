using System.Collections.Generic;
using BetterSpriteEditor.Model;
using UnityEngine;

namespace BetterSpriteEditor.Operations.Slicing
{
    public struct IsometricSliceSettings
    {
        public int CellWidth;
        public int CellHeight;
        public int OffsetX;
        public int OffsetY;
        /// <summary>Shifts the first row by half a cell (staggered grids start either way).</summary>
        public bool IsAlternate;
        public bool SkipEmptyCells;
    }

    /// <summary>
    /// Isometric slicing: a staggered grid where every row steps down by half a cell and
    /// alternates a half-cell horizontal offset, so cells interlock as diamonds.
    /// Emptiness is judged inside the diamond only, not the full bounding rect
    /// (a cell whose corners hold neighbouring art still counts as empty).
    /// Mirrors the behaviour of Unity's internal isometric slicer.
    /// </summary>
    public static class IsometricSlicer
    {
        private const float minOpaqueRatio = 0.01f;

        public static List<Rect> Slice(
            IsometricSliceSettings settings, int textureWidth, int textureHeight, TextureAlphaMap alphaMap)
        {
            var rects = new List<Rect>();
            float cellWidth = settings.CellWidth;
            float cellHeight = settings.CellHeight;
            if (cellWidth < 1f || cellHeight < 1f)
                return rects;

            bool skipEmpty = settings.SkipEmptyCells && alphaMap != null;
            float gradient = (cellWidth * 0.5f) / (cellHeight * 0.5f);

            bool isAlternate = settings.IsAlternate;
            float y = textureHeight - settings.OffsetY;
            while (y - cellHeight >= 0f)
            {
                float x = settings.OffsetX + (isAlternate ? cellWidth * 0.5f : 0f);
                while (x + cellWidth <= textureWidth)
                {
                    var rect = new Rect(x, y - cellHeight, cellWidth, cellHeight);
                    if (!skipEmpty || DiamondHasContent(rect, settings, gradient, alphaMap))
                        rects.Add(rect);
                    x += cellWidth;
                }
                isAlternate = !isAlternate;
                y -= cellHeight * 0.5f;
            }
            return rects;
        }

        /// <summary>
        /// Walks the diamond row by row from its horizontal axis outwards, narrowing by the
        /// gradient each step, and reports whether enough of it is opaque.
        /// </summary>
        private static bool DiamondHasContent(
            Rect rect, IsometricSliceSettings settings, float gradient, TextureAlphaMap alphaMap)
        {
            int originX = (int)rect.x;
            int originY = (int)rect.y;
            int width = settings.CellWidth;
            int odd = settings.CellHeight % 2;
            int topY = settings.CellHeight / 2 - 1;
            int bottomY = topY + odd;

            int totalPixels = 0;
            int opaquePixels = 0;
            for (int rowOffset = 0; rowOffset <= topY; rowOffset++)
            {
                int inset = Mathf.CeilToInt(gradient * rowOffset);
                for (int x = inset; x < width - inset; x++)
                {
                    if (alphaMap.IsOpaqueActual(originX + x, originY + topY - rowOffset))
                        opaquePixels++;
                    if (alphaMap.IsOpaqueActual(originX + x, originY + bottomY + rowOffset))
                        opaquePixels++;
                    totalPixels += 2;
                }
            }

            // Odd heights leave one unpaired middle row spanning the full width.
            if (odd > 0)
            {
                for (int x = 0; x < width; x++)
                {
                    if (alphaMap.IsOpaqueActual(originX + x, originY + topY + 1))
                        opaquePixels++;
                    totalPixels++;
                }
            }

            return totalPixels > 0 && opaquePixels / (float)totalPixels > minOpaqueRatio;
        }
    }
}
