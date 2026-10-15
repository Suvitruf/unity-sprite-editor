using System.Collections.Generic;
using BetterSpriteEditor.Input;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace BetterSpriteEditor.UI.Preferences
{
    /// <summary>
    /// Preferences page listing every bindable action, grouped by category, with
    /// click-to-capture rebinding, conflict warnings and reset buttons.
    /// </summary>
    public static class ShortcutPreferences
    {
        private static readonly Dictionary<ShortcutActionId, BindingCaptureField> fields = new();
        private static readonly Dictionary<ShortcutActionId, Label> conflictLabels = new();

        [SettingsProvider]
        public static SettingsProvider Create()
        {
            return new SettingsProvider("Preferences/Better Sprite Editor", SettingsScope.User)
            {
                label = "Better Sprite Editor",
                keywords = new HashSet<string> { "sprite", "shortcut", "hotkey", "binding", "slice" },
                activateHandler = (_, rootElement) => Build(rootElement),
                deactivateHandler = () =>
                {
                    fields.Clear();
                    conflictLabels.Clear();
                }
            };
        }

        private static void Build(VisualElement root)
        {
            fields.Clear();
            conflictLabels.Clear();

            root.style.paddingLeft = 10;
            root.style.paddingRight = 10;
            root.style.paddingTop = 8;

            // Header row keeps Reset All permanently visible, above the (possibly long) list.
            var headerRow = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, marginBottom = 2 }
            };
            headerRow.Add(new Label("Shortcuts")
            {
                style = { unityFontStyleAndWeight = FontStyle.Bold, fontSize = 14, flexGrow = 1 }
            });
            headerRow.Add(new Button(() =>
            {
                if (!EditorUtility.DisplayDialog("Better Sprite Editor",
                        "Reset all shortcuts and gestures to their defaults?", "Reset All", "Cancel"))
                    return;
                ShortcutRegistry.ResetAll();
                RefreshAll();
            })
            {
                text = "Reset All",
                tooltip = "Discard every binding override and restore the defaults."
            });
            root.Add(headerRow);

            root.Add(new Label("Overrides are stored per project in UserSettings/. " +
                               "Click a binding, then press the key or mouse button.")
            {
                style = { whiteSpace = WhiteSpace.Normal, opacity = 0.75f, marginBottom = 8 }
            });

            var scroll = new ScrollView { style = { flexGrow = 1 } };
            root.Add(scroll);

            string currentCategory = null;
            foreach (var action in ShortcutRegistry.Actions)
            {
                if (action.Category != currentCategory)
                {
                    currentCategory = action.Category;
                    scroll.Add(new Label(currentCategory)
                    {
                        style =
                        {
                            unityFontStyleAndWeight = FontStyle.Bold,
                            marginTop = 10,
                            marginBottom = 2
                        }
                    });
                }
                scroll.Add(BuildRow(action));
            }

        }

        private static VisualElement BuildRow(ShortcutActionInfo action)
        {
            var container = new VisualElement { style = { marginBottom = 2 } };

            var row = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            row.Add(new Label(action.DisplayName)
            {
                style = { minWidth = 180, unityTextAlign = TextAnchor.MiddleLeft }
            });

            var captureField = new BindingCaptureField(ShortcutRegistry.Get(action.Id));
            captureField.BindingCaptured += binding =>
            {
                ShortcutRegistry.Set(action.Id, binding);
                RefreshAll();
            };
            fields[action.Id] = captureField;
            row.Add(captureField);

            var resetButton = new Button(() =>
            {
                ShortcutRegistry.ResetToDefault(action.Id);
                RefreshAll();
            })
            {
                text = "Reset",
                tooltip = $"Default: {action.DefaultBinding}",
                style = { width = 55 }
            };
            row.Add(resetButton);
            container.Add(row);

            var conflictLabel = new Label
            {
                style =
                {
                    color = new Color(1f, 0.6f, 0.3f),
                    marginLeft = 182,
                    whiteSpace = WhiteSpace.Normal,
                    display = DisplayStyle.None
                }
            };
            conflictLabels[action.Id] = conflictLabel;
            container.Add(conflictLabel);

            return container;
        }

        private static void RefreshAll()
        {
            foreach (var action in ShortcutRegistry.Actions)
            {
                var binding = ShortcutRegistry.Get(action.Id);
                if (fields.TryGetValue(action.Id, out var field))
                    field.SetBinding(binding);

                if (!conflictLabels.TryGetValue(action.Id, out var label))
                    continue;
                var conflicts = ShortcutRegistry.FindConflicts(action.Id, binding);
                if (conflicts.Count == 0)
                {
                    label.style.display = DisplayStyle.None;
                    continue;
                }
                var names = new List<string>(conflicts.Count);
                foreach (var conflict in conflicts)
                    names.Add(conflict.DisplayName);
                label.text = $"Conflicts with: {string.Join(", ", names)}";
                label.style.display = DisplayStyle.Flex;
            }
        }
    }
}
