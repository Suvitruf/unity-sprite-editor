using System.Collections.Generic;
using BetterSpriteEditor.Model;
using BetterSpriteEditor.Operations;
using UnityEngine;
using UnityEngine.UIElements;

namespace BetterSpriteEditor.Tools
{
    /// <summary>
    /// Selection and editing:
    /// - click selects (smallest region under cursor); Ctrl toggles, Shift adds;
    /// - drag on empty space = marquee select (Shift adds); drag on selection moves ALL selected;
    /// - handles resize, blue mid-edge points edit the 9-slice border (active region only);
    /// - arrows nudge the whole selection (Shift = x10), Esc deselects.
    /// One history entry per completed drag/nudge/marquee-edit.
    ///
    /// Hit priority on the active region: border points > resize corners/edges > body.
    /// </summary>
    public class SelectTool : IViewportTool
    {
        private const float pivotHitRadius = 8f;
        private const float borderHitRadius = 6f;
        private const float borderLineGrabMinSeparation = 5f; // view px from the region edge
        private const float cornerHitRadius = 7f;
        private const float edgeHitRadius = 5f; // buffer: resizing wins over picking a neighbor
        private const float handleDrawSize = 7f;
        private const float marqueeThreshold = 3f; // view px before an empty-drag becomes a marquee

        private static readonly Color handleFillColor = Color.white;
        private static readonly Color handleBorderColor = new(0.15f, 0.15f, 0.15f);
        private static readonly Color borderHandleFillColor = new(0.2f, 0.9f, 0.35f);
        private static readonly Color marqueeFillColor = new(0.25f, 0.6f, 1f, 0.1f);
        private static readonly Color marqueeStrokeColor = new(0.25f, 0.6f, 1f, 0.9f);

        private enum DragKind
        {
            None,
            Move,
            Resize,
            Border,
            Pivot,
            Marquee
        }

        private readonly ToolContext context;

        private DragKind dragKind = DragKind.None;
        private RectHandle dragHandle = RectHandle.None;
        private Vector2 dragStartTexturePos;

        // Resize/border drag (single region).
        private SpriteRegion draggedRegion;
        private SpriteRegion dragStartSnapshot;
        private Rect dragStartRect;

        // Move drag (whole selection).
        private readonly List<(SpriteRegion region, SpriteRegion snapshot)> moveEntries = new();
        private Vector2 moveDeltaMin;
        private Vector2 moveDeltaMax;

        // Marquee.
        private Vector2 marqueeStartView;
        private Vector2 marqueeCurrentView;
        private bool marqueePassedThreshold;
        private bool marqueeAdditive;

        public SelectTool(ToolContext context)
        {
            this.context = context;
        }

        public bool OnPointerDown(Vector2 viewPos, PointerDownEvent evt)
        {
            var document = context.Document;
            var texturePos = context.Transform.ViewToTexture(viewPos);
            var activeRegion = document.FindRegion(context.Selection.ActiveId);

            // The pivot ring is small and drawn on top, so it wins over every other handle.
            var pivotRegion = HitTestPivot(viewPos);
            if (pivotRegion != null && document.Capabilities.Has(SpriteEditCapabilities.EditPivot))
            {
                if (pivotRegion != activeRegion)
                    context.Selection.Add(pivotRegion.Id);
                BeginSingleDrag(pivotRegion, DragKind.Pivot, RectHandle.None, texturePos);
                // Dragging only means something for a custom pivot. Seed the custom value from
                // the resolved preset first, so the pivot stays put instead of jumping to a
                // stale vector when the alignment flips.
                pivotRegion.Pivot = pivotRegion.GetNormalizedPivot();
                pivotRegion.Alignment = SpriteAlignment.Custom;
                context.NotifyLiveEdit();
                return true;
            }

            var borderHandle = HitTestBorderHandle(viewPos, activeRegion);
            if (borderHandle != RectHandle.None && document.Capabilities.Has(SpriteEditCapabilities.EditBorder))
            {
                BeginSingleDrag(activeRegion, DragKind.Border, borderHandle, texturePos);
                return true;
            }

            var resizeHandle = HitTestActiveHandle(viewPos, activeRegion);
            if (document.CanEditRect && resizeHandle != RectHandle.None && resizeHandle != RectHandle.Body)
            {
                BeginSingleDrag(activeRegion, DragKind.Resize, resizeHandle, texturePos);
                return true;
            }

            bool ctrl = evt.ctrlKey || evt.commandKey;
            bool shift = evt.shiftKey;
            var hitRegion = HitTestRegions(texturePos);

            if (hitRegion == null)
            {
                BeginMarquee(viewPos, additive: shift || ctrl);
                return true;
            }

            if (ctrl)
            {
                context.Selection.Toggle(hitRegion.Id);
                context.Repaint();
                return true;
            }
            if (shift)
            {
                context.Selection.Add(hitRegion.Id);
                context.Repaint();
                return true;
            }

            if (!context.Selection.IsSelected(hitRegion.Id))
                context.Selection.Set(hitRegion.Id);
            if (document.CanEditRect)
                BeginMoveDrag(texturePos);
            context.Repaint();
            return true;
        }

