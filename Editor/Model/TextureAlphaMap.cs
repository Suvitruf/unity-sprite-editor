using UnityEngine;

namespace BetterSpriteEditor.Model
{
    /// <summary>
    /// Alpha channel of a texture, one byte per pixel (~16MB for a 4K atlas, cached per
    /// document). Values are kept rather than a bitmask so callers can pick their own
    /// transparency threshold — outline generation needs that, slicing does not.
    ///
    /// Lives in readable-texture space, which can be smaller than the actual (source) space
    /// when import max-size caps the texture; conversions are provided.
    /// </summary>
    public class TextureAlphaMap
    {
        private readonly byte[] alpha;

        /// <summary>Readable-texture dimensions (the space of IsOpaque coordinates).</summary>
        public int Width { get; }
        public int Height { get; }

        /// <summary>Actual (source) dimensions — the space sprite rects live in.</summary>
        public int ActualWidth { get; }
        public int ActualHeight { get; }

        public TextureAlphaMap(Color32[] pixels, int width, int height, int actualWidth, int actualHeight)
        {
            Width = width;
            Height = height;
            ActualWidth = actualWidth;
            ActualHeight = actualHeight;
            alpha = new byte[width * height];
            for (int i = 0; i < pixels.Length && i < alpha.Length; i++)
                alpha[i] = pixels[i].a;
        }

        public byte GetAlpha(int x, int y)
        {
            return alpha[y * Width + x];
        }

        /// <summary>Opaque means alpha strictly above the threshold; 0 keeps "any alpha at all".</summary>
        public bool IsOpaque(int x, int y, byte threshold = 0)
        {
            return alpha[y * Width + x] > threshold;
        }

        /// <summary>Samples one pixel given in actual/source coordinates.</summary>
        public bool IsOpaqueActual(int actualX, int actualY, byte threshold = 0)
        {
            int x = Mathf.Clamp(actualX * Width / ActualWidth, 0, Width - 1);
            int y = Mathf.Clamp(actualY * Height / ActualHeight, 0, Height - 1);
            return IsOpaque(x, y, threshold);
        }

        /// <summary>True when the rect (in actual/source pixels) contains any opaque pixel.</summary>
        public bool AnyOpaqueInActualRect(Rect actualRect)
        {
            int x0 = Mathf.Clamp(Mathf.FloorToInt(actualRect.xMin * Width / (float)ActualWidth), 0, Width - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt(actualRect.xMax * Width / (float)ActualWidth) - 1, 0, Width - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt(actualRect.yMin * Height / (float)ActualHeight), 0, Height - 1);
            int y1 = Mathf.Clamp(Mathf.CeilToInt(actualRect.yMax * Height / (float)ActualHeight) - 1, 0, Height - 1);

            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    if (IsOpaque(x, y))
                        return true;
                }
            }
            return false;
        }

        /// <summary>Converts an inclusive readable-space pixel bounding box to an actual-space rect.</summary>
        public Rect ReadableBoundsToActualRect(int xMin, int yMin, int xMax, int yMax)
        {
            if (Width == ActualWidth && Height == ActualHeight)
                return Rect.MinMaxRect(xMin, yMin, xMax + 1, yMax + 1);

            float scaleX = ActualWidth / (float)Width;
            float scaleY = ActualHeight / (float)Height;
            return Rect.MinMaxRect(
                Mathf.Round(xMin * scaleX), Mathf.Round(yMin * scaleY),
                Mathf.Round((xMax + 1) * scaleX), Mathf.Round((yMax + 1) * scaleY));
        }
    }
}
