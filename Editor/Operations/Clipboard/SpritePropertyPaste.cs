using System;
using System.Collections.Generic;
using BetterSpriteEditor.Model;
using UnityEditor;
using UnityEngine;

namespace BetterSpriteEditor.Operations.Clipboard
{
    [Flags]
    internal enum SpritePropertyFields
    {
        None = 0, Pivot = 1, Border = 2, Outline = 4, Physics = 8,
        OutlineTessellation = 16, PhysicsTessellation = 32
    }

    internal enum ShapePasteSpace
    {
        [InspectorName("Pixels relative to pivot")] Pivot,
        [InspectorName("Pixels from bottom-left")] RectOrigin,
        [InspectorName("Scale to frame size")] Normalized
    }

    internal enum PivotPasteSpace { Normalized, [InspectorName("Pixels from bottom-left")] Pixels }

    internal sealed class SpritePropertyPasteSettings
    {
        internal SpritePropertyFields Fields = SpritePropertyFields.Pivot;
        internal ShapePasteSpace ShapeSpace;
        internal PivotPasteSpace PivotSpace;
        internal ShapeKind OutlineSource = ShapeKind.Outline;
        internal ShapeKind PhysicsSource = ShapeKind.Physics;
        internal SpritePropertyPasteSettings Clone() => (SpritePropertyPasteSettings)MemberwiseClone();
        internal bool Includes(SpritePropertyFields fields) => (Fields & fields) != 0;
    }

    internal sealed class SpritePropertyPasteEntry
    {
        internal SpriteRegion Before;
        internal SpriteRegion After;
        internal string Issue;
        internal bool Excluded;
        internal bool Changed => !Excluded && Issue == null && After != null && !Before.ContentEquals(After);
    }

    internal sealed class SpritePropertyPastePlan
    {
        internal readonly List<SpritePropertyPasteEntry> Entries = new();
        internal SpritePropertyPasteSettings Settings;
        internal string Error;
        internal int ChangedCount => Entries.FindAll(e => e.Changed).Count;
        internal bool CanApply => Error == null && ChangedCount > 0 && Entries.TrueForAll(e => e.Excluded || e.Issue == null);

        internal IEditorCommand CreateCommand(SpriteDocument document)
        {
            if (!CanApply || SpritePropertyPaste.PermissionIssue(document, Settings) != null) return null;
            var commands = new List<IEditorCommand>();
            foreach (var entry in Entries)
            {
                if (entry.Excluded) continue;
                var current = document.FindRegion(entry.Before.Id);
                if (current == null || !current.ContentEquals(entry.Before)) return null;
                if (entry.Changed)
                    commands.Add(new ModifyRegionCommand(document, current.Id, entry.Before.Clone(), entry.After.Clone(), "Paste properties"));
            }
            return commands.Count == 1 ? commands[0] : new CompositeCommand("Paste properties", commands);
        }
    }

    /// <summary>Plans selective edits without modifying a document or silently clipping geometry.</summary>
    internal static class SpritePropertyPaste
    {
        internal static SpritePropertyPastePlan Build(SpriteDocument document, IReadOnlyList<SpriteRegion> targets,
            SpritePropertyPayload source, SpritePropertyPasteSettings settings, ISet<GUID> excluded = null)
        {
            var plan = new SpritePropertyPastePlan { Settings = settings.Clone() };
            if (source == null || source.Version != 1)
                plan.Error = "Copy properties from one sprite first. The clipboard is empty or incompatible.";
            else if (targets == null || targets.Count == 0)
                plan.Error = "Select one or more target sprites.";
            else if (settings.Fields == SpritePropertyFields.None)
                plan.Error = "Choose properties to paste.";
            else
                plan.Error = PermissionIssue(document, settings) ?? SourceIssue(source, settings);
            if (plan.Error != null) return plan;

            var ids = new HashSet<GUID>();
            foreach (var target in targets)
            {
                if (!ids.Add(target.Id)) continue;
                var entry = new SpritePropertyPasteEntry { Before = target.Clone(), Excluded = excluded?.Contains(target.Id) == true };
                plan.Entries.Add(entry);
                if (entry.Excluded) continue;
                var after = target.Clone();
                entry.After = after;
                if (!PositiveSize(after.Rect.size)) entry.Issue = "The target frame has an invalid size.";
                else if (settings.Includes(SpritePropertyFields.Pivot))
                {
                    after.Alignment = source.Alignment;
                    after.Pivot = source.Pivot;
                    if (settings.PivotSpace == PivotPasteSpace.Pixels)
                    {
                        var pixelPivot = Vector2.Scale(source.ResolvedPivot, source.Size);
                        var normalized = new Vector2(pixelPivot.x / after.Rect.width, pixelPivot.y / after.Rect.height);
                        if (!after.GetNormalizedPivot().Equals(normalized))
                        {
                            after.Alignment = SpriteAlignment.Custom;
                            after.Pivot = normalized;
                        }
                    }
                    if (!Finite(after.GetNormalizedPivot())) entry.Issue = "The resulting pivot cannot be represented.";
                }
                if (entry.Issue == null && settings.Includes(SpritePropertyFields.Border))
                {
                    after.Border = source.Border;
                    var b = after.Border;
                    if (b.x + b.z > after.Rect.width || b.y + b.w > after.Rect.height)
                        entry.Issue = "The copied borders do not fit this frame.";
                }
                if (entry.Issue == null) entry.Issue = ApplyShape(source, after, ShapeKind.Outline, settings.OutlineSource,
                    settings.Includes(SpritePropertyFields.Outline), settings.Includes(SpritePropertyFields.OutlineTessellation), settings.ShapeSpace);
                if (entry.Issue == null) entry.Issue = ApplyShape(source, after, ShapeKind.Physics, settings.PhysicsSource,
                    settings.Includes(SpritePropertyFields.Physics), settings.Includes(SpritePropertyFields.PhysicsTessellation), settings.ShapeSpace);
            }
            return plan;
        }

