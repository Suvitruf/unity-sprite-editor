using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BetterSpriteEditor.DataAccess;
using BetterSpriteEditor.Model;
using BetterSpriteEditor.Operations.Usages;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.U2D;
using UnityEditor.U2D.Sprites;
using UnityEngine;
using Object = UnityEngine.Object;

namespace BetterSpriteEditor.Tests
{
    public class ImporterRegressionTests
    {
        private string directory;
        private string path;
        private string assetGuid;
        private Texture2D texture;
        private SerializationMode previousSerialization;

        [SetUp]
        public void SetUp()
        {
            previousSerialization = EditorSettings.serializationMode;
            EditorSettings.serializationMode = SerializationMode.ForceText;
            directory = "Assets/BetterSpriteEditorTests-" + GUID.Generate();
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(directory));
            path = directory + "/sheet.png";
            var pixels = new Texture2D(16, 16);
            try { File.WriteAllBytes(path, pixels.EncodeToPNG()); }
            finally { Object.DestroyImmediate(pixels); }
            AssetDatabase.ImportAsset(path);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Multiple;
            importer.SaveAndReimport();
            texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            assetGuid = AssetDatabase.AssetPathToGUID(path);
            var provider = Provider();
            var rects = new[]
            {
                new SpriteRect { name = "A", spriteID = GUID.Generate(), rect = new Rect(0, 0, 8, 8) },
                new SpriteRect { name = "B", spriteID = GUID.Generate(), rect = new Rect(8, 0, 8, 8) }
            };
            provider.SetSpriteRects(rects);
            provider.GetDataProvider<ISpriteNameFileIdDataProvider>().SetNameFileIdPairs(
                rects.Select(rect => new SpriteNameFileIdPair(rect.name, rect.spriteID)));
            provider.Apply();
            importer.SaveAndReimport();
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(directory);
            EditorSettings.serializationMode = previousSerialization;
            string order = $"UserSettings/BetterSpriteEditor/order-{assetGuid}.json";
            if (File.Exists(order)) File.Delete(order);
        }

        private ISpriteEditorDataProvider Provider()
        {
            var factories = new SpriteDataProviderFactories(); factories.Init();
            var provider = factories.GetSpriteEditorDataProviderFromObject(texture);
            provider.InitSpriteEditorDataProvider();
            return provider;
        }

        private class CapabilityProvider : ISpriteEditorDataProvider
        {
            private readonly ISpriteEditorDataProvider inner;
            public Object targetObject { get; }
            public object CapabilityOverride { get; set; }
            public SpriteImportMode spriteImportMode => inner.spriteImportMode;
            public float pixelsPerUnit => inner.pixelsPerUnit;

            public CapabilityProvider(ISpriteEditorDataProvider inner, Object target)
            {
                this.inner = inner;
                targetObject = target;
            }

            public SpriteRect[] GetSpriteRects() => inner.GetSpriteRects();
            public void SetSpriteRects(SpriteRect[] rects) => throw new NotSupportedException();
            public void Apply() => throw new NotSupportedException();
            public void InitSpriteEditorDataProvider() { }
            public T GetDataProvider<T>() where T : class => CapabilityOverride as T;
            public bool HasDataProvider(Type type) => CapabilityOverride != null && type.IsInstanceOfType(CapabilityOverride);
        }

        [Test]
        public void Capabilities_TextureImporterWithoutNativeProviderAllowsStandardEdits()
        {
            var provider = new CapabilityProvider(Provider(), AssetImporter.GetAtPath(path));
            var document = SpriteDocumentRepository.Load(texture);
            SpriteDocumentRepository.Configure(document, provider);
            Assert.That(document.Capabilities, Is.EqualTo(SpriteEditCapabilities.All));
            Assert.That(document.CanChangeRegions && document.CanEditRect && document.CanEditName, Is.True);
        }

