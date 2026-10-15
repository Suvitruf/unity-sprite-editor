using UnityEngine;

namespace BetterSpriteEditor.Model
{
    public static class RegionValidator
    {
        /// <summary>
        /// Snaps the region to integer pixels and clamps it into the texture:
        /// size at least 1x1, rect inside bounds, border never exceeding the rect.
        /// </summary>
        public static void Clamp(SpriteRegion region, int textureWidth, int textureHeight)
        {
            var rect = region.Rect;
            float width = Mathf.Clamp(Mathf.Round(rect.width), 1f, textureWidth);
            float height = Mathf.Clamp(Mathf.Round(rect.height), 1f, textureHeight);
            float x = Mathf.Clamp(Mathf.Round(rect.x), 0f, textureWidth - width);
            float y = Mathf.Clamp(Mathf.Round(rect.y), 0f, textureHeight - height);
            region.Rect = new Rect(x, y, width, height);

            // Border components: x=left, y=bottom, z=right, w=top.
            var border = region.Border;
            border.x = Mathf.Clamp(Mathf.Round(border.x), 0f, width);
            border.z = Mathf.Clamp(Mathf.Round(border.z), 0f, width - border.x);
            border.y = Mathf.Clamp(Mathf.Round(border.y), 0f, height);
            border.w = Mathf.Clamp(Mathf.Round(border.w), 0f, height - border.y);
            region.Border = border;
        }

        /// <summary>Unique region name for a new sprite, stock-style: "textureName_N".</summary>
        public static string GenerateUniqueName(SpriteDocument document, string baseName)
        {
            for (int i = document.Regions.Count; ; i++)
            {
                string candidate = $"{baseName}_{i}";
                if (document.Regions.TrueForAll(region => region.Name != candidate))
                    return candidate;
            }
        }

        public static bool IsNameAvailable(SpriteDocument document, SpriteRegion target, string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;
            foreach (var region in document.Regions)
            {
                if (region != target && region.Name == name)
                    return false;
            }
            return true;
        }
    }
}
