using System;
using System.Collections.Generic;
using BetterSpriteEditor.DataAccess;
using BetterSpriteEditor.Input;
using BetterSpriteEditor.Model;
using BetterSpriteEditor.Operations;
using BetterSpriteEditor.Operations.Clipboard;
using BetterSpriteEditor.Operations.Shapes;
using BetterSpriteEditor.Operations.Usages;
using BetterSpriteEditor.Tools;
using BetterSpriteEditor.UI.Inspector;
using BetterSpriteEditor.UI.Viewport;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace BetterSpriteEditor.UI
{
    /// <summary>
    /// Main window of the Better Sprite Editor. A module selector switches between rect
    /// editing (Select/Create tools, slicing, combine, rename) and the two shape modes
    /// (custom outline and physics shape), which hand the viewport to the shape tool.
    /// </summary>
    public class BetterSpriteEditorWindow : EditorWindow
    {
        private const string windowTitle = "Better Sprite Editor";

        /// <summary>Mirrors the stock editor's module dropdown.</summary>
        private enum EditorMode
        {
            [InspectorName("Sprite Editor")] SpriteEditor,
            [InspectorName("Custom Outline")] CustomOutline,
            [InspectorName("Custom Physics Shape")] CustomPhysicsShape
        }

        private enum ToolMode
        {
            Select,
            Create
        }

        [SerializeField] private Texture2D targetTexture;

        // Survives domain reloads (the document does not): distinguishes "same session,
        // restore silently" from "fresh open after a crash, ask first".
        [SerializeField] private bool hadUnappliedChanges;

        private SpriteDocument document;
        private SelectionModel selection;
        private HistoryStack history;
        private ToolContext toolContext;
        private SelectTool selectTool;
        private CreateTool createTool;
        private ShapeTool shapeTool;
        private ToolMode toolMode = ToolMode.Select;
        private EditorMode editorMode = EditorMode.SpriteEditor;

        // Guards against double-handling when both the command event and the raw key arrive.
        private double lastUndoRedoTime;

        private readonly HashSet<GUID> unusedRegionIds = new();
        private TextureAlphaMap cachedAlphaMap;

        private const long previewPixelBudget = 1_500_000;
        private const long previewDebounceMilliseconds = 40;
        private IVisualElementScheduledItem previewSchedule;
        private ShapeGenerationSettings pendingPreviewSettings;

        private Label infoLabel;
        private Label zoomLabel;
        private HelpBox messageBox;
        private VisualElement contentSplit;
        private SpriteViewportElement viewport;
        private RegionInspectorElement inspector;
        private EnumField modeField;
        private ToolbarToggle selectToggle;
        private ToolbarToggle createToggle;
        private ToolbarToggle alphaToggle;
        private ToolbarButton fitButton;
        private ToolbarButton oneToOneButton;
        private ToolbarButton combineButton;
        private ToolbarButton renameButton;
        private ToolbarButton usagesButton;
        private ToolbarToggle sliceToggle;
        private ToolbarButton splitButton;
        private ToolbarButton alignPivotsButton;
        private ToolbarButton boundsButton;
        private SlicePanelElement slicePanel;
        private AlignPivotsPanelElement alignPivotsPanel;
        private BoundsPanelElement boundsPanel;
        private PropertyPastePanelElement propertyPastePanel;
        private ToolbarButton copyPropertiesButton;
        private ToolbarButton pastePropertiesButton;
        private HistoryPanelElement historyPanel;
        private RegionListPanel regionList;
        private TwoPaneSplitView listSplit;
        private ToolbarToggle listToggle;

        private const string showListPrefsKey = "BetterSpriteEditor.ShowRegionList";
        private ToolbarButton undoButton;
        private ToolbarButton redoButton;
        private ToolbarButton applyButton;
        private ToolbarButton revertButton;

        [MenuItem("Window/2D/Better Sprite Editor")]
        public static void OpenFromMenu()
        {
            Open(Selection.activeObject as Texture2D);
        }

        [MenuItem("Assets/Open in Better Sprite Editor", true)]
        private static bool ValidateOpenFromAssets()
        {
            return Selection.activeObject is Texture2D;
        }

        [MenuItem("Assets/Open in Better Sprite Editor")]
        private static void OpenFromAssets()
        {
            Open(Selection.activeObject as Texture2D);
        }

        [MenuItem("CONTEXT/TextureImporter/Open in Better Sprite Editor")]
        private static void OpenFromImporterContext(MenuCommand command)
        {
            if (command.context is not TextureImporter importer)
                return;

            Open(AssetDatabase.LoadAssetAtPath<Texture2D>(importer.assetPath));
        }

        public static void Open(Texture2D texture)
        {
            var window = GetWindow<BetterSpriteEditorWindow>();
            window.titleContent = new GUIContent(windowTitle);
            if (texture != null)
                window.SetTarget(texture);
            window.Show();
        }

        /// <summary>Same guard for the window regaining OS/tab focus with nothing selected.</summary>
        private void OnFocus()
        {
            rootVisualElement?.schedule.Execute(() =>
            {
                if (rootVisualElement.panel?.focusController?.focusedElement == null)
                    rootVisualElement.Focus();
            });
        }

        private void OnDisable()
        {
            boundsPanel?.Hide();
            propertyPastePanel?.Hide();
            AssemblyReloadEvents.beforeAssemblyReload -= SaveRecoverySnapshot;
            SpriteAssetPostprocessor.AssetsChanged -= CheckExternalChanges;
            ShortcutRegistry.Changed -= RefreshShortcutTooltips;
            SpritePropertyClipboard.Changed -= RefreshClipboardButtons;
            SpriteUsagesWindow.SearchCompleted -= OnUsageSearchCompleted;
            SpriteUsagesWindow.Closed -= ClearUnusedHighlight;
        }

        /// <summary>The highlight only means something while its results are on screen.</summary>
        private void ClearUnusedHighlight()
        {
            if (unusedRegionIds.Count == 0)
                return;
            unusedRegionIds.Clear();
            viewport?.SetUnusedRegions(unusedRegionIds);
            regionList?.RefreshUnusedMarkers();
        }

        /// <summary>
        /// Replace no-reference markers with one result scope. IDs survive unapplied renames;
        /// incomplete searches clear earlier markers rather than presenting them as current.
        /// </summary>
        private void OnUsageSearchCompleted(string searchedTexturePath,
            IReadOnlyCollection<string> searchedNames, IReadOnlyCollection<string> unusedNames, UsageScope scope)
        {
            if (document == null || searchedTexturePath != document.AssetPath)
                return;

            unusedRegionIds.Clear();
            regionList?.SetUsageScope(scope == UsageScope.CurrentScene ? "loaded scenes" : "scanned project assets");
            var unused = new HashSet<string>(unusedNames);
            foreach (var name in searchedNames)
            {
                var region = document.Regions.Find(candidate => candidate.Id.ToString() == name);
                if (region == null)
                    continue;
                if (unused.Contains(name))
                    unusedRegionIds.Add(region.Id);
                else
                    unusedRegionIds.Remove(region.Id);
            }
            viewport.SetUnusedRegions(unusedRegionIds);
            regionList?.RefreshUnusedMarkers();
        }

        /// <summary>
        /// Called by Unity's close-with-unsaved-changes dialog (Save/Discard/Cancel; Cancel
        /// aborts the close automatically). Throwing keeps the window open on failure.
        /// </summary>
        public override void SaveChanges()
        {
            if (document is { IsDirty: true })
            {
                SpriteDocumentRepository.Save(document);
                history.MarkSavePoint();
            }
            ClearRecovery();
            base.SaveChanges();
        }

        public override void DiscardChanges()
        {
            // The document is in-memory only — nothing on disk to roll back.
            ClearRecovery();
            base.DiscardChanges();
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;

            var toolbar = new Toolbar();
            root.Add(toolbar);

            // Top-left module selector, like the stock Sprite Editor window.
            modeField = new EnumField(EditorMode.SpriteEditor) { style = { width = 150 } };
            modeField.tooltip = "Switch between rect editing, custom outline and physics shape.";
            modeField.RegisterValueChangedCallback(evt => SetEditorMode((EditorMode)evt.newValue));
            toolbar.Add(modeField);

            listToggle = new ToolbarToggle
            {
                text = "List",
                value = EditorPrefs.GetBool(showListPrefsKey, true),
                tooltip = "Show the region list panel"
            };
            listToggle.RegisterValueChangedCallback(evt =>
            {
                EditorPrefs.SetBool(showListPrefsKey, evt.newValue);
                if (evt.newValue)
                    listSplit.UnCollapse();
                else
                    listSplit.CollapseChild(0);
            });
            toolbar.Add(listToggle);

            toolbar.Add(new ToolbarSpacer());

            selectToggle = new ToolbarToggle { text = "Select" };
            selectToggle.RegisterValueChangedCallback(_ => SetTool(ToolMode.Select));
            toolbar.Add(selectToggle);

            createToggle = new ToolbarToggle { text = "Create" };
            createToggle.RegisterValueChangedCallback(_ => SetTool(ToolMode.Create));
            toolbar.Add(createToggle);

            combineButton = new ToolbarButton(CombineSelectedRegions) { text = "Combine" };
            toolbar.Add(combineButton);

            renameButton = new ToolbarButton(RenameSelectedRegions) { text = "Rename" };
            toolbar.Add(renameButton);

            usagesButton = new ToolbarButton(FindUsagesOfSelection) { text = "Usages" };
            toolbar.Add(usagesButton);

            sliceToggle = new ToolbarToggle { text = "Slice" };
            sliceToggle.RegisterValueChangedCallback(evt =>
            {
                if (evt.newValue && document is { CanChangeRegions: true })
                {
                    // The overlay panels share the viewport's top-left corner — one at a time.
                    alignPivotsPanel.Hide();
                    boundsPanel.Hide();
                    propertyPastePanel?.Hide();
                    slicePanel.Show(document, history, selection, viewport);
                }
                else
                    slicePanel.Hide();
                if (evt.newValue && document is not { CanChangeRegions: true })
                    sliceToggle.SetValueWithoutNotify(false);
            });
            toolbar.Add(sliceToggle);

            splitButton = new ToolbarButton(SplitActiveRegion)
            {
                text = "Split",
                tooltip = "Split the active region into a grid of smaller regions"
            };
            toolbar.Add(splitButton);

            boundsButton = new ToolbarButton(EditSelectedBounds)
            {
                text = "Bounds",
                tooltip = "Trim transparency or use uniform frame bounds while preserving pivot anchors and shapes"
            };
            toolbar.Add(boundsButton);
            copyPropertiesButton = new ToolbarButton(CopyProperties) { text = "Copy Properties", name = "copy-properties" };
            copyPropertiesButton.tooltip = "Copy the properties of one selected sprite, including unapplied edits";
            toolbar.Add(copyPropertiesButton);
            pastePropertiesButton = new ToolbarButton(PasteProperties) { text = "Paste Properties…", name = "paste-properties" };
            toolbar.Add(pastePropertiesButton);
            RefreshClipboardButtons();

            alignPivotsButton = new ToolbarButton(AlignPivotsOfSelection)
            {
                text = "Pivots",
                tooltip = "Align pivots across the selected frames so animations stop jittering " +
                          "(select 2 or more)"
            };
            toolbar.Add(alignPivotsButton);

            toolbar.Add(new ToolbarSpacer());

            fitButton = new ToolbarButton(() => viewport.FitToWindow()) { text = "Fit" };
            toolbar.Add(fitButton);
            oneToOneButton = new ToolbarButton(() => viewport.SetZoomOneToOne()) { text = "1:1" };
            toolbar.Add(oneToOneButton);
            alphaToggle = new ToolbarToggle { text = "Alpha" };
            alphaToggle.RegisterValueChangedCallback(evt => viewport.SetAlphaView(evt.newValue));
            toolbar.Add(alphaToggle);

            toolbar.Add(new ToolbarSpacer());

            undoButton = new ToolbarButton(() => TryUndo()) { text = "Undo" };
            toolbar.Add(undoButton);
            redoButton = new ToolbarButton(() => TryRedo()) { text = "Redo" };
            toolbar.Add(redoButton);

            toolbar.Add(new ToolbarSpacer { style = { flexGrow = 1 } });

            infoLabel = new Label { style = { unityTextAlign = TextAnchor.MiddleRight, marginRight = 8 } };
            toolbar.Add(infoLabel);
            zoomLabel = new Label
            {
                style = { unityTextAlign = TextAnchor.MiddleRight, minWidth = 52, marginRight = 4 }
            };
            toolbar.Add(zoomLabel);

            revertButton = new ToolbarButton(RevertDocument) { text = "Revert" };
            toolbar.Add(revertButton);
            applyButton = new ToolbarButton(ApplyDocument) { text = "Apply" };
            toolbar.Add(applyButton);

            messageBox = new HelpBox(string.Empty, HelpBoxMessageType.Info);
            root.Add(messageBox);

            var split = new TwoPaneSplitView(1, 280, TwoPaneSplitViewOrientation.Horizontal)
            {
                viewDataKey = "bse-inspector-split"
            };
            viewport = new SpriteViewportElement();
            viewport.ViewChanged += RefreshZoomLabel;
            viewport.DragOutHandler = TryStartDragOut;
            var viewportHost = new VisualElement { style = { flexGrow = 1 } };
            viewportHost.Add(viewport);
            slicePanel = new SlicePanelElement();
            slicePanel.Closed += () => sliceToggle.SetValueWithoutNotify(false);
            viewportHost.Add(slicePanel);
            alignPivotsPanel = new AlignPivotsPanelElement();
            viewportHost.Add(alignPivotsPanel);
            boundsPanel = new BoundsPanelElement();
            viewportHost.Add(boundsPanel);
            propertyPastePanel = new PropertyPastePanelElement();
            viewportHost.Add(propertyPastePanel);
            viewport.OnionAnchorDragged += alignPivotsPanel.SetAnchorFromViewportDrag;
            split.Add(viewportHost);

            var rightPane = new VisualElement();
            inspector = new RegionInspectorElement { style = { flexGrow = 1 } };
            inspector.DragSelectionOut = TryStartDragOutOfSelection;
            inspector.GenerateShapes = GenerateShapesForSelection;
            inspector.ClearShapes = ClearShapesForSelection;
            inspector.KeepPivotPositionChanged = keep =>
            {
                if (toolContext != null)
                    toolContext.KeepPivotPosition = keep;
            };
            inspector.SnapToPixelsChanged = snap =>
            {
                if (shapeTool != null)
                    shapeTool.SnapToPixels = snap;
            };
            inspector.PreviewRequested = UpdateShapePreview;
            rightPane.Add(inspector);
            historyPanel = new HistoryPanelElement();
            rightPane.Add(historyPanel);
            split.Add(rightPane);

            regionList = new RegionListPanel();
            var outerSplit = new TwoPaneSplitView(0, 190, TwoPaneSplitViewOrientation.Horizontal)
            {
                viewDataKey = "bse-list-split"
            };
            outerSplit.Add(regionList);
            outerSplit.Add(split);
            contentSplit = outerSplit;
            root.Add(outerSplit);

            listSplit = outerSplit;
            if (!EditorPrefs.GetBool(showListPrefsKey, true))
                outerSplit.CollapseChild(0);

            // Undo/redo interception: command events are the official path (they also block the
            // global editor undo while our window is focused); the raw-key fallback covers cases
            // where no command event is generated. The time guard prevents double handling.
            root.RegisterCallback<ValidateCommandEvent>(OnValidateCommand, TrickleDown.TrickleDown);
            root.RegisterCallback<ExecuteCommandEvent>(OnExecuteCommand, TrickleDown.TrickleDown);
            root.RegisterCallback<KeyDownEvent>(OnRootKeyDown, TrickleDown.TrickleDown);

            // Keyboard events only enter a panel while something in it has focus. Clicking
            // padding, labels or panel chrome focuses nothing — and then Ctrl+Z falls
            // through to the editor's GLOBAL undo (reverting scene changes). So after any
            // click that leaves the panel focusless, the root adopts the focus. Deferred a
            // tick, because the click's own focus handling must settle first.
            root.focusable = true;
            root.RegisterCallback<PointerDownEvent>(_ =>
                root.schedule.Execute(() =>
                {
                    if (root.panel?.focusController?.focusedElement == null)
                        root.Focus();
                }));

            ShortcutRegistry.Changed += RefreshShortcutTooltips;
            SpritePropertyClipboard.Changed += RefreshClipboardButtons;
            SpriteUsagesWindow.SearchCompleted += OnUsageSearchCompleted;
            SpriteUsagesWindow.Closed += ClearUnusedHighlight;

            // Reload survival: a guaranteed snapshot before every domain reload (recompile,
            // play mode), plus a periodic one so a crash loses at most half a minute.
            AssemblyReloadEvents.beforeAssemblyReload += SaveRecoverySnapshot;
            SpriteAssetPostprocessor.AssetsChanged += CheckExternalChanges;
            root.schedule.Execute(SaveRecoverySnapshot).Every(30000);
            RefreshShortcutTooltips();
            RefreshTarget();
        }

        public void SetTarget(Texture2D texture)
        {
            if (texture == targetTexture && document != null)
                return;
            if (!SessionTransition.TryLeave(document is { IsDirty: true },
                    () => EditorUtility.DisplayDialogComplex(windowTitle,
                        $"Save unapplied changes to '{document.AssetPath}' before opening another texture?",
                        "Save", "Cancel", "Discard"), TryApplyDocument, ClearRecovery))
                return;
            ClearRecovery();
            targetTexture = texture;
            ReloadTarget();
        }

        private void ReloadTarget()
        {
            ClearShapePreview();
            if (document != null)
                document.Changed -= OnDocumentChanged;
            if (selection != null)
                selection.Changed -= OnSelectionChanged;
            if (viewport != null)
                viewport.ActiveTool = null;
            document = null;
            history = null;
            selection = null;
            inspector?.Bind(null, null, null);
            historyPanel?.Bind(null);
            regionList?.Bind(null, null, null, null, null);
            viewport?.SetSession(null, null);
            slicePanel?.Hide();
            alignPivotsPanel?.Hide();
            boundsPanel?.Hide();
            propertyPastePanel?.Hide();
            hadUnappliedChanges = false;
            RefreshTarget();
        }

        private void CheckExternalChanges()
        {
            if (document == null)
                return;
            try
            {
                SpriteDocumentRepository.EnsureUnchanged(document);
                RefreshInfoLabel();
            }
            catch (Exception exception)
            {
                if (!document.IsDirty && document.Texture != null)
                {
                    ReloadTarget();
                    return;
                }
                SaveRecoverySnapshot();
                ClearShapePreview();
                slicePanel.Hide();
                alignPivotsPanel.Hide();
                boundsPanel.Hide();
                propertyPastePanel?.Hide();
                contentSplit.SetEnabled(false);
                messageBox.text = exception.Message;
                messageBox.messageType = HelpBoxMessageType.Warning;
                messageBox.style.display = DisplayStyle.Flex;
            }
        }

        private void RefreshTarget()
        {
            // CreateGUI has not run yet when SetTarget is called on a freshly created window.
            if (viewport == null)
                return;

            if (targetTexture == null)
            {
                ShowMessage("No texture selected. Select a texture in the Project window and use " +
                            "'Open in Better Sprite Editor' from its context menu.",
                    HelpBoxMessageType.Info);
                titleContent = new GUIContent(windowTitle);
                infoLabel.text = string.Empty;
                combineButton.SetEnabled(false);
                renameButton.SetEnabled(false);
                splitButton.SetEnabled(false);
                alignPivotsButton.SetEnabled(false);
                usagesButton.SetEnabled(false);
                RefreshHistoryButtons();
                RefreshUnsavedChangesState();
                return;
            }

            if (document == null && !TryLoadDocument())
                return;

            messageBox.style.display = DisplayStyle.None;
            contentSplit.style.display = DisplayStyle.Flex;
            usagesButton.SetEnabled(true);
            titleContent = new GUIContent($"{windowTitle} - {targetTexture.name}");
            RefreshInfoLabel();
            RefreshZoomLabel();
            RefreshHistoryButtons();
        }

        private bool TryLoadDocument(bool ignoreRecovery = false)
        {
            DocumentSnapshotDto recovery = null;
            try
            {
                var diskDocument = SpriteDocumentRepository.Load(targetTexture);
                recovery = ignoreRecovery ? null : TryConsumeRecovery(diskDocument.AssetPath);
                document = recovery == null ? diskDocument : DocumentSnapshot.RestoreDocument(recovery, targetTexture);
                document.Capabilities = diskDocument.Capabilities;
                document.HasOutlineProvider = diskDocument.HasOutlineProvider;
                document.HasPhysicsProvider = diskDocument.HasPhysicsProvider;
                selection = new SelectionModel();
                history = new HistoryStack(document);
                if (recovery != null)
                {
                    DocumentSnapshot.RestoreHistory(recovery.History, document, history);
                    var ids = new List<GUID>();
                    foreach (var id in recovery.SelectedIds)
                    {
                        var guid = new GUID(id);
                        if (document.FindRegion(guid) != null)
                            ids.Add(guid);
                    }
                    selection.SetRange(ids);
                }
            }
            catch (Exception exception)
            {
                if (recovery != null)
                {
                    RecoveryStore.Quarantine(recovery.TextureGuid, exception.Message);
                    // All restoration, including history and selection, is inside this boundary.
                    return TryLoadDocument(true);
                }
                document = null;
                history = null;
                selection = null;
                infoLabel.text = string.Empty;
                ShowMessage($"Cannot load sprite data: {exception.Message}", HelpBoxMessageType.Error);
                RefreshUnsavedChangesState();
                return false;
            }
            contentSplit.SetEnabled(true);
            toolContext = new ToolContext
            {
                Document = document,
                Selection = selection,
                History = history,
                Transform = viewport.Transform,
                RepaintTarget = viewport,
                KeepPivotPosition = inspector.KeepPivotPosition
            };
            toolContext.LiveEdited += inspector.Refresh;
            selectTool = new SelectTool(toolContext);
            createTool = new CreateTool(toolContext);
            shapeTool = new ShapeTool(toolContext)
            {
                SnapToPixels = EditorPrefs.GetBool("BetterSpriteEditor.ShapeSnapToPixels", true)
            };

            document.Changed += OnDocumentChanged;
            selection.Changed += OnSelectionChanged;

            slicePanel.Hide();
            sliceToggle.SetValueWithoutNotify(false);
            alignPivotsPanel.Hide();
            boundsPanel.Hide();
            propertyPastePanel?.Hide();
            cachedAlphaMap = null; // belongs to the previous texture
            unusedRegionIds.Clear(); // usage results belong to the previous texture
            viewport.SetUnusedRegions(unusedRegionIds);
            viewport.SetSession(document, selection);
            inspector.Bind(document, selection, history);
            historyPanel.Bind(history);
            regionList.Bind(document, selection, history,
                id => unusedRegionIds.Contains(id),
                region => viewport.FrameRect(region.Rect));
            SetTool(ToolMode.Select);
            SetEditorMode(editorMode);

            hadUnappliedChanges = document.IsDirty;
            RefreshUnsavedChangesState();
            OnSelectionChanged();
            return true;
        }

        private void OnSelectionChanged()
        {
            viewport.MarkDirtyRepaint();
            RefreshClipboardButtons();
            combineButton.SetEnabled(document.CanChangeRegions && selection is { Count: >= 2 });
            renameButton.SetEnabled(document.CanEditName && selection is { Count: >= 1 });
            splitButton.SetEnabled(document.CanChangeRegions && selection is { Count: >= 1 });
            createToggle.SetEnabled(document.CanChangeRegions);
            sliceToggle.SetEnabled(document.CanChangeRegions);
            modeField.SetEnabled(document.HasOutlineProvider || document.HasPhysicsProvider);
            alignPivotsButton.SetEnabled(document.Capabilities.Has(SpriteEditCapabilities.EditPivot) && selection is { Count: >= 2 });
            boundsButton.SetEnabled(document.CanPreservePivotPosition && selection is { Count: >= 1 });
            // The preview follows the selection, so picking another sprite re-previews it.
            if (editorMode != EditorMode.SpriteEditor)
                UpdateShapePreview(inspector.GetShapeSettings());
        }

        /// <summary>Writes (or clears) the recovery file for the open document.</summary>
        private void SaveRecoverySnapshot()
        {
            if (document == null)
                return;
            string textureGuid = document.AssetGuid;
            if (string.IsNullOrEmpty(textureGuid))
                return;

            if (!document.IsDirty)
            {
                RecoveryStore.Delete(textureGuid);
                return;
            }
            RecoveryStore.Save(DocumentSnapshot.Capture(document, selection: selection, history: history));
        }

        /// <summary>
        /// Checks for a recovery snapshot before a fresh load. Same-session reloads restore
        /// silently — a prompt on every recompile would be noise; a fresh open after a crash
        /// asks first. Conflicting, legacy, or damaged snapshots are retained for inspection,
        /// and only a fully validated session is restored.
        /// </summary>
        private DocumentSnapshotDto TryConsumeRecovery(string assetPath)
        {
            string textureGuid = AssetDatabase.AssetPathToGUID(assetPath);
            var dto = RecoveryStore.TryLoad(textureGuid);
            if (dto == null)
                return null;

            try
            {
                DocumentSnapshot.Validate(dto);
                if (dto.TextureGuid != textureGuid || dto.BaselineRevision != AssetState.Revision(assetPath))
                    throw new InvalidOperationException("The asset changed since these edits were made.");
            }
            catch (Exception exception)
            {
                RecoveryStore.Quarantine(textureGuid, exception.Message);
                ShowNotification(new GUIContent("Recovery needs inspection; a copy was retained in Library/BetterSpriteEditor."));
                return null;
            }

            if (hadUnappliedChanges)
                return dto;

            int editCount = dto.History?.Undo?.Length ?? 0;
            bool recover = EditorUtility.DisplayDialog(windowTitle,
                $"Found unapplied changes for '{System.IO.Path.GetFileName(assetPath)}' from a " +
                $"previous session ({dto.Regions.Length} regions, {editCount} recorded edits).\n\n" +
                "Recover them?",
                "Recover", "Discard");
            if (!recover)
            {
                RecoveryStore.Delete(textureGuid);
                return null;
            }
            return dto;
        }

        private void OnDocumentChanged()
        {
            ClearUnusedHighlight();
            selection.PruneMissing(document);
            RefreshClipboardButtons();
            hadUnappliedChanges = document.IsDirty;
            if (!document.IsDirty)
                ClearRecovery();
            viewport.MarkDirtyRepaint();
            RefreshInfoLabel();
            RefreshHistoryButtons();
            RefreshUnsavedChangesState();
        }

        private void RefreshUnsavedChangesState()
        {
            bool dirty = document is { IsDirty: true };
            hasUnsavedChanges = dirty;
            saveChangesMessage = dirty
                ? $"'{document.AssetPath}' has unapplied sprite changes."
                : string.Empty;
        }

        // --- Tools ---

        private void SetTool(ToolMode mode)
        {
            if (mode == ToolMode.Create && document is not { CanChangeRegions: true })
                mode = ToolMode.Select;
            toolMode = mode;
            selectToggle.SetValueWithoutNotify(mode == ToolMode.Select);
            createToggle.SetValueWithoutNotify(mode == ToolMode.Create);
            if (editorMode == EditorMode.SpriteEditor)
                RefreshActiveTool();
        }

        /// <summary>
        /// Switching module changes which tool owns the viewport and which controls apply.
        /// Rect tools and slicing belong to Sprite Editor mode; shape editing to the other two.
        /// </summary>
        private void SetEditorMode(EditorMode mode)
        {
            if (document != null && ((mode == EditorMode.CustomOutline && !document.HasOutlineProvider)
                || (mode == EditorMode.CustomPhysicsShape && !document.HasPhysicsProvider)))
                mode = EditorMode.SpriteEditor;
            propertyPastePanel?.Hide();
            editorMode = mode;
            modeField.SetValueWithoutNotify(mode);

            bool isSpriteMode = mode == EditorMode.SpriteEditor;
            selectToggle.style.display = isSpriteMode ? DisplayStyle.Flex : DisplayStyle.None;
            createToggle.style.display = isSpriteMode ? DisplayStyle.Flex : DisplayStyle.None;
            combineButton.style.display = isSpriteMode ? DisplayStyle.Flex : DisplayStyle.None;
            renameButton.style.display = isSpriteMode ? DisplayStyle.Flex : DisplayStyle.None;
            sliceToggle.style.display = isSpriteMode ? DisplayStyle.Flex : DisplayStyle.None;
            splitButton.style.display = isSpriteMode ? DisplayStyle.Flex : DisplayStyle.None;
            alignPivotsButton.style.display = isSpriteMode ? DisplayStyle.Flex : DisplayStyle.None;
            boundsButton.style.display = isSpriteMode ? DisplayStyle.Flex : DisplayStyle.None;
            if (!isSpriteMode && sliceToggle.value)
                sliceToggle.value = false;
            if (!isSpriteMode)
            {
                alignPivotsPanel?.Hide();
                boundsPanel?.Hide();
                propertyPastePanel?.Hide();
            }

            if (shapeTool != null)
                shapeTool.Kind = mode == EditorMode.CustomOutline ? ShapeKind.Outline : ShapeKind.Physics;
            inspector?.SetShapeMode(isSpriteMode ? null : shapeTool?.Kind);
            RefreshActiveTool();

            if (isSpriteMode || document == null)
                ClearShapePreview();
            else
                UpdateShapePreview(inspector.GetShapeSettings());
        }

        private void RefreshActiveTool()
        {
            if (viewport == null || document == null)
                return;
            viewport.ActiveTool = editorMode switch
            {
                EditorMode.SpriteEditor => toolMode == ToolMode.Select ? selectTool : createTool,
                _ => shapeTool
            };
        }

        // --- Undo/redo routing ---

        private void OnValidateCommand(ValidateCommandEvent evt)
        {
            if (history == null || evt.target is TextElement
                || rootVisualElement.panel?.focusController?.focusedElement is TextElement)
                return;
            if (evt.commandName is "Undo" or "Redo")
                evt.StopPropagation();
        }

        private void OnExecuteCommand(ExecuteCommandEvent evt)
        {
            if (history == null || evt.target is TextElement
                || rootVisualElement.panel?.focusController?.focusedElement is TextElement)
                return;
            if (evt.commandName == "Undo")
            {
                TryUndo();
                evt.StopPropagation();
            }
            else if (evt.commandName == "Redo")
            {
                TryRedo();
                evt.StopPropagation();
            }
        }

        private void OnRootKeyDown(KeyDownEvent evt)
        {
            // Never hijack keys from text editing (field undo, typing 'm' in a name, …).
            if (evt.target is TextElement)
                return;

            if (ShortcutRegistry.Matches(ShortcutActionId.Undo, evt))
            {
                TryUndo();
                evt.StopPropagation();
            }
            else if (ShortcutRegistry.Matches(ShortcutActionId.Redo, evt)
                     || ShortcutRegistry.Matches(ShortcutActionId.RedoAlternate, evt))
            {
                TryRedo();
                evt.StopPropagation();
            }
            else if (ShortcutRegistry.Matches(ShortcutActionId.SelectTool, evt))
            {
                SetTool(ToolMode.Select);
                evt.StopPropagation();
            }
            else if (ShortcutRegistry.Matches(ShortcutActionId.CreateTool, evt))
            {
                SetTool(ToolMode.Create);
                evt.StopPropagation();
            }
            // In the shape modes Delete belongs to the shape tool (remove a point), so this
            // trickle-down handler must not swallow it and delete whole regions instead.
            else if (editorMode == EditorMode.SpriteEditor
                     && ShortcutRegistry.Matches(ShortcutActionId.DeleteSelection, evt))
            {
                DeleteSelectedRegions();
                evt.StopPropagation();
            }
            else if (ShortcutRegistry.Matches(ShortcutActionId.CombineSelection, evt))
            {
                CombineSelectedRegions();
                evt.StopPropagation();
            }
            else if (ShortcutRegistry.Matches(ShortcutActionId.FindUsages, evt))
            {
                FindUsagesOfSelection();
                evt.StopPropagation();
            }
            else if (ShortcutRegistry.Matches(ShortcutActionId.RenameSelection, evt))
            {
                RenameSelectedRegions();
                evt.StopPropagation();
            }
            else if (ShortcutRegistry.Matches(ShortcutActionId.ApplyChanges, evt))
            {
                ApplyDocument();
                evt.StopPropagation();
            }
            else if (ShortcutRegistry.Matches(ShortcutActionId.ToggleSlicePanel, evt))
            {
                sliceToggle.value = !sliceToggle.value;
                evt.StopPropagation();
            }
            else if (ShortcutRegistry.Matches(ShortcutActionId.FitView, evt))
            {
                viewport.FitToWindow();
                evt.StopPropagation();
            }
            else if (ShortcutRegistry.Matches(ShortcutActionId.ZoomOneToOne, evt))
            {
                viewport.SetZoomOneToOne();
                evt.StopPropagation();
            }
            else if (ShortcutRegistry.Matches(ShortcutActionId.ToggleAlphaView, evt))
            {
                alphaToggle.value = !alphaToggle.value;
                evt.StopPropagation();
            }
        }

        private void TryUndo()
        {
            if (history == null || !GuardUndoRedo())
                return;
            history.Undo();
        }

        private void TryRedo()
        {
            if (history == null || !GuardUndoRedo())
                return;
            history.Redo();
        }

        private bool GuardUndoRedo()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - lastUndoRedoTime < 0.05)
                return false;
            lastUndoRedoTime = now;
            return true;
        }

        // --- Actions ---

        private void DeleteSelectedRegions()
        {
            if (document == null || !document.CanChangeRegions || selection.Count == 0)
                return;

            var regions = new List<SpriteRegion>();
            foreach (var id in selection.Ids)
            {
                var region = document.FindRegion(id);
                if (region != null)
                    regions.Add(region);
            }
            if (regions.Count == 0)
                return;

            history.Do(new DeleteRegionsCommand(document, regions));
            selection.Clear();
        }

        /// <summary>
        /// Starts an external drag of the region under the cursor (or the whole selection when
        /// that region is part of it), so it can be dropped into the scene, the hierarchy or a
        /// sprite field. Returns false when there is nothing draggable, leaving the gesture free.
        /// </summary>
        private bool TryStartDragOut(Vector2 texturePosition)
        {
            if (document == null)
                return false;
            var hitRegion = document.FindRegionAt(texturePosition);
            if (hitRegion == null)
                return false;

            // Grabbing a selected region drags the whole selection; anything else drags itself.
            var regions = new List<SpriteRegion>();
            if (selection.IsSelected(hitRegion.Id))
                CollectSelectedRegions(regions);
            else
                regions.Add(hitRegion);

            return StartDragOf(regions);
        }

        /// <summary>Drag source for the inspector preview: always the current selection.</summary>
        private bool TryStartDragOutOfSelection()
        {
            if (document == null || selection.Count == 0)
                return false;
            var regions = new List<SpriteRegion>();
            CollectSelectedRegions(regions);
            return StartDragOf(regions);
        }

        private void CollectSelectedRegions(List<SpriteRegion> into)
        {
            foreach (var region in document.Regions)
            {
                if (selection.IsSelected(region.Id))
                    into.Add(region);
            }
        }

        private bool StartDragOf(List<SpriteRegion> regions)
        {
            if (regions.Count == 0)
                return false;

            var sprites = ResolveSprites(regions, out string missingName);
            if (sprites.Count == 0)
            {
                ShowNotification(new GUIContent(
                    $"'{missingName}' has no sprite asset yet — apply changes first."));
                return false;
            }

            DragAndDrop.PrepareStartDrag();
            // Only objectReferences: adding the texture path would let drop targets take the
            // whole texture instead of the specific sprite sub-assets.
            DragAndDrop.objectReferences = sprites.ToArray();
            DragAndDrop.StartDrag(sprites.Count == 1 ? sprites[0].name : $"{sprites.Count} Sprites");

            if (missingName != null)
                ShowNotification(new GUIContent($"'{missingName}' was skipped — apply changes first."));
            else if (document.IsDirty)
                ShowNotification(new GUIContent("Dragging the last applied state."));
            return true;
        }

        /// <summary>
        /// Maps regions to their Sprite sub-assets by name. Regions that were created or renamed
        /// without applying have no sub-asset yet; the first such name comes back in missingName.
        /// </summary>
        private List<UnityEngine.Object> ResolveSprites(List<SpriteRegion> regions, out string missingName)
        {
            missingName = null;
            var spritesByName = new Dictionary<string, Sprite>();
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(document.AssetPath))
            {
                if (asset is Sprite sprite)
                    spritesByName[sprite.name] = sprite;
            }

            var result = new List<UnityEngine.Object>(regions.Count);
            foreach (var region in regions)
            {
                if (spritesByName.TryGetValue(region.Name, out var sprite))
                    result.Add(sprite);
                else
                    missingName ??= region.Name;
            }
            return result;
        }

        /// <summary>
        /// Searches with the selected regions, or the whole sheet when nothing is selected —
        /// "which of these sprites is actually used?" is a useful question for a whole atlas.
        /// </summary>
        private void FindUsagesOfSelection()
        {
            if (document == null)
                return;

            var names = new List<string>();
            var ids = new List<string>();
            foreach (var region in document.Regions)
            {
                if (selection.Count == 0 || selection.IsSelected(region.Id))
                {
                    names.Add(region.Name);
                    ids.Add(region.Id.ToString());
                }
            }
            if (names.Count == 0)
                return;

            SpriteUsagesWindow.Open(document.AssetPath, names, document.IsDirty, ids);
        }

        private void RenameSelectedRegions()
        {
            if (document == null || !document.CanEditName || selection.Count == 0)
                return;
            RenameDialogWindow.Open(document, selection, history, this);
        }

        private void AlignPivotsOfSelection()
        {
            if (document == null || !document.Capabilities.Has(SpriteEditCapabilities.EditPivot) || selection.Count < 2)
                return;
            var regions = new List<SpriteRegion>();
            CollectSelectedRegions(regions);
            var reference = document.FindRegion(selection.ActiveId) ?? regions[0];

            // The overlay panels share the viewport's top-left corner — one at a time.
            if (sliceToggle.value)
                sliceToggle.value = false;
            boundsPanel.Hide();
            propertyPastePanel?.Hide();
            alignPivotsPanel.Show(document, regions, reference, GetAlphaMap,
                anchors => viewport.SetOnionSkin(BuildOnionFrames(anchors)),
                ApplyPivotAnchors);
        }

        private List<(Rect, Vector2)> BuildOnionFrames(Dictionary<GUID, Vector2> anchors)
        {
            if (anchors == null)
                return null;
            var frames = new List<(Rect, Vector2)>(anchors.Count);
            foreach (var region in document.Regions)
            {
                if (anchors.TryGetValue(region.Id, out var anchor))
                    frames.Add((region.Rect, anchor));
            }
            return frames;
        }

        private void ApplyPivotAnchors(Dictionary<GUID, Vector2> anchors)
        {
            var commands = new List<IEditorCommand>();
            string label = $"Align pivots of {anchors.Count} regions";
            foreach (var entry in anchors)
            {
                var region = document.FindRegion(entry.Key);
                if (region == null || region.Rect.width <= 0f || region.Rect.height <= 0f)
                    continue;

                var before = region.Clone();
                var pivot = new Vector2(
                    (entry.Value.x - region.Rect.x) / region.Rect.width,
                    (entry.Value.y - region.Rect.y) / region.Rect.height);
                // Same rounding as pivot dragging, to avoid float noise in the meta file.
                pivot.x = Mathf.Round(pivot.x * 10000f) / 10000f;
                pivot.y = Mathf.Round(pivot.y * 10000f) / 10000f;

                region.Alignment = SpriteAlignment.Custom;
                region.Pivot = pivot;
                if (before.Alignment != region.Alignment || before.Pivot != region.Pivot)
                    commands.Add(new ModifyRegionCommand(document, region.Id, before, region.Clone(), label));
            }

            viewport.SetOnionSkin(null);
            if (commands.Count == 0)
                return;
            history.PushApplied(commands.Count == 1 ? commands[0] : new CompositeCommand(label, commands));
        }

        private void RefreshClipboardButtons()
        {
            copyPropertiesButton?.SetEnabled(document != null && selection is { Count: 1 });
            pastePropertiesButton?.SetEnabled(document != null && selection is { Count: > 0 } && SpritePropertyClipboard.HasData);
        }

        private void CopyProperties()
        {
            if (document == null || selection is not { Count: 1 }) return;
            var region = document.FindRegion(selection.ActiveId);
            if (region == null) return;
            try
            {
                propertyPastePanel.Hide();
                SpritePropertyClipboard.Copy(document, region);
                ShowNotification(new GUIContent($"Copied properties: {region.Name}"));
            }
            catch (Exception exception) { ShowNotification(new GUIContent(exception.Message)); }
        }

        private void PasteProperties()
        {
            if (document == null || selection.Count == 0) return;
            if (sliceToggle.value) sliceToggle.value = false;
            alignPivotsPanel.Hide();
            boundsPanel.Hide();
            ClearShapePreview();
            propertyPastePanel.Show(document, selection, history, SpritePropertyClipboard.Read(), viewport.SetPropertyPreview);
        }

        private void EditSelectedBounds()
        {
            if (document == null || !document.CanPreservePivotPosition || selection.Count == 0
                || editorMode != EditorMode.SpriteEditor)
                return;
            if (sliceToggle.value)
                sliceToggle.value = false;
            alignPivotsPanel.Hide();
            propertyPastePanel.Hide();
            boundsPanel.Show(document, selection, history, GetAlphaMap, viewport.SetPreviewRects);
        }

        private void SplitActiveRegion()
        {
            if (document == null || !document.CanChangeRegions)
                return;
            var region = document.FindRegion(selection.ActiveId);
            if (region == null)
                return;

            SplitDialogWindow.Open(document, region, this, GetAlphaMap, result =>
            {
                var commands = new List<IEditorCommand>
                {
                    new DeleteRegionsCommand(document, new List<SpriteRegion> { region })
                };
                var childIds = new List<GUID>(result.Rects.Count);
                for (int i = 0; i < result.Rects.Count; i++)
                {
                    // Children inherit the source's pivot settings; a border would rarely
                    // survive a split meaningfully, so it starts clean.
                    var child = new SpriteRegion
                    {
                        Id = GUID.Generate(),
                        Name = result.BaseName + (result.StartNumber + i),
                        Rect = result.Rects[i],
                        Alignment = region.Alignment,
                        Pivot = region.Pivot,
                        Border = Vector4.zero,
                        CustomData = string.Empty
                    };
                    commands.Add(new CreateRegionCommand(document, child));
                    childIds.Add(child.Id);
                }

                history.Do(new CompositeCommand(
                    $"Split '{region.Name}' into {result.Rects.Count} regions", commands));
                selection.SetRange(childIds);
            });
        }

        private void CombineSelectedRegions()
        {
            if (document == null || !document.CanChangeRegions || selection.Count < 2)
                return;

            var regions = new List<SpriteRegion>();
            foreach (var region in document.Regions)
            {
                if (selection.IsSelected(region.Id))
                    regions.Add(region);
            }
            if (regions.Count < 2)
                return;

            CombineDialogWindow.Open(document, regions, this, result =>
            {
                var union = regions[0].Rect;
                foreach (var region in regions)
                {
                    union = Rect.MinMaxRect(
                        Mathf.Min(union.xMin, region.Rect.xMin),
                        Mathf.Min(union.yMin, region.Rect.yMin),
                        Mathf.Max(union.xMax, region.Rect.xMax),
                        Mathf.Max(union.yMax, region.Rect.yMax));
                }

                var combined = new SpriteRegion
                {
                    Id = GUID.Generate(),
                    Name = result.Name,
                    Rect = union,
                    Alignment = result.Alignment,
                    Pivot = result.Pivot,
                    Border = Vector4.zero,
                    CustomData = string.Empty
                };

                history.Do(new CompositeCommand($"Combine {regions.Count} regions",
                    new List<IEditorCommand>
                    {
                        new DeleteRegionsCommand(document, regions),
                        new CreateRegionCommand(document, combined)
                    }));
                selection.Set(combined.Id);
            });
        }

        /// <summary>
        /// Shows what Generate would produce, without committing. Limited to the selection —
        /// tracing every region of a sheet on each slider tick would be far too slow, and the
        /// point of a preview is the sprite you are looking at.
        /// </summary>
        private void UpdateShapePreview(ShapeGenerationSettings settings)
        {
            if (propertyPastePanel is { IsOpen: true }) return;
            // Coalesce: a slider drag fires continuously, and retracing on every tick would
            // stall the drag. One pass shortly after the last change is enough.
            pendingPreviewSettings = settings;
            previewSchedule ??= rootVisualElement.schedule.Execute(RunShapePreview);
            previewSchedule.ExecuteLater(previewDebounceMilliseconds);
        }

        private void RunShapePreview()
        {
            if (propertyPastePanel is { IsOpen: true }) return;
            var settings = pendingPreviewSettings;

            if (document == null || editorMode == EditorMode.SpriteEditor || selection.Count == 0)
            {
                viewport.SetPreviewShapes(null);
                inspector.SetPreviewStatus(string.Empty);
                return;
            }

            var regions = new List<SpriteRegion>();
            CollectSelectedRegions(regions);

            // Budget by pixels rather than region count: cost scales with area, and a count cap
            // truncates a wide selection of small sprites for no reason.
            int selectedCount = regions.Count;
            long budget = 0;
            int previewed = 0;
            foreach (var region in regions)
            {
                budget += (long)region.Rect.width * (long)region.Rect.height;
                if (budget > previewPixelBudget && previewed > 0)
                    break;
                previewed++;
            }
            if (settings.SharedShape)
                previewed = regions.Count;
            if (previewed < regions.Count)
                regions.RemoveRange(previewed, regions.Count - previewed);

            string truncationNote = previewed < selectedCount
                ? $"Previewing {previewed} of {selectedCount} selected regions."
                : string.Empty;

            TextureAlphaMap alphaMap;
            try
            {
                alphaMap = GetAlphaMap();
            }
            catch (Exception)
            {
                viewport.SetPreviewShapes(null);
                inspector.SetPreviewStatus(string.Empty);
                return;
            }

            var preview = new List<Vector2[]>();
            // Declared outside the loop: an out variable inside the collection expression would
            // fall out of scope before the status message below.
            bool budgetApplied;
            List<(SpriteRegion region, List<Vector2[]> shapes)> generated;
            try
            {
                generated = ComputeShapes(regions, settings, alphaMap, out budgetApplied);
            }
            catch (InvalidOperationException exception)
            {
                viewport.SetPreviewShapes(null);
                inspector.SetPreviewStatus(exception.Message);
                return;
            }
            foreach (var (region, shapes) in generated)
            {
                foreach (var path in shapes)
                {
                    // Shape points are centre-relative; the viewport draws in texture space.
                    var texturePath = new Vector2[path.Length];
                    for (int i = 0; i < path.Length; i++)
                        texturePath[i] = region.ShapePointToTexture(path[i]);
                    preview.Add(texturePath);
                }
            }
            viewport.SetPreviewShapes(preview);

            int largestPointCount = 0;
            foreach (var entry in generated)
            {
                int count = 0;
                foreach (var path in entry.shapes)
                    count += path.Length;
                largestPointCount = Mathf.Max(largestPointCount, count);
            }
            string budgetNote = settings.MaxPoints > 0 && largestPointCount > settings.MaxPoints
                ? $"Point target {settings.MaxPoints} cannot be met without changing geometry; largest shape has {largestPointCount} points."
                : budgetApplied ? "Shapes were coarsened to approach the point target." : string.Empty;
            inspector.SetPreviewStatus(string.IsNullOrEmpty(truncationNote)
                ? budgetNote
                : truncationNote + (string.IsNullOrEmpty(budgetNote) ? string.Empty : "\n" + budgetNote));
        }

        private void ClearShapePreview()
        {
            previewSchedule?.Pause();
            viewport?.SetPreviewShapes(null);
            inspector?.SetPreviewStatus(string.Empty);
        }

        /// <summary>
        /// Shapes for each region, honouring the shared-shape setting. One place so the preview
        /// and the commit can never disagree about what Generate would produce.
        /// </summary>
        private List<(SpriteRegion region, List<Vector2[]> shapes)> ComputeShapes(
            List<SpriteRegion> regions, ShapeGenerationSettings settings, TextureAlphaMap alphaMap,
            out bool pointBudgetApplied)
        {
            pointBudgetApplied = false;
            var result = new List<(SpriteRegion, List<Vector2[]>)>(regions.Count);

            if (!settings.SharedShape || regions.Count < 2)
            {
                foreach (var region in regions)
                {
                    result.Add((region,
                        OutlineGenerator.Generate(alphaMap, region.Rect, settings, out bool limited)));
                    pointBudgetApplied |= limited;
                }
                return result;
            }

            var shared = SharedShapeBuilder.Build(alphaMap, regions, settings, out pointBudgetApplied);
            foreach (var region in regions)
            {
                // Each region gets its own copy: they must not alias once edited by hand.
                var paths = new List<Vector2[]>(shared.Count);
                foreach (var path in shared)
                {
                    var regionPath = SharedShapeBuilder.ToRegionSpace(path, region);
                    if (regionPath != null)
                        paths.Add(regionPath);
                }
                result.Add((region, paths));
            }
            return result;
        }

        private ShapeKind CurrentShapeKind =>
            editorMode == EditorMode.CustomOutline ? ShapeKind.Outline : ShapeKind.Physics;

        /// <summary>
        /// Traces shapes from the sprite's alpha for every selected region (all regions when
        /// nothing is selected), replacing whatever was there. One history entry for the lot.
        /// </summary>
        private void GenerateShapesForSelection(ShapeGenerationSettings settings, bool allRegions)
        {
            if (document == null || editorMode == EditorMode.SpriteEditor)
                return;

            // An explicit selection means "regenerate these", so Keep Existing only guards the
            // sheet-wide case, where clobbering hand-edited shapes is the real risk.
            bool sheetWide = allRegions || selection.Count == 0;
            var regions = new List<SpriteRegion>();
            if (sheetWide)
                regions.AddRange(document.Regions);
            else
                CollectSelectedRegions(regions);

            int skipped = 0;
            if (sheetWide && settings.KeepExisting)
            {
                var kindToKeep = CurrentShapeKind;
                skipped = regions.RemoveAll(region => region.GetShapes(kindToKeep).Count > 0);
            }
            if (regions.Count == 0)
            {
                if (skipped > 0)
                    ShowNotification(new GUIContent($"All {skipped} regions already have a shape."));
                return;
            }

            TextureAlphaMap alphaMap;
            try
            {
                alphaMap = GetAlphaMap();
            }
            catch (Exception exception)
            {
                EditorUtility.DisplayDialog(windowTitle,
                    $"Cannot read the texture's pixels:\n{exception.Message}", "OK");
                return;
            }

            var kind = CurrentShapeKind;
            string label = regions.Count == 1
                ? $"Generate shape for '{regions[0].Name}'"
                : $"Generate shapes for {regions.Count} regions";

            var commands = new List<IEditorCommand>();
            bool budgetApplied = false;
            try
            {
                EditorUtility.DisplayProgressBar("Generating shapes", label, 0.5f);
                var generated = ComputeShapes(regions, settings, alphaMap, out budgetApplied);
                foreach (var (region, shapes) in generated)
                {
                    var before = region.Clone();
                    region.SetShapes(kind, shapes);
                    region.SetTessellation(kind, settings.Detail);
                    commands.Add(new ModifyRegionCommand(document, region.Id, before, region.Clone(), label));
                }
            }
            catch (InvalidOperationException exception)
            {
                EditorUtility.DisplayDialog(windowTitle, exception.Message, "OK");
                return;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (commands.Count == 0)
                return;
            history.PushApplied(commands.Count == 1 ? commands[0] : new CompositeCommand(label, commands));
            // The real shapes are drawn now, so the ghosts would just double every line.
            ClearShapePreview();
            if (skipped > 0)
                ShowNotification(new GUIContent($"{skipped} regions kept their existing shape."));
            else if (budgetApplied)
                ShowNotification(new GUIContent(
                    "Point target requested. See the shape point count; topology may require more points."));
        }

        private void ClearShapesForSelection()
        {
            if (document == null || editorMode == EditorMode.SpriteEditor)
                return;

            var regions = new List<SpriteRegion>();
            if (selection.Count > 0)
                CollectSelectedRegions(regions);
            else
                regions.AddRange(document.Regions);

            var kind = CurrentShapeKind;
            string label = regions.Count == 1
                ? $"Clear shape of '{regions[0].Name}'"
                : $"Clear shapes of {regions.Count} regions";

            var commands = new List<IEditorCommand>();
            foreach (var region in regions)
            {
                if (region.GetShapes(kind).Count == 0)
                    continue;
                var before = region.Clone();
                region.SetShapes(kind, new List<Vector2[]>());
                commands.Add(new ModifyRegionCommand(document, region.Id, before, region.Clone(), label));
            }
            if (commands.Count == 0)
                return;
            history.PushApplied(commands.Count == 1 ? commands[0] : new CompositeCommand(label, commands));
        }

        /// <summary>
        /// Alpha data is expensive to read (a full readable copy plus GetPixels32), so it is
        /// cached for the open document. The first read is the one that used to look like a
        /// freeze, hence the progress bar.
        /// </summary>
        private TextureAlphaMap GetAlphaMap()
        {
            if (cachedAlphaMap != null)
                return cachedAlphaMap;
            try
            {
                EditorUtility.DisplayProgressBar(windowTitle, "Reading texture pixels…", 0.5f);
                cachedAlphaMap = SpriteDocumentRepository.LoadAlphaMap(document);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            return cachedAlphaMap;
        }

        private void ApplyDocument()
        {
            TryApplyDocument();
        }

        private bool TryApplyDocument()
        {
            if (document == null || !document.IsDirty)
                return true;
            try
            {
                SpriteDocumentRepository.Save(document);
                history.MarkSavePoint();
                ClearRecovery();
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogError($"[BetterSpriteEditor] Apply failed: {exception}");
                EditorUtility.DisplayDialog(windowTitle, $"Apply failed:\n{exception.Message}", "OK");
                return false;
            }
        }

        /// <summary>A recovery file only exists while there is genuinely something to lose.</summary>
        private void ClearRecovery()
        {
            hadUnappliedChanges = false;
            if (document == null)
                return;
            string textureGuid = document.AssetGuid;
            if (!string.IsNullOrEmpty(textureGuid))
                RecoveryStore.Delete(textureGuid);
        }

        private void RevertDocument()
        {
            if (document == null)
                return;
            if (document.IsDirty && !EditorUtility.DisplayDialog(windowTitle,
                    "Discard all unapplied changes? This also clears the undo history.",
                    "Discard", "Cancel"))
                return;

            ClearRecovery(); // an explicit revert means the snapshot is unwanted too
            ReloadTarget(); // explicit discard bypasses target-switch confirmation
        }

        // --- UI refresh ---

        private void RefreshInfoLabel()
        {
            if (document == null)
                return;
            infoLabel.text = $"{document.TextureWidth}x{document.TextureHeight} · " +
                             $"{document.Regions.Count} regions · {document.ImportMode}" +
                             (document.IsDirty ? " · modified" : string.Empty);
        }

        private void RefreshZoomLabel()
        {
            zoomLabel.text = document == null ? string.Empty : $"{viewport.ZoomLevel * 100f:0}%";
        }

        /// <summary>Tooltips quote the live bindings, so rebinding is visible immediately.</summary>
        private void RefreshShortcutTooltips()
        {
            selectToggle.tooltip = WithShortcut("Select tool", ShortcutActionId.SelectTool);
            createToggle.tooltip = WithShortcut("Create tool", ShortcutActionId.CreateTool);
            combineButton.tooltip = WithShortcut(
                "Combine the selected regions into one (select 2 or more)", ShortcutActionId.CombineSelection);
            renameButton.tooltip = WithShortcut(
                "Rename the selected regions with a rule", ShortcutActionId.RenameSelection);
            usagesButton.tooltip = WithShortcut(
                "Find where the selected regions are used (all regions when nothing is selected)",
                ShortcutActionId.FindUsages);
            sliceToggle.tooltip = WithShortcut(
                "Slice the texture (preview before applying)", ShortcutActionId.ToggleSlicePanel);
            fitButton.tooltip = WithShortcut("Fit to window", ShortcutActionId.FitView);
            oneToOneButton.tooltip = WithShortcut("Zoom 1:1", ShortcutActionId.ZoomOneToOne);
            alphaToggle.tooltip = WithShortcut("Show alpha channel", ShortcutActionId.ToggleAlphaView);
            applyButton.tooltip = WithShortcut("Apply changes to the asset", ShortcutActionId.ApplyChanges);
            RefreshHistoryButtons();
        }

        private static string WithShortcut(string text, ShortcutActionId id)
        {
            string binding = ShortcutRegistry.GetDisplayString(id);
            return string.IsNullOrEmpty(binding) ? text : $"{text} ({binding})";
        }

        private void RefreshHistoryButtons()
        {
            bool hasHistory = history != null;
            string undoBinding = ShortcutRegistry.GetDisplayString(ShortcutActionId.Undo);
            string redoBinding = ShortcutRegistry.GetDisplayString(ShortcutActionId.Redo);
            undoButton.SetEnabled(hasHistory && history.CanUndo);
            redoButton.SetEnabled(hasHistory && history.CanRedo);
            undoButton.tooltip = hasHistory && history.CanUndo
                ? $"Undo {history.UndoLabel} ({undoBinding})"
                : $"Undo ({undoBinding})";
            redoButton.tooltip = hasHistory && history.CanRedo
                ? $"Redo {history.RedoLabel} ({redoBinding})"
                : $"Redo ({redoBinding})";
            applyButton?.SetEnabled(document is { IsDirty: true });
            revertButton?.SetEnabled(document is { IsDirty: true });
        }

        private void ShowMessage(string text, HelpBoxMessageType type)
        {
            messageBox.text = text;
            messageBox.messageType = type;
            messageBox.style.display = DisplayStyle.Flex;
            if (contentSplit != null)
                contentSplit.style.display = DisplayStyle.None;
        }
    }
}
