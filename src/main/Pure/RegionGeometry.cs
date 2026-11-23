namespace SkinForger.Main.Pure;

/// <summary>
/// How a Spine atlas region maps to pixels, and how the Dump turns it into a PNG.
///
/// Every rule here was DERIVED FROM REAL DATA on 2026-11-17 — 1134 manifest entries and the PNGs beside
/// them, across 23 atlases of four costumes — and cross-checked against the conventions SkinForger.Core
/// already relies on (UntouchedRegionRebase computes the same rectangle the same way). Tests\ re-derives
/// all of it on whatever files the machine has.
///
/// The three coordinate systems that meet here:
///   - Spine UV (u, v, u2, v2): 0..1 of the atlas page, v measured from the BOTTOM (Unity);
///   - atlas pixels: top-left origin, which is what manifest.json's x / y hold and what region.x / region.y
///     are set to (Core's rebase writes region.y = pageHeight - packedY - packedHeight);
///   - the part's own image: top-left origin, the PNG the creator edits.
///
/// Verified rules:
///   1. x  = round(min(u, u2) * pageWidth)                                  [0 violations / 1134]
///      y  = pageHeight - round(max(v, v2) * pageHeight)                    [0 violations / 1134]
///      the stored rectangle spans round(|u2 - u| * pageWidth) by round(|v - v2| * pageHeight).
///   2. rotate == (degrees == 90), exactly, with no exception in the data. The stored rectangle is
///      transposed if and only if rotate is set: manifest width / height are the transposed span for
///      degrees 90, and the span as-is for degrees 0, 180 and 270 (25 entries at 180, 35 at 270).
///      So <see cref="Transposed"/> keys off rotate, not off "degrees is a quarter turn".
///   3. manifest width / height are the TRIMMED size; originalWidth / originalHeight are the untrimmed
///      size of the part as it is drawn (127 entries differ).
///   4. The PNG the Dump writes is the UNTRIMMED canvas (originalWidth x originalHeight), with the trimmed
///      drawing placed inside it at (offsetX, originalHeight - offsetY - height): offsetY is measured from
///      the BOTTOM, as Spine stores it. Checked against the alpha bounds of 119 real trimmed PNGs:
///      118 fit this placement, 1 does not (that one's drawing extends past the original trim, which is
///      what happens after a creator paints outside the silhouette). The "from the top" reading fits only
///      84 of them, so it is wrong.
/// Pure (no Unity).
/// </summary>
public static class RegionGeometry
{
    /// <summary>True when the atlas stores the part transposed (width and height swapped).</summary>
    public static bool Transposed(bool rotate) => rotate;

    /// <summary>The <c>rotate</c> flag that belongs with a <c>degrees</c> value (rotate == degrees 90).</summary>
    public static bool RotateFlagFor(int degrees) => Normalize(degrees) == 90;

    /// <summary>Degrees brought into 0 / 90 / 180 / 270.</summary>
    public static int Normalize(int degrees) => ((degrees % 360) + 360) % 360;

    /// <summary>
    /// The region's rectangle in atlas pixels, top-left origin, exactly as manifest.json records x / y and
    /// the stored span. <paramref name="pageWidth"/> / <paramref name="pageHeight"/> are the atlas page size.
    /// </summary>
    public static PixelRect StoredRect(float u, float v, float u2, float v2, int pageWidth, int pageHeight)
    {
        int left = Round(Math.Min(u, u2) * pageWidth);
        int right = Round(Math.Max(u, u2) * pageWidth);
        int topFromBottom = Round(Math.Max(v, v2) * pageHeight);
        int bottomFromBottom = Round(Math.Min(v, v2) * pageHeight);
        return new PixelRect(left, pageHeight - topFromBottom, right - left, topFromBottom - bottomFromBottom);
    }

    /// <summary>
    /// The trimmed size of the part as it is drawn: the stored span, transposed when the atlas turned it.
    /// This is what manifest.json's width / height hold.
    /// </summary>
    public static (int Width, int Height) LogicalSize(PixelRect stored, bool rotate) =>
        Transposed(rotate) ? (stored.Height, stored.Width) : (stored.Width, stored.Height);

