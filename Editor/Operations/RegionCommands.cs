using System.Collections.Generic;
using System.Linq;
using BetterSpriteEditor.Model;
using UnityEditor;
using UnityEngine;

namespace BetterSpriteEditor.Operations
{
    public class CreateRegionCommand : IEditorCommand
    {
        private readonly SpriteDocument document;
        private readonly SpriteRegion region;

        public string Label => $"Create '{region.Name}'";

        /// <summary>For snapshotting.</summary>
        internal SpriteRegion Region => region;

        public CreateRegionCommand(SpriteDocument document, SpriteRegion region)
        {
            this.document = document;
            this.region = region;
        }

        public void Execute()
        {
            document.Regions.Add(region);
        }

        public void Undo()
        {
            document.Regions.Remove(region);
        }
    }

    public class DeleteRegionsCommand : IEditorCommand
    {
        private readonly SpriteDocument document;
        // Indices are captured so Undo restores the original list order.
        private readonly List<(SpriteRegion region, int index)> entries;

        public string Label { get; }

        /// <summary>For snapshotting.</summary>
        internal IReadOnlyList<(SpriteRegion region, int index)> Entries => entries;

        /// <summary>Recovery restore: entries and label come from a snapshot, verbatim —
        /// the regular constructor would recompute indices from the wrong document state.</summary>
        internal DeleteRegionsCommand(
            SpriteDocument document, List<(SpriteRegion region, int index)> entries, string label)
        {
            this.document = document;
            this.entries = entries;
            Label = label;
        }

        public DeleteRegionsCommand(SpriteDocument document, IEnumerable<SpriteRegion> regions)
        {
            this.document = document;
            entries = regions
                .Select(region => (region, index: document.Regions.IndexOf(region)))
                .Where(entry => entry.index >= 0)
                .OrderBy(entry => entry.index)
                .ToList();
            Label = entries.Count == 1
                ? $"Delete '{entries[0].region.Name}'"
                : $"Delete {entries.Count} regions";
        }

        public void Execute()
        {
            for (int i = entries.Count - 1; i >= 0; i--)
                document.Regions.Remove(entries[i].region);
        }

        public void Undo()
        {
            foreach (var (region, index) in entries)
                document.Regions.Insert(UnityEngine.Mathf.Min(index, document.Regions.Count), region);
        }
    }

    /// <summary>
    /// Moves a region in this editor's presentation order. Apply persists it in UserSettings;
    /// Unity's importer and sprite pickers retain their own order.
    /// </summary>
    public class ReorderRegionCommand : IEditorCommand
    {
        private readonly SpriteDocument document;
        private readonly GUID regionId;
        private readonly int fromIndex;
        private readonly int toIndex;

        public string Label { get; }

        internal GUID RegionId => regionId;
        internal int FromIndex => fromIndex;
        internal int ToIndex => toIndex;

        public ReorderRegionCommand(SpriteDocument document, GUID regionId, int fromIndex, int toIndex)
        {
            this.document = document;
            this.regionId = regionId;
            this.fromIndex = fromIndex;
            this.toIndex = toIndex;
            Label = $"Reorder '{document.FindRegion(regionId)?.Name}'";
        }

        public void Execute()
        {
            Move(fromIndex, toIndex);
        }

        public void Undo()
        {
            Move(toIndex, fromIndex);
        }

        private void Move(int from, int to)
        {
            var region = document.FindRegion(regionId);
            if (region == null)
                return;
            document.Regions.Remove(region);
            document.Regions.Insert(Mathf.Clamp(to, 0, document.Regions.Count), region);
        }
    }

    /// <summary>
    /// Any change to a single region's fields (move, resize, rename, pivot, border, …),
    /// stored as full before/after snapshots — simple and immune to partial-state bugs.
    /// </summary>
    public class ModifyRegionCommand : IEditorCommand
    {
        private readonly SpriteDocument document;
        private readonly GUID regionId;
        private readonly SpriteRegion before;
        private readonly SpriteRegion after;

        public string Label { get; }

        /// <summary>For snapshotting.</summary>
        internal GUID RegionId => regionId;
        internal SpriteRegion Before => before;
        internal SpriteRegion After => after;

        public ModifyRegionCommand(
            SpriteDocument document, GUID regionId, SpriteRegion before, SpriteRegion after, string label)
        {
            this.document = document;
            this.regionId = regionId;
            this.before = before;
            this.after = after;
            Label = label;
        }

        public void Execute()
        {
            document.FindRegion(regionId)?.CopyFrom(after);
        }

        public void Undo()
        {
            document.FindRegion(regionId)?.CopyFrom(before);
        }
    }
}
