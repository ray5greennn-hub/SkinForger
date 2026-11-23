namespace SkinForger.Main.Pure;

/// <summary>
/// Where the GAME's drawing sits inside an author's enlarged picture, found by looking at the pixels.
///
/// Why this exists: when an author enlarges a part's canvas, something has to say which side they added
/// the room to. The sidecar's anchor is supposed to, and usually does — but an anchor the author typed by
/// hand, or one written before the canvas was enlarged again, can place the game's canvas outside the
/// picture entirely. SkinForger then had nothing left but to centre the drawing, which is a guess, and on
/// the player's elfin it is a wrong one for four of its six enlarged parts (head, wing_l, fork, leg_r_a).
///
/// The pixels are not a guess. Both images are already in memory at that moment — the game's atlas, which
/// every replacement is composed from, and the author's PNG — so the original can simply be looked for
/// inside the bigger picture. That is what SkinForger itself used to do: wing_r's sidecar still records
/// the result in its own words, "matched game atlas turn 2, y from top, score=0.19".
///
/// The search is tiny. It only runs when the canvas grew, and only over the room that was added: the
/// player's largest case is the fork, 17 by 27 pixels of slack, which is 486 positions.
///
/// Pure (no Unity).
/// </summary>
public static class CanvasMatch
{
    /// <summary>How closely the silhouette has to line up before a match is believed at all.</summary>
    private const double Close = 0.05;

    /// <summary>How much better than the nearest rival position the winner has to be.</summary>
    private const double Clear = 0.01;

    /// <summary>A match, with the score that earned it (lower is better; 0 is identical).</summary>
    public readonly record struct Found(int Left, int Top, double Score, double Centred, bool Confident);

    /// <summary>
    /// Looks for <paramref name="original"/> inside <paramref name="author"/> and answers where it sits.
    ///
    /// Both buffers are RGBA, top-left origin, four bytes per pixel. The score is the mean absolute
    /// difference per channel over the original's area, scaled to 0..1; alpha counts double, because the
    /// silhouette is what identifies a part even when the author has recoloured it completely.
    ///
    /// <see cref="Found.Confident"/> is true when the best position is clearly better than the centred
    /// one AND clearly better than the next-best position that is not next to it — a drawing that matches
    /// everywhere equally (a flat colour, an empty canvas) says nothing about where it belongs, and
    /// centring is the honest answer for it.
    /// </summary>
    public static Found Locate(byte[] original, int originalWidth, int originalHeight,
        byte[] author, int authorWidth, int authorHeight)
    {
        int centredLeft = (authorWidth - originalWidth) / 2;
        int centredTop = (authorHeight - originalHeight) / 2;
        var centred = new Found(centredLeft, centredTop, 1.0, 1.0, false);

        if (originalWidth <= 0 || originalHeight <= 0) return centred;
        if (authorWidth < originalWidth || authorHeight < originalHeight) return centred;
        if (original.Length < originalWidth * originalHeight * 4) return centred;
        if (author.Length < authorWidth * authorHeight * 4) return centred;

        int slackX = authorWidth - originalWidth;
        int slackY = authorHeight - originalHeight;

        double best = double.MaxValue, second = double.MaxValue, centredScore = 1.0;
        int bestLeft = centredLeft, bestTop = centredTop, secondLeft = -99, secondTop = -99;

        for (int top = 0; top <= slackY; top++)
        for (int left = 0; left <= slackX; left++)
        {
            double score = Difference(original, originalWidth, originalHeight,
                                      author, authorWidth, left, top);
            if (left == centredLeft && top == centredTop) centredScore = score;
            if (score < best)
            {
                // The previous best only counts as a rival if it is not a neighbour of the new one.
                if (Math.Abs(bestLeft - left) > 1 || Math.Abs(bestTop - top) > 1)
                {
                    second = best;
                    secondLeft = bestLeft;
                    secondTop = bestTop;
                }
                best = score;
                bestLeft = left;
                bestTop = top;
            }
            else if (score < second && (Math.Abs(bestLeft - left) > 1 || Math.Abs(bestTop - top) > 1))
            {
                second = score;
                secondLeft = left;
                secondTop = top;
            }
        }

        _ = (secondLeft, secondTop);
        // No room to move in either direction: there is only one position, and it is the answer.
        if (slackX == 0 && slackY == 0) return new Found(0, 0, best, centredScore, true);

        // Believed when the silhouette actually lines up AND the winning position is a clear, separate
        // minimum. A picture with nothing to go on -- empty, or one flat colour -- matches every position
        // equally well, so its rival is just as good and it is not believed; centring is honest for it.
        bool confident = best <= Close && (second == double.MaxValue || best + Clear < second);
        return confident
            ? new Found(bestLeft, bestTop, best, centredScore, true)
            : new Found(centredLeft, centredTop, best, centredScore, false);
    }

