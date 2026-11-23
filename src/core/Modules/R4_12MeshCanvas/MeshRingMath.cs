namespace SkinForger.R47.Core;

/// <summary>
/// Canvas expansion for a MeshAttachment without touching the attachment.
///
/// Coordinates:
///  r-space = the attachment's original regionUVs (0..1 over the ORIGINAL image, v from the top); static.
///  canvas  = the expanded PNG (see <see cref="CanvasSpec"/>).
///
/// Static topology (computed once from regionUVs + triangles):
///  "expanded canvas rectangle minus the mesh" is triangulated: the rectangle (with evenly spaced side points)
///  is the outer polygon, every outer boundary loop of the mesh is a hole, and holes inside the mesh are
///  filled from their own boundary vertices. Ear clipping + constrained Delaunay flips. Band triangles
///  reference native boundary vertices directly, so every seam is exact.
///
/// Per frame (from Spine's evaluated world vertices, i.e. after bones, weights and Deform):
///  outer points = global least-squares affine map (r -> world) + inverse-distance blend of the nearest
///  boundary vertices' residuals.
/// </summary>
public readonly record struct V2(double X, double Y)
{
    public static V2 operator +(V2 a, V2 b) => new(a.X + b.X, a.Y + b.Y);
    public static V2 operator -(V2 a, V2 b) => new(a.X - b.X, a.Y - b.Y);
    public static V2 operator *(V2 a, double s) => new(a.X * s, a.Y * s);
    public static double Cross(V2 a, V2 b) => a.X * b.Y - a.Y * b.X;
    public double Length => Math.Sqrt(X * X + Y * Y);
}

/// <summary>
/// Expanded canvas: the ORIGINAL image (OriginalWidth x OriginalHeight, regionUVs 0..1, v from the top)
/// sits with its top-left corner at pixel (Left, Top) of the expanded PNG (ExpandedWidth x ExpandedHeight).
/// This is SkinForger's existing JSON contract: Left = anchorX - defaultAnchorX - offsetX (same for Top).
/// </summary>
public sealed record CanvasSpec(int OriginalWidth, int OriginalHeight, int ExpandedWidth, int ExpandedHeight, double Left, double Top)
{
    public CanvasSpec(int originalWidth, int originalHeight, int pad)
        : this(originalWidth, originalHeight, originalWidth + 2 * pad, originalHeight + 2 * pad, pad, pad) { }

    public double Right => ExpandedWidth - Left - OriginalWidth;
    public double Bottom => ExpandedHeight - Top - OriginalHeight;

    /// <summary>
    /// The original image must lie inside the canvas. A canvas of the original size is valid ("full canvas":
    /// the whole PNG rectangle is drawn, not only the mesh hull).
    /// </summary>
    public string? Problem()
    {
        const double tol = 0.51;
        if (OriginalWidth <= 0 || OriginalHeight <= 0 || ExpandedWidth <= 0 || ExpandedHeight <= 0) return "non-positive-size";
        if (Left < -tol || Top < -tol || Right < -tol || Bottom < -tol)
            return $"original-not-inside-canvas left={Left} top={Top} right={Right} bottom={Bottom}";
        return null;
    }

    public bool IsExpanded => ExpandedWidth > OriginalWidth || ExpandedHeight > OriginalHeight;

    /// <summary>Original-image r coordinate -> expanded-canvas coordinate (0..1 over the whole PNG).</summary>
    public V2 ToCanvas(V2 r) => new((Left + r.X * OriginalWidth) / ExpandedWidth, (Top + r.Y * OriginalHeight) / ExpandedHeight);

    /// <summary>Canvas coordinate clamped to the PNG (outer points on a side without padding).</summary>
    public V2 ToCanvasClamped(V2 r)
    {
        V2 c = ToCanvas(r);
        return new(Math.Clamp(c.X, 0, 1), Math.Clamp(c.Y, 0, 1));
    }

    // Geometric rectangle in r-space; a side without padding is pushed out by a twentieth of a pixel so the mesh
    // boundary never touches it (its UVs are clamped to the PNG edge, so nothing visible is added).
    private const double MinPadPixels = 0.05;
    public double MinR => -Math.Max(Left, MinPadPixels) / OriginalWidth;
    public double MaxR => 1 + Math.Max(Right, MinPadPixels) / OriginalWidth;
    public double MinRY => -Math.Max(Top, MinPadPixels) / OriginalHeight;
    public double MaxRY => 1 + Math.Max(Bottom, MinPadPixels) / OriginalHeight;
}

