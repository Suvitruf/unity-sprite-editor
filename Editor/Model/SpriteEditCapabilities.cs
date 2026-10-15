using System;

namespace BetterSpriteEditor.Model
{
    [Flags]
    internal enum SpriteEditCapabilities
    {
        None = 0,
        EditSpriteName = 1 << 0,
        EditSpriteRect = 1 << 1,
        EditBorder = 1 << 2,
        EditPivot = 1 << 3,
        CreateAndDeleteSprite = 1 << 4,
        All = EditSpriteName | EditSpriteRect | EditBorder | EditPivot | CreateAndDeleteSprite
    }

    internal static class SpriteEditCapabilitiesExtensions
    {
        internal static bool Has(this SpriteEditCapabilities capabilities, SpriteEditCapabilities required)
        {
            return (capabilities & required) == required;
        }
    }
}
