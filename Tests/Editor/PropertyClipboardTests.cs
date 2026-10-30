using System.Collections.Generic;
using System.Linq;
using BetterSpriteEditor.DataAccess;
using BetterSpriteEditor.Model;
using BetterSpriteEditor.Operations;
using BetterSpriteEditor.Operations.Clipboard;
using BetterSpriteEditor.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace BetterSpriteEditor.Tests
{
    [TestFixture]
    internal sealed class PropertyClipboardTests
    {
        private SpriteDocument document;
        private SpriteRegion source;
        private SpritePropertyPayload payload;
        private SpritePropertyPasteSettings settings;
        private HistoryStack history;
        private SelectionModel selection;
        private string previousClipboard;
        private GameObject panelHost;
        private PanelSettings panelSettings;
        private SpriteRegion Target => document.Regions[0];

        [SetUp]
        public void SetUp()
        {
            previousClipboard = SessionState.GetString(SpritePropertyClipboard.SessionKey, string.Empty);
            document = new SpriteDocument("Assets/clipboard-test.png", null, 128, 128, 100, SpriteImportMode.Multiple)
            {
                AssetGuid = GUID.Generate().ToString(), BaselineRevision = "clipboard-tests",
                HasOutlineProvider = true, HasPhysicsProvider = true
            };
            source = new SpriteRegion
            {
                Id = GUID.Generate(), Name = "source", Rect = new Rect(80, 80, 8, 8),
                Alignment = SpriteAlignment.Custom, Pivot = new Vector2(0.25f, 0.5f),
                Border = Vector4.one, CustomData = "source-private-data",
                Outlines = new List<Vector2[]> { new[] { new Vector2(-2, -2), new Vector2(2, -2), new Vector2(0, 2) } },
                PhysicsOutlines = new List<Vector2[]> { new[] { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(1, 1), new Vector2(-1, 1) } },
                OutlineTessellation = 0.2f, PhysicsTessellation = 0.7f
            };
            document.Regions.Add(new SpriteRegion
            {
                Id = GUID.Generate(), Name = "target", Rect = new Rect(16, 16, 12, 10),
                Alignment = SpriteAlignment.Center, CustomData = "target-private-data"
            });
            payload = SpritePropertyPayload.Capture(document, source);
            settings = new SpritePropertyPasteSettings { Fields = SpritePropertyFields.Physics };
            history = new HistoryStack(document);
            selection = new SelectionModel();
            selection.Set(Target.Id);
        }

        [TearDown]
        public void TearDown()
        {
            if (panelHost != null) Object.DestroyImmediate(panelHost);
            if (panelSettings != null) Object.DestroyImmediate(panelSettings);
            SessionState.SetString(SpritePropertyClipboard.SessionKey, previousClipboard);
        }

        private SpritePropertyPastePlan Plan(ISet<GUID> excluded = null) =>
            SpritePropertyPaste.Build(document, document.Regions, payload, settings, excluded);

        [TestCase(ShapePasteSpace.Pivot, 1, -1)]
        [TestCase(ShapePasteSpace.RectOrigin, -3, -2)]
        [TestCase(ShapePasteSpace.Normalized, -1.5f, -1.25f)]
        public void Mapping_UsesLocalPixelsAndIgnoresAtlasPosition(ShapePasteSpace space, float x, float y)
        {
            settings.ShapeSpace = space;
            var before = Target.Clone();
            var plan = Plan();
            Assert.That(plan.CanApply, Is.True);
            Assert.That(plan.Entries[0].After.PhysicsOutlines[0][0], Is.EqualTo(new Vector2(x, y)));
            Assert.That(Target.ContentEquals(before), Is.True);
            Assert.That(document.IsDirty, Is.False);
        }

        [Test]
        public void PivotAndShape_UseResultingPivotForAlignment()
        {
            settings.Fields |= SpritePropertyFields.Pivot;
            history.Do(Plan().CreateCommand(document));
            Assert.That(Target.Pivot, Is.EqualTo(source.Pivot));
            Assert.That(Target.PhysicsOutlines[0][0], Is.EqualTo(new Vector2(-2, -1)));
        }

        [TestCase(PivotPasteSpace.Normalized, 0.25f, 0.5f)]
        [TestCase(PivotPasteSpace.Pixels, 1f / 6f, 0.4f)]
        public void Pivot_OffersNormalizedOrPixelOffsets(PivotPasteSpace mode, float x, float y)
        {
            settings.Fields = SpritePropertyFields.Pivot;
            settings.PivotSpace = mode;
            history.Do(Plan().CreateCommand(document));
            Assert.That(Target.GetNormalizedPivot(), Is.EqualTo(new Vector2(x, y)));
        }

        [Test]
        public void OutsideFractionalPivot_RemainsExactAndUndoRestoresPreset()
        {
            source.Pivot = new Vector2(-0.125f, 1.25f);
            payload = SpritePropertyPayload.Capture(document, source);
            settings.Fields = SpritePropertyFields.Pivot;
            settings.PivotSpace = PivotPasteSpace.Pixels;
            history.Do(Plan().CreateCommand(document));
            Assert.That(Target.GetPivotTexturePosition(), Is.EqualTo(Target.Rect.position + new Vector2(-1, 10)));
            history.Undo();
            Assert.That(Target.Alignment, Is.EqualTo(SpriteAlignment.Center));
        }

        [Test]
        public void NormalizedPivot_PreservesPresetAndInactiveCustomValue()
        {
            source.Alignment = SpriteAlignment.BottomCenter;
            payload = SpritePropertyPayload.Capture(document, source);
            settings.Fields = SpritePropertyFields.Pivot;
            history.Do(Plan().CreateCommand(document));
            Assert.That(Target.Alignment, Is.EqualTo(SpriteAlignment.BottomCenter));
            Assert.That(Target.Pivot, Is.EqualTo(source.Pivot));
        }

        [TestCase(ShapeKind.Outline)]
        [TestCase(ShapeKind.Physics)]
        public void CrossMode_CopiesGeometryAndOptionalTessellation(ShapeKind destination)
        {
            settings.Fields = destination == ShapeKind.Outline
                ? SpritePropertyFields.Outline | SpritePropertyFields.OutlineTessellation
                : SpritePropertyFields.Physics | SpritePropertyFields.PhysicsTessellation;
            settings.OutlineSource = ShapeKind.Physics;
            settings.PhysicsSource = ShapeKind.Outline;
            var origin = destination == ShapeKind.Outline ? ShapeKind.Physics : ShapeKind.Outline;
            history.Do(Plan().CreateCommand(document));
            Assert.That(Target.GetShapes(destination)[0].Length, Is.EqualTo(source.GetShapes(origin)[0].Length));
            Assert.That(Target.GetTessellation(destination), Is.EqualTo(source.GetTessellation(origin)));
            Assert.That(Target.GetShapes(origin), Is.Empty);
        }

        [Test]
        public void SelectivePaste_PreservesIdentityRectAndOtherProperties()
        {
            var before = Target.Clone();
            history.Do(Plan().CreateCommand(document));
            before.PhysicsOutlines = SpriteRegion.ClonePaths(Target.PhysicsOutlines);
            Assert.That(Target.ContentEquals(before), Is.True);
        }

        [Test]
        public void Conflict_BlocksBatchUntilTargetIsExplicitlyExcluded()
        {
            var small = Target.Clone(); small.Id = GUID.Generate(); small.Name = "small"; small.Rect = new Rect(40, 0, 2, 2);
            document.Regions.Add(small);
            var plan = Plan();
            Assert.That(plan.Entries[1].Issue, Does.Contain("does not fit"));
            Assert.That(plan.CreateCommand(document), Is.Null);
            Assert.That(Target.PhysicsOutlines, Is.Empty);
            plan = Plan(new HashSet<GUID> { small.Id });
            history.Do(plan.CreateCommand(document));
            Assert.That(Target.PhysicsOutlines, Is.Not.Empty);
            Assert.That(small.PhysicsOutlines, Is.Empty);
            history.Undo();
            Assert.That(Target.PhysicsOutlines, Is.Empty);
        }

        [Test]
        public void Borders_RejectOversizedInsetsInsteadOfClamping()
        {
            settings.Fields = SpritePropertyFields.Border;
            payload.Border = new Vector4(7, 1, 6, 1);
            Assert.That(Plan().Entries[0].Issue, Does.Contain("borders do not fit"));
            Assert.That(Plan().CreateCommand(document), Is.Null);
        }

        [Test]
        public void EmptyShape_ClearsOnlyChosenDestination()
        {
            payload.Physics = System.Array.Empty<ClipboardPath>();
            Target.Outlines = SpriteRegion.ClonePaths(source.Outlines);
            Target.PhysicsOutlines = SpriteRegion.ClonePaths(source.PhysicsOutlines);
            history.Do(Plan().CreateCommand(document));
            Assert.That(Target.PhysicsOutlines, Is.Empty);
            Assert.That(Target.Outlines, Is.Not.Empty);
            history.Undo();
            Assert.That(Target.PhysicsOutlines, Is.Not.Empty);
        }

        [Test]
        public void TessellationOnly_DoesNotReplaceGeometry()
        {
            settings.Fields = SpritePropertyFields.PhysicsTessellation;
            history.Do(Plan().CreateCommand(document));
            Assert.That(Target.PhysicsTessellation, Is.EqualTo(0.7f));
            Assert.That(Target.PhysicsOutlines, Is.Empty);
        }

        [Test]
        public void SourceAndTargets_DoNotShareMutableGeometryOrHistorySnapshots()
        {
            var second = Target.Clone(); second.Id = GUID.Generate(); document.Regions.Add(second);
            var plan = Plan();
            var command = plan.CreateCommand(document);
            history.Do(command);
            var expected = second.PhysicsOutlines[0][0];
            Target.PhysicsOutlines[0][0] = Vector2.one * 999;
            payload.Physics[0].Points[0] = Vector2.one * 500;
            plan.Entries[1].After.PhysicsOutlines[0][0] = Vector2.one * 250;
            Assert.That(second.PhysicsOutlines[0][0], Is.EqualTo(expected));
            Assert.That(source.PhysicsOutlines[0][0], Is.EqualTo(new Vector2(-1, -1)));
            history.Undo(); history.Redo();
            Assert.That(Target.PhysicsOutlines[0][0], Is.EqualTo(expected));
            Assert.That(history.Position, Is.EqualTo(1));
        }

        [Test]
        public void StalePreview_RejectsInterveningEditsAndCapabilityChanges()
        {
            var plan = Plan();
            Target.Name = "renamed";
            Assert.That(plan.CreateCommand(document), Is.Null);
            plan = Plan();
            document.HasPhysicsProvider = false;
            Assert.That(plan.CreateCommand(document), Is.Null);
        }

        [TestCase(SpritePropertyFields.Pivot)]
        [TestCase(SpritePropertyFields.Border)]
        [TestCase(SpritePropertyFields.Outline)]
        [TestCase(SpritePropertyFields.Physics)]
        public void Capabilities_BlockUnsupportedSelectedFields(SpritePropertyFields field)
        {
            settings.Fields = field;
            document.Capabilities = SpriteEditCapabilities.None;
            document.HasOutlineProvider = document.HasPhysicsProvider = false;
            Assert.That(Plan().Error, Is.Not.Null);
            Assert.That(Plan().CreateCommand(document), Is.Null);
        }

        [Test]
        public void MissingSourceProvider_IsDifferentFromAnEmptyShape()
        {
            payload.HasPhysics = false;
            Assert.That(Plan().Error, Does.Contain("did not provide"));
            settings.Fields = SpritePropertyFields.Pivot;
            Assert.That(Plan().CanApply, Is.True);
        }

        [Test]
        public void SingleMode_AllowsSupportedProperties()
        {
            var single = new SpriteDocument("other", null, 32, 32, 64, SpriteImportMode.Single);
            single.Regions.Add(Target.Clone());
            settings.Fields = SpritePropertyFields.Pivot | SpritePropertyFields.Border;
            Assert.That(SpritePropertyPaste.Build(single, single.Regions, payload, settings).CanApply, Is.True);
        }

        [Test]
        public void InvalidGeometryAndNonfiniteValues_AreRejected()
        {
            payload.Physics[0].Points[0] = new Vector2(float.NaN, 0);
            Assert.That(Plan().CreateCommand(document), Is.Null);
            payload.Physics[0].Points = new[] { Vector2.zero, Vector2.one, Vector2.one * 2 };
            Assert.That(Plan().Entries[0].Issue, Does.Contain("zero area"));
            settings.Fields = SpritePropertyFields.Pivot;
            payload.Pivot = new Vector2(float.PositiveInfinity, 0);
            Assert.That(Plan().Error, Is.Not.Null);
            payload.Alignment = SpriteAlignment.Center;
            Assert.That(Plan().Error, Does.Contain("stored custom pivot"));
        }

        [Test]
        public void IdenticalPaste_DoesNotCreateHistoryOrDirtyDocument()
        {
            payload = SpritePropertyPayload.Capture(document, Target);
            settings.Fields = (SpritePropertyFields)63;
            Assert.That(Plan().ChangedCount, Is.Zero);
            Assert.That(Plan().CreateCommand(document), Is.Null);
            Assert.That(history.CanUndo || document.IsDirty, Is.False);
        }

        [Test]
        public void SessionClipboard_IsVersionedAndDetachedFromSourceAndReaders()
        {
            SpritePropertyClipboard.Copy(document, source);
            source.PhysicsOutlines[0][0] = Vector2.one * 100;
            var copy = SpritePropertyClipboard.Read();
            Assert.That(copy.SourceName, Is.EqualTo("source"));
            Assert.That(copy.Physics[0].Points[0], Is.EqualTo(new Vector2(-1, -1)));
            copy.Physics[0].Points[0] = Vector2.one * 200;
            Assert.That(SpritePropertyClipboard.Read().Physics[0].Points[0], Is.EqualTo(new Vector2(-1, -1)));
            string json = SessionState.GetString(SpritePropertyClipboard.SessionKey, "");
            Assert.That(json, Does.Not.Contain(source.Id.ToString()));
            Assert.That(json, Does.Not.Contain("source-private-data"));
            SessionState.SetString(SpritePropertyClipboard.SessionKey, "{\"Version\":99}");
            Assert.That(SpritePropertyClipboard.Read(), Is.Null);
        }

        [Test]
        public void Recovery_RestoresPasteAndOneStepUndoRedo()
        {
            settings.Fields = SpritePropertyFields.Physics | SpritePropertyFields.Pivot | SpritePropertyFields.Border;
            var before = Target.Clone();
            history.Do(Plan().CreateCommand(document));
            var expected = Target.Clone();
            var dto = JsonUtility.FromJson<DocumentSnapshotDto>(JsonUtility.ToJson(DocumentSnapshot.Capture(document, history, selection)));
            var restored = DocumentSnapshot.RestoreDocument(dto, null);
            var restoredHistory = new HistoryStack(restored);
            DocumentSnapshot.RestoreHistory(dto.History, restored, restoredHistory);
            Assert.That(restored.Regions[0].ContentEquals(expected), Is.True);
            restoredHistory.Undo();
            Assert.That(restored.Regions[0].ContentEquals(before), Is.True);
            restoredHistory.Redo();
            Assert.That(restored.Regions[0].ContentEquals(expected), Is.True);
        }

        private PropertyPastePanelElement Panel(out SpritePropertyPastePlan[] captured)
        {
            panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            panelHost = new GameObject("Property clipboard panel test", typeof(UIDocument));
            var ui = panelHost.GetComponent<UIDocument>();
            ui.panelSettings = panelSettings;
            var panel = new PropertyPastePanelElement();
            ui.rootVisualElement.Add(panel);
            var capture = new SpritePropertyPastePlan[1];
            panel.Show(document, selection, history, payload, p => capture[0] = p);
            captured = capture;
            return panel;
        }

        private static void Click(Button button)
        {
            using var evt = NavigationSubmitEvent.GetPooled();
            evt.target = button;
            button.SendEvent(evt);
        }

        [Test]
        public void Panel_PreviewsLatestMappingCancelsAndCommitsOnce()
        {
            var panel = Panel(out var preview);
            Assert.That(preview[0].ChangedCount, Is.EqualTo(1));
            Assert.That(history.CanUndo || document.IsDirty, Is.False);
            panel.Q<Toggle>("paste-pivot").value = false;
            panel.Q<Toggle>("paste-physics").value = true;
            panel.Q<EnumField>("paste-shape-space").value = ShapePasteSpace.Normalized;
            Assert.That(preview[0].Entries[0].After.PhysicsOutlines[0][0], Is.EqualTo(new Vector2(-1.5f, -1.25f)));
            Click(panel.Q<Button>("paste-cancel"));
            Assert.That(preview[0], Is.Null);
            Assert.That(history.CanUndo || document.IsDirty, Is.False);
            panel.Show(document, selection, history, payload, p => preview[0] = p);
            Click(panel.Q<Button>("paste-commit"));
            Assert.That(history.Position, Is.EqualTo(1));
            Assert.That(Target.PhysicsOutlines[0][0], Is.EqualTo(new Vector2(-1.5f, -1.25f)));
            Assert.That(preview[0], Is.Null);
        }

        [Test]
        public void Panel_ExcludesConflictsAndTracksSelection()
        {
            var small = Target.Clone(); small.Id = GUID.Generate(); small.Rect = new Rect(40, 0, 2, 2);
            document.Regions.Add(small); selection.Add(small.Id);
            var panel = Panel(out var preview);
            panel.Q<Toggle>("paste-pivot").value = false;
            panel.Q<Toggle>("paste-physics").value = true;
            Assert.That(panel.Q<Button>("paste-commit").enabledSelf, Is.False);
            panel.Q<Toggle>("paste-target-" + small.Id).value = false;
            Assert.That(panel.Q<Button>("paste-commit").enabledSelf, Is.True);
            selection.Clear();
            Assert.That(panel.Q<Button>("paste-commit").enabledSelf, Is.False);
            selection.Set(Target.Id);
            Click(panel.Q<Button>("paste-commit"));
            Assert.That(Target.PhysicsOutlines, Is.Not.Empty);
            Assert.That(small.PhysicsOutlines, Is.Empty);
        }
    }
}