public sealed class RingTopology
{
    private const int AnchorCount = 4;
    private const double SideSpacingPixels = 20;

    /// <summary>Native local indices of all outer-boundary vertices (residual anchors).</summary>
    public int[] Boundary { get; }
    /// <summary>Outer points (canvas rectangle) in r-space.</summary>
    public V2[] Outer { get; }
    /// <summary>For each outer point: indices into <see cref="Boundary"/> and normalised weights.</summary>
    public int[][] AnchorLoop { get; }
    public double[][] AnchorWeight { get; }
    /// <summary>Band triangles. Value &lt; NativeCount: native local vertex; otherwise NativeCount + outer index.</summary>
    public int[] Triangles { get; }
    public int NativeCount { get; }
    public double MinStaticArea { get; }
    public int Flips { get; }
    public int Islands { get; }
    public int FilledHoles { get; }

    private RingTopology(int[] boundary, V2[] outer, int[][] anchorLoop, double[][] anchorWeight, int[] triangles,
        int nativeCount, double minStaticArea, int flips, int islands, int filledHoles)
    {
        Boundary = boundary; Outer = outer; AnchorLoop = anchorLoop; AnchorWeight = anchorWeight; Triangles = triangles;
        NativeCount = nativeCount; MinStaticArea = minStaticArea; Flips = flips; Islands = islands; FilledHoles = filledHoles;
    }