        public void OnPointerDrag(Vector2 viewPos)
        {
            var texturePos = context.Transform.ViewToTexture(viewPos);
            var delta = texturePos - dragStartTexturePos;
            var document = context.Document;

            switch (dragKind)
            {
                case DragKind.Move:
                {
                    var clamped = new Vector2(
                        Mathf.Clamp(Mathf.Round(delta.x), moveDeltaMin.x, moveDeltaMax.x),
                        Mathf.Clamp(Mathf.Round(delta.y), moveDeltaMin.y, moveDeltaMax.y));
                    foreach (var (region, snapshot) in moveEntries)
                    {
                        var rect = snapshot.Rect;
                        rect.position += clamped;
                        region.Rect = rect;
                    }
                    break;
                }
                case DragKind.Resize:
                    draggedRegion.Rect = RectHandles.ResizeTextureRect(
                        dragStartRect, dragHandle, delta, document.TextureWidth, document.TextureHeight);
                    break;
                case DragKind.Border:
                    draggedRegion.Border = DragBorder(dragStartSnapshot.Border, dragStartRect, dragHandle, delta);
                    break;
                case DragKind.Pivot:
                {
                    // Normalized, and deliberately unclamped: pivots outside the rect are legal.
                    var rect = draggedRegion.Rect;
                    draggedRegion.Pivot = new Vector2(
                        RoundNormalized((texturePos.x - rect.xMin) / rect.width),
                        RoundNormalized((texturePos.y - rect.yMin) / rect.height));
                    break;
                }
                case DragKind.Marquee:
                    marqueeCurrentView = viewPos;
                    if (!marqueePassedThreshold
                        && (marqueeCurrentView - marqueeStartView).magnitude >= marqueeThreshold)
                        marqueePassedThreshold = true;
                    break;
                default:
                    return;
            }
            context.NotifyLiveEdit();
        }

        public void OnPointerUp(Vector2 viewPos, PointerUpEvent evt)
        {
            switch (dragKind)
            {
                case DragKind.Move:
                    FinishMoveDrag();
                    break;
                case DragKind.Resize:
                case DragKind.Border:
                case DragKind.Pivot:
                    FinishSingleDrag();
                    break;
                case DragKind.Marquee:
                    FinishMarquee();
                    break;
            }
            EndDrag();
        }

        public bool OnKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode == KeyCode.Escape)
            {
                context.Selection.Clear();
                context.Repaint();
                return true;
            }

            if (!context.Document.CanEditRect)
                return false;

            var nudge = evt.keyCode switch
            {
                KeyCode.LeftArrow => new Vector2(-1f, 0f),
                KeyCode.RightArrow => new Vector2(1f, 0f),
                KeyCode.UpArrow => new Vector2(0f, 1f),
                KeyCode.DownArrow => new Vector2(0f, -1f),
                _ => Vector2.zero
            };
            if (nudge == Vector2.zero)
                return false;

            var regions = GetSelectedRegions();
            if (regions.Count == 0)
                return false;

            if (evt.shiftKey)
                nudge *= 10f;

            var (deltaMin, deltaMax) = GetMoveDeltaBounds(regions);
            nudge.x = Mathf.Clamp(nudge.x, deltaMin.x, deltaMax.x);
            nudge.y = Mathf.Clamp(nudge.y, deltaMin.y, deltaMax.y);
            if (nudge == Vector2.zero)
                return true;

