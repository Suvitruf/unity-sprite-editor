using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using BetterSpriteEditor.Model;
using UnityEditor;
using UnityEngine;

namespace BetterSpriteEditor.DataAccess
{
    internal static class AssetState
    {
        internal static string Revision(string path)
        {
            return Hash(path) + ":" + Hash(path + ".meta");
        }

        private static string Hash(string path)
        {
            if (!File.Exists(path))
                return "missing";
            using var stream = File.OpenRead(path);
            using var hash = SHA256.Create();
            return Convert.ToBase64String(hash.ComputeHash(stream));
        }

        internal static void WriteAtomic(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, text);
            if (File.Exists(path))
                File.Replace(temporary, path, null);
            else
                File.Move(temporary, path);
        }
    }

    // Presentation order belongs to this editor, not TextureImporter's sprite array.
    internal static class RegionOrderStore
    {
        [Serializable]
        private class Order { public string[] Ids; }

        private static string PathFor(string guid) =>
            $"UserSettings/BetterSpriteEditor/order-{guid}.json";

        internal static void Save(SpriteDocument document)
        {
            var ids = document.Regions.ConvertAll(region => region.Id.ToString());
            AssetState.WriteAtomic(PathFor(document.AssetGuid), JsonUtility.ToJson(new Order { Ids = ids.ToArray() }));
        }

        internal static void Load(SpriteDocument document)
        {
            try
            {
                string path = PathFor(document.AssetGuid);
                if (!File.Exists(path))
                    return;
                var order = JsonUtility.FromJson<Order>(File.ReadAllText(path));
                var remaining = new List<SpriteRegion>(document.Regions);
                var sorted = new List<SpriteRegion>();
                foreach (var id in order?.Ids ?? Array.Empty<string>())
                {
                    var region = remaining.Find(candidate => candidate.Id.ToString() == id);
                    if (region == null)
                        continue;
                    sorted.Add(region);
                    remaining.Remove(region);
                }
                sorted.AddRange(remaining);
                document.Regions.Clear();
                document.Regions.AddRange(sorted);
            }
            catch (Exception exception) when (exception is IOException || exception is ArgumentException)
            {
                Debug.LogWarning($"[BetterSpriteEditor] Could not load presentation order: {exception.Message}");
            }
        }
    }

    internal class SpriteAssetPostprocessor : AssetPostprocessor
    {
        internal static event Action AssetsChanged;
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted,
            string[] moved, string[] movedFrom)
        {
            // Never reload or import recursively inside the import callback.
            EditorApplication.delayCall -= Notify;
            EditorApplication.delayCall += Notify;
        }

        private static void Notify() => AssetsChanged?.Invoke();
    }
}
