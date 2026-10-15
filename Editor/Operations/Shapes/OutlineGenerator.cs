using System.Collections;
using System.Collections.Generic;
using BetterSpriteEditor.Model;
using UnityEngine;

namespace BetterSpriteEditor.Operations.Shapes
{
    /// <summary>
    /// Generates outline polygons from a region's alpha. Unity's own generator
    /// (SpriteUtility.GenerateOutline) is internal, so this is our equivalent.
    ///
    /// The contour follows pixel EDGES, not pixel centres. That is what makes containment
    /// exact: every vertex sits on the lattice between pixels, so "the simplified edge may not
    /// cut inside the contour" literally means "it may not cut into a pixel". Tracing centres
    /// and compensating with an outward extrusion — the earlier approach — could not hold that
    /// promise on staircases and single-pixel spikes, where the compensation under-reaches.
    ///
    /// Output points are relative to the centre of the rect, matching the importer's convention.
    /// </summary>
    public static class OutlineGenerator
    {
        // Simplification may bulge outward freely (that only inflates the shape) but must not
        // cut inward at all; this is float tolerance, not an allowance.
        private const float inwardTolerance = 0.01f;

        public static List<Vector2[]> Generate(
            TextureAlphaMap alphaMap, Rect rect, ShapeGenerationSettings settings)
        {
            return Generate(alphaMap, rect, settings, out _);
        }

        public static List<Vector2[]> Generate(TextureAlphaMap alphaMap, Rect rect,
            ShapeGenerationSettings settings, out bool pointBudgetApplied)
        {
            pointBudgetApplied = false;
            int width = Mathf.RoundToInt(rect.width);
            int height = Mathf.RoundToInt(rect.height);
            if (alphaMap == null || width <= 0 || height <= 0)
                return new List<Vector2[]>();

            var opaque = BuildMask(alphaMap, rect, settings.AlphaTolerance, width, height);
            return GenerateFromMask(opaque, width, height, settings,
                new Vector2(width * 0.5f, height * 0.5f), out pointBudgetApplied);
        }

        /// <summary>Opacity mask of one region, in region-local pixel coordinates.</summary>
        public static BitArray BuildMask(
            TextureAlphaMap alphaMap, Rect rect, byte tolerance, int width, int height)
        {
            int originX = Mathf.RoundToInt(rect.x);
            int originY = Mathf.RoundToInt(rect.y);
            var mask = new BitArray(width * height);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                    mask[y * width + x] = alphaMap.IsOpaqueActual(originX + x, originY + y, tolerance);
            }
            return mask;
        }

        /// <summary>
        /// Generates from an arbitrary mask. Points come back relative to <paramref name="origin"/>,
        /// which is the rect centre for a single region and the shared pivot when several
        /// regions are combined.
        /// </summary>
        public static List<Vector2[]> GenerateFromMask(BitArray opaque, int width, int height,
            ShapeGenerationSettings settings, Vector2 origin)
        {
            return GenerateFromMask(opaque, width, height, settings, origin, out _);
        }

