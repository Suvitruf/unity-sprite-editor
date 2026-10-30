using System;
using System.Collections.Generic;
using BetterSpriteEditor.Input;
using BetterSpriteEditor.Model;
using BetterSpriteEditor.Tools;
using UnityEngine;
using UnityEngine.UIElements;

namespace BetterSpriteEditor.UI.Viewport
{
    /// <summary>
    /// Texture viewport: renders the document's texture with zoom/pan, pixel grid, region
    /// outlines and selection, and routes left-button input to the active tool.
    /// Gestures (hardcoded until the ShortcutRegistry owns them): wheel = zoom to cursor,
    /// RMB/MMB-drag or Space+LMB-drag = pan.
    /// </summary>
    public partial class SpriteViewportElement : VisualElement
    {
        private const float pixelGridMinZoom = 8f;

        /// <summary>Points per Painter2D stroke call; see the flushes in the draw passes.</summary>
        private const int maxBatchedPoints = 400;
        private static readonly Color backgroundColor = new(0.16f, 0.16f, 0.16f);
        private static readonly Color textureBorderColor = new(0.6f, 0.6f, 0.6f, 0.8f);
        private static readonly Color pixelGridColor = new(0.5f, 0.5f, 0.5f, 0.15f);
        private static readonly Color regionOutlineColor = new(0.75f, 0.75f, 0.75f, 0.85f);
        private static readonly Color selectedOutlineColor = new(0.25f, 0.6f, 1f, 1f);
        private static readonly Color borderLineColor = new(0.2f, 0.9f, 0.35f, 0.7f);
        private static readonly Color previewRectColor = new(1f, 0.6f, 0.1f, 0.95f);
        private static readonly Color unusedOutlineColor = new(1f, 0.6f, 0.15f, 1f);
        private static readonly Color pivotColor = new(0.25f, 0.6f, 1f, 1f);
        private const float pivotRadius = 6f;
        private const float pivotLineWidth = 3f;

        private static readonly ShortcutActionId[] panGestureActions =
        {
            ShortcutActionId.PanViewMouse,
            ShortcutActionId.PanViewMouseAlternate,
            ShortcutActionId.PanViewModifierDrag
        };

        private static Material alphaViewMaterial;
        private static Material checkerCompositeMaterial;
        private static StyleSheet cursorStyleSheet;

        private static readonly System.Collections.Generic.Dictionary<ViewportCursor, string> cursorClasses = new()
        {
            { ViewportCursor.ResizeHorizontal, "bse-cursor-resize-horizontal" },
            { ViewportCursor.ResizeVertical, "bse-cursor-resize-vertical" },
            { ViewportCursor.ResizeUpLeft, "bse-cursor-resize-up-left" },
            { ViewportCursor.ResizeUpRight, "bse-cursor-resize-up-right" },
            { ViewportCursor.Move, "bse-cursor-move" }
        };

        private readonly ViewportTransform viewTransform = new();
        private ViewportCursor currentCursor = ViewportCursor.Default;

        private SpriteDocument document;
        private SelectionModel selection;
        private IViewportTool activeTool;
        private RenderTexture previewTexture;
        private System.Collections.Generic.List<Rect> previewRects;
        private System.Collections.Generic.List<Vector2[]> previewShapes;
        private System.Collections.Generic.List<(Rect textureRect, Vector2 anchorTexture)> onionFrames;
        private HashSet<UnityEditor.GUID> unusedRegionIds;
        private bool showAlpha;
        private bool needsInitialFit = true;

        private bool isPanning;
        private bool isToolDragging;
        private bool isOnionDragging;
        private int activePointerId = -1;
        private Vector2 lastPanPosition;
        private readonly HashSet<KeyCode> heldKeys = new();

        /// <summary>Raised whenever zoom or pan changes — used for the zoom label.</summary>
        public event Action ViewChanged;

        /// <summary>
        /// Raised while the candidate pivot is dragged inside the onion-skin box, with the
        /// pointer normalized to the stacked frames' bounds (0..1, y up like texture space).
        /// </summary>
        public event Action<Vector2> OnionAnchorDragged;

