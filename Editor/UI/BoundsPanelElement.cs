using System;
using System.Collections.Generic;
using BetterSpriteEditor.Model;
using BetterSpriteEditor.Operations;
using BetterSpriteEditor.Operations.Bounds;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace BetterSpriteEditor.UI
{
    internal sealed class BoundsPanelElement : VisualElement
    {
        private SpriteDocument document;
        private SelectionModel selection;
        private HistoryStack history;
        private Func<TextureAlphaMap> getAlpha;
        private Action<List<Rect>> preview;
        private FrameBoundsPlan plan;
        private readonly EnumField mode;
        private readonly IntegerField padding;
        private readonly IntegerField threshold;
        private readonly Toggle automatic;
        private readonly Vector2IntField size;
        private readonly Label hint;
        private readonly Label summary;
        private readonly ScrollView results;
        private readonly Button commit;

        internal BoundsPanelElement()
        {
            style.position = Position.Absolute;
            style.top = style.left = 8;
            style.width = 320;
            style.backgroundColor = new Color(0.22f, 0.22f, 0.22f, 0.98f);
            style.paddingLeft = style.paddingRight = 8;
            style.paddingTop = style.paddingBottom = 8;
            style.display = DisplayStyle.None;
            Add(new Label("Frame Bounds") { style = { unityFontStyleAndWeight = FontStyle.Bold } });
            mode = new EnumField("Action", FrameBoundsMode.TrimTransparency) { name = "bounds-mode" };
            Add(mode);
            padding = new IntegerField("Padding (px)") { name = "bounds-padding", isDelayed = true };
            Add(padding);
            threshold = new IntegerField("Alpha threshold") { name = "bounds-threshold", isDelayed = true };
            threshold.tooltip = "Pixels with alpha greater than this value (0–254) determine the trimmed bounds.";
            Add(threshold);
            automatic = new Toggle("Automatic size") { name = "bounds-auto-size", value = true };
            Add(automatic);
            size = new Vector2IntField("Size (px)") { name = "bounds-size", value = new Vector2Int(64, 64) };
            size.Query<IntegerField>().ForEach(field => field.isDelayed = true);
            Add(size);
            hint = new Label { style = { whiteSpace = WhiteSpace.Normal, marginTop = 4, opacity = 0.8f } };
            Add(hint);
            summary = new Label { name = "bounds-summary", style = { whiteSpace = WhiteSpace.Normal, marginTop = 6 } };
            Add(summary);
            results = new ScrollView { style = { maxHeight = 180, marginTop = 4 } };
            Add(results);
            var buttons = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, justifyContent = Justify.FlexEnd, marginTop = 6 }
            };
            buttons.Add(new Button(Hide) { name = "bounds-cancel", text = "Cancel" });
            commit = new Button(Commit) { name = "bounds-commit", text = "Change Bounds" };
            buttons.Add(commit);
            Add(buttons);
            mode.RegisterValueChangedCallback(_ => Recompute());
            padding.RegisterValueChangedCallback(_ => Recompute());
            threshold.RegisterValueChangedCallback(_ => Recompute());
            automatic.RegisterValueChangedCallback(_ => Recompute());
            size.RegisterValueChangedCallback(_ => Recompute());
            RegisterCallback<DetachFromPanelEvent>(_ => Hide());
        }

        internal void Show(SpriteDocument newDocument, SelectionModel newSelection, HistoryStack newHistory,
            Func<TextureAlphaMap> alphaProvider, Action<List<Rect>> showPreview)
        {
            Hide();
            document = newDocument;
            selection = newSelection;
            history = newHistory;
            getAlpha = alphaProvider;
            preview = showPreview;
            document.Changed += Recompute;
            selection.Changed += Recompute;
            style.display = DisplayStyle.Flex;
            Recompute();
        }

        internal void Hide()
        {
            if (document != null) document.Changed -= Recompute;
            if (selection != null) selection.Changed -= Recompute;
            document = null;
            selection = null;
            history = null;
            plan = null;
            getAlpha = null;
            style.display = DisplayStyle.None;
            preview?.Invoke(null);
            preview = null;
        }

        private void Recompute()
        {
            if (document == null) return;
            bool trim = (FrameBoundsMode)mode.value == FrameBoundsMode.TrimTransparency;
            threshold.style.display = trim ? DisplayStyle.Flex : DisplayStyle.None;
            automatic.style.display = trim ? DisplayStyle.None : DisplayStyle.Flex;
            size.style.display = trim || automatic.value ? DisplayStyle.None : DisplayStyle.Flex;
            hint.text = trim
                ? "Trim to visible pixels plus padding within the original rectangle. Empty frames stay unchanged."
                : "Use common bounds around existing pivots, retaining every original frame. Padding expands the common bounds.";
            hint.text += " Pivot anchors, shapes, and existing 9-slice lines stay fixed. Save with the main Apply button.";
            var selected = document.Regions.FindAll(r => selection.IsSelected(r.Id));
            try
            {
                // Avoid an expensive pixel read when no operation can be offered.
                var alpha = selected.Count > 0 && document.CanPreservePivotPosition ? getAlpha?.Invoke() : null;
                plan = FrameBounds.Build(document, selected, alpha, new FrameBoundsSettings
                {
                    Mode = (FrameBoundsMode)mode.value, Padding = padding.value,
                    AlphaThreshold = trim ? threshold.value : 0,
                    AutomaticSize = automatic.value, Size = size.value
                });
            }
            catch (Exception exception)
            {
                plan = new FrameBoundsPlan { Error = $"Cannot read texture pixels: {exception.Message}" };
            }
            commit.SetEnabled(plan.CanApply);
            results.Clear();
            int conflicts = plan.Entries.FindAll(e => e.Issue != null).Count;
            summary.text = plan.Error ?? $"{plan.ChangedCount} changes; {plan.EmptyCount} empty frames unchanged; {conflicts} conflicts."
                + (!trim && plan.UniformSize.x > 0 ? $" Size: {plan.UniformSize.x} × {plan.UniformSize.y}." : string.Empty);
            var rects = new List<Rect>();
            foreach (var entry in plan.Entries)
            {
                var rect = entry.ProposedRect;
                string status = entry.Issue ?? (entry.Empty ? "Empty — unchanged" : entry.Changed ? "Ready" : "Unchanged");
                var row = new Label($"{entry.Before.Name}: {rect.width:0} × {rect.height:0} at ({rect.x:0}, {rect.y:0}) — {status}")
                {
                    style = { whiteSpace = WhiteSpace.Normal, marginBottom = 4 }
                };
                if (entry.Issue != null) row.style.color = new Color(1f, 0.6f, 0.3f);
                results.Add(row);
                // Conflicting proposals are still visible; the row explains why they cannot commit.
                if (!entry.Empty && rect.width >= 1 && rect.height >= 1
                    && rect.width <= document.TextureWidth && rect.height <= document.TextureHeight
                    && !float.IsNaN(rect.x) && !float.IsInfinity(rect.x)
                    && !float.IsNaN(rect.y) && !float.IsInfinity(rect.y))
                    rects.Add(rect);
            }
            preview?.Invoke(plan.Error == null ? rects : null);
        }

        private void Commit()
        {
            if (document == null) return;
            Recompute(); // Validate current selection, metadata, and capabilities again.
            var command = plan.CreateCommand(document);
            if (command == null) return;
            var currentHistory = history;
            Hide();
            currentHistory.Do(command);
        }
    }
}