    public static RingTopology Build(IReadOnlyList<float> regionUvs, IReadOnlyList<int> triangles, CanvasSpec spec)
    {
        string? problem = spec.Problem();
        if (problem != null) throw new InvalidDataException("bad-canvas-spec " + problem);
        int n = regionUvs.Count / 2;
        if (n < 3 || regionUvs.Count % 2 != 0 || triangles.Count % 3 != 0 || triangles.Count == 0) throw new InvalidDataException("bad-mesh-arrays");
        var r = new V2[n];
        for (int i = 0; i < n; i++)
        {
            r[i] = new(regionUvs[2 * i], regionUvs[2 * i + 1]);
            if (!double.IsFinite(r[i].X) || !double.IsFinite(r[i].Y)) throw new InvalidDataException("non-finite-region-uv");
        }
        foreach (int t in triangles) if (t < 0 || t >= n) throw new InvalidDataException("triangle-index-out-of-range");

        // Orientation of the mesh in r-space (sum of triangle signed areas).
        double meshArea = 0;
        for (int i = 0; i < triangles.Count; i += 3)
            meshArea += V2.Cross(r[triangles[i + 1]] - r[triangles[i]], r[triangles[i + 2]] - r[triangles[i]]);
        if (!(Math.Abs(meshArea) > 1e-12)) throw new InvalidDataException("mesh-area-zero");
        int meshSign = Math.Sign(meshArea);

        // Boundary loops, traversed in triangle direction. Outer loops have the mesh's orientation; holes the opposite.
        List<int[]> loops = BoundaryLoops(triangles);
        var islands = new List<int[]>();
        var holes = new List<int[]>();
        foreach (int[] loop in loops)
        {
            double area = LoopArea(r, loop);
            if (Math.Sign(area) == meshSign) islands.Add(loop); else holes.Add(loop);
        }
        if (islands.Count == 0) throw new InvalidDataException("no-outer-boundary");

        double x0 = spec.MinR, x1 = spec.MaxR, y0 = spec.MinRY, y1 = spec.MaxRY;
        foreach (int[] loop in islands)
            foreach (int v in loop)
                if (!(r[v].X > x0 && r[v].X < x1 && r[v].Y > y0 && r[v].Y < y1)) throw new InvalidDataException("boundary-outside-expanded-canvas");

        // Outer rectangle (positive orientation) with side points roughly every SideSpacingPixels.
        var outer = new List<V2>();
        void Side(V2 a, V2 b, double pixels)
        {
            int steps = Math.Max(1, (int)Math.Round(pixels / SideSpacingPixels));
            for (int s = 0; s < steps; s++) outer.Add(a + (b - a) * ((double)s / steps));
        }
        double pixelsX = (x1 - x0) * spec.OriginalWidth, pixelsY = (y1 - y0) * spec.OriginalHeight;
        Side(new(x0, y0), new(x1, y0), pixelsX);
        Side(new(x1, y0), new(x1, y1), pixelsY);
        Side(new(x1, y1), new(x0, y1), pixelsX);
        Side(new(x0, y1), new(x0, y0), pixelsY);
        int k = outer.Count;
        var rectArea = 0.0;
        for (int i = 0; i < k; i++) rectArea += V2.Cross(outer[i], outer[(i + 1) % k]);
        if (rectArea < 0) outer.Reverse();

        // Point ids: 0..k-1 rectangle, then native vertices at k + v.
        var pts = new V2[k + n];
        for (int i = 0; i < k; i++) pts[i] = outer[i];
        for (int v = 0; v < n; v++) pts[k + v] = r[v];

        // Rectangle polygon with every island as a negatively oriented hole, merged by bridges.
        var polygon = Enumerable.Range(0, k).ToList();
        var constraints = new HashSet<(int, int)>();
        for (int i = 0; i < k; i++) constraints.Add(Key(i, (i + 1) % k));
        var holeLoops = islands.Select(loop =>
        {
            int[] ids = loop.Select(v => k + v).ToArray();
            if (PolyArea(pts, ids) > 0) Array.Reverse(ids);
            return ids;
        }).OrderByDescending(ids => ids.Max(id => pts[id].X)).ToList();
        foreach (int[] ids in holeLoops)
            for (int i = 0; i < ids.Length; i++) constraints.Add(Key(ids[i], ids[(i + 1) % ids.Length]));
        for (int h = 0; h < holeLoops.Count; h++)
            polygon = Bridge(pts, polygon, holeLoops[h], holeLoops.Skip(h + 1).ToList());

        var tri = EarClip(pts, polygon);
        int flips = DelaunayFlips(pts, tri, constraints);

        // Holes inside the mesh (original pixels never rasterised) are filled with native vertices only.
        int filled = 0;
        foreach (int[] loop in holes)
        {
            int[] ids = loop.Select(v => k + v).ToArray();
            if (PolyArea(pts, ids) < 0) Array.Reverse(ids);
            var fill = EarClip(pts, ids.ToList());
            var fillConstraints = new HashSet<(int, int)>();
            for (int i = 0; i < ids.Length; i++) fillConstraints.Add(Key(ids[i], ids[(i + 1) % ids.Length]));
            DelaunayFlips(pts, fill, fillConstraints);
            tri.AddRange(fill);
            filled++;
        }

        int Map(int id) => id < k ? n + id : id - k;
        var tris = new int[tri.Count * 3];
        double minArea = double.PositiveInfinity;
        for (int t = 0; t < tri.Count; t++)
        {
            (int a, int b, int c) = tri[t];
            minArea = Math.Min(minArea, V2.Cross(pts[b] - pts[a], pts[c] - pts[a]));
            tris[3 * t] = Map(a); tris[3 * t + 1] = Map(b); tris[3 * t + 2] = Map(c);
        }
        if (!(minArea > 1e-12)) throw new InvalidDataException("static-band-triangle-degenerate minArea=" + minArea);

        // Residual anchors: nearest outer-boundary vertices, inverse-square-distance weights.
        int[] boundary = islands.SelectMany(l => l).Distinct().ToArray();

        // Local linear fit per boundary vertex (1-ring, 2-ring fallback); null = use the global affine only.
        var adjacency = new HashSet<int>[n];
        for (int i = 0; i < n; i++) adjacency[i] = new HashSet<int>();
        for (int i = 0; i < triangles.Count; i += 3)
            for (int e = 0; e < 3; e++)
            {
                int a = triangles[i + e], b = triangles[i + (e + 1) % 3];
                adjacency[a].Add(b); adjacency[b].Add(a);
            }
        var neighbours = new int[boundary.Length][];
        var inverseS = new double[]?[boundary.Length];
        for (int i = 0; i < boundary.Length; i++)
        {
            int v = boundary[i];
            var set = new HashSet<int>(adjacency[v]);
            double[]? inv = TryInverse(r, v, set);
            if (inv == null)
            {
                foreach (int a in adjacency[v].ToArray()) set.UnionWith(adjacency[a]);
                set.Remove(v);
                inv = TryInverse(r, v, set);
            }
            neighbours[i] = set.ToArray();
            inverseS[i] = inv;
        }
        var anchorLoop = new int[k][];
        var anchorWeight = new double[k][];
        for (int o = 0; o < k; o++)
        {
            var nearest = Enumerable.Range(0, boundary.Length).OrderBy(i => (r[boundary[i]] - outer[o]).Length).Take(AnchorCount).ToArray();
            var w = nearest.Select(i => 1.0 / Math.Max(1e-12, Math.Pow((r[boundary[i]] - outer[o]).Length, 2))).ToArray();
            double sum = w.Sum();
            anchorLoop[o] = nearest;
            anchorWeight[o] = w.Select(x => x / sum).ToArray();
        }

        var anchorDistancePx = new double[k][];
        for (int o = 0; o < k; o++)
            anchorDistancePx[o] = anchorLoop[o].Select(i =>
            {
                V2 d = outer[o] - r[boundary[i]];
                return Math.Sqrt(Math.Pow(d.X * spec.OriginalWidth, 2) + Math.Pow(d.Y * spec.OriginalHeight, 2));
            }).ToArray();

        return new(boundary, outer.ToArray(), anchorLoop, anchorWeight, tris, n, minArea, flips, islands.Count, filled)
        {
            Neighbours = neighbours, InverseS = inverseS, AnchorDistancePx = anchorDistancePx,
        };
    }

