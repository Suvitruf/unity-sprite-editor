using System;
using System.Collections.Generic;
using BetterSpriteEditor.DataAccess;
using BetterSpriteEditor.Model;
using BetterSpriteEditor.Operations;
using BetterSpriteEditor.Operations.Slicing;
using BetterSpriteEditor.UI.Viewport;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace BetterSpriteEditor.UI
{
    /// <summary>
    /// Slicing panel with a non-destructive preview: every parameter change updates orange
    /// ghost rects in the viewport; nothing touches the document until Slice is pressed,
    /// and the whole slice commits as ONE history entry.
    /// </summary>
    public class SlicePanelElement : VisualElement
    {
        private enum SliceMethod
        {
            Automatic,
            GridByCellSize,
            GridByCellCount,
            Isometric
        }

        private enum ExistingRegionsPolicy
        {
            ReplaceAll,
            KeepExisting
        }

        private SpriteDocument document;
        private HistoryStack history;
        private SelectionModel selection;
        private SpriteViewportElement viewport;

        private TextureAlphaMap alphaMap;
        private string alphaMapError;
        private List<Rect> previewRects = new();

        private EnumField methodField;
        private VisualElement cellSizeRow;
        private IntegerField cellWidthField;
        private IntegerField cellHeightField;
        private VisualElement cellCountRow;
        private IntegerField columnsField;
        private IntegerField rowsField;
        private VisualElement offsetRow;
        private IntegerField offsetXField;
        private IntegerField offsetYField;
        private VisualElement paddingRow;
        private IntegerField paddingXField;
        private IntegerField paddingYField;
        private Toggle skipEmptyToggle;
        private Toggle alternateToggle;
        private IntegerField minSizeField;
        private EnumField policyField;
        private EnumField alignmentField;
        private Vector2Field pivotField;
        private Label previewCountLabel;
        private Label warningLabel;
        private Button sliceButton;

        /// <summary>Raised when the panel wants to close (Slice committed or Cancel pressed).</summary>
        public event Action Closed;

        public SlicePanelElement()
        {
            style.position = Position.Absolute;
            style.top = 8;
            style.left = 8;
            style.width = 250;
            style.backgroundColor = new Color(0.22f, 0.22f, 0.22f, 0.98f);
            style.borderTopWidth = style.borderBottomWidth = 1;
            style.borderLeftWidth = style.borderRightWidth = 1;
            SetBorderColor(new Color(0.1f, 0.1f, 0.1f));
            style.paddingLeft = style.paddingRight = 8;
            style.paddingTop = 6;
            style.paddingBottom = 8;
            style.display = DisplayStyle.None;

            Add(new Label("Slice")
            {
                style = { unityFontStyleAndWeight = FontStyle.Bold, marginBottom = 4 }
            });

            methodField = new EnumField("Method", SliceMethod.Automatic);
            methodField.RegisterValueChangedCallback(_ => OnParamsChanged());
            Add(methodField);

            minSizeField = MakeIntField("Min Size", 2);
            Add(minSizeField);

            cellSizeRow = MakeFieldRow(
                cellWidthField = MakeIntField("W", 64), cellHeightField = MakeIntField("H", 64));
            Add(cellSizeRow);

            cellCountRow = MakeFieldRow(
                columnsField = MakeIntField("C", 4), rowsField = MakeIntField("R", 4));
            Add(cellCountRow);

            offsetRow = MakeFieldRow(
                offsetXField = MakeIntField("Off X", 0), offsetYField = MakeIntField("Off Y", 0));
            Add(offsetRow);

            paddingRow = MakeFieldRow(
                paddingXField = MakeIntField("Pad X", 0), paddingYField = MakeIntField("Pad Y", 0));
            Add(paddingRow);

            alternateToggle = new Toggle("Alternate Rows")
            {
                tooltip = "Shift the first row by half a cell."
            };
            alternateToggle.RegisterValueChangedCallback(_ => OnParamsChanged());
            Add(alternateToggle);

            skipEmptyToggle = new Toggle("Skip Empty Cells") { value = true };
            skipEmptyToggle.RegisterValueChangedCallback(_ => OnParamsChanged());
            Add(skipEmptyToggle);

            policyField = new EnumField("Existing", ExistingRegionsPolicy.ReplaceAll);
            policyField.RegisterValueChangedCallback(_ => OnParamsChanged());
            Add(policyField);

            alignmentField = new EnumField("Pivot", SpriteAlignment.Center);
            alignmentField.RegisterValueChangedCallback(evt =>
                pivotField.SetEnabled((SpriteAlignment)evt.newValue == SpriteAlignment.Custom));
            Add(alignmentField);

            pivotField = new Vector2Field("Custom") { value = new Vector2(0.5f, 0.5f) };
            pivotField.SetEnabled(false);
            Add(pivotField);

            warningLabel = new Label
            {
                style =
                {
                    color = new Color(1f, 0.6f, 0.3f),
                    whiteSpace = WhiteSpace.Normal,
                    display = DisplayStyle.None,
                    marginTop = 4
                }
            };
            Add(warningLabel);

            previewCountLabel = new Label { style = { marginTop = 4 } };
            Add(previewCountLabel);

            var buttonRow = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, justifyContent = Justify.FlexEnd, marginTop = 6 }
            };
            buttonRow.Add(new Button(Cancel) { text = "Cancel" });
            sliceButton = new Button(Commit) { text = "Slice" };
            buttonRow.Add(sliceButton);
            Add(buttonRow);
        }

        public void Show(SpriteDocument newDocument, HistoryStack newHistory,
            SelectionModel newSelection, SpriteViewportElement newViewport)
        {
            if (document != newDocument)
            {
                alphaMap = null;
                alphaMapError = null;
            }
            document = newDocument;
            history = newHistory;
            selection = newSelection;
            viewport = newViewport;

            style.display = DisplayStyle.Flex;
            OnParamsChanged();
        }

        public void Hide()
        {
            style.display = DisplayStyle.None;
            previewRects.Clear();
            viewport?.SetPreviewRects(null);
        }

        // --- Preview ---

        private void OnParamsChanged()
        {
            if (document == null || style.display == DisplayStyle.None)
                return;

            var method = (SliceMethod)methodField.value;
            bool isAutomatic = method == SliceMethod.Automatic;
            bool isIsometric = method == SliceMethod.Isometric;
            bool usesCellSize = method == SliceMethod.GridByCellSize || isIsometric;
            cellSizeRow.style.display = usesCellSize ? DisplayStyle.Flex : DisplayStyle.None;
            cellCountRow.style.display = method == SliceMethod.GridByCellCount ? DisplayStyle.Flex : DisplayStyle.None;
            offsetRow.style.display = isAutomatic ? DisplayStyle.None : DisplayStyle.Flex;
            paddingRow.style.display = !isAutomatic && !isIsometric ? DisplayStyle.Flex : DisplayStyle.None;
            alternateToggle.style.display = isIsometric ? DisplayStyle.Flex : DisplayStyle.None;
            skipEmptyToggle.style.display = isAutomatic ? DisplayStyle.None : DisplayStyle.Flex;
            minSizeField.style.display = isAutomatic ? DisplayStyle.Flex : DisplayStyle.None;

            RecomputePreview();
        }

        private void RecomputePreview()
        {
            var method = (SliceMethod)methodField.value;
            bool needsAlpha = method == SliceMethod.Automatic
                              || (skipEmptyToggle.value && method != SliceMethod.Automatic);

            warningLabel.style.display = DisplayStyle.None;
            if (needsAlpha && !TryLoadAlphaMap())
            {
                warningLabel.text = $"Pixel data unavailable: {alphaMapError}" +
                                    (method == SliceMethod.Automatic ? string.Empty : " Empty cells are kept.");
                warningLabel.style.display = DisplayStyle.Flex;
                if (method == SliceMethod.Automatic)
                {
                    previewRects.Clear();
                    UpdatePreviewUI();
                    return;
                }
            }

            switch (method)
            {
                case SliceMethod.Automatic:
                    previewRects = AutoSlicer.Slice(alphaMap,
                        Mathf.Max(1, minSizeField.value), Mathf.Max(1, minSizeField.value));
                    break;
                case SliceMethod.Isometric:
                    previewRects = IsometricSlicer.Slice(new IsometricSliceSettings
                    {
                        CellWidth = Mathf.Max(1, cellWidthField.value),
                        CellHeight = Mathf.Max(1, cellHeightField.value),
                        OffsetX = Mathf.Max(0, offsetXField.value),
                        OffsetY = Mathf.Max(0, offsetYField.value),
                        IsAlternate = alternateToggle.value,
                        SkipEmptyCells = skipEmptyToggle.value && alphaMap != null
                    }, document.TextureWidth, document.TextureHeight, alphaMap);
                    break;
                default:
                    var settings = new GridSliceSettings
                    {
                        ByCellCount = method == SliceMethod.GridByCellCount,
                        CellWidth = Mathf.Max(1, cellWidthField.value),
                        CellHeight = Mathf.Max(1, cellHeightField.value),
                        Columns = Mathf.Max(1, columnsField.value),
                        Rows = Mathf.Max(1, rowsField.value),
                        OffsetX = Mathf.Max(0, offsetXField.value),
                        OffsetY = Mathf.Max(0, offsetYField.value),
                        PaddingX = Mathf.Max(0, paddingXField.value),
                        PaddingY = Mathf.Max(0, paddingYField.value),
                        SkipEmptyCells = skipEmptyToggle.value && alphaMap != null
                    };
                    previewRects = GridSlicer.Slice(
                        settings, document.TextureWidth, document.TextureHeight, alphaMap);
                    break;
            }

            if ((ExistingRegionsPolicy)policyField.value == ExistingRegionsPolicy.KeepExisting)
                previewRects.RemoveAll(rect =>
                    document.Regions.Exists(region => region.Rect.Overlaps(rect)));

            UpdatePreviewUI();
        }

        private void UpdatePreviewUI()
        {
            viewport.SetPreviewRects(previewRects);
            previewCountLabel.text = $"{previewRects.Count} regions";
            sliceButton.SetEnabled(previewRects.Count > 0);
        }

        private bool TryLoadAlphaMap()
        {
            if (alphaMap != null)
                return true;
            if (alphaMapError != null)
                return false; // already failed for this document — don't spam reads
            try
            {
                alphaMap = SpriteDocumentRepository.LoadAlphaMap(document);
                return true;
            }
            catch (Exception exception)
            {
                alphaMapError = exception.Message;
                return false;
            }
        }

        // --- Commit ---

        private void Commit()
        {
            if (previewRects.Count == 0 || !document.CanChangeRegions)
                return;

            bool replaceAll = (ExistingRegionsPolicy)policyField.value == ExistingRegionsPolicy.ReplaceAll;
            var commands = new List<IEditorCommand>();
            if (replaceAll && document.Regions.Count > 0)
                commands.Add(new DeleteRegionsCommand(document, new List<SpriteRegion>(document.Regions)));

            // Names must be unique among the regions that survive plus the new ones.
            var usedNames = new HashSet<string>();
            if (!replaceAll)
            {
                foreach (var region in document.Regions)
                    usedNames.Add(region.Name);
            }

            var alignment = (SpriteAlignment)alignmentField.value;
            var pivot = pivotField.value;
            string baseName = document.Texture.name;
            int nameIndex = 0;
            foreach (var rect in previewRects)
            {
                string name;
                do
                {
                    name = $"{baseName}_{nameIndex++}";
                } while (!usedNames.Add(name));

                commands.Add(new CreateRegionCommand(document, new SpriteRegion
                {
                    Id = GUID.Generate(),
                    Name = name,
                    Rect = rect,
                    Alignment = alignment,
                    Pivot = pivot,
                    Border = Vector4.zero,
                    CustomData = string.Empty
                }));
            }

            history.Do(new CompositeCommand($"Slice into {previewRects.Count} regions", commands));
            selection.Clear();
            Cancel();
        }

        private void Cancel()
        {
            Hide();
            Closed?.Invoke();
        }

        // --- UI helpers ---

        private void SetBorderColor(Color color)
        {
            style.borderTopColor = color;
            style.borderBottomColor = color;
            style.borderLeftColor = color;
            style.borderRightColor = color;
        }

        private IntegerField MakeIntField(string label, int initialValue)
        {
            var field = new IntegerField(label)
            {
                value = initialValue,
                isDelayed = true,
                style = { flexGrow = 1, flexBasis = 0 }
            };
            field.labelElement.style.minWidth = 38;
            field.RegisterValueChangedCallback(_ => OnParamsChanged());
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
