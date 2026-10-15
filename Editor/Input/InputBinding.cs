using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

namespace BetterSpriteEditor.Input
{
    public enum BindingKind
    {
        None,
        Key,
        Mouse
    }

    /// <summary>Values match UnityEngine.UIElements.MouseButton.</summary>
    public enum BindingMouseButton
    {
        Left = 0,
        Right = 1,
        Middle = 2
    }

    /// <summary>
    /// One input binding — either a key chord or a mouse gesture. Both may require an extra
    /// held key (that is what makes "Space + Left Mouse" expressible in the same model).
    /// </summary>
    [Serializable]
    public struct InputBinding : IEquatable<InputBinding>
    {
        public BindingKind Kind;
        public KeyCode Key;
        public BindingMouseButton Button;
        /// <summary>Ctrl on Windows/Linux, Cmd on macOS.</summary>
        public bool Action;
        public bool Shift;
        public bool Alt;
        /// <summary>Extra key that must be held down, e.g. Space. None when unused.</summary>
        public KeyCode HeldKey;

        public static InputBinding None => new() { Kind = BindingKind.None };

        public static InputBinding FromKey(KeyCode key, bool action = false, bool shift = false, bool alt = false)
        {
            return new InputBinding
            {
                Kind = BindingKind.Key, Key = key, Action = action, Shift = shift, Alt = alt,
                HeldKey = KeyCode.None
            };
        }

        public static InputBinding FromMouse(BindingMouseButton button, KeyCode heldKey = KeyCode.None,
            bool action = false, bool shift = false, bool alt = false)
        {
            return new InputBinding
            {
                Kind = BindingKind.Mouse, Button = button, HeldKey = heldKey,
                Action = action, Shift = shift, Alt = alt, Key = KeyCode.None
            };
        }

        public bool Matches(KeyDownEvent evt)
        {
            return Kind == BindingKind.Key
                   && evt.keyCode == Key
                   && evt.actionKey == Action
                   && evt.shiftKey == Shift
                   && evt.altKey == Alt;
        }

        /// <summary>
        /// Mouse gesture match. <paramref name="isKeyHeld"/> answers whether an extra key
        /// (e.g. Space) is currently down — the viewport tracks that.
        /// </summary>
        public bool Matches(int button, bool action, bool shift, bool alt, Func<KeyCode, bool> isKeyHeld)
        {
            if (Kind != BindingKind.Mouse || button != (int)Button)
                return false;
            if (HeldKey != KeyCode.None && (isKeyHeld == null || !isKeyHeld(HeldKey)))
                return false;
            return action == Action && shift == Shift && alt == Alt;
        }

        public override string ToString()
        {
            if (Kind == BindingKind.None)
                return "None";

            var text = new StringBuilder();
            if (HeldKey != KeyCode.None)
                text.Append(HeldKey).Append('+');
            if (Action)
                text.Append(Application.platform == RuntimePlatform.OSXEditor ? "Cmd+" : "Ctrl+");
            if (Shift)
                text.Append("Shift+");
            if (Alt)
                text.Append("Alt+");
            text.Append(Kind == BindingKind.Key ? KeyName(Key) : $"{Button} Mouse");
            return text.ToString();
        }

        private static string KeyName(KeyCode key)
        {
            // Alpha0..9 read badly as "Alpha1".
            if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9)
                return ((int)(key - KeyCode.Alpha0)).ToString();
            return key.ToString();
        }

        public bool Equals(InputBinding other)
        {
            if (Kind != other.Kind)
                return false;
            if (Kind == BindingKind.None)
                return true;
            bool sameInput = Kind == BindingKind.Key ? Key == other.Key : Button == other.Button;
            return sameInput && Action == other.Action && Shift == other.Shift
                   && Alt == other.Alt && HeldKey == other.HeldKey;
        }

        public override bool Equals(object obj)
        {
            return obj is InputBinding other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Kind, Key, Button, Action, Shift, Alt, HeldKey);
        }
    }

    /// <summary>Serializable override entry (only changed bindings are stored).</summary>
    [Serializable]
    public class BindingOverride
    {
        public ShortcutActionId Action;
        public InputBinding Value;
    }

    /// <summary>Static description of one bindable action.</summary>
    public class ShortcutActionInfo
    {
        public ShortcutActionId Id;
        public string Category;
        public string DisplayName;
        public InputBinding DefaultBinding;

        public ShortcutActionInfo(ShortcutActionId id, string category, string displayName, InputBinding defaultBinding)
        {
            Id = id;
            Category = category;
            DisplayName = displayName;
            DefaultBinding = defaultBinding;
        }
    }
}
