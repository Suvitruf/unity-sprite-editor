using System;
using System.Collections.Generic;
using BetterSpriteEditor.Model;
using UnityEditor;
using UnityEditor.U2D.Sprites;
using UnityEngine;

namespace BetterSpriteEditor.DataAccess
{
    /// <summary>
    /// Loads and saves <see cref="SpriteDocument"/>s through the public Sprite Editor
    /// Data Provider API. The only place in the package that talks to importers.
    /// </summary>
    public static class SpriteDocumentRepository
    {
        public static SpriteDocument Load(Texture2D texture)
        {
            if (texture == null)
                throw new ArgumentNullException(nameof(texture));

            string assetPath = AssetDatabase.GetAssetPath(texture);
            if (string.IsNullOrEmpty(assetPath))
                throw new InvalidOperationException($"Texture '{texture.name}' is not an asset on disk.");

            var dataProvider = AcquireDataProvider(texture, assetPath);

            var textureDataProvider = dataProvider.GetDataProvider<ITextureDataProvider>();
            int width = texture.width;
            int height = texture.height;
            textureDataProvider?.GetTextureActualWidthAndHeight(out width, out height);

            var document = new SpriteDocument(
                assetPath,
                texture,
                width,
                height,
                dataProvider.pixelsPerUnit,
                dataProvider.spriteImportMode);

            foreach (var spriteRect in dataProvider.GetSpriteRects())
                document.Regions.Add(ToRegion(spriteRect));

            Configure(document, dataProvider);
            document.AssetGuid = AssetDatabase.AssetPathToGUID(assetPath);
            document.BaselineRevision = AssetState.Revision(assetPath);
            LoadShapes(dataProvider, document);
            RegionOrderStore.Load(document);
            return document;
        }

        /// <summary>
        /// Writes the document back to the importer and reimports the asset.
        /// One reimport per call — batch callers save each document once.
        /// </summary>
        public static void Save(SpriteDocument document)
        {
            if (document == null)
                throw new ArgumentNullException(nameof(document));
            if (document.Texture == null)
                throw new InvalidOperationException($"Texture asset for '{document.AssetPath}' no longer exists.");

            EnsureUnchanged(document);
            var dataProvider = AcquireDataProvider(document.Texture, document.AssetPath);
            ValidateSave(document, dataProvider);

            var spriteRects = new SpriteRect[document.Regions.Count];
            for (int i = 0; i < document.Regions.Count; i++)
                spriteRects[i] = ToSpriteRect(document.Regions[i]);
            dataProvider.SetSpriteRects(spriteRects);

            SyncNameFileIdPairs(dataProvider, document);
            // After SetSpriteRects: shape providers are keyed by sprite GUID, so the rects
            // they belong to must already be registered.
            SaveShapes(dataProvider, document);

            dataProvider.Apply();

            if (dataProvider.targetObject is AssetImporter importer)
                importer.SaveAndReimport();
            else
                AssetDatabase.ImportAsset(document.AssetPath, ImportAssetOptions.ForceUpdate);

            document.BaselineRevision = AssetState.Revision(document.AssetPath);
            RegionOrderStore.Save(document);
            document.ClearDirty();
        }

        internal static void Configure(SpriteDocument document, ISpriteEditorDataProvider provider)
        {
            document.Capabilities = SpriteProviderCompatibility.GetCapabilities(provider);
            document.HasOutlineProvider = provider.GetDataProvider<ISpriteOutlineDataProvider>() != null;
            document.HasPhysicsProvider = provider.GetDataProvider<ISpritePhysicsOutlineDataProvider>() != null;
            if (provider.spriteImportMode is not (SpriteImportMode.Single or SpriteImportMode.Multiple))
                throw new InvalidOperationException("Only Single and Multiple sprite import modes are supported.");
        }

        internal static void EnsureUnchanged(SpriteDocument document)
        {
            string currentPath = AssetDatabase.GUIDToAssetPath(document.AssetGuid);
            if (string.IsNullOrEmpty(currentPath))
                throw new InvalidOperationException("The texture was deleted. Unapplied edits have been retained.");
            document.AssetPath = currentPath;
            if (document.BaselineRevision != AssetState.Revision(currentPath))
                throw new InvalidOperationException("The texture or its import settings changed outside this window. " +
                    "Unapplied edits have been retained. Revert to reload the asset before editing it again.");
        }

        internal static void ValidateSave(SpriteDocument document, ISpriteEditorDataProvider provider)
        {
            Configure(document, provider);
            if (provider.spriteImportMode != document.ImportMode)
                throw new InvalidOperationException("The sprite import mode changed. Reload the texture.");
            var originals = new Dictionary<GUID, SpriteRect>();
            foreach (var rect in provider.GetSpriteRects())
                originals.Add(rect.spriteID, rect);
            var ids = new HashSet<GUID>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var region in document.Regions)
            {
                if (region.Id == default || !ids.Add(region.Id) || string.IsNullOrWhiteSpace(region.Name)
                    || !names.Add(region.Name))
                    throw new InvalidOperationException("Sprite IDs and names must be unique and nonempty.");
                if (!originals.TryGetValue(region.Id, out var original))
                {
                    if (!document.CanChangeRegions)
                        throw new InvalidOperationException("This importer does not allow adding sprites. Use Multiple mode.");
                    continue;
                }
                if ((!document.CanEditRect && region.Rect != original.rect)
                    || (!document.CanEditName && region.Name != original.name)
                    || (!document.Capabilities.Has(SpriteEditCapabilities.EditPivot)
                        && (region.Pivot != original.pivot || region.Alignment != original.alignment))
                    || (!document.Capabilities.Has(SpriteEditCapabilities.EditBorder) && region.Border != original.border))
                    throw new InvalidOperationException("The importer does not support one of the requested edits.");
            }
            if (!document.CanChangeRegions && (ids.Count != originals.Count || !ids.SetEquals(originals.Keys)))
                throw new InvalidOperationException("This importer does not allow removing sprites. Use Multiple mode.");
        }