        /// <summary>
        /// Called when the drag-out gesture fires, with the texture-space position pressed.
        /// Returns true when an external drag was started, so the viewport leaves the gesture
        /// alone — Unity's DragAndDrop cannot run while we hold pointer capture.
        /// </summary>
        public Func<Vector2, bool> DragOutHandler;

        public float ZoomLevel => viewTransform.Zoom;
        public ViewportTransform Transform => viewTransform;

        public IViewportTool ActiveTool
        {
            get => activeTool;
            set
            {
                activeTool?.OnDeactivate();
                activeTool = value;
                ApplyCursor(ViewportCursor.Default);
                MarkDirtyRepaint();
            }
        }

        public SpriteViewportElement()
        {
            // Focusable so key events (Space, arrows) reach us; focused on pointer enter because
            // pan/nudge must work without a prior click — but never stolen from text editing.
            focusable = true;
            style.flexGrow = 1;
            style.overflow = Overflow.Hidden;
            style.backgroundColor = backgroundColor;

            var styles = GetCursorStyleSheet();
            if (styles != null)
                styleSheets.Add(styles);

            generateVisualContent += OnGenerateVisualContent;

            RegisterCallback<WheelEvent>(OnWheel);
            RegisterCallback<PointerDownEvent>(OnPointerDown);
            RegisterCallback<PointerMoveEvent>(OnPointerMove);
            RegisterCallback<PointerMoveEvent>(OnPointerMoveForCursor);
            RegisterCallback<PointerUpEvent>(OnPointerUp);
            RegisterCallback<PointerCaptureOutEvent>(OnPointerCaptureOut);
            RegisterCallback<PointerEnterEvent>(OnPointerEnter);
            RegisterCallback<KeyDownEvent>(OnKeyDown);
            RegisterCallback<KeyUpEvent>(OnKeyUp);
            RegisterCallback<FocusOutEvent>(_ => heldKeys.Clear());
            RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
            RegisterCallback<AttachToPanelEvent>(_ => RebuildPreviewTexture());
            RegisterCallback<DetachFromPanelEvent>(_ => ReleasePreviewTexture());
        }

        public void SetSession(SpriteDocument newDocument, SelectionModel newSelection)
        {
            document = newDocument;
            selection = newSelection;
            previewRects = null;
            propertyPreview = null;
            viewTransform.SetTextureSize(document?.TextureWidth ?? 0, document?.TextureHeight ?? 0);
            RebuildPreviewTexture();

            if (document != null && contentRect.width > 0f)
            {
                viewTransform.Fit(contentRect.size);
                needsInitialFit = false;
                NotifyViewChanged();
            }
            else
            {
                needsInitialFit = true;
            }
            MarkDirtyRepaint();
        }

        /// <summary>Regions the usage search found no references for; drawn with an orange outline.</summary>
        public void SetUnusedRegions(HashSet<UnityEditor.GUID> ids)
        {
            unusedRegionIds = ids;
            MarkDirtyRepaint();
        }

        /// <summary>
        /// Frames to stack pivot-aligned in the onion-skin box, plus candidate-pivot crosses
        /// drawn on the frames themselves; null clears. Used by the pivot alignment dialog.
        /// </summary>
        public void SetOnionSkin(System.Collections.Generic.List<(Rect textureRect, Vector2 anchorTexture)> frames)
        {
            onionFrames = frames;
            MarkDirtyRepaint();
        }

        /// <summary>Ghost polygons for shape generation preview (texture space); null clears.</summary>
        public void SetPreviewShapes(System.Collections.Generic.List<Vector2[]> shapes)
        {
            previewShapes = shapes;
            MarkDirtyRepaint();
        }

        /// <summary>Ghost rects for slice preview (texture space); null clears.</summary>
        public void SetPreviewRects(System.Collections.Generic.List<Rect> rects)
        {
            previewRects = rects;
            MarkDirtyRepaint();
        }

        public void SetAlphaView(bool value)
        {
            if (showAlpha == value)
                return;
            showAlpha = value;
            RebuildPreviewTexture();
            MarkDirtyRepaint();
        }

        public void FitToWindow()
        {
            viewTransform.Fit(contentRect.size);
            NotifyViewChanged();
        }

        public void SetZoomOneToOne()
        {
            viewTransform.ZoomAround(contentRect.center, 1f);
            NotifyViewChanged();
        }

