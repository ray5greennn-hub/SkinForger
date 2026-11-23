namespace SkinForger.Main.Pure;

/// <summary>A replacement image waiting to be placed in the runtime atlas.</summary>
/// <param name="Identity">The part it replaces (atlas|region|attachment).</param>
/// <param name="Image">Top-left RGBA pixels, straight (not premultiplied).</param>
public sealed record Replacement(string Identity, byte[] Image, int Width, int Height);

/// <summary>Where a replacement ended up, and what the atlas region must now say.</summary>
/// <param name="Rect">Its own rectangle in the runtime atlas, top-left origin.</param>
/// <param name="U">Left edge, 0..1 of the atlas width.</param>
/// <param name="V">TOP edge in Spine's bottom-up UV, i.e. the larger v.</param>
/// <param name="U2">Right edge.</param>
/// <param name="V2">Bottom edge in bottom-up UV, i.e. the smaller v.</param>
public sealed record PlacedReplacement(string Identity, PixelRect Rect, float U, float V, float U2, float V2)
{
    /// <summary>A placed replacement is never stored turned, so these are what the region must carry.</summary>
    public bool Rotate => false;
    public int Degrees => 0;
}

/// <summary>What one composition produced.</summary>
public sealed record AtlasComposition(int Width, int Height, byte[] Pixels, List<PlacedReplacement> Placed, List<string> Refused);

/// <summary>
/// Builds the runtime atlas: the game's own atlas with every replacement image placed in a rectangle of its
/// own, and the UVs each region must be given to find it.
///
/// Four rules, each of them a bug that has already happened (SF日志):
///
///  1. EVERY REPLACEMENT GETS A FRESH, INDEPENDENT RECTANGLE. The game packs its atlas by polygon, so the
///     rectangles of two parts overlap (2026-10-04: hand_right was covered by hair_4 over 1196 pixels).
///     Writing a replacement into the original rectangle therefore damages a neighbour. New rectangles are
///     allocated in space added BELOW the original atlas, which is never written to.
///  2. THE ORIGINAL ATLAS IS THE ONLY INPUT. Composing from a previously composed atlas makes it grow
///     without end (2026-10-01: 2048 -> 2738 -> 3428 -> 4118). This takes the original pixels and the list
///     of replacements, and nothing else.
///  3. A NEW RECTANGLE IS NEVER STORED TURNED (rotate = false, degrees = 0), whatever the original was.
///     The rotation contract is where the 2026-10-04 breakage came from; a fresh rectangle has no reason to
///     carry it, and the region is told so explicitly rather than inheriting it.
///  4. THE ATLAS IS PREMULTIPLIED. Replacement PNGs are straight alpha, so they are premultiplied on the
///     way in — otherwise edges go dark or grow an outline.
///
/// Pure (no Unity): the caller hands over the original atlas as a top-left RGBA buffer and gets the new one
/// back the same way, so the whole composition is checked outside the game.
/// </summary>
public static class AtlasComposer
{
    /// <summary>A pixel of space around each placed image, so bilinear sampling cannot pull in a neighbour.</summary>
    public const int Padding = 2;

    /// <summary>
    /// Places every replacement under the original atlas. The result's height grows as needed (to a multiple
    /// of four, which keeps the texture friendly to upload); an image wider than the atlas is refused rather
    /// than squeezed.
    /// </summary>
    public static AtlasComposition Compose(byte[] original, int width, int height, IEnumerable<Replacement> replacements)
    {
        var placed = new List<PlacedReplacement>();
        var refused = new List<string>();
        var accepted = new List<(Replacement Item, PixelRect Rect)>();

        int shelfTop = height + Padding;       // the original atlas is never written to
        int shelfHeight = 0;
        int cursor = Padding;

        // Tallest first, then by identity. Shelf packing wastes the difference between the tallest image on
        // a shelf and every other image on it, so putting similar heights together keeps the atlas short.
        // Measured against the old Main on 2026-11-17: it grew Bassist's atlas to 512x7663 in twelve steps,
        // copying the whole thing again each time (EnsureAtlasCapacity, 257 ms). Here the size is worked out
        // first and the atlas is allocated once.
        foreach (Replacement item in replacements
                     .OrderByDescending(r => r.Height)
                     .ThenBy(r => r.Identity, StringComparer.Ordinal))
        {
            if (item.Width <= 0 || item.Height <= 0 || item.Image.Length != PixelMath.BufferLength(item.Width, item.Height))
            {
                refused.Add($"{item.Identity}: the image is {item.Width}x{item.Height} but holds {item.Image.Length} bytes");
                continue;
            }
            if (item.Width + Padding * 2 > width)
            {
                refused.Add($"{item.Identity}: {item.Width} px wide does not fit an atlas of {width}");
                continue;
            }

            if (cursor + item.Width + Padding > width)   // next shelf
            {
                shelfTop += shelfHeight + Padding;
                shelfHeight = 0;
                cursor = Padding;
            }

            var rect = new PixelRect(cursor, shelfTop, item.Width, item.Height);
            accepted.Add((item, rect));
            cursor += item.Width + Padding;
            shelfHeight = Math.Max(shelfHeight, item.Height);
        }

        int newHeight = accepted.Count == 0 ? height : Round4(shelfTop + shelfHeight + Padding);
        byte[] pixels = PixelMath.Transparent(width, newHeight);
        PixelMath.Blit(pixels, width, newHeight, original, width, height, 0, 0);

        foreach ((Replacement item, PixelRect rect) in accepted)
        {
            byte[] image = (byte[])item.Image.Clone();
            PixelMath.PremultiplyInPlace(image, item.Width, item.Height);
            PixelMath.Blit(pixels, width, newHeight, image, item.Width, item.Height, rect.X, rect.Y);
            placed.Add(Place(item.Identity, rect, width, newHeight));
        }

        return new AtlasComposition(width, newHeight, pixels, placed, refused);
    }

    /// <summary>
    /// The UVs of a rectangle in an atlas of the given size. Spine measures v from the BOTTOM while the
    /// rectangle is in top-left pixels, so the top edge becomes the LARGER v — the same conversion
    /// SkinForger.Core's rebase does when it writes region.y back.
    /// </summary>
    public static PlacedReplacement Place(string identity, PixelRect rect, int atlasWidth, int atlasHeight)
    {
        float u = rect.X / (float)atlasWidth;
        float u2 = rect.Right / (float)atlasWidth;
        float v = (atlasHeight - rect.Y) / (float)atlasHeight;            // top edge
        float v2 = (atlasHeight - rect.Bottom) / (float)atlasHeight;      // bottom edge
        return new PlacedReplacement(identity, rect, u, v, u2, v2);
    }

    /// <summary>
    /// Where the original regions are after the atlas grew. Nothing moves — the original pixels stay at the
    /// same coordinates — but every UV was a fraction of the OLD height, so the vertical ones change.
    /// A region that is being replaced does not go through here; it gets its own rectangle instead.
    /// </summary>
    public static (float V, float V2) Rebase(float v, float v2, int oldHeight, int newHeight)
    {
        if (oldHeight <= 0 || newHeight <= 0 || oldHeight == newHeight) return (v, v2);
        // v is a fraction from the bottom of the old atlas; the bottom moved down by (newHeight - oldHeight).
        float top = (1f - v) * oldHeight;          // pixels from the old top edge
        float bottom = (1f - v2) * oldHeight;
        return (1f - top / newHeight, 1f - bottom / newHeight);
    }

    private static int Round4(int value) => (value + 3) / 4 * 4;
}
