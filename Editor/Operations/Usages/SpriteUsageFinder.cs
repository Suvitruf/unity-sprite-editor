using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.U2D;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace BetterSpriteEditor.Operations.Usages
{
    /// <summary>
    /// Finds where sprites are referenced, in the open scenes or across the project.
    ///
    /// Per-region precision is the whole point and the reason this is not just a dependency
    /// query: Unity serializes references as a GUID plus a local file ID, and
    /// AssetDatabase.GetDependencies only reports the texture. Matching the sprite sub-asset's
    /// local file ID is what tells one region of a sheet from another.
    /// </summary>
    public static class SpriteUsageFinder
    {
        private static readonly string[] scannedExtensions =
        {
            ".prefab", ".unity", ".asset", ".anim", ".controller", ".overrideController", ".spriteatlas"
        };

        /// <summary>Loads the Sprite sub-assets for the given region names, keyed by name.</summary>
        public static Dictionary<Object, string> LoadSprites(string texturePath, IEnumerable<string> regionNames, IReadOnlyList<string> regionIds = null)
        {
            var names = new List<string>(regionNames);
            var wanted = new HashSet<string>(names);
            var namesById = new Dictionary<string, string>();
            if (regionIds != null && regionIds.Count == names.Count)
                for (int i = 0; i < names.Count; i++)
                    namesById[regionIds[i]] = names[i];
            var result = new Dictionary<Object, string>();
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(texturePath))
            {
                if (asset is not Sprite sprite)
                    continue;
                if (regionIds != null)
                {
                    if (namesById.TryGetValue(sprite.GetSpriteID().ToString(), out var name))
                        result[sprite] = name;
                }
                else if (wanted.Contains(sprite.name))
                    result[sprite] = sprite.name;
            }
            return result;
        }

        // --- Open scenes ---

        /// <summary>
        /// Walks every component of every loaded scene. Property-walking rather than checking
        /// known component types, so UI Images, tiles, custom scripts and anything else count.
        /// </summary>
        public static List<SpriteUsage> FindInOpenScenes(Dictionary<Object, string> sprites)
        {
            var usages = new List<SpriteUsage>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded)
                    continue;
                foreach (var root in scene.GetRootGameObjects())
                    ScanGameObject(root, sprites, scene.path, isScene: true, usages);
            }
            return usages;
        }

        private static void ScanGameObject(GameObject root, Dictionary<Object, string> sprites,
            string assetPath, bool isScene, List<SpriteUsage> usages)
        {
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component == null)
                    continue; // missing script

                using var serialized = new SerializedObject(component);
                using var property = serialized.GetIterator();
                var visitedManaged = new HashSet<long>();
                bool enterChildren = true;
                while (property.Next(enterChildren))
                {
                    enterChildren = property.propertyType != SerializedPropertyType.String;
                    if (property.propertyType == SerializedPropertyType.ManagedReference)
                        enterChildren = visitedManaged.Add(property.managedReferenceId);
                    if (property.propertyType != SerializedPropertyType.ObjectReference)
                        continue;
                    var value = property.objectReferenceValue;
                    if (value == null || !sprites.TryGetValue(value, out string regionName))
                        continue;

                    usages.Add(new SpriteUsage
                    {
                        RegionName = regionName,
                        AssetPath = assetPath,
                        ObjectPath = GetHierarchyPath(component.transform),
                        ComponentName = component.GetType().Name,
                        PropertyName = property.displayName,
                        SceneObject = isScene ? component.gameObject : null,
                        IsScene = isScene
                    });
                }
            }
        }

        private static string GetHierarchyPath(Transform transform)
        {
            string path = transform.name;
            var parent = transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }

        // --- Whole project ---

        /// <summary>
        /// Two-phase scan: shortlist assets whose serialized text mentions the texture GUID
        /// (cheap), then match the exact fileID+guid pair to attribute each hit to a region.
        /// Prefabs among the hits are loaded to resolve object-level detail; scenes are left at
        /// file level so a project scan never has to open every scene.
        /// </summary>
        public static UsageSearchResult FindInProject(string texturePath, Dictionary<Object, string> sprites,
            Func<float, string, bool> onProgress)
        {
            var result = new UsageSearchResult();
            var usages = result.Usages;
            string textureGuid = AssetDatabase.AssetPathToGUID(texturePath);
            if (string.IsNullOrEmpty(textureGuid))
                return result;

            // Region name per local file id — this is what makes results per-region.
            var regionByFileId = new Dictionary<long, string>();
            foreach (var pair in sprites)
            {
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(pair.Key, out _, out long localId))
                    regionByFileId[localId] = pair.Value;
            }
            if (regionByFileId.Count == 0)
                return result;

            var referencePattern = new Regex(
                @"fileID:\s*(-?\d+)\s*,\s*guid:\s*" + Regex.Escape(textureGuid),
                RegexOptions.Compiled);

            var candidates = CollectCandidatePaths(texturePath);
            for (int i = 0; i < candidates.Count; i++)
            {
                string path = candidates[i];
                if (onProgress != null && !onProgress((float)i / candidates.Count, path))
                    return result;

                string text;
                try
                {
                    text = File.ReadAllText(path);
                }
                catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
                {
                    result.SkippedAssets++;
                    continue;
                }
                if (!text.StartsWith("%YAML", StringComparison.Ordinal))
                {
                    result.SkippedAssets++;
                    continue;
                }
                if (text.IndexOf(textureGuid, StringComparison.Ordinal) < 0)
                    continue;

                CollectFileMatches(path, text, referencePattern, regionByFileId, sprites, usages);
            }
            result.Completed = true;
            return result;
        }

        private static void CollectFileMatches(string path, string text, Regex referencePattern,
            Dictionary<long, string> regionByFileId, Dictionary<Object, string> sprites, List<SpriteUsage> usages)
        {
            var countsByRegion = new Dictionary<string, int>();
            foreach (Match match in referencePattern.Matches(text))
            {
                if (!long.TryParse(match.Groups[1].Value, out long fileId))
                    continue;
                if (!regionByFileId.TryGetValue(fileId, out string regionName))
                    continue; // another region of the same sheet, or the texture itself
                countsByRegion.TryGetValue(regionName, out int count);
                countsByRegion[regionName] = count + 1;
            }
            if (countsByRegion.Count == 0)
                return;

            bool isScene = path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase);
            bool isPrefab = path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);

            // Prefabs are cheap to load, so give them the same object-level detail as scenes.
            if (isPrefab)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null)
                {
                    int before = usages.Count;
                    ScanGameObject(prefab, sprites, path, isScene: false, usages);
                    // Keep raw hits for sprites whose object detail could not be resolved.
                    for (int i = before; i < usages.Count; i++)
                        countsByRegion.Remove(usages[i].RegionName);
                }
            }

            foreach (var entry in countsByRegion)
            {
                usages.Add(new SpriteUsage
                {
                    RegionName = entry.Key,
                    AssetPath = path,
                    IsScene = isScene,
                    Occurrences = entry.Value
                });
            }
        }

        private static List<string> CollectCandidatePaths(string texturePath)
        {
            var paths = new List<string>();
            foreach (var path in AssetDatabase.GetAllAssetPaths())
            {
                if (path == texturePath || !path.StartsWith("Assets/", StringComparison.Ordinal))
                    continue;
                foreach (var extension in scannedExtensions)
                {
                    if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    {
                        paths.Add(path);
                        break;
                    }
                }
            }
            return paths;
        }

        /// <summary>
        /// Text scanning only works with force-text serialization. Binary projects would need a
        /// load-and-inspect fallback, so the UI warns instead of reporting a silent zero.
        /// </summary>
        public static bool ProjectUsesTextSerialization =>
            EditorSettings.serializationMode == SerializationMode.ForceText;

        // --- Navigation ---

        /// <summary>Selects or opens whatever the usage points at. Returns false if it could not.</summary>
        public static bool Reveal(SpriteUsage usage)
        {
            if (usage.SceneObject != null)
            {
                Selection.activeObject = usage.SceneObject;
                EditorGUIUtility.PingObject(usage.SceneObject);
                return true;
            }

            if (usage.IsScene)
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                    return false;
                var scene = EditorSceneManager.OpenScene(usage.AssetPath, OpenSceneMode.Single);
                return scene.IsValid();
            }

            var asset = AssetDatabase.LoadAssetAtPath<Object>(usage.AssetPath);
            if (asset == null)
                return false;
            AssetDatabase.OpenAsset(asset);
            SelectByPath(usage);
            return true;
        }

        private static void SelectByPath(SpriteUsage usage)
        {
            if (string.IsNullOrEmpty(usage.ObjectPath))
                return;
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null)
                return;

            // The stored path starts at the prefab root, which is the stage's root object.
            var segments = usage.ObjectPath.Split('/');
            var current = stage.prefabContentsRoot.transform;
            for (int i = 1; i < segments.Length && current != null; i++)
                current = current.Find(segments[i]);
            if (current != null)
                Selection.activeObject = current.gameObject;
        }

        public static void Ping(SpriteUsage usage)
        {
            var target = usage.SceneObject != null
                ? usage.SceneObject
                : AssetDatabase.LoadAssetAtPath<Object>(usage.AssetPath);
            if (target != null)
                EditorGUIUtility.PingObject(target);
        }
    }
}