    /// <summary>
    /// Where the trimmed drawing sits inside the untrimmed canvas, in top-left image coordinates.
    /// <paramref name="offsetY"/> comes from Spine and is measured from the bottom.
    /// </summary>
    public static PixelRect TrimPlacement(float offsetX, float offsetY, int width, int height, int originalHeight) =>
        new(Round(offsetX), originalHeight - Round(offsetY) - height, width, height);

    /// <summary>
    /// The page size implied by a manifest entry: the span in UV against the span in pixels. Used to check
    /// an entry without having the atlas at hand (Tests), and to recognise an atlas that has grown.
    /// Returns false when the UVs are degenerate.
    /// </summary>
    public static bool SolvePageSize(RegionManifestEntry entry, out int pageWidth, out int pageHeight)
    {
        pageWidth = pageHeight = 0;
        float du = Math.Abs(entry.U2 - entry.U), dv = Math.Abs(entry.V - entry.V2);
        if (du <= 0f || dv <= 0f) return false;
        (int storedWidth, int storedHeight) = Transposed(entry.Rotate)
            ? (entry.Height, entry.Width)
            : (entry.Width, entry.Height);
        pageWidth = Round(storedWidth / du);
        pageHeight = Round(storedHeight / dv);
        return pageWidth > 0 && pageHeight > 0;
    }

    /// <summary>
    /// Builds the PNG the Dump writes for a part, from the atlas as a top-left RGBA buffer:
    /// cut the stored rectangle out, turn it back the way the part is drawn, un-premultiply, and place it
    /// on the untrimmed canvas. Returns the buffer and its size.
    /// </summary>
    public static byte[] BuildPartImage(byte[] atlas, int atlasWidth, int atlasHeight,
        PixelRect stored, int degrees, bool rotate, float offsetX, float offsetY,
        int originalWidth, int originalHeight, out int width, out int height)
    {
        byte[] cut = PixelMath.Crop(atlas, atlasWidth, atlasHeight, stored);

        // Turn it back the way the game's own Dump always has.
        //
        // 2026-11-21: every part packed at 90 or 270 degrees came out of the authored Dump turned a HALF
        // CIRCLE from what the old Dump wrote (proved against the parity references: sound_A, sound_B,
        // face_A and hand_left are each the old picture rotated 180). The arithmetic is the same in both;
        // the COORDINATE SYSTEM is not. The old Main rotated Unity pixel arrays, whose rows run BOTTOM-up,
        // and this works on a TOP-left buffer -- and one index formula read in the two conventions turns
        // the picture opposite ways, so clockwise there is counter-clockwise here. Two wrongs at 90
        // degrees are 180 degrees apart, which is exactly what the pictures showed.
        //
        // So: rotate by +degrees here to land where the old Main landed. Packing a replacement BACK is not
        // affected either way -- AtlasComposer stores every replacement upright (PlacedReplacement.Degrees
        // is 0) -- so this direction is the Dump's business alone.
        byte[] upright = PixelMath.RotateClockwise(cut, stored.Width, stored.Height, Normalize(degrees),
            out int uprightWidth, out int uprightHeight);

        // The data says only `rotate` transposes; a degrees value that disagrees would leave the sizes
        // crossed, so fall back to the stored span rather than writing a wrongly shaped canvas.
        (int logicalWidth, int logicalHeight) = LogicalSize(stored, rotate);
        if (uprightWidth != logicalWidth || uprightHeight != logicalHeight)
        {
            upright = cut;
            uprightWidth = stored.Width;
            uprightHeight = stored.Height;
            logicalWidth = stored.Width;
            logicalHeight = stored.Height;
        }

        PixelMath.UnpremultiplyInPlace(upright, uprightWidth, uprightHeight);

        width = originalWidth > 0 ? originalWidth : logicalWidth;
        height = originalHeight > 0 ? originalHeight : logicalHeight;
        if (width == logicalWidth && height == logicalHeight) return upright;   // untrimmed: nothing to place

        PixelRect place = TrimPlacement(offsetX, offsetY, logicalWidth, logicalHeight, height);
        byte[] canvas = PixelMath.Transparent(width, height);
        PixelMath.Blit(canvas, width, height, upright, uprightWidth, uprightHeight, place.X, place.Y);
        return canvas;
    }

