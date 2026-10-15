using UnityEngine;

namespace BetterSpriteEditor.Operations.Usages
{
    public sealed class UsageSearchResult
    {
        public readonly System.Collections.Generic.List<SpriteUsage> Usages = new();
        public bool Completed;
        public int SkippedAssets;
        public bool CanReportNoReferences => Completed && SkippedAssets == 0;
    }

    public enum UsageScope
    {
        CurrentScene,
        WholeProject
    }

    /// <summary>
    /// One place a sprite is referenced. Open-scene hits are object-level (we can point at the
    /// exact GameObject); project hits are asset-level unless the asset was cheap to resolve.
    /// </summary>
    public class SpriteUsage
    {
        /// <summary>Region whose sprite is referenced.</summary>
        public string RegionName;

        public string AssetPath;

        /// <summary>Hierarchy path inside the asset/scene; empty when unresolved.</summary>
        public string ObjectPath;

        public string ComponentName;

        /// <summary>Serialized property holding the reference, e.g. "Sprite".</summary>
        public string PropertyName;

        /// <summary>Live object for hits in open scenes — lets us select it directly.</summary>
        public Object SceneObject;

        /// <summary>True when AssetPath is a scene file.</summary>
        public bool IsScene;

        /// <summary>Reference count, for asset-level hits that were not resolved per object.</summary>
        public int Occurrences = 1;

        public string DisplayLocation
        {
            get
            {
                if (!string.IsNullOrEmpty(ObjectPath))
                {
                    string component = string.IsNullOrEmpty(ComponentName) ? string.Empty : $" · {ComponentName}";
                    string property = string.IsNullOrEmpty(PropertyName) ? string.Empty : $".{PropertyName}";
                    return $"{ObjectPath}{component}{property}";
                }
                string suffix = Occurrences > 1 ? $" ({Occurrences} references)" : string.Empty;
                return string.IsNullOrEmpty(PropertyName)
                    ? $"(in file){suffix}"
                    : $"{PropertyName}{suffix}";
            }
        }
    }
}
