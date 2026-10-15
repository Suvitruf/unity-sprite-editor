using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace BetterSpriteEditor.Input
{
    public enum ShortcutActionId
    {
        // Tools
        SelectTool,
        CreateTool,
        // Editing
        Undo,
        Redo,
        RedoAlternate,
        DeleteSelection,
        CombineSelection,
        RenameSelection,
        FindUsages,
        ApplyChanges,
        ToggleSlicePanel,
        // View
        FitView,
        ZoomOneToOne,
        ToggleAlphaView,
        // Gestures
        PanViewMouse,
        PanViewMouseAlternate,
        PanViewModifierDrag,
        DragRegionOut
    }

    /// <summary>
    /// Single source of truth for every binding: code defaults, overridden by user settings.
    /// All input routing goes through here so nothing is ever hardcoded at the call site.
    /// </summary>
    public static class ShortcutRegistry
    {
        private static readonly List<ShortcutActionInfo> actions = new()
        {
            new(ShortcutActionId.SelectTool, "Tools", "Select Tool", InputBinding.FromKey(KeyCode.M)),
            new(ShortcutActionId.CreateTool, "Tools", "Create Tool", InputBinding.FromKey(KeyCode.U)),

            new(ShortcutActionId.Undo, "Editing", "Undo", InputBinding.FromKey(KeyCode.Z, action: true)),
            new(ShortcutActionId.Redo, "Editing", "Redo", InputBinding.FromKey(KeyCode.Y, action: true)),
            new(ShortcutActionId.RedoAlternate, "Editing", "Redo (alternate)",
                InputBinding.FromKey(KeyCode.Z, action: true, shift: true)),
            new(ShortcutActionId.DeleteSelection, "Editing", "Delete Selection",
                InputBinding.FromKey(KeyCode.Delete)),
            new(ShortcutActionId.CombineSelection, "Editing", "Combine Selection",
                InputBinding.FromKey(KeyCode.E, action: true)),
            // F2 is the near-universal rename shortcut (Windows, Blender, most editors).
            new(ShortcutActionId.RenameSelection, "Editing", "Rename Selection",
                InputBinding.FromKey(KeyCode.F2)),
            new(ShortcutActionId.FindUsages, "Editing", "Find Usages",
                InputBinding.FromKey(KeyCode.F, action: true)),
            new(ShortcutActionId.ApplyChanges, "Editing", "Apply", InputBinding.FromKey(KeyCode.S, action: true)),
            new(ShortcutActionId.ToggleSlicePanel, "Editing", "Toggle Slice Panel",
                InputBinding.FromKey(KeyCode.S, shift: true)),

            new(ShortcutActionId.FitView, "View", "Fit To Window", InputBinding.FromKey(KeyCode.F)),
            new(ShortcutActionId.ZoomOneToOne, "View", "Zoom 1:1", InputBinding.FromKey(KeyCode.Alpha1)),
            new(ShortcutActionId.ToggleAlphaView, "View", "Toggle Alpha View", InputBinding.FromKey(KeyCode.A)),

            new(ShortcutActionId.PanViewMouse, "Gestures", "Pan View",
                InputBinding.FromMouse(BindingMouseButton.Right)),
            new(ShortcutActionId.PanViewMouseAlternate, "Gestures", "Pan View (alternate)",
                InputBinding.FromMouse(BindingMouseButton.Middle)),
            new(ShortcutActionId.PanViewModifierDrag, "Gestures", "Pan View (modifier + drag)",
                InputBinding.FromMouse(BindingMouseButton.Left, heldKey: KeyCode.Space)),
            // Alt is free: Ctrl and Shift already carry selection semantics on click.
            new(ShortcutActionId.DragRegionOut, "Gestures", "Drag Region Out",
                InputBinding.FromMouse(BindingMouseButton.Left, alt: true))
        };

        /// <summary>Raised when any binding changes, so UI can refresh tooltips.</summary>
        public static event Action Changed;

        public static IReadOnlyList<ShortcutActionInfo> Actions => actions;

        public static ShortcutActionInfo GetInfo(ShortcutActionId id)
        {
            return actions.Find(action => action.Id == id);
        }

        public static InputBinding Get(ShortcutActionId id)
        {
            return ShortcutSettings.instance.TryGet(id, out var binding) ? binding : GetInfo(id).DefaultBinding;
        }

        public static bool IsOverridden(ShortcutActionId id)
        {
            return ShortcutSettings.instance.TryGet(id, out _);
        }

        public static void Set(ShortcutActionId id, InputBinding binding)
        {
            if (binding.Equals(GetInfo(id).DefaultBinding))
                ShortcutSettings.instance.ClearOverride(id);
            else
                ShortcutSettings.instance.Set(id, binding);
            Changed?.Invoke();
        }

        public static void ResetToDefault(ShortcutActionId id)
        {
            ShortcutSettings.instance.ClearOverride(id);
            Changed?.Invoke();
        }

        public static void ResetAll()
        {
            ShortcutSettings.instance.ClearAll();
            Changed?.Invoke();
        }

        public static bool Matches(ShortcutActionId id, KeyDownEvent evt)
        {
            return Get(id).Matches(evt);
        }

        public static bool MatchesMouse(ShortcutActionId id, int button, bool action, bool shift, bool alt,
            Func<KeyCode, bool> isKeyHeld)
        {
            return Get(id).Matches(button, action, shift, alt, isKeyHeld);
        }

        /// <summary>
        /// True when a key is used as the "hold" part of a mouse gesture (Space by default).
        /// The viewport swallows those so they can't trigger anything else while held.
        /// </summary>
        public static bool IsGestureHeldKey(KeyCode key)
        {
            if (key == KeyCode.None)
                return false;
            foreach (var action in actions)
            {
                var binding = Get(action.Id);
                if (binding.Kind == BindingKind.Mouse && binding.HeldKey == key)
                    return true;
            }
            return false;
        }

        /// <summary>Display text for tooltips, e.g. "Ctrl+E". Empty when unbound.</summary>
        public static string GetDisplayString(ShortcutActionId id)
        {
            var binding = Get(id);
            return binding.Kind == BindingKind.None ? string.Empty : binding.ToString();
        }

        /// <summary>Other actions bound to the same input — the Preferences UI warns about these.</summary>
        public static List<ShortcutActionInfo> FindConflicts(ShortcutActionId id, InputBinding binding)
        {
            var conflicts = new List<ShortcutActionInfo>();
            if (binding.Kind == BindingKind.None)
                return conflicts;
            foreach (var action in actions)
            {
                if (action.Id != id && Get(action.Id).Equals(binding))
                    conflicts.Add(action);
            }
            return conflicts;
        }
    }
}