    /// <summary>
    /// The anchor of a part with no placement of its own (a mesh attachment, or anything the quad formula
    /// does not apply to): the centre of its canvas. 54 of the 160 parts measured in game land here.
    /// </summary>
    public static (float X, float Y) DefaultAnchor(int originalWidth, int originalHeight) =>
        (originalWidth / 2f, originalHeight / 2f);

    /// <summary>
    /// The anchor the Dump writes for a region (quad) attachment, in top-left pixels of its canvas.
    ///
    /// MEASURED, not guessed. The old Main's own anchors were read out of the game on 2026-11-17 together
    /// with each attachment's placement, and this formula reproduces all twelve recorded parts to within
    /// 0.0007 px — which is the rounding of the log itself. Upright parts, parts turned by 89, 92, 99, 142
    /// degrees, and parts whose attachment is half a pixel smaller than the canvas are all included; the
    /// twelve are kept as fixtures in Tests\.
    ///
    ///   turn the attachment's own offset back by its rotation:
    ///       x' =  x·cos(r) + y·sin(r)
    ///       y' = -x·sin(r) + y·cos(r)
    ///   measure from the centre of the ATTACHMENT, with Y pointing down (the image's direction):
    ///       (width / 2 - x',  height / 2 + y')
    ///   then scale from attachment units to canvas pixels:
    ///       x (canvasWidth / width),  x (canvasHeight / height)
    ///
    /// The last step is the one that is easy to miss: an attachment is often half a pixel smaller than the
    /// canvas, and without the scaling those parts come out 0.1 - 0.25 px off — small enough to look like
    /// rounding and large enough to move a drawing when the canvas is later enlarged.
    /// </summary>
    public static (float X, float Y) QuadAnchor(float attachmentX, float attachmentY, float attachmentWidth,
        float attachmentHeight, float rotationDegrees, int canvasWidth, int canvasHeight)
    {
        if (attachmentWidth <= 0f || attachmentHeight <= 0f) return DefaultAnchor(canvasWidth, canvasHeight);

        double radians = rotationDegrees * Math.PI / 180.0;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        double turnedX = attachmentX * cos + attachmentY * sin;
        double turnedY = -attachmentX * sin + attachmentY * cos;

        double x = (attachmentWidth / 2.0 - turnedX) * (canvasWidth / (double)attachmentWidth);
        double y = (attachmentHeight / 2.0 + turnedY) * (canvasHeight / (double)attachmentHeight);
        return ((float)x, (float)y);
    }

