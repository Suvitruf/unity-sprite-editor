using System.Collections.Generic;
using BetterSpriteEditor.Model;
using UnityEditor;
using UnityEngine;

namespace BetterSpriteEditor.Operations.Pivots
{
    public enum PivotAlignMode
    {
        /// <summary>Anchor placed in each frame's inferred sheet cell (the robust default).</summary>
        InferredGrid,
        /// <summary>Anchor placed in each frame's opaque-pixel bounds (for packed sheets).</summary>
        ContentBounds,
        /// <summary>The active region's current pivot, transferred to the rest via the grid.</summary>
        ReferenceFrame
    }

    /// <summary>
    /// Computes per-frame pivot positions that keep an animation steady.
    ///
    /// Jitter happens because auto-sliced rects crop the art differently per frame, so a
    /// rect-relative pivot lands on a different point OF THE ARTWORK each frame. A stable
    /// pivot must be the same artwork point everywhere, which requires a shared reference
    /// space: the sheet's implicit cell grid (recoverable from the rect layout, and the
    /// ground truth the artist aligned to), or failing that the content bounds.
    ///
    /// Anchors are returned as texture-space points; converting to each frame's normalized
    /// pivot is the caller's one-liner. Pivots outside 0..1 are expected and correct for
    /// frames whose content does not reach the anchor.
    /// </summary>
    public static class PivotAlignment
    {
        public static Dictionary<GUID, Vector2> ComputeAnchors(
            IReadOnlyList<SpriteRegion> regions, PivotAlignMode mode, Vector2 anchorNormalized,
            SpriteRegion referenceRegion, TextureAlphaMap alphaMap, byte alphaTolerance)
        {
            var anchors = new Dictionary<GUID, Vector2>();
            if (regions == null || regions.Count == 0)
                return anchors;

            switch (mode)
            {
                case PivotAlignMode.ContentBounds:
                {
                    foreach (var region in regions)
                    {
                        var bounds = OpaqueBounds(alphaMap, region.Rect, alphaTolerance) ?? region.Rect;
                        anchors[region.Id] = bounds.min + Vector2.Scale(anchorNormalized, bounds.size);
                    }
                    break;
                }
                case PivotAlignMode.ReferenceFrame:
                {
                    var cells = InferCells(regions);
                    if (referenceRegion == null || !cells.TryGetValue(referenceRegion.Id, out var referenceCell)
                        || referenceCell.width <= 0f || referenceCell.height <= 0f)
                        break;
                    // Where the reference's pivot sits within ITS cell is the shared anchor.
                    var referencePivot = referenceRegion.GetPivotTexturePosition();
                    var inCell = new Vector2(
                        (referencePivot.x - referenceCell.xMin) / referenceCell.width,
                        (referencePivot.y - referenceCell.yMin) / referenceCell.height);
                    foreach (var region in regions)
                    {
                        if (cells.TryGetValue(region.Id, out var cell))
                            anchors[region.Id] = cell.min + Vector2.Scale(inCell, cell.size);
                    }
                    break;
                }
                default:
                {
                    var cells = InferCells(regions);
                    foreach (var region in regions)
                    {
                        if (cells.TryGetValue(region.Id, out var cell))
                            anchors[region.Id] = cell.min + Vector2.Scale(anchorNormalized, cell.size);
                    }
                    break;
                }
            }
            return anchors;
        }

        /// <summary>
        /// Recovers each frame's "home cell" from the rect layout: rows are clustered by
        /// vertical overlap, a row's band spans its tallest extents, and boundaries between
        /// horizontal neighbours sit at the midpoint of the gap — so uneven, auto-sliced rects
        /// still map back onto the sheet's implicit grid.
        /// </summary>
        public static Dictionary<GUID, Rect> InferCells(IReadOnlyList<SpriteRegion> regions)
        {
            var cells = new Dictionary<GUID, Rect>();
            var sorted = new List<SpriteRegion>(regions);
            sorted.Sort((a, b) => b.Rect.yMax.CompareTo(a.Rect.yMax));

            var rows = new List<List<SpriteRegion>>();
            var currentRow = new List<SpriteRegion>();
            float rowMin = 0f;
            float rowMax = 0f;
            foreach (var region in sorted)
            {
                bool overlapsRow = currentRow.Count > 0
                                   && region.Rect.yMax > rowMin && region.Rect.yMin < rowMax;
                if (currentRow.Count > 0 && !overlapsRow)
                {
                    rows.Add(currentRow);
                    currentRow = new List<SpriteRegion>();
                }
                if (currentRow.Count == 0)
                {
                    rowMin = region.Rect.yMin;
                    rowMax = region.Rect.yMax;
                }
                else
                {
                    rowMin = Mathf.Min(rowMin, region.Rect.yMin);
                    rowMax = Mathf.Max(rowMax, region.Rect.yMax);
                }
                currentRow.Add(region);
            }
            if (currentRow.Count > 0)
                rows.Add(currentRow);

            foreach (var row in rows)
            {
                row.Sort((a, b) => a.Rect.xMin.CompareTo(b.Rect.xMin));
                float bandMin = float.MaxValue;
                float bandMax = float.MinValue;
                foreach (var region in row)
                {
                    bandMin = Mathf.Min(bandMin, region.Rect.yMin);
                    bandMax = Mathf.Max(bandMax, region.Rect.yMax);
                }

                for (int i = 0; i < row.Count; i++)
                {
                    float left = i == 0
                        ? row[i].Rect.xMin
                        : (row[i - 1].Rect.xMax + row[i].Rect.xMin) * 0.5f;
                    float right = i == row.Count - 1
                        ? row[i].Rect.xMax
                        : (row[i].Rect.xMax + row[i + 1].Rect.xMin) * 0.5f;
                    cells[row[i].Id] = Rect.MinMaxRect(left, bandMin, right, bandMax);
                }
            }
            return cells;
        }

        private static Rect? OpaqueBounds(TextureAlphaMap alphaMap, Rect rect, byte tolerance)
        {
            if (alphaMap == null)
                return null;

            int originX = Mathf.RoundToInt(rect.x);
            int originY = Mathf.RoundToInt(rect.y);
            int width = Mathf.RoundToInt(rect.width);
            int height = Mathf.RoundToInt(rect.height);

            int xMin = int.MaxValue, xMax = int.MinValue, yMin = int.MaxValue, yMax = int.MinValue;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (!alphaMap.IsOpaqueActual(originX + x, originY + y, tolerance))
                        continue;
                    xMin = Mathf.Min(xMin, x);
                    xMax = Mathf.Max(xMax, x);
                    yMin = Mathf.Min(yMin, y);
                    yMax = Mathf.Max(yMax, y);
                }
            }
            if (xMin > xMax)
                return null;
            return Rect.MinMaxRect(
                originX + xMin, originY + yMin, originX + xMax + 1, originY + yMax + 1);
        }
    }
}
