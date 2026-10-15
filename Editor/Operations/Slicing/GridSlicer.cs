using System.Collections.Generic;
using BetterSpriteEditor.Model;
using UnityEngine;

namespace BetterSpriteEditor.Operations.Slicing
{
    public struct GridSliceSettings
    {
        public bool ByCellCount;
        public int CellWidth;
        public int CellHeight;
        public int Columns;
        public int Rows;
        public int OffsetX;
        public int OffsetY;
        public int PaddingX;
        public int PaddingY;
        public bool SkipEmptyCells;
    }

    /// <summary>
    /// Grid slicing in actual texture space. Cells are generated top-left to bottom-right
    /// (stock naming order); offset is measured from the texture's top-left corner.
    /// </summary>
    public static class GridSlicer
    {
        /// <param name="alphaMap">Required only when SkipEmptyCells is set.</param>
        public static List<Rect> Slice(
            GridSliceSettings settings, int textureWidth, int textureHeight, TextureAlphaMap alphaMap)
        {
            var rects = new List<Rect>();

            if (textureWidth <= 0 || textureHeight <= 0 || settings.OffsetX < 0 || settings.OffsetY < 0
                || settings.PaddingX < 0 || settings.PaddingY < 0
                || (settings.ByCellCount && (settings.Columns < 1 || settings.Rows < 1)))
                return rects;

            int cellWidth;
            int cellHeight;
            if (settings.ByCellCount)
            {
                int columns = Mathf.Max(1, settings.Columns);
                int rows = Mathf.Max(1, settings.Rows);
                cellWidth = (int)(((long)textureWidth - settings.OffsetX - (long)settings.PaddingX * (columns - 1)) / columns);
                cellHeight = (int)(((long)textureHeight - settings.OffsetY - (long)settings.PaddingY * (rows - 1)) / rows);
            }
            else
            {
                cellWidth = settings.CellWidth;
                cellHeight = settings.CellHeight;
            }
            if (cellWidth < 1 || cellHeight < 1)
                return rects;

            // Count mode leaves indivisible remainder pixels at the right/bottom edges.
            int rowLimit = settings.ByCellCount ? settings.Rows : int.MaxValue;
            int columnLimit = settings.ByCellCount ? settings.Columns : int.MaxValue;
            for (int row = 0; row < rowLimit; row++)
            {
                long topY = settings.OffsetY + row * ((long)cellHeight + settings.PaddingY);
                if (topY + cellHeight > textureHeight)
                    break;
                for (int column = 0; column < columnLimit; column++)
                {
                    long x = settings.OffsetX + column * ((long)cellWidth + settings.PaddingX);
                    if (x + cellWidth > textureWidth)
                        break;
                    // topY is measured from the texture's top; rects use bottom-left origin.
                    var rect = new Rect(x, textureHeight - topY - cellHeight, cellWidth, cellHeight);
                    if (settings.SkipEmptyCells && alphaMap != null && !alphaMap.AnyOpaqueInActualRect(rect))
                        continue;
                    rects.Add(rect);
                }
            }
            return rects;
        }
    }
}
