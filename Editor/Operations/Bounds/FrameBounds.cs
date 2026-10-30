using System;
using System.Collections.Generic;
using BetterSpriteEditor.Model;
using UnityEngine;

namespace BetterSpriteEditor.Operations.Bounds
{
    internal enum FrameBoundsMode { TrimTransparency, UniformFrameBounds }

    internal sealed class FrameBoundsSettings
    {
        internal FrameBoundsMode Mode;
        internal int Padding;
        internal int AlphaThreshold;
        internal bool AutomaticSize = true;
        internal Vector2Int Size = new Vector2Int(64, 64);
    }

    internal sealed class FrameBoundsEntry
    {
        internal SpriteRegion Before;
        internal SpriteRegion After;
        internal Rect ProposedRect;
        internal string Issue;
        internal bool Empty;
        internal bool Changed => After != null;
    }

    internal sealed class FrameBoundsPlan
    {
        internal readonly List<FrameBoundsEntry> Entries = new();
        internal string Error;
        internal string Label;
        internal Vector2Int UniformSize;
        internal int ChangedCount => Entries.FindAll(e => e.Changed).Count;
        internal int EmptyCount => Entries.FindAll(e => e.Empty).Count;
        internal bool CanApply => Error == null && Entries.TrueForAll(e => e.Issue == null) && ChangedCount > 0;

        internal IEditorCommand CreateCommand(SpriteDocument document)
        {
            if (!CanApply || !document.CanPreservePivotPosition)
                return null;
            var commands = new List<IEditorCommand>();
            foreach (var entry in Entries)
            {
                var current = document.FindRegion(entry.Before.Id);
                // A preview must never overwrite an intervening edit, including shape edits.
                if (current == null || !FrameBounds.SameRegion(current, entry.Before))
                    return null;
                if (!entry.Changed)
                    continue;
                if (!document.Capabilities.Has(SpriteEditCapabilities.EditBorder)
                    && !entry.Before.Border.Equals(entry.After.Border))
                    return null;
                if (entry.Before.Rect.center != entry.After.Rect.center
                    && ((entry.Before.Outlines.Count > 0 && !document.HasOutlineProvider)
                        || (entry.Before.PhysicsOutlines.Count > 0 && !document.HasPhysicsProvider)))
                    return null;
                commands.Add(new ModifyRegionCommand(document, current.Id, entry.Before, entry.After, Label));
            }
            return commands.Count == 1 ? commands[0] : new CompositeCommand(Label, commands);
        }
    }

    /// <summary>Plans rectangle changes in source pixels; never mutates the document or clips shapes.</summary>
    internal static class FrameBounds
    {
        private const float epsilon = 0.0001f;

