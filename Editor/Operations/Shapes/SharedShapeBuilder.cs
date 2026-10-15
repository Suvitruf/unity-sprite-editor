using System;
using System.Collections;
using System.Collections.Generic;
using BetterSpriteEditor.Model;
using UnityEngine;

namespace BetterSpriteEditor.Operations.Shapes
{
    /// <summary>
    /// Builds one shape covering several regions at once, for animation frames that should
    /// share a collider.
    ///
    /// Generating each frame separately gives slightly different polygons, so the collider
    /// twitches as the animation plays. Combining the frames' alpha into a single mask and
    /// generating once removes that entirely.
    ///
    /// Frames are aligned by their PIVOT, not by their rect corner: the pivot is what the
    /// sprite is positioned by, so it is the only alignment under which a shared shape stays
    /// still on screen when frames differ in size or offset.
    /// </summary>
    public static class SharedShapeBuilder
    {
        /// <summary>Generates a shape from the union of the regions, relative to their pivot.</summary>
        public static List<Vector2[]> Build(
            TextureAlphaMap alphaMap, IReadOnlyList<SpriteRegion> regions, ShapeGenerationSettings settings)
        {
            return Build(alphaMap, regions, settings, out _);
        }

        public static List<Vector2[]> Build(TextureAlphaMap alphaMap,
            IReadOnlyList<SpriteRegion> regions, ShapeGenerationSettings settings,
            out bool pointBudgetApplied)
        {
            pointBudgetApplied = false;
            if (alphaMap == null || regions == null || regions.Count == 0)
                return new List<Vector2[]>();

            // Keep exact pivots. Raster cells conservatively cover every translated source pixel.
            float minX = float.PositiveInfinity, minY = float.PositiveInfinity;
            float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
            foreach (var region in regions)
            {
                var pivot = PixelPivot(region);
                minX = Mathf.Min(minX, -pivot.x);
                minY = Mathf.Min(minY, -pivot.y);
                maxX = Mathf.Max(maxX, region.Rect.width - pivot.x);
                maxY = Mathf.Max(maxY, region.Rect.height - pivot.y);
            }
            float originX = Mathf.Floor(minX), originY = Mathf.Floor(minY);
            double canvasWidthValue = Math.Ceiling(maxX) - originX;
            double canvasHeightValue = Math.Ceiling(maxY) - originY;
            const long maximumCanvasPixels = 16_777_216;
            if (double.IsNaN(canvasWidthValue) || double.IsNaN(canvasHeightValue)
                || canvasWidthValue <= 0 || canvasHeightValue <= 0
                || canvasWidthValue * canvasHeightValue > maximumCanvasPixels)
                throw new InvalidOperationException("Shared shape exceeds the 16M pixel working limit. " +
                    "Reduce the selection or bring the frame pivots closer together.");
            int canvasWidth = (int)canvasWidthValue, canvasHeight = (int)canvasHeightValue;
            var canvas = new BitArray(canvasWidth * canvasHeight);
            foreach (var region in regions)
            {
                int width = Mathf.RoundToInt(region.Rect.width), height = Mathf.RoundToInt(region.Rect.height);
                var pivot = PixelPivot(region);
                var mask = OutlineGenerator.BuildMask(alphaMap, region.Rect, settings.AlphaTolerance, width, height);
                var offset = new Vector2(-pivot.x - originX, -pivot.y - originY);
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        if (!mask[y * width + x])
                            continue;
                        int x0 = Mathf.Max(0, Mathf.FloorToInt(x + offset.x));
                        int y0 = Mathf.Max(0, Mathf.FloorToInt(y + offset.y));
                        int x1 = Mathf.Min(canvasWidth, Mathf.CeilToInt(x + offset.x + 1));
                        int y1 = Mathf.Min(canvasHeight, Mathf.CeilToInt(y + offset.y + 1));
                        for (int targetY = y0; targetY < y1; targetY++)
                            for (int targetX = x0; targetX < x1; targetX++)
                                canvas[targetY * canvasWidth + targetX] = true;
                    }
                }
            }
            return OutlineGenerator.GenerateFromMask(canvas, canvasWidth, canvasHeight, settings,
                new Vector2(-originX, -originY), out pointBudgetApplied);
        }

        /// <summary>
        /// Converts without changing the shared geometry. Reject an incompatible frame instead
        /// of silently clipping it to a different collider.
        /// </summary>
        public static Vector2[] ToRegionSpace(Vector2[] pivotRelativePath, SpriteRegion region)
        {
            if (pivotRelativePath == null)
                return null;
            var halfSize = region.Rect.size * 0.5f;
            var offset = PixelPivot(region) - halfSize;
            var points = new List<Vector2>(pivotRelativePath.Length);
            foreach (var source in pivotRelativePath)
            {
                var point = source + offset;
                if (point.x < -halfSize.x - 0.0001f || point.x > halfSize.x + 0.0001f
                    || point.y < -halfSize.y - 0.0001f || point.y > halfSize.y + 0.0001f)
                    throw new InvalidOperationException($"The shared shape does not fit '{region.Name}'. " +
                        "Use larger frame bounds aligned to the same pivot, reduce Outset, or disable Shared Shape. " +
                        "No shapes have been changed.");
                points.Add(point);
            }
            return OutlineGenerator.ToPathArray(points);
        }

        private static Vector2 PixelPivot(SpriteRegion region)
        {
            var pivot = region.GetNormalizedPivot();
            return new Vector2(pivot.x * region.Rect.width, pivot.y * region.Rect.height);
        }
    }
}
