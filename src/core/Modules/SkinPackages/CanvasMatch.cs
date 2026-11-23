namespace SkinForger.SkinPackages;

/// <summary>
/// Finds where an original image lies inside a bigger canvas (2026-11-16, "the anchor must not move when the canvas
/// is enlarged, whichever side"). Pure .NET (testable outside the game). Pixels are RGBA, row 0 = bottom (Unity).
/// Coarse search on a sampled grid over every offset, then a 1-pixel refinement around the best one. The score is
/// the mean alpha difference plus, where both are visible, a colour difference; confidence is judged against the
/// centred position. Returns the offset of the original's TOP-LEFT corner inside the canvas, in top-left image
/// coordinates (the JSON's origin).
/// </summary>
public static class CanvasMatch
{
    public readonly record struct Result(int Left, int Top, double Score, double CentredScore, bool Confident, string Method);

    /// <summary>
    /// Two comparisons, the more convincing confident one wins (2026-11-16):
    ///  - colour: only where the original has drawing, alpha + colour (survives a background filled behind the part);
    ///  - shape: every pixel, alpha only (survives recolouring: Maid hair_front light blue -> navy, same outline).
    /// </summary>
    public static Result Find(byte[] original, int ow, int oh, byte[] canvas, int w, int h)
    {
        // 2026-11-16 (Maid headhoop): a candidate read from an EMPTY part of the atlas is fully transparent and
        // "matched by shape" with score 0 any empty area of the canvas. An original must have real drawing.
        int drawn = 0;
        for (int i = 3; i < Math.Min(original.Length, ow * oh * 4); i += 4) if (original[i] > 32) drawn++;
        if (drawn < Math.Max(16, ow * oh / 50))
            return new Result((w - ow) / 2, (h - oh) / 2, double.NaN, double.NaN, false, "original-empty");
        Result colour = FindMode(original, ow, oh, canvas, w, h, shape: false);
        Result shape = FindMode(original, ow, oh, canvas, w, h, shape: true);
        if (colour.Confident && !shape.Confident) return colour;
        if (shape.Confident && !colour.Confident) return shape;
        if (!colour.Confident) return colour;   // neither: report the colour attempt (centred)
        if (colour.Left == shape.Left && colour.Top == shape.Top) return colour;
        // Both confident but different: the one that beats its centred score by more.
        double rc = colour.CentredScore > 0 ? colour.Score / colour.CentredScore : 1, rs = shape.CentredScore > 0 ? shape.Score / shape.CentredScore : 1;
        return rs < rc ? shape : colour;
    }

    private static Result FindMode(byte[] original, int ow, int oh, byte[] canvas, int w, int h, bool shape)
    {
        int centredLeft = (w - ow) / 2, centredTop = (h - oh) / 2;
        if (ow <= 0 || oh <= 0 || w < ow || h < oh || original.Length < ow * oh * 4 || canvas.Length < w * h * 4)
            return new Result(centredLeft, centredTop, double.NaN, double.NaN, false, "invalid-input");

        int stride = Math.Max(1, (int)Math.Ceiling(Math.Max(ow, oh) / 48.0));
        int fine = Math.Max(1, stride / 2);
        long positions = (long)(w - ow + 1) * (h - oh + 1);
        int fx = 0, fy = 0;
        double fineBest = double.MaxValue;
        if (positions <= 4000)
        {
            // Small enlargement: every position (2026-11-16: the coarse grid could miss the true spot by a few pixels).
            for (int x = 0; x <= w - ow; x++)
                for (int y = 0; y <= h - oh; y++)
                {
                    double score = Score(original, ow, oh, canvas, w, h, x, y, fine, fineBest, shape);
                    if (score < fineBest) { fineBest = score; fx = x; fy = y; }
                }
        }
        else
        {
            int bestX = 0, bestY = 0;
            double best = double.MaxValue;
            foreach (int x in Steps(w - ow, stride))
                foreach (int y in Steps(h - oh, stride))
                {
                    double score = Score(original, ow, oh, canvas, w, h, x, y, stride, best, shape);
                    if (score < best) { best = score; bestX = x; bestY = y; }
                }
            for (int x = Math.Max(0, bestX - stride); x <= Math.Min(w - ow, bestX + stride); x++)
                for (int y = Math.Max(0, bestY - stride); y <= Math.Min(h - oh, bestY + stride); y++)
                {
                    double score = Score(original, ow, oh, canvas, w, h, x, y, fine, fineBest, shape);
                    if (score < fineBest) { fineBest = score; fx = x; fy = y; }
                }
        }
        // The centred position competes on the same terms (same sampling).
        int centredY = (h - oh) - centredTop;
        double centred = Score(original, ow, oh, canvas, w, h, centredLeft, centredY, fine, double.MaxValue, shape);
        if (centred <= fineBest) { fineBest = centred; fx = centredLeft; fy = centredY; }
        // Image rows are bottom-up; the offset found is from the bottom-left. Convert to top-left.
        int left = fx, top = (h - oh) - fy;
        bool atCentre = left == centredLeft && top == centredTop;
        // Scores count only the original's drawn pixels: a true match is near 0 (edges / repainting raise it).
        // Accept a clearly good match, or a fair one that clearly beats the centre. 2026-11-16 (Maid headhoop): a weak
        // score AT the centre (0.34 = the centred score) was taken as "matched" - it only meant nothing fits better.
        bool confident = shape
            ? fineBest <= 0.12 || (fineBest <= 0.25 && fineBest <= centred * 0.6) || (atCentre && fineBest <= 0.18)
            : fineBest <= 0.2 || (fineBest <= 0.35 && fineBest <= centred * 0.7) || (atCentre && fineBest <= 0.25);
        return confident
            ? new Result(left, top, fineBest, centred, true, shape ? "matched by shape" : "matched by colour")
            : new Result(centredLeft, centredTop, fineBest, centred, false, "centred(low-confidence)");
    }

