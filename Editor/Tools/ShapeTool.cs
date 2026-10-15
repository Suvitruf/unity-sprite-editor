using System.Collections.Generic;
using BetterSpriteEditor.Model;
using BetterSpriteEditor.Operations;
using UnityEngine;
using UnityEngine.UIElements;

namespace BetterSpriteEditor.Tools
{
    /// <summary>
    /// Edits the polygon paths of the active region (custom outline or physics shape).
    ///
    /// Dragging on empty space marquee-selects points; Shift adds to the selection. Selected
    /// points move together and delete together. Clicking an edge inserts a point there, and
    /// Ctrl+drag draws a new rectangular path. Clicking another region switches to it, so you
    /// can move between sprites without leaving the mode.
    /// </summary>
    public class ShapeTool : IViewportTool
    {
        private const float pointHitRadius = 7f;
        private const float edgeHitRadius = 5f;
        private const float pointDrawSize = 7f;
        private const float newPathMinSize = 4f;
        private const float marqueeThreshold = 3f;

        /// <summary>Points per Painter2D stroke call; see the flush in DrawOverlay.</summary>
        private const int maxBatchedPoints = 400;

        private static readonly Color pathColor = new(0.2f, 0.9f, 0.35f, 0.95f);
        private static readonly Color pointColor = Color.white;
        private static readonly Color selectedPointColor = new(1f, 0.85f, 0.2f);
        private static readonly Color pointBorderColor = new(0.15f, 0.15f, 0.15f);
        private static readonly Color newPathFillColor = new(0.2f, 0.9f, 0.35f, 0.12f);
        private static readonly Color marqueeFillColor = new(0.25f, 0.6f, 1f, 0.1f);
        private static readonly Color marqueeStrokeColor = new(0.25f, 0.6f, 1f, 0.9f);

        private enum DragKind
        {
            None,
            Points,
            Marquee,
            NewPath
        }

        private readonly ToolContext context;

        /// <summary>Selected points as (path index, point index) pairs of the active region.</summary>
        private readonly HashSet<(int path, int point)> selectedPoints = new();

        public ShapeKind Kind { get; set; } = ShapeKind.Physics;

        /// <summary>Mirrors the Shape section's toggle: dragged points land on whole pixels.</summary>
        public bool SnapToPixels { get; set; } = true;

        private DragKind dragKind = DragKind.None;
        private SpriteRegion draggedRegion;
        private SpriteRegion dragStartSnapshot;
        private Vector2 dragStartTexture;
        private Vector2 dragCurrentView;
        private Vector2 dragStartView;
        private bool marqueeAdditive;
        private bool marqueeSelectsRegions;
        private bool passedThreshold;

        public ShapeTool(ToolContext context)
        {
            this.context = context;
        }

        private SpriteRegion ActiveRegion => context.Document?.FindRegion(context.Selection.ActiveId);

        public bool OnPointerDown(Vector2 viewPos, PointerDownEvent evt)
        {
            var region = ActiveRegion;
            var texturePos = context.Transform.ViewToTexture(viewPos);
            bool shift = evt.shiftKey;
            bool ctrl = evt.ctrlKey || evt.commandKey;

            dragStartView = viewPos;
            dragCurrentView = viewPos;
            dragStartTexture = texturePos;
            passedThreshold = false;

            if (region != null)
            {
                var shapes = region.GetShapes(Kind);

                if (TryHitPoint(region, shapes, viewPos, out int pathIndex, out int pointIndex))
                {
                    var hit = (pathIndex, pointIndex);
                    if (shift)
                    {
                        if (!selectedPoints.Remove(hit))
                            selectedPoints.Add(hit);
                        context.Repaint();
                        return true;
                    }
                    if (!selectedPoints.Contains(hit))
                    {
                        selectedPoints.Clear();
                        selectedPoints.Add(hit);
                    }
                    BeginPointDrag(region);
                    context.Repaint();
                    return true;
                }

                if (!ctrl && TryHitEdge(region, shapes, viewPos, out pathIndex, out int insertAt))
                {
                    var before = region.Clone();
                    var path = new List<Vector2>(shapes[pathIndex]);
                    path.Insert(insertAt, region.TextureToShapePoint(Snap(texturePos)));
                    shapes[pathIndex] = path.ToArray();
                    ShiftSelectionAfterInsert(pathIndex, insertAt);
                    selectedPoints.Clear();
                    selectedPoints.Add((pathIndex, insertAt));
                    context.History.PushApplied(new ModifyRegionCommand(
                        context.Document, region.Id, before, region.Clone(),
                        $"Add shape point to '{region.Name}'"));
                    BeginPointDrag(region);
                    context.Repaint();
                    return true;
                }
            }

            var hitRegion = context.Document.FindRegionAt(texturePos);
            if (hitRegion != null && hitRegion != region)
            {
                // Shift or Ctrl builds a multi-region selection so Generate can target several
                // sprites at once; a plain click switches to editing just this one.
                if (shift || ctrl)
                    context.Selection.Add(hitRegion.Id);
                else
                    context.Selection.Set(hitRegion.Id);
                selectedPoints.Clear();
                context.Repaint();
                return true;
            }

            if (ctrl && region != null)
            {
                dragKind = DragKind.NewPath;
                context.Repaint();
                return true;
            }

            dragKind = DragKind.Marquee;
            marqueeAdditive = shift;
            // Points always live inside the active region's rect (they are clamped to it), so a
            // drag starting outside it cannot mean "select points" — it means "select regions".
            marqueeSelectsRegions = region == null || !region.Rect.Contains(texturePos);
            context.Repaint();
            return true;
        }