    private int[][] Neighbours { get; init; } = Array.Empty<int[]>();
    private double[]?[] InverseS { get; init; } = Array.Empty<double[]?>();
    private double[][] AnchorDistancePx { get; init; } = Array.Empty<double[]>();

    /// <summary>Blend scale (pixels): outer points this close to a boundary vertex follow its local deformation.</summary>
    /// <remarks>Offline comparison on real Bunny frames: 0 (global affine + boundary residual) is the best setting;
    /// larger values introduced folds. Kept configurable for tests only.</remarks>
    public static double LocalScalePixels { get; set; } = 0;

    private static double[]? TryInverse(V2[] r, int v, HashSet<int> set)
    {
        double sxx = 0, sxy = 0, syy = 0;
        foreach (int k in set)
        {
            V2 d = r[k] - r[v];
            sxx += d.X * d.X; sxy += d.X * d.Y; syy += d.Y * d.Y;
        }
        double det = sxx * syy - sxy * sxy, trace = sxx + syy;
        if (!(trace > 0) || det < 1e-3 * trace * trace) return null;
        return new[] { syy / det, -sxy / det, -sxy / det, sxx / det };
    }

    private static (int, int) Key(int a, int b) => a < b ? (a, b) : (b, a);

    private static double LoopArea(V2[] r, int[] loop)
    {
        double area = 0;
        for (int i = 0; i < loop.Length; i++) area += V2.Cross(r[loop[i]], r[loop[(i + 1) % loop.Length]]);
        return area;
    }

    private static double PolyArea(V2[] pts, IReadOnlyList<int> ids)
    {
        double area = 0;
        for (int i = 0; i < ids.Count; i++) area += V2.Cross(pts[ids[i]], pts[ids[(i + 1) % ids.Count]]);
        return area;
    }

    private static List<int[]> BoundaryLoops(IReadOnlyList<int> triangles)
    {
        var count = new Dictionary<(int, int), int>();
        for (int i = 0; i < triangles.Count; i += 3)
            for (int e = 0; e < 3; e++)
            {
                var key = Key(triangles[i + e], triangles[i + (e + 1) % 3]);
                count[key] = count.GetValueOrDefault(key) + 1;
            }
        var next = new Dictionary<int, int>();
        for (int i = 0; i < triangles.Count; i += 3)
            for (int e = 0; e < 3; e++)
            {
                int a = triangles[i + e], b = triangles[i + (e + 1) % 3];
                if (count[Key(a, b)] != 1) continue;
                if (next.ContainsKey(a)) throw new InvalidDataException("non-manifold-boundary-vertex");
                next[a] = b;
            }
        if (next.Count < 3) throw new InvalidDataException("boundary-too-small");
        var loops = new List<int[]>();
        var seen = new HashSet<int>();
        foreach (int start in next.Keys.OrderBy(v => v))
        {
            if (seen.Contains(start)) continue;
            var loop = new List<int>();
            int cur = start;
            do
            {
                if (!seen.Add(cur)) throw new InvalidDataException("boundary-loop-crossing");
                loop.Add(cur);
                if (!next.TryGetValue(cur, out cur)) throw new InvalidDataException("open-boundary");
            } while (cur != start);
            if (loop.Count < 3) throw new InvalidDataException("degenerate-boundary-loop");
            loops.Add(loop.ToArray());
        }
        return loops;
    }