        public static List<Vector2[]> GenerateFromMask(BitArray opaque, int width, int height,
            ShapeGenerationSettings settings, Vector2 origin, out bool pointBudgetApplied)
        {
            pointBudgetApplied = false;
            var result = new List<Vector2[]>();
            if (opaque == null || width <= 0 || height <= 0)
                return result;

            var centre = origin;
            int outset = Mathf.Max(0, settings.Outset);
            int minimumArea = Mathf.Max(1, settings.MinimumBlobSize);

            // The presets need no tracing at all — they are built from the opaque bounds or hull.
            if (settings.Preset != ShapePreset.Traced)
            {
                var preset = GeneratePreset(opaque, width, height, settings, outset, centre);
                if (preset != null)
                    result.Add(preset);
                return result;
            }

            // Growing the mask is what produces the standoff, and because simplification may
            // never cut inward, the outset is an exact minimum rather than a hope.
            var grown = Dilate(opaque, width, height, outset);

            // How far the shape may inflate, scaled to the region so a slider position means the
            // same thing on a 16px sprite and a 256px one. At detail 0 the allowance spans the
            // whole sprite, which collapses the result to the convex hull — the "one big
            // polygon" the stock editor produces.
            //
            // The interpolation is GEOMETRIC, not linear. Point count grows roughly with 1/ε, so
            // a linear ramp spends the bottom half of the slider on values that all produce the
            // hull and then falls off a cliff; multiplying by a constant factor per step instead
            // makes the count climb steadily across the whole slider.
            float detail = Mathf.Clamp01(settings.Detail);
            float coarsest = Mathf.Max(1f, Mathf.Max(width, height));
            const float finest = 0.2f;
            float epsilon = coarsest * Mathf.Pow(finest / coarsest, detail);

            // Prepare the loops once — extraction is the expensive part, and the point budget
            // below may need to re-simplify them several times.
            var prepared = new List<(List<Vector2> loop, bool isHole)>();
            foreach (var loop in ExtractLoops(grown, width, height))
            {
                float area = SignedArea(loop);
                bool isHole = area < 0f;
                if (isHole && !settings.DetectHoles)
                    continue;
                if (Mathf.Abs(area) < minimumArea)
                    continue;

                // Normalise to counter-clockwise so the simplifier's notion of "outward" is
                // uniform; holes are wound back at the end.
                if (isHole)
                    loop.Reverse();
                prepared.Add((loop, isHole));
            }

            // The budget can only coarsen: a larger tolerance inflates the shape, which is
            // always safe here because simplification is one-sided. The search terminates
            // because a large enough tolerance collapses the shape to its convex hull.
            int budget = Mathf.Max(0, settings.MaxPoints);
            const int maxAttempts = 24;
            for (int attempt = 0; ; attempt++)
            {
                result.Clear();
                int totalPoints = 0;
                foreach (var (loop, isHole) in prepared)
                {
                    var path = BuildPath(loop, epsilon, isHole, settings, width, height, centre);
                    if (path == null)
                        continue;
                    result.Add(path);
                    totalPoints += path.Length;
                }

                if (budget <= 0 || totalPoints <= budget || attempt >= maxAttempts
                    || epsilon >= coarsest)
                    break;

                epsilon = Mathf.Min(coarsest, epsilon * 1.5f);
                pointBudgetApplied = true;
            }
            return result;
        }

