using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace BetterSpriteEditor.Operations.Clipboard
{
    [Serializable]
    internal sealed class ClipboardPath
    {
        public Vector2[] Points;
    }

    // Deliberately independent of importer identity and the document recovery format.
    [Serializable]
    internal sealed class SpritePropertyPayload
    {
        public int Version = 1;
        public string SourceName;
        public Vector2 Size;
        public float PixelsPerUnit;
        public SpriteAlignment Alignment;
        public Vector2 Pivot;
        public Vector4 Border;
        public bool HasOutline;
        public bool HasPhysics;
        public ClipboardPath[] Outlines;
        public ClipboardPath[] Physics;
        public float OutlineTessellation;
        public float PhysicsTessellation;

        internal static SpritePropertyPayload Capture(Model.SpriteDocument document, Model.SpriteRegion region)
        {
            return new SpritePropertyPayload
            {
                SourceName = region.Name, Size = region.Rect.size, PixelsPerUnit = document.PixelsPerUnit,
                Alignment = region.Alignment, Pivot = region.Pivot, Border = region.Border,
                HasOutline = document.HasOutlineProvider, HasPhysics = document.HasPhysicsProvider,
                Outlines = CapturePaths(region.Outlines), Physics = CapturePaths(region.PhysicsOutlines),
                OutlineTessellation = region.OutlineTessellation, PhysicsTessellation = region.PhysicsTessellation
            };
        }

        internal Vector2 ResolvedPivot => new Model.SpriteRegion { Alignment = Alignment, Pivot = Pivot }.GetNormalizedPivot();

        internal ClipboardPath[] Paths(Model.ShapeKind kind) => kind == Model.ShapeKind.Outline ? Outlines : Physics;
        internal bool Has(Model.ShapeKind kind) => kind == Model.ShapeKind.Outline ? HasOutline : HasPhysics;
        internal float Tessellation(Model.ShapeKind kind) => kind == Model.ShapeKind.Outline ? OutlineTessellation : PhysicsTessellation;

        private static ClipboardPath[] CapturePaths(List<Vector2[]> paths)
        {
            var result = new ClipboardPath[paths?.Count ?? 0];
            for (int i = 0; i < result.Length; i++)
                result[i] = new ClipboardPath { Points = paths[i] == null ? Array.Empty<Vector2>() : (Vector2[])paths[i].Clone() };
            return result;
        }
    }

    internal static class SpritePropertyClipboard
    {
        internal const string SessionKey = "BetterSpriteEditor.PropertyClipboard.v1";
        internal static event Action Changed;
        internal static bool HasData => !string.IsNullOrEmpty(SessionState.GetString(SessionKey, string.Empty));

        internal static void Copy(Model.SpriteDocument document, Model.SpriteRegion region)
        {
            string json = JsonUtility.ToJson(SpritePropertyPayload.Capture(document, region));
            if (json.Length > 8 * 1024 * 1024)
                throw new InvalidOperationException("The copied properties exceed the 8 MB clipboard limit.");
            SessionState.SetString(SessionKey, json);
            Changed?.Invoke();
        }

        // Read a fresh object graph: neither sources nor paste sessions share mutable paths.
        internal static SpritePropertyPayload Read()
        {
            string json = SessionState.GetString(SessionKey, string.Empty);
            if (string.IsNullOrEmpty(json)) return null;
            if (json.Length > 8 * 1024 * 1024) return null;
            try
            {
                var payload = JsonUtility.FromJson<SpritePropertyPayload>(json);
                return payload?.Version == 1 ? payload : null;
            }
            catch (ArgumentException) { return null; }
        }
    }
}