            string label = regions.Count == 1 ? $"Nudge '{regions[0].Name}'" : $"Nudge {regions.Count} regions";
            var commands = new List<IEditorCommand>(regions.Count);
            foreach (var region in regions)
            {
                var before = region.Clone();
                var rect = region.Rect;
                rect.position += nudge;
                region.Rect = rect;
                commands.Add(new ModifyRegionCommand(context.Document, region.Id, before, region.Clone(), label));
            }
            context.History.PushApplied(commands.Count == 1
                ? commands[0]
                : new CompositeCommand(label, commands));
            return true;
        }

        public void OnDeactivate()
        {
            EndDrag();
        }

        public ViewportCursor GetCursor(Vector2 viewPos)
        {
            RectHandle handle;
            bool isBorder;
            if (dragKind is DragKind.Resize or DragKind.Border)
            {
                handle = dragHandle;
                isBorder = dragKind == DragKind.Border;
            }
            else if (dragKind is DragKind.Move or DragKind.Pivot)
            {
                return ViewportCursor.Move;
            }
            else
            {
                if (context.Document != null && HitTestPivot(viewPos) != null)
                    return ViewportCursor.Move;

                var activeRegion = context.Document?.FindRegion(context.Selection.ActiveId);
                handle = HitTestBorderHandle(viewPos, activeRegion);
                isBorder = handle != RectHandle.None;
                if (!isBorder)
                    handle = HitTestActiveHandle(viewPos, activeRegion);
            }

            if (isBorder)
            {
                return handle is RectHandle.Left or RectHandle.Right
                    ? ViewportCursor.ResizeHorizontal
                    : ViewportCursor.ResizeVertical;
            }
            return handle switch
            {
                RectHandle.Left or RectHandle.Right => ViewportCursor.ResizeHorizontal,
                RectHandle.Top or RectHandle.Bottom => ViewportCursor.ResizeVertical,
                RectHandle.TopLeft or RectHandle.BottomRight => ViewportCursor.ResizeUpLeft,
                RectHandle.TopRight or RectHandle.BottomLeft => ViewportCursor.ResizeUpRight,
                RectHandle.Body => ViewportCursor.Move,
                _ => ViewportCursor.Default
            };
        }

        public void DrawOverlay(Painter2D painter)
        {
            if (dragKind == DragKind.Marquee && marqueePassedThreshold)
            {
                var rect = Rect.MinMaxRect(
                    Mathf.Min(marqueeStartView.x, marqueeCurrentView.x),
                    Mathf.Min(marqueeStartView.y, marqueeCurrentView.y),
                    Mathf.Max(marqueeStartView.x, marqueeCurrentView.x),
                    Mathf.Max(marqueeStartView.y, marqueeCurrentView.y));
                painter.fillColor = marqueeFillColor;
                painter.strokeColor = marqueeStrokeColor;
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

            var activeRegion = context.Document?.FindRegion(context.Selection.ActiveId);
            if (activeRegion == null)
                return;

            // Border lines for all regions are drawn by the viewport; the tool adds only handles.
            var viewRect = context.Transform.TextureRectToView(activeRegion.Rect);
            foreach (var handle in RectHandles.AllHandles)
                DrawHandleSquare(painter,
                    RectHandles.GetHandleViewPoint(viewRect, handle), handleFillColor);

            foreach (var side in borderSides)
                DrawHandleSquare(painter,
                    context.Transform.TextureToView(GetBorderPointTexture(activeRegion, side)),
                    borderHandleFillColor);
        }

        // --- Move (whole selection) ---

        private void BeginMoveDrag(Vector2 texturePos)
        {
            moveEntries.Clear();
            foreach (var region in GetSelectedRegions())
                moveEntries.Add((region, region.Clone()));
            if (moveEntries.Count == 0)
                return;

            var regions = new List<SpriteRegion>(moveEntries.Count);
            foreach (var (region, _) in moveEntries)
                regions.Add(region);
            (moveDeltaMin, moveDeltaMax) = GetMoveDeltaBounds(regions);

            dragKind = DragKind.Move;
            dragHandle = RectHandle.Body;
            dragStartTexturePos = texturePos;
        }

        private void FinishMoveDrag()
        {
            string label = moveEntries.Count == 1
                ? $"Move '{moveEntries[0].region.Name}'"
                : $"Move {moveEntries.Count} regions";

            var commands = new List<IEditorCommand>();
            foreach (var (region, snapshot) in moveEntries)
            {
                if (region.Rect != snapshot.Rect)
                    commands.Add(new ModifyRegionCommand(
                        context.Document, region.Id, snapshot, region.Clone(), label));
            }
            if (commands.Count == 0)
                return;

            context.History.PushApplied(commands.Count == 1
                ? commands[0]
                : new CompositeCommand(label, commands));
        }

        /// <summary>Delta range keeping every region inside the texture — relative offsets stay intact.</summary>
        private (Vector2 min, Vector2 max) GetMoveDeltaBounds(List<SpriteRegion> regions)
        {
            var document = context.Document;
            var min = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            var max = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            foreach (var region in regions)
            {
                var rect = region.Rect;
                min.x = Mathf.Max(min.x, -rect.x);
                min.y = Mathf.Max(min.y, -rect.y);
                max.x = Mathf.Min(max.x, document.TextureWidth - rect.width - rect.x);
                max.y = Mathf.Min(max.y, document.TextureHeight - rect.height - rect.y);
            }
            return (min, max);
        }

        // --- Marquee ---

        private void BeginMarquee(Vector2 viewPos, bool additive)
        {
            dragKind = DragKind.Marquee;
            marqueeStartView = viewPos;
            marqueeCurrentView = viewPos;
            marqueePassedThreshold = false;
            marqueeAdditive = additive;
        }

        private void FinishMarquee()
        {
            if (!marqueePassedThreshold)
            {
                if (!marqueeAdditive)
                    context.Selection.Clear();
                context.Repaint();
                return;
            }

            var textureA = context.Transform.ViewToTexture(marqueeStartView);
            var textureB = context.Transform.ViewToTexture(marqueeCurrentView);
            var marqueeRect = Rect.MinMaxRect(
                Mathf.Min(textureA.x, textureB.x), Mathf.Min(textureA.y, textureB.y),
                Mathf.Max(textureA.x, textureB.x), Mathf.Max(textureA.y, textureB.y));

            var hitIds = new List<UnityEditor.GUID>();
            foreach (var region in context.Document.Regions)
            {
                if (region.Rect.Overlaps(marqueeRect))
                    hitIds.Add(region.Id);
            }

            if (marqueeAdditive)
                context.Selection.AddRange(hitIds);
            else
                context.Selection.SetRange(hitIds);
            context.Repaint();
        }

        // --- Border editing ---

        private static readonly RectHandle[] borderSides =
            { RectHandle.Left, RectHandle.Right, RectHandle.Top, RectHandle.Bottom };

        /// <summary>Mid-edge point of one border line, in texture space. Border components:
        /// x=left, y=bottom, z=right, w=top — insets from the region's edges.</summary>
        private static Vector2 GetBorderPointTexture(SpriteRegion region, RectHandle side)
        {
            var rect = region.Rect;
            var border = region.Border;
            return side switch
            {
                RectHandle.Left => new Vector2(rect.xMin + border.x, rect.center.y),
                RectHandle.Right => new Vector2(rect.xMax - border.z, rect.center.y),
                RectHandle.Top => new Vector2(rect.center.x, rect.yMax - border.w),
                _ => new Vector2(rect.center.x, rect.yMin + border.y)
            };
        }

        private RectHandle HitTestBorderHandle(Vector2 viewPos, SpriteRegion activeRegion)
        {
            if (activeRegion == null)
                return RectHandle.None;

            float sqrRadius = borderHitRadius * borderHitRadius;
            var viewRect = context.Transform.TextureRectToView(activeRegion.Rect);
            float zoom = context.Transform.Zoom;
            var border = activeRegion.Border;

            foreach (var side in borderSides)
            {
                var point = context.Transform.TextureToView(GetBorderPointTexture(activeRegion, side));
                if ((point - viewPos).sqrMagnitude <= sqrRadius)
                    return side;

                // Once the border line is clearly separated from the region edge on screen,
                // the whole line is grabbable — near the edge only the mid point works, so
                // region-edge resizing is never shadowed.
                float offset = side switch
                {
                    RectHandle.Left => border.x,
                    RectHandle.Right => border.z,
                    RectHandle.Top => border.w,
                    _ => border.y
                };
                if (offset * zoom <= borderLineGrabMinSeparation)
                    continue;

                bool hit = side is RectHandle.Left or RectHandle.Right
                    ? Mathf.Abs(viewPos.x - point.x) <= edgeHitRadius
                      && viewPos.y >= viewRect.yMin - edgeHitRadius
                      && viewPos.y <= viewRect.yMax + edgeHitRadius
                    : Mathf.Abs(viewPos.y - point.y) <= edgeHitRadius
                      && viewPos.x >= viewRect.xMin - edgeHitRadius
                      && viewPos.x <= viewRect.xMax + edgeHitRadius;
                if (hit)
                    return side;
            }
            return RectHandle.None;
        }

        /// <summary>
        /// Border offsets only grow inward: each side clamps to [0, size - opposite side],
        /// so offsets stay positive and the lines never cross.
        /// </summary>
        private static Vector4 DragBorder(Vector4 startBorder, Rect rect, RectHandle side, Vector2 delta)
        {
            var border = startBorder;
            switch (side)
            {
                case RectHandle.Left:
                    border.x = Mathf.Clamp(Mathf.Round(startBorder.x + delta.x), 0f, rect.width - startBorder.z);
                    break;
                case RectHandle.Right:
                    border.z = Mathf.Clamp(Mathf.Round(startBorder.z - delta.x), 0f, rect.width - startBorder.x);
                    break;
                case RectHandle.Top:
                    border.w = Mathf.Clamp(Mathf.Round(startBorder.w - delta.y), 0f, rect.height - startBorder.y);
                    break;
                case RectHandle.Bottom:
                    border.y = Mathf.Clamp(Mathf.Round(startBorder.y + delta.y), 0f, rect.height - startBorder.w);
                    break;
            }
            return border;
        }

        private static void DrawHandleSquare(Painter2D painter, Vector2 center, Color fill)
        {
            var rect = new Rect(
                center.x - handleDrawSize * 0.5f, center.y - handleDrawSize * 0.5f,
                handleDrawSize, handleDrawSize);
            painter.fillColor = fill;
            painter.BeginPath();
            painter.MoveTo(new Vector2(rect.xMin, rect.yMin));
            painter.LineTo(new Vector2(rect.xMax, rect.yMin));
            painter.LineTo(new Vector2(rect.xMax, rect.yMax));
            painter.LineTo(new Vector2(rect.xMin, rect.yMax));
            painter.ClosePath();
            painter.Fill();
            painter.strokeColor = handleBorderColor;
            painter.lineWidth = 1f;
            painter.Stroke();
        }

        // --- Shared drag/hit helpers ---

        private void BeginSingleDrag(SpriteRegion region, DragKind kind, RectHandle handle, Vector2 texturePos)
        {
            draggedRegion = region;
            dragKind = kind;
            dragHandle = handle;
            dragStartSnapshot = region.Clone();
            dragStartRect = region.Rect;
            dragStartTexturePos = texturePos;
        }

        private void FinishSingleDrag()
        {
            if (draggedRegion == null)
                return;
            bool changed = draggedRegion.Rect != dragStartSnapshot.Rect
                           || draggedRegion.Border != dragStartSnapshot.Border
                           || draggedRegion.Pivot != dragStartSnapshot.Pivot
                           || draggedRegion.Alignment != dragStartSnapshot.Alignment;
            if (!changed)
                return;

            string label = dragKind switch
            {
                DragKind.Border => $"Edit border of '{draggedRegion.Name}'",
                DragKind.Pivot => $"Set pivot of '{draggedRegion.Name}'",
                _ => $"Resize '{draggedRegion.Name}'"
            };
            context.History.PushApplied(new ModifyRegionCommand(
                context.Document, draggedRegion.Id, dragStartSnapshot, draggedRegion.Clone(), label));
        }

        private static float RoundNormalized(float value)
        {
            return Mathf.Round(value * 10000f) / 10000f;
        }

        /// <summary>Pivot ring of any selected region under the cursor.</summary>
        private SpriteRegion HitTestPivot(Vector2 viewPos)
        {
            float sqrRadius = pivotHitRadius * pivotHitRadius;
            foreach (var region in context.Document.Regions)
            {
                if (!context.Selection.IsSelected(region.Id))
                    continue;
                var point = context.Transform.TextureToView(region.GetPivotTexturePosition());
                if ((point - viewPos).sqrMagnitude <= sqrRadius)
                    return region;
            }
            return null;
        }

        private void EndDrag()
        {
            draggedRegion = null;
            dragStartSnapshot = null;
            moveEntries.Clear();
            dragKind = DragKind.None;
            dragHandle = RectHandle.None;
        }

        private List<SpriteRegion> GetSelectedRegions()
        {
            var regions = new List<SpriteRegion>();
            foreach (var region in context.Document.Regions)
            {
                if (context.Selection.IsSelected(region.Id))
                    regions.Add(region);
            }
            return regions;
        }

        private RectHandle HitTestActiveHandle(Vector2 viewPos, SpriteRegion activeRegion)
        {
            if (activeRegion == null)
                return RectHandle.None;
            var viewRect = context.Transform.TextureRectToView(activeRegion.Rect);
            return RectHandles.HitTest(viewRect, viewPos, cornerHitRadius, edgeHitRadius);
        }

        private SpriteRegion HitTestRegions(Vector2 texturePos)
        {
            return context.Document.FindRegionAt(texturePos);
        }
    }
}
