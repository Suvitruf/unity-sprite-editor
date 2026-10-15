using System;
using System.Collections.Generic;
using BetterSpriteEditor.Model;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace BetterSpriteEditor.UI
{
    /// <summary>
    /// Modal dialog for combining selected regions: choose the new region's name and pivot
    /// settings; the combined rect is the union of the sources (shown read-only).
    /// </summary>
    public class CombineDialogWindow : EditorWindow
    {
        public class Result
        {
            public string Name;
            public SpriteAlignment Alignment;
            public Vector2 Pivot;
        }

        private SpriteDocument document;
        private List<SpriteRegion> sourceRegions;
        private Rect unionRect;
        private Action<Result> onConfirm;
        private EditorWindow owner;

        private TextField nameField;
        private EnumField alignmentField;
        private Vector2Field pivotField;
        private Label validationLabel;

        public static void Open(
            SpriteDocument document, List<SpriteRegion> regions, EditorWindow owner,
            Action<Result> onConfirm)
        {
            var window = CreateInstance<CombineDialogWindow>();
            window.document = document;
            window.sourceRegions = regions;
            window.owner = owner;
            window.onConfirm = onConfirm;
            window.unionRect = ComputeUnionRect(regions);
            window.titleContent = new GUIContent("Combine Regions");
            var size = new Vector2(320, 190);
            window.minSize = window.maxSize = size;
            window.position = new Rect(owner.position.center - size * 0.5f, size);
            window.ShowModalUtility();
        }

        private void OnDestroy()
        {
            // The modal steals focus; without this the next Ctrl+Z would hit the editor's
            // global undo instead of our window. Delayed, so it runs after modal teardown.
            var windowToFocus = owner;
            if (windowToFocus != null)
                EditorApplication.delayCall += () => windowToFocus.Focus();
        }

        private static Rect ComputeUnionRect(List<SpriteRegion> regions)
        {
            var union = regions[0].Rect;
            for (int i = 1; i < regions.Count; i++)
            {
                union = Rect.MinMaxRect(
                    Mathf.Min(union.xMin, regions[i].Rect.xMin),
                    Mathf.Min(union.yMin, regions[i].Rect.yMin),
                    Mathf.Max(union.xMax, regions[i].Rect.xMax),
                    Mathf.Max(union.yMax, regions[i].Rect.yMax));
            }
            return union;
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.paddingLeft = 8;
            root.style.paddingRight = 8;
            root.style.paddingTop = 8;

            root.Add(new Label(
                $"Combining {sourceRegions.Count} regions into " +
                $"{unionRect.width:0}x{unionRect.height:0} at ({unionRect.x:0}, {unionRect.y:0}).")
            {
                style = { whiteSpace = WhiteSpace.Normal, marginBottom = 6 }
            });

            // The sources are deleted by the combine, so the active region's name is free to reuse.
            nameField = new TextField("Name") { value = sourceRegions[^1].Name };
            root.Add(nameField);

            alignmentField = new EnumField("Alignment", SpriteAlignment.Center);
            alignmentField.RegisterValueChangedCallback(evt =>
                pivotField.SetEnabled((SpriteAlignment)evt.newValue == SpriteAlignment.Custom));
            root.Add(alignmentField);

            pivotField = new Vector2Field("Custom Pivot") { value = new Vector2(0.5f, 0.5f) };
            pivotField.SetEnabled(false);
            root.Add(pivotField);

            validationLabel = new Label
            {
                style = { color = new Color(1f, 0.45f, 0.4f), whiteSpace = WhiteSpace.Normal }
            };
            root.Add(validationLabel);

            var buttonRow = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, justifyContent = Justify.FlexEnd, marginTop = 8 }
            };
            buttonRow.Add(new Button(Close) { text = "Cancel" });
            buttonRow.Add(new Button(Confirm) { text = "Combine" });
            root.Add(buttonRow);
        }

        private void Confirm()
        {
            string name = nameField.value?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                validationLabel.text = "Name must not be empty.";
                return;
            }
            // Unique among the regions that will remain after the combine.
            foreach (var region in document.Regions)
            {
                if (!sourceRegions.Contains(region) && region.Name == name)
                {
                    validationLabel.text = $"Name '{name}' is already used by another region.";
                    return;
                }
            }

            var result = new Result
            {
                Name = name,
                Alignment = (SpriteAlignment)alignmentField.value,
                Pivot = pivotField.value
            };
            Close();
            onConfirm?.Invoke(result);
        }
    }
}