        internal static string PermissionIssue(SpriteDocument document, SpritePropertyPasteSettings settings)
        {
            if (document == null) return "Open a sprite texture first.";
            if (settings.Includes(SpritePropertyFields.Pivot) && !document.Capabilities.Has(SpriteEditCapabilities.EditPivot))
                return "This importer does not allow pivot editing.";
            if (settings.Includes(SpritePropertyFields.Border) && !document.Capabilities.Has(SpriteEditCapabilities.EditBorder))
                return "This importer does not allow border editing.";
            if (settings.Includes(SpritePropertyFields.Outline | SpritePropertyFields.OutlineTessellation) && !document.HasOutlineProvider)
                return "This importer does not provide editable outlines.";
            if (settings.Includes(SpritePropertyFields.Physics | SpritePropertyFields.PhysicsTessellation) && !document.HasPhysicsProvider)
                return "This importer does not provide editable physics shapes.";
            return null;
        }

        private static string SourceIssue(SpritePropertyPayload source, SpritePropertyPasteSettings settings)
        {
            if (!PositiveSize(source.Size)) return "The copied frame size is invalid.";
            if (!Enum.IsDefined(typeof(ShapePasteSpace), settings.ShapeSpace)
                || !Enum.IsDefined(typeof(PivotPasteSpace), settings.PivotSpace)
                || !Enum.IsDefined(typeof(ShapeKind), settings.OutlineSource)
                || !Enum.IsDefined(typeof(ShapeKind), settings.PhysicsSource)
                || (settings.Fields & ~(SpritePropertyFields)63) != 0) return "Invalid paste options.";
            if ((settings.Includes(SpritePropertyFields.Pivot)
                || (settings.Includes(SpritePropertyFields.Outline | SpritePropertyFields.Physics) && settings.ShapeSpace == ShapePasteSpace.Pivot))
                && (!Enum.IsDefined(typeof(SpriteAlignment), source.Alignment) || !Finite(source.ResolvedPivot)))
                return "The copied pivot is invalid.";
            if (settings.Includes(SpritePropertyFields.Pivot) && !Finite(source.Pivot))
                return "The stored custom pivot is invalid.";
            if (settings.Includes(SpritePropertyFields.Border))
                for (int i = 0; i < 4; i++)
                    if (!Finite(source.Border[i]) || source.Border[i] < 0) return "The copied borders are invalid.";
            if (settings.Includes(SpritePropertyFields.Outline | SpritePropertyFields.OutlineTessellation) && !source.Has(settings.OutlineSource))
                return "The copied sprite did not provide the chosen outline source.";
            if (settings.Includes(SpritePropertyFields.Physics | SpritePropertyFields.PhysicsTessellation) && !source.Has(settings.PhysicsSource))
                return "The copied sprite did not provide the chosen physics source.";
            return null;
        }

        private static string ApplyShape(SpritePropertyPayload source, SpriteRegion target, ShapeKind destination,
            ShapeKind origin, bool pastePaths, bool pasteTessellation, ShapePasteSpace space)
        {
            if (pasteTessellation)
            {
                float value = source.Tessellation(origin);
                // Unity uses -1 for the default tessellation value.
                if (!Finite(value) || value < -1 || value > 1) return "The copied tessellation value is invalid.";
                target.SetTessellation(destination, value);
            }
            if (!pastePaths) return null;
            var paths = new List<Vector2[]>();
            foreach (var path in source.Paths(origin) ?? Array.Empty<ClipboardPath>())
            {
                if (path?.Points == null || path.Points.Length < 3) return "The copied contour has fewer than three vertices.";
                var points = new Vector2[path.Points.Length];
                for (int i = 0; i < points.Length; i++)
                {
                    var point = path.Points[i];
                    switch (space)
                    {
                        case ShapePasteSpace.Pivot:
                            point += source.Size * 0.5f - Vector2.Scale(source.ResolvedPivot, source.Size)
                                + Vector2.Scale(target.GetNormalizedPivot(), target.Rect.size) - target.Rect.size * 0.5f;
                            break;
                        case ShapePasteSpace.RectOrigin:
                            point += (source.Size - target.Rect.size) * 0.5f;
                            break;
                        case ShapePasteSpace.Normalized:
                            point = Vector2.Scale(point, new Vector2(target.Rect.width / source.Size.x, target.Rect.height / source.Size.y));
                            break;
                    }
                    if (!Finite(point) || Mathf.Abs(point.x) > target.Rect.width * 0.5f + 0.0001f
                        || Mathf.Abs(point.y) > target.Rect.height * 0.5f + 0.0001f)
                        return $"The copied {destination.ToString().ToLowerInvariant()} does not fit this frame. Adjust the mapping or exclude this target.";
                    points[i] = point;
                }
                double area = 0;
                for (int i = 0; i < points.Length; i++)
                {
                    var a = points[i]; var b = points[(i + 1) % points.Length];
                    area += (double)a.x * b.y - (double)b.x * a.y;
                }
                if (Math.Abs(area) < 0.000001) return "The copied contour has zero area.";
                paths.Add(points);
            }
            target.SetShapes(destination, paths);
            return null;
        }

        private static bool PositiveSize(Vector2 size) => Finite(size) && size.x > 0 && size.y > 0;
        private static bool Finite(Vector2 value) => Finite(value.x) && Finite(value.y);
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