        internal static FrameBoundsPlan Build(SpriteDocument document, IReadOnlyList<SpriteRegion> regions,
            TextureAlphaMap alpha, FrameBoundsSettings settings)
        {
            var plan = new FrameBoundsPlan
            {
                Label = settings.Mode == FrameBoundsMode.TrimTransparency ? "Trim transparency" : "Uniform frame bounds"
            };
            if (document == null || !document.CanPreservePivotPosition)
                return Fail(plan, "Bounds editing requires Multiple mode and rectangle/pivot editing support.");
            if (regions == null || regions.Count == 0)
                return Fail(plan, "Select one or more regions.");
            if (settings.Padding < 0 || settings.Padding > Math.Max(document.TextureWidth, document.TextureHeight)
                || settings.AlphaThreshold < 0 || settings.AlphaThreshold > 254)
                return Fail(plan, "Use nonnegative padding within the texture size and an alpha threshold from 0 to 254.");
            if (alpha == null || alpha.ActualWidth != document.TextureWidth || alpha.ActualHeight != document.TextureHeight)
                return Fail(plan, "Texture pixels are unavailable or no longer match this document. Reload the texture.");
            if (alpha.Width != alpha.ActualWidth || alpha.Height != alpha.ActualHeight)
                return Fail(plan, "Full-resolution pixels are required. Increase the texture's Max Size and reload before editing bounds.");

            foreach (var region in regions)
            {
                var entry = new FrameBoundsEntry { Before = region.Clone(), ProposedRect = region.Rect };
                plan.Entries.Add(entry);
                if (!ValidRect(region.Rect, document) || !Finite(region.GetPivotTexturePosition()))
                    entry.Issue = "Requires a valid whole-pixel rectangle and finite pivot.";
            }
            if (plan.Entries.Exists(e => e.Issue != null))
                return plan;

            if (settings.Mode == FrameBoundsMode.UniformFrameBounds)
            {
                var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
                var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
                foreach (var entry in plan.Entries)
                {
                    var anchor = entry.Before.GetPivotTexturePosition();
                    min = Vector2.Min(min, entry.Before.Rect.min - anchor);
                    max = Vector2.Max(max, entry.Before.Rect.max - anchor);
                }
                min -= Vector2.one * settings.Padding;
                max += Vector2.one * settings.Padding;
                var size = max - min;
                if (!Whole(size.x) || !Whole(size.y))
                    return Fail(plan, "Selected pivots fall on different fractional pixel grids. Align their pixel fractions before making uniform bounds.");
                if (size.x > document.TextureWidth || size.y > document.TextureHeight)
                    return Fail(plan, "The common frame size exceeds the texture. Reduce padding or adjust the pivots.");
                var required = new Vector2Int(Mathf.RoundToInt(size.x), Mathf.RoundToInt(size.y));
                var chosen = settings.AutomaticSize ? required : settings.Size;
                if (chosen.x < required.x || chosen.y < required.y)
                    return Fail(plan, $"The size must be at least {required.x} × {required.y} to retain every original frame.");
                if (chosen.x > document.TextureWidth || chosen.y > document.TextureHeight)
                    return Fail(plan, "The requested size exceeds the texture.");
                min -= new Vector2((chosen.x - required.x) / 2, (chosen.y - required.y) / 2);
                plan.UniformSize = chosen;
                foreach (var entry in plan.Entries)
                {
                    var origin = entry.Before.GetPivotTexturePosition() + min;
                    if (!Whole(origin.x) || !Whole(origin.y))
                        entry.Issue = "Pivot fractions cannot share whole-pixel bounds. Align the pivot fractions first.";
                    entry.ProposedRect = new Rect(Mathf.Round(origin.x), Mathf.Round(origin.y), chosen.x, chosen.y);
                }
            }
            else
            {
                foreach (var entry in plan.Entries)
                {
                    var rect = entry.Before.Rect;
                    int left = (int)rect.xMax, bottom = (int)rect.yMax, right = -1, top = -1;
                    for (int y = (int)rect.yMin; y < rect.yMax; y++)
                        for (int x = (int)rect.xMin; x < rect.xMax; x++)
                            if (alpha.IsOpaque(x, y, (byte)settings.AlphaThreshold))
                            {
                                left = Math.Min(left, x); bottom = Math.Min(bottom, y);
                                right = Math.Max(right, x); top = Math.Max(top, y);
                            }
                    if (right < left)
                    {
                        entry.Empty = true;
                        continue;
                    }
                    entry.ProposedRect = Rect.MinMaxRect(
                        Math.Max(rect.xMin, left - settings.Padding), Math.Max(rect.yMin, bottom - settings.Padding),
                        Math.Min(rect.xMax, right + 1 + settings.Padding), Math.Min(rect.yMax, top + 1 + settings.Padding));
                }
            }

            foreach (var entry in plan.Entries)
            {
                if (entry.Issue != null || entry.Empty || entry.ProposedRect.Equals(entry.Before.Rect))
                    continue;
                if (!ValidRect(entry.ProposedRect, document))
                {
                    entry.Issue = "The proposed rectangle extends outside the texture.";
                    continue;
                }
                if (AddsArtwork(entry.Before.Rect, entry.ProposedRect, alpha))
                {
                    entry.Issue = "Expansion would include artwork outside this region (including neighboring sprites).";
                    continue;
                }
                PrepareMetadata(document, entry);
            }
            return plan;
        }

        private static void PrepareMetadata(SpriteDocument document, FrameBoundsEntry entry)
        {
            var before = entry.Before;
            var after = before.Clone();
            after.Rect = entry.ProposedRect;
            after.PreservePivotTexturePosition(before);
            if (!Finite(after.Pivot))
            {
                entry.Issue = "The resized pivot cannot be represented.";
                return;
            }
            var border = before.Border;
            for (int i = 0; i < 4; i++)
                if (!Finite(border[i]) || border[i] < 0)
                {
                    entry.Issue = "The existing 9-slice border is invalid.";
                    return;
                }
            // Zero means no border on that edge. Nonzero border lines stay at their texture positions.
            after.Border = new Vector4(
                border.x == 0 ? 0 : border.x + before.Rect.xMin - after.Rect.xMin,
                border.y == 0 ? 0 : border.y + before.Rect.yMin - after.Rect.yMin,
                border.z == 0 ? 0 : border.z + after.Rect.xMax - before.Rect.xMax,
                border.w == 0 ? 0 : border.w + after.Rect.yMax - before.Rect.yMax);
            var b = after.Border;
            if (b.x < 0 || b.y < 0 || b.z < 0 || b.w < 0
                || b.x + b.z > after.Rect.width || b.y + b.w > after.Rect.height)
            {
                entry.Issue = "The bounds would cut a 9-slice border line. Increase padding or adjust the border first.";
                return;
            }
            if (!b.Equals(before.Border) && !document.Capabilities.Has(SpriteEditCapabilities.EditBorder))
            {
                entry.Issue = "Preserving 9-slice border lines requires border editing support.";
                return;
            }
            var translation = before.Rect.center - after.Rect.center;
            if (!TranslateShapes(after.Outlines, translation, after.Rect.size, document.HasOutlineProvider, out string issue)
                || !TranslateShapes(after.PhysicsOutlines, translation, after.Rect.size, document.HasPhysicsProvider, out issue))
            {
                entry.Issue = issue;
                return;
            }
            entry.After = after;
        }

