using System.Linq;
using BetterSpriteEditor.DataAccess;
using BetterSpriteEditor.Model;
using BetterSpriteEditor.Operations;
using BetterSpriteEditor.Operations.Pivots;
using BetterSpriteEditor.UI.Inspector;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace BetterSpriteEditor.Tests
{
    public class RelativePivotTests
    {
        private const string unitPreference = "BetterSpriteEditor.PivotUnitMode";
        private const string keepPreference = "BetterSpriteEditor.KeepPivotPosition";
        private bool hadUnitPreference, hadKeepPreference, oldKeepPreference;
        private int oldUnitPreference;
        private SpriteDocument document;
        private SelectionModel selection;
        private HistoryStack history;
        private GameObject inspectorHost;
        private PanelSettings inspectorPanel;

        [SetUp]
        public void SetUp()
        {
            hadUnitPreference = EditorPrefs.HasKey(unitPreference);
            oldUnitPreference = EditorPrefs.GetInt(unitPreference);
            hadKeepPreference = EditorPrefs.HasKey(keepPreference);
            oldKeepPreference = EditorPrefs.GetBool(keepPreference);
            document = new SpriteDocument("Assets", null, 256, 256, 100, SpriteImportMode.Multiple)
            {
                AssetGuid = GUID.Generate().ToString(), BaselineRevision = "relative-pivot-test"
            };
            document.Regions.Add(new SpriteRegion
            {
                Id = GUID.Generate(), Name = "preset", Rect = new Rect(10, 20, 20, 40),
                Alignment = SpriteAlignment.TopCenter, Pivot = new Vector2(0.13f, 0.87f),
                Border = new Vector4(0.25f, 1, 2, 3), CustomData = "preserved"
            });
            document.Regions.Add(new SpriteRegion
            {
                Id = GUID.Generate(), Name = "outside", Rect = new Rect(80, 50, 32, 16),
                Alignment = SpriteAlignment.Custom, Pivot = new Vector2(-0.25f, 1.5f)
            });
            document.Regions.Add(new SpriteRegion
            {
                Id = GUID.Generate(), Name = "unselected", Rect = new Rect(150, 20, 10, 10),
                Alignment = SpriteAlignment.BottomRight
            });
            document.Regions[0].Outlines.Add(new[] { Vector2.zero, Vector2.right, Vector2.up });
            document.Regions[0].PhysicsOutlines.Add(new[] { Vector2.one, Vector2.left, Vector2.down });
            selection = new SelectionModel();
            selection.SetRange(document.Regions.Take(2).Select(r => r.Id));
            history = new HistoryStack(document);
        }

        [TearDown]
        public void TearDown()
        {
            if (inspectorHost != null) Object.DestroyImmediate(inspectorHost);
            if (inspectorPanel != null) Object.DestroyImmediate(inspectorPanel);
            if (hadUnitPreference) EditorPrefs.SetInt(unitPreference, oldUnitPreference);
            else EditorPrefs.DeleteKey(unitPreference);
            if (hadKeepPreference) EditorPrefs.SetBool(keepPreference, oldKeepPreference);
            else EditorPrefs.DeleteKey(keepPreference);
        }

        private IEditorCommand Command(Vector2 offset, out string error)
        {
            return PivotOffset.CreateCommand(document,
                document.Regions.Where(r => selection.IsSelected(r.Id)).ToArray(), offset, out error);
        }

        private static void AssertAnchor(SpriteRegion region, Vector2 expected)
        {
            Assert.That(Vector2.Distance(region.GetPivotTexturePosition(), expected), Is.LessThan(0.0001f));
        }

        [TestCase(2f, -3f)]
        [TestCase(-0.25f, 0.125f)]
        [TestCase(50f, -80f)]
        public void Shift_MixedPivotsAndUnequalRectsMoveBySamePixelDelta(float x, float y)
        {
            var originals = document.Regions.Select(r => r.Clone()).ToArray();
            var delta = new Vector2(x, y);
            var command = Command(delta, out string error);
            Assert.That(error, Is.Null);
            Assert.That(command, Is.Not.Null);
            Assert.That(document.IsDirty, Is.False);
            Assert.That(document.Regions[0].Alignment, Is.EqualTo(SpriteAlignment.TopCenter));
            history.Do(command);
            Assert.That(history.Position, Is.EqualTo(1));
            for (int i = 0; i < originals.Length; i++)
            {
                var region = document.Regions[i];
                AssertAnchor(region, originals[i].GetPivotTexturePosition() + (i < 2 ? delta : Vector2.zero));
                Assert.That(region.Id, Is.EqualTo(originals[i].Id));
                Assert.That(region.Name, Is.EqualTo(originals[i].Name));
                Assert.That(region.Rect, Is.EqualTo(originals[i].Rect));
                Assert.That(region.Border, Is.EqualTo(originals[i].Border));
                Assert.That(region.CustomData, Is.EqualTo(originals[i].CustomData));
                Assert.That(region.Outlines, Is.EqualTo(originals[i].Outlines));
                Assert.That(region.PhysicsOutlines, Is.EqualTo(originals[i].PhysicsOutlines));
            }
            Assert.That(document.Regions.Take(2).All(r => r.Alignment == SpriteAlignment.Custom), Is.True);
            history.Undo();
            Assert.That(document.IsDirty, Is.False);
            for (int i = 0; i < originals.Length; i++)
            {
                Assert.That(document.Regions[i].Alignment, Is.EqualTo(originals[i].Alignment));
                Assert.That(document.Regions[i].Pivot, Is.EqualTo(originals[i].Pivot));
            }
            history.Redo();
            AssertAnchor(document.Regions[0], originals[0].GetPivotTexturePosition() + delta);
        }

        [Test]
        public void Shift_HistorySurvivesRecoveryAndUndoAfterSavePoint()
        {
            var original = document.Regions[0].Clone();
            history.Do(Command(new Vector2(2, -1), out _));
            history.MarkSavePoint();
            var snapshot = DocumentSnapshot.Capture(document, history, selection);
            snapshot = JsonUtility.FromJson<DocumentSnapshotDto>(JsonUtility.ToJson(snapshot));
            var restored = DocumentSnapshot.RestoreDocument(snapshot, null);
            var restoredHistory = new HistoryStack(restored);
            DocumentSnapshot.RestoreHistory(snapshot.History, restored, restoredHistory);
            restoredHistory.Undo();
            Assert.That(restored.IsDirty, Is.True);
            Assert.That(restored.Regions[0].Alignment, Is.EqualTo(original.Alignment));
            Assert.That(restored.Regions[0].Pivot, Is.EqualTo(original.Pivot));
            restoredHistory.Redo();
            Assert.That(restored.IsDirty, Is.False);
            AssertAnchor(restored.Regions[0], original.GetPivotTexturePosition() + new Vector2(2, -1));
        }

        [TestCase(0f)]
        [TestCase(1e-30f)]
        public void Shift_UnchangedPivotCreatesNoCommandOrPresetConversion(float offset)
        {
            Assert.That(Command(new Vector2(offset, offset), out string error), Is.Null);
            Assert.That(error, Is.Null);
            Assert.That(document.Regions[0].Alignment, Is.EqualTo(SpriteAlignment.TopCenter));
            Assert.That(history.CanUndo || document.IsDirty, Is.False);
        }

        [TestCase(float.NaN, 1f)]
        [TestCase(1f, float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity, 1f)]
        public void Shift_NonFiniteOffsetIsRejected(float x, float y)
        {
            Assert.That(Command(new Vector2(x, y), out string error), Is.Null);
            Assert.That(error, Is.Not.Empty);
            Assert.That(document.IsDirty, Is.False);
        }

        [TestCase(0f, 1f)]
        [TestCase(float.NaN, 1f)]
        [TestCase(1e-30f, float.MaxValue)]
        public void Shift_InvalidSecondRegionRejectsWholeSelection(float width, float offset)
        {
            document.Regions[1].Rect = new Rect(80, 50, width, 16);
            var first = document.Regions[0].Clone();
            Assert.That(Command(new Vector2(offset, 1), out string error), Is.Null);
            Assert.That(error, Is.Not.Empty);
            Assert.That(document.Regions[0].Pivot, Is.EqualTo(first.Pivot));
            Assert.That(document.Regions[0].Alignment, Is.EqualTo(first.Alignment));
            Assert.That(document.IsDirty, Is.False);
        }

        [Test]
        public void Shift_SingleModeNeedsOnlyPivotCapability()
        {
            var region = document.Regions[0];
            var anchor = region.GetPivotTexturePosition();
            document = new SpriteDocument("Assets", null, 256, 256, 100, SpriteImportMode.Single)
            {
                Capabilities = SpriteEditCapabilities.EditPivot
            };
            document.Regions.Add(region);
            history = new HistoryStack(document);
            history.Do(Command(new Vector2(3, -2), out string error));
            Assert.That(error, Is.Null);
            AssertAnchor(region, anchor + new Vector2(3, -2));
            document.Capabilities = SpriteEditCapabilities.EditSpriteRect;
            Assert.That(Command(Vector2.one, out error), Is.Null);
            Assert.That(error, Is.Not.Empty);
        }

        private RegionInspectorElement Inspector()
        {
            inspectorPanel = ScriptableObject.CreateInstance<PanelSettings>();
            inspectorHost = new GameObject("Relative pivot inspector test", typeof(UIDocument));
            var uiDocument = inspectorHost.GetComponent<UIDocument>();
            uiDocument.panelSettings = inspectorPanel;
            var inspector = new RegionInspectorElement();
            uiDocument.rootVisualElement.Add(inspector);
            inspector.Bind(document, selection, history);
            return inspector;
        }

        private static void Submit(Button button)
        {
            using var evt = NavigationSubmitEvent.GetPooled();
            evt.target = button;
            button.SendEvent(evt);
        }

        [TestCase(PivotUnitMode.Normalized)]
        [TestCase(PivotUnitMode.Pixels)]
        public void Inspector_OffsetWaitsForClickAndRepeatsInPixels(PivotUnitMode mode)
        {
            EditorPrefs.SetInt(unitPreference, (int)mode);
            EditorPrefs.SetBool(keepPreference, true);
            var inspector = Inspector();
            var field = inspector.Q<Vector2Field>("pivot-offset");
            var button = inspector.Q<Button>("shift-pivots");
            Assert.That(button.enabledSelf, Is.False);
            var anchors = document.Regions.Select(r => r.GetPivotTexturePosition()).ToArray();
            var offset = new Vector2(2, -0.5f);
            field.value = offset;
            Assert.That(button.enabledSelf, Is.True);
            Assert.That(history.CanUndo || document.IsDirty, Is.False);
            Submit(button);
            Assert.That(history.Position, Is.EqualTo(1));
            Assert.That(field.value, Is.EqualTo(offset));
            Submit(button);
            Assert.That(history.Position, Is.EqualTo(2));
            for (int i = 0; i < 2; i++) AssertAnchor(document.Regions[i], anchors[i] + 2 * offset);
            AssertAnchor(document.Regions[2], anchors[2]);
            history.Undo();
            for (int i = 0; i < 2; i++) AssertAnchor(document.Regions[i], anchors[i] + offset);
            selection.Set(document.Regions[2].Id);
            Submit(button);
            AssertAnchor(document.Regions[2], anchors[2] + offset);
        }

        [Test]
        public void Inspector_DisablesShiftForInvalidOffsetMissingSelectionOrCapability()
        {
            var inspector = Inspector();
            var field = inspector.Q<Vector2Field>("pivot-offset");
            var button = inspector.Q<Button>("shift-pivots");
            field.value = new Vector2(float.NaN, 1);
            Assert.That(button.enabledSelf, Is.False);
            field.value = Vector2.one;
            selection.Clear();
            Assert.That(button.enabledSelf || field.enabledSelf, Is.False);
            selection.Set(document.Regions[0].Id);
            document.Capabilities = SpriteEditCapabilities.EditSpriteRect;
            inspector.Refresh();
            Assert.That(button.enabledSelf || field.enabledSelf, Is.False);
            Submit(button);
            Assert.That(history.CanUndo || document.IsDirty, Is.False);
        }
    }
}
