using System;
using System.Collections.Generic;
using BetterSpriteEditor.Model;
using BetterSpriteEditor.Operations;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace BetterSpriteEditor.UI
{
    /// <summary>
    /// Searchable list of all regions beside the viewport: click to select (Ctrl/Shift work
    /// as everywhere), double-click to zoom the viewport to the region, drag rows to reorder.
    /// Rows show a thumbnail cropped straight from the texture, so unapplied regions preview
    /// correctly too, and an orange dot marks regions the usage search found unused.
    /// </summary>
    public class RegionListPanel : VisualElement
    {
        private static readonly Color unusedDotColor = new(1f, 0.6f, 0.15f);

        private SpriteDocument document;
        private SelectionModel selection;
        private HistoryStack history;
        private Func<UnityEditor.GUID, bool> isUnused;
        private Action<SpriteRegion> frameRegion;

        private readonly List<SpriteRegion> filtered = new();
        private ToolbarSearchField searchField;
        private ListView listView;
        private bool syncingSelection;
        private string usageScope = "scanned assets";
        internal void SetUsageScope(string scope) => usageScope = scope;

        public RegionListPanel()
        {
            style.minWidth = 140;

            searchField = new ToolbarSearchField { style = { width = Length.Percent(96) } };
            searchField.RegisterValueChangedCallback(_ => RebuildItems());
            Add(searchField);

            listView = new ListView
            {
                fixedItemHeight = 26,
                selectionType = SelectionType.Multiple,
                reorderable = true,
                style = { flexGrow = 1 }
            };
            listView.makeItem = MakeRow;
            listView.bindItem = BindRow;
            listView.selectionChanged += OnListSelectionChanged;
            listView.itemsChosen += OnItemsChosen;
            listView.itemIndexChanged += OnItemReordered;
            Add(listView);
        }

        public void Bind(SpriteDocument newDocument, SelectionModel newSelection,
            HistoryStack newHistory, Func<UnityEditor.GUID, bool> unusedLookup,
            Action<SpriteRegion> frameCallback)
        {
            if (document != null)
                document.Changed -= RebuildItems;
            if (selection != null)
                selection.Changed -= SyncSelectionFromModel;

            document = newDocument;
            selection = newSelection;
            history = newHistory;
            isUnused = unusedLookup;
            frameRegion = frameCallback;

            if (document != null)
                document.Changed += RebuildItems;
            if (selection != null)
                selection.Changed += SyncSelectionFromModel;

            searchField.SetValueWithoutNotify(string.Empty);
            RebuildItems();
        }

        /// <summary>Repaints rows when the unused set changes (usage search ran or cleared).</summary>
        public void RefreshUnusedMarkers()
        {
            listView.RefreshItems();
        }

        private void RebuildItems()
        {
            filtered.Clear();
            if (document != null)
            {
                string query = searchField.value ?? string.Empty;
                foreach (var region in document.Regions)
                {
                    if (query.Length == 0
                        || region.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                        filtered.Add(region);
                }
            }

            // Reordering a filtered subset would be ambiguous, so it needs the full list.
            listView.reorderable = filtered.Count == document?.Regions.Count;
            listView.itemsSource = filtered;
            listView.RefreshItems();
            SyncSelectionFromModel();
        }

        // --- Rows ---

        private VisualElement MakeRow()
        {
            var row = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, paddingLeft = 2 }
            };
            row.Add(new Image
            {
                name = "thumb",
                scaleMode = ScaleMode.ScaleToFit,
                style = { width = 22, height = 22, marginRight = 4, flexShrink = 0 }
            });
            row.Add(new Label
            {
                name = "name",
                style = { flexGrow = 1, overflow = Overflow.Hidden, unityTextAlign = TextAnchor.MiddleLeft }
            });
            row.Add(new VisualElement
            {
                name = "unused",
                tooltip = "No usages found for this sprite",
                style =
                {
                    width = 7, height = 7, borderTopLeftRadius = 4, borderTopRightRadius = 4,
                    borderBottomLeftRadius = 4, borderBottomRightRadius = 4,
                    backgroundColor = unusedDotColor, marginRight = 6, flexShrink = 0,
                    display = DisplayStyle.None
                }
            });
            return row;
        }

        private void BindRow(VisualElement row, int index)
        {
            if (index < 0 || index >= filtered.Count)
                return;
            var region = filtered[index];

            // Cropping via UV shows the region as it is NOW, unapplied edits included —
            // sprite sub-assets would only show the last applied state.
            var thumb = row.Q<Image>("thumb");
            thumb.image = document.Texture;
            thumb.uv = new Rect(
                region.Rect.x / document.TextureWidth,
                region.Rect.y / document.TextureHeight,
                region.Rect.width / document.TextureWidth,
                region.Rect.height / document.TextureHeight);

            row.Q<Label>("name").text = region.Name;
            row.Q<VisualElement>("unused").tooltip = $"No references found in {usageScope}; dynamic loading is not checked.";
            row.Q<VisualElement>("unused").style.display =
                isUnused != null && isUnused(region.Id) ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // --- Selection sync (two-way, guarded against feedback) ---

        private void OnListSelectionChanged(IEnumerable<object> items)
        {
            if (syncingSelection || selection == null)
                return;
            var ids = new List<UnityEditor.GUID>();
            foreach (var item in items)
            {
                if (item is SpriteRegion region)
                    ids.Add(region.Id);
            }
            syncingSelection = true;
            try
            {
                if (ids.Count == 0)
                    selection.Clear();
                else
                    selection.SetRange(ids);
            }
            finally
            {
                syncingSelection = false;
            }
        }

        private void SyncSelectionFromModel()
        {
            if (syncingSelection || selection == null)
                return;
            var indices = new List<int>();
            for (int i = 0; i < filtered.Count; i++)
            {
                if (selection.IsSelected(filtered[i].Id))
                    indices.Add(i);
            }
            syncingSelection = true;
            try
            {
                listView.SetSelectionWithoutNotify(indices);
            }
            finally
            {
                syncingSelection = false;
            }
        }

        private void OnItemsChosen(IEnumerable<object> items)
        {
            foreach (var item in items)
            {
                if (item is SpriteRegion region)
                {
                    frameRegion?.Invoke(region);
                    return;
                }
            }
        }

        // --- Reorder ---

        private void OnItemReordered(int fromIndex, int toIndex)
        {
            if (document == null || history == null)
                return;
            if (fromIndex < 0 || fromIndex >= document.Regions.Count)
                return;

            // The ListView has already moved the row in `filtered`; mirror it in the document
            // FIRST — PushApplied raises document.Changed, which rebuilds this list from the
            // document, so the document must already hold the new order by then.
            var region = filtered[toIndex];
            int documentFrom = document.Regions.IndexOf(region);
            if (documentFrom < 0 || documentFrom == toIndex)
                return;
            document.Regions.Remove(region);
            document.Regions.Insert(Mathf.Clamp(toIndex, 0, document.Regions.Count), region);
            history.PushApplied(new ReorderRegionCommand(document, region.Id, documentFrom, toIndex));
        }
    }
}
