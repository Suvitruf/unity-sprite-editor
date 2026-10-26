using System.Collections.Generic;
using BetterSpriteEditor.Model;
using UnityEditor;
using UnityEngine;

namespace BetterSpriteEditor.Operations.Pivots
{
    internal static class PivotOffset
    {
        internal static bool IsFinite(Vector2 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x)
                && !float.IsNaN(value.y) && !float.IsInfinity(value.y);
        }

        /// <summary>
        /// Builds one undoable edit without mutating the document. Every selected pivot
        /// moves by the same texture-pixel delta, regardless of rect size or alignment.
        /// </summary>
        internal static IEditorCommand CreateCommand(SpriteDocument document,
            IReadOnlyList<SpriteRegion> regions, Vector2 offsetPixels, out string error)
        {
            error = null;
            if (document == null || !document.Capabilities.Has(SpriteEditCapabilities.EditPivot))
            {
                error = "Pivot editing is not available for this texture.";
                return null;
            }
            if (!IsFinite(offsetPixels))
            {
                error = "Enter a finite pixel offset for X and Y.";
                return null;
            }
            if (regions == null || regions.Count == 0 || offsetPixels.Equals(Vector2.zero))
                return null;

            string label = regions.Count == 1
                ? $"Shift pivot of '{regions[0].Name}'"
                : $"Shift pivots of {regions.Count} regions";
            var commands = new List<IEditorCommand>(regions.Count);
            foreach (var region in regions)
            {
                var size = region.Rect.size;
                if (!IsFinite(size) || size.x <= 0f || size.y <= 0f)
                {
                    error = $"Cannot shift '{region.Name}': its rectangle must have a finite, positive size.";
                    return null;
                }

                // Resolve presets first: their stored custom vector may be stale. Do not
                // round or clamp; fractional and outside-rect pivots are intentional.
                var current = region.GetNormalizedPivot();
                var shifted = current + new Vector2(offsetPixels.x / size.x, offsetPixels.y / size.y);
                if (!IsFinite(shifted))
                {
                    error = $"Cannot shift '{region.Name}': the resulting pivot is not finite.";
                    return null;
                }
                if (shifted.Equals(current))
                    continue;

                var before = region.Clone();
                var after = region.Clone();
                after.Alignment = SpriteAlignment.Custom;
                after.Pivot = shifted;
                commands.Add(new ModifyRegionCommand(document, region.Id, before, after, label));
            }

            return commands.Count == 0 ? null
                : commands.Count == 1 ? commands[0]
                : new CompositeCommand(label, commands);
        }
    }
}