        /// <summary>Zooms to one region (double-click in the region list).</summary>
        public void FrameRect(Rect textureRect)
        {
            viewTransform.Frame(textureRect, contentRect.size, 40f);
            NotifyViewChanged();
        }

        private void OnGeometryChanged(GeometryChangedEvent evt)
        {
            if (needsInitialFit && document != null && contentRect.width > 0f)
            {
                viewTransform.Fit(contentRect.size);
                needsInitialFit = false;
                NotifyViewChanged();
            }
        }

        // --- Input ---

        private void OnWheel(WheelEvent evt)
        {
            float factor = Mathf.Pow(1.05f, -evt.delta.y);
            viewTransform.ZoomAround(evt.localMousePosition, viewTransform.Zoom * factor);
            NotifyViewChanged();
            evt.StopPropagation();
        }

        private void OnPointerEnter(PointerEnterEvent evt)
        {
            // Don't steal focus while the user is typing in an inspector field. When a
            // TextField is edited the focused element is the FIELD, not its inner
            // TextElement, so both shapes must be recognised — checking only TextElement
            // let hovering the viewport yank focus mid-typing.
            if (IsTextInputFocused())
                return;
            Focus();
        }

        private bool IsTextInputFocused()
        {
            var focused = panel?.focusController?.focusedElement as VisualElement;
            if (focused == null)
                return false;
            if (focused is TextElement)
                return true;
            // Text-based fields (TextField, IntegerField, Vector2Field parts, …) all carry
            // the base text-field input element; its presence marks an editable field.
            return focused.Q(className: TextInputBaseField<string>.inputUssClassName) != null;
        }

        private void OnPointerDown(PointerDownEvent evt)
        {
            bool actionKey = evt.ctrlKey || evt.commandKey;

            // The onion-skin box is overlay UI: a plain left press inside it places the
            // candidate pivot and starts dragging it, and must never fall through to the
            // tools (or the drag-out gesture) underneath. Pan gestures still win.
            if (evt.button == (int)MouseButton.LeftMouse && !IsPanGesture(evt)
                && TryGetOnionSkinLayout(out var onionLayout)
                && onionLayout.Box.Contains(evt.localPosition))
            {
                isOnionDragging = true;
                activePointerId = evt.pointerId;
                this.CapturePointer(evt.pointerId);
                ReportOnionAnchor(onionLayout, evt.localPosition);
                evt.StopPropagation();
                return;
            }

            // Decided here, before any pointer capture: a captured pointer never reaches
            // Unity's drag manager, so this cannot be resolved later in the gesture.
            if (document != null && DragOutHandler != null
                && ShortcutRegistry.MatchesMouse(ShortcutActionId.DragRegionOut, evt.button,
                    actionKey, evt.shiftKey, evt.altKey, heldKeys.Contains)
                && DragOutHandler(viewTransform.ViewToTexture(evt.localPosition)))
            {
                evt.StopPropagation();
                return;
            }

            // Pan wins over tools: the modifier gesture must pan even inside a region.
            if (IsPanGesture(evt))
            {
                isPanning = true;
                activePointerId = evt.pointerId;
                lastPanPosition = evt.position;
                this.CapturePointer(evt.pointerId);
                evt.StopPropagation();
                return;
            }

            if (evt.button == (int)MouseButton.LeftMouse && document != null && activeTool != null
                && activeTool.OnPointerDown(evt.localPosition, evt))
            {
                isToolDragging = true;
                activePointerId = evt.pointerId;
                this.CapturePointer(evt.pointerId);
                evt.StopPropagation();
            }
        }

        private void OnPointerMove(PointerMoveEvent evt)
        {
            if (evt.pointerId != activePointerId)
                return;

            if (isPanning)
            {
                viewTransform.Pan((Vector2)evt.position - lastPanPosition);
                lastPanPosition = evt.position;
                NotifyViewChanged();
                evt.StopPropagation();
            }
            else if (isOnionDragging)
            {
                if (TryGetOnionSkinLayout(out var layout))
                    ReportOnionAnchor(layout, evt.localPosition);
                evt.StopPropagation();
            }
            else if (isToolDragging)
            {
                activeTool?.OnPointerDrag(evt.localPosition);
                evt.StopPropagation();
            }
        }

