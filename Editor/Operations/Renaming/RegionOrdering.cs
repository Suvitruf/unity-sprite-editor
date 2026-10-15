using System.Collections.Generic;
using BetterSpriteEditor.Model;
using UnityEditor;
using UnityEngine;

namespace BetterSpriteEditor.Operations.Renaming
{
    public enum RenameOrder
    {
        RowMajor,
        ColumnMajor,
        SelectionOrder,
        ExistingName
    }

    /// <summary>
    /// Puts regions into the order a rename counter should follow. Spatial orders group regions
    /// into rows (or columns) first, so sprites on the same row stay together even when their
    /// heights differ slightly — a plain sort by coordinate would interleave them.
    /// </summary>
    public static class RegionOrdering
    {
        public static List<SpriteRegion> Sort(
            IEnumerable<SpriteRegion> regions, RenameOrder order, IReadOnlyList<GUID> selectionOrder)
        {
            var list = new List<SpriteRegion>(regions);
            switch (order)
            {
                case RenameOrder.RowMajor:
                    return SortRowMajor(list);
                case RenameOrder.ColumnMajor:
                    return SortColumnMajor(list);
                case RenameOrder.ExistingName:
                    list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                    return list;
                default:
                    return SortBySelection(list, selectionOrder);
            }
        }

        /// <summary>Top row first, left to right within each row.</summary>
        private static List<SpriteRegion> SortRowMajor(List<SpriteRegion> regions)
        {
            regions.Sort((a, b) => b.Rect.yMax.CompareTo(a.Rect.yMax));
            var result = new List<SpriteRegion>(regions.Count);
            var row = new List<SpriteRegion>();
            float rowMin = 0f;
            float rowMax = 0f;

            foreach (var region in regions)
            {
                bool overlapsRow = row.Count > 0 && region.Rect.yMax > rowMin && region.Rect.yMin < rowMax;
                if (row.Count > 0 && !overlapsRow)
                {
                    FlushRow(row, result, byX: true);
                    row.Clear();
                }
                if (row.Count == 0)
                {
                    rowMin = region.Rect.yMin;
                    rowMax = region.Rect.yMax;
                }
                else
                {
                    rowMin = Mathf.Min(rowMin, region.Rect.yMin);
                    rowMax = Mathf.Max(rowMax, region.Rect.yMax);
                }
                row.Add(region);
            }
            FlushRow(row, result, byX: true);
            return result;
        }

        /// <summary>Leftmost column first, top to bottom within each column.</summary>
        private static List<SpriteRegion> SortColumnMajor(List<SpriteRegion> regions)
        {
            regions.Sort((a, b) => a.Rect.xMin.CompareTo(b.Rect.xMin));
            var result = new List<SpriteRegion>(regions.Count);
            var column = new List<SpriteRegion>();
            float columnMin = 0f;
            float columnMax = 0f;

            foreach (var region in regions)
            {
                bool overlapsColumn = column.Count > 0
                                      && region.Rect.xMax > columnMin && region.Rect.xMin < columnMax;
                if (column.Count > 0 && !overlapsColumn)
                {
                    FlushRow(column, result, byX: false);
                    column.Clear();
                }
                if (column.Count == 0)
                {
                    columnMin = region.Rect.xMin;
                    columnMax = region.Rect.xMax;
                }
                else
                {
                    columnMin = Mathf.Min(columnMin, region.Rect.xMin);
                    columnMax = Mathf.Max(columnMax, region.Rect.xMax);
                }
                column.Add(region);
            }
            FlushRow(column, result, byX: false);
            return result;
        }

        private static void FlushRow(List<SpriteRegion> group, List<SpriteRegion> result, bool byX)
        {
            if (group.Count == 0)
                return;
            if (byX)
                group.Sort((a, b) => a.Rect.xMin.CompareTo(b.Rect.xMin));
            else
                group.Sort((a, b) => b.Rect.yMax.CompareTo(a.Rect.yMax));
            result.AddRange(group);
        }

        private static List<SpriteRegion> SortBySelection(
            List<SpriteRegion> regions, IReadOnlyList<GUID> selectionOrder)
        {
            if (selectionOrder == null)
                return regions;

            var result = new List<SpriteRegion>(regions.Count);
            foreach (var id in selectionOrder)
            {
                var region = regions.Find(candidate => candidate.Id == id);
                if (region != null)
                    result.Add(region);
            }
            // Anything not covered by the selection order (shouldn't happen) keeps its place.
            foreach (var region in regions)
            {
                if (!result.Contains(region))
                    result.Add(region);
            }
            return result;
        }
    }
}
