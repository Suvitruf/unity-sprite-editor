using System;
using BetterSpriteEditor.Input;
using UnityEngine;
using UnityEngine.UIElements;

namespace BetterSpriteEditor.UI.Preferences
{
    /// <summary>
    /// Click to arm, then press the key chord or mouse button to bind it.
    /// A key commits on release, so holding a key and clicking a mouse button instead
    /// captures a gesture like "Space + Left Mouse".
    /// Esc cancels, Delete/Backspace unbinds.
    /// </summary>
    public class BindingCaptureField : VisualElement
    {
        private static readonly Color capturingColor = new(0.25f, 0.42f, 0.65f);

        private readonly Button button;
        private InputBinding binding;
        private bool capturing;
        private KeyCode heldKey = KeyCode.None;

        public event Action<InputBinding> BindingCaptured;

        public BindingCaptureField(InputBinding initialBinding)
        {
            binding = initialBinding;
            style.flexGrow = 1;

            button = new Button(BeginCapture) { style = { flexGrow = 1, marginLeft = 0 } };
            Add(button);

            button.RegisterCallback<KeyDownEvent>(OnKeyDown, TrickleDown.TrickleDown);
            button.RegisterCallback<KeyUpEvent>(OnKeyUp, TrickleDown.TrickleDown);
            button.RegisterCallback<PointerDownEvent>(OnPointerDown, TrickleDown.TrickleDown);
            button.RegisterCallback<FocusOutEvent>(_ => EndCapture());

            Refresh();
        }

        public void SetBinding(InputBinding newBinding)
        {
            binding = newBinding;
            Refresh();
        }

        private void BeginCapture()
        {
            capturing = true;
            heldKey = KeyCode.None;
            button.Focus();
            Refresh();
        }

        private void EndCapture()
        {
            if (!capturing)
                return;
            capturing = false;
            heldKey = KeyCode.None;
            Refresh();
        }

        private void OnKeyDown(KeyDownEvent evt)
        {
            if (!capturing)
                return;
            evt.StopPropagation();

            if (IsModifierKey(evt.keyCode))
                return;

            if (evt.keyCode == KeyCode.Escape)
            {
                EndCapture();
                return;
            }
            if (evt.keyCode is KeyCode.Delete or KeyCode.Backspace)
            {
                Commit(InputBinding.None);
                return;
            }

            // Remembered, not committed: a mouse click before release turns it into a gesture.
            heldKey = evt.keyCode;
            Refresh();
        }

        private void OnKeyUp(KeyUpEvent evt)
        {
            if (!capturing || evt.keyCode != heldKey)
                return;
            evt.StopPropagation();
            Commit(InputBinding.FromKey(heldKey, evt.actionKey, evt.shiftKey, evt.altKey));
        }

        private void OnPointerDown(PointerDownEvent evt)
        {
            if (!capturing)
                return;

            // A plain left click is how the field was armed; without a held key or modifier
            // it would bind instantly and lock the user out of every other choice.
            bool plainLeftClick = evt.button == (int)BindingMouseButton.Left
                                  && heldKey == KeyCode.None
                                  && !evt.ctrlKey && !evt.commandKey && !evt.shiftKey && !evt.altKey;
            if (plainLeftClick)
                return;

            evt.StopPropagation();
            Commit(InputBinding.FromMouse((BindingMouseButton)evt.button, heldKey,
                evt.ctrlKey || evt.commandKey, evt.shiftKey, evt.altKey));
        }

        private void Commit(InputBinding newBinding)
        {
            binding = newBinding;
            capturing = false;
            heldKey = KeyCode.None;
            Refresh();
            BindingCaptured?.Invoke(binding);
        }

        private void Refresh()
        {
            if (capturing)
            {
                button.text = heldKey != KeyCode.None
                    ? $"{heldKey}+…  (release to bind key, or click a mouse button)"
                    : "Press key or mouse button…";
            }
            else
            {
                button.text = binding.ToString();
            }
            button.style.backgroundColor = capturing ? capturingColor : StyleKeyword.Null;
            button.tooltip = capturing
                ? "Release a key to bind it, or hold it and click a mouse button for a gesture. " +
                  "Esc cancels, Delete unbinds."
                : "Click, then press the key or mouse button to bind.";
        }

        private static bool IsModifierKey(KeyCode key)
        {
            return key is KeyCode.LeftControl or KeyCode.RightControl
                or KeyCode.LeftShift or KeyCode.RightShift
                or KeyCode.LeftAlt or KeyCode.RightAlt
                or KeyCode.LeftCommand or KeyCode.RightCommand
                or KeyCode.LeftWindows or KeyCode.RightWindows
                or KeyCode.AltGr or KeyCode.None;
        }
    }
}
