using System;
using BetterSpriteEditor.Model;
using BetterSpriteEditor.Operations;
using BetterSpriteEditor.UI.Viewport;
using UnityEngine;
using UnityEngine.UIElements;

namespace BetterSpriteEditor.Tools
{
    /// <summary>Everything a viewport tool needs to do its work.</summary>
    public class ToolContext
    {
        public SpriteDocument Document;
        public SelectionModel Selection;
        public HistoryStack History;
        public ViewportTransform Transform;
        public VisualElement RepaintTarget;
        internal bool KeepPivotPosition;

        /// <summary>Raised while a drag mutates regions, so the inspector can follow live.</summary>
        public event Action LiveEdited;

        public void Repaint()
        {
            RepaintTarget?.MarkDirtyRepaint();
        }

        /// <summary>
        /// Repaint plus a live-value notification. Used during drags, where the document is
        /// mutated directly and no history entry exists yet to trigger a refresh.
        /// </summary>
        public void NotifyLiveEdit()
        {
            Repaint();
            LiveEdited?.Invoke();
        }
    }

    /// <summary>Cursor the viewport should show; mapped to editor USS cursors by the viewport.</summary>
    public enum ViewportCursor
    {
        Default,
        ResizeHorizontal,
        ResizeVertical,
        ResizeUpLeft,
        ResizeUpRight,
        Move
    }

    /// <summary>
    /// A viewport editing mode (Select, Create, …). The viewport routes left-button pointer
    /// events here in view-space coordinates; tools convert via context.Transform.
    /// </summary>
    public interface IViewportTool
    {
        /// <summary>Cursor for the current pointer position (and during drags).</summary>
        ViewportCursor GetCursor(Vector2 viewPos);

        /// <summary>Return true to consume the press — the viewport then captures the pointer
        /// and routes the drag to this tool.</summary>
        bool OnPointerDown(Vector2 viewPos, PointerDownEvent evt);

        void OnPointerDrag(Vector2 viewPos);

        /// <summary>evt is null when the drag was cancelled by a pointer-capture loss.</summary>
        void OnPointerUp(Vector2 viewPos, PointerUpEvent evt);

        /// <summary>Return true when the key was consumed.</summary>
        bool OnKeyDown(KeyDownEvent evt);

        void OnDeactivate();

        /// <summary>Tool-specific overlay, drawn after the region outlines.</summary>
        void DrawOverlay(Painter2D painter);
    }
}