    /// <summary>
    /// The author's rigid nudge (sidecar offsetX / offsetY, in the picture's pixels) expressed in a QUAD
    /// attachment's own units, ready to be added to its x / y.
    ///
    /// Who moves what: SkinForger.Core's PartTransforms moves MESH parts, and says in as many words that
    /// region parts are left alone because "Main moves region quads by offset itself". Main did not —
    /// nothing in the authored Main wrote a quad's x or y — so for every quad the author's nudge simply
    /// did nothing. 15 of the player's parts carry one, the elfin's head (-18, 30) and fork (70, 20)
    /// among them.
    ///
    /// The directions are the ones SkinForger has always published: NEGATIVE moves left / up, so positive
    /// offsetY moves the part DOWN. That is the same frame Core's PartTransforms uses for a mesh (image
    /// pixels, y down, mapped into the part's own frame), which is what makes a mesh and a quad move the
    /// same way for the same number.
    ///
    /// The conversion is the inverse of <see cref="QuadAnchor"/>, step for step:
    ///   * pixels to attachment units, per axis: x (width / canvasWidth), x (height / canvasHeight);
    ///   * y flipped, because the picture's y points down and Spine's points up;
    ///   * turned by the attachment's own rotation, since QuadAnchor turns the other way to read it back.
    /// </summary>
    public static (float X, float Y) QuadNudge(float offsetX, float offsetY,
        float attachmentWidth, float attachmentHeight, float rotationDegrees,
        int canvasWidth, int canvasHeight)
    {
        if (attachmentWidth <= 0f || attachmentHeight <= 0f || canvasWidth <= 0 || canvasHeight <= 0)
            return (0f, 0f);

        double x = offsetX * (attachmentWidth / (double)canvasWidth);
        double y = -offsetY * (attachmentHeight / (double)canvasHeight);

        // Turned by the attachment's own rotation, and the game's own numbers say why.
        //
        // x / y are an offset in the BONE's space, so a nudge has to be expressed in that space -- and
        // the bone is not upright. SkinForger measured the elfin in game on 2026-11-20: its head is
        // "spun -84.55 deg", its left wing -51.39, its right wing -121.35, while all of them are DRAWN
        // upright on screen. A part can only be spun by its attachment and drawn straight if its bone
        // carries the opposite turn, so bone and attachment cancel, and the picture's own axes are the
        // screen's axes. Turning the author's pixels by the attachment's rotation is exactly the step
        // from the picture's axes into the bone's.
        //
        // Checked against the head: the author's (-18, 30) becomes (-0.316, +0.152) in bone space, which
        // the bone's own +84.55 turns back into (-0.181, -0.300) on screen -- 18 pixels left and 30 down,
        // to the pixel, which is what the author wrote. Dropping the turn (briefly, 718B24B4) sent it 28
        // right and 21 down instead, and the head was the part the player said was worst.
        //
        // The fork is the control: it is spun 0 degrees, so the turn does nothing to it either way, and
        // its (70, 20) is 70 right and 20 down in both.
        double radians = rotationDegrees * Math.PI / 180.0;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        return ((float)(x * cos - y * sin), (float)(x * sin + y * cos));
    }

    /// <summary>
    /// Where a quad attachment sits after the author's nudge and extra turn, from its PRISTINE placement.
    ///
    /// It takes the pristine x / y / rotation rather than the current ones because a skin is applied
    /// again on every scene and every renderer: adding the nudge to whatever the attachment already says
    /// would walk the part further off on each apply.
    ///
    /// <paramref name="turnDegrees"/> is the sidecar's rotation (plus "Rotate"), positive = clockwise on
    /// screen, which is NEGATIVE in Spine where y points up. The part turns about its ANCHOR, which for a
    /// quad is the bone origin — and the bone origin is the local origin x / y are measured from, so the
    /// turn is simply x / y rotated with it. That is the same pivot Core turns a mesh about
    /// (PartTransforms uses DefaultAnchor), so again the two kinds of part agree.
    ///
    /// Core turns first and then shifts; this does the same.
    /// </summary>
    public static (float X, float Y, float Rotation) QuadPlacement(float pristineX, float pristineY,
        float pristineRotation, float nudgeX, float nudgeY, float turnDegrees)
    {
        double radians = -turnDegrees * Math.PI / 180.0;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        double x = pristineX * cos - pristineY * sin;
        double y = pristineX * sin + pristineY * cos;
        return ((float)(x + nudgeX), (float)(y + nudgeY), pristineRotation - turnDegrees);
    }

    /// <summary>
    /// A quad attachment's size in attachment units per canvas pixel. Everything below is in these.
    /// </summary>
    public static (double X, double Y) QuadUnits(float attachmentWidth, float attachmentHeight,
        int canvasWidth, int canvasHeight) =>
        (canvasWidth <= 0 ? 0 : attachmentWidth / (double)canvasWidth,
         canvasHeight <= 0 ? 0 : attachmentHeight / (double)canvasHeight);