        /// <summary>
        /// Reads the texture's alpha channel for slicing (auto-slice, skip-empty-cells).
        /// One-shot read; callers should cache the map per document.
        /// </summary>
        public static TextureAlphaMap LoadAlphaMap(SpriteDocument document)
        {
            var dataProvider = AcquireDataProvider(document.Texture, document.AssetPath);
            var textureProvider = dataProvider.GetDataProvider<ITextureDataProvider>();
            var readable = textureProvider?.GetReadableTexture2D();
            if (readable == null)
                throw new InvalidOperationException(
                    $"Cannot read pixels of '{document.AssetPath}' (unsupported texture format?).");

            return new TextureAlphaMap(
                readable.GetPixels32(), readable.width, readable.height,
                document.TextureWidth, document.TextureHeight);
        }

        private static ISpriteEditorDataProvider AcquireDataProvider(Texture2D texture, string assetPath)
        {
            var factories = new SpriteDataProviderFactories();
            factories.Init();
            var dataProvider = factories.GetSpriteEditorDataProviderFromObject(texture);
            if (dataProvider == null)
                throw new InvalidOperationException(
                    $"'{assetPath}' has no sprite data provider. Is the texture imported as a Sprite?");

            dataProvider.InitSpriteEditorDataProvider();
            return dataProvider;
        }

        /// <summary>
        /// Keeps name↔fileId pairs consistent with the regions. A region keeps its GUID for life,
        /// so renames update the pair instead of adding one — that is what keeps existing sprite
        /// references valid after reimport.
        /// </summary>
        private static void SyncNameFileIdPairs(ISpriteEditorDataProvider dataProvider, SpriteDocument document)
        {
            var nameFileIdDataProvider = dataProvider.GetDataProvider<ISpriteNameFileIdDataProvider>();
            if (nameFileIdDataProvider == null)
                return;

            var pairs = new List<SpriteNameFileIdPair>(document.Regions.Count);
            foreach (var region in document.Regions)
                pairs.Add(new SpriteNameFileIdPair(region.Name, region.Id));
            nameFileIdDataProvider.SetNameFileIdPairs(pairs);
        }

        /// <summary>
        /// Reads custom outline and physics shape polygons. Points come back relative to the
        /// centre of each region's rect — the model keeps that convention untouched.
        /// </summary>
        private static void LoadShapes(ISpriteEditorDataProvider dataProvider, SpriteDocument document)
        {
            var outlineProvider = dataProvider.GetDataProvider<ISpriteOutlineDataProvider>();
            var physicsProvider = dataProvider.GetDataProvider<ISpritePhysicsOutlineDataProvider>();
            if (outlineProvider == null && physicsProvider == null)
                return;

            foreach (var region in document.Regions)
            {
                if (outlineProvider != null)
                {
                    region.Outlines = outlineProvider.GetOutlines(region.Id) ?? new List<Vector2[]>();
                    region.OutlineTessellation = outlineProvider.GetTessellationDetail(region.Id);
                }
                if (physicsProvider != null)
                {
                    region.PhysicsOutlines = physicsProvider.GetOutlines(region.Id) ?? new List<Vector2[]>();
                    region.PhysicsTessellation = physicsProvider.GetTessellationDetail(region.Id);
                }
            }
        }

        private static void SaveShapes(ISpriteEditorDataProvider dataProvider, SpriteDocument document)
        {
            var outlineProvider = dataProvider.GetDataProvider<ISpriteOutlineDataProvider>();
            var physicsProvider = dataProvider.GetDataProvider<ISpritePhysicsOutlineDataProvider>();
            if (outlineProvider == null && physicsProvider == null)
                return;

            foreach (var region in document.Regions)
            {
                if (outlineProvider != null)
                {
                    outlineProvider.SetOutlines(region.Id, region.Outlines ?? new List<Vector2[]>());
                    outlineProvider.SetTessellationDetail(region.Id, region.OutlineTessellation);
                }
                if (physicsProvider != null)
                {
                    physicsProvider.SetOutlines(region.Id, region.PhysicsOutlines ?? new List<Vector2[]>());
                    physicsProvider.SetTessellationDetail(region.Id, region.PhysicsTessellation);
                }
            }
        }

        private static SpriteRegion ToRegion(SpriteRect spriteRect)
        {
            return new SpriteRegion
            {
                Id = spriteRect.spriteID,
                Name = spriteRect.name,
                Rect = spriteRect.rect,
                Alignment = spriteRect.alignment,
                Pivot = spriteRect.pivot,
                Border = spriteRect.border,
                CustomData = SpriteProviderCompatibility.GetCustomData(spriteRect)
            };
        }

        private static SpriteRect ToSpriteRect(SpriteRegion region)
        {
            var spriteRect = new SpriteRect
            {
                spriteID = region.Id,
                name = region.Name,
                rect = region.Rect,
                alignment = region.Alignment,
                pivot = region.Pivot,
                border = region.Border
            };
            SpriteProviderCompatibility.SetCustomData(spriteRect, region.CustomData);
            return spriteRect;
        }
    }
}