        // Hover (not captured) moves don't pass the activePointerId check above — handled here.
        private void OnPointerMoveForCursor(PointerMoveEvent evt)
        {
            if (isPanning || isOnionDragging || document == null || activeTool == null)
                return;
            ApplyCursor(activeTool.GetCursor(evt.localPosition));
        }

        private void ApplyCursor(ViewportCursor cursor)
        {
            if (cursor == currentCursor)
                return;
            if (cursorClasses.TryGetValue(currentCursor, out var oldClass))
                RemoveFromClassList(oldClass);
            if (cursorClasses.TryGetValue(cursor, out var newClass))
                AddToClassList(newClass);
            currentCursor = cursor;
        }

        private void OnPointerUp(PointerUpEvent evt)
        {
            if (evt.pointerId != activePointerId)
                return;

            if (isToolDragging)
                activeTool?.OnPointerUp(evt.localPosition, evt);

            if (this.HasPointerCapture(evt.pointerId))
                this.ReleasePointer(evt.pointerId);
            StopActivePointer();
            evt.StopPropagation();
        }

        private void OnPointerCaptureOut(PointerCaptureOutEvent evt)
        {
            // Capture lost mid-drag (window switch, etc.) — let the tool finish cleanly.
            if (isToolDragging)
                activeTool?.OnPointerUp(lastPanPosition, null);
            StopActivePointer();
        }

        private void StopActivePointer()
        {
            isPanning = false;
            isToolDragging = false;
            isOnionDragging = false;
            activePointerId = -1;
        }

        private bool IsPanGesture(PointerDownEvent evt)
        {
            bool action = evt.ctrlKey || evt.commandKey;
            foreach (var id in panGestureActions)
            {
                if (ShortcutRegistry.MatchesMouse(id, evt.button, action, evt.shiftKey, evt.altKey,
                        heldKeys.Contains))
                    return true;
            }
            return false;
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            // Tracked for gestures that require a held key (Space + drag by default).
            heldKeys.Add(evt.keyCode);
            if (ShortcutRegistry.IsGestureHeldKey(evt.keyCode))
            {
                evt.StopPropagation();
                return;
            }

            if (document != null && activeTool != null && activeTool.OnKeyDown(evt))
                evt.StopPropagation();
        }

        private void OnKeyUp(KeyUpEvent evt)
        {
            heldKeys.Remove(evt.keyCode);
        }

        private void NotifyViewChanged()
        {
            ViewChanged?.Invoke();
            MarkDirtyRepaint();
        }

        // --- Preview texture ---

        private void RebuildPreviewTexture()
        {
            ReleasePreviewTexture();
            if (document?.Texture == null)
                return;

            var sourceTexture = document.Texture;
            previewTexture = new RenderTexture(sourceTexture.width, sourceTexture.height, 0)
            {
                filterMode = FilterMode.Point,
                hideFlags = HideFlags.HideAndDontSave
            };

            var material = showAlpha ? GetAlphaViewMaterial() : GetCheckerCompositeMaterial();
            if (material != null)
                Graphics.Blit(sourceTexture, previewTexture, material);
            else
                Graphics.Blit(sourceTexture, previewTexture);
        }

        private void ReleasePreviewTexture()
        {
            if (previewTexture == null)
                return;
            previewTexture.Release();
            UnityEngine.Object.DestroyImmediate(previewTexture);
            previewTexture = null;
        }

        private static StyleSheet GetCursorStyleSheet()
        {
            if (cursorStyleSheet != null)
                return cursorStyleSheet;

            // Path-independent lookup: works from Assets during development and from Packages later.
            var guids = UnityEditor.AssetDatabase.FindAssets("SpriteViewportStyles t:StyleSheet");
            if (guids.Length == 0)
            {
                Debug.LogWarning("[BetterSpriteEditor] SpriteViewportStyles.uss not found; resize cursors disabled.");
                return null;
            }
            cursorStyleSheet = UnityEditor.AssetDatabase.LoadAssetAtPath<StyleSheet>(
                UnityEditor.AssetDatabase.GUIDToAssetPath(guids[0]));
            return cursorStyleSheet;
        }

        private static Material GetAlphaViewMaterial()
        {
            return GetBlitMaterial(ref alphaViewMaterial, "Hidden/BetterSpriteEditor/AlphaView");
        }