        private static bool TranslateShapes(List<Vector2[]> paths, Vector2 delta, Vector2 size,
            bool hasProvider, out string issue)
        {
            issue = null;
            if (paths.Count > 0 && !delta.Equals(Vector2.zero) && !hasProvider)
            {
                issue = "Preserving the existing shape requires its data provider.";
                return false;
            }
            foreach (var path in paths)
                for (int i = 0; i < path.Length; i++)
                {
                    var point = path[i] + delta;
                    if (!Finite(point) || Mathf.Abs(point.x) > size.x * 0.5f + epsilon
                        || Mathf.Abs(point.y) > size.y * 0.5f + epsilon)
                    {
                        issue = "The bounds would clip an outline or physics shape. Increase padding or edit the shape first.";
                        return false;
                    }
                    path[i] = point;
                }
            return true;
        }

        private static bool AddsArtwork(Rect oldRect, Rect candidate, TextureAlphaMap alpha)
        {
            // Scan only newly included strips; transparent overlap is harmless.
            bool Any(int x0, int y0, int x1, int y1)
            {
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                        if (alpha.IsOpaque(x, y)) return true;
                return false;
            }
            int x0 = (int)candidate.xMin, x1 = (int)candidate.xMax;
            int y0 = (int)candidate.yMin, y1 = (int)candidate.yMax;
            int bottom = Math.Max(y0, (int)oldRect.yMin), top = Math.Min(y1, (int)oldRect.yMax);
            return Any(x0, y0, x1, Math.Min(y1, (int)oldRect.yMin))
                || Any(x0, Math.Max(y0, (int)oldRect.yMax), x1, y1)
                || Any(x0, bottom, Math.Min(x1, (int)oldRect.xMin), top)
                || Any(Math.Max(x0, (int)oldRect.xMax), bottom, x1, top);
        }

        private static bool ValidRect(Rect rect, SpriteDocument document)
        {
            return Finite(rect.position) && Finite(rect.size)
                && rect.x == Math.Round(rect.x) && rect.y == Math.Round(rect.y)
                && rect.width == Math.Round(rect.width) && rect.height == Math.Round(rect.height)
                && rect.width >= 1 && rect.height >= 1 && rect.xMin >= 0 && rect.yMin >= 0
                && rect.xMax <= document.TextureWidth && rect.yMax <= document.TextureHeight;
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool Finite(Vector2 value) => Finite(value.x) && Finite(value.y);
        private static bool Whole(float value) => Finite(value) && Math.Abs(value - Math.Round(value)) <= epsilon;
        private static FrameBoundsPlan Fail(FrameBoundsPlan plan, string error) { plan.Error = error; return plan; }

        internal static bool SameRegion(SpriteRegion a, SpriteRegion b)
        {
            return a.Id == b.Id && a.Name == b.Name && a.Rect.Equals(b.Rect) && a.Alignment == b.Alignment
                && a.Pivot.Equals(b.Pivot) && a.Border.Equals(b.Border) && a.CustomData == b.CustomData
                && a.OutlineTessellation.Equals(b.OutlineTessellation) && a.PhysicsTessellation.Equals(b.PhysicsTessellation)
                && SamePaths(a.Outlines, b.Outlines) && SamePaths(a.PhysicsOutlines, b.PhysicsOutlines);
        }

        private static bool SamePaths(List<Vector2[]> a, List<Vector2[]> b)
        {
            int count = a?.Count ?? 0;
            if (count != (b?.Count ?? 0)) return false;
            for (int i = 0; i < count; i++)
            {
                int length = a[i]?.Length ?? 0;
                if (length != (b[i]?.Length ?? 0)) return false;
                for (int j = 0; j < length; j++)
                    if (!a[i][j].Equals(b[i][j])) return false;
            }
            return true;
        }
    }
}
