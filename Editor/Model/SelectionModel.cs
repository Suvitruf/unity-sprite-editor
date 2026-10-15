using System;
using System.Collections.Generic;
using UnityEditor;

namespace BetterSpriteEditor.Model
{
    /// <summary>
    /// Multi-selection of regions by GUID. The "active" region (last selected) drives the
    /// inspector and resize handles. Insertion order is preserved so features that care about
    /// the order the user picked regions in (batch renaming) can rely on it.
    /// </summary>
    public class SelectionModel
    {
        private readonly HashSet<GUID> ids = new();
        private readonly List<GUID> order = new();

        public GUID ActiveId { get; private set; }

        public event Action Changed;

        public IReadOnlyCollection<GUID> Ids => ids;

        /// <summary>Selected ids in the order they were added.</summary>
        public IReadOnlyList<GUID> OrderedIds => order;

        public int Count => ids.Count;

        public bool IsSelected(GUID id)
        {
            return ids.Contains(id);
        }

        public void Set(GUID id)
        {
            if (ids.Count == 1 && ids.Contains(id) && ActiveId == id)
                return;
            ids.Clear();
            order.Clear();
            ids.Add(id);
            order.Add(id);
            ActiveId = id;
            Changed?.Invoke();
        }

        public void Add(GUID id)
        {
            bool added = ids.Add(id);
            if (added)
                order.Add(id);
            else if (ActiveId == id)
                return;
            ActiveId = id;
            Changed?.Invoke();
        }

        public void Toggle(GUID id)
        {
            if (ids.Remove(id))
            {
                order.Remove(id);
                if (ActiveId == id)
                    ActiveId = order.Count > 0 ? order[^1] : default;
                Changed?.Invoke();
            }
            else
            {
                Add(id);
            }
        }

        /// <summary>Replaces the selection with the given ids (marquee select).</summary>
        public void SetRange(IEnumerable<GUID> newIds)
        {
            ids.Clear();
            order.Clear();
            foreach (var id in newIds)
            {
                if (ids.Add(id))
                    order.Add(id);
            }
            ActiveId = order.Count > 0 ? order[^1] : default;
            Changed?.Invoke();
        }

        public void AddRange(IEnumerable<GUID> newIds)
        {
            bool changed = false;
            foreach (var id in newIds)
            {
                if (ids.Add(id))
                {
                    order.Add(id);
                    changed = true;
                }
                ActiveId = id;
            }
            if (changed)
                Changed?.Invoke();
        }

        public void Clear()
        {
            if (ids.Count == 0)
                return;
            ids.Clear();
            order.Clear();
            ActiveId = default;
            Changed?.Invoke();
        }

        /// <summary>Drops ids whose regions no longer exist (e.g. after undoing a create).</summary>
        public void PruneMissing(SpriteDocument document)
        {
            int removed = ids.RemoveWhere(id => document.FindRegion(id) == null);
            if (removed == 0)
                return;
            order.RemoveAll(id => !ids.Contains(id));
            if (document.FindRegion(ActiveId) == null)
                ActiveId = order.Count > 0 ? order[^1] : default;
            Changed?.Invoke();
        }
    }
}