        /// <summary>
        /// Extracts every closed boundary as a polygon along pixel edges. Each opaque cell
        /// contributes the edges it does not share with another opaque cell, directed so the
        /// interior stays on the left; chaining those gives counter-clockwise outer loops and
        /// clockwise hole loops, so holes fall out of the same pass rather than needing a
        /// separate search.
        /// </summary>
        private static List<List<Vector2>> ExtractLoops(BitArray mask, int width, int height)
        {
            int lattice = width + 1;
            var outgoing = new Dictionary<int, List<int>>();

            void AddEdge(int fromX, int fromY, int toX, int toY)
            {
                int from = fromY * lattice + fromX;
                int to = toY * lattice + toX;
                if (!outgoing.TryGetValue(from, out var list))
                {
                    list = new List<int>(2);
                    outgoing[from] = list;
                }
                list.Add(to);
            }

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (!mask[y * width + x])
                        continue;
                    if (y == 0 || !mask[(y - 1) * width + x])
                        AddEdge(x, y, x + 1, y);
                    if (y == height - 1 || !mask[(y + 1) * width + x])
                        AddEdge(x + 1, y + 1, x, y + 1);
                    if (x == 0 || !mask[y * width + (x - 1)])
                        AddEdge(x, y + 1, x, y);
                    if (x == width - 1 || !mask[y * width + (x + 1)])
                        AddEdge(x + 1, y, x + 1, y + 1);
                }
            }

            var loops = new List<List<Vector2>>();
            var starts = new List<int>(outgoing.Keys);
            foreach (var start in starts)
            {
                while (outgoing.TryGetValue(start, out var startEdges) && startEdges.Count > 0)
                {
                    var loop = new List<Vector2>();
                    int current = start;
                    int previous = -1;

                    while (true)
                    {
                        if (!outgoing.TryGetValue(current, out var edges) || edges.Count == 0)
                            break;

                        // Prefer carrying straight on: at a point where two blobs touch
                        // diagonally, turning would split a loop that should stay whole.
                        int chosen = 0;
                        if (edges.Count > 1 && previous >= 0)
                        {
                            int wantX = current % lattice + (current % lattice - previous % lattice);
                            int wantY = current / lattice + (current / lattice - previous / lattice);
                            int straight = wantY * lattice + wantX;
                            int index = edges.IndexOf(straight);
                            if (index >= 0)
                                chosen = index;
                        }

                        int next = edges[chosen];
                        edges.RemoveAt(chosen);
                        loop.Add(new Vector2(current % lattice, current / lattice));
                        previous = current;
                        current = next;

                        if (current == start)
                            break;
                    }

                    if (loop.Count >= 4)
                        loops.Add(RemoveCollinear(loop));
                }
            }
            return loops;
        }

        /// <summary>Pixel-edge loops carry a vertex per pixel; straight runs collapse to one.</summary>
        private static List<Vector2> RemoveCollinear(List<Vector2> points)
        {
            var result = new List<Vector2>(points.Count);
            int count = points.Count;
            for (int i = 0; i < count; i++)
            {
                var previous = points[(i - 1 + count) % count];
                var current = points[i];
                var next = points[(i + 1) % count];
                if (Mathf.Abs(Cross(current - previous, next - current)) > 1e-6f)
                    result.Add(current);
            }
            return result.Count >= 3 ? result : points;
        }

        /// <summary>
        /// Turns one traced loop into a finished path: simplify, wind, snap, clamp.
        ///
        /// A hole is the same work mirrored. Simplification may only ever move the boundary in
        /// the safe direction — outward for an outer contour (inflating the shape), inward for a
        /// hole (shrinking it) — because both cases must avoid swallowing opaque pixels. Passing
        /// the flipped orientation is what swaps the permitted direction.
        /// </summary>
        private static Vector2[] BuildPath(List<Vector2> loop, float epsilon, bool isHole,
            ShapeGenerationSettings settings, int width, int height, Vector2 centre)
        {
            if (loop.Count < 3)
                return null;

            float sign = isHole ? -1f : 1f;

            // Simplification can, rarely, make a polygon cross itself. Back off until it does
            // not: shapes must stay simple, points are cheaper than broken colliders.
            var simplified = Simplify(loop, epsilon, sign);
            float attemptEpsilon = epsilon;
            for (int attempt = 0; attempt < 3 && HasSelfIntersection(simplified); attempt++)
            {
                attemptEpsilon *= 0.5f;
                simplified = Simplify(loop, attemptEpsilon, sign);
            }
            if (HasSelfIntersection(simplified))
                simplified = new List<Vector2>(loop); // keep the valid contour when simplification fails
            if (simplified.Count < 3)
                return null;

            // Holes wind opposite to outer paths so tessellation reads them as cut-outs.
            EnsureWinding(simplified, wantPositiveArea: !isHole);

            var clamped = new List<Vector2>(simplified.Count);
            for (int i = 0; i < simplified.Count; i++)
            {
                // Vertices already sit on the pixel lattice, so snapping only matters for
                // shapes edited by hand afterwards.
                clamped.Add(ClampToRegion(simplified[i], width, height) - centre);
            }
            return ToPathArray(clamped);
        }

        /// <summary>
        /// Drops points that coincide with their neighbour. Clamping can collapse several
        /// vertices onto a rect edge, and a zero-length segment crashes Unity's stroke
        /// tessellation when it tries to mitre the join.
        /// </summary>
        public static Vector2[] ToPathArray(List<Vector2> points)
        {
            var result = new List<Vector2>(points.Count);
            foreach (var point in points)
            {
                if (result.Count == 0 || (point - result[^1]).sqrMagnitude > 1e-6f)
                    result.Add(point);
            }
            // The loop closes implicitly, so the last point must differ from the first too.
            while (result.Count > 1 && (result[^1] - result[0]).sqrMagnitude <= 1e-6f)
                result.RemoveAt(result.Count - 1);
            if (result.Count < 3 || Mathf.Abs(SignedArea(result)) < 0.0001f)
                return null;
            if (HasSelfIntersection(result))
                throw new System.InvalidOperationException("Generated shape intersects itself. Increase Detail or adjust the selection.");
            return result.ToArray();
        }

        private static void EnsureWinding(List<Vector2> points, bool wantPositiveArea)
        {
            if (SignedArea(points) >= 0f != wantPositiveArea)
                points.Reverse();
        }

        /// <summary>
        /// Grows the mask by a round (Euclidean) radius rather than a square one, so diagonal
        /// edges get the same standoff as orthogonal edges — a square element would push corners
        /// out by radius × √2. Uses a 3-4 chamfer distance transform: two linear passes, close
        /// enough to a true disc at the radii this tool uses.
        /// </summary>
        private static BitArray Dilate(BitArray source, int width, int height, int radius)
        {
            if (radius <= 0)
                return source;

            const int orthogonal = 3;
            const int diagonal = 4;
            int limit = radius * orthogonal;
            int infinity = int.MaxValue / 4;

            var distance = new int[width * height];
            for (int i = 0; i < distance.Length; i++)
                distance[i] = source[i] ? 0 : infinity;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = y * width + x;
                    int best = distance[index];
                    if (y > 0)
                    {
                        best = Mathf.Min(best, distance[index - width] + orthogonal);
                        if (x > 0)
                            best = Mathf.Min(best, distance[index - width - 1] + diagonal);
                        if (x < width - 1)
                            best = Mathf.Min(best, distance[index - width + 1] + diagonal);
                    }
                    if (x > 0)
                        best = Mathf.Min(best, distance[index - 1] + orthogonal);
                    distance[index] = best;
                }
            }

            for (int y = height - 1; y >= 0; y--)
            {
                for (int x = width - 1; x >= 0; x--)
                {
                    int index = y * width + x;
                    int best = distance[index];
                    if (y < height - 1)
                    {
                        best = Mathf.Min(best, distance[index + width] + orthogonal);
                        if (x > 0)
                            best = Mathf.Min(best, distance[index + width - 1] + diagonal);
                        if (x < width - 1)
                            best = Mathf.Min(best, distance[index + width + 1] + diagonal);
                    }
                    if (x < width - 1)
                        best = Mathf.Min(best, distance[index + 1] + orthogonal);
                    distance[index] = best;
                }
            }

            var result = new BitArray(width * height);
            for (int i = 0; i < distance.Length; i++)
                result[i] = distance[i] <= limit;
            return result;
        }

        // --- Presets ---

        private static Vector2[] GeneratePreset(BitArray opaque, int width, int height,
            ShapeGenerationSettings settings, int outset, Vector2 centre)
        {
            if (!TryGetOpaqueBounds(opaque, width, height, out var bounds))
                return null;

            bounds = Rect.MinMaxRect(
                bounds.xMin - outset, bounds.yMin - outset,
                bounds.xMax + outset, bounds.yMax + outset);

            List<Vector2> points;
            if (settings.Preset == ShapePreset.Box)
            {
                points = new List<Vector2>
                {
                    new(bounds.xMin, bounds.yMin),
                    new(bounds.xMax, bounds.yMin),
                    new(bounds.xMax, bounds.yMax),
                    new(bounds.xMin, bounds.yMax)
                };
            }
            else
            {
                points = BuildConvexHull(opaque, width, height, outset);
            }

            if (points == null || points.Count < 3)
                return null;

            // A hull over a large sprite can carry plenty of points, so it honours the budget
            // too. Simplifying a convex polygon outward keeps it convex and still enclosing.
            int budget = Mathf.Max(0, settings.MaxPoints);
            if (budget >= 3 && points.Count > budget)
            {
                float hullEpsilon = 0.5f;
                float limit = Mathf.Max(width, height);
                for (int attempt = 0; attempt < 24 && points.Count > budget && hullEpsilon < limit; attempt++)
                {
                    var simplified = Simplify(points, hullEpsilon, SignedArea(points) >= 0f ? 1f : -1f);
                    if (simplified.Count >= 3)
                        points = simplified;
                    hullEpsilon *= 1.5f;
                }
            }

            var clamped = new List<Vector2>(points.Count);
            foreach (var candidate in points)
            {
                var point = candidate;
                if (settings.SnapToPixels)
                {
                    point.x = Mathf.Round(point.x);
                    point.y = Mathf.Round(point.y);
                }
                clamped.Add(ClampToRegion(point, width, height) - centre);
            }
            return ToPathArray(clamped);
        }

        private static bool TryGetOpaqueBounds(BitArray opaque, int width, int height, out Rect bounds)
        {
            int xMin = int.MaxValue, xMax = int.MinValue, yMin = int.MaxValue, yMax = int.MinValue;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (!opaque[y * width + x])
                        continue;
                    xMin = Mathf.Min(xMin, x);
                    xMax = Mathf.Max(xMax, x);
                    yMin = Mathf.Min(yMin, y);
                    yMax = Mathf.Max(yMax, y);
                }
            }
            if (xMin > xMax)
            {
                bounds = default;
                return false;
            }
            // Pixel indices are corners of unit cells, so the max side needs the extra pixel.
            bounds = Rect.MinMaxRect(xMin, yMin, xMax + 1, yMax + 1);
            return true;
        }

        /// <summary>Andrew's monotone chain over the corners of every opaque pixel.</summary>
        private static List<Vector2> BuildConvexHull(BitArray opaque, int width, int height, float outset)
        {
            var candidates = new List<Vector2>();
            for (int y = 0; y < height; y++)
            {
                // Only the leftmost and rightmost opaque pixel of each row can be on the hull.
                int first = -1;
                int last = -1;
                for (int x = 0; x < width; x++)
                {
                    if (!opaque[y * width + x])
                        continue;
                    if (first < 0)
                        first = x;
                    last = x;
                }
                if (first < 0)
                    continue;
                candidates.Add(new Vector2(first, y));
                candidates.Add(new Vector2(first, y + 1));
                candidates.Add(new Vector2(last + 1, y));
                candidates.Add(new Vector2(last + 1, y + 1));
            }
            if (candidates.Count < 3)
                return null;

            candidates.Sort((a, b) => Mathf.Approximately(a.x, b.x)
                ? a.y.CompareTo(b.y)
                : a.x.CompareTo(b.x));

            var hull = new List<Vector2>();
            for (int pass = 0; pass < 2; pass++)
            {
                int start = hull.Count;
                for (int i = 0; i < candidates.Count; i++)
                {
                    var point = pass == 0 ? candidates[i] : candidates[candidates.Count - 1 - i];
                    while (hull.Count >= start + 2
                           && Cross(hull[^1] - hull[^2], point - hull[^2]) <= 0f)
                        hull.RemoveAt(hull.Count - 1);
                    hull.Add(point);
                }
                hull.RemoveAt(hull.Count - 1); // shared endpoint between the two chains
            }
            if (hull.Count < 3)
                return null;

            return outset > 0f ? Extrude(hull, outset) : hull;
        }

        // --- Simplification ---

        /// <summary>
        /// One-sided Ramer–Douglas–Peucker: a chord may only bulge OUTWARD from the contour.
        ///
        /// Plain RDP treats both directions alike, so a coarse tolerance happily cuts across a
        /// convex bulge and the resulting edge slices through the sprite's pixels. Restricting
        /// removal to the outward direction means simplification can only ever inflate the
        /// shape, so every visible pixel stays inside at any detail level — and at a very coarse
        /// tolerance the result collapses toward the convex hull, which is why the stock editor
        /// can show a dozen points and still contain the sprite.
        /// </summary>
        /// <param name="epsilon">How much the shape may inflate — this is what Detail drives.</param>
        private static List<Vector2> Simplify(List<Vector2> points, float epsilon, float sign)
        {
            if (points.Count < 3)
                return points;

            var keep = new bool[points.Count];
            keep[0] = true;
            keep[points.Count - 1] = true;
            SimplifySegment(points, 0, points.Count - 1, epsilon, sign, keep);

            var result = new List<Vector2>();
            for (int i = 0; i < points.Count; i++)
            {
                if (keep[i])
                    result.Add(points[i]);
            }
            return result;
        }

        private static void SimplifySegment(List<Vector2> points, int first, int last,
            float epsilon, float sign, bool[] keep)
        {
            if (last <= first + 1)
                return;

            var a = points[first];
            var b = points[last];

            float worstOutward = 0f;
            int outwardIndex = -1;
            float worstInward = 0f;
            int inwardIndex = -1;

            for (int i = first + 1; i < last; i++)
            {
                // Positive: the point lies outside the chord, so dropping it would cut into
                // the shape. Negative: the chord already lies outside, so dropping inflates.
                float signed = SignedDistanceToLine(points[i], a, b, sign);
                if (signed > worstOutward)
                {
                    worstOutward = signed;
                    outwardIndex = i;
                }
                else if (-signed > worstInward)
                {
                    worstInward = -signed;
                    inwardIndex = i;
                }
            }

            int splitIndex = -1;
            if (outwardIndex >= 0 && worstOutward > inwardTolerance)
                splitIndex = outwardIndex;
            else if (inwardIndex >= 0 && worstInward > epsilon)
                splitIndex = inwardIndex;
            if (splitIndex < 0)
                return;

            keep[splitIndex] = true;
            SimplifySegment(points, first, splitIndex, epsilon, sign, keep);
            SimplifySegment(points, splitIndex, last, epsilon, sign, keep);
        }

        /// <summary>Distance from the infinite line A→B, positive on the polygon's outside.</summary>
        private static float SignedDistanceToLine(Vector2 point, Vector2 a, Vector2 b, float sign)
        {
            var ab = b - a;
            float length = ab.magnitude;
            if (length < 1e-6f)
                return 0f;
            var outward = new Vector2(ab.y, -ab.x) / length * sign;
            return Vector2.Dot(point - a, outward);
        }

        // --- Geometry helpers ---

        /// <summary>
        /// True when any two non-adjacent edges of the closed polygon cross. O(n²), but n is
        /// small after simplification and a crossing shape is worse than the check.
        /// </summary>
        private static bool HasSelfIntersection(List<Vector2> points)
        {
            int count = points.Count;
            if (count < 4)
                return false;

            for (int i = 0; i < count; i++)
            {
                var a1 = points[i];
                var a2 = points[(i + 1) % count];
                for (int j = i + 1; j < count; j++)
                {
                    // Skip shared endpoints: neighbouring edges always touch.
                    if (j == i || (j + 1) % count == i || j == (i + 1) % count)
                        continue;
                    if (SegmentsIntersect(a1, a2, points[j], points[(j + 1) % count]))
                        return true;
                }
            }
            return false;
        }

        private static bool SegmentsIntersect(Vector2 a1, Vector2 a2, Vector2 b1, Vector2 b2)
        {
            float d1 = Cross(b2 - b1, a1 - b1);
            float d2 = Cross(b2 - b1, a2 - b1);
            float d3 = Cross(a2 - a1, b1 - a1);
            float d4 = Cross(a2 - a1, b2 - a1);
            return ((d1 > 0f && d2 < 0f) || (d1 < 0f && d2 > 0f))
                   && ((d3 > 0f && d4 < 0f) || (d3 < 0f && d4 > 0f));
        }

        private static float Cross(Vector2 a, Vector2 b)
        {
            return a.x * b.y - a.y * b.x;
        }

        /// <summary>
        /// Moves every vertex outward along its mitred normal. Used by the convex hull preset,
        /// where the polygon is convex so offsetting cannot fold it onto itself.
        /// </summary>
        private static List<Vector2> Extrude(List<Vector2> points, float distance)
        {
            int count = points.Count;
            if (count < 3 || Mathf.Approximately(distance, 0f))
                return points;

            float sign = SignedArea(points) >= 0f ? 1f : -1f;
            var result = new List<Vector2>(count);
            for (int i = 0; i < count; i++)
            {
                var previous = points[(i - 1 + count) % count];
                var current = points[i];
                var next = points[(i + 1) % count];

                var incoming = OutwardNormal(previous, current, sign);
                var outgoing = OutwardNormal(current, next, sign);
                var bisector = incoming + outgoing;
                if (bisector.sqrMagnitude < 1e-6f)
                {
                    result.Add(current + outgoing * distance);
                    continue;
                }

                bisector.Normalize();
                float miter = Mathf.Max(0.35f, Vector2.Dot(bisector, outgoing));
                result.Add(current + bisector * (distance / miter));
            }
            return result;
        }

        private static Vector2 OutwardNormal(Vector2 from, Vector2 to, float sign)
        {
            var direction = to - from;
            if (direction.sqrMagnitude < 1e-6f)
                return Vector2.zero;
            return new Vector2(direction.y, -direction.x).normalized * sign;
        }

        private static float SignedArea(List<Vector2> points)
        {
            float area = 0f;
            for (int i = 0; i < points.Count; i++)
            {
                var current = points[i];
                var next = points[(i + 1) % points.Count];
                area += current.x * next.y - next.x * current.y;
            }
            return area * 0.5f;
        }

        /// <summary>
        /// Keeps a point inside the region: the outset pushes outward, so art touching the rect
        /// edge would otherwise produce points beyond the sprite.
        /// </summary>
        private static Vector2 ClampToRegion(Vector2 point, int width, int height)
        {
            return new Vector2(
                Mathf.Clamp(point.x, 0f, width),
                Mathf.Clamp(point.y, 0f, height));
        }
    }
}