        private static Material GetCheckerCompositeMaterial()
        {
            return GetBlitMaterial(ref checkerCompositeMaterial, "Hidden/BetterSpriteEditor/CheckerComposite");
        }

        private static Material GetBlitMaterial(ref Material cache, string shaderName)
        {
            if (cache != null)
                return cache;

            var shader = Shader.Find(shaderName);
            if (shader == null)
            {
                Debug.LogWarning($"[BetterSpriteEditor] Shader '{shaderName}' not found; using plain blit.");
                return null;
            }
            cache = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            return cache;
        }

        // --- Drawing ---

        private void OnGenerateVisualContent(MeshGenerationContext context)
        {
            if (document == null || previewTexture == null)
                return;

            var textureViewRect = viewTransform.TextureBoundsInView();
            DrawTextureQuad(context, textureViewRect, previewTexture);

            var painter = context.painter2D;
            DrawRectOutline(painter, textureViewRect, textureBorderColor);
            if (viewTransform.Zoom >= pixelGridMinZoom)
                DrawPixelGrid(painter, textureViewRect);
            DrawRegionOutlines(painter);
            DrawPreviewRects(painter);
            DrawPreviewShapes(painter);
            activeTool?.DrawOverlay(painter);
            DrawOnionSkin(context, painter);
            DrawPropertyPreview(painter);
        }

        private static void DrawTextureQuad(MeshGenerationContext context, Rect rect, Texture texture)
        {
            var mesh = context.Allocate(4, 6, texture);

            // Clockwise from top-left; v=1 is the texture's top.
            // UVs are remapped to the internal atlas by the renderer automatically.
            mesh.SetNextVertex(MakeVertex(rect.xMin, rect.yMin, 0f, 1f));
            mesh.SetNextVertex(MakeVertex(rect.xMax, rect.yMin, 1f, 1f));
            mesh.SetNextVertex(MakeVertex(rect.xMax, rect.yMax, 1f, 0f));
            mesh.SetNextVertex(MakeVertex(rect.xMin, rect.yMax, 0f, 0f));
            mesh.SetNextIndex(0);
            mesh.SetNextIndex(1);
            mesh.SetNextIndex(2);
            mesh.SetNextIndex(0);
            mesh.SetNextIndex(2);
            mesh.SetNextIndex(3);
        }

        private static Vertex MakeVertex(float x, float y, float u, float v)
        {
            return new Vertex
            {
                position = new Vector3(x, y, 0f),
                tint = Color.white,
                uv = new Vector2(u, v)
            };
        }

        private static void DrawRectOutline(Painter2D painter, Rect rect, Color color)
        {
            painter.strokeColor = color;
            painter.lineWidth = 1f;
            painter.BeginPath();
            AddRectPath(painter, rect);
            painter.Stroke();
        }

        private static void AddRectPath(Painter2D painter, Rect rect)
        {
            painter.MoveTo(new Vector2(rect.xMin, rect.yMin));
            painter.LineTo(new Vector2(rect.xMax, rect.yMin));
            painter.LineTo(new Vector2(rect.xMax, rect.yMax));
            painter.LineTo(new Vector2(rect.xMin, rect.yMax));
            painter.ClosePath();
        }

        private void DrawPixelGrid(Painter2D painter, Rect textureViewRect)
        {
            float left = Mathf.Max(textureViewRect.xMin, 0f);
            float right = Mathf.Min(textureViewRect.xMax, contentRect.width);
            float top = Mathf.Max(textureViewRect.yMin, 0f);
            float bottom = Mathf.Min(textureViewRect.yMax, contentRect.height);
            if (left >= right || top >= bottom)
                return;

            var textureMin = viewTransform.ViewToTexture(new Vector2(left, bottom));
            var textureMax = viewTransform.ViewToTexture(new Vector2(right, top));

            painter.strokeColor = pixelGridColor;
            painter.lineWidth = 1f;
            painter.BeginPath();

            int firstColumn = Mathf.Max(0, Mathf.CeilToInt(textureMin.x));
            int lastColumn = Mathf.Min(viewTransform.TextureWidth, Mathf.FloorToInt(textureMax.x));
            for (int x = firstColumn; x <= lastColumn; x++)
            {
                float viewX = viewTransform.TextureToView(new Vector2(x, 0f)).x;
                painter.MoveTo(new Vector2(viewX, top));
                painter.LineTo(new Vector2(viewX, bottom));
            }

            int firstRow = Mathf.Max(0, Mathf.CeilToInt(textureMin.y));
            int lastRow = Mathf.Min(viewTransform.TextureHeight, Mathf.FloorToInt(textureMax.y));
            for (int y = firstRow; y <= lastRow; y++)
            {
                float viewY = viewTransform.TextureToView(new Vector2(0f, y)).y;
                painter.MoveTo(new Vector2(left, viewY));
                painter.LineTo(new Vector2(right, viewY));
            }

            painter.Stroke();
        }