        public void OnPointerDrag(Vector2 viewPos)
        {
            dragCurrentView = viewPos;
            if (!passedThreshold && (viewPos - dragStartView).magnitude >= marqueeThreshold)
                passedThreshold = true;

            switch (dragKind)
            {
                case DragKind.Points:
                    MoveSelectedPoints(context.Transform.ViewToTexture(viewPos));
                    context.NotifyLiveEdit();
                    break;
                case DragKind.Marquee:
                case DragKind.NewPath:
                    context.Repaint();
                    break;
            }
        }

        public void OnPointerUp(Vector2 viewPos, PointerUpEvent evt)
        {
            switch (dragKind)
            {
                case DragKind.Points:
                    FinishPointDrag();
                    break;
                case DragKind.Marquee:
                    FinishMarquee();
                    break;
                case DragKind.NewPath:
                    FinishNewPath();
                    break;
            }
            dragKind = DragKind.None;
            draggedRegion = null;
            dragStartSnapshot = null;
        }

        public bool OnKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode == KeyCode.Escape)
            {
                selectedPoints.Clear();
                context.Repaint();
                return true;
            }

            if (evt.keyCode is not (KeyCode.Delete or KeyCode.Backspace))
                return false;

            var region = ActiveRegion;
            if (region == null || selectedPoints.Count == 0)
                return false;

            var shapes = region.GetShapes(Kind);
            var before = region.Clone();

            // Group by path so indices can be removed from the back, and so a path that drops
            // below three points goes entirely rather than leaving a degenerate polygon.
            var byPath = new Dictionary<int, List<int>>();
            foreach (var (path, point) in selectedPoints)
            {
                if (path < 0 || path >= shapes.Count)
                    continue;
                if (!byPath.TryGetValue(path, out var list))
                {
                    list = new List<int>();
                    byPath[path] = list;
                }
                list.Add(point);
            }
            if (byPath.Count == 0)
                return false;

            var pathIndices = new List<int>(byPath.Keys);
            pathIndices.Sort();
            int removedPoints = 0;
            for (int i = pathIndices.Count - 1; i >= 0; i--)
            {
                int pathIndex = pathIndices[i];
                var points = new List<Vector2>(shapes[pathIndex]);
                var toRemove = byPath[pathIndex];
                toRemove.Sort();
                removedPoints += toRemove.Count;

                if (points.Count - toRemove.Count < 3)
                {
                    shapes.RemoveAt(pathIndex);
                    continue;
                }
                for (int j = toRemove.Count - 1; j >= 0; j--)
                {
                    if (toRemove[j] >= 0 && toRemove[j] < points.Count)
                        points.RemoveAt(toRemove[j]);
                }
                shapes[pathIndex] = points.ToArray();
            }

