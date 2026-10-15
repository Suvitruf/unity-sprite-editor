namespace BetterSpriteEditor.Operations.Shapes
{
    /// <summary>How the shape is produced.</summary>
    public enum ShapePreset
    {
        /// <summary>Follow the sprite's alpha.</summary>
        Traced,
        /// <summary>A rectangle around the visible pixels.</summary>
        Box,
        /// <summary>The convex hull of the visible pixels.</summary>
        ConvexHull
    }

    /// <summary>Settings shared by the shape UI, the generator and the editing tool.</summary>
    public struct ShapeGenerationSettings
    {
        public ShapePreset Preset;

        /// <summary>0 = coarse, 1 = follows the pixels closely.</summary>
        public float Detail;

        /// <summary>Pixels with alpha at or below this count as transparent.</summary>
        public byte AlphaTolerance;

        /// <summary>Round points to whole pixels, both when generating and when dragging.</summary>
        public bool SnapToPixels;

        /// <summary>
        /// How far outside the visible pixels the shape sits, in pixels. Also limits how far
        /// simplification may cut back in, so the shape always contains every visible pixel.
        /// </summary>
        public int Outset;

        /// <summary>Blobs smaller than this many pixels are ignored as noise.</summary>
        public int MinimumBlobSize;

        /// <summary>
        /// Ceiling on the points a region's shape may use; 0 means unlimited. It can only
        /// coarsen the result — Detail stays the preference, the budget overrides it downward.
        /// </summary>
        public int MaxPoints;

        /// <summary>
        /// Cut enclosed transparent areas out of the shape, as reversed-winding paths.
        /// Without it a ring or a window frame comes out solid.
        /// </summary>
        public bool DetectHoles;

        /// <summary>
        /// Generate one shape from the union of the selected regions and give it to all of
        /// them, aligned by pivot. Keeps a collider still across animation frames.
        /// </summary>
        public bool SharedShape;

        /// <summary>
        /// Honoured by the caller, not the generator: leave regions that already have a shape
        /// untouched, so a sheet-wide Generate cannot discard hand-edited work.
        /// </summary>
        public bool KeepExisting;

        public static ShapeGenerationSettings Default => new()
        {
            Preset = ShapePreset.Traced,
            Detail = 0.5f,
            AlphaTolerance = 0,
            SnapToPixels = true,
            Outset = 1,
            MinimumBlobSize = 4,
            MaxPoints = 0,
            DetectHoles = true,
            SharedShape = false,
            KeepExisting = false
        };
    }
}
