using System;
using BetterSpriteEditor.Model;
using UnityEditor;
using UnityEditor.U2D.Sprites;

namespace BetterSpriteEditor.DataAccess
{
    // The built-in Sprite package retains version 1.0.0 across different editor API versions.
    // Keep references to newer public APIs here, outside the document and UI layers.
    internal static class SpriteProviderCompatibility
    {
        internal static SpriteEditCapabilities GetCapabilities(ISpriteEditorDataProvider provider)
        {
#if UNITY_6000_1_OR_NEWER
            var capabilityProvider = provider.GetDataProvider<ISpriteFrameEditCapability>();
            if (capabilityProvider != null)
            {
                var native = capabilityProvider.GetEditCapability();
                var result = SpriteEditCapabilities.None;
                if (native.HasCapability(EEditCapability.EditSpriteName))
                    result |= SpriteEditCapabilities.EditSpriteName;
                if (native.HasCapability(EEditCapability.EditSpriteRect))
                    result |= SpriteEditCapabilities.EditSpriteRect;
                if (native.HasCapability(EEditCapability.EditBorder))
                    result |= SpriteEditCapabilities.EditBorder;
                if (native.HasCapability(EEditCapability.EditPivot))
                    result |= SpriteEditCapabilities.EditPivot;
                if (native.HasCapability(EEditCapability.CreateAndDeleteSprite))
                    result |= SpriteEditCapabilities.CreateAndDeleteSprite;
                return result;
            }
#endif
            // Standard texture importers support these edits; the document additionally enforces
            // Single/Multiple mode. Unknown providers must not inherit those permissions.
            return provider.targetObject is TextureImporter
                ? SpriteEditCapabilities.All
                : SpriteEditCapabilities.None;
        }

        internal static string GetCustomData(SpriteRect spriteRect)
        {
#if UNITY_6000_0_OR_NEWER
            return spriteRect.customData;
#else
            return null;
#endif
        }

        internal static void SetCustomData(SpriteRect spriteRect, string value)
        {
#if UNITY_6000_0_OR_NEWER
            spriteRect.customData = value;
#else
            if (!string.IsNullOrEmpty(value))
                throw new InvalidOperationException("This Unity version cannot save custom sprite metadata. " +
                    "Open these edits in Unity 6 or newer to preserve the metadata.");
#endif
        }
    }
}
