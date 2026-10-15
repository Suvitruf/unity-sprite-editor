using BetterSpriteEditor.Operations;
using UnityEngine;
using UnityEngine.UIElements;

namespace BetterSpriteEditor.UI
{
    /// <summary>
    /// Photoshop-style history: every action as a row, current state highlighted, undone
    /// (redoable) entries greyed out below. Clicking a row jumps straight to that state.
    /// </summary>
    public class HistoryPanelElement : VisualElement
    {
        private static readonly Color currentRowColor = new(0.25f, 0.42f, 0.65f, 0.55f);

        private HistoryStack history;
        private ScrollView list;

        public HistoryPanelElement()
        {
            style.borderTopWidth = 1;
            style.borderTopColor = new Color(0.13f, 0.13f, 0.13f);
            style.paddingTop = 4;
            style.flexShrink = 0;

            Add(new Label("History")
            {
                style =
                {
                    unityFontStyleAndWeight = FontStyle.Bold,
                    marginLeft = 6,
                    marginBottom = 2
                }
            });

            list = new ScrollView { style = { maxHeight = 180 } };
            Add(list);
        }

        public void Bind(HistoryStack newHistory)
        {
            if (history != null)
                history.Changed -= Rebuild;
            history = newHistory;
            if (history != null)
                history.Changed += Rebuild;
            Rebuild();
        }

        private void Rebuild()
        {
            list.Clear();
            if (history == null)
                return;

            AddRow("Opened", 0, applied: true);
            for (int i = 0; i < history.UndoEntries.Count; i++)
                AddRow(history.UndoEntries[i].Label, i + 1, applied: true);

            // RedoEntries is stack-ordered: the last element is the next redo.
            int position = history.Position;
            for (int i = history.RedoEntries.Count - 1; i >= 0; i--)
            {
                int targetPosition = position + (history.RedoEntries.Count - i);
                AddRow(history.RedoEntries[i].Label, targetPosition, applied: false);
            }

            // Keep the current row visible.
            if (list.childCount > position)
                list.ScrollTo(list[position]);
        }

        private void AddRow(string text, int targetPosition, bool applied)
        {
            bool isCurrent = history.Position == targetPosition;
            var row = new Label(text)
            {
                style =
                {
                    paddingLeft = 8,
                    paddingTop = 2,
                    paddingBottom = 2,
                    opacity = applied ? 1f : 0.45f,
                    backgroundColor = isCurrent ? currentRowColor : Color.clear
                }
            };
            row.RegisterCallback<ClickEvent>(_ => history.JumpTo(targetPosition));
            list.Add(row);
        }
    }
}