            selectedPoints.Clear();
            string label = removedPoints == 1
                ? $"Delete shape point of '{region.Name}'"
                : $"Delete {removedPoints} shape points of '{region.Name}'";
            context.History.PushApplied(new ModifyRegionCommand(
                context.Document, region.Id, before, region.Clone(), label));
            context.Repaint();
            return true;
        }

        public void OnDeactivate()
        {
            dragKind = DragKind.None;
            draggedRegion = null;
            dragStartSnapshot = null;
            selectedPoints.Clear();
        }

        public ViewportCursor GetCursor(Vector2 viewPos)
        {
            if (dragKind == DragKind.Points)
                return ViewportCursor.Move;
            var region = ActiveRegion;
            if (region == null)
                return ViewportCursor.Default;
            return TryHitPoint(region, region.GetShapes(Kind), viewPos, out _, out _)
                ? ViewportCursor.Move
                : ViewportCursor.Default;
        }

        public void DrawOverlay(Painter2D painter)
        {
            var region = ActiveRegion;
            if (region == null)
                return;

            // Every selected region shows its shapes, so a multi-region selection is visible;
            // only the active one gets point handles, to keep editing unambiguous.
            painter.strokeColor = pathColor;
            painter.lineWidth = 1.5f;
            painter.BeginPath();
            int batched = 0;
            foreach (var selected in context.Document.Regions)
            {
                if (!context.Selection.IsSelected(selected.Id))
                    continue;
                foreach (var path in selected.GetShapes(Kind))
                {
                    if (path.Length < 2)
                        continue;
                    painter.MoveTo(ToView(selected, path[0]));
                    for (int i = 1; i < path.Length; i++)
                        painter.LineTo(ToView(selected, path[i]));
                    painter.ClosePath();

                    // Flush periodically: one unbounded path overruns Painter2D's stroke
                    // tessellation, which crashes the editor rather than failing gracefully.
                    batched += path.Length;
                    if (batched < maxBatchedPoints)
                        continue;
                    painter.Stroke();
                    painter.BeginPath();
                    batched = 0;
                }
            }
            painter.Stroke();

            var shapes = region.GetShapes(Kind);
            for (int pathIndex = 0; pathIndex < shapes.Count; pathIndex++)
            {
                var path = shapes[pathIndex];
                for (int pointIndex = 0; pointIndex < path.Length; pointIndex++)
                {
                    bool isSelected = selectedPoints.Contains((pathIndex, pointIndex));
                    DrawPoint(painter, ToView(region, path[pointIndex]),
                        isSelected ? selectedPointColor : pointColor);
                }
            }

            if (!passedThreshold)
                return;

            if (dragKind == DragKind.Marquee)
            {
                DrawRect(painter, MakeViewRect(dragStartView, dragCurrentView),
                    marqueeFillColor, marqueeStrokeColor);
            }
            else if (dragKind == DragKind.NewPath)
            {
                var textureRect = MakeRect(dragStartTexture, context.Transform.ViewToTexture(dragCurrentView));
                DrawRect(painter, context.Transform.TextureRectToView(textureRect),
                    newPathFillColor, pathColor);
            }
        }

        // --- Point dragging ---

        private void BeginPointDrag(SpriteRegion region)
        {
            dragKind = DragKind.Points;
            draggedRegion = region;
            dragStartSnapshot = region.Clone();
        }

        /// <summary>Moves every selected point by the same delta, from the drag-start snapshot.</summary>
        private void MoveSelectedPoints(Vector2 texturePos)
        {
            if (draggedRegion == null || dragStartSnapshot == null)
                return;

            var delta = texturePos - dragStartTexture;
            var shapes = draggedRegion.GetShapes(Kind);
            var original = dragStartSnapshot.GetShapes(Kind);

            foreach (var (pathIndex, pointIndex) in selectedPoints)
            {
                if (pathIndex < 0 || pathIndex >= shapes.Count || pathIndex >= original.Count)
                    continue;
                var path = shapes[pathIndex];
                var startPath = original[pathIndex];
                if (pointIndex < 0 || pointIndex >= path.Length || pointIndex >= startPath.Length)
                    continue;

                var texturePoint = draggedRegion.ShapePointToTexture(startPath[pointIndex]) + delta;
                path[pointIndex] = draggedRegion.TextureToShapePoint(
                    ClampToRegion(Snap(texturePoint), draggedRegion));
            }
        }

        private void FinishPointDrag()
        {
            // Only record a move when something actually moved: clicking to select a point must
            // not fill the history with no-op entries.
            if (draggedRegion == null || dragStartSnapshot == null
                || ShapesEqual(dragStartSnapshot.GetShapes(Kind), draggedRegion.GetShapes(Kind)))
                return;

            string label = selectedPoints.Count == 1
                ? $"Move shape point of '{draggedRegion.Name}'"
                : $"Move {selectedPoints.Count} shape points of '{draggedRegion.Name}'";
            context.History.PushApplied(new ModifyRegionCommand(
                context.Document, draggedRegion.Id, dragStartSnapshot, draggedRegion.Clone(), label));
        }

        // --- Marquee ---

        private void FinishMarquee()
        {
            if (!passedThreshold)
            {
                // A plain click on empty space clears the point selection.
                if (!marqueeAdditive)
                {
                    selectedPoints.Clear();
                    context.Repaint();
                }
                return;
            }

            if (marqueeSelectsRegions)
            {
                SelectRegionsInMarquee();
                return;
            }

            var region = ActiveRegion;
            if (region == null)
                return;

            var viewRect = MakeViewRect(dragStartView, dragCurrentView);
            if (!marqueeAdditive)
                selectedPoints.Clear();

            var shapes = region.GetShapes(Kind);
            for (int pathIndex = 0; pathIndex < shapes.Count; pathIndex++)
            {
                var path = shapes[pathIndex];
                for (int pointIndex = 0; pointIndex < path.Length; pointIndex++)
                {
                    if (viewRect.Contains(ToView(region, path[pointIndex])))
                        selectedPoints.Add((pathIndex, pointIndex));
                }
            }
            context.Repaint();
        }

        /// <summary>
        /// Region-level marquee, so several sprites can be selected without leaving the mode —
        /// which is what makes Generate work on a group.
        /// </summary>
        private void SelectRegionsInMarquee()
        {
            var textureRect = MakeRect(
                context.Transform.ViewToTexture(dragStartView),
                context.Transform.ViewToTexture(dragCurrentView));

            var ids = new List<UnityEditor.GUID>();
            foreach (var region in context.Document.Regions)
            {
                if (region.Rect.Overlaps(textureRect))
                    ids.Add(region.Id);
            }

            if (marqueeAdditive)
                context.Selection.AddRange(ids);
            else
                context.Selection.SetRange(ids);
            selectedPoints.Clear();
            context.Repaint();
        }

        // --- New path ---

        private void FinishNewPath()
        {
            var region = ActiveRegion;
            if (region == null || !passedThreshold)
                return;

            var rect = MakeRect(dragStartTexture, context.Transform.ViewToTexture(dragCurrentView));
            if (SnapToPixels)
                rect = Rect.MinMaxRect(
                    Mathf.Round(rect.xMin), Mathf.Round(rect.yMin),
                    Mathf.Round(rect.xMax), Mathf.Round(rect.yMax));
            // A new shape belongs to the sprite too, so it cannot spill outside the region.
            rect = Rect.MinMaxRect(
                Mathf.Max(rect.xMin, region.Rect.xMin), Mathf.Max(rect.yMin, region.Rect.yMin),
                Mathf.Min(rect.xMax, region.Rect.xMax), Mathf.Min(rect.yMax, region.Rect.yMax));
            if (rect.width < newPathMinSize || rect.height < newPathMinSize)
                return;

            var before = region.Clone();
            var shapes = region.GetShapes(Kind);
            shapes.Add(new[]
            {
                region.TextureToShapePoint(new Vector2(rect.xMin, rect.yMin)),
                region.TextureToShapePoint(new Vector2(rect.xMax, rect.yMin)),
                region.TextureToShapePoint(new Vector2(rect.xMax, rect.yMax)),
                region.TextureToShapePoint(new Vector2(rect.xMin, rect.yMax))
            });
            selectedPoints.Clear();
            context.History.PushApplied(new ModifyRegionCommand(
                context.Document, region.Id, before, region.Clone(), $"Add shape to '{region.Name}'"));
            context.Repaint();
        }

        // --- Helpers ---

        private Vector2 Snap(Vector2 texturePoint)
        {
            if (!SnapToPixels)
                return texturePoint;
            return new Vector2(Mathf.Round(texturePoint.x), Mathf.Round(texturePoint.y));
        }

        /// <summary>A shape belongs to its sprite, so its points never leave the region's rect.</summary>
        private static Vector2 ClampToRegion(Vector2 texturePoint, SpriteRegion region)
        {
            return new Vector2(
                Mathf.Clamp(texturePoint.x, region.Rect.xMin, region.Rect.xMax),
                Mathf.Clamp(texturePoint.y, region.Rect.yMin, region.Rect.yMax));
        }

        /// <summary>Keeps selection indices valid after a point is inserted into a path.</summary>
        private void ShiftSelectionAfterInsert(int pathIndex, int insertAt)
        {
            var updated = new List<(int path, int point)>();
            foreach (var entry in selectedPoints)
            {
                updated.Add(entry.path == pathIndex && entry.point >= insertAt
                    ? (entry.path, entry.point + 1)
                    : entry);
            }
            selectedPoints.Clear();
            foreach (var entry in updated)
                selectedPoints.Add(entry);
        }

        private Vector2 ToView(SpriteRegion region, Vector2 shapePoint)
        {
            return context.Transform.TextureToView(region.ShapePointToTexture(shapePoint));
        }

        private bool TryHitPoint(SpriteRegion region, List<Vector2[]> shapes, Vector2 viewPos,
            out int pathIndex, out int pointIndex)
        {
            float sqrRadius = pointHitRadius * pointHitRadius;
            for (int i = 0; i < shapes.Count; i++)
            {
                var path = shapes[i];
                for (int j = 0; j < path.Length; j++)
                {
                    if ((ToView(region, path[j]) - viewPos).sqrMagnitude > sqrRadius)
                        continue;
                    pathIndex = i;
                    pointIndex = j;
                    return true;
                }
            }
            pathIndex = pointIndex = -1;
            return false;
        }

        /// <summary>Finds the edge under the cursor and where a new point would be inserted.</summary>
        private bool TryHitEdge(SpriteRegion region, List<Vector2[]> shapes, Vector2 viewPos,
            out int pathIndex, out int insertAt)
        {
            for (int i = 0; i < shapes.Count; i++)
            {
                var path = shapes[i];
                for (int j = 0; j < path.Length; j++)
                {
                    var a = ToView(region, path[j]);
                    var b = ToView(region, path[(j + 1) % path.Length]);
                    if (DistanceToSegment(viewPos, a, b) > edgeHitRadius)
                        continue;
                    pathIndex = i;
                    insertAt = j + 1;
                    return true;
                }
            }
            pathIndex = insertAt = -1;
            return false;
        }

        private static float DistanceToSegment(Vector2 point, Vector2 a, Vector2 b)
        {
            var ab = b - a;
            float lengthSquared = ab.sqrMagnitude;
            if (lengthSquared < 1e-6f)
                return Vector2.Distance(point, a);
            float t = Mathf.Clamp01(Vector2.Dot(point - a, ab) / lengthSquared);
            return Vector2.Distance(point, a + ab * t);
        }

        private static Rect MakeRect(Vector2 a, Vector2 b)
        {
            return Rect.MinMaxRect(
                Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y),
                Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
        }

        private static Rect MakeViewRect(Vector2 a, Vector2 b)
        {
            return MakeRect(a, b);
        }

        private static void DrawRect(Painter2D painter, Rect rect, Color fill, Color stroke)
        {
            painter.fillColor = fill;
            painter.strokeColor = stroke;
            painter.lineWidth = 1f;
            painter.BeginPath();
            painter.MoveTo(new Vector2(rect.xMin, rect.yMin));
            painter.LineTo(new Vector2(rect.xMax, rect.yMin));
            painter.LineTo(new Vector2(rect.xMax, rect.yMax));
            painter.LineTo(new Vector2(rect.xMin, rect.yMax));
            painter.ClosePath();
            painter.Fill();
            painter.Stroke();
        }

        private static void DrawPoint(Painter2D painter, Vector2 center, Color fill)
        {
            var rect = new Rect(
                center.x - pointDrawSize * 0.5f, center.y - pointDrawSize * 0.5f,
                pointDrawSize, pointDrawSize);
            painter.fillColor = fill;
            painter.BeginPath();
            painter.MoveTo(new Vector2(rect.xMin, rect.yMin));
            painter.LineTo(new Vector2(rect.xMax, rect.yMin));
            painter.LineTo(new Vector2(rect.xMax, rect.yMax));
            painter.LineTo(new Vector2(rect.xMin, rect.yMax));
            painter.ClosePath();
            painter.Fill();
            painter.strokeColor = pointBorderColor;
            painter.lineWidth = 1f;
            painter.Stroke();
        }

        private static bool ShapesEqual(List<Vector2[]> a, List<Vector2[]> b)
        {
            if (a.Count != b.Count)
                return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].Length != b[i].Length)
                    return false;
                for (int j = 0; j < a[i].Length; j++)
                {
                    if (a[i][j] != b[i][j])
                        return false;
                }
            }
            return true;
        }
    }
}
