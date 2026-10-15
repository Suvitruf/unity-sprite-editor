using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace BetterSpriteEditor.Input
{
    /// <summary>
    /// User overrides of the code defaults. Stored per project under UserSettings/ — an
    /// installed package folder is read-only, and bindings are a per-user preference anyway.
    /// </summary>
    [FilePath("UserSettings/BetterSpriteEditorShortcuts.asset", FilePathAttribute.Location.ProjectFolder)]
    public class ShortcutSettings : ScriptableSingleton<ShortcutSettings>
    {
        [SerializeField] private List<BindingOverride> overrides = new();

        public bool TryGet(ShortcutActionId action, out InputBinding binding)
        {
            foreach (var entry in overrides)
            {
                if (entry.Action == action)
                {
                    binding = entry.Value;
                    return true;
                }
            }
            binding = InputBinding.None;
            return false;
        }

        public void Set(ShortcutActionId action, InputBinding binding)
        {
            foreach (var entry in overrides)
            {
                if (entry.Action != action)
                    continue;
                entry.Value = binding;
                Persist();
                return;
            }
            overrides.Add(new BindingOverride { Action = action, Value = binding });
            Persist();
        }

        public void ClearOverride(ShortcutActionId action)
        {
            overrides.RemoveAll(entry => entry.Action == action);
            Persist();
        }

        public void ClearAll()
        {
            overrides.Clear();
            Persist();
        }

        private void Persist()
        {
            Save(true);
        }
    }
}
