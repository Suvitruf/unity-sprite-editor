using System.Collections.Generic;
using System.Linq;
using BetterSpriteEditor.DataAccess;
using BetterSpriteEditor.Model;
using BetterSpriteEditor.Operations;
using BetterSpriteEditor.Operations.Bounds;
using BetterSpriteEditor.UI;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace BetterSpriteEditor.Tests
{
    public class FrameBoundsTests
    {
        private SpriteDocument document;
        private SelectionModel selection;
        private HistoryStack history;
        private Color32[] pixels;
        private GameObject panelHost;
        private PanelSettings panelSettings;
        private SpriteRegion First => document.Regions[0];
        private SpriteRegion Second => document.Regions[1];

        [SetUp]
        public void SetUp()
        {
            document = new SpriteDocument("Assets", null, 64, 64, 100, SpriteImportMode.Multiple)
            {
                AssetGuid = GUID.Generate().ToString(), BaselineRevision = "bounds-tests",
                HasOutlineProvider = true, HasPhysicsProvider = true
            };
            document.Regions.Add(new SpriteRegion
            {
                Id = GUID.Generate(), Name = "first", Rect = new Rect(8, 8, 12, 10),
                Alignment = SpriteAlignment.Center, Pivot = new Vector2(0.12f, 0.34f)
            });
            document.Regions.Add(new SpriteRegion
            {
                Id = GUID.Generate(), Name = "second", Rect = new Rect(32, 8, 8, 14),
                Alignment = SpriteAlignment.BottomCenter
            });
            selection = new SelectionModel();
            selection.SetRange(document.Regions.Select(r => r.Id));
            history = new HistoryStack(document);
            pixels = new Color32[64 * 64];
            Paint(10, 11, 4, 3);
        }

        [TearDown]
        public void TearDown()
        {
            if (panelHost != null) Object.DestroyImmediate(panelHost);
            if (panelSettings != null) Object.DestroyImmediate(panelSettings);
        }

        private void Paint(int x, int y, int width, int height, byte alpha = 255)
        {
            for (int j = y; j < y + height; j++)
                for (int i = x; i < x + width; i++) pixels[j * 64 + i] = new Color32(255, 255, 255, alpha);
        }

        private TextureAlphaMap Alpha() => new TextureAlphaMap(pixels, 64, 64, 64, 64);
        private FrameBoundsPlan Plan(FrameBoundsSettings settings = null)
        {
            return FrameBounds.Build(document, document.Regions.FindAll(r => selection.IsSelected(r.Id)),
                Alpha(), settings ?? new FrameBoundsSettings());
        }

        private static Vector2[] TextureShape(SpriteRegion region, ShapeKind kind)
        {
            return region.GetShapes(kind).SelectMany(p => p).Select(region.ShapePointToTexture).ToArray();
        }

        private void AddShapes()
        {
            var path = new[] { new Vector2(10, 11), new Vector2(14, 11), new Vector2(14, 14), new Vector2(10, 14) }
                .Select(First.TextureToShapePoint).ToArray();
            First.Outlines.Add(path);
            First.PhysicsOutlines.Add((Vector2[])path.Clone());
            First.OutlineTessellation = 0.35f;
            First.PhysicsTessellation = 0.65f;
        }

        [TestCase(0, 10, 11, 4, 3)]
        [TestCase(1, 9, 10, 6, 5)]
        [TestCase(20, 8, 8, 12, 10)]
        public void Trim_UsesAlphaAndCapsPaddingToOriginalRect(int padding, int x, int y, int w, int h)
        {
            var before = First.Clone();
            var plan = Plan(new FrameBoundsSettings { Padding = padding });
            Assert.That(plan.Entries[0].ProposedRect, Is.EqualTo(new Rect(x, y, w, h)));
            Assert.That(plan.EmptyCount, Is.EqualTo(1));
            Assert.That(FrameBounds.SameRegion(First, before), Is.True);
            Assert.That(document.IsDirty || history.CanUndo, Is.False);
            Assert.That(plan.CanApply, Is.EqualTo(padding != 20));
        }

        [Test]
        public void Trim_AlphaThresholdIsStrictAndSinglePixelSurvives()
        {
            pixels = new Color32[pixels.Length];
            Paint(9, 9, 1, 1, 20);
            Paint(12, 12, 1, 1, 21);
            var plan = Plan(new FrameBoundsSettings { AlphaThreshold = 20 });
            Assert.That(plan.Entries[0].ProposedRect, Is.EqualTo(new Rect(12, 12, 1, 1)));
            Assert.That(plan.CanApply, Is.True);
            pixels = new Color32[pixels.Length];
            plan = Plan();
            Assert.That(plan.EmptyCount, Is.EqualTo(2));
            Assert.That(plan.CreateCommand(document), Is.Null);
        }

        [Test]
        public void Trim_PreservesFractionalOutsidePivotShapesBordersIdentityAndRecovery()
        {
            AddShapes();
            First.Alignment = SpriteAlignment.Custom;
            First.Pivot = new Vector2(-0.12345f, 1.23456f);
            First.Border = new Vector4(3, 4, 7, 5); // Texture lines x=11/13 and y=12/13.
            First.CustomData = "retain";
            var original = First.Clone();
            var other = Second.Clone();
            var plan = Plan();
            Assert.That(plan.CanApply, Is.True, plan.Entries[0].Issue);
            history.Do(plan.CreateCommand(document));
            Assert.That(history.Position, Is.EqualTo(1));
            Assert.That(Vector2.Distance(First.GetPivotTexturePosition(), original.GetPivotTexturePosition()), Is.LessThan(0.0001f));
            Assert.That(First.Border, Is.EqualTo(new Vector4(1, 1, 1, 1)));
            Assert.That(First.Id, Is.EqualTo(original.Id));
            Assert.That(First.Name, Is.EqualTo(original.Name));
            Assert.That(First.CustomData, Is.EqualTo(original.CustomData));
            Assert.That(First.OutlineTessellation, Is.EqualTo(original.OutlineTessellation));
            Assert.That(First.PhysicsTessellation, Is.EqualTo(original.PhysicsTessellation));
            foreach (var kind in new[] { ShapeKind.Outline, ShapeKind.Physics })
                Assert.That(TextureShape(First, kind), Is.EqualTo(TextureShape(original, kind)));
            Assert.That(FrameBounds.SameRegion(Second, other), Is.True);
            history.MarkSavePoint();
            var dto = DocumentSnapshot.Capture(document, history, selection);
            dto = JsonUtility.FromJson<DocumentSnapshotDto>(JsonUtility.ToJson(dto));
            var restored = DocumentSnapshot.RestoreDocument(dto, null);
            var restoredHistory = new HistoryStack(restored);
            DocumentSnapshot.RestoreHistory(dto.History, restored, restoredHistory);
            restoredHistory.Undo();
            Assert.That(FrameBounds.SameRegion(restored.Regions[0], original), Is.True);
            Assert.That(restored.IsDirty, Is.True);
            restoredHistory.Redo();
            Assert.That(FrameBounds.SameRegion(restored.Regions[0], First), Is.True);
            Assert.That(restored.IsDirty, Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Trim_RejectsClippedShapeWithoutMutation(bool physics)
        {
            First.GetShapes(physics ? ShapeKind.Physics : ShapeKind.Outline).Add(new[] { new Vector2(-6, -5), Vector2.zero, Vector2.one });
            var before = First.Clone();
            var plan = Plan();
            Assert.That(plan.Entries[0].Issue, Does.Contain("clip"));
            Assert.That(plan.CreateCommand(document), Is.Null);
            Assert.That(FrameBounds.SameRegion(First, before), Is.True);
        }

        [Test]
        public void Trim_RejectsCutBorderAndMissingBorderPermission()
        {
            First.Border = new Vector4(1, 0, 0, 0);
            Assert.That(Plan().Entries[0].Issue, Does.Contain("border line"));
            First.Border = new Vector4(3, 0, 0, 0);
            document.Capabilities &= ~SpriteEditCapabilities.EditBorder;
            Assert.That(Plan().Entries[0].Issue, Does.Contain("border editing"));
            First.Border = Vector4.zero;
            Assert.That(Plan().CanApply, Is.True);
        }

        [Test]
        public void Trim_RequiresProviderForShapeTranslation()
        {
            AddShapes();
            document.HasOutlineProvider = false;
            Assert.That(Plan().Entries[0].Issue, Does.Contain("provider"));
        }

        [Test]
        public void Uniform_UsesCommonPivotRelativeExtentsAndOneUndoStep()
        {
            Paint(34, 10, 2, 4);
            AddShapes();
            var before = document.Regions.Select(r => r.Clone()).ToArray();
            var plan = Plan(new FrameBoundsSettings { Mode = FrameBoundsMode.UniformFrameBounds });
            Assert.That(plan.CanApply, Is.True, plan.Error);
            Assert.That(plan.UniformSize, Is.EqualTo(new Vector2Int(12, 19)));
            history.Do(plan.CreateCommand(document));
            for (int i = 0; i < before.Length; i++)
            {
                var current = document.Regions[i];
                Assert.That(current.Rect.size, Is.EqualTo(new Vector2(12, 19)));
                Assert.That(current.GetPivotTexturePosition(), Is.EqualTo(before[i].GetPivotTexturePosition()));
                Assert.That(current.GetPivotTexturePosition() - current.Rect.min, Is.EqualTo(new Vector2(6, 5)));
                Assert.That(TextureShape(current, ShapeKind.Outline), Is.EqualTo(TextureShape(before[i], ShapeKind.Outline)));
            }
            Assert.That(history.Position, Is.EqualTo(1));
            history.Undo();
            Assert.That(document.Regions.Zip(before, FrameBounds.SameRegion).All(equal => equal), Is.True);
            Assert.That(document.IsDirty, Is.False);
        }

        [Test]
        public void Uniform_ExplicitSizeAndPaddingAddTransparentSpace()
        {
            var settings = new FrameBoundsSettings
            {
                Mode = FrameBoundsMode.UniformFrameBounds, Padding = 1,
                AutomaticSize = false, Size = new Vector2Int(16, 23)
            };
            var plan = Plan(settings);
            Assert.That(plan.CanApply, Is.True, plan.Error);
            Assert.That(plan.Entries[0].ProposedRect, Is.EqualTo(new Rect(6, 6, 16, 23)));
            settings.Size = new Vector2Int(12, 19);
            Assert.That(Plan(settings).Error, Does.Contain("at least 14 × 21"));
        }

        [Test]
        public void Uniform_PreservesOutsidePivotsOnCompatibleFractionalGrid()
        {
            foreach (var region in document.Regions)
            {
                region.Alignment = SpriteAlignment.Custom;
                region.Pivot = new Vector2(1.25f, -0.25f);
            }
            var plan = Plan(new FrameBoundsSettings { Mode = FrameBoundsMode.UniformFrameBounds });
            Assert.That(plan.CanApply, Is.True, plan.Error);
            foreach (var entry in plan.Entries.Where(e => e.Changed))
                Assert.That(entry.After.GetPivotTexturePosition(), Is.EqualTo(entry.Before.GetPivotTexturePosition()));
            Second.Pivot = new Vector2(1.25f, -0.3f);
            Assert.That(Plan(new FrameBoundsSettings { Mode = FrameBoundsMode.UniformFrameBounds }).Error,
                Does.Contain("fractional pixel grids"));
        }

        [Test]
        public void Uniform_ConflictsBlockWholeBatchForNeighborArtworkOrTextureEdge()
        {
            var settings = new FrameBoundsSettings { Mode = FrameBoundsMode.UniformFrameBounds };
            Paint(30, 4, 1, 1, 1); // Even faint new artwork is protected.
            var plan = Plan(settings);
            Assert.That(plan.Entries[0].Changed, Is.True);
            Assert.That(plan.Entries[1].Issue, Does.Contain("artwork"));
            Assert.That(plan.CreateCommand(document), Is.Null);
            settings.AutomaticSize = false;
            settings.Size = new Vector2Int(64, 64);
            plan = Plan(settings);
            Assert.That(plan.Entries.Any(e => e.Issue != null && e.Issue.Contains("outside")), Is.True);
            Assert.That(plan.CanApply, Is.False);
        }

        [Test]
        public void Build_RejectsRestrictedCapabilitiesSingleModeAndDownsampledAlpha()
        {
            document.Capabilities = SpriteEditCapabilities.EditSpriteRect;
            Assert.That(Plan().Error, Does.Contain("editing support"));
            var single = new SpriteDocument("Assets", null, 64, 64, 100, SpriteImportMode.Single);
            Assert.That(FrameBounds.Build(single, document.Regions, Alpha(), new FrameBoundsSettings()).Error,
                Does.Contain("Multiple"));
            document.Capabilities = SpriteEditCapabilities.All;
            var reduced = new TextureAlphaMap(new Color32[32 * 32], 32, 32, 64, 64);
            Assert.That(FrameBounds.Build(document, document.Regions, reduced, new FrameBoundsSettings()).Error,
                Does.Contain("Full-resolution"));
        }

        [Test]
        public void Plan_RejectsStaleMetadataAndChangedCapabilities()
        {
            AddShapes();
            var plan = Plan();
            First.PhysicsOutlines[0][0] += Vector2.one;
            Assert.That(plan.CreateCommand(document), Is.Null);
            plan = Plan();
            document.Capabilities = SpriteEditCapabilities.None;
            Assert.That(plan.CreateCommand(document), Is.Null);
        }

        [TestCase(8.25f)]
        [TestCase(7.99995f)]
        public void Build_RejectsFractionalSourceRectsInsteadOfSamplingAdjacentPixels(float x)
        {
            First.Rect = new Rect(x, 8, 12, 10);
            var plan = Plan();
            Assert.That(plan.Entries[0].Issue, Does.Contain("whole-pixel"));
            Assert.That(plan.CreateCommand(document), Is.Null);
        }

        [Test]
        public void Uniform_AlreadyUniformFramesDoNotDirtyOrConvertPresets()
        {
            Second.Rect = new Rect(32, 8, 12, 10);
            Second.Alignment = SpriteAlignment.Center;
            var plan = Plan(new FrameBoundsSettings { Mode = FrameBoundsMode.UniformFrameBounds });
            Assert.That(plan.ChangedCount, Is.Zero);
            Assert.That(plan.CreateCommand(document), Is.Null);
            Assert.That(First.Alignment, Is.EqualTo(SpriteAlignment.Center));
            Assert.That(history.CanUndo || document.IsDirty, Is.False);
        }

        private BoundsPanelElement Panel(out List<Rect>[] captured)
        {
            panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            panelHost = new GameObject("Bounds panel test", typeof(UIDocument));
            var ui = panelHost.GetComponent<UIDocument>();
            ui.panelSettings = panelSettings;
            var panel = new BoundsPanelElement();
            ui.rootVisualElement.Add(panel);
            var capture = new List<Rect>[1];
            panel.Show(document, selection, history, Alpha, rects => capture[0] = rects);
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
        public void Panel_PreviewsCurrentSelectionCommitsOnceAndCancelsCleanly()
        {
            var panel = Panel(out var preview);
            Assert.That(preview[0][0], Is.EqualTo(new Rect(10, 11, 4, 3)));
            Assert.That(history.CanUndo || document.IsDirty, Is.False);
            selection.Set(Second.Id);
            Assert.That(panel.Q<Button>("bounds-commit").enabledSelf, Is.False);
            selection.Set(First.Id);
            panel.Q<IntegerField>("bounds-padding").value = 1;
            Assert.That(preview[0][0], Is.EqualTo(new Rect(9, 10, 6, 5)));
            Click(panel.Q<Button>("bounds-commit"));
            Assert.That(history.Position, Is.EqualTo(1));
            Assert.That(First.Rect, Is.EqualTo(new Rect(9, 10, 6, 5)));
            Assert.That(preview[0], Is.Null);
            panel.Show(document, selection, history, Alpha, rects => preview[0] = rects);
            Click(panel.Q<Button>("bounds-cancel"));
            history.Undo();
            Assert.That(preview[0], Is.Null);
            Assert.That(document.IsDirty, Is.False);
        }

        [Test]
        public void Panel_ShowsUniformModeAndConflictWithoutEditing()
        {
            var panel = Panel(out _);
            panel.Q<EnumField>("bounds-mode").value = FrameBoundsMode.UniformFrameBounds;
            Assert.That(panel.Q<Label>("bounds-summary").text, Does.Contain("12 × 19"));
            panel.Q<Toggle>("bounds-auto-size").value = false;
            panel.Q<Vector2IntField>("bounds-size").value = new Vector2Int(1, 1);
            Assert.That(panel.Q<Button>("bounds-commit").enabledSelf, Is.False);
            Assert.That(panel.Q<Label>("bounds-summary").text, Does.Contain("at least"));
            Assert.That(history.CanUndo || document.IsDirty, Is.False);
        }

        [Test]
        public void Panel_RetargetsAndReportsPixelReadFailureWithoutRetainingOldSubscriptions()
        {
            var panel = Panel(out var oldPreview);
            var newDocument = new SpriteDocument("Assets", null, 64, 64, 100, SpriteImportMode.Multiple);
            newDocument.Regions.Add(First.Clone());
            var newSelection = new SelectionModel();
            newSelection.Set(First.Id);
            int callbacks = 0;
            panel.Show(newDocument, newSelection, new HistoryStack(newDocument),
                () => throw new System.InvalidOperationException("fixture read failure"), _ => callbacks++);
            Assert.That(oldPreview[0], Is.Null);
            Assert.That(panel.Q<Label>("bounds-summary").text, Does.Contain("fixture read failure"));
            Assert.That(panel.Q<Button>("bounds-commit").enabledSelf, Is.False);
            int count = callbacks;
            document.MarkDirty();
            selection.Clear();
            Assert.That(callbacks, Is.EqualTo(count));
        }
    }
}