    /// <summary>
    /// Where the bone origin falls inside a canvas of the given size, in top-left pixels — a quad's
    /// anchor, with the attachment's scale taken into account.
    ///
    /// This and <see cref="QuadCentre"/> are a matched pair, and they are the rule SkinForger has always
    /// used: the player's own ReplacementDispatcher states both (AnchorFromAttachment and
    /// CenterFromAnchor). An anchor is a PIXEL POSITION in the author's picture where the bone origin
    /// must land — that and nothing else. There is no test of whether the game's old canvas "fits"
    /// inside the picture, and no centred fallback: both were invented on 2026-11-20 and they are what
    /// moved the elfin's head, wings, fork and leg.
    /// </summary>
    public static (float X, float Y) QuadAnchorIn(float attachmentX, float attachmentY,
        float rotationDegrees, float scaleX, float scaleY,
        int canvasWidth, int canvasHeight, double unitX, double unitY)
    {
        double anchorX = canvasWidth * 0.5, anchorY = canvasHeight * 0.5;
        if (unitX == 0 || unitY == 0) return ((float)anchorX, (float)anchorY);

        double radians = rotationDegrees * Math.PI / 180.0;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        double sx = Math.Abs(scaleX) < 0.0001f ? 1.0 : scaleX;
        double sy = Math.Abs(scaleY) < 0.0001f ? 1.0 : scaleY;
        double localX = (cos * -attachmentX + sin * -attachmentY) / sx;
        double localY = (-sin * -attachmentX + cos * -attachmentY) / sy;
        return ((float)(anchorX + localX / unitX), (float)(anchorY - localY / unitY));
    }

    /// <summary>
    /// The attachment x / y that puts the bone origin at <paramref name="anchorX"/> /
    /// <paramref name="anchorY"/> of a <paramref name="canvasWidth"/> x <paramref name="canvasHeight"/>
    /// picture, then moves the part by the author's nudge.
    ///
    /// The nudge is added at the very end, in the bone's own axes and NOT turned by the attachment's
    /// rotation — scaled only from pixels into units. That is how the player's own dispatcher does it,
    /// and their eleven hand-tuned offsets were tuned against exactly this.
    /// </summary>
    public static (float X, float Y) QuadCentre(float rotationDegrees, float scaleX, float scaleY,
        int canvasWidth, int canvasHeight, float anchorX, float anchorY,
        float offsetX, float offsetY, double unitX, double unitY)
    {
        double localX = (anchorX - canvasWidth * 0.5) * unitX;
        double localY = (canvasHeight * 0.5 - anchorY) * unitY;
        double sx = Math.Abs(scaleX) < 0.0001f ? 1.0 : scaleX;
        double sy = Math.Abs(scaleY) < 0.0001f ? 1.0 : scaleY;
        localX *= sx;
        localY *= sy;

        double radians = rotationDegrees * Math.PI / 180.0;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        return ((float)(-(cos * localX - sin * localY) + offsetX * unitX),
                (float)(-(sin * localX + cos * localY) - offsetY * unitY));
    }

    /// <summary>The anchor of a part: the quad formula when it has a placement, the canvas centre otherwise.</summary>
    public static (float X, float Y) AnchorOf(PartSpec part, int canvasWidth, int canvasHeight) =>
        part.HasQuad
            ? QuadAnchor(part.QuadX, part.QuadY, part.QuadWidth, part.QuadHeight, part.QuadRotation, canvasWidth, canvasHeight)
            : DefaultAnchor(canvasWidth, canvasHeight);

    /// <summary>
    /// What an atlas region, AND the mesh attachments that draw from it, must say about themselves once a
    /// replacement image has been placed in a fresh rectangle.
    ///
    /// Every field here is one the game had its own value for, and every one of them becomes a lie the
    /// moment the part moves to a new rectangle:
    ///
    ///   * <c>Width</c>/<c>Height</c> -- the packed size. The game's may be SMALLER than the canvas,
    ///     because the packer trims transparent edges.
    ///   * <c>OriginalWidth</c>/<c>OriginalHeight</c> -- the untrimmed canvas. A dumped PNG is this size,
    ///     which is why a replacement has no trim at all.
    ///   * <c>OffsetX</c>/<c>OffsetY</c> -- where the trimmed pixels sat inside that canvas. Zero now.
    ///   * <c>Rotate</c>/<c>Degrees</c> -- the packer may store a region turned. AtlasComposer never does
    ///     (rule 3), so this is always "not turned".
    ///
    /// It exists as one pure function because the 2026-11-19 hair bug was the region and the mesh
    /// disagreeing: the region was updated and the mesh's own copy of these six numbers was not, so Spine
    /// kept mapping char_1_rock/hair_1 as "181 of 182 pixels, turned 270 degrees" into a rectangle that
    /// was 182 wide and not turned. Both callers now read the answer from here, so they cannot drift.
    /// </summary>
    public static ReplacedRegion AfterReplacement(PixelRect rect, int originalWidth, int originalHeight) =>
        AfterReplacement(rect, originalWidth, originalHeight,
            (rect.Width - originalWidth) / 2.0, (rect.Height - originalHeight) / 2.0);