    /// <summary>Mean alpha difference over every sampled pixel of the original box (outline match, colours ignored).</summary>
    private static double ShapeScore(byte[] o, int ow, int oh, byte[] c, int w, int x, int y, int step, double stopAbove)
    {
        double sum = 0;
        int count = 0, seen = 0;
        for (int oy = 0; oy < oh; oy += step)
        {
            int orow = oy * ow, crow = (oy + y) * w + x;
            for (int ox = 0; ox < ow; ox += step)
            {
                sum += Math.Abs(o[(orow + ox) * 4 + 3] - c[(crow + ox) * 4 + 3]) / 255.0;
                count++;
            }
            if (count > 64 && ++seen > 4 && sum / count > stopAbove * 1.5) return double.MaxValue;
        }
        return count == 0 ? double.MaxValue : sum / count;
    }

    private static IEnumerable<int> Steps(int max, int stride)
    {
        for (int v = 0; v <= max; v += stride) yield return v;
        if (max % stride != 0) yield return max;
    }

    /// <summary>
    /// Mean difference (0..1) of the original placed at (x, y) (bottom-left offset) in the canvas, measured only where
    /// the ORIGINAL has drawing (alpha &gt; 32). 2026-11-16: the creator filled the new canvas with a solid colour
    /// behind the part; comparing the original's transparent area as well made every position look equally bad.
    /// </summary>
    private static double Score(byte[] o, int ow, int oh, byte[] c, int w, int h, int x, int y, int step, double stopAbove, bool shape)
    {
        if (shape) return ShapeScore(o, ow, oh, c, w, x, y, step, stopAbove);
        double sum = 0;
        int count = 0, seen = 0;
        for (int oy = 0; oy < oh; oy += step)
        {
            int orow = oy * ow, crow = (oy + y) * w + x;
            for (int ox = 0; ox < ow; ox += step)
            {
                int oi = (orow + ox) * 4, ci = (crow + ox) * 4;
                int ao = o[oi + 3];
                if (ao <= 32) continue;
                int ac = c[ci + 3];
                double d = Math.Abs(ao - ac) / 255.0;
                if (ac > 32)
                    d += (Math.Abs(o[oi] - c[ci]) + Math.Abs(o[oi + 1] - c[ci + 1]) + Math.Abs(o[oi + 2] - c[ci + 2])) / (3.0 * 255.0) * 0.5;
                sum += d;
                count++;
            }
            seen++;
            // Early exit once enough rows show this offset cannot win.
            if (count > 64 && seen > 4 && sum / count > stopAbove * 1.5) return double.MaxValue;
        }
        return count < 8 ? double.MaxValue : sum / count;
    }
}