        private void DrawRegionOutlines(Painter2D painter)
        {
            if (document.Regions.Count == 0)
                return;

            // Unselected pass, flushed in batches for the same reason as the shape passes.
            painter.strokeColor = regionOutlineColor;
            painter.lineWidth = 1f;
            painter.BeginPath();
            int batched = 0;
            foreach (var region in document.Regions)
            {
                if (selection != null && selection.IsSelected(region.Id))
                    continue;
                AddRectPath(painter, viewTransform.TextureRectToView(region.Rect));
                batched += 4;
                if (batched < maxBatchedPoints)
                    continue;
                painter.Stroke();
                painter.BeginPath();
                batched = 0;
            }
            painter.Stroke();

            // Unused regions, over the plain outlines but under the selection so selection wins.
            if (unusedRegionIds is { Count: > 0 })
            {
                painter.strokeColor = unusedOutlineColor;
                painter.lineWidth = 2f;
                painter.BeginPath();
                foreach (var region in document.Regions)
                {
                    if (unusedRegionIds.Contains(region.Id)
                        && (selection == null || !selection.IsSelected(region.Id)))
                        AddRectPath(painter, viewTransform.TextureRectToView(region.Rect));
                }
                painter.Stroke();
            }

            // 9-slice border lines for every region that has one, selected or not.
            painter.strokeColor = borderLineColor;
            painter.lineWidth = 1f;
            painter.BeginPath();
            foreach (var region in document.Regions)
            {
                if (region.Border != Vector4.zero)
                    AddBorderLinesPath(painter, region.Rect, region.Border);
            }
            painter.Stroke();

            // Selected pass, drawn on top.
            if (selection == null || selection.Count == 0)
                return;
            painter.strokeColor = selectedOutlineColor;
            painter.lineWidth = 1.5f;
            painter.BeginPath();
            foreach (var region in document.Regions)
            {
                if (selection.IsSelected(region.Id))
                    AddRectPath(painter, viewTransform.TextureRectToView(region.Rect));
            }
            painter.Stroke();

            DrawPivots(painter);
        }

        /// <summary>Pivot of each selected region as a fixed-size ring, so it stays readable at any zoom.</summary>
        private void DrawPivots(Painter2D painter)
        {
            painter.strokeColor = pivotColor;
            painter.lineWidth = pivotLineWidth;
            painter.BeginPath();
            foreach (var region in document.Regions)
            {
                if (!selection.IsSelected(region.Id))
                    continue;
                var center = viewTransform.TextureToView(region.GetPivotTexturePosition());
                painter.MoveTo(new Vector2(center.x + pivotRadius, center.y));
                painter.Arc(center, pivotRadius, new Angle(0f), new Angle(360f));
            }
            painter.Stroke();
        }

        private void DrawPreviewRects(Painter2D painter)
        {
            if (previewRects == null || previewRects.Count == 0)
                return;
            painter.strokeColor = previewRectColor;
            painter.lineWidth = 1f;
            painter.BeginPath();
            foreach (var rect in previewRects)
                AddRectPath(painter, viewTransform.TextureRectToView(rect));
            painter.Stroke();
        }

        /// <summary>
        /// Where the onion-skin box lands on screen; shared by drawing and by the pivot-drag
        /// hit test so the two can never disagree. Only valid while the box is visible.
        /// </summary>
        private struct OnionSkinLayout
        {
            /// <summary>The framed box in the viewport's top-right corner.</summary>
            public Rect Box;
            /// <summary>The area inside the box the stacked frames occupy.</summary>
            public Rect StackRect;
            /// <summary>The shared candidate-pivot point; frames hang off it by their extents.</summary>
            public Vector2 PivotView;
            /// <summary>Texture pixels to box pixels.</summary>
            public float Scale;
        }