    /// <summary>
    /// The same, for a replacement drawn on a bigger canvas than the game's.
    ///
    /// <paramref name="left"/> / <paramref name="top"/> say where the GAME's canvas starts inside the
    /// author's picture, in top-left pixels. That is SkinForger's existing contract, the one
    /// SkinForger.Core's mesh canvas states in CanvasSpec:
    ///
    ///     left = anchorX - defaultAnchorX
    ///
    /// and the caller is responsible for falling back to a centred placement when those numbers do not put
    /// the canvas inside the picture — exactly as Core does. Taking left/top rather than the anchors keeps
    /// Main and Core speaking the same language instead of each deriving a placement of its own, which is
    /// how the elfin's quads ended up flung around on 2026-11-20.
    ///
    /// Spine measures a region's offset from the BOTTOM-left of the untrimmed canvas, so:
    ///     OffsetX = -left
    ///     OffsetY = top + canvasHeight - imageHeight
    /// Both are zero when the image is the canvas and starts at (0,0), which is the ordinary case.
    /// </summary>
    public static ReplacedRegion AfterReplacement(PixelRect rect, int originalWidth, int originalHeight,
        double left, double top)
    {
        if (originalWidth <= 0 || originalHeight <= 0)
            return new ReplacedRegion(rect.Width, rect.Height, rect.Width, rect.Height, 0f, 0f, false, 0);

        return new ReplacedRegion(
            rect.Width, rect.Height,
            originalWidth, originalHeight,
            (float)-left,
            (float)(top + originalHeight - rect.Height),
            false, 0);
    }

    /// <summary>
    /// Where the game's canvas starts inside the author's picture: from the anchors when they put it
    /// inside, centred when they do not. The fallback is not a guess — it is the same one Core's resolver
    /// uses, so a part that Core recentres is placed identically here.
    ///
    ///     left = anchorX - defaultAnchorX,   top = anchorY - defaultAnchorY
    ///
    /// <paramref name="defaultAnchorX"/> is the anchor in the GAME's canvas, which for a quad is
    /// <see cref="QuadAnchor"/> and NOT the canvas centre. Passing the centre is what crammed the elfin's
    /// head, wings, fork and leg together on 2026-11-20: a quad's anchor marks where the bone origin falls
    /// inside the picture, which for a head or a wing is routinely outside it (head 157 in a 143-tall
    /// picture, wing_l 130 in a 90-tall one), so measuring it against the centre produces a difference of
    /// tens of pixels, fails <see cref="Inside"/> and recentres a part whose placement was perfectly well
    /// described. <see cref="AnchorOf"/> is the one function that answers this, for a mesh and a quad
    /// alike.
    ///
    /// The author's offsetX / offsetY are deliberately NOT subtracted. They are a RIGID MOVE of the whole
    /// part (SkinForger.Core's PartTransforms applies them, and Main hands them over for exactly that in
    /// RegionAnchor.OffsetX / OffsetY), not a statement about where the canvas sits. Core's own resolver
    /// says so where it reads Main's placement -- "offsetX/Y of a mesh part = rigid move of the whole
    /// part, not a canvas shift" -- and subtracting them here was the second half of the same bug: head
    /// carries a hand-set nudge of (-18, 30) and fork one of (70, 20), which on their own throw the
    /// canvas right out of the picture.
    /// </summary>
    public static (double Left, double Top) CanvasStart(PixelRect rect, int originalWidth, int originalHeight,
        float anchorX, float anchorY, float defaultAnchorX, float defaultAnchorY)
    {
        double left = anchorX - defaultAnchorX;
        double top = anchorY - defaultAnchorY;
        if (Inside(left, top, originalWidth, originalHeight, rect.Width, rect.Height)) return (left, top);
        return ((rect.Width - originalWidth) / 2.0, (rect.Height - originalHeight) / 2.0);
    }

