using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace BetterSpriteEditor.Model
{
    /// <summary>Which polygon set is being edited — the two are stored separately by the importer.</summary>
    public enum ShapeKind
    {
        Outline,
        Physics
    }


    /// <summary>How pivot values are presented and entered in the UI (storage is always normalized).</summary>
    public enum PivotUnitMode
    {
        Normalized,
        Pixels
    }


    /// <summary>
    /// In-memory representation of one sprite region (a sprite rect) of a texture.
    /// Plain data object — persistence lives in the DataAccess layer.
    /// </summary>
    public class SpriteRegion
    {
        /// <summary>
        /// Stable sprite GUID. Existing sprite references survive edits only while this is preserved,
        /// so it must never change for a region that already exists in the asset.
        /// </summary>
        public GUID Id { get; set; }

        public string Name { get; set; }

        /// <summary>Region rectangle in texture pixels, bottom-left origin.</summary>
        public Rect Rect { get; set; }

        public SpriteAlignment Alignment { get; set; }

        /// <summary>Normalized pivot. Only meaningful when Alignment is SpriteAlignment.Custom.</summary>
        public Vector2 Pivot { get; set; }

        /// <summary>Border for 9-slicing: (left, bottom, right, top) in pixels.</summary>
        public Vector4 Border { get; set; }

        /// <summary>Opaque importer data — preserved verbatim so round-trips stay lossless.</summary>
        public string CustomData { get; set; }

        /// <summary>
        /// Custom render outline: closed polygons whose points are relative to the CENTRE of
        /// this region's rect, in pixels. That is the importer's convention, not texture space.
        /// </summary>
        public List<Vector2[]> Outlines { get; set; } = new();

        public float OutlineTessellation { get; set; }

        /// <summary>Physics shape polygons, same centre-relative convention as Outlines.</summary>
        public List<Vector2[]> PhysicsOutlines { get; set; } = new();

        public float PhysicsTessellation { get; set; }

        public List<Vector2[]> GetShapes(ShapeKind kind)
        {
            return kind == ShapeKind.Physics ? PhysicsOutlines : Outlines;
        }

        public void SetShapes(ShapeKind kind, List<Vector2[]> shapes)
        {
            if (kind == ShapeKind.Physics)
                PhysicsOutlines = shapes;
            else
                Outlines = shapes;
        }

        public float GetTessellation(ShapeKind kind)
        {
            return kind == ShapeKind.Physics ? PhysicsTessellation : OutlineTessellation;
        }

        public void SetTessellation(ShapeKind kind, float value)
        {
            if (kind == ShapeKind.Physics)
                PhysicsTessellation = value;
            else
                OutlineTessellation = value;
        }

        /// <summary>Converts a centre-relative shape point to texture space.</summary>
        public Vector2 ShapePointToTexture(Vector2 point)
        {
            return point + new Vector2(Rect.x + Rect.width * 0.5f, Rect.y + Rect.height * 0.5f);
        }

        public Vector2 TextureToShapePoint(Vector2 texturePoint)
        {
            return texturePoint - new Vector2(Rect.x + Rect.width * 0.5f, Rect.y + Rect.height * 0.5f);
        }

        /// <summary>Normalized pivot resolved from Alignment (Custom uses the Pivot field).</summary>
        public Vector2 GetNormalizedPivot()
        {
            return Alignment switch
            {
                SpriteAlignment.TopLeft => new Vector2(0f, 1f),
                SpriteAlignment.TopCenter => new Vector2(0.5f, 1f),
                SpriteAlignment.TopRight => new Vector2(1f, 1f),
                SpriteAlignment.LeftCenter => new Vector2(0f, 0.5f),
                SpriteAlignment.RightCenter => new Vector2(1f, 0.5f),
                SpriteAlignment.BottomLeft => new Vector2(0f, 0f),
                SpriteAlignment.BottomCenter => new Vector2(0.5f, 0f),
                SpriteAlignment.BottomRight => new Vector2(1f, 0f),
                SpriteAlignment.Custom => Pivot,
                _ => new Vector2(0.5f, 0.5f)
            };
        }

        /// <summary>Pivot position in texture pixels.</summary>
        public Vector2 GetPivotTexturePosition()
        {
            var normalized = GetNormalizedPivot();
            return new Vector2(
                Rect.xMin + normalized.x * Rect.width,
                Rect.yMin + normalized.y * Rect.height);
        }

        /// <summary>Copies all editable fields (not the Id) from another region.</summary>
        public void CopyFrom(SpriteRegion source)
        {
            Name = source.Name;
            Rect = source.Rect;
            Alignment = source.Alignment;
            Pivot = source.Pivot;
            Border = source.Border;
            CustomData = source.CustomData;
            // Deep copy: undo snapshots must not alias the live polygon arrays.
            Outlines = ClonePaths(source.Outlines);
            OutlineTessellation = source.OutlineTessellation;
            PhysicsOutlines = ClonePaths(source.PhysicsOutlines);
            PhysicsTessellation = source.PhysicsTessellation;
        }

        public static List<Vector2[]> ClonePaths(List<Vector2[]> source)
        {
            var result = new List<Vector2[]>(source?.Count ?? 0);
            if (source == null)
                return result;
            foreach (var path in source)
                result.Add(path == null ? System.Array.Empty<Vector2>() : (Vector2[])path.Clone());
            return result;
        }

        public SpriteRegion Clone()
        {
            var clone = new SpriteRegion { Id = Id };
            clone.CopyFrom(this);
            return clone;
        }
    }
}
