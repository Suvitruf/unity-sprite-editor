using System.Linq;
using BetterSpriteEditor.DataAccess;
using BetterSpriteEditor.Model;
using BetterSpriteEditor.Operations;
using BetterSpriteEditor.Tools;
using BetterSpriteEditor.UI.Inspector;
using BetterSpriteEditor.UI.Viewport;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace BetterSpriteEditor.Tests
{
    public class PivotPreservingRectTests
    {
        private const string preferenceKey = "BetterSpriteEditor.KeepPivotPosition";
        private bool hadPreference;
        private bool oldPreference;
        private SpriteDocument document;
        private SpriteRegion region;
        private SelectionModel selection;
        private HistoryStack history;
        private ToolContext context;
        private SelectTool tool;
        private GameObject inspectorHost;
        private PanelSettings inspectorPanel;

        [SetUp]
        public void SetUp()
        {
            hadPreference = EditorPrefs.HasKey(preferenceKey);
            oldPreference = EditorPrefs.GetBool(preferenceKey);
            EditorPrefs.SetBool(preferenceKey, false);
            document = new SpriteDocument("Assets", null, 128, 128, 100, SpriteImportMode.Multiple)
            {
                AssetGuid = GUID.Generate().ToString(), BaselineRevision = "test-revision"
            };
            region = new SpriteRegion
            {
                Id = GUID.Generate(), Name = "frame", Rect = new Rect(40, 40, 40, 40),
                Alignment = SpriteAlignment.Center, Pivot = new Vector2(0.13f, 0.87f)
            };
            document.Regions.Add(region);
            selection = new SelectionModel();
            selection.Set(region.Id);
            history = new HistoryStack(document);
            var transform = new ViewportTransform();
            transform.SetTextureSize(128, 128);
            context = new ToolContext
            {
                Document = document, Selection = selection, History = history,
                Transform = transform, KeepPivotPosition = true
            };
            tool = new SelectTool(context);
        }

        [TearDown]
        public void TearDown()
        {
            if (inspectorHost != null)
                Object.DestroyImmediate(inspectorHost);
            if (inspectorPanel != null)
                Object.DestroyImmediate(inspectorPanel);
            if (hadPreference)
                EditorPrefs.SetBool(preferenceKey, oldPreference);
            else
                EditorPrefs.DeleteKey(preferenceKey);
        }

        private static void AssertAnchor(SpriteRegion target, Vector2 expected)
        {
            Assert.That(Vector2.Distance(target.GetPivotTexturePosition(), expected), Is.LessThan(0.0001f));
        }

        private Vector2 BeginResize(RectHandle handle = RectHandle.Right)
        {
            var point = RectHandles.GetHandleViewPoint(context.Transform.TextureRectToView(region.Rect), handle);
            // Border handles occupy edge midpoints, so grab the resize edge off-center.
            if (handle == RectHandle.Left || handle == RectHandle.Right)
                point.y += 10;
            else if (handle == RectHandle.Top || handle == RectHandle.Bottom)
                point.x += 10;
            using var evt = PointerDownEvent.GetPooled();
            Assert.That(tool.OnPointerDown(point, evt), Is.True);
            return point;
        }

        private RegionInspectorElement Inspector(bool keepPivot = true)
        {
            var inspector = new RegionInspectorElement();
            // Attach a real panel so field changes dispatch events, without opening a
            // native editor window (which requires a graphics device in batch tests).
            inspectorPanel = ScriptableObject.CreateInstance<PanelSettings>();
            inspectorHost = new GameObject("Pivot inspector test", typeof(UIDocument));
            var uiDocument = inspectorHost.GetComponent<UIDocument>();
            uiDocument.panelSettings = inspectorPanel;
            uiDocument.rootVisualElement.Add(inspector);
            inspector.Bind(document, selection, history);
            inspector.Q<Toggle>("keep-pivot-position").value = keepPivot;
            return inspector;
        }

        private static IntegerField Field(RegionInspectorElement inspector, string label)
        {
            return inspector.Query<IntegerField>().ToList().Single(field => field.label == label);
        }

        [TestCase(RectHandle.Left)]
        [TestCase(RectHandle.Right)]
        [TestCase(RectHandle.Top)]
        [TestCase(RectHandle.Bottom)]
        [TestCase(RectHandle.TopLeft)]
        [TestCase(RectHandle.TopRight)]
        [TestCase(RectHandle.BottomLeft)]
        [TestCase(RectHandle.BottomRight)]
        public void Drag_EachHandlePreservesAnchorAndUndoesAsOneEdit(RectHandle handle)
        {
            var before = region.Clone();
            var anchor = region.GetPivotTexturePosition();
            var point = BeginResize(handle);
            tool.OnPointerDrag(point + new Vector2(11.4f, -7.4f));
            AssertAnchor(region, anchor);
            tool.OnPointerDrag(point + new Vector2(17.4f, -12.4f));
            AssertAnchor(region, anchor);
            var resized = region.Rect;
            Assert.That(resized, Is.Not.EqualTo(before.Rect));
            tool.OnPointerUp(point, null); // Capture loss uses the same commit path.
            Assert.That(history.Position, Is.EqualTo(1));
            history.Undo();
            Assert.That(region.Rect, Is.EqualTo(before.Rect));
            Assert.That(region.Alignment, Is.EqualTo(before.Alignment));
            Assert.That(region.Pivot, Is.EqualTo(before.Pivot));
            Assert.That(document.IsDirty, Is.False);
            history.Redo();
            Assert.That(region.Rect, Is.EqualTo(resized));
            AssertAnchor(region, anchor);
        }

        [Test]
        public void Drag_ReturningToStartRestoresPresetWithoutDirtying()
        {
            var point = BeginResize();
            tool.OnPointerDrag(point + new Vector2(12, 0));
            Assert.That(region.Alignment, Is.EqualTo(SpriteAlignment.Custom));
            tool.OnPointerDrag(point);
            tool.OnPointerUp(point, null);
            Assert.That(region.Alignment, Is.EqualTo(SpriteAlignment.Center));
            Assert.That(region.Pivot, Is.EqualTo(new Vector2(0.13f, 0.87f)));
            Assert.That(history.CanUndo || document.IsDirty, Is.False);
        }

        [TestCase(0.52345f, 0.63125f)]
        [TestCase(-0.25f, 1.5f)]
        public void Drag_ClampedToOnePixelKeepsFractionalAndOutsidePivots(float x, float y)
        {
            region.Alignment = SpriteAlignment.Custom;
            region.Pivot = new Vector2(x, y);
            var anchor = region.GetPivotTexturePosition();
            var point = BeginResize();
            tool.OnPointerDrag(point + new Vector2(-200, 0));
            Assert.That(region.Rect.width, Is.EqualTo(1));
            AssertAnchor(region, anchor);
            Assert.That(Mathf.Abs(region.Pivot.x), Is.GreaterThan(1));
        }

        [TestCase(false, true)]
        [TestCase(true, false)]
        public void Drag_WithoutPreferenceOrPivotCapabilityUsesExistingBehavior(bool enabled, bool canEditPivot)
        {
            context.KeepPivotPosition = enabled;
            if (!canEditPivot)
                document.Capabilities = SpriteEditCapabilities.EditSpriteRect;
            var point = BeginResize();
            tool.OnPointerDrag(point + new Vector2(10, 0));
            tool.OnPointerUp(point, null);
            Assert.That(region.Rect.width, Is.EqualTo(50));
            Assert.That(region.Alignment, Is.EqualTo(SpriteAlignment.Center));
            Assert.That(region.Pivot, Is.EqualTo(new Vector2(0.13f, 0.87f)));
            AssertAnchor(region, new Vector2(65, 60));
        }

        [Test]
        public void Drag_PresetIsRetainedWhenItStillDescribesTheAnchor()
        {
            region.Alignment = SpriteAlignment.BottomLeft;
            var anchor = region.GetPivotTexturePosition();
            var point = BeginResize();
            tool.OnPointerDrag(point + new Vector2(10, 0));
            Assert.That(region.Rect.width, Is.EqualTo(50));
            AssertAnchor(region, anchor);
            Assert.That(region.Alignment, Is.EqualTo(SpriteAlignment.BottomLeft));
        }

        [Test]
        public void MoveAndNudge_KeepEachSelectedAnchorAndGroupUndo()
        {
            var second = region.Clone();
            second.Id = GUID.Generate();
            second.Name = "second";
            second.Rect = new Rect(88, 40, 32, 40);
            document.Regions.Add(second);
            selection.Add(second.Id);
            var anchors = document.Regions.Select(r => r.GetPivotTexturePosition()).ToArray();
            var point = context.Transform.TextureToView(new Vector2(48, 52));
            using (var evt = PointerDownEvent.GetPooled())
                Assert.That(tool.OnPointerDown(point, evt), Is.True);
            tool.OnPointerDrag(point + new Vector2(50, -4)); // Whole-group delta clamps to +8 X.
            tool.OnPointerUp(point, null);
            Assert.That(region.Rect.x, Is.EqualTo(48));
            Assert.That(second.Rect.x, Is.EqualTo(96));
            for (int i = 0; i < anchors.Length; i++)
                AssertAnchor(document.Regions[i], anchors[i]);
            Assert.That(history.Position, Is.EqualTo(1));
            history.Undo();
            using (var evt = KeyDownEvent.GetPooled(new Event { type = EventType.KeyDown, keyCode = KeyCode.LeftArrow }))
                Assert.That(tool.OnKeyDown(evt), Is.True);
            Assert.That(region.Rect.x, Is.EqualTo(39));
            Assert.That(second.Rect.x, Is.EqualTo(87));
            for (int i = 0; i < anchors.Length; i++)
                AssertAnchor(document.Regions[i], anchors[i]);
            history.Undo();
            Assert.That(region.Alignment, Is.EqualTo(SpriteAlignment.Center));
            Assert.That(second.Alignment, Is.EqualTo(SpriteAlignment.Center));
        }

        [TestCase("X", -20)]
        [TestCase("Y", 200)]
        [TestCase("W", 0)]
        [TestCase("H", 200)]
        public void Inspector_PreservesAnchorAfterClampingNumericRectEdits(string field, int value)
        {
            var anchor = region.GetPivotTexturePosition();
            var inspector = Inspector();
            Field(inspector, field).value = value;
            AssertAnchor(region, anchor);
            Assert.That(region.Rect.width, Is.GreaterThanOrEqualTo(1));
            Assert.That(region.Rect.height, Is.GreaterThanOrEqualTo(1));
            Assert.That(region.Rect.xMin, Is.GreaterThanOrEqualTo(0));
            Assert.That(region.Rect.yMax, Is.LessThanOrEqualTo(128));
            Assert.That(history.Position, Is.EqualTo(1));
        }

        [Test]
        public void Inspector_MultiResizeSurvivesRecoveryAndUndoAfterSavePoint()
        {
            var second = region.Clone();
            second.Id = GUID.Generate();
            second.Name = "second";
            second.Rect = new Rect(88, 40, 20, 24);
            second.Alignment = SpriteAlignment.Custom;
            second.Pivot = new Vector2(-0.25f, 1.5f);
            document.Regions.Add(second);
            selection.Add(second.Id);
            var originals = document.Regions.Select(r => r.Clone()).ToArray();
            var inspector = Inspector();
            Field(inspector, "W").value = 50;
            Assert.That(history.Position, Is.EqualTo(1));
            for (int i = 0; i < originals.Length; i++)
                AssertAnchor(document.Regions[i], originals[i].GetPivotTexturePosition());
            history.MarkSavePoint();
            var dto = DocumentSnapshot.Capture(document, history, selection);
            dto = JsonUtility.FromJson<DocumentSnapshotDto>(JsonUtility.ToJson(dto));
            var restored = DocumentSnapshot.RestoreDocument(dto, null);
            var restoredHistory = new HistoryStack(restored);
            DocumentSnapshot.RestoreHistory(dto.History, restored, restoredHistory);
            restoredHistory.Undo();
            Assert.That(restored.IsDirty, Is.True);
            for (int i = 0; i < originals.Length; i++)
            {
                Assert.That(restored.Regions[i].Rect, Is.EqualTo(originals[i].Rect));
                Assert.That(restored.Regions[i].Alignment, Is.EqualTo(originals[i].Alignment));
                Assert.That(restored.Regions[i].Pivot, Is.EqualTo(originals[i].Pivot));
            }
            restoredHistory.Redo();
            Assert.That(restored.IsDirty, Is.False);
            for (int i = 0; i < originals.Length; i++)
                AssertAnchor(restored.Regions[i], originals[i].GetPivotTexturePosition());
        }

        [Test]
        public void Inspector_ToggleIsRememberedWithoutEditingTheDocument()
        {
            var inspector = Inspector();
            Assert.That(inspector.KeepPivotPosition, Is.True);
            Assert.That(new RegionInspectorElement().KeepPivotPosition, Is.True);
            Assert.That(history.CanUndo || document.IsDirty, Is.False);
            inspector.Q<Toggle>("keep-pivot-position").value = false;
            Field(inspector, "W").value = 50;
            Assert.That(region.Alignment, Is.EqualTo(SpriteAlignment.Center));
            AssertAnchor(region, new Vector2(65, 60));
        }

        [Test]
        public void Inspector_UnsupportedPivotEditingDisablesToggleAndKeepsPivotMetadata()
        {
            EditorPrefs.SetBool(preferenceKey, true);
            document.Capabilities = SpriteEditCapabilities.EditSpriteRect;
            var inspector = Inspector(false);
            var toggle = inspector.Q<Toggle>("keep-pivot-position");
            Assert.That(toggle.enabledSelf, Is.False);
            Assert.That(toggle.value, Is.False);
            Field(inspector, "W").value = 50;
            Assert.That(region.Alignment, Is.EqualTo(SpriteAlignment.Center));
            Assert.That(region.Pivot, Is.EqualTo(new Vector2(0.13f, 0.87f)));
        }

        [Test]
        public void Inspector_UnchangedClampedRectDoesNotConvertPresetOrCreateHistory()
        {
            region.Rect = new Rect(0, 40, 40, 40);
            var inspector = Inspector();
            Field(inspector, "X").value = -10;
            Assert.That(region.Alignment, Is.EqualTo(SpriteAlignment.Center));
            Assert.That(history.CanUndo || document.IsDirty, Is.False);
        }
    }
}
