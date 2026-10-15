using System;
using System.Collections.Generic;
using BetterSpriteEditor.Model;
using BetterSpriteEditor.Operations;
using BetterSpriteEditor.Operations.Shapes;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace BetterSpriteEditor.UI.Inspector
{
    /// <summary>
    /// Inspector for the selection. Multi-edit works Unity-style: fields with a common value show
    /// it, differing fields show the mixed dash; committing a value writes it to every selected
    /// region as ONE history entry. Name editing is single-selection only (names must stay unique).
    /// </summary>
    public class RegionInspectorElement : VisualElement
    {
        private SpriteDocument document;
        private SelectionModel selection;
        private HistoryStack history;

        private Label emptyLabel;
        private Label selectionCountLabel;
        private VisualElement fieldsRoot;
        private Image previewImage;
        private Label previewHintLabel;

        private readonly Dictionary<string, Sprite> spriteCache = new();
        private bool spriteCacheBuilt;
        private bool wasDirty;

        /// <summary>
        /// Starts an external drag of the current selection; returns true when one began.
        /// Set by the window — the inspector only owns the handle, not the drag logic.
        /// </summary>
        public Func<bool> DragSelectionOut;

        /// <summary>
        /// Generates shapes with the given settings. The flag requests every region on the
        /// sheet rather than just the selection.
        /// </summary>
        public Action<ShapeGenerationSettings, bool> GenerateShapes;

        /// <summary>Clears the current shape kind for the selection.</summary>
        public Action ClearShapes;

        /// <summary>Raised when the snap toggle changes, so the editing tool follows it.</summary>
        public Action<bool> SnapToPixelsChanged;

        /// <summary>
        /// Raised whenever a generation setting changes, so the viewport can show what
        /// Generate would produce without committing anything.
        /// </summary>
        public Action<ShapeGenerationSettings> PreviewRequested;

        public ShapeGenerationSettings GetShapeSettings()
        {
            return CurrentShapeSettings();
        }

        /// <summary>
        /// Note about the preview, e.g. that only part of the selection is being previewed.
        /// Empty text hides it. Generate always covers the whole selection regardless.
        /// </summary>
        public void SetPreviewStatus(string text)
        {
            if (previewStatusLabel == null)
                return;
            previewStatusLabel.text = text;
            previewStatusLabel.style.display =
                string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private void NotifyPreview()
        {
            if (shapeMode.HasValue)
                PreviewRequested?.Invoke(CurrentShapeSettings());
        }

        private const string snapPrefsKey = "BetterSpriteEditor.ShapeSnapToPixels";
        private const string outsetPrefsKey = "BetterSpriteEditor.ShapeOutset";
        private const string minBlobPrefsKey = "BetterSpriteEditor.ShapeMinBlob";
        private const string keepExistingPrefsKey = "BetterSpriteEditor.ShapeKeepExisting";
        private const string presetPrefsKey = "BetterSpriteEditor.ShapePreset";
        private const string holesPrefsKey = "BetterSpriteEditor.ShapeDetectHoles";
        private const string sharedPrefsKey = "BetterSpriteEditor.ShapeShared";
        private const string maxPointsPrefsKey = "BetterSpriteEditor.ShapeMaxPoints";
        private const string detailPrefsKey = "BetterSpriteEditor.ShapeDetail";
        private const string alphaPrefsKey = "BetterSpriteEditor.ShapeAlphaTolerance";

        private static string AlphaPrefsKey(ShapeKind kind)
        {
            return $"{alphaPrefsKey}.{kind}";
        }

        /// <summary>Stock defaults: outline keeps every visible pixel, physics ignores faint ones.</summary>
        private static int DefaultAlphaTolerance(ShapeKind kind)
        {
            return kind == ShapeKind.Physics ? 200 : 0;
        }

        private ShapeKind? shapeMode;
        private VisualElement shapeSection;
        private Label shapeInfoLabel;
        private Label previewStatusLabel;
        private Slider shapeDetailSlider;
        private SliderInt alphaToleranceSlider;
        private SliderInt outsetField;
        private SliderInt minBlobField;
        private IntegerField maxPointsField;
        private Toggle snapToggle;
        private Toggle keepExistingToggle;
        private Toggle detectHolesToggle;
        private Toggle sharedShapeToggle;
        private EnumField presetField;

        private ShapeGenerationSettings CurrentShapeSettings()
        {
            return new ShapeGenerationSettings
            {
                Preset = (ShapePreset)presetField.value,
                Detail = shapeDetailSlider.value,
                AlphaTolerance = (byte)Mathf.Clamp(alphaToleranceSlider.value, 0, 254),
                SnapToPixels = snapToggle.value,
                Outset = outsetField.value,
                MinimumBlobSize = minBlobField.value,
                MaxPoints = maxPointsField.value,
                DetectHoles = detectHolesToggle.value,
                SharedShape = sharedShapeToggle.value,
                KeepExisting = keepExistingToggle.value
            };
        }

        /// <summary>Only the traced preset uses simplification and blob filtering.</summary>
        private void RefreshShapeControlVisibility()
        {
            if (presetField == null)
                return;
            bool traced = (ShapePreset)presetField.value == ShapePreset.Traced;
            shapeDetailSlider.style.display = traced ? DisplayStyle.Flex : DisplayStyle.None;
            minBlobField.style.display = traced ? DisplayStyle.Flex : DisplayStyle.None;
            // Box and hull are solid by definition, so holes cannot apply.
            detectHolesToggle.style.display = traced ? DisplayStyle.Flex : DisplayStyle.None;
        }
        private TextField nameField;
        private VisualElement nameInput;
        private Image nameWarningIcon;
        private IntegerField xField;
        private IntegerField yField;
        private IntegerField widthField;
        private IntegerField heightField;
        private EnumField alignmentField;
        private EnumField unitModeField;
        private Vector2Field pivotField;
        private IntegerField borderLeftField;
        private IntegerField borderRightField;
        private IntegerField borderTopField;
        private IntegerField borderBottomField;

        private bool refreshing;
        private bool nameFieldEditing;

        private const string unitModePrefsKey = "BetterSpriteEditor.PivotUnitMode";

        private static PivotUnitMode LoadUnitMode()
        {
            return (PivotUnitMode)EditorPrefs.GetInt(unitModePrefsKey, (int)PivotUnitMode.Normalized);
        }

        private PivotUnitMode UnitMode => (PivotUnitMode)unitModeField.value;

        /// <summary>Pivot as shown in the UI: resolved from alignment, converted to the active unit.</summary>
        private Vector2 GetDisplayPivot(SpriteRegion region)
        {
            var normalized = region.GetNormalizedPivot();
            return UnitMode == PivotUnitMode.Pixels
                ? new Vector2(normalized.x * region.Rect.width, normalized.y * region.Rect.height)
                : normalized;
        }

        public RegionInspectorElement()
        {
            style.paddingLeft = 6;
            style.paddingRight = 6;
            style.paddingTop = 6;

            emptyLabel = new Label("No region selected.")
            {
                style = { unityFontStyleAndWeight = FontStyle.Italic, opacity = 0.7f }
            };
            Add(emptyLabel);

            fieldsRoot = new VisualElement();
            Add(fieldsRoot);

            selectionCountLabel = new Label
            {
                style = { unityFontStyleAndWeight = FontStyle.Bold, marginBottom = 4 }
            };
            fieldsRoot.Add(selectionCountLabel);

            fieldsRoot.Add(BuildPreview());
            fieldsRoot.Add(BuildShapeSection());

            nameField = new TextField("Name") { isDelayed = true, style = { flexGrow = 1 } };
            nameField.RegisterValueChangedCallback(OnNameChanged);
            // Live feedback while typing: the delayed field only commits on Enter/blur, so
            // InputEvent is what tells us the text changed keystroke by keystroke.
            nameField.RegisterCallback<InputEvent>(evt => UpdateNameValidation(evt.newData));
            // Tracked so refreshes (document/selection events firing mid-edit) never
            // overwrite text the user is in the middle of typing.
            nameField.RegisterCallback<FocusInEvent>(_ => nameFieldEditing = true);
            nameField.RegisterCallback<FocusOutEvent>(_ =>
            {
                nameFieldEditing = false;
                Refresh(); // catch up with anything skipped while editing
            });
            nameInput = nameField.Q(TextField.textInputUssName);

            nameWarningIcon = new Image
            {
                image = EditorGUIUtility.IconContent("console.warnicon.sml").image,
                scaleMode = ScaleMode.ScaleToFit,
                style =
                {
                    width = 16,
                    height = 16,
                    alignSelf = Align.Center,
                    marginLeft = 2,
                    display = DisplayStyle.None
                }
            };
            fieldsRoot.Add(MakeFieldRow(nameField, nameWarningIcon));

            fieldsRoot.Add(MakeSectionLabel("Rect"));
            fieldsRoot.Add(MakeFieldRow(
                xField = MakeIntField("X"), yField = MakeIntField("Y")));
            fieldsRoot.Add(MakeFieldRow(
                widthField = MakeIntField("W"), heightField = MakeIntField("H")));

            fieldsRoot.Add(MakeSectionLabel("Border (9-slice)"));
            fieldsRoot.Add(MakeFieldRow(
                borderLeftField = MakeIntField("L"), borderRightField = MakeIntField("R")));
            fieldsRoot.Add(MakeFieldRow(
                borderTopField = MakeIntField("T"), borderBottomField = MakeIntField("B")));

            fieldsRoot.Add(MakeSectionLabel("Pivot"));
            alignmentField = new EnumField("Alignment", SpriteAlignment.Center);
            alignmentField.RegisterValueChangedCallback(OnAlignmentChanged);
            fieldsRoot.Add(alignmentField);

            unitModeField = new EnumField("Unit Mode", LoadUnitMode());
            unitModeField.tooltip = "How pivot values are shown and entered. Storage stays normalized.";
            unitModeField.RegisterValueChangedCallback(evt =>
            {
                EditorPrefs.SetInt(unitModePrefsKey, (int)(PivotUnitMode)evt.newValue);
                Refresh();
            });
            fieldsRoot.Add(unitModeField);

            pivotField = new Vector2Field("Pivot");
            pivotField.RegisterValueChangedCallback(OnPivotChanged);
            // Default composite layout leaves the X/Y inputs very cramped in a narrow panel.
            pivotField.labelElement.style.minWidth = 54;
            pivotField.Query<FloatField>().ForEach(field =>
            {
                field.style.flexGrow = 1;
                field.style.flexBasis = 0;
                field.style.minWidth = 54;
                field.labelElement.style.minWidth = 10;
                field.labelElement.style.marginRight = 2;
            });
            fieldsRoot.Add(pivotField);

            RegisterIntCallback(xField, "Move", (region, value) =>
            {
                var rect = region.Rect;
                rect.x = value;
                region.Rect = rect;
            });
            RegisterIntCallback(yField, "Move", (region, value) =>
            {
                var rect = region.Rect;
                rect.y = value;
                region.Rect = rect;
            });
            RegisterIntCallback(widthField, "Resize", (region, value) =>
            {
                var rect = region.Rect;
                rect.width = value;
                region.Rect = rect;
            });
            RegisterIntCallback(heightField, "Resize", (region, value) =>
            {
                var rect = region.Rect;
                rect.height = value;
                region.Rect = rect;
            });
            RegisterIntCallback(borderLeftField, "Edit border of", (region, value) =>
            {
                var border = region.Border;
                border.x = value;
                region.Border = border;
            });
            RegisterIntCallback(borderRightField, "Edit border of", (region, value) =>
            {
                var border = region.Border;
                border.z = value;
                region.Border = border;
            });
            RegisterIntCallback(borderTopField, "Edit border of", (region, value) =>
            {
                var border = region.Border;
                border.w = value;
                region.Border = border;
            });
            RegisterIntCallback(borderBottomField, "Edit border of", (region, value) =>
            {
                var border = region.Border;
                border.y = value;
                region.Border = border;
            });

            Refresh();
        }

        /// <summary>Shape controls, shown only in the outline and physics shape modes.</summary>
        private VisualElement BuildShapeSection()
        {
            shapeSection = new VisualElement { style = { display = DisplayStyle.None } };

            shapeSection.Add(MakeSectionLabel("Shape"));
            shapeInfoLabel = new Label { style = { opacity = 0.75f, whiteSpace = WhiteSpace.Normal } };
            shapeSection.Add(shapeInfoLabel);

            previewStatusLabel = new Label
            {
                style =
                {
                    color = new Color(1f, 0.6f, 0.3f),
                    whiteSpace = WhiteSpace.Normal,
                    display = DisplayStyle.None
                }
            };
            shapeSection.Add(previewStatusLabel);

            presetField = new EnumField("Shape", ShapePreset.Traced)
            {
                tooltip = "Traced follows the alpha; Box, Ellipse and Convex Hull need no tracing."
            };
            presetField.RegisterValueChangedCallback(evt =>
            {
                EditorPrefs.SetInt(presetPrefsKey, (int)(ShapePreset)evt.newValue);
                RefreshShapeControlVisibility();
                NotifyPreview();
            });
            presetField.SetValueWithoutNotify(
                (ShapePreset)EditorPrefs.GetInt(presetPrefsKey, (int)ShapePreset.Traced));
            shapeSection.Add(presetField);

            shapeDetailSlider = new Slider("Detail", 0f, 1f)
            {
                value = EditorPrefs.GetFloat(detailPrefsKey, 0.5f),
                showInputField = true,
                tooltip = "How closely a generated shape follows the sprite's alpha. " +
                          "Low values give a handful of points and cut corners; " +
                          "high values follow the pixels."
            };
            shapeDetailSlider.RegisterValueChangedCallback(evt =>
            {
                EditorPrefs.SetFloat(detailPrefsKey, evt.newValue);
                NotifyPreview();
            });
            shapeSection.Add(shapeDetailSlider);

            alphaToleranceSlider = new SliderInt("Alpha Tolerance", 0, 254)
            {
                showInputField = true,
                tooltip = "Pixels with alpha at or below this value count as transparent. " +
                          "Raise it to ignore soft edges."
            };
            alphaToleranceSlider.RegisterValueChangedCallback(evt =>
            {
                if (shapeMode.HasValue)
                    EditorPrefs.SetInt(AlphaPrefsKey(shapeMode.Value), evt.newValue);
                NotifyPreview();
            });
            shapeSection.Add(alphaToleranceSlider);

            outsetField = new SliderInt("Outset", 0, 8)
            {
                value = EditorPrefs.GetInt(outsetPrefsKey, 1),
                showInputField = true,
                tooltip = "How far outside the visible pixels the shape sits, in pixels. " +
                          "Unity's own Custom Outline uses about 1; its physics shape uses 0. " +
                          "A larger outset also allows more simplification."
            };
            outsetField.RegisterValueChangedCallback(evt =>
            {
                EditorPrefs.SetInt(outsetPrefsKey, evt.newValue);
                NotifyPreview();
            });
            shapeSection.Add(outsetField);

            minBlobField = new SliderInt("Min Blob Size", 1, 64)
            {
                value = EditorPrefs.GetInt(minBlobPrefsKey, 4),
                showInputField = true,
                tooltip = "Ignore opaque blobs smaller than this many pixels, so stray specks " +
                          "on antialiased art do not become their own shapes."
            };
            minBlobField.RegisterValueChangedCallback(evt =>
            {
                EditorPrefs.SetInt(minBlobPrefsKey, evt.newValue);
                NotifyPreview();
            });
            shapeSection.Add(minBlobField);

            maxPointsField = new IntegerField("Point Target")
            {
                value = EditorPrefs.GetInt(maxPointsPrefsKey, 0),
                isDelayed = true,
                tooltip = "Requested point count; 0 means unlimited. Disconnected paths or preserved geometry " +
                          "may require more points. Preview reports the actual result."
            };
            maxPointsField.RegisterValueChangedCallback(evt =>
            {
                int clamped = evt.newValue <= 0 ? 0 : Mathf.Max(3, evt.newValue);
                if (clamped != evt.newValue)
                    maxPointsField.SetValueWithoutNotify(clamped);
                EditorPrefs.SetInt(maxPointsPrefsKey, clamped);
                NotifyPreview();
            });
            shapeSection.Add(maxPointsField);

            detectHolesToggle = new Toggle("Detect Holes")
            {
                value = EditorPrefs.GetBool(holesPrefsKey, true),
                tooltip = "Cut enclosed transparent areas out of the shape. " +
                          "Without it a ring or a window frame comes out solid."
            };
            detectHolesToggle.RegisterValueChangedCallback(evt =>
            {
                EditorPrefs.SetBool(holesPrefsKey, evt.newValue);
                NotifyPreview();
            });
            shapeSection.Add(detectHolesToggle);

            sharedShapeToggle = new Toggle("Shared Shape")
            {
                value = EditorPrefs.GetBool(sharedPrefsKey, false),
                tooltip = "Generate one shape from all selected regions combined and give it " +
                          "to each of them, aligned by pivot. Keeps a collider steady across " +
                          "animation frames instead of twitching frame to frame."
            };
            sharedShapeToggle.RegisterValueChangedCallback(evt =>
            {
                EditorPrefs.SetBool(sharedPrefsKey, evt.newValue);
                NotifyPreview();
            });
            shapeSection.Add(sharedShapeToggle);

            keepExistingToggle = new Toggle("Keep Existing")
            {
                value = EditorPrefs.GetBool(keepExistingPrefsKey, true),
                tooltip = "When generating across the whole sheet, leave regions that already " +
                          "have a shape untouched. An explicit selection is always regenerated."
            };
            keepExistingToggle.RegisterValueChangedCallback(evt =>
                EditorPrefs.SetBool(keepExistingPrefsKey, evt.newValue));
            shapeSection.Add(keepExistingToggle);

            snapToggle = new Toggle("Snap To Pixels")
            {
                value = EditorPrefs.GetBool(snapPrefsKey, true),
                tooltip = "Keep points on whole pixels, both when generating and when dragging."
            };
            snapToggle.RegisterValueChangedCallback(evt =>
            {
                EditorPrefs.SetBool(snapPrefsKey, evt.newValue);
                SnapToPixelsChanged?.Invoke(evt.newValue);
                NotifyPreview();
            });
            shapeSection.Add(snapToggle);

            var buttonRow = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 2 } };
            buttonRow.Add(new Button(() => GenerateShapes?.Invoke(CurrentShapeSettings(), false))
            {
                text = "Generate",
                tooltip = "Generate shapes for the selected regions " +
                          "(the whole sheet when nothing is selected).",
                style = { flexGrow = 1 }
            });
            buttonRow.Add(new Button(() => GenerateShapes?.Invoke(CurrentShapeSettings(), true))
            {
                text = "Generate All",
                tooltip = "Generate shapes for every region on the sheet.",
                style = { flexGrow = 1 }
            });
            buttonRow.Add(new Button(() => ClearShapes?.Invoke())
            {
                text = "Clear",
                style = { flexGrow = 1 }
            });
            shapeSection.Add(buttonRow);

            RefreshShapeControlVisibility();

            shapeSection.Add(new Label(
                "Drag outside the sprite to select regions; Shift-click adds one. Drag inside " +
                "it to select points (Shift adds), then move or Delete them together. Click an " +
                "edge to insert a point. Ctrl+drag draws a new shape.")
            {
                style = { opacity = 0.6f, whiteSpace = WhiteSpace.Normal, marginTop = 4 }
            });

            return shapeSection;
        }

        /// <summary>Null hides the shape section; a kind shows it for that shape set.</summary>
        public void SetShapeMode(ShapeKind? kind)
        {
            shapeMode = kind;
            if (shapeSection == null)
                return;
            shapeSection.style.display = kind.HasValue ? DisplayStyle.Flex : DisplayStyle.None;
            if (kind.HasValue)
            {
                RefreshShapeControlVisibility();
                // Each shape kind remembers its own tolerance, as the stock modules do.
                alphaToleranceSlider.SetValueWithoutNotify(
                    EditorPrefs.GetInt(AlphaPrefsKey(kind.Value), DefaultAlphaTolerance(kind.Value)));
            }
            Refresh();
        }

        private void RefreshShapeSection(List<SpriteRegion> regions)
        {
            if (!shapeMode.HasValue || shapeSection.style.display == DisplayStyle.None)
                return;

            int pathCount = 0;
            int pointCount = 0;
            foreach (var region in regions)
            {
                foreach (var path in region.GetShapes(shapeMode.Value))
                {
                    pathCount++;
                    pointCount += path.Length;
                }
            }
            string kindName = shapeMode.Value == ShapeKind.Physics ? "Physics shape" : "Custom outline";
            shapeInfoLabel.text = pathCount == 0
                ? $"{kindName}: none yet — Generate creates one from the sprite's alpha."
                : $"{kindName}: {pathCount} path(s), {pointCount} point(s).";

            var active = document?.FindRegion(selection?.ActiveId ?? default);
            if (active != null)
                shapeDetailSlider.SetValueWithoutNotify(active.GetTessellation(shapeMode.Value));
        }

        /// <summary>
        /// Sprite preview that doubles as a drag handle: dragging it drops the selected sprites
        /// into the scene, the hierarchy or a sprite field, exactly like a Project-window drag.
        /// </summary>
        private VisualElement BuildPreview()
        {
            var container = new VisualElement { style = { alignItems = Align.Center, marginBottom = 6 } };

            previewImage = new Image
            {
                scaleMode = ScaleMode.ScaleToFit,
                tooltip = "Drag into the scene, the hierarchy or a sprite field.",
                style =
                {
                    width = 72,
                    height = 72,
                    borderTopWidth = 1,
                    borderBottomWidth = 1,
                    borderLeftWidth = 1,
                    borderRightWidth = 1,
                    borderTopColor = new Color(0.1f, 0.1f, 0.1f),
                    borderBottomColor = new Color(0.1f, 0.1f, 0.1f),
                    borderLeftColor = new Color(0.1f, 0.1f, 0.1f),
                    borderRightColor = new Color(0.1f, 0.1f, 0.1f),
                    backgroundColor = new Color(0.16f, 0.16f, 0.16f)
                }
            };
            // Started on press, before any capture — Unity's drag manager never sees a
            // captured pointer. See the drag-out research for why this cannot be deferred.
            previewImage.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (evt.button != 0 || DragSelectionOut == null)
                    return;
                if (DragSelectionOut())
                    evt.StopPropagation();
            });
            container.Add(previewImage);

            previewHintLabel = new Label
            {
                style = { opacity = 0.6f, marginTop = 2, whiteSpace = WhiteSpace.Normal }
            };
            container.Add(previewHintLabel);

            return container;
        }

        /// <summary>
        /// Sprite sub-assets by name. Refresh runs on every drag frame, so this never reloads
        /// on a miss — the cache is invalidated when an Apply lands instead (see Refresh).
        /// </summary>
        private Sprite FindSprite(string regionName)
        {
            if (document == null || string.IsNullOrEmpty(regionName))
                return null;

            if (!spriteCacheBuilt)
            {
                spriteCache.Clear();
                foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(document.AssetPath))
                {
                    if (asset is Sprite sprite)
                        spriteCache[sprite.name] = sprite;
                }
                spriteCacheBuilt = true;
            }
            return spriteCache.TryGetValue(regionName, out var found) ? found : null;
        }

        private void RefreshPreview(List<SpriteRegion> regions)
        {
            var active = document?.FindRegion(selection?.ActiveId ?? default) ?? regions[0];
            var sprite = FindSprite(active.Name);
            previewImage.sprite = sprite;

            if (sprite == null)
                previewHintLabel.text = "No sprite asset yet — apply changes to drag this region.";
            else if (regions.Count > 1)
                previewHintLabel.text = $"Drag to use these {regions.Count} sprites";
            else
                previewHintLabel.text = "Drag to use this sprite";
        }

        public void Bind(SpriteDocument newDocument, SelectionModel newSelection, HistoryStack newHistory)
        {
            if (document != null)
                document.Changed -= Refresh;
            if (selection != null)
                selection.Changed -= Refresh;

            document = newDocument;
            selection = newSelection;
            history = newHistory;
            spriteCache.Clear();
            spriteCacheBuilt = false;

            if (document != null)
                document.Changed += Refresh;
            if (selection != null)
                selection.Changed += Refresh;

            Refresh();
        }

        /// <summary>Selected regions in document order (stable for aggregation and labels).</summary>
        private List<SpriteRegion> GetSelectedRegions()
        {
            var regions = new List<SpriteRegion>();
            if (document == null || selection == null)
                return regions;
            foreach (var region in document.Regions)
            {
                if (selection.IsSelected(region.Id))
                    regions.Add(region);
            }
            return regions;
        }

        /// <summary>Re-reads all field values from the model (safe to call during a drag).</summary>
        public void Refresh()
        {
            // An Apply just landed: sub-assets may have been created or renamed on disk.
            bool dirtyNow = document is { IsDirty: true };
            if (wasDirty && !dirtyNow)
                spriteCacheBuilt = false;
            wasDirty = dirtyNow;

            refreshing = true;
            try
            {
                var regions = GetSelectedRegions();
                bool hasSelection = regions.Count > 0;
                emptyLabel.style.display = hasSelection ? DisplayStyle.None : DisplayStyle.Flex;
                fieldsRoot.style.display = hasSelection ? DisplayStyle.Flex : DisplayStyle.None;
                if (!hasSelection)
                    return;

                bool multi = regions.Count > 1;
                selectionCountLabel.text = multi ? $"{regions.Count} regions selected" : string.Empty;
                selectionCountLabel.style.display = multi ? DisplayStyle.Flex : DisplayStyle.None;

                RefreshPreview(regions);
                RefreshShapeSection(regions);

                // Name: single-selection only, duplicates are impossible to express for many.
                nameField.SetEnabled(!multi && document.CanEditName);
                foreach (var field in new[] { xField, yField, widthField, heightField })
                    field.SetEnabled(document.CanEditRect);
                foreach (var field in new[] { borderLeftField, borderRightField, borderTopField, borderBottomField })
                    field.SetEnabled(document.Capabilities.Has(SpriteEditCapabilities.EditBorder));
                alignmentField.SetEnabled(document.Capabilities.Has(SpriteEditCapabilities.EditPivot));
                if (!nameFieldEditing)
                {
                    nameField.SetValueWithoutNotify(multi ? string.Empty : regions[0].Name);
                    nameField.showMixedValue = multi;
                    UpdateNameValidation(nameField.value);
                }

                SetIntField(xField, regions, region => (int)region.Rect.x);
                SetIntField(yField, regions, region => (int)region.Rect.y);
                SetIntField(widthField, regions, region => (int)region.Rect.width);
                SetIntField(heightField, regions, region => (int)region.Rect.height);
                SetIntField(borderLeftField, regions, region => (int)region.Border.x);
                SetIntField(borderRightField, regions, region => (int)region.Border.z);
                SetIntField(borderTopField, regions, region => (int)region.Border.w);
                SetIntField(borderBottomField, regions, region => (int)region.Border.y);

                var alignment = regions[0].Alignment;
                bool alignmentMixed = regions.Exists(region => region.Alignment != alignment);
                alignmentField.SetValueWithoutNotify(alignment);
                alignmentField.showMixedValue = alignmentMixed;

                var displayPivot = GetDisplayPivot(regions[0]);
                bool pivotMixed = regions.Exists(region => GetDisplayPivot(region) != displayPivot);
                pivotField.SetValueWithoutNotify(displayPivot);
                pivotField.showMixedValue = pivotMixed;
                pivotField.label = UnitMode == PivotUnitMode.Pixels ? "Pivot (px)" : "Pivot";
                pivotField.SetEnabled(document.Capabilities.Has(SpriteEditCapabilities.EditPivot)
                    && !alignmentMixed && alignment == SpriteAlignment.Custom);
            }
            finally
            {
                refreshing = false;
            }
        }

        private static void SetIntField(IntegerField field, List<SpriteRegion> regions,
            Func<SpriteRegion, int> getter)
        {
            int first = getter(regions[0]);
            bool mixed = regions.Exists(region => getter(region) != first);
            field.SetValueWithoutNotify(first);
            field.showMixedValue = mixed;
        }

        /// <summary>
        /// Applies the mutation to every selected region (clone → mutate → clamp) and records
        /// the changes as one history entry.
        /// </summary>
        private void ApplyEdit(string labelPrefix, Action<SpriteRegion> mutate)
        {
            var regions = GetSelectedRegions();
            if (regions.Count == 0 || refreshing)
                return;

            string label = regions.Count == 1
                ? $"{labelPrefix} '{regions[0].Name}'"
                : $"{labelPrefix} {regions.Count} regions";

            var commands = new List<IEditorCommand>();
            foreach (var region in regions)
            {
                var before = region.Clone();
                mutate(region);
                RegionValidator.Clamp(region, document.TextureWidth, document.TextureHeight);
                if (!RegionsEqual(before, region))
                    commands.Add(new ModifyRegionCommand(document, region.Id, before, region.Clone(), label));
            }

            if (commands.Count == 0)
            {
                Refresh(); // out-of-range input snapped back to the same values
                return;
            }
            history.PushApplied(commands.Count == 1
                ? commands[0]
                : new CompositeCommand(label, commands));
        }

        private void OnNameChanged(ChangeEvent<string> evt)
        {
            if (refreshing)
                return;
            var regions = GetSelectedRegions();
            if (regions.Count != 1)
                return;

            if (!RegionValidator.IsNameAvailable(document, regions[0], evt.newValue))
            {
                // Empty or duplicate name — reject. The text stays in the field with the warning
                // visible while it has focus; losing focus restores the region's real name.
                UpdateNameValidation(evt.newValue);
                Refresh();
                return;
            }
            ApplyEdit("Rename", region => region.Name = evt.newValue);
        }

        private static readonly Color nameWarningColor = new Color(1f, 0.6f, 0.1f);

        /// <summary>
        /// Shows or hides the "name is taken / empty" warning for the typed name: orange border on
        /// the input plus a warning icon whose tooltip explains the problem.
        /// </summary>
        private void UpdateNameValidation(string typedName)
        {
            var regions = GetSelectedRegions();
            string problem = null;
            if (regions.Count == 1 && document != null)
            {
                if (string.IsNullOrWhiteSpace(typedName))
                    problem = "Name must not be empty.";
                else if (!RegionValidator.IsNameAvailable(document, regions[0], typedName))
                    problem = $"Name '{typedName}' is already used by another region.";
            }

            bool invalid = problem != null;
            nameWarningIcon.style.display = invalid ? DisplayStyle.Flex : DisplayStyle.None;
            nameWarningIcon.tooltip = problem ?? string.Empty;
            nameField.tooltip = invalid ? problem : regions.Count > 1
                ? "Names are edited one region at a time."
                : string.Empty;

            var border = invalid ? new StyleColor(nameWarningColor) : new StyleColor(StyleKeyword.Null);
            nameInput.style.borderTopColor = border;
            nameInput.style.borderBottomColor = border;
            nameInput.style.borderLeftColor = border;
            nameInput.style.borderRightColor = border;
        }

        private void OnAlignmentChanged(ChangeEvent<Enum> evt)
        {
            if (refreshing)
                return;

            var newAlignment = (SpriteAlignment)evt.newValue;
            ApplyEdit("Set alignment of", region =>
            {
                // Switching to Custom must keep the pivot where it is, not restore a stale vector.
                if (newAlignment == SpriteAlignment.Custom)
                    region.Pivot = region.GetNormalizedPivot();
                region.Alignment = newAlignment;
            });
        }

        private void OnPivotChanged(ChangeEvent<Vector2> evt)
        {
            if (refreshing)
                return;

            // In Pixels mode each region converts against its own size, so the same pixel
            // offset stays visually identical across differently sized regions.
            ApplyEdit("Set pivot of", region => region.Pivot = UnitMode == PivotUnitMode.Pixels
                ? new Vector2(
                    region.Rect.width > 0f ? evt.newValue.x / region.Rect.width : 0f,
                    region.Rect.height > 0f ? evt.newValue.y / region.Rect.height : 0f)
                : evt.newValue);
        }

        private void RegisterIntCallback(IntegerField field, string labelPrefix, Action<SpriteRegion, int> setter)
        {
            field.RegisterValueChangedCallback(evt =>
            {
                if (refreshing)
                    return;
                ApplyEdit(labelPrefix, region => setter(region, evt.newValue));
            });
        }

        private static bool RegionsEqual(SpriteRegion a, SpriteRegion b)
        {
            return a.Name == b.Name
                   && a.Rect == b.Rect
                   && a.Alignment == b.Alignment
                   && a.Pivot == b.Pivot
                   && a.Border == b.Border;
        }

        private static IntegerField MakeIntField(string label)
        {
            var field = new IntegerField(label) { isDelayed = true, style = { flexGrow = 1, flexBasis = 0 } };
            field.labelElement.style.minWidth = 16;
            return field;
        }

        private static VisualElement MakeFieldRow(params VisualElement[] fields)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            foreach (var field in fields)
                row.Add(field);
            return row;
        }

        private static Label MakeSectionLabel(string text)
        {
            return new Label(text)
            {
                style =
                {
                    unityFontStyleAndWeight = FontStyle.Bold,
                    marginTop = 8,
                    marginBottom = 2
                }
            };
        }
    }
}