    /// <summary>Merge a (negatively oriented) hole into the polygon through a mutually visible vertex pair.</summary>
    private static List<int> Bridge(V2[] pts, List<int> polygon, int[] hole, List<int[]> laterHoles)
    {
        int hs = Enumerable.Range(0, hole.Length).OrderByDescending(i => pts[hole[i]].X).First();
        V2 h = pts[hole[hs]];
        var edges = new List<(int, int)>();
        for (int i = 0; i < polygon.Count; i++) edges.Add((polygon[i], polygon[(i + 1) % polygon.Count]));
        for (int i = 0; i < hole.Length; i++) edges.Add((hole[i], hole[(i + 1) % hole.Length]));
        foreach (int[] other in laterHoles)
            for (int i = 0; i < other.Length; i++) edges.Add((other[i], other[(i + 1) % other.Length]));

        foreach (int pi in Enumerable.Range(0, polygon.Count).OrderBy(i => (pts[polygon[i]] - h).Length))
        {
            int p = polygon[pi];
            V2 pp = pts[p];
            if ((pp - h).Length < 1e-12) continue;
            bool blocked = false;
            foreach ((int a, int b) in edges)
            {
                if (a == p || b == p || a == hole[hs] || b == hole[hs]) continue;
                if (pts[a] == pp || pts[b] == pp || pts[a] == h || pts[b] == h) continue;
                if (SegmentsIntersect(h, pp, pts[a], pts[b])) { blocked = true; break; }
            }
            if (blocked) continue;
            // The bridge must leave p into the polygon interior (between its two neighbours).
            V2 prev = pts[polygon[(pi - 1 + polygon.Count) % polygon.Count]], nextP = pts[polygon[(pi + 1) % polygon.Count]];
            if (!InsideAngle(prev, pp, nextP, h)) continue;
            var merged = new List<int>(polygon.Count + hole.Length + 2);
            for (int i = 0; i <= pi; i++) merged.Add(polygon[i]);
            for (int i = 0; i <= hole.Length; i++) merged.Add(hole[(hs + i) % hole.Length]);
            merged.Add(p);
            for (int i = pi + 1; i < polygon.Count; i++) merged.Add(polygon[i]);
            return merged;
        }
        throw new InvalidDataException("no-bridge");
    }

    /// <summary>True when direction p->q lies inside the interior angle at p of a positively oriented polygon.</summary>
    private static bool InsideAngle(V2 prev, V2 p, V2 next, V2 q)
    {
        V2 a = prev - p, b = next - p, d = q - p;
        bool convex = V2.Cross(p - prev, next - p) >= 0;
        double ca = V2.Cross(b, d), cb = V2.Cross(d, a);
        return convex ? ca > 0 && cb > 0 : !(ca <= 0 && cb <= 0) || (ca > 0 && cb > 0);
    }