        [Test]
        public void Capabilities_UnknownProviderCannotSaveFrameEdits()
        {
            var provider = new CapabilityProvider(Provider(), texture);
            var document = SpriteDocumentRepository.Load(texture);
            SpriteDocumentRepository.Configure(document, provider);
            Assert.That(document.Capabilities, Is.EqualTo(SpriteEditCapabilities.None));
            Assert.That(document.HasOutlineProvider || document.HasPhysicsProvider, Is.False);
            document.Regions[0].Pivot = Vector2.one;
            Assert.Throws<InvalidOperationException>(() => SpriteDocumentRepository.ValidateSave(document, provider));
        }

#if UNITY_6000_1_OR_NEWER
        private class NativeCapabilities : ISpriteFrameEditCapability
        {
            private EditCapability value;
            public NativeCapabilities(EEditCapability flags) { value = new EditCapability(flags); }
            public EditCapability GetEditCapability() => value;
            public void SetEditCapability(EditCapability next) { value = next; }
        }

        [TestCase(EEditCapability.None, (int)SpriteEditCapabilities.None)]
        [TestCase(EEditCapability.EditSpriteName, (int)SpriteEditCapabilities.EditSpriteName)]
        [TestCase(EEditCapability.EditSpriteRect, (int)SpriteEditCapabilities.EditSpriteRect)]
        [TestCase(EEditCapability.EditPivot, (int)SpriteEditCapabilities.EditPivot)]
        [TestCase(EEditCapability.EditBorder, (int)SpriteEditCapabilities.EditBorder)]
        [TestCase(EEditCapability.CreateAndDeleteSprite, (int)SpriteEditCapabilities.CreateAndDeleteSprite)]
        [TestCase(EEditCapability.All, (int)SpriteEditCapabilities.All)]
        public void Capabilities_NativeFlagsOverrideTextureImporterFallback(EEditCapability native, int expected)
        {
            var provider = new CapabilityProvider(Provider(), AssetImporter.GetAtPath(path))
            {
                CapabilityOverride = new NativeCapabilities(native)
            };
            Assert.That(SpriteProviderCompatibility.GetCapabilities(provider), Is.EqualTo((SpriteEditCapabilities)expected));
        }

        [Test]
        public void Capabilities_SaveRechecksChangedNativePermissions()
        {
            var native = new NativeCapabilities(EEditCapability.All);
            var provider = new CapabilityProvider(Provider(), AssetImporter.GetAtPath(path)) { CapabilityOverride = native };
            var document = SpriteDocumentRepository.Load(texture);
            SpriteDocumentRepository.Configure(document, provider);
            document.Regions[0].Name = "Changed";
            native.SetEditCapability(new EditCapability(EEditCapability.None));
            Assert.Throws<InvalidOperationException>(() => SpriteDocumentRepository.ValidateSave(document, provider));
            Assert.That(document.CanEditName, Is.False);
        }
#endif

        [TestCase("name")]
        [TestCase("rect")]
        [TestCase("remove")]
        public void Apply_SingleModeRejectsUnsupportedEdits(string edit)
        {
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.SaveAndReimport();
            string metadata = File.ReadAllText(path + ".meta");
            var document = SpriteDocumentRepository.Load(texture);
            if (edit == "name") document.Regions[0].Name = "Changed";
            if (edit == "rect") document.Regions[0].Rect = new Rect(1, 1, 8, 8);
            if (edit == "remove") document.Regions.Clear();
            Assert.Throws<InvalidOperationException>(() => SpriteDocumentRepository.Save(document));
            Assert.That(File.ReadAllText(path + ".meta"), Is.EqualTo(metadata));
        }

        [Test]
        public void Apply_SingleModeAllowsPivotAndBorder()
        {
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.SaveAndReimport();
            var document = SpriteDocumentRepository.Load(texture);
            document.Regions[0].Alignment = SpriteAlignment.Custom;
            document.Regions[0].Pivot = new Vector2(0.25f, 0.75f);
            document.Regions[0].Border = Vector4.one;
            SpriteDocumentRepository.Save(document);
            var restored = SpriteDocumentRepository.Load(texture).Regions[0];
            Assert.That(restored.Pivot, Is.EqualTo(new Vector2(0.25f, 0.75f)));
            Assert.That(restored.Border, Is.EqualTo(Vector4.one));
        }

