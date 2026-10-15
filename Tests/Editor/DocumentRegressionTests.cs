using System;
using System.Collections.Generic;
using System.IO;
using BetterSpriteEditor.DataAccess;
using BetterSpriteEditor.Model;
using BetterSpriteEditor.Operations;
using BetterSpriteEditor.Operations.Renaming;
using BetterSpriteEditor.Operations.Shapes;
using BetterSpriteEditor.Operations.Slicing;
using BetterSpriteEditor.Operations.Usages;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace BetterSpriteEditor.Tests
{
    public class DocumentRegressionTests
    {
        private static SpriteDocument Document()
        {
            return new SpriteDocument("", null, 32, 32, 100, SpriteImportMode.Multiple)
            {
                AssetGuid = GUID.Generate().ToString(), BaselineRevision = "test-revision"
            };
        }

        private static SpriteRegion Region(string name = "sprite") => new()
        {
            Id = GUID.Generate(), Name = name, Rect = new Rect(0, 0, 10, 10),
            Pivot = new Vector2(0.5f, 0.5f), Alignment = SpriteAlignment.Custom
        };

        private static (SpriteDocument, HistoryStack) RoundTrip(SpriteDocument document, HistoryStack history)
        {
            var dto = DocumentSnapshot.Capture(document, history, new SelectionModel());
            dto = JsonUtility.FromJson<DocumentSnapshotDto>(JsonUtility.ToJson(dto));
            var restored = DocumentSnapshot.RestoreDocument(dto, null);
            var restoredHistory = new HistoryStack(restored);
            DocumentSnapshot.RestoreHistory(dto.History, restored, restoredHistory);
            return (restored, restoredHistory);
        }

        [TestCase(0, true, true)]
        [TestCase(0, false, false)]
        [TestCase(1, true, false)]
        [TestCase(2, true, true)]
        public void Session_TargetChangeRetainsEditsOnCancelOrFailedSave(int choice, bool saved, bool expected)
        {
            bool discarded = false;
            bool result = SessionTransition.TryLeave(true, () => choice, () => saved, () => discarded = true);
            Assert.That(result, Is.EqualTo(expected));
            Assert.That(discarded, Is.EqualTo(expected));
        }

        [Test]
        public void Recovery_CreateThenDelete_UndoAndRedoPreserveIdentity()
        {
            var document = Document();
            var history = new HistoryStack(document);
            var region = Region();
            history.Do(new CreateRegionCommand(document, region));
            history.Do(new DeleteRegionsCommand(document, new[] { region }));
            var (restored, commands) = RoundTrip(document, history);
            commands.Undo();
            Assert.That(restored.Regions.Count, Is.EqualTo(1));
            commands.Undo();
            Assert.That(restored.Regions, Is.Empty);
            Assert.That(restored.IsDirty, Is.False);
            commands.Redo();
            commands.Redo();
            Assert.That(restored.Regions, Is.Empty);
        }

        [Test]
        public void Recovery_UndoneCreateAndDelete_RedoThenUndoPreservesIdentity()
        {
            var document = Document();
            var history = new HistoryStack(document);
            var region = Region();
            history.Do(new CreateRegionCommand(document, region));
            history.Do(new DeleteRegionsCommand(document, new[] { region }));
            history.Undo();
            history.Undo();
            var (restored, commands) = RoundTrip(document, history);
            commands.Redo();
            commands.Redo();
            Assert.That(restored.Regions, Is.Empty);
            commands.Undo();
            commands.Undo();
            Assert.That(restored.Regions, Is.Empty);
        }

        [Test]
        public void Recovery_ReorderRetainsUndoAndDirtySavePoint()
        {
            var document = Document();
            var a = Region("A"); var b = Region("B");
            document.Regions.AddRange(new[] { a, b });
            var history = new HistoryStack(document);
            var after = a.Clone(); after.Name = "renamed";
            history.Do(new ModifyRegionCommand(document, a.Id, a.Clone(), after, "Rename"));
            history.MarkSavePoint();
            history.Do(new ReorderRegionCommand(document, a.Id, 0, 1));
            var (restored, commands) = RoundTrip(document, history);
            Assert.That(restored.IsDirty, Is.True);
            Assert.That(restored.Regions[0].Id, Is.EqualTo(b.Id));
            commands.Undo();
            Assert.That(restored.Regions[0].Id, Is.EqualTo(a.Id));
            Assert.That(restored.IsDirty, Is.False);
        }

        [Test]
        public void Recovery_CapsRedoWithoutClaimingAnUnreachableSavePoint()
        {
            var document = Document();
            var region = Region(); document.Regions.Add(region);
            var history = new HistoryStack(document);
            for (int i = 0; i < 120; i++)
            {
                var after = region.Clone(); after.Name = "sprite" + i;
                history.Do(new ModifyRegionCommand(document, region.Id, region.Clone(), after, "Rename"));
            }
            history.MarkSavePoint();
            history.JumpTo(0);
            var (_, restored) = RoundTrip(document, history);
            Assert.That(restored.RedoEntries.Count, Is.EqualTo(100));
            Assert.That(restored.SavePointDepth, Is.EqualTo(-1));
        }

        [TestCase("history")]
        [TestCase("selection")]
        [TestCase("version")]
        [TestCase("command")]
        public void Recovery_MalformedDataIsRejectedBeforeRestore(string corruption)
        {
            var document = Document(); var history = new HistoryStack(document);
            history.Do(new CreateRegionCommand(document, Region()));
            var dto = DocumentSnapshot.Capture(document, history, new SelectionModel());
            switch (corruption)
            {
                case "history": dto.History = null; break;
                case "selection": dto.SelectedIds = new[] { "bad-guid" }; break;
                case "version": dto.Version = 999; break;
                case "command": dto.History.Undo[0].Leaf.Kind = 99; break;
            }
            Assert.Throws<InvalidDataException>(() => DocumentSnapshot.RestoreDocument(dto, null));
        }

        [Test]
        public void Grid_CountModeHonorsRequestedCountsWhenDimensionsDoNotDivide()
        {
            var result = GridSlicer.Slice(new GridSliceSettings { ByCellCount = true, Columns = 6, Rows = 6 }, 10, 10, null);
            Assert.That(result.Count, Is.EqualTo(36));
            Assert.That(result[0], Is.EqualTo(new Rect(0, 9, 1, 1)));
            Assert.That(result[^1], Is.EqualTo(new Rect(5, 4, 1, 1)));
        }

        [Test]
        public void Grid_SizeModeStillFillsAvailableSpace()
        {
            Assert.That(GridSlicer.Slice(new GridSliceSettings { CellWidth = 1, CellHeight = 1 }, 10, 10, null).Count,
                Is.EqualTo(100));
        }

        [Test]
        public void Grid_NegativePaddingCannotCreateAnInfiniteLoop()
        {
            Assert.That(GridSlicer.Slice(new GridSliceSettings { CellWidth = 1, CellHeight = 1, PaddingX = -1 },
                10, 10, null), Is.Empty);
        }

        [TestCase(0.525f, 0f)]
        [TestCase(1.25f, -10f)]
        public void SharedShape_UsesExactUnclampedPivot(float pivot, float sourceX)
        {
            var region = Region(); region.Pivot = new Vector2(pivot, 0.5f);
            var path = new[] { new Vector2(sourceX, -1), new Vector2(sourceX + 1, -1), new Vector2(sourceX, 1) };
            var result = SharedShapeBuilder.ToRegionSpace(path, region);
            Assert.That(result[0].x, Is.EqualTo(sourceX + pivot * 10 - 5).Within(0.00001f));
        }

        [Test]
        public void SharedShape_FractionalFramesProduceIdenticalPivotSpaceGeometry()
        {
            var pixels = new Color32[16 * 8];
            for (int y = 2; y < 4; y++)
                for (int x = 2; x < 4; x++)
                {
                    pixels[y * 16 + x] = new Color32(255, 255, 255, 255);
                    pixels[y * 16 + x + 8] = new Color32(255, 255, 255, 255);
                }
            var a = Region("A"); a.Rect = new Rect(0, 0, 8, 8); a.Pivot = new Vector2(0.525f, 0.5f);
            var b = Region("B"); b.Rect = new Rect(8, 0, 8, 8); b.Pivot = new Vector2(0.6f, 0.5f);
            var settings = new ShapeGenerationSettings { Detail = 1, Outset = 0, MinimumBlobSize = 1 };
            var shared = SharedShapeBuilder.Build(new TextureAlphaMap(pixels, 16, 8, 16, 8), new[] { a, b }, settings);
            Assert.That(shared, Is.Not.Empty);
            foreach (var path in shared)
            {
                var first = SharedShapeBuilder.ToRegionSpace(path, a);
                var second = SharedShapeBuilder.ToRegionSpace(path, b);
                Assert.That(first.Length, Is.EqualTo(second.Length));
                for (int i = 0; i < first.Length; i++)
                {
                    var inFirstPivotSpace = first[i] - Vector2.Scale(a.Pivot - Vector2.one * 0.5f, a.Rect.size);
                    var inSecondPivotSpace = second[i] - Vector2.Scale(b.Pivot - Vector2.one * 0.5f, b.Rect.size);
                    Assert.That(Vector2.Distance(inFirstPivotSpace, inSecondPivotSpace), Is.LessThan(0.0001f));
                }
            }
        }

        [Test]
        public void SharedShape_RejectsClippingToASmallerFrame()
        {
            var path = new[] { new Vector2(-10, -1), new Vector2(10, -1), new Vector2(0, 1) };
            Assert.Throws<InvalidOperationException>(() => SharedShapeBuilder.ToRegionSpace(path, Region()));
        }

        [Test]
        public void Shape_RejectsZeroAreaPath()
        {
            Assert.That(OutlineGenerator.ToPathArray(new List<Vector2> { Vector2.zero, Vector2.right, Vector2.right * 2 }), Is.Null);
        }

        [Test]
        public void Usage_CancelledOrSkippedScansCannotReportNoReferences()
        {
            Assert.That(new UsageSearchResult().CanReportNoReferences, Is.False);
            Assert.That(new UsageSearchResult { Completed = true, SkippedAssets = 1 }.CanReportNoReferences, Is.False);
            Assert.That(new UsageSearchResult { Completed = true }.CanReportNoReferences, Is.True);
        }

        [Test]
        public void Rename_PathologicalRegexHasFiniteTimeout()
        {
            var rule = RenameRule.Default;
            rule.UseRegex = true; rule.FindText = "(a+)+$"; rule.ReplaceText = "x";
            Assert.Throws<ArgumentException>(() => rule.Apply(new string('a', 100) + "!", 0));
        }
    }
}
