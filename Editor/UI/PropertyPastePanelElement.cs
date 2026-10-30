using System;
using System.Collections.Generic;
using BetterSpriteEditor.Model;
using BetterSpriteEditor.Operations;
using BetterSpriteEditor.Operations.Clipboard;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace BetterSpriteEditor.UI
{
    internal sealed class PropertyPastePanelElement : VisualElement
    {
        private readonly Dictionary<SpritePropertyFields, Toggle> fields = new();
        private readonly EnumField pivotSpace;
        private readonly EnumField shapeSpace;
        private readonly EnumField outlineSource;
        private readonly EnumField physicsSource;
        private readonly Label sourceLabel;
        private readonly Label summary;
        private readonly Label note;
        private readonly ScrollView results;
        private readonly Button commit;
        private readonly HashSet<GUID> excluded = new();
        private SpriteDocument document;
        private SelectionModel selection;
        private HistoryStack history;
        private SpritePropertyPayload source;
        private SpritePropertyPastePlan plan;
        private Action<SpritePropertyPastePlan> preview;
        internal bool IsOpen => document != null;

        internal PropertyPastePanelElement()
        {
            name = "property-paste-panel";
            style.position = Position.Absolute;
            style.top = style.left = 8;
            style.width = 360;
            style.maxHeight = Length.Percent(95);
            style.backgroundColor = new Color(0.22f, 0.22f, 0.22f, 0.98f);
            style.paddingLeft = style.paddingRight = 8;
            style.paddingTop = style.paddingBottom = 8;
            style.display = DisplayStyle.None;
            Add(new Label("Paste Properties") { style = { unityFontStyleAndWeight = FontStyle.Bold } });
            var body = new ScrollView { style = { flexShrink = 1 } };
            Add(body);
            sourceLabel = WrappedLabel();
            body.Add(sourceLabel);
            AddToggle(body, SpritePropertyFields.Pivot, "Pivot", true);
            pivotSpace = AddEnum(body, "Pivot coordinates", PivotPasteSpace.Normalized, "paste-pivot-space");
            AddToggle(body, SpritePropertyFields.Border, "Border (pixels)");
            AddToggle(body, SpritePropertyFields.Outline, "Outline");
            AddToggle(body, SpritePropertyFields.OutlineTessellation, "Outline tessellation");
            outlineSource = AddEnum(body, "Copy outline from", ShapeKind.Outline, "paste-outline-source");
            AddToggle(body, SpritePropertyFields.Physics, "Physics shape");
            AddToggle(body, SpritePropertyFields.PhysicsTessellation, "Physics tessellation");
            physicsSource = AddEnum(body, "Copy physics from", ShapeKind.Physics, "paste-physics-source");
            shapeSpace = AddEnum(body, "Shape coordinates", ShapePasteSpace.Pivot, "paste-shape-space");
            note = WrappedLabel();
            body.Add(note);
            summary = WrappedLabel();
            summary.name = "paste-summary";
            body.Add(summary);
            results = new ScrollView { name = "paste-targets", style = { maxHeight = 180, marginTop = 4 } };
            body.Add(results);
            var buttons = new VisualElement { style = { flexDirection = FlexDirection.Row, justifyContent = Justify.FlexEnd, marginTop = 6 } };
            buttons.Add(new Button(Hide) { name = "paste-cancel", text = "Cancel" });
            commit = new Button(Commit) { name = "paste-commit", text = "Paste Properties" };
            buttons.Add(commit);
            Add(buttons);
            RegisterCallback<DetachFromPanelEvent>(_ => Hide());
        }

        private static Label WrappedLabel() => new() { style = { whiteSpace = WhiteSpace.Normal, marginTop = 4 } };

        private void AddToggle(VisualElement parent, SpritePropertyFields field, string label, bool value = false)
        {
            var toggle = new Toggle(label) { name = "paste-" + field.ToString().ToLowerInvariant(), value = value };
            fields.Add(field, toggle);
            parent.Add(toggle);
            toggle.RegisterValueChangedCallback(_ => Recompute());
        }

        private EnumField AddEnum(VisualElement parent, string label, Enum value, string elementName)
        {
            var field = new EnumField(label, value) { name = elementName };
            parent.Add(field);
            field.RegisterValueChangedCallback(_ => Recompute());
            return field;
        }

        internal void Show(SpriteDocument newDocument, SelectionModel newSelection, HistoryStack newHistory,
            SpritePropertyPayload payload, Action<SpritePropertyPastePlan> showPreview)
        {
            Hide();
            document = newDocument;
            selection = newSelection;
            history = newHistory;
            source = payload;
            preview = showPreview;
            excluded.Clear();
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
            source = null;
            plan = null;
            style.display = DisplayStyle.None;
            preview?.Invoke(null);
            preview = null;
        }

        private void Recompute()
        {
            if (document == null) return;
            var settings = new SpritePropertyPasteSettings
            {
                Fields = SpritePropertyFields.None, PivotSpace = (PivotPasteSpace)pivotSpace.value,
                ShapeSpace = (ShapePasteSpace)shapeSpace.value,
                OutlineSource = (ShapeKind)outlineSource.value, PhysicsSource = (ShapeKind)physicsSource.value
            };
            foreach (var pair in fields) if (pair.Value.value) settings.Fields |= pair.Key;
            pivotSpace.style.display = settings.Includes(SpritePropertyFields.Pivot) ? DisplayStyle.Flex : DisplayStyle.None;
            outlineSource.style.display = settings.Includes(SpritePropertyFields.Outline | SpritePropertyFields.OutlineTessellation) ? DisplayStyle.Flex : DisplayStyle.None;
            physicsSource.style.display = settings.Includes(SpritePropertyFields.Physics | SpritePropertyFields.PhysicsTessellation) ? DisplayStyle.Flex : DisplayStyle.None;
            shapeSpace.style.display = settings.Includes(SpritePropertyFields.Outline | SpritePropertyFields.Physics) ? DisplayStyle.Flex : DisplayStyle.None;
            sourceLabel.text = source == null ? "Clipboard unavailable." : $"Copied: {source.SourceName} ({source.Size.x} × {source.Size.y} px)";
            note.text = "Blue: pivot; yellow: border; orange: outline; green: physics. Outline interiors are tinted; artwork outside them may be clipped. Uncheck targets to exclude them. Save with the main Apply button.";
            if (source != null && source.PixelsPerUnit != document.PixelsPerUnit)
                note.text += " Source and target PPU differ: pixel-preserving modes do not preserve world size.";
            if (source != null && ((settings.Includes(SpritePropertyFields.Outline) && (source.Paths(settings.OutlineSource)?.Length ?? 0) == 0)
                || (settings.Includes(SpritePropertyFields.Physics) && (source.Paths(settings.PhysicsSource)?.Length ?? 0) == 0)))
                note.text += " An empty copied shape will clear the selected destination shape.";
            var selected = document.Regions.FindAll(r => selection.IsSelected(r.Id));
            plan = SpritePropertyPaste.Build(document, selected, source, settings, excluded);
            summary.text = plan.Error ?? $"{plan.ChangedCount} changes; {plan.Entries.FindAll(e => !e.Excluded && e.Issue != null).Count} conflicts; {plan.Entries.FindAll(e => e.Excluded).Count} excluded.";
            commit.SetEnabled(plan.CanApply);
            results.Clear();
            foreach (var entry in plan.Entries)
            {
                var id = entry.Before.Id;
                var row = new Toggle(entry.Before.Name) { name = "paste-target-" + id, value = !entry.Excluded };
                row.RegisterValueChangedCallback(evt =>
                {
                    if (evt.newValue) excluded.Remove(id); else excluded.Add(id);
                    Recompute();
                });
                results.Add(row);
                string status = entry.Excluded ? "Excluded" : entry.Issue ?? (entry.Changed ? "Ready" : "Unchanged");
                if (!entry.Excluded && entry.Issue == null && entry.After != null)
                {
                    if (settings.Includes(SpritePropertyFields.Pivot)) status += $" · Pivot {entry.Before.GetNormalizedPivot()} → {entry.After.GetNormalizedPivot()}";
                    if (settings.Includes(SpritePropertyFields.Border)) status += $" · Border {entry.Before.Border} → {entry.After.Border}";
                }
                var detail = WrappedLabel();
                detail.text = status;
                if (entry.Issue != null) detail.style.color = new Color(1f, 0.6f, 0.3f);
                results.Add(detail);
            }
            preview?.Invoke(plan.Error == null ? plan : null);
        }

        private void Commit()
        {
            if (document == null) return;
            Recompute();
            var command = plan.CreateCommand(document);
            if (command == null) return;
            var currentHistory = history;
            Hide();
            currentHistory.Do(command);
        }
    }
}