        [Test]
        public void Apply_CustomMetadataIsPreservedOrRejectedBeforeWriting()
        {
            var document = SpriteDocumentRepository.Load(texture);
            const string customData = "opaque importer data: \"keep me\"";
            // Put the data on the last region to exercise preflight before any provider writes.
            document.Regions[document.Regions.Count - 1].CustomData = customData;
#if UNITY_6000_0_OR_NEWER
            SpriteDocumentRepository.Save(document);
            document = SpriteDocumentRepository.Load(texture);
            var region = document.Regions.Find(r => r.CustomData == customData);
            Assert.That(region, Is.Not.Null);
            region.Name = "Renamed with metadata";
            SpriteDocumentRepository.Save(document);
            Assert.That(SpriteDocumentRepository.Load(texture).FindRegion(region.Id).CustomData, Is.EqualTo(customData));
#else
            string metadata = File.ReadAllText(path + ".meta");
            Assert.Throws<InvalidOperationException>(() => SpriteDocumentRepository.Save(document));
            Assert.That(File.ReadAllText(path + ".meta"), Is.EqualTo(metadata));
            Assert.That(SpriteDocumentRepository.Load(texture).Regions.TrueForAll(r => r.CustomData == null), Is.True);
#endif
        }

        [Test]
        public void Apply_RenamePreservesSpriteLocalFileId()
        {
            var sprite = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>().First(s => s.name == "A");
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sprite, out string guid, out long id);
            var document = SpriteDocumentRepository.Load(texture);
            document.Regions.Find(r => r.Name == "A").Name = "Renamed";
            SpriteDocumentRepository.Save(document);
            sprite = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>().First(s => s.name == "Renamed");
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sprite, out string newGuid, out long newId);
            Assert.That(newGuid, Is.EqualTo(guid));
            Assert.That(newId, Is.EqualTo(id));
        }

        [TestCase(0.5f, 0.5f)]
        [TestCase(-0.25f, 1.5f)]
        public void Apply_ResizePreservesPivotAnchorAndSpriteIdentity(float pivotX, float pivotY)
        {
            var document = SpriteDocumentRepository.Load(texture);
            var region = document.Regions.Find(r => r.Name == "A");
            region.Alignment = SpriteAlignment.Custom;
            region.Pivot = new Vector2(pivotX, pivotY);
            SpriteDocumentRepository.Save(document);
            var sprite = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>().First(s => s.name == "A");
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sprite, out string guid, out long id);
            document = SpriteDocumentRepository.Load(texture);
            region = document.Regions.Find(r => r.Name == "A");
            var before = region.Clone();
            var anchor = region.GetPivotTexturePosition();
            region.Rect = new Rect(2, 1, 4, 5);
            region.PreservePivotTexturePosition(before);
            SpriteDocumentRepository.Save(document);
            var restored = SpriteDocumentRepository.Load(texture).FindRegion(before.Id);
            Assert.That(restored, Is.Not.Null);
            Assert.That(Vector2.Distance(restored.GetPivotTexturePosition(), anchor), Is.LessThan(0.0001f));
            sprite = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>().First(s => s.name == "A");
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(sprite, out string newGuid, out long newId);
            Assert.That(newGuid, Is.EqualTo(guid));
            Assert.That(newId, Is.EqualTo(id));
            Assert.That(Vector2.Distance(sprite.rect.position + sprite.pivot, anchor), Is.LessThan(0.0001f));
        }

        [Test]
        public void Apply_ExternalChangeIsRejectedWithoutOverwritingMetadata()
        {
            var document = SpriteDocumentRepository.Load(texture);
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.spritePixelsPerUnit = 75;
            importer.SaveAndReimport();
            string metadata = File.ReadAllText(path + ".meta");
            document.Regions[0].Name = "Stale edit";
            Assert.Throws<InvalidOperationException>(() => SpriteDocumentRepository.Save(document));
            Assert.That(File.ReadAllText(path + ".meta"), Is.EqualTo(metadata));
        }

        [Test]
        public void Snapshot_KeepsLoadRevisionAfterExternalImageChange()
        {
            var document = SpriteDocumentRepository.Load(texture);
            string baseline = document.BaselineRevision;
            var image = new Texture2D(20, 20);
            try { File.WriteAllBytes(path, image.EncodeToPNG()); }
            finally { Object.DestroyImmediate(image); }
            var dto = DocumentSnapshot.Capture(document, new Operations.HistoryStack(document), new SelectionModel());
            Assert.That(dto.BaselineRevision, Is.EqualTo(baseline));
            Assert.That(dto.BaselineRevision, Is.Not.EqualTo(AssetState.Revision(path)));
        }

        [Test]
        public void Apply_SingleModeRejectsAdditionalRegions()
        {
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.SaveAndReimport();
            var document = SpriteDocumentRepository.Load(texture);
            Assert.That(document.CanChangeRegions, Is.False);
            document.Regions.Add(new SpriteRegion { Id = GUID.Generate(), Name = "extra", Rect = new Rect(0, 0, 8, 8) });
            document.MarkDirty();
            Assert.Throws<InvalidOperationException>(() => SpriteDocumentRepository.Save(document));
            Assert.That(document.IsDirty, Is.True);
        }

        [Test]
        public void Apply_PresentationOrderSurvivesReloadWithoutChangingIds()
        {
            var document = SpriteDocumentRepository.Load(texture);
            document.Regions.Reverse();
            var expected = document.Regions.Select(r => r.Id).ToArray();
            SpriteDocumentRepository.Save(document);
            Assert.That(SpriteDocumentRepository.Load(texture).Regions.Select(r => r.Id), Is.EqualTo(expected));
        }

        [Test]
        public void Apply_MovedAssetResolvesByGuid()
        {
            var document = SpriteDocumentRepository.Load(texture);
            string moved = directory + "/moved.png";
            Assert.That(AssetDatabase.MoveAsset(path, moved), Is.Empty);
            document.Regions[0].Name = "Moved";
            SpriteDocumentRepository.Save(document);
            Assert.That(document.AssetPath, Is.EqualTo(moved));
        }

        [Test]
        public void Usage_HiddenAndCyclicManagedReferencesAreScanned()
        {
            var sprites = SpriteUsageFinder.LoadSprites(path, new[] { "A", "B" });
            var root = new GameObject("Sprite reference fixture");
            try
            {
                var component = root.AddComponent<HiddenSpriteReference>();
                component.Visible = (Sprite)sprites.First(pair => pair.Value == "A").Key;
                component.Hidden = (Sprite)sprites.First(pair => pair.Value == "B").Key;
                component.Graph = new HiddenSpriteReference.Node { Sprite = component.Hidden };
                component.Graph.Next = component.Graph;
                var sceneHits = SpriteUsageFinder.FindInOpenScenes(sprites);
                Assert.That(sceneHits.Any(hit => hit.RegionName == "B" && hit.SceneObject == root), Is.True);
                PrefabUtility.SaveAsPrefabAsset(root, directory + "/references.prefab");
                var projectHits = SpriteUsageFinder.FindInProject(path, sprites, null);
                Assert.That(projectHits.Usages.Any(hit => hit.RegionName == "B" && hit.AssetPath.EndsWith("references.prefab")), Is.True);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void Usage_CancelledProjectScanDoesNotClaimCompletion()
        {
            var candidate = new GameObject("Candidate");
            try { PrefabUtility.SaveAsPrefabAsset(candidate, directory + "/candidate.prefab"); }
            finally { Object.DestroyImmediate(candidate); }
            var result = SpriteUsageFinder.FindInProject(path,
                SpriteUsageFinder.LoadSprites(path, new[] { "A" }), (_, _) => false);
            Assert.That(result.Completed, Is.False);
        }

        [Test]
        public void Usage_UnappliedRenameResolvesOriginalSpriteById()
        {
            var document = SpriteDocumentRepository.Load(texture);
            var region = document.Regions[0];
            var found = SpriteUsageFinder.LoadSprites(path, new[] { "Unapplied" }, new[] { region.Id.ToString() });
            Assert.That(found.Count, Is.EqualTo(1));
            Assert.That(found.Values.Single(), Is.EqualTo("Unapplied"));
            Assert.That(((Sprite)found.Keys.Single()).GetSpriteID(), Is.EqualTo(region.Id));
        }
    }
}
