using System.Collections;
using System.Collections.Generic;
using BetterSpriteEditor.Model;
using UnityEngine;

namespace BetterSpriteEditor.Operations.Slicing
{
    /// <summary>
    /// Automatic slicing: bounding boxes of 8-connected opaque pixel components
    /// (Unity's own utility is internal, so this is our reimplementation).
    /// Results are sorted top-left to bottom-right for stable naming.
    /// </summary>
    public static class AutoSlicer
    {
        public static List<Rect> Slice(TextureAlphaMap alphaMap, int minWidth, int minHeight)
        {
            int width = alphaMap.Width;
            int height = alphaMap.Height;
            var visited = new BitArray(width * height);
            var stack = new Stack<int>();
            var rects = new List<Rect>();

            for (int startIndex = 0; startIndex < width * height; startIndex++)
            {
                if (visited[startIndex] || !alphaMap.IsOpaque(startIndex % width, startIndex / width))
                    continue;

                // Flood-fill one component, tracking its bounding box.
                int xMin = int.MaxValue, xMax = int.MinValue, yMin = int.MaxValue, yMax = int.MinValue;
                visited[startIndex] = true;
                stack.Push(startIndex);
                while (stack.Count > 0)
                {
                    int index = stack.Pop();
                    int x = index % width;
                    int y = index / width;
                    xMin = Mathf.Min(xMin, x);
                    xMax = Mathf.Max(xMax, x);
                    yMin = Mathf.Min(yMin, y);
                    yMax = Mathf.Max(yMax, y);

                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx;
                            int ny = y + dy;
                            if (nx < 0 || nx >= width || ny < 0 || ny >= height)
                                continue;
                            int neighborIndex = ny * width + nx;
                            if (visited[neighborIndex] || !alphaMap.IsOpaque(nx, ny))
                                continue;
                            visited[neighborIndex] = true;
                            stack.Push(neighborIndex);
                        }
                    }
                }

                var rect = alphaMap.ReadableBoundsToActualRect(xMin, yMin, xMax, yMax);
                if (rect.width >= minWidth && rect.height >= minHeight)
                    rects.Add(rect);
            }

            // Top row first, then left to right (texture space is y-up, so top = larger yMax).
            rects.Sort((a, b) => a.yMax != b.yMax ? b.yMax.CompareTo(a.yMax) : a.xMin.CompareTo(b.xMin));
            return rects;
        }
    }
}
