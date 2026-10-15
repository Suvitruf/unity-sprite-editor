using System;
using System.Collections.Generic;
using BetterSpriteEditor.Model;
using BetterSpriteEditor.Operations.Slicing;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace BetterSpriteEditor.UI
{
    /// <summary>
    /// Modal dialog for splitting one region into a grid of smaller regions.
    /// Children are named base name + counter (row-major, top-left first), and the whole
    /// split commits as one history entry.
    /// </summary>
    public class SplitDialogWindow : EditorWindow
    {
        public class Result
        {
            public List<Rect> Rects;
            public string BaseName;
            public int StartNumber;
        }

        private enum SplitMode
        {
            ByCellCount,
            ByCellSize
        }

        private SpriteDocument document;
        private SpriteRegion sourceRegion;
        private EditorWindow owner;
        private Func<TextureAlphaMap> alphaMapProvider;
        private Action<Result> onConfirm;

        private TextureAlphaMap alphaMap;
        private bool alphaMapFailed;
        private List<Rect> computedRects = new();

        private EnumField modeField;
        private VisualElement countRow;
        private IntegerField columnsField;
        private IntegerField rowsField;
        private VisualElement sizeRow;
        private IntegerField cellWidthField;
        private IntegerField cellHeightField;
        private Toggle skipEmptyToggle;
        private TextField baseNameField;
        private IntegerField startNumberField;
        private Label previewLabel;
        private Label validationLabel;
        private Button splitButton;

        public static void Open(SpriteDocument document, SpriteRegion region, EditorWindow owner,
            Func<TextureAlphaMap> alphaMapProvider, Action<Result> onConfirm)
        {
            var window = CreateInstance<SplitDialogWindow>();
            window.document = document;
            window.sourceRegion = region;
            window.owner = owner;
            window.alphaMapProvider = alphaMapProvider;
            window.onConfirm = onConfirm;
            window.titleContent = new GUIContent($"Split '{region.Name}'");
            var size = new Vector2(340f, 250f);
            window.minSize = window.maxSize = size;
            window.position = new Rect(owner.position.center - size * 0.5f, size);
            window.ShowModalUtility();
        }

        private void OnDestroy()
        {
            // The modal steals focus; hand it back so the next Ctrl+Z reaches our history.
            var windowToFocus = owner;
            if (windowToFocus != null)
                EditorApplication.delayCall += () => windowToFocus.Focus();
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.paddingLeft = root.style.paddingRight = 8;
            root.style.paddingTop = 8;

            root.Add(new Label(
                $"Splitting a {sourceRegion.Rect.width:0}x{sourceRegion.Rect.height:0} region into a grid.")
            {
                style = { whiteSpace = WhiteSpace.Normal, marginBottom = 6 }
            });

            modeField = new EnumField("Method", SplitMode.ByCellCount);
            modeField.RegisterValueChangedCallback(_ => Recompute());
            root.Add(modeField);

            countRow = MakeFieldRow(
                columnsField = MakeIntField("C", 2), rowsField = MakeIntField("R", 2));
            root.Add(countRow);

            sizeRow = MakeFieldRow(
                cellWidthField = MakeIntField("W", 16), cellHeightField = MakeIntField("H", 16));
            root.Add(sizeRow);

            skipEmptyToggle = new Toggle("Skip Empty Cells") { value = true };
            skipEmptyToggle.RegisterValueChangedCallback(_ => Recompute());
            root.Add(skipEmptyToggle);

            baseNameField = new TextField("Base Name") { value = sourceRegion.Name + "_" };
            baseNameField.tooltip = "Children are named <base name><counter>, top-left first.";
            baseNameField.RegisterValueChangedCallback(_ => Recompute());
            root.Add(baseNameField);

            startNumberField = new IntegerField("Start Number")
            {
                value = 0,
                isDelayed = true,
                tooltip = "First counter value, e.g. 1 for names starting at <base name>1."
            };
            startNumberField.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue < 0)
                    startNumberField.SetValueWithoutNotify(0);
                Recompute();
            });
            root.Add(startNumberField);

            previewLabel = new Label { style = { marginTop = 4 } };
            root.Add(previewLabel);

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
            splitButton = new Button(Confirm) { text = "Split" };
            buttonRow.Add(splitButton);
            root.Add(buttonRow);

            Recompute();
        }

        private void Recompute()
        {
            var mode = (SplitMode)modeField.value;
            countRow.style.display = mode == SplitMode.ByCellCount ? DisplayStyle.Flex : DisplayStyle.None;
            sizeRow.style.display = mode == SplitMode.ByCellSize ? DisplayStyle.Flex : DisplayStyle.None;

            // Cells are generated in region-local space, then offset into texture space —
            // the empty-cell test needs real texture coordinates.
            var settings = new GridSliceSettings
            {
                ByCellCount = mode == SplitMode.ByCellCount,
                Columns = Mathf.Max(1, columnsField.value),
                Rows = Mathf.Max(1, rowsField.value),
                CellWidth = Mathf.Max(1, cellWidthField.value),
                CellHeight = Mathf.Max(1, cellHeightField.value),
                SkipEmptyCells = false
            };
            var localRects = GridSlicer.Slice(settings,
                Mathf.RoundToInt(sourceRegion.Rect.width), Mathf.RoundToInt(sourceRegion.Rect.height), null);

            bool skipEmpty = skipEmptyToggle.value && TryLoadAlphaMap();
            computedRects.Clear();
            foreach (var local in localRects)
            {
                var textureRect = new Rect(
                    local.x + sourceRegion.Rect.x, local.y + sourceRegion.Rect.y,
                    local.width, local.height);
                if (skipEmpty && !alphaMap.AnyOpaqueInActualRect(textureRect))
                    continue;
                computedRects.Add(textureRect);
            }

            string error = Validate();
            if (skipEmptyToggle.value && alphaMapFailed)
                error = string.IsNullOrEmpty(error)
                    ? "Pixel data unavailable — empty cells are kept."
                    : error;

            previewLabel.text = computedRects.Count == 0
                ? "No cells fit these settings."
                : $"Will create {computedRects.Count} regions, e.g. '{baseNameField.value}{startNumberField.value}'.";
            validationLabel.text = error ?? string.Empty;
            splitButton.SetEnabled(computedRects.Count > 0 && string.IsNullOrEmpty(error));
        }

        /// <summary>Null when everything is fine; otherwise the reason Split is disabled.</summary>
        private string Validate()
        {
            string baseName = baseNameField.value;
            if (string.IsNullOrWhiteSpace(baseName))
                return "Base name must not be empty.";

            // The source is deleted by the split, so its own name is free to reuse.
            var taken = new HashSet<string>();
            foreach (var region in document.Regions)
            {
                if (region != sourceRegion)
                    taken.Add(region.Name);
            }
            int start = startNumberField.value;
            for (int i = 0; i < computedRects.Count; i++)
            {
                string candidate = baseName + (start + i);
                if (taken.Contains(candidate))
                    return $"Name '{candidate}' is already used by another region.";
            }
            return null;
        }

        private bool TryLoadAlphaMap()
        {
            if (alphaMap != null)
                return true;
            if (alphaMapFailed)
                return false;
            try
            {
                alphaMap = alphaMapProvider?.Invoke();
            }
            catch (Exception)
            {
                alphaMap = null;
            }
            alphaMapFailed = alphaMap == null;
            return !alphaMapFailed;
        }

        private void Confirm()
        {
            if (computedRects.Count == 0)
                return;
            var result = new Result
            {
                Rects = new List<Rect>(computedRects),
                BaseName = baseNameField.value,
                StartNumber = startNumberField.value
            };
            Close();
            onConfirm?.Invoke(result);
        }

        private IntegerField MakeIntField(string label, int initialValue)
        {
            var field = new IntegerField(label)
            {
                value = initialValue,
                isDelayed = true,
                style = { flexGrow = 1, flexBasis = 0 }
            };
            field.labelElement.style.minWidth = 16;
            field.RegisterValueChangedCallback(_ => Recompute());
            return field;
        }

        private static VisualElement MakeFieldRow(params VisualElement[] fields)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            foreach (var field in fields)
                row.Add(field);
            return row;
        }
    }
}