    private static bool SegmentsIntersect(V2 a, V2 b, V2 c, V2 d)
    {
        double d1 = V2.Cross(b - a, c - a), d2 = V2.Cross(b - a, d - a);
        double d3 = V2.Cross(d - c, a - c), d4 = V2.Cross(d - c, b - c);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    private static List<(int A, int B, int C)> EarClip(V2[] pts, List<int> polygon)
    {
        var poly = new List<int>(polygon);
        var result = new List<(int, int, int)>();
        int guard = 0;
        while (poly.Count > 3)
        {
            if (++guard > 200000) throw new InvalidDataException("ear-clip-no-progress");
            int best = -1; double bestScore = double.NegativeInfinity;
            for (int i = 0; i < poly.Count; i++)
            {
                int ia = poly[(i - 1 + poly.Count) % poly.Count], ib = poly[i], ic = poly[(i + 1) % poly.Count];
                V2 a = pts[ia], b = pts[ib], c = pts[ic];
                if (!(V2.Cross(b - a, c - a) > 1e-14)) continue;
                bool contains = false;
                for (int j = 0; j < poly.Count && !contains; j++)
                {
                    int id = poly[j];
                    if (id == ia || id == ib || id == ic) continue;
                    V2 p = pts[id];
                    if (p == a || p == b || p == c) continue;
                    if (V2.Cross(b - a, p - a) >= -1e-14 && V2.Cross(c - b, p - b) >= -1e-14 && V2.Cross(a - c, p - c) >= -1e-14)
                        contains = true;
                }
                if (contains) continue;
                double score = MinAngle(a, b, c);
                if (score > bestScore) { bestScore = score; best = i; }
            }
            if (best < 0)
            {
                // Remove a zero-area spike (bridge duplicates or collinear vertices) and continue.
                int spike = -1;
                for (int i = 0; i < poly.Count && spike < 0; i++)
                {
                    V2 a = pts[poly[(i - 1 + poly.Count) % poly.Count]], b = pts[poly[i]], c = pts[poly[(i + 1) % poly.Count]];
                    if (Math.Abs(V2.Cross(b - a, c - a)) <= 1e-14) spike = i;
                }
                if (spike < 0) throw new InvalidDataException("ear-clip-failed remaining=" + poly.Count);
                poly.RemoveAt(spike);
                continue;
            }
            result.Add((poly[(best - 1 + poly.Count) % poly.Count], poly[best], poly[(best + 1) % poly.Count]));
            poly.RemoveAt(best);
        }
        if (V2.Cross(pts[poly[1]] - pts[poly[0]], pts[poly[2]] - pts[poly[0]]) > 1e-14) result.Add((poly[0], poly[1], poly[2]));
        return result;
    }

    private static double MinAngle(V2 a, V2 b, V2 c)
    {
        static double Ang(V2 p, V2 q, V2 s) { V2 u = q - p, v = s - p; return Math.Abs(Math.Atan2(V2.Cross(u, v), u.X * v.X + u.Y * v.Y)); }
        return Math.Min(Ang(a, b, c), Math.Min(Ang(b, c, a), Ang(c, a, b)));
    }

    private static int DelaunayFlips(V2[] pts, List<(int A, int B, int C)> tri, HashSet<(int, int)> constraints)
    {
        int flips = 0;
        for (int pass = 0; pass < 200; pass++)
        {
            bool changed = false;
            var owner = new Dictionary<(int, int), List<int>>();
            for (int t = 0; t < tri.Count; t++)
            {
                var (a, b, c) = tri[t];
                foreach (var e in new[] { Key(a, b), Key(b, c), Key(c, a) })
                {
                    if (!owner.TryGetValue(e, out var list)) owner[e] = list = new List<int>();
                    list.Add(t);
                }
            }
            var dirty = new HashSet<int>();
            foreach (var (edge, list) in owner)
            {
                if (list.Count != 2 || constraints.Contains(edge) || dirty.Contains(list[0]) || dirty.Contains(list[1])) continue;
                int t1 = list[0], t2 = list[1];
                int p = edge.Item1, q = edge.Item2;
                int o1 = Opp(tri[t1], p, q), o2 = Opp(tri[t2], p, q);
                if (o1 == o2) continue;
                if (V2.Cross(pts[q] - pts[p], pts[o1] - pts[p]) < 0) (p, q) = (q, p);
                if (!InCircle(pts[p], pts[q], pts[o1], pts[o2])) continue;
                if (!(V2.Cross(pts[o2] - pts[o1], pts[q] - pts[o1]) > 1e-14) || !(V2.Cross(pts[o1] - pts[o2], pts[p] - pts[o2]) > 1e-14))
                    continue;
                tri[t1] = (o1, p, o2);
                tri[t2] = (o2, q, o1);
                if (V2.Cross(pts[tri[t1].B] - pts[tri[t1].A], pts[tri[t1].C] - pts[tri[t1].A]) < 0) tri[t1] = (o1, o2, p);
                if (V2.Cross(pts[tri[t2].B] - pts[tri[t2].A], pts[tri[t2].C] - pts[tri[t2].A]) < 0) tri[t2] = (o2, o1, q);
                dirty.Add(t1); dirty.Add(t2);
                flips++; changed = true;
            }
            if (!changed) break;
        }
        return flips;
    }

    private static int Opp((int A, int B, int C) t, int p, int q) => t.A != p && t.A != q ? t.A : t.B != p && t.B != q ? t.B : t.C;

    private static bool InCircle(V2 a, V2 b, V2 c, V2 d)
    {
        double adx = a.X - d.X, ady = a.Y - d.Y, bdx = b.X - d.X, bdy = b.Y - d.Y, cdx = c.X - d.X, cdy = c.Y - d.Y;
        double det = (adx * adx + ady * ady) * (bdx * cdy - cdx * bdy) - (bdx * bdx + bdy * bdy) * (adx * cdy - cdx * ady)
            + (cdx * cdx + cdy * cdy) * (adx * bdy - bdx * ady);
        return det > 1e-18;
    }

    /// <summary>
    /// World positions of all outer points: global least-squares affine map (r -> world) over the whole mesh,
    /// plus the inverse-distance blend of the nearest boundary vertices' residuals.
    /// </summary>
    public V2[] OuterPositions(IReadOnlyList<V2> regionUv, Func<int, V2> position)
    {
        int n = NativeCount;
        double mr0 = 0, mr1 = 0, mp0 = 0, mp1 = 0;
        for (int v = 0; v < n; v++) { V2 r = regionUv[v], p = position(v); mr0 += r.X; mr1 += r.Y; mp0 += p.X; mp1 += p.Y; }
        mr0 /= n; mr1 /= n; mp0 /= n; mp1 /= n;
        double sxx = 0, sxy = 0, syy = 0, a = 0, b = 0, c = 0, d = 0;
        for (int v = 0; v < n; v++)
        {
            V2 r = regionUv[v], p = position(v);
            double rx = r.X - mr0, ry = r.Y - mr1, px = p.X - mp0, py = p.Y - mp1;
            sxx += rx * rx; sxy += rx * ry; syy += ry * ry;
            a += px * rx; b += px * ry; c += py * rx; d += py * ry;
        }
        double det = sxx * syy - sxy * sxy;
        if (!(Math.Abs(det) > 1e-18)) throw new InvalidOperationException("global-affine-degenerate");
        double i00 = syy / det, i01 = -sxy / det, i11 = sxx / det;
        double m00 = a * i00 + b * i01, m01 = a * i01 + b * i11, m10 = c * i00 + d * i01, m11 = c * i01 + d * i11;

        // Per boundary vertex: local linear map J_i (least squares over its ring), or the global map if degenerate.
        var local = new double[Boundary.Length * 4];
        for (int i = 0; i < Boundary.Length; i++)
        {
            int o = i * 4;
            double[]? s = InverseS.Length > i ? InverseS[i] : null;
            if (s == null) { local[o] = m00; local[o + 1] = m01; local[o + 2] = m10; local[o + 3] = m11; continue; }
            int v = Boundary[i];
            V2 pv = position(v), rv = regionUv[v];
            double la = 0, lb = 0, lc = 0, ld = 0;
            foreach (int kk in Neighbours[i])
            {
                V2 dp = position(kk) - pv, dr = regionUv[kk] - rv;
                la += dp.X * dr.X; lb += dp.X * dr.Y; lc += dp.Y * dr.X; ld += dp.Y * dr.Y;
            }
            local[o] = la * s[0] + lb * s[2]; local[o + 1] = la * s[1] + lb * s[3];
            local[o + 2] = lc * s[0] + ld * s[2]; local[o + 3] = lc * s[1] + ld * s[3];
        }

        // Outer point = weighted blend over its nearest boundary vertices of  p_i + M_i (o - r_i),
        // M_i = lambda J_i + (1 - lambda) A with lambda = exp(-distance / LocalScalePixels):
        // close to the mesh the band follows the local deformation, far away the whole-mesh motion.
        var result = new V2[Outer.Length];
        for (int q = 0; q < Outer.Length; q++)
        {
            V2 sum = default;
            for (int j = 0; j < AnchorLoop[q].Length; j++)
            {
                int i = AnchorLoop[q][j], v = Boundary[i], o = i * 4;
                double lambda = AnchorDistancePx.Length > q && LocalScalePixels > 0 ? Math.Exp(-AnchorDistancePx[q][j] / LocalScalePixels) : 0;
                double n00 = lambda * local[o] + (1 - lambda) * m00, n01 = lambda * local[o + 1] + (1 - lambda) * m01;
                double n10 = lambda * local[o + 2] + (1 - lambda) * m10, n11 = lambda * local[o + 3] + (1 - lambda) * m11;
                V2 delta = Outer[q] - regionUv[v];
                V2 p = position(v) + new V2(n00 * delta.X + n01 * delta.Y, n10 * delta.X + n11 * delta.Y);
                sum += p * AnchorWeight[q][j];
            }
            result[q] = sum;
        }
        return result;
    }

    /// <summary>
    /// Allocation-free per-frame path used at runtime: global least-squares affine + inverse-distance blend of the
    /// boundary residuals (the offline-best setting, LocalScalePixels = 0). Writes Outer.Length points into result.
    /// </summary>
    public void OuterPositionsFast(V2[] regionUv, V2[] position, V2[] result, V2[] residualScratch)
    {
        int n = NativeCount;
        double mr0 = 0, mr1 = 0, mp0 = 0, mp1 = 0;
        for (int v = 0; v < n; v++) { mr0 += regionUv[v].X; mr1 += regionUv[v].Y; mp0 += position[v].X; mp1 += position[v].Y; }
        mr0 /= n; mr1 /= n; mp0 /= n; mp1 /= n;
        double sxx = 0, sxy = 0, syy = 0, a = 0, b = 0, c = 0, d = 0;
        for (int v = 0; v < n; v++)
        {
            double rx = regionUv[v].X - mr0, ry = regionUv[v].Y - mr1, px = position[v].X - mp0, py = position[v].Y - mp1;
            sxx += rx * rx; sxy += rx * ry; syy += ry * ry;
            a += px * rx; b += px * ry; c += py * rx; d += py * ry;
        }
        double det = sxx * syy - sxy * sxy;
        if (!(Math.Abs(det) > 1e-18)) throw new InvalidOperationException("global-affine-degenerate");
        double i00 = syy / det, i01 = -sxy / det, i11 = sxx / det;
        double m00 = a * i00 + b * i01, m01 = a * i01 + b * i11, m10 = c * i00 + d * i01, m11 = c * i01 + d * i11;
        for (int i = 0; i < Boundary.Length; i++)
        {
            int v = Boundary[i];
            double rx = regionUv[v].X - mr0, ry = regionUv[v].Y - mr1;
            residualScratch[i] = new V2(position[v].X - (mp0 + m00 * rx + m01 * ry), position[v].Y - (mp1 + m10 * rx + m11 * ry));
        }
        for (int q = 0; q < Outer.Length; q++)
        {
            double rx = Outer[q].X - mr0, ry = Outer[q].Y - mr1;
            double x = mp0 + m00 * rx + m01 * ry, y = mp1 + m10 * rx + m11 * ry;
            int[] anchors = AnchorLoop[q];
            double[] weights = AnchorWeight[q];
            for (int j = 0; j < anchors.Length; j++) { V2 res = residualScratch[anchors[j]]; x += res.X * weights[j]; y += res.Y * weights[j]; }
            result[q] = new V2(x, y);
        }
    }

    /// <summary>Smallest (world area / r-space area) of band triangles relative to their mean; &lt;= 0 means a fold.</summary>
    public double MinSignedAreaRatio(IReadOnlyList<V2> regionUv, Func<int, V2> position, V2[] outerPositions)
    {
        V2 P(int v) => v < NativeCount ? position(v) : outerPositions[v - NativeCount];
        V2 R(int v) => v < NativeCount ? regionUv[v] : Outer[v - NativeCount];
        var ratios = new double[Triangles.Length / 3];
        double sign = 0;
        for (int t = 0; t < Triangles.Length; t += 3)
        {
            double world = V2.Cross(P(Triangles[t + 1]) - P(Triangles[t]), P(Triangles[t + 2]) - P(Triangles[t]));
            double uv = V2.Cross(R(Triangles[t + 1]) - R(Triangles[t]), R(Triangles[t + 2]) - R(Triangles[t]));
            ratios[t / 3] = world / uv;
            sign += ratios[t / 3];
        }
        sign = Math.Sign(sign);
        double mean = ratios.Average(q => q * sign);
        return ratios.Min(q => q * sign) / mean;
    }
}