        private bool TryGetOnionSkinLayout(out OnionSkinLayout layout)
        {
            layout = default;
            if (onionFrames == null || onionFrames.Count == 0 || previewTexture == null)
                return false;

            // Extents of every frame relative to its anchor decide the stack's bounds.
            float maxLeft = 0f, maxRight = 0f, maxUp = 0f, maxDown = 0f;
            foreach (var (rect, anchor) in onionFrames)
            {
                maxLeft = Mathf.Max(maxLeft, anchor.x - rect.xMin);
                maxRight = Mathf.Max(maxRight, rect.xMax - anchor.x);
                maxUp = Mathf.Max(maxUp, rect.yMax - anchor.y);
                maxDown = Mathf.Max(maxDown, anchor.y - rect.yMin);
            }
            float neededWidth = maxLeft + maxRight;
            float neededHeight = maxUp + maxDown;
            if (neededWidth <= 0f || neededHeight <= 0f)
                return false;

            const float boxSize = 240f;
            const float padding = 10f;
            var box = new Rect(contentRect.width - boxSize - 12f, 12f, boxSize, boxSize);
            float scale = Mathf.Min(
                (box.width - padding * 2f) / neededWidth,
                (box.height - padding * 2f) / neededHeight);
            if (scale <= 0f)
                return false;

            layout.Box = box;
            layout.PivotView = new Vector2(
                box.xMin + padding + maxLeft * scale,
                box.yMin + padding + maxUp * scale);
            layout.StackRect = new Rect(
                layout.PivotView.x - maxLeft * scale, layout.PivotView.y - maxUp * scale,
                neededWidth * scale, neededHeight * scale);
            layout.Scale = scale;
            return true;
        }

        private void ReportOnionAnchor(OnionSkinLayout layout, Vector2 viewPosition)
        {
            var stack = layout.StackRect;
            // View y grows down, texture y grows up — the stack's bottom edge is v = 0.
            var normalized = new Vector2(
                Mathf.Clamp01((viewPosition.x - stack.xMin) / stack.width),
                Mathf.Clamp01((stack.yMax - viewPosition.y) / stack.height));
            OnionAnchorDragged?.Invoke(normalized);
        }

        /// <summary>
        /// Onion skin for pivot alignment: all frames drawn semi-transparent, stacked with
        /// their candidate pivots coincident, in a fixed box at the top-right. If the art
        /// lines up, the animation is stable — visible statically, before applying anything.
        /// Also drops a cross on each frame in texture space showing where its pivot lands.
        /// </summary>
        private void DrawOnionSkin(MeshGenerationContext context, Painter2D painter)
        {
            if (!TryGetOnionSkinLayout(out var layout))
                return;

            var box = layout.Box;
            var pivotView = layout.PivotView;
            float scale = layout.Scale;

            painter.fillColor = new Color(0f, 0f, 0f, 0.6f);
            painter.strokeColor = new Color(0.1f, 0.1f, 0.1f);
            painter.lineWidth = 1f;
            painter.BeginPath();
            AddRectPath(painter, box);
            painter.Fill();
            painter.Stroke();

            float alpha = Mathf.Clamp(2.5f / onionFrames.Count, 0.15f, 0.5f);
            var tint = new Color(1f, 1f, 1f, alpha);
            foreach (var (rect, anchor) in onionFrames)
            {
                var viewRect = Rect.MinMaxRect(
                    pivotView.x - (anchor.x - rect.xMin) * scale,
                    pivotView.y - (rect.yMax - anchor.y) * scale,
                    pivotView.x + (rect.xMax - anchor.x) * scale,
                    pivotView.y + (anchor.y - rect.yMin) * scale);
                var uvRect = Rect.MinMaxRect(
                    rect.xMin / viewTransform.TextureWidth, rect.yMin / viewTransform.TextureHeight,
                    rect.xMax / viewTransform.TextureWidth, rect.yMax / viewTransform.TextureHeight);
                DrawTexturedQuad(context, viewRect, uvRect, tint);
            }

            painter.strokeColor = pivotColor;
            painter.lineWidth = 1.5f;
            painter.BeginPath();
            painter.MoveTo(new Vector2(pivotView.x - 6f, pivotView.y));
            painter.LineTo(new Vector2(pivotView.x + 6f, pivotView.y));
            painter.MoveTo(new Vector2(pivotView.x, pivotView.y - 6f));
            painter.LineTo(new Vector2(pivotView.x, pivotView.y + 6f));
            painter.Stroke();

            // Candidate-pivot crosses on the frames themselves, out in texture space.
            painter.strokeColor = previewRectColor;
            painter.lineWidth = 1.5f;
            painter.BeginPath();
            foreach (var (_, anchor) in onionFrames)
            {
                var view = viewTransform.TextureToView(anchor);
                painter.MoveTo(new Vector2(view.x - 5f, view.y));
                painter.LineTo(new Vector2(view.x + 5f, view.y));
                painter.MoveTo(new Vector2(view.x, view.y - 5f));
                painter.LineTo(new Vector2(view.x, view.y + 5f));
            }
            painter.Stroke();
        }

