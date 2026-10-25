using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace BetterSpriteEditor.Model
{
    /// <summary>
    /// In-memory working copy of one texture's sprite data. All edits happen here;
    /// nothing touches the asset until the document is saved (Apply).
    /// </summary>
    public class SpriteDocument
    {
        public string AssetPath { get; internal set; }
        internal string AssetGuid { get; set; }
        internal string BaselineRevision { get; set; }
        internal SpriteEditCapabilities Capabilities { get; set; } = SpriteEditCapabilities.All;
        internal bool HasOutlineProvider { get; set; }
        internal bool HasPhysicsProvider { get; set; }
        internal bool CanChangeRegions => ImportMode == SpriteImportMode.Multiple
            && Capabilities.Has(SpriteEditCapabilities.CreateAndDeleteSprite);
        internal bool CanEditRect => ImportMode == SpriteImportMode.Multiple
            && Capabilities.Has(SpriteEditCapabilities.EditSpriteRect);
        internal bool CanPreservePivotPosition => CanEditRect
            && Capabilities.Has(SpriteEditCapabilities.EditPivot);
        internal bool CanEditName => ImportMode == SpriteImportMode.Multiple
            && Capabilities.Has(SpriteEditCapabilities.EditSpriteName);
        public Texture2D Texture { get; }
        public int TextureWidth { get; }
        public int TextureHeight { get; }
        public float PixelsPerUnit { get; }
        public SpriteImportMode ImportMode { get; }

        public List<SpriteRegion> Regions { get; } = new();

        public bool IsDirty { get; private set; }

        /// <summary>Raised on MarkDirty and ClearDirty — UI listens to refresh itself.</summary>
        public event Action Changed;

        public SpriteDocument(
            string assetPath,
            Texture2D texture,
            int textureWidth,
            int textureHeight,
            float pixelsPerUnit,
            SpriteImportMode importMode)
        {
            AssetPath = assetPath;
            Texture = texture;
            TextureWidth = textureWidth;
            TextureHeight = textureHeight;
            PixelsPerUnit = pixelsPerUnit;
            ImportMode = importMode;
        }

        public SpriteRegion FindRegion(GUID id)
        {
            foreach (var region in Regions)
            {
                if (region.Id == id)
                    return region;
            }
            return null;
        }

        /// <summary>
        /// Smallest region containing the point, so sprites nested inside bigger rects
        /// stay reachable. Returns null when the point is outside every region.
        /// </summary>
        public SpriteRegion FindRegionAt(Vector2 texturePosition)
        {
            SpriteRegion best = null;
            float bestArea = float.MaxValue;
            foreach (var region in Regions)
            {
                if (!region.Rect.Contains(texturePosition))
                    continue;
                float area = region.Rect.width * region.Rect.height;
                if (area < bestArea)
                {
                    bestArea = area;
                    best = region;
                }
            }
            return best;
        }

        public void MarkDirty()
        {
            IsDirty = true;
            Changed?.Invoke();
        }

        public void ClearDirty()
        {
            IsDirty = false;
            Changed?.Invoke();
        }
    }
}