    /// <summary>
    /// Where the game's canvas starts when the drawing cannot be FOUND, only followed: line the two
    /// drawings up by where their ink is.
    ///
    /// This is the case an author who REDREW a part lands in. There is no original left in the picture to
    /// search for — the player's elfin was redrawn as a different character altogether, and its silhouette
    /// scores 0.12 to 0.56 against the game's, which is nowhere near a match. Centring is then the usual
    /// answer, and centring is blind: it lines up the CANVASES, and an author who drew their part a
    /// little off-centre in a bigger canvas gets it moved by exactly that much.
    ///
    /// Lining up the INK instead is what a person does by eye. Both drawings are the same part of the
    /// same character, so putting the middle of the author's drawing where the middle of the game's
    /// drawing was keeps the part where it belongs, however differently it is drawn.
    ///
    /// Falls back to centring when either picture has no ink at all. The answer is kept inside the room
    /// the author actually added, so a part can never be placed outside its own picture.
    /// </summary>
    public static Found AlignByInk(byte[] original, int originalWidth, int originalHeight,
        byte[] author, int authorWidth, int authorHeight)
    {
        int centredLeft = (authorWidth - originalWidth) / 2;
        int centredTop = (authorHeight - originalHeight) / 2;
        var centred = new Found(centredLeft, centredTop, 1.0, 1.0, false);

        if (originalWidth <= 0 || originalHeight <= 0) return centred;
        if (authorWidth < originalWidth || authorHeight < originalHeight) return centred;
        if (original.Length < originalWidth * originalHeight * 4) return centred;
        if (author.Length < authorWidth * authorHeight * 4) return centred;

        if (!InkBox(original, originalWidth, originalHeight, out int ol, out int ot, out int or, out int ob))
            return centred;
        if (!InkBox(author, authorWidth, authorHeight, out int al, out int at, out int ar, out int ab))
            return centred;

        double originalCentreX = (ol + or) / 2.0, originalCentreY = (ot + ob) / 2.0;
        double authorCentreX = (al + ar) / 2.0, authorCentreY = (at + ab) / 2.0;

        int left = (int)Math.Round(authorCentreX - originalCentreX, MidpointRounding.AwayFromZero);
        int top = (int)Math.Round(authorCentreY - originalCentreY, MidpointRounding.AwayFromZero);
        left = Math.Clamp(left, 0, authorWidth - originalWidth);
        top = Math.Clamp(top, 0, authorHeight - originalHeight);
        return new Found(left, top, 0.0, 0.0, true);
    }

    /// <summary>The box the visible pixels occupy, inclusive. False when the picture is empty.</summary>
    private static bool InkBox(byte[] image, int width, int height,
        out int left, out int top, out int right, out int bottom)
    {
        left = top = int.MaxValue;
        right = bottom = int.MinValue;
        for (int y = 0; y < height; y++)
        {
            int at = y * width * 4 + 3;
            for (int x = 0; x < width; x++, at += 4)
            {
                if (image[at] == 0) continue;
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;
            }
        }
        return right >= left && bottom >= top;
    }

    /// <summary>
    /// How badly the original disagrees with the window of the author's picture at (left, top).
    ///
    /// The SILHOUETTE decides: the mean absolute difference in alpha, 0 (identical) to 1. Colour is only
    /// a tie-breaker, at a thousandth of the weight, because an author redraws a part — recolours it,
    /// shades it differently — far more often than they re-cut its outline. Judging on colour would lose
    /// exactly the parts this exists for.
    /// </summary>
    private static double Difference(byte[] original, int originalWidth, int originalHeight,
        byte[] author, int authorWidth, int left, int top)
    {
        long alpha = 0, colour = 0;
        for (int y = 0; y < originalHeight; y++)
        {
            int from = y * originalWidth * 4;
            int into = ((top + y) * authorWidth + left) * 4;
            for (int x = 0; x < originalWidth; x++, from += 4, into += 4)
            {
                alpha += Math.Abs(original[from + 3] - author[into + 3]);
                colour += Math.Abs(original[from] - author[into]);
                colour += Math.Abs(original[from + 1] - author[into + 1]);
                colour += Math.Abs(original[from + 2] - author[into + 2]);
            }
        }
        double pixels = 255.0 * originalWidth * originalHeight;
        return alpha / pixels + colour / (pixels * 3.0) * 0.001;
    }
}