        private void DrawTexturedQuad(MeshGenerationContext context, Rect viewRect, Rect uvRect, Color tint)
        {
            var mesh = context.Allocate(4, 6, previewTexture);
            mesh.SetNextVertex(new Vertex
            {
                position = new Vector3(viewRect.xMin, viewRect.yMin, 0f), tint = tint,
                uv = new Vector2(uvRect.xMin, uvRect.yMax)
            });
            mesh.SetNextVertex(new Vertex
            {
                position = new Vector3(viewRect.xMax, viewRect.yMin, 0f), tint = tint,
                uv = new Vector2(uvRect.xMax, uvRect.yMax)
            });
            mesh.SetNextVertex(new Vertex
            {
                position = new Vector3(viewRect.xMax, viewRect.yMax, 0f), tint = tint,
                uv = new Vector2(uvRect.xMax, uvRect.yMin)
            });
            mesh.SetNextVertex(new Vertex
            {
                position = new Vector3(viewRect.xMin, viewRect.yMax, 0f), tint = tint,
                uv = new Vector2(uvRect.xMin, uvRect.yMin)
            });
            mesh.SetNextIndex(0);
            mesh.SetNextIndex(1);
            mesh.SetNextIndex(2);
            mesh.SetNextIndex(0);
            mesh.SetNextIndex(2);
            mesh.SetNextIndex(3);
        }

        private void DrawPreviewShapes(Painter2D painter)
        {
            if (previewShapes == null || previewShapes.Count == 0)
                return;
            painter.strokeColor = previewRectColor;
            painter.lineWidth = 1.5f;
            painter.BeginPath();
            int batched = 0;
            foreach (var path in previewShapes)
            {
                if (path == null || path.Length < 2)
                    continue;
                painter.MoveTo(viewTransform.TextureToView(path[0]));
                for (int i = 1; i < path.Length; i++)
                    painter.LineTo(viewTransform.TextureToView(path[i]));
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
            painter.Stroke();
        }

        /// <summary>Border components: x=left, y=bottom, z=right, w=top — insets from the edges.</summary>
        private void AddBorderLinesPath(Painter2D painter, Rect rect, Vector4 border)
        {
            AddTextureLine(painter, new Vector2(rect.xMin + border.x, rect.yMin), new Vector2(rect.xMin + border.x, rect.yMax));
            AddTextureLine(painter, new Vector2(rect.xMax - border.z, rect.yMin), new Vector2(rect.xMax - border.z, rect.yMax));
            AddTextureLine(painter, new Vector2(rect.xMin, rect.yMax - border.w), new Vector2(rect.xMax, rect.yMax - border.w));
            AddTextureLine(painter, new Vector2(rect.xMin, rect.yMin + border.y), new Vector2(rect.xMax, rect.yMin + border.y));
        }

        private void AddTextureLine(Painter2D painter, Vector2 textureA, Vector2 textureB)
        {
            painter.MoveTo(viewTransform.TextureToView(textureA));
            painter.LineTo(viewTransform.TextureToView(textureB));
        }
    }
}