    /// <summary>
    /// Where the game's canvas starts when the author has SAID which of the nine canvas positions they
    /// enlarged from. Null when the sidecar says nothing, or says "auto".
    ///
    /// The nine names, English and Chinese, are SkinForger.Core's own (AutoAnchor.CanvasAnchorOffset);
    /// the two sides must answer the same thing for the same word, so the table is copied exactly.
    /// "top-left" means the drawing stayed at the top left and the room went on the right and bottom.
    /// </summary>
    public static (int Left, int Top)? CanvasAnchorStart(string? canvasAnchor, int extraWidth, int extraHeight)
    {
        string value = (canvasAnchor ?? string.Empty).Trim().ToLowerInvariant()
            .Replace("_", "-").Replace(" ", "-");
        (int X, int Y)? cell = value switch
        {
            "top-left" or "left-top" or "左上" => (0, 0),
            "top" or "top-center" or "top-centre" or "上" or "上中" => (1, 0),
            "top-right" or "right-top" or "右上" => (2, 0),
            "left" or "center-left" or "centre-left" or "middle-left" or "左" or "左中" => (0, 1),
            "center" or "centre" or "middle" or "中" or "居中" or "中间" => (1, 1),
            "right" or "center-right" or "centre-right" or "middle-right" or "右" or "右中" => (2, 1),
            "bottom-left" or "left-bottom" or "左下" => (0, 2),
            "bottom" or "bottom-center" or "bottom-centre" or "下" or "下中" => (1, 2),
            "bottom-right" or "right-bottom" or "右下" => (2, 2),
            _ => null,
        };
        if (cell == null) return null;
        return (cell.Value.X == 0 ? 0 : cell.Value.X == 1 ? extraWidth / 2 : extraWidth,
                cell.Value.Y == 0 ? 0 : cell.Value.Y == 1 ? extraHeight / 2 : extraHeight);
    }

    /// <summary>Whether the canvas lies inside the picture, with Core's own 0.51 px tolerance.</summary>
    public static bool Inside(double left, double top, int canvasWidth, int canvasHeight,
        int imageWidth, int imageHeight)
    {
        const double tolerance = 0.51;
        double right = imageWidth - left - canvasWidth;
        double bottom = imageHeight - top - canvasHeight;
        return left >= -tolerance && top >= -tolerance && right >= -tolerance && bottom >= -tolerance;
    }

    /// <summary>
    /// Which part of a replacement image a MESH should sample.
    ///
    /// A mesh's shape is its vertices, and SkinForger does not add vertices — so a mesh cannot be made to
    /// cover a canvas bigger than the one the game gave it. What it CAN do is keep its exact shape and
    /// sample the piece of the author's picture that corresponds to the game's canvas, placed by the
    /// anchor. The author's extra border is then simply not shown, and nothing moves, stretches or
    /// collapses.
    ///
    /// <paramref name="anchorX"/> is the anchor in the author's canvas (the sidecar), and
    /// <paramref name="defaultAnchorX"/> the anchor in the game's. Their difference says how far into the
    /// author's picture the game's canvas starts:
    ///
    ///     start = anchorX - defaultAnchorX
    ///
    /// For an image the same size as the canvas with an unmoved anchor this is (0,0) and the whole image
    /// is sampled, which is exactly the ordinary case.
    ///
    /// Clamped to stay inside the image: an anchor that would hang off the edge samples the nearest
    /// in-bounds window rather than reading someone else's pixels out of the atlas.
    /// </summary>
    public static PixelRect MeshWindow(PixelRect placed, int canvasWidth, int canvasHeight,
        float anchorX, float anchorY, float defaultAnchorX, float defaultAnchorY)
    {
        if (canvasWidth <= 0 || canvasHeight <= 0) return placed;

        int width = Math.Min(canvasWidth, placed.Width);
        int height = Math.Min(canvasHeight, placed.Height);
        int startX = Math.Clamp(Round(anchorX - defaultAnchorX), 0, placed.Width - width);
        int startY = Math.Clamp(Round(anchorY - defaultAnchorY), 0, placed.Height - height);
        return new PixelRect(placed.X + startX, placed.Y + startY, width, height);
    }

    private static int Round(float value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
    private static int Round(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
}
