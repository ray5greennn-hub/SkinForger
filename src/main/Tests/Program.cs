using System.Text.Json;
using SkinForger.Main.Pure;

namespace SkinForger.Main.Tests;

/// <summary>
/// Offline checks of the pure layer. Two parts: rules (pixel math, selections, layout) and, when a real
/// SkinCreator\Images folder is given, a round-trip of every manifest.json and anchor sidecar in it — the
/// formats are frozen, so "read then write gives the same meaning" is the thing that must hold.
/// </summary>
internal static class Program
{
    private static int _passed, _failed;

    /// <summary>
    /// The package-file shim. SkinForger.Core is not loaded here, which is exactly the case that has to
    /// keep working: with no Core at all, every call must be the plain System.IO one, because that is what
    /// a skin in a folder -- every skin the player has -- depends on.
    /// </summary>
    private static void SkinFileRules()
    {
        Check("with no Core loaded, the package reader is not routed", !SkinFiles.Routed);
        Check("with no Core loaded, no package Other folder is offered", SkinFiles.PackageOtherPictures == null);
        Check("a missing file does not exist", !SkinFiles.Exists(Path.Combine(
            Path.GetTempPath(), "skinforger-no-such-file-" + Guid.NewGuid().ToString("N") + ".png")));
        Check("an empty path does not exist", !SkinFiles.Exists(null) && !SkinFiles.Exists(string.Empty));
        Check("a missing folder does not exist", !SkinFiles.DirectoryExists(Path.Combine(
            Path.GetTempPath(), "skinforger-no-such-folder-" + Guid.NewGuid().ToString("N"))));

        string file = Path.Combine(Path.GetTempPath(), "skinforger-shim-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(file, "forged");
            Check("a real file is found through the shim", SkinFiles.Exists(file));
            Check("a real file reads back through the shim",
                  SkinFiles.ReadAllText(file) == "forged" && SkinFiles.ReadAllBytes(file).Length == 6);
            Check("a real folder is found through the shim",
                  SkinFiles.DirectoryExists(Path.GetDirectoryName(file)));
            Check("a real folder lists through the shim",
                  SkinFiles.GetFiles(Path.GetDirectoryName(file)!, Path.GetFileName(file),
                                     SearchOption.TopDirectoryOnly).Length == 1);
            Check("nothing is in a package when no package is mounted", !SkinFiles.IsInPackage(file));
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Finding the GAME's drawing inside an author's enlarged picture.
    ///
    /// This is the answer for a part whose sidecar anchor cannot place the canvas -- four of the player's
    /// six enlarged elfin parts. Centring them is a guess; the pixels are not.
    /// </summary>
    /// <summary>
    /// A region that hangs off its own atlas page. The game has one, and refusing it cost the player a
    /// character's hair in a song.
    /// </summary>
    private static void OverflowingRegionRules()
    {
        // Muse Dash's own char_1_rock: hair_1 is packed at u 0.91259766..1.0009766 of a 2048-wide page,
        // i.e. x 1869 with a span of 181 -- two pixels past the edge. The sampler clamps and the game
        // draws it; SkinForger has to keep the part, not drop it.
        var hair1 = new PixelRect(1869, 26, 181, 176);
        Check("the game's own hair_1 really does hang off its page", !hair1.FitsIn(2048, 512));

        PixelRect kept = hair1.ClampedTo(2048, 512);
        Check("hair_1 survives, trimmed to the page",
              !kept.IsEmpty && kept.X == 1869 && kept.Y == 26 && kept.Width == 179 && kept.Height == 176,
              $"{kept.Width}x{kept.Height} at {kept.X},{kept.Y}");
        Check("hair_1 then fits", kept.FitsIn(2048, 512));

        // The ordinary case is untouched -- this must not move the 1300-odd parts that already fit.
        foreach ((int x, int y, int w, int h) in new[]
                 {
                     (0, 0, 2048, 512), (1008, 159, 182, 150), (1692, 252, 153, 182), (1, 1, 1, 1),
                 })
        {
            var inside = new PixelRect(x, y, w, h);
            Check($"a rectangle already inside the page is unchanged ({w}x{h} at {x},{y})",
                  inside.ClampedTo(2048, 512).Equals(inside));
        }

        // Hanging off any edge, including a negative origin, keeps whatever is really there.
        Check("a rectangle off the bottom is trimmed",
              new PixelRect(10, 500, 20, 40).ClampedTo(2048, 512).Equals(new PixelRect(10, 500, 20, 12)));
        Check("a rectangle off the left is trimmed",
              new PixelRect(-5, 10, 20, 20).ClampedTo(2048, 512).Equals(new PixelRect(0, 10, 15, 20)));
        Check("a rectangle entirely outside the page is empty",
              new PixelRect(3000, 10, 20, 20).ClampedTo(2048, 512).IsEmpty);
        Check("a rectangle entirely above the page is empty",
              new PixelRect(10, -50, 20, 20).ClampedTo(2048, 512).IsEmpty);
        Check("an empty rectangle stays empty",
              new PixelRect(10, 10, 0, 0).ClampedTo(2048, 512).IsEmpty);
    }

    /// <summary>
    /// Images\Other: the pictures that belong to no costume, matched back by the texture's own name.
    /// </summary>
    private static void OtherPictureRules()
    {
        // The dump turns a texture name into a file name by replacing what Windows forbids. Matching
        // applies the same rule to the live name -- these are the player's actual files.
        foreach ((string textureName, string stem) in new[]
                 {
                     ("Headset", "Headset"),
                     ("Font Texture", "Font Texture"),
                     ("UnityWhite", "UnityWhite"),
                     ("SprHeadsetTip", "SprHeadsetTip"),
                     ("1_muscle_ui", "1_muscle_ui"),
                     ("UI/Thing", "UI_Thing"),
                     ("a:b*c?d", "a_b_c_d"),
                 })
        {
            Check($"\"{textureName}\" is dumped as \"{stem}.png\"",
                  OtherPictures.FileStemOf(textureName) == stem, OtherPictures.FileStemOf(textureName));
            var picture = new OtherPicture(stem, stem + ".png", 0, 0, "UI");
            Check($"\"{textureName}\" matches its own file", OtherPictures.Matches(picture, textureName));
        }

        Check("a texture with no name matches nothing",
              !OtherPictures.Matches(new OtherPicture("Headset", "Headset.png", 0, 0, ""), null) &&
              !OtherPictures.Matches(new OtherPicture("Headset", "Headset.png", 0, 0, ""), ""));
        Check("a different texture does not match",
              !OtherPictures.Matches(new OtherPicture("Headset", "Headset.png", 0, 0, ""), "Headset2"));

        // The sidecar the dump writes, as the player's own Headset.json has it.
        byte[] sidecar = System.Text.Encoding.UTF8.GetBytes(
            "{ \"version\": 2, \"origin\": \"top-left\", \"anchorX\": 335.0, \"anchorY\": 300.0, " +
            "\"dumpedWidth\": 670, \"dumpedHeight\": 600, \"source\": \"loaded by name\", \"kind\": \"Spine\" }");
        OtherPicture read = OtherPictures.Describe("Headset.png", sidecar);
        Check("the sidecar gives the size it was dumped at",
              read.Stem == "Headset" && read.DumpedWidth == 670 && read.DumpedHeight == 600 && read.Kind == "Spine",
              $"{read.Stem} {read.DumpedWidth}x{read.DumpedHeight} {read.Kind}");
        OtherPicture none = OtherPictures.Describe("Headset.png", null);
        Check("a picture with no sidecar still counts",
              none.Stem == "Headset" && none.DumpedWidth == 0);
        OtherPicture broken = OtherPictures.Describe("Headset.png",
            System.Text.Encoding.UTF8.GetBytes("{ not json at all"));
        Check("a hand-broken sidecar does not cost the author their picture",
              broken.Stem == "Headset" && broken.DumpedWidth == 0);

        // THE invariant: edited at the size it was dumped, nothing moves by so much as a texel.
        foreach ((int w, int h) in new[] { (670, 600), (566, 311), (160, 160), (4096, 4096), (1, 1) })
        {
            var whole = new PixelRect(0, 0, w, h);
            Check($"a {w}x{h} picture edited at its own size keeps its rectangle exactly",
                  OtherPictures.SpriteRect(whole, w, h, w, h).Equals(whole));
            Check($"a {w}x{h} picture edited at its own size keeps its pixels-per-unit",
                  Math.Abs(OtherPictures.PixelsPerUnit(100f, w, w) - 100f) < 1e-6f);
        }

        // A sprite that is a sub-rectangle of a sheet, on a picture the author drew at twice the size:
        // the rectangle doubles with it, so the same part of the drawing is shown.
        PixelRect part = OtherPictures.SpriteRect(new PixelRect(10, 20, 30, 40), 100, 200, 200, 400);
        Check("a sub-rectangle grows with the picture",
              part.Equals(new PixelRect(20, 40, 60, 80)), $"{part.Width}x{part.Height} at {part.X},{part.Y}");
        Check("and the pixels-per-unit grows with it, so the size on screen does not change",
              Math.Abs(OtherPictures.PixelsPerUnit(100f, 100, 200) - 200f) < 1e-4f);

        // Never off the texture, never nothing, never a crash on nonsense.
        PixelRect clamped = OtherPictures.SpriteRect(new PixelRect(90, 90, 20, 20), 100, 100, 50, 50);
        Check("a rectangle stays inside the new picture",
              clamped.X >= 0 && clamped.Y >= 0 && clamped.Right <= 50 && clamped.Bottom <= 50,
              $"{clamped.Width}x{clamped.Height} at {clamped.X},{clamped.Y}");
        Check("a rectangle never collapses to nothing", clamped.Width >= 1 && clamped.Height >= 1);
        foreach ((int fw, int fh, int tw, int th) in new[] { (0, 10, 10, 10), (10, 0, 10, 10), (10, 10, 0, 10), (10, 10, 10, 0) })
            Check("a nonsense size leaves the rectangle alone",
                  OtherPictures.SpriteRect(new PixelRect(1, 2, 3, 4), fw, fh, tw, th)
                      .Equals(new PixelRect(1, 2, 3, 4)));
        Check("a nonsense pixels-per-unit is left alone",
              Math.Abs(OtherPictures.PixelsPerUnit(0f, 10, 20)) < 1e-6f);

        // Censored pictures are never read back, the same rule the Dump and HexieBlock use.
        Check("a censored name is refused", DumpNaming.IsCensoredName("hexie_thing"));
        Check("an ordinary name is not", !DumpNaming.IsCensoredName("Headset"));
    }

    private static void CanvasMatchRules()
    {
        // A recognisable little drawing: a filled blob with a bright corner, so it matches in one place
        // only. Built once and pasted at a known position inside a bigger, otherwise empty canvas.
        static byte[] Drawing(int w, int h, int seed)
        {
            byte[] image = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int at = (y * w + x) * 4;
                bool inside = x > w / 6 && x < w - w / 6 && y > h / 6 && y < h - h / 6;
                bool mark = x < w / 5 && y < h / 5;
                image[at] = (byte)(inside ? (x * 7 + seed) % 256 : 0);
                image[at + 1] = (byte)(inside ? (y * 5 + seed) % 256 : 0);
                image[at + 2] = (byte)(mark ? 255 : 0);
                image[at + 3] = (byte)(inside || mark ? 255 : 0);
            }
            return image;
        }

        static byte[] Paste(byte[] drawing, int dw, int dh, int cw, int ch, int left, int top)
        {
            byte[] canvas = new byte[cw * ch * 4];
            for (int y = 0; y < dh; y++)
            for (int x = 0; x < dw; x++)
            {
                int from = (y * dw + x) * 4, into = ((top + y) * cw + left + x) * 4;
                canvas[into] = drawing[from];
                canvas[into + 1] = drawing[from + 1];
                canvas[into + 2] = drawing[from + 2];
                canvas[into + 3] = drawing[from + 3];
            }
            return canvas;
        }

        // The player's own enlarged parts, at their real sizes, with the drawing put in each corner and
        // in the middle. Every one has to come back exactly, and be believed.
        foreach ((string part, int gw, int gh, int pw, int ph) in new[]
                 {
                     ("head",    115, 138, 138, 143),
                     ("wing_l",  110,  81, 130,  90),
                     ("fork",    191,  94, 208, 121),
                     ("leg_r_a",  13,  33,  14,  33),
                     ("body",     31,  54,  40,  54),
                     ("wing_r",  125,  89, 130,  90),
                 })
        {
            byte[] drawing = Drawing(gw, gh, part.Length * 13);
            foreach ((int left, int top) in new[]
                     {
                         (0, 0), (pw - gw, ph - gh), (0, ph - gh), (pw - gw, 0),
                         ((pw - gw) / 2, (ph - gh) / 2),
                     })
            {
                byte[] picture = Paste(drawing, gw, gh, pw, ph, left, top);
                CanvasMatch.Found found = CanvasMatch.Locate(drawing, gw, gh, picture, pw, ph);
                Check($"{part}: the drawing is found at ({left}, {top})",
                      found.Confident && found.Left == left && found.Top == top,
                      $"got ({found.Left}, {found.Top}) confident={found.Confident} score={found.Score:0.####}");
            }
        }

        // Recoloured, same silhouette: the player redraws a part, they do not re-cut it. Alpha counts
        // double for exactly this reason, so the position still comes back.
        {
            const int gw = 110, gh = 81, pw = 130, ph = 90;
            byte[] drawing = Drawing(gw, gh, 1);
            byte[] recoloured = (byte[])drawing.Clone();
            for (int at = 0; at < recoloured.Length; at += 4)
                if (recoloured[at + 3] != 0) { recoloured[at] = 200; recoloured[at + 1] = 30; recoloured[at + 2] = 40; }
            byte[] picture = Paste(recoloured, gw, gh, pw, ph, 20, 9);
            CanvasMatch.Found found = CanvasMatch.Locate(drawing, gw, gh, picture, pw, ph);
            Check("a recoloured part is still found by its silhouette",
                  found.Confident && found.Left == 20 && found.Top == 9,
                  $"({found.Left}, {found.Top}) confident={found.Confident}");
        }

        // A REDRAWN part: same part of the same character, drawn completely differently, so there is
        // nothing to find. Lining the two drawings up by their ink is what a person does by eye, and it
        // is strictly better informed than centring, which lines up the canvases and ignores the drawing.
        {
            const int gw = 115, gh = 138, pw = 138, ph = 143;

            // The game's drawing: ink from (10,20) to (104,117), so its middle is at (57, 68.5).
            byte[] game = new byte[gw * gh * 4];
            for (int y = 20; y <= 117; y++)
            for (int x = 10; x <= 104; x++)
                game[(y * gw + x) * 4 + 3] = 255;

            // The author's: a different shape entirely, ink from (4,2) to (120,125), middle (62, 63.5).
            byte[] drawn = new byte[pw * ph * 4];
            for (int y = 2; y <= 125; y++)
            for (int x = 4; x <= 120; x++)
            {
                int at = (y * pw + x) * 4;
                drawn[at] = 220; drawn[at + 1] = 20; drawn[at + 2] = 20; drawn[at + 3] = 255;
            }

            CanvasMatch.Found found = CanvasMatch.Locate(game, gw, gh, drawn, pw, ph);
            Check("a redrawn part is NOT claimed as found", !found.Confident);

            CanvasMatch.Found ink = CanvasMatch.AlignByInk(game, gw, gh, drawn, pw, ph);
            // 62 - 57 = 5 across, 63.5 - 68.5 = -5 down, clamped into the room that exists (0..23, 0..5).
            Check("a redrawn part is lined up by its ink",
                  ink.Confident && ink.Left == 5 && ink.Top == 0,
                  $"({ink.Left}, {ink.Top}) confident={ink.Confident}");
            Check("lining up by ink is not the same as centring",
                  ink.Left != (pw - gw) / 2);

            // And it never leaves the picture, whatever the drawings say.
            Check("lining up stays inside the picture",
                  ink.Left >= 0 && ink.Top >= 0 && ink.Left <= pw - gw && ink.Top <= ph - gh);
        }

        // No ink anywhere: there is nothing to line up, so centring is the honest answer.
        {
            CanvasMatch.Found ink = CanvasMatch.AlignByInk(new byte[40 * 40 * 4], 40, 40,
                new byte[80 * 80 * 4], 80, 80);
            Check("a picture with no ink is centred, not lined up",
                  !ink.Confident && ink.Left == 20 && ink.Top == 20);
        }

        // Two drawings that fill their canvases line up exactly where centring would -- the agreement is
        // what shows the rule is not doing anything strange in the ordinary case.
        {
            const int gw = 60, gh = 40, pw = 80, ph = 60;
            byte[] game = new byte[gw * gh * 4];
            for (int at = 3; at < game.Length; at += 4) game[at] = 255;
            byte[] drawn = new byte[pw * ph * 4];
            for (int at = 3; at < drawn.Length; at += 4) drawn[at] = 255;
            CanvasMatch.Found ink = CanvasMatch.AlignByInk(game, gw, gh, drawn, pw, ph);
            Check("two full canvases line up centred",
                  ink.Left == (pw - gw) / 2 && ink.Top == (ph - gh) / 2,
                  $"({ink.Left}, {ink.Top})");
        }

        // Nothing to go on: an empty picture matches everywhere equally, so the honest answer is centred
        // and NOT confident. This is the guard that stops a flat part being flung somewhere arbitrary.
        {
            const int gw = 40, gh = 40, pw = 80, ph = 80;
            CanvasMatch.Found found = CanvasMatch.Locate(new byte[gw * gh * 4], gw, gh,
                new byte[pw * ph * 4], pw, ph);
            Check("an empty picture is centred, not guessed",
                  !found.Confident && found.Left == 20 && found.Top == 20);
        }

        // No room to move: one position, and it is the answer.
        {
            byte[] drawing = Drawing(32, 24, 3);
            CanvasMatch.Found found = CanvasMatch.Locate(drawing, 32, 24, drawing, 32, 24);
            Check("a picture the size of the canvas starts at the corner",
                  found.Confident && found.Left == 0 && found.Top == 0);
        }

        // Nonsense in, centred out -- never an exception and never an out-of-range answer.
        {
            CanvasMatch.Found small = CanvasMatch.Locate(new byte[16], 2, 2, new byte[4], 1, 1);
            Check("a picture smaller than the canvas is centred", !small.Confident);
            CanvasMatch.Found empty = CanvasMatch.Locate(Array.Empty<byte>(), 0, 0, new byte[64], 4, 4);
            Check("a canvas of no size is centred", !empty.Confident);
        }
    }

    private static void Check(string what, bool ok, string? detail = null)
    {
        if (ok) { _passed++; return; }
        _failed++;
        Console.WriteLine($"  FAIL  {what}{(detail == null ? "" : "  -- " + detail)}");
    }

    private static int Main(string[] args)
    {
        Console.WriteLine("SkinForger authored Main - pure layer checks");

        PixelRectRules();
        AlphaBoundsRules();
        PremultiplyRules();
        CropAndBlitRules();
        FlipRules();
        RotationRules();
        SelectionRules();
        LayoutRules();
        ManifestRules();
        AnchorRules();
        DumpNamingRules();
        GeometryRules();
        DumpPlanRules();
        AnchorFormulaFixtures();
        FpsLimiterRules();
        RosterRules();
        WindowModelRules();
        PrefabIndexRules();
        AtlasComposerRules();
        SkinIndexRules();
        MenuKeyRules();
        DumpComparisonRules();

        PageSpellingRules();
        ReplacedRegionRules();
        ElfinCanvasPlacementRules();
        MeshWindowRules();
        SharedRegionRules();
        SkinFileRules();
        CanvasMatchRules();
        OtherPictureRules();
        OverflowingRegionRules();

        string? images = args.Length > 0 ? args[0] : null;
        if (images != null && Directory.Exists(images)) RealFiles(images);
        else if (images != null) Console.WriteLine($"  (skipped real files: {images} not found)");

        // The game root is two folders above SkinCreator\Images; with it, the checks that need the
        // player's own installation run too.
        string? gameRoot = images == null ? null
            : Path.GetFullPath(Path.Combine(images, "..", "..", ".."));
        if (gameRoot != null && Directory.Exists(gameRoot)) LoadingPictureRules(gameRoot);

        Console.WriteLine($"\n{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- rules

    private static void PixelRectRules()
    {
        var rect = new PixelRect(10, 20, 30, 40);
        Check("PixelRect right/bottom", rect.Right == 40 && rect.Bottom == 60);
        Check("PixelRect fits inside", rect.FitsIn(40, 60) && !rect.FitsIn(39, 60));
        Check("PixelRect negative size clamps", new PixelRect(0, 0, -5, 10).IsEmpty);
        Check("PixelRect transposed swaps", rect.Transposed.Width == 40 && rect.Transposed.Height == 30);
        Check("PixelRect overlap", rect.Overlaps(new PixelRect(39, 59, 2, 2)) && !rect.Overlaps(new PixelRect(40, 60, 2, 2)));
        Check("PixelRect contains", rect.Contains(new PixelRect(10, 20, 1, 1)) && !rect.Contains(new PixelRect(9, 20, 1, 1)));
    }

    private static void AlphaBoundsRules()
    {
        byte[] pixels = PixelMath.Transparent(8, 8);
        Check("alpha bounds of empty image", PixelMath.AlphaBounds(pixels, 8, 8).IsEmpty);

        Set(pixels, 8, 3, 5, 10, 20, 30, 200);
        Set(pixels, 8, 6, 2, 10, 20, 30, 1);
        PixelRect bounds = PixelMath.AlphaBounds(pixels, 8, 8);
        Check("alpha bounds covers both pixels", bounds == new PixelRect(3, 2, 4, 4), bounds.ToString());
        Check("alpha threshold drops faint pixel", PixelMath.AlphaBounds(pixels, 8, 8, 1) == new PixelRect(3, 5, 1, 1));
    }

    private static void PremultiplyRules()
    {
        byte[] pixels = { 128, 64, 32, 128 };
        PixelMath.UnpremultiplyInPlace(pixels, 1, 1);
        Check("unpremultiply scales colour up", pixels[0] == 255 && pixels[1] == 128 && pixels[2] == 64, string.Join(",", pixels));

        byte[] opaque = { 10, 20, 30, 255 };
        PixelMath.UnpremultiplyInPlace(opaque, 1, 1);
        Check("unpremultiply leaves opaque alone", opaque[0] == 10 && opaque[1] == 20 && opaque[2] == 30);

        byte[] clear = { 77, 88, 99, 0 };
        PixelMath.UnpremultiplyInPlace(clear, 1, 1);
        Check("unpremultiply leaves transparent alone", clear[0] == 77);

        // Valid premultiplied input only: a channel can never exceed alpha (colour was already multiplied by it).
        // 200 with alpha 128 is impossible, and un-premultiplying it clamps at 255 — information that cannot return.
        byte[] roundTrip = { 100, 64, 32, 128 };
        PixelMath.UnpremultiplyInPlace(roundTrip, 1, 1);
        PixelMath.PremultiplyInPlace(roundTrip, 1, 1);
        Check("premultiply round-trip within 1", Math.Abs(roundTrip[0] - 100) <= 1 && Math.Abs(roundTrip[1] - 64) <= 1,
              string.Join(",", roundTrip));

        byte[] invalid = { 200, 0, 0, 128 };   // colour above alpha: not a real PMA pixel
        PixelMath.UnpremultiplyInPlace(invalid, 1, 1);
        Check("impossible PMA pixel clamps instead of overflowing", invalid[0] == 255);
    }

    private static void CropAndBlitRules()
    {
        byte[] source = PixelMath.Transparent(4, 4);
        Set(source, 4, 1, 1, 9, 9, 9, 255);

        byte[] exact = PixelMath.Crop(source, 4, 4, new PixelRect(1, 1, 2, 2));
        Check("crop keeps the pixel", exact[3] == 255 && exact.Length == PixelMath.BufferLength(2, 2));

        byte[] bigger = PixelMath.Crop(source, 4, 4, new PixelRect(-2, -2, 8, 8));
        Check("crop larger than source pads transparent", bigger.Length == PixelMath.BufferLength(8, 8) && At(bigger, 8, 3, 3, 3) == 255,
              "pixel should land at 3,3");

        byte[] target = PixelMath.Transparent(6, 6);
        PixelMath.Blit(target, 6, 6, source, 4, 4, 2, 2);
        Check("blit places the pixel", At(target, 6, 3, 3, 3) == 255);
        PixelMath.Blit(target, 6, 6, source, 4, 4, 5, 5);   // mostly outside
        Check("blit clips without throwing", true);
    }

    private static void FlipRules()
    {
        byte[] pixels = PixelMath.Transparent(3, 4);
        Set(pixels, 3, 0, 0, 1, 2, 3, 255);     // top-left
        Set(pixels, 3, 2, 3, 9, 9, 9, 128);     // bottom-right

        byte[] flipped = PixelMath.FlipRows(pixels, 3, 4);
        Check("flip moves the top row to the bottom", At(flipped, 3, 0, 3, 3) == 255);
        Check("flip moves the bottom row to the top", At(flipped, 3, 2, 0, 3) == 128);
        Check("flip twice is identity", PixelMath.Same(PixelMath.FlipRows(flipped, 3, 4), pixels));
        Check("flip keeps the length", flipped.Length == pixels.Length);
        Check("flip of one row is identity", PixelMath.Same(PixelMath.FlipRows(pixels[..(3 * PixelMath.BytesPerPixel)], 3, 1),
                                                            pixels[..(3 * PixelMath.BytesPerPixel)]));
    }

    private static void RotationRules()
    {
        byte[] source = PixelMath.Transparent(3, 2);
        Set(source, 3, 2, 0, 1, 2, 3, 255);   // top-right corner

        byte[] zero = PixelMath.RotateClockwise(source, 3, 2, 0, out int w0, out int h0);
        Check("0 degrees keeps size and content", w0 == 3 && h0 == 2 && PixelMath.Same(zero, source));

        byte[] ninety = PixelMath.RotateClockwise(source, 3, 2, 90, out int w90, out int h90);
        Check("90 degrees swaps size", w90 == 2 && h90 == 3);
        Check("90 degrees moves top-right to bottom-right", At(ninety, 2, 1, 2, 3) == 255);

        byte[] back = PixelMath.RotateClockwise(ninety, w90, h90, 270, out int wb, out int hb);
        Check("90 then 270 is identity", wb == 3 && hb == 2 && PixelMath.Same(back, source));

        byte[] oneEighty = PixelMath.RotateClockwise(source, 3, 2, 180, out _, out _);
        Check("180 degrees moves to bottom-left", At(oneEighty, 3, 0, 1, 3) == 255);

        bool threw = false;
        try { PixelMath.RotateClockwise(source, 3, 2, 45, out _, out _); } catch (ArgumentOutOfRangeException) { threw = true; }
        Check("45 degrees is refused", threw);
    }

    private static void SelectionRules()
    {
        string folder = Path.Combine(Path.GetTempPath(), "sf-main-tests-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var layout = new MainLayout(folder);
            Directory.CreateDirectory(layout.CreatorRoot);
            var store = new SelectionStore(layout);
            store.Load();
            Check("unknown index is Original", store.Get(3, false) == SelectionStore.Original);

            store.Set(3, 2, false);
            store.Set(1, 5, true);
            var reloaded = new SelectionStore(layout);
            reloaded.Load();
            Check("character selection survives a reload", reloaded.Get(3, false) == 2);
            Check("elfin selection survives a reload", reloaded.Get(1, true) == 5);
            Check("character and elfin are separate files", reloaded.Get(3, true) == SelectionStore.Original);
            Check("replaced lists the chosen index", reloaded.Replaced(false).SequenceEqual(new[] { 3 }));

            File.WriteAllText(layout.SavedCharactersFile, "{ this is not json");
            var damaged = new SelectionStore(layout);
            damaged.Load();
            Check("damaged Saved.json loads as no selection", damaged.Get(3, false) == SelectionStore.Original);

            damaged.Set(7, 1, false);
            string written = File.ReadAllText(layout.SavedCharactersFile);
            Check("keys are written as strings", written.Contains("\"7\""), written);

            reloaded.Clear();
            var cleared = new SelectionStore(layout);
            cleared.Load();
            Check("clear puts everything back to Original", cleared.Get(3, false) == SelectionStore.Original);
        }
        finally { try { Directory.Delete(folder, true); } catch { /* temp folder */ } }
    }

    private static void LayoutRules()
    {
        var layout = new MainLayout(@"C:\Game");
        Check("images folder", layout.Images == @"C:\Game\SkinForger\SkinCreator\Images", layout.Images);
        Check("player skins folder", layout.Player == @"C:\Game\SkinForger\Skins", layout.Player);
        Check("manifest path", layout.ManifestOf("Bassist") == @"C:\Game\SkinForger\SkinCreator\Images\Bassist\manifest.json");
        Check("settings stay in SkinCreator", layout.SettingsPath.EndsWith(@"SkinCreator\Settings.json"));
        Check("reserved: library mount", MainLayout.IsReservedFolderName("sfpkg-library"));
        Check("reserved: replaced", MainLayout.IsReservedFolderName("replaced"));
        Check("reserved: anything with atlas/cache/backup", MainLayout.IsReservedFolderName("my-atlas-cache"));
        Check("not reserved: a real costume", !MainLayout.IsReservedFolderName("Bassist"));
        Check("safe file name strips invalid characters", MainLayout.SafeFileName("hair:1/2") == "hair_1_2", MainLayout.SafeFileName("hair:1/2"));
        Check("safe file name of nothing", MainLayout.SafeFileName("   ") == "unnamed");
    }

    private static void ManifestRules()
    {
        var manifest = new RegionManifest();
        manifest.Put(new RegionManifestEntry
        {
            File = @"1_rock_main_show\hand_left.png", Atlas = "1_rock_main_show",
            AttachmentName = "hand_left", RegionName = "hand_left", DisplayName = "hand_left",
            X = 189, Y = 422, Width = 160, Height = 66, Rotate = true, Degrees = 90,
            OriginalWidth = 160, OriginalHeight = 66, U = 0.36914062f, V = 0.7939453f, U2 = 0.49804688f, V2 = 0.7158203f
        });
        Check("found by file with either separator", manifest.ByFile("1_rock_main_show/hand_left.png") != null);
        Check("found by part identity", manifest.ByPart("1_rock_main_show", "hand_left", "hand_left") != null);
        Check("part identity format", manifest.Regions[0].Identity == "1_rock_main_show|hand_left|hand_left");
        Check("packed rect from entry", manifest.Regions[0].PackedRect == new PixelRect(189, 422, 160, 66));

        manifest.Put(new RegionManifestEntry { File = @"1_rock_main_show\hand_left.png", Atlas = "1_rock_main_show", Width = 1 });
        Check("re-dump replaces instead of duplicating", manifest.Regions.Count == 1 && manifest.Regions[0].Width == 1);

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true });
        string text = System.Text.Encoding.UTF8.GetString(json);
        Check("keys are camelCase", text.Contains("\"attachmentName\"") && text.Contains("\"originalWidth\"") && text.Contains("\"regions\""), text[..Math.Min(120, text.Length)]);
        Check("no PascalCase keys leaked", !text.Contains("\"AttachmentName\""));
        RegionManifest again = RegionManifest.Parse(json);
        Check("manifest round-trip keeps the entry", again.Regions.Count == 1 && again.Regions[0].Atlas == "1_rock_main_show");

        RegionManifest withUnknown = RegionManifest.Parse(System.Text.Encoding.UTF8.GetBytes(
            "{\"version\":1,\"futureField\":7,\"regions\":[{\"file\":\"a.png\",\"somethingNew\":true}]}"));
        Check("unknown members are preserved", withUnknown.Extra != null && withUnknown.Regions[0].Extra != null);
    }

    private static void AnchorRules()
    {
        string folder = Path.Combine(Path.GetTempPath(), "sf-anchor-tests-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(folder);
            string image = Path.Combine(folder, "bady.png");
            Check("sidecar path", RegionAnchorFile.PathOf(image).EndsWith("bady.json"));
            Check("missing sidecar reads as null", RegionAnchorFile.ReadIfExists(image) == null);

            RegionAnchorFile anchor = RegionAnchorFile.ForDump(660f, 691.5f, 0f);
            anchor.Write(image);
            string text = File.ReadAllText(RegionAnchorFile.PathOf(image));
            Check("v2 core fields are camelCase", text.Contains("\"anchorX\"") && text.Contains("\"origin\""), text);
            Check("later fields stay PascalCase", text.Contains("\"Scale\"") && text.Contains("\"MovementAmplitude\"") && text.Contains("\"Rotate\""), text);

            RegionAnchorFile? read = RegionAnchorFile.ReadIfExists(image);
            Check("sidecar round-trip", read != null && read.AnchorY == 691.5f && read.IsTopLeft && read.Scale == 1f);
            Check("untouched when nothing was changed", read!.IsUntouched(660f, 691.5f));
            read.X = 3f;
            Check("touched once a control moved", !read.IsUntouched(660f, 691.5f));
            read.X = 0f;
            read.AnchorX = 700f;
            Check("touched once the anchor was hand-edited", !read.IsUntouched(660f, 691.5f));

            File.WriteAllText(RegionAnchorFile.PathOf(image), "{ broken");
            Check("broken sidecar reads as null, not an exception", RegionAnchorFile.ReadIfExists(image) == null);
        }
        finally { try { Directory.Delete(folder, true); } catch { /* temp folder */ } }
    }

    private static void DumpNamingRules()
    {
        Check("display name is the tail of the attachment name", DumpNaming.DisplayNameOf("1_images_rock/body") == "body");
        Check("display name of a plain name", DumpNaming.DisplayNameOf("leg_L_2_4") == "leg_L_2_4");
        Check("display name is made file-safe", DumpNaming.DisplayNameOf("a/b:c") == "b_c");

        var naming = new DumpNaming();
        Check("first use has no suffix", naming.NextFileName("char_1_rock_fail", "1_images_rock/hair_B") == "hair_B.png");
        Check("second use of the same name gets _2", naming.NextFileName("char_1_rock_fail", "1_images_bunny/hair_B") == "hair_B_2.png");
        Check("third use gets _3", naming.NextFileName("char_1_rock_fail", "1_images_other/hair_B") == "hair_B_3.png");
        Check("another atlas folder numbers on its own", naming.NextFileName("char_1_rock", "1_images_rock/hair_B") == "hair_B.png");
        naming.Reset();
        Check("reset starts the numbering again", naming.NextFileName("char_1_rock_fail", "1_images_rock/hair_B") == "hair_B.png");

        Check("manifest file uses a backslash", DumpNaming.ManifestFile("char_1_rock", "hair_B.png") == @"char_1_rock\hair_B.png");

        RegionManifestEntry entry = DumpNaming.Entry("char_1_rock_fail", "hair_B_2.png", "1_images_bunny/hair_B",
            "1_images_bunny/hair_B", new PixelRect(5, 6, 7, 8), true, 90, 7, 8, 0f, 0f, 0.1f, 0.2f, 0.3f, 0.4f);
        Check("entry file matches the chosen name", entry.File == @"char_1_rock_fail\hair_B_2.png");
        Check("entry display name has no suffix", entry.DisplayName == "hair_B");
        Check("entry keeps the full attachment name", entry.AttachmentName == "1_images_bunny/hair_B");
        Check("entry identity is the part triple", entry.Identity == "char_1_rock_fail|1_images_bunny/hair_B|1_images_bunny/hair_B");

        Check("part image accepted", DumpNaming.IsPartImage(@"char_1_rock\hair_B.png"));
        Check("replaced backup is not a part", !DumpNaming.IsPartImage(@"replaced\hair_B.png"));
        Check("thumbnail is not a part", !DumpNaming.IsPartImage(@"SkinSwitchIcons\rock_girl_skin_thumb.png"));
        Check("censored name is not a part", !DumpNaming.IsPartImage(@"char_1_bunny\body_1_hexie.png"));
        Check("nested path is not a part", !DumpNaming.IsPartImage(@"char_1_rock\sub\hair_B.png"));
        Check("loose file is not a part", !DumpNaming.IsPartImage("manifest.json"));
    }

    private static void GeometryRules()
    {
        Check("rotate flag belongs to 90 only", RegionGeometry.RotateFlagFor(90) && !RegionGeometry.RotateFlagFor(270) &&
                                                !RegionGeometry.RotateFlagFor(180) && !RegionGeometry.RotateFlagFor(0));
        Check("degrees normalise", RegionGeometry.Normalize(-90) == 270 && RegionGeometry.Normalize(450) == 90);
        // A frame-by-frame folder keeps one anchors.json; its name is the only marker the game gives us.
        Check("cat atlas by name",
              DumpNaming.IsFrameByFrameAtlas("1_rock_main_show_cat") &&
              DumpNaming.IsFrameByFrameAtlas(Path.Combine("C:", "x", "Images", "Bassist", "char_1_rock_cat")) &&
              DumpNaming.IsFrameByFrameAtlas(Path.Combine("C:", "x", "char_3_maid_cat2") + Path.DirectorySeparatorChar) &&
              !DumpNaming.IsFrameByFrameAtlas("1_rock_main_show") &&
              !DumpNaming.IsFrameByFrameAtlas("Bassist"));

        // The real Bassist hand_left entry: 512x2048 page, stored turned 90 degrees.
        PixelRect stored = RegionGeometry.StoredRect(0.36914062f, 0.7939453f, 0.49804688f, 0.7158203f, 512, 2048);
        Check("stored rect x/y are top-left atlas pixels", stored.X == 189 && stored.Y == 422, stored.ToString());
        Check("stored rect spans the UV rectangle", stored.Width == 66 && stored.Height == 160, stored.ToString());
        (int width, int height) = RegionGeometry.LogicalSize(stored, rotate: true);
        Check("a turned region reports the transposed size", width == 160 && height == 66);
        Check("an upright region reports the span", RegionGeometry.LogicalSize(stored, rotate: false) == (66, 160));

        // The real Bassist hand_left_thumb_4 entry: upright.
        PixelRect upright = RegionGeometry.StoredRect(0.5253906f, 0.2705078f, 0.5683594f, 0.2626953f, 512, 2048);
        Check("upright entry reproduces x/y", upright.X == 269 && upright.Y == 1494, upright.ToString());
        Check("upright entry reproduces size", upright.Width == 22 && upright.Height == 16);

        // Trim: offsetY is measured from the bottom (118 of 119 real trimmed PNGs fit this, 84 fit the other reading).
        PixelRect place = RegionGeometry.TrimPlacement(0f, 1f, 100, 95, 100);
        Check("trim placement counts offsetY from the bottom", place.X == 0 && place.Y == 4, place.ToString());
        PixelRect none = RegionGeometry.TrimPlacement(0f, 0f, 10, 10, 10);
        Check("an untrimmed part sits at the origin", none.X == 0 && none.Y == 0);

        Check("default anchor is the canvas centre", RegionGeometry.DefaultAnchor(195, 161) == (97.5f, 80.5f));

        // Building a part image: 4x4 atlas, a 2x2 region at (1,1), untrimmed.
        byte[] atlas = PixelMath.Transparent(4, 4);
        Set(atlas, 4, 1, 1, 200, 0, 0, 255);
        Set(atlas, 4, 2, 2, 0, 200, 0, 255);
        byte[] image = RegionGeometry.BuildPartImage(atlas, 4, 4, new PixelRect(1, 1, 2, 2), 0, false, 0f, 0f, 2, 2,
            out int imageWidth, out int imageHeight);
        Check("untrimmed part keeps the region size", imageWidth == 2 && imageHeight == 2);
        Check("untrimmed part keeps the pixels", At(image, 2, 0, 0, 0) == 200 && At(image, 2, 1, 1, 1) == 200);

        // Trimmed: the same 2x2 drawing on a 4x4 canvas, offset (1,1) from the bottom-left.
        byte[] trimmed = RegionGeometry.BuildPartImage(atlas, 4, 4, new PixelRect(1, 1, 2, 2), 0, false, 1f, 1f, 4, 4,
            out int trimmedWidth, out int trimmedHeight);
        Check("trimmed part is written on the untrimmed canvas", trimmedWidth == 4 && trimmedHeight == 4);
        Check("trimmed drawing lands at (offsetX, height - offsetY - h)", At(trimmed, 4, 1, 1, 0) == 200,
              "expected the red pixel at 1,1");

        // Turned by 90: the image comes back upright and transposed.
        byte[] turned = RegionGeometry.BuildPartImage(atlas, 4, 4, new PixelRect(1, 1, 1, 2), 90, true, 0f, 0f, 2, 1,
            out int turnedWidth, out int turnedHeight);
        Check("a turned region is written upright", turnedWidth == 2 && turnedHeight == 1, $"{turnedWidth}x{turnedHeight}");
        Check("turned image has the pixels", turned.Length == PixelMath.BufferLength(2, 1));
    }

    /// <summary>
    /// A synthetic atlas with known parts, planned end to end: names, manifest entries, images and the
    /// reasons a part is left out. No game, no PNG encoder.
    /// </summary>
    private static void DumpPlanRules()
    {
        const int atlasWidth = 64, atlasHeight = 32;
        byte[] atlas = PixelMath.Transparent(atlasWidth, atlasHeight);
        // Fill two rectangles with recognisable colours (premultiplied: opaque, so values survive untouched).
        Fill(atlas, atlasWidth, new PixelRect(4, 4, 8, 6), 10, 20, 30, 255);
        Fill(atlas, atlasWidth, new PixelRect(20, 10, 3, 9), 40, 50, 60, 255);

        PartSpec Upright() => Spec("char_test", "body", "body", new PixelRect(4, 4, 8, 6), false, 0, 8, 6, 0f, 0f);
        PartSpec Turned() => Spec("char_test", "arm", "arm", new PixelRect(20, 10, 3, 9), true, 90, 9, 3, 0f, 0f);
        PartSpec Trimmed() => Spec("char_test", "hat", "hat", new PixelRect(4, 4, 8, 6), false, 0, 12, 10, 2f, 1f);

        var skips = new List<string>();
        List<PlannedPart> planned = DumpPlan.ForAtlas(
            new[] { Upright(), Turned(), Trimmed() }, atlas, atlasWidth, atlasHeight,
            skipped: (part, reason) => skips.Add(part.AttachmentName + ": " + reason));
        Check("plan keeps every usable part", planned.Count == 3 && skips.Count == 0, string.Join("; ", skips));

        PlannedPart body = planned[0];
        Check("plan names the file after the part", body.FileName == "body.png");
        Check("plan path is <atlas>\\<file>", body.RelativePath == @"char_test\body.png");
        Check("upright image is the region size", body.Width == 8 && body.Height == 6);
        Check("upright image carries the pixels", At(body.Image, 8, 0, 0, 0) == 10 && At(body.Image, 8, 7, 5, 2) == 30);
        Check("manifest entry keeps the rectangle", body.Entry.PackedRect == new PixelRect(4, 4, 8, 6));
        Check("manifest entry keeps the identity", body.Entry.Identity == "char_test|body|body");
        Check("anchor defaults to the canvas centre", body.Anchor.AnchorX == 4f && body.Anchor.AnchorY == 3f);
        Check("anchor is written as a top-left v2 sidecar", body.Anchor.IsTopLeft && body.Anchor.Version == 2);

        PlannedPart arm = planned[1];
        Check("a turned part is written upright", arm.Width == 9 && arm.Height == 3, $"{arm.Width}x{arm.Height}");
        Check("a turned part's manifest size is transposed", arm.Entry.Width == 9 && arm.Entry.Height == 3);
        Check("a turned part keeps rotate and degrees", arm.Entry.Rotate && arm.Entry.Degrees == 90);

        PlannedPart hat = planned[2];
        Check("a trimmed part is written on the untrimmed canvas", hat.Width == 12 && hat.Height == 10);
        Check("a trimmed drawing lands at the bottom-counted offset", At(hat.Image, 12, 2, 3, 0) == 10,
              "expected the drawing's top-left at (2, 10-1-6=3)");
        Check("a trimmed part reports the trimmed size in the manifest", hat.Entry.Width == 8 && hat.Entry.Height == 6);

        // Two parts wanting the same name: the second is numbered, the identity still tells them apart.
        List<PlannedPart> repeats = DumpPlan.ForAtlas(
            new[] { Spec("char_test", "a/hair", "a/hair", new PixelRect(4, 4, 8, 6), false, 0, 8, 6, 0f, 0f),
                    Spec("char_test", "b/hair", "b/hair", new PixelRect(4, 4, 8, 6), false, 0, 8, 6, 0f, 0f) },
            atlas, atlasWidth, atlasHeight);
        Check("a repeated name is numbered", repeats[0].FileName == "hair.png" && repeats[1].FileName == "hair_2.png");
        Check("a numbered part keeps the plain display name", repeats[1].Entry.DisplayName == "hair");
        Check("the two are different parts", repeats[0].Entry.Identity != repeats[1].Entry.Identity);

        // Parts that must be left out, each with its reason.
        skips.Clear();
        List<PlannedPart> refused = DumpPlan.ForAtlas(
            new[] { Spec("char_test", "body_hexie", "body_hexie", new PixelRect(4, 4, 8, 6), false, 0, 8, 6, 0f, 0f),
                    Spec("char_test", "outside", "outside", new PixelRect(60, 28, 8, 6), false, 0, 8, 6, 0f, 0f),
                    Spec("char_test", "nosize", "nosize", new PixelRect(4, 4, 8, 6), false, 0, 0, 0, 0f, 0f) },
            atlas, atlasWidth, atlasHeight, skipped: (part, reason) => skips.Add(part.AttachmentName + ": " + reason));
        Check("censored, out-of-bounds and sizeless parts are all left out", refused.Count == 0 && skips.Count == 3,
              string.Join("; ", skips));
        Check("the censored part says so", skips[0].Contains("censored"));
        Check("the out-of-bounds part says which atlas it does not fit", skips[1].Contains("outside the 64x32"));

        // The manifest merges instead of dropping the atlases dumped earlier.
        var existing = new RegionManifest();
        existing.Put(new RegionManifestEntry { File = @"other_atlas\leg.png", Atlas = "other_atlas", RegionName = "leg", AttachmentName = "leg" });
        RegionManifest merged = DumpPlan.Manifest(planned, existing);
        Check("manifest keeps an earlier atlas", merged.Regions.Any(r => r.Atlas == "other_atlas"));
        Check("manifest gains this atlas", merged.Regions.Count(r => r.Atlas == "char_test") == 3);
        Check("re-dumping the same atlas does not duplicate", DumpPlan.Manifest(planned, merged).Regions.Count == 4);

        // A part the game no longer has is reported, not silently dropped.
        var gone = new RegionManifest();
        gone.Put(body.Entry);
        gone.Put(new RegionManifestEntry { File = @"char_test\ghost.png", Atlas = "char_test", RegionName = "ghost", AttachmentName = "ghost" });
        List<RegionManifestEntry> stale = DumpPlan.Stale(gone, planned);
        Check("a vanished part is reported as stale", stale.Count == 1 && stale[0].RegionName == "ghost");
    }

    /// <summary>
    /// The anchor formula against the twelve parts measured in game on 2026-11-17 (probe log): canvas size,
    /// the anchor the old Main computed, and the attachment's own placement. These are fixtures — if the
    /// formula is ever "simplified", these parts will say so.
    /// </summary>
    private static void AnchorFormulaFixtures()
    {
        // name, canvasW, canvasH, measuredX, measuredY, attachment x, y, width, height, rotation
        (string Name, int W, int H, float X, float Y, float Ax, float Ay, float Aw, float Ah, float Rot)[] measured =
        {
            ("hand_left_thumb_4", 22, 16, 4.668f, 4.827f, 6.89f, -0.942f, 21.5f, 16f, 19.358f),
            ("guita", 204, 568, 74.353f, 284.508f, -0.195f, 27.651f, 204f, 567.5f, 89.352f),
            ("hand_left_thumb_3", 18, 29, 9.9f, 7.369f, 7.042f, -0.545f, 17.5f, 28.5f, 92.687f),
            ("hand_left_thumb_2", 17, 23, 9.91f, 3.758f, 7.869f, -0.069f, 17f, 23f, 99.822f),
            ("arm_left", 74, 91, 8.608f, 17.524f, 39.687f, 1.686f, 73.5f, 91f, 47.204f),
            ("box", 201, 152, 99.345f, 150.185f, 0.611f, 73.948f, 201f, 151.5f, 0.422f),
            ("leg_left_1", 134, 48, 1.493f, 24.667f, 65.507f, 0.667f, 134f, 48f, 0f),
            ("box_2", 141, 275, 15.822f, 271.024f, 54.484f, 133.281f, 140.5f, 274.5f, 0f),
            ("box_ring", 100, 100, 50.088f, 48.365f, -0.088f, -1.635f, 100f, 100f, 0f),
            ("arm_right", 95, 83, 87.063f, 16.097f, 46.612f, -3.7f, 94.5f, 82.5f, 142.777f),
            ("box_3", 186, 115, 98.055f, 111.406f, -5.055f, 53.906f, 186f, 115f, 0f),
            ("box_ring_2", 50, 50, 25.522f, 25.271f, -0.522f, 0.271f, 50f, 50f, 0f),
        };

        double worst = 0;
        foreach (var part in measured)
        {
            (float x, float y) = RegionGeometry.QuadAnchor(part.Ax, part.Ay, part.Aw, part.Ah, part.Rot, part.W, part.H);
            double residual = Math.Max(Math.Abs(x - part.X), Math.Abs(y - part.Y));
            worst = Math.Max(worst, residual);
            // The log keeps three decimals, so anything under 0.002 is the log's own rounding.
            Check($"anchor of {part.Name} matches the game", residual < 0.002,
                  $"got ({x:0.###}, {y:0.###}) want ({part.X}, {part.Y}), off by {residual:0.####}");
        }
        Check("every measured anchor is reproduced to within the log's rounding", worst < 0.002, $"worst {worst:0.####}");

        // The scaling step is what the five half-pixel parts need: without it they drift by ~0.2 px.
        (float naiveX, float _) = ((float)(141 / 2.0 - 54.484), 0f);
        Check("the naive formula without scaling is measurably wrong", Math.Abs(naiveX - 15.822f) > 0.1f,
              "box_2 would be 0.19 px off");

        // A part with no placement of its own is anchored by its canvas centre.
        PartSpec mesh = Spec("char_test", "hair", "hair", new PixelRect(0, 0, 8, 6), false, 0, 8, 6, 0f, 0f);
        Check("a mesh part falls back to the canvas centre", RegionGeometry.AnchorOf(mesh, 8, 6) == (4f, 3f));
        PartSpec quad = mesh with { HasQuad = true, QuadX = 0f, QuadY = 0f, QuadWidth = 8f, QuadHeight = 6f };
        Check("a quad with no offset also sits at the centre", RegionGeometry.AnchorOf(quad, 8, 6) == (4f, 3f));
        Check("a degenerate quad falls back instead of dividing by zero",
              RegionGeometry.QuadAnchor(1f, 1f, 0f, 0f, 0f, 8, 6) == (4f, 3f));
    }

    private static void FpsLimiterRules()
    {
        Check("the current form is read", FpsLimiterSettings.Parse("{\"FPSLimit\": 120}") == 120);
        Check("zero means do not cap", FpsLimiterSettings.Parse("{\"FPSLimit\": 0}") == 0);
        Check("legacy enabled=false means do not cap", FpsLimiterSettings.Parse("{\"enabled\": false}") == 0);
        Check("legacy targetFps is read", FpsLimiterSettings.Parse("{\"targetFps\": 144}") == 144);
        Check("FPSLimit wins over the legacy fields", FpsLimiterSettings.Parse("{\"targetFps\": 144, \"FPSLimit\": 90}") == 90);
        Check("an empty object falls back to the default", FpsLimiterSettings.Parse("{}") == FpsLimiterSettings.DefaultLimit);

        var warnings = new List<string>();
        Check("a cap below the minimum is refused", FpsLimiterSettings.Parse("{\"FPSLimit\": 1}", warnings.Add) == 60);
        Check("a cap above the maximum is refused", FpsLimiterSettings.Parse("{\"FPSLimit\": 1000}", warnings.Add) == 60);
        Check("both refusals are explained", warnings.Count == 2 && warnings[0].Contains("between 15 and 360"));

        Check("a cap is applied when there is none", FpsLimiterSettings.ShouldApply(-1, 60));
        Check("a cap is applied when the current one is higher", FpsLimiterSettings.ShouldApply(144, 60));
        Check("a cap is not re-applied when it already holds", !FpsLimiterSettings.ShouldApply(60, 60));
        Check("a lower target is left alone", !FpsLimiterSettings.ShouldApply(30, 60));
        Check("limit 0 never applies", !FpsLimiterSettings.ShouldApply(-1, 0));

        string folder = Path.Combine(Path.GetTempPath(), "sf-fps-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string path = Path.Combine(folder, MainLayout.FpsLimiterFile);
            Check("a missing file is created with the default", FpsLimiterSettings.Load(path) == 60 && File.Exists(path));
            Check("the written file is the current form", File.ReadAllText(path).Contains("\"FPSLimit\": 60"));
            File.WriteAllText(path, "{ broken");
            Check("a broken file falls back instead of throwing", FpsLimiterSettings.Load(path, _ => { }) == 60);
        }
        finally { try { Directory.Delete(folder, true); } catch { /* temp folder */ } }
    }

    private static void RosterRules()
    {
        const string characters = """
            [ { "cosName": "Marija", "other": 1 },
              { "cosName": "Rin" },
              { "cosName": "Buro_hexie" },
              { "cosName": "   " },
              { "noName": true },
              { "cosName": "Bassist" } ]
            """;
        List<RosterEntry> listed = Roster.Parse(characters, Roster.CharacterNameField);
        Check("every usable row is listed", listed.Count == 3, string.Join(", ", listed));
        Check("the index is the row's position, not its place in the list",
              listed[2].Index == 5 && listed[2].Name == "Bassist", listed[2].ToString());
        Check("a censored row is left out but keeps its index free",
              listed.All(e => e.Name != "Buro_hexie") && listed[1].Index == 1);
        Check("a blank name is left out", listed.All(e => e.Name.Trim().Length > 0));
        Check("the row count includes the rows left out", Roster.Count(characters) == 6);
        Check("a name can be looked up by index", Roster.NameAt(listed, 5) == "Bassist");
        Check("an unknown index gives an empty name", Roster.NameAt(listed, 3) == string.Empty);

        const string elfins = """[ { "name": "Shika" }, { "name": "Nekoyama" } ]""";
        Check("elfins use their own field", Roster.Parse(elfins, Roster.ElfinNameField).Count == 2);
        Check("the character field finds nothing in the elfin table",
              Roster.Parse(elfins, Roster.CharacterNameField).Count == 0);

        Check("the tables are named per kind", Roster.TableOf(false) == "character_English" && Roster.TableOf(true) == "elfin_English");
        Check("the name fields are named per kind", Roster.NameFieldOf(false) == "cosName" && Roster.NameFieldOf(true) == "name");

        Check("no table at all gives an empty list", Roster.Parse(null, "cosName").Count == 0);
        Check("broken json gives an empty list instead of throwing", Roster.Parse("{ not json", "cosName").Count == 0);
        Check("an object instead of an array gives an empty list", Roster.Parse("{\"cosName\":\"x\"}", "cosName").Count == 0);
        Check("broken json counts as no rows", Roster.Count("{ not json") == 0);
    }

    private static void WindowModelRules()
    {
        const string creator = @"C:\Game\SkinForger\SkinCreator\Images";
        string bassist = Path.Combine(creator, "Bassist");

        List<SkinChoice> none = WindowModel.ChoicesOf("Rin", Array.Empty<string>(), creator);
        Check("a costume with no skins can still be set to the game's art", none.Count == 1 && none[0].IsOriginal);
        Check("the first choice is labelled Original", none[0].Label == "Original");

        List<SkinChoice> edited = WindowModel.ChoicesOf("Bassist", new[] { bassist }, creator);
        Check("the creator's own folder is the Edited choice", edited.Count == 2 && edited[1].Label == "Edited");
        Check("Edited is position 0, as it is stored", edited[1].Position == 0);
        Check("Edited keeps pointing at the folder", edited[1].Directory == bassist);

        List<SkinChoice> withSkin = WindowModel.ChoicesOf("Bassist", new[] { @"C:\Game\SkinForger\Skins\sfpkg-library\Bassist", bassist }, creator);
        Check("another skin is named after its folder", withSkin[1].Label == "Bassist" && withSkin[1].Position == 0);
        Check("the creator's folder is still recognised among several", withSkin[2].Label == "Edited" && withSkin[2].Position == 1);

        var roster = new List<RosterEntry> { new(0, "Marija"), new(2, "Bassist"), new(3, "Buro_hexie") };
        List<CostumeRow> rows = WindowModel.Rows(roster, elfin: false, creator,
            costume => costume == "Bassist" ? new[] { bassist } : Array.Empty<string>(),
            index => index == 2 ? 0 : SelectionStore.Original);
        Check("a censored costume never reaches the window", rows.Count == 2);
        Check("a row keeps the roster index", rows[1].Index == 2 && rows[1].Name == "Bassist");
        Check("a row with no dump has no Edited button", !rows[0].HasEdited);
        Check("a row with a dump has one", rows[1].HasEdited);
        Check("the showing choice is the stored one", rows[1].Current.Label == "Edited" && !rows[1].IsOriginal);
        Check("a row left alone shows the game's art", rows[0].IsOriginal);
        Check("replaced lists only the changed rows", WindowModel.Replaced(rows).Single().Name == "Bassist");

        Check("the Edited button of a dumped costume stores its position", WindowModel.EditedPosition(rows[1]) == 0);
        Check("the Edited button of a costume with no dump stores Original",
              WindowModel.EditedPosition(rows[0]) == SelectionStore.Original);

        Check("clicking a choice that is not showing selects it",
              WindowModel.PositionAfterClick(rows[0], rows[0].Choices[0]) == SelectionStore.Original);
        Check("clicking the choice that is already showing puts it back to Original",
              WindowModel.PositionAfterClick(rows[1], rows[1].Choices[1]) == SelectionStore.Original);
        Check("clicking another choice switches to it",
              WindowModel.PositionAfterClick(rows[1] with { Selected = SelectionStore.Original }, rows[1].Choices[1]) == 0);

        Check("an unknown stored position falls back to the game's art",
              (rows[1] with { Selected = 7 }).Current.IsOriginal);

        Check("a folder label is its own name", WindowModel.LabelOf(@"C:\a\b\Cat Skin") == "Cat Skin");
        Check("a trailing separator does not confuse the label", WindowModel.LabelOf(@"C:\a\b\Cat Skin\") == "Cat Skin");
    }

    private static void MenuKeyRules()
    {
        Check("a plain name is kept", MenuKeySettings.Normalize("Insert") == "Insert");
        Check("surrounding space is dropped", MenuKeySettings.Normalize("  F7  ") == "F7");
        Check("a trailing newline is dropped", MenuKeySettings.Normalize("Home\r\n") == "Home");
        Check("quotes are dropped", MenuKeySettings.Normalize("\"Delete\"") == "Delete");
        Check("only the first line counts", MenuKeySettings.Normalize("End\nignored") == "End");
        Check("nothing gives nothing", MenuKeySettings.Normalize("   ") == string.Empty);

        string folder = Path.Combine(Path.GetTempPath(), "sf-menukey-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string path = Path.Combine(folder, MainLayout.MenuKeyFile);
            Check("a missing file is created with Insert", MenuKeySettings.Load(path) == "Insert" && File.Exists(path));
            File.WriteAllText(path, "F9\r\n");
            Check("an edited file is read", MenuKeySettings.Load(path) == "F9");
            File.WriteAllText(path, "   ");
            Check("an empty file falls back to Insert", MenuKeySettings.Load(path, _ => { }) == "Insert");
        }
        finally { try { Directory.Delete(folder, true); } catch { /* temp folder */ } }
    }

    private static void PrefabIndexRules()
    {
        // Shaped like the game's character table: a costume's own show prefabs, plus a "skins" array whose
        // entries have the same fields again (Bassist and Maid have several skins; one may be censored).
        const string characters = """
            [ { "cosName": "Bassist",
                "mainShow": "1_rock_main_show", "battleShow": "char_1_rock",
                "victoryShow": "1_rock_victory", "failShow": "char_1_rock_fail",
                "skins": [ { "mainShow": "1_rock_main_show_cat", "battleShow": "char_1_rock_cat", "isCat": true },
                           { "mainShow": "1_musclerin_show", "battleShow": "char_1_rock_muscle" },
                           { "mainShow": "1_rock_hexie_show", "hexie": true } ] },
              { "cosName": "Marija", "mainShow": "2_marija_main_show" } ]
            """;
        const string elfins = """[ { "name": "Shika", "prefabName": "elfin_deer" } ]""";

        List<PrefabOwner> owners = PrefabIndex.Build(characters, elfins);
        Check("a costume's own prefabs are indexed", owners.Any(o => o.Prefab == "1_rock_main_show" && o.Index == 0));
        Check("the prefabs of its other skins are too", owners.Any(o => o.Prefab == "char_1_rock_muscle" && o.Index == 0));
        Check("a censored skin contributes nothing", owners.All(o => !o.Prefab.Contains("hexie")));
        Check("a cat skin is marked", owners.Single(o => o.Prefab == "char_1_rock_cat").Cat);
        Check("a normal skin is not", !owners.Single(o => o.Prefab == "char_1_rock").Cat);
        Check("elfins come from their own table", owners.Single(o => o.Prefab == "elfin_deer").Elfin);
        Check("a second character keeps its own index", owners.Single(o => o.Prefab == "2_marija_main_show").Index == 1);

        // The rule that matters: "char_1_rock" is a prefix of "char_1_rock_cat" and of "char_1_rock_muscle".
        Check("owners are sorted longest name first",
              owners.Select(o => o.Prefab.Length).SequenceEqual(owners.Select(o => o.Prefab.Length).OrderByDescending(l => l)));
        Check("a cat skeleton is not mistaken for the plain skin",
              PrefabIndex.Match(owners, "char_1_rock_cat(Clone)")?.Prefab == "char_1_rock_cat");
        Check("a muscle skeleton is not either",
              PrefabIndex.Match(owners, "char_1_rock_muscle(Clone)")?.Prefab == "char_1_rock_muscle");
        Check("the plain skin still matches itself",
              PrefabIndex.Match(owners, "char_1_rock(Clone)")?.Prefab == "char_1_rock");
        Check("every one of them belongs to the same costume",
              new[] { "char_1_rock_cat", "char_1_rock_muscle", "char_1_rock" }
                  .All(n => PrefabIndex.Match(owners, n)?.Index == 0));

        Check("a name no prefab appears in matches nothing", PrefabIndex.Match(owners, "PnlRole(Clone)") == null);
        Check("an empty name matches nothing", PrefabIndex.Match(owners, string.Empty) == null);
        Check("matching ignores case", PrefabIndex.Match(owners, "CHAR_1_ROCK_CAT")?.Index == 0);

        Check("a costume's prefabs can be listed", PrefabIndex.PrefabsOf(owners, 0, false).Count == 8,
              string.Join(", ", PrefabIndex.PrefabsOf(owners, 0, false)));
        Check("an elfin's prefabs are separate", PrefabIndex.PrefabsOf(owners, 0, true).Single() == "elfin_deer");

        Check("a show field is recognised", PrefabIndex.IsPrefabField("victoryShow") && PrefabIndex.IsPrefabField("mainShow"));
        Check("prefabName is recognised", PrefabIndex.IsPrefabField("prefabName"));
        Check("an ordinary field is not", !PrefabIndex.IsPrefabField("cosName") && !PrefabIndex.IsPrefabField("headName"));
        Check("a censored field is not", !PrefabIndex.IsPrefabField("mainShowHexie"));

        Check("no table gives no owners", PrefabIndex.Build(null, null).Count == 0);
        Check("broken json gives no owners", PrefabIndex.Parse("{ not json", false).Count == 0);
    }

    private static void AtlasComposerRules()
    {
        const int width = 64, height = 32;
        byte[] original = PixelMath.Transparent(width, height);
        Fill(original, width, new PixelRect(0, 0, width, height), 7, 8, 9, 255);

        AtlasComposition nothing = AtlasComposer.Compose(original, width, height, Array.Empty<Replacement>());
        Check("with no replacement the atlas keeps its size", nothing.Width == width && nothing.Height == height);
        Check("and its pixels", PixelMath.Same(nothing.Pixels, original));

        byte[] red = PixelMath.Transparent(8, 6);
        Fill(red, 8, new PixelRect(0, 0, 8, 6), 200, 0, 0, 128);     //半透明: tests the premultiply on the way in
        byte[] blue = PixelMath.Transparent(10, 4);
        Fill(blue, 10, new PixelRect(0, 0, 10, 4), 0, 0, 200, 255);

        AtlasComposition composed = AtlasComposer.Compose(original, width, height, new[]
        {
            new Replacement("a|hair|hair", red, 8, 6),
            new Replacement("a|body|body", blue, 10, 4),
        });
        Check("nothing was refused", composed.Refused.Count == 0, string.Join("; ", composed.Refused));
        Check("both replacements were placed", composed.Placed.Count == 2);
        Check("the atlas grew downwards only", composed.Width == width && composed.Height > height);
        Check("the height is a multiple of four", composed.Height % 4 == 0, composed.Height.ToString());

        // The game's own pixels are untouched, at the same coordinates.
        byte[] top = PixelMath.Crop(composed.Pixels, composed.Width, composed.Height, new PixelRect(0, 0, width, height));
        Check("the original atlas is copied unchanged", PixelMath.Same(top, original));

        PlacedReplacement hair = composed.Placed.Single(p => p.Identity == "a|hair|hair");
        PlacedReplacement body = composed.Placed.Single(p => p.Identity == "a|body|body");
        Check("a new rectangle is below the original atlas", hair.Rect.Y >= height && body.Rect.Y >= height);
        Check("new rectangles do not overlap each other", !hair.Rect.Overlaps(body.Rect));
        Check("a new rectangle is never stored turned", !hair.Rotate && hair.Degrees == 0);
        Check("the rectangle is the size of the image", hair.Rect.Width == 8 && hair.Rect.Height == 6);

        // The image went in premultiplied: 200 at alpha 128 becomes about 100.
        byte placedRed = At(composed.Pixels, composed.Width, hair.Rect.X, hair.Rect.Y, 0);
        Check("a replacement is premultiplied on the way in", Math.Abs(placedRed - 100) <= 1, placedRed.ToString());
        Check("and keeps its alpha", At(composed.Pixels, composed.Width, hair.Rect.X, hair.Rect.Y, 3) == 128);
        Check("an opaque replacement is unchanged",
              At(composed.Pixels, composed.Width, body.Rect.X, body.Rect.Y, 2) == 200);

        // The UVs must point back at exactly that rectangle.
        PixelRect fromUv = RegionGeometry.StoredRect(hair.U, hair.V, hair.U2, hair.V2, composed.Width, composed.Height);
        Check("the UVs of a placement name its own rectangle", fromUv == hair.Rect, $"{fromUv} vs {hair.Rect}");

        // A region that was not replaced keeps its pixels after the atlas grew.
        var untouched = new PixelRect(4, 5, 20, 9);
        PlacedReplacement before = AtlasComposer.Place("x", untouched, width, height);
        (float v, float v2) = AtlasComposer.Rebase(before.V, before.V2, height, composed.Height);
        PixelRect after = RegionGeometry.StoredRect(before.U, v, before.U2, v2, composed.Width, composed.Height);
        Check("an untouched region still names its own pixels after the atlas grew", after == untouched,
              $"{after} vs {untouched}");
        Check("rebasing an atlas that did not grow changes nothing",
              AtlasComposer.Rebase(0.25f, 0.1f, height, height) == (0.25f, 0.1f));

        // Shelves: enough images to need a second row.
        var many = Enumerable.Range(0, 12).Select(i => new Replacement($"a|p{i:00}|p{i:00}", PixelMath.Transparent(20, 5), 20, 5)).ToList();
        AtlasComposition shelves = AtlasComposer.Compose(original, width, height, many);
        Check("every image found a place", shelves.Placed.Count == 12 && shelves.Refused.Count == 0);
        Check("images are laid out in rows", shelves.Placed.Select(p => p.Rect.Y).Distinct().Count() > 1);
        var rects = shelves.Placed.Select(p => p.Rect).ToList();
        bool overlap = rects.Any(a => rects.Any(b => !ReferenceEquals(a, b) && a != b && a.Overlaps(b)));
        Check("no two images overlap", !overlap);
        Check("no image is in the original area", rects.All(r => r.Y >= height));
        Check("every image is inside the atlas", rects.All(r => r.FitsIn(shelves.Width, shelves.Height)));

        // Packing quality. The old Main grew Bassist's 512x2048 atlas to 512x7663 in twelve steps; the
        // height here must stay close to what the images actually need, whatever order they arrive in.
        var mixed = new List<Replacement>();
        var random = new Random(20261117);
        long area = 0;
        for (int i = 0; i < 60; i++)
        {
            int w = 8 + random.Next(40), h = 4 + random.Next(60);
            mixed.Add(new Replacement($"a|m{i:00}|m{i:00}", PixelMath.Transparent(w, h), w, h));
            area += (long)(w + AtlasComposer.Padding) * (h + AtlasComposer.Padding);
        }
        AtlasComposition packed = AtlasComposer.Compose(original, width, height, mixed);
        int added = packed.Height - height;
        int ideal = (int)(area / width);
        Check("every image was placed", packed.Placed.Count == 60 && packed.Refused.Count == 0);
        Check("the atlas grows roughly as much as the images need", added <= ideal * 2,
              $"added {added} rows for about {ideal} rows of images");
        var packedRects = packed.Placed.Select(p => p.Rect).ToList();
        Check("nothing overlaps in a full atlas",
              !packedRects.Any(a => packedRects.Any(b => a != b && a.Overlaps(b))));
        Check("the original area is still untouched", packedRects.All(r => r.Y >= height));

        // Refusals, each with its reason.
        AtlasComposition refused = AtlasComposer.Compose(original, width, height, new[]
        {
            new Replacement("a|wide|wide", PixelMath.Transparent(200, 4), 200, 4),
            new Replacement("a|lying|lying", new byte[8], 10, 10),
        });
        Check("an image wider than the atlas is refused", refused.Refused.Any(r => r.Contains("does not fit")));
        Check("an image whose buffer does not match its size is refused", refused.Refused.Any(r => r.Contains("bytes")));
        Check("a refused image is not placed", refused.Placed.Count == 0);
        Check("and the atlas does not grow for it", refused.Height == height);

        // The order of the placements does not depend on the order they arrive in.
        AtlasComposition first = AtlasComposer.Compose(original, width, height, new[]
        {
            new Replacement("a|one|one", PixelMath.Transparent(6, 6), 6, 6),
            new Replacement("a|two|two", PixelMath.Transparent(6, 6), 6, 6),
        });
        AtlasComposition second = AtlasComposer.Compose(original, width, height, new[]
        {
            new Replacement("a|two|two", PixelMath.Transparent(6, 6), 6, 6),
            new Replacement("a|one|one", PixelMath.Transparent(6, 6), 6, 6),
        });
        Check("composing is deterministic",
              first.Placed.Select(p => p.Identity + p.Rect).SequenceEqual(second.Placed.Select(p => p.Identity + p.Rect)));
    }

    private static void SkinIndexRules()
    {
        var manifest = new RegionManifest();
        void Part(string atlas, string name, string file)
        {
            manifest.Put(new RegionManifestEntry
            {
                File = file, Atlas = atlas, RegionName = name, AttachmentName = name, DisplayName = name
            });
        }
        Part("char_1_rock", "hair_B", @"char_1_rock\hair_B.png");
        Part("char_1_rock", "1_images_bunny/hair_B", @"char_1_rock\hair_B_2.png");
        Part("char_1_rock", "body_hexie", @"char_1_rock\body_hexie.png");
        Part("char_1_rock", "ghost", @"char_1_rock\ghost.png");

        var onDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\skin\manifest.json", @"C:\skin\char_1_rock\hair_B.png", @"C:\skin\char_1_rock\hair_B_2.png"
        };
        SkinIndex skin = SkinIndex.Read(@"C:\skin", onDisk.Contains, _ => manifest);

        Check("the folder was read", skin.Problem == null, skin.Problem);
        Check("only images that exist are indexed", skin.Count == 2, skin.Count.ToString());
        Check("a part is found by its identity",
              skin.FileFor("char_1_rock|hair_B|hair_B") == @"C:\skin\char_1_rock\hair_B.png");
        Check("two parts that share a name are told apart by identity",
              skin.FileFor("char_1_rock|1_images_bunny/hair_B|1_images_bunny/hair_B") == @"C:\skin\char_1_rock\hair_B_2.png");
        Check("a censored part is never indexed", skin.FileFor("char_1_rock|body_hexie|body_hexie") == null);
        Check("a part whose file was never written is left out", skin.FileFor("char_1_rock|ghost|ghost") == null);
        Check("a part no skin has gives null", skin.FileFor("char_1_rock|nothing|nothing") == null);

        SkinIndex missing = SkinIndex.Read(@"C:\none", _ => false, _ => manifest);
        Check("a folder with no manifest says so", missing.Problem != null && missing.Problem!.Contains("manifest.json"));
        Check("and indexes nothing", missing.Count == 0);

        SkinIndex broken = SkinIndex.Read(@"C:\skin", onDisk.Contains, _ => throw new InvalidDataException("damaged"));
        Check("a damaged manifest is reported, not thrown", broken.Problem != null && broken.Problem!.Contains("damaged"));

        // Layering: the last skin that has a part wins.
        var lower = SkinIndex.Read(@"C:\skin", onDisk.Contains, _ => manifest);
        var upperManifest = new RegionManifest();
        upperManifest.Put(new RegionManifestEntry
        {
            File = @"char_1_rock\hair_B.png", Atlas = "char_1_rock", RegionName = "hair_B", AttachmentName = "hair_B"
        });
        SkinIndex upper = SkinIndex.Read(@"C:\mine",
            p => p.Equals(@"C:\mine\manifest.json", StringComparison.OrdinalIgnoreCase) ||
                 p.Equals(@"C:\mine\char_1_rock\hair_B.png", StringComparison.OrdinalIgnoreCase),
            _ => upperManifest);

        Dictionary<string, string> layered = SkinIndex.Layer(new[] { lower, upper });
        Check("the higher skin wins for a part both have",
              layered["char_1_rock|hair_B|hair_B"] == @"C:\mine\char_1_rock\hair_B.png");
        Check("a part only the lower skin has is kept",
              layered["char_1_rock|1_images_bunny/hair_B|1_images_bunny/hair_B"] == @"C:\skin\char_1_rock\hair_B_2.png");
        Check("layering adds nothing else", layered.Count == 2);
    }

    private static void DumpComparisonRules()
    {
        RegionManifestEntry Entry(string name, int x = 1, int y = 2, int w = 3, int h = 4) => new()
        {
            File = $@"char_test\{name}.png", Atlas = "char_test", AttachmentName = name, RegionName = name,
            DisplayName = name, X = x, Y = y, Width = w, Height = h, OriginalWidth = w, OriginalHeight = h
        };

        var old = new RegionManifest();
        old.Put(Entry("body"));
        var mine = new RegionManifest();
        mine.Put(Entry("body"));
        Check("identical manifests compare clean", DumpComparison.Manifests(old, mine).Count == 0);

        var moved = new RegionManifest();
        moved.Put(Entry("body", x: 9));
        List<DumpDifference> differences = DumpComparison.Manifests(old, moved);
        Check("a changed field is reported", differences.Count == 1 && differences[0].Kind == "manifest");
        Check("the report names the field and both values", differences[0].Detail.Contains("x: old 1, authored 9"));
        Check("a field difference is not clean", !DumpComparison.IsClean(differences));

        var withExtra = new RegionManifest();
        withExtra.Put(Entry("body"));
        withExtra.Put(Entry("tail"));
        Check("a part only the authored dump has is 'extra'",
              DumpComparison.Manifests(old, withExtra).Single().Kind == "extra");

        var censored = new RegionManifest();
        censored.Put(Entry("body"));
        censored.Put(Entry("body_hexie"));
        List<DumpDifference> censoredDifferences = DumpComparison.Manifests(censored, mine);
        Check("a censored part the old dump listed is expected to be missing",
              censoredDifferences.Single().Kind == "censored-omitted");
        Check("a censored omission is clean", DumpComparison.IsClean(censoredDifferences));

        var renamed = new RegionManifest();
        RegionManifestEntry renumbered = Entry("body");
        renumbered.File = @"char_test\body_2.png";
        renamed.Put(renumbered);
        List<DumpDifference> renumberedDifferences = DumpComparison.Manifests(old, renamed);
        Check("a different file name for the same part is 'renumbered'", renumberedDifferences.Single().Kind == "renumbered");
        Check("renumbering is clean", DumpComparison.IsClean(renumberedDifferences));

        byte[] a = { 10, 20, 30, 255, 1, 2, 3, 4 };
        byte[] b = { 10, 20, 30, 255, 1, 2, 3, 4 };
        Check("identical pixels count as none differing", DumpComparison.CountDifferingPixels(a, b) == 0);
        b[5] = 9;
        Check("one differing pixel is counted once", DumpComparison.CountDifferingPixels(a, b) == 1);
        Check("tolerance absorbs a small difference", DumpComparison.CountDifferingPixels(a, b, 10) == 0);
        Check("different lengths count as all differing", DumpComparison.CountDifferingPixels(a, new byte[4]) == 2);

        Check("the report of nothing says identical", DumpComparison.Report(Array.Empty<DumpDifference>()).Contains("identical"));
        Check("the report counts by kind", DumpComparison.Report(differences).Contains("manifest x1"));
    }

    private static PartSpec Spec(string atlas, string attachment, string region, PixelRect stored, bool rotate,
        int degrees, int originalWidth, int originalHeight, float offsetX, float offsetY) =>
        new(atlas, attachment, region, stored, rotate, degrees, originalWidth, originalHeight, offsetX, offsetY,
            0.1f, 0.2f, 0.3f, 0.4f, 64, 32);

    private static void Fill(byte[] pixels, int width, PixelRect rect, byte r, byte g, byte b, byte a)
    {
        for (int y = rect.Y; y < rect.Bottom; y++)
            for (int x = rect.X; x < rect.Right; x++)
                Set(pixels, width, x, y, r, g, b, a);
    }

    // ---------------------------------------------------------------- real files

    private static void RealFiles(string imagesFolder)
    {
        Console.WriteLine($"\nreal files under {imagesFolder}");
        int manifests = 0, entries = 0, sidecars = 0;
        foreach (string file in Directory.EnumerateFiles(imagesFolder, RegionManifest.FileName, SearchOption.AllDirectories))
        {
            RegionManifest manifest = RegionManifest.Read(file);
            manifests++;
            entries += manifest.Regions.Count;

            // Every real part this player has dumped must still be found when the page is named the way
            // Spine names it (with ".png"). This is the 16:31 trial's failure, measured on their own files:
            // while these two differed, nothing was ever replaced.
            int spelled = manifest.Regions.Count(region =>
                RegionManifest.PartIdentity(region.Atlas + ".png", region.RegionName, region.AttachmentName)
                    == region.Identity);
            Check($"{Path.GetFileName(Path.GetDirectoryName(file))}: all {manifest.Regions.Count} parts match " +
                  "their Spine page spelling", spelled == manifest.Regions.Count,
                  $"{spelled} of {manifest.Regions.Count}");
            byte[] again = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true });
            RegionManifest reparsed = RegionManifest.Parse(again);
            bool same = reparsed.Regions.Count == manifest.Regions.Count &&
                        reparsed.Regions.Zip(manifest.Regions).All(pair =>
                            RegionManifest.Normalize(pair.First.File) == RegionManifest.Normalize(pair.Second.File) &&
                            pair.First.Identity == pair.Second.Identity &&
                            pair.First.PackedRect == pair.Second.PackedRect &&
                            pair.First.Rotate == pair.Second.Rotate && pair.First.Degrees == pair.Second.Degrees &&
                            pair.First.U == pair.Second.U && pair.First.V2 == pair.Second.V2);
            Check($"round-trip {Path.GetFileName(Path.GetDirectoryName(file))}/manifest.json", same);

            // The frozen naming rules, re-derived on this machine's own files.
            string costume = Path.GetFileName(Path.GetDirectoryName(file))!;
            var replay = new DumpNaming();
            foreach (RegionManifestEntry entry in manifest.Regions)
            {
                string[] segments = entry.File.Replace('/', '\\').Split('\\');
                Check($"{costume}/{entry.File}: file is <atlas>\\<name>.png",
                      segments.Length == 2 && segments[0] == entry.Atlas && segments[1].EndsWith(".png", StringComparison.OrdinalIgnoreCase));
                Check($"{costume}/{entry.File}: displayName is the tail of attachmentName",
                      entry.DisplayName == DumpNaming.DisplayNameOf(entry.AttachmentName),
                      $"{entry.DisplayName} vs {entry.AttachmentName}");
                // Replaying the dump in manifest order must reproduce the file name, suffix included.
                string expected = replay.NextFileName(entry.Atlas, entry.AttachmentName);
                Check($"{costume}/{entry.File}: name reproduced by the numbering rule", segments[1] == expected,
                      $"expected {expected}");

                // Geometry: solve the page size from this entry, then rebuild x / y / width / height from its UVs.
                Check($"{costume}/{entry.File}: rotate matches degrees", entry.Rotate == RegionGeometry.RotateFlagFor(entry.Degrees),
                      $"rotate={entry.Rotate} degrees={entry.Degrees}");
                if (!RegionGeometry.SolvePageSize(entry, out int pageWidth, out int pageHeight))
                {
                    Check($"{costume}/{entry.File}: page size solvable", false, "degenerate UVs");
                    continue;
                }
                PixelRect rebuilt = RegionGeometry.StoredRect(entry.U, entry.V, entry.U2, entry.V2, pageWidth, pageHeight);
                (int logicalWidth, int logicalHeight) = RegionGeometry.LogicalSize(rebuilt, entry.Rotate);
                Check($"{costume}/{entry.File}: x / y rebuilt from UVs",
                      rebuilt.X == entry.X && rebuilt.Y == entry.Y,
                      $"got {rebuilt.X},{rebuilt.Y} want {entry.X},{entry.Y} (page {pageWidth}x{pageHeight})");
                Check($"{costume}/{entry.File}: width / height rebuilt from UVs",
                      logicalWidth == entry.Width && logicalHeight == entry.Height,
                      $"got {logicalWidth}x{logicalHeight} want {entry.Width}x{entry.Height}");
                Check($"{costume}/{entry.File}: trim fits inside the untrimmed size",
                      entry.OffsetX + entry.Width <= entry.OriginalWidth + 0.01f &&
                      entry.OffsetY + entry.Height <= entry.OriginalHeight + 0.01f);
            }
        }
        foreach (string image in Directory.EnumerateFiles(imagesFolder, "*.png", SearchOption.AllDirectories))
        {
            RegionAnchorFile? anchor = RegionAnchorFile.ReadIfExists(image);
            if (anchor == null) continue;
            sidecars++;
            Check($"sidecar {Path.GetFileName(image)} is top-left v2", anchor.IsTopLeft && anchor.Version == RegionAnchorFile.CurrentVersion,
                  $"version {anchor.Version}, origin {anchor.Origin}");
        }
        Console.WriteLine($"  {manifests} manifests, {entries} entries, {sidecars} sidecars");
    }

    // ---------------------------------------------------------------- the two 2026-11-19 trial failures

    /// <summary>
    /// A part identity must not depend on HOW the atlas page was spelled.
    ///
    /// The same page reaches SkinForger twice: the Spine page calls itself "1_rock_main_show.png" and the
    /// loading path calls it "1_rock_main_show". Identities are plain strings, so while those produced two
    /// different keys the replacement images were read and composed and then no region was pointed at
    /// them: "0 replaced, 0 rebased, atlas 512x2048 -> 512x3728" with no error anywhere. Nothing but the
    /// game could show that, which is exactly why it is a test now.
    /// </summary>
    private static void PageSpellingRules()
    {

        string texture = RegionManifest.PartIdentity("1_rock_main_show", "hair_1", "hair_1");
        foreach (string spelling in new[]
                 {
                     "1_rock_main_show.png",
                     "1_rock_main_show",
                     "Bassist/1_rock_main_show.png",
                     @"Bassist\1_rock_main_show.png",
                 })
            Check($"identity of \"{spelling}\" equals the texture-name identity",
                  RegionManifest.PartIdentity(spelling, "hair_1", "hair_1") == texture,
                  RegionManifest.PartIdentity(spelling, "hair_1", "hair_1") + " vs " + texture);

        // Different pages must still be different parts, or the canonicalisation would hide real clashes.
        Check("a different page is a different identity",
              RegionManifest.PartIdentity("1_rock_main_show_cat.png", "hair_1", "hair_1") != texture);
        Check("a different region is a different identity",
              RegionManifest.PartIdentity("1_rock_main_show", "hair_2", "hair_1") != texture);

        // The hand-off the trial lost: step 1 keys parts by the TEXTURE name, step 4 looks them up with
        // the PAGE name. Both halves have to find each other for every part.
        var composed = new[] { "hair_1", "face", "hand_right" }
            .Select(name => RegionManifest.PartIdentity("1_rock_main_show", name, name))
            .ToHashSet(StringComparer.Ordinal);
        int found = new[] { "hair_1", "face", "hand_right" }
            .Count(name => composed.Contains(RegionManifest.PartIdentity("1_rock_main_show.png", name, name)));
        Check("every composed part is found again by its page name", found == 3, $"{found} of 3");
    }

    /// <summary>
    /// A mesh keeps its own shape and samples the piece of the author's picture that matches the game's
    /// canvas. Checked with the player's real elfin, whose six expanded parts are the big ones -- head,
    /// wings, body, fork -- which is why getting them wrong made the whole elfin look wrong.
    /// </summary>
    private static void MeshWindowRules()
    {
        // 1. Nothing expanded: the window is the whole image, so this is the ordinary path untouched.
        foreach ((int w, int h) in new[] { (70, 121), (29, 24), (145, 127), (13, 33) })
        {
            var placed = new PixelRect(100, 200, w, h);
            PixelRect whole = RegionGeometry.MeshWindow(placed, w, h, w / 2f, h / 2f, w / 2f, h / 2f);
            Check($"{w}x{h} unexpanded: the mesh samples the whole image", whole.Equals(placed), whole.ToString());
        }

        // 2. The player's expanded elfin parts, with their real sidecar anchors. The window must be the
        //    GAME's canvas, and it must start exactly where the anchors say.
        foreach ((string part, int canvasW, int canvasH, int pngW, int pngH, float ax, float ay) in new[]
                 {
                     ("body",    31,  54,  40,  54, 19.5f, 27.0f),
                     ("wing_l", 110,  81, 130,  90,  0.0f, 130.0f),
                     ("head",   115, 138, 138, 143, 92.0f, 157.0f),
                     ("fork",   191,  94, 208, 121, 93.0526f, 49.2638f),
                 })
        {
            var placed = new PixelRect(7, 9, pngW, pngH);
            PixelRect window = RegionGeometry.MeshWindow(placed, canvasW, canvasH, ax, ay, canvasW / 2f, canvasH / 2f);

            Check($"{part}: the window is the game's canvas", window.Width == canvasW && window.Height == canvasH,
                  $"{window.Width}x{window.Height}");
            Check($"{part}: the window stays inside the image",
                  window.X >= placed.X && window.Y >= placed.Y &&
                  window.X + window.Width <= placed.X + placed.Width &&
                  window.Y + window.Height <= placed.Y + placed.Height, window.ToString());

            // Where the anchors are in bounds, the start is exactly their difference -- no guessing.
            int wantX = Math.Clamp((int)Math.Round(ax - canvasW / 2f, MidpointRounding.AwayFromZero), 0, pngW - canvasW);
            Check($"{part}: the window starts where the anchors say", window.X - placed.X == wantX,
                  $"{window.X - placed.X} vs {wantX}");
        }

        // 3. An anchor that would hang off the edge is clamped, never allowed to read a neighbour's pixels.
        PixelRect clamped = RegionGeometry.MeshWindow(new PixelRect(0, 0, 130, 90), 110, 81, 999f, -999f, 55f, 40.5f);
        Check("an impossible anchor is clamped into the image",
              clamped.X == 20 && clamped.Y == 0 && clamped.Width == 110 && clamped.Height == 81, clamped.ToString());
    }

    /// <summary>
    /// The placement SkinForger hands to SkinForger.Core's elfin canvas adapter, checked against the
    /// adapter's own formula:
    ///
    ///     u = (DefaultAnchorX - AnchorX + OffsetX + uv.x * NormalWidth) / BaseNormalWidth
    ///
    /// Two things must hold, and both failed in game before this test existed:
    ///   * a part nobody expanded must come out exactly where it was (u == uv.x), and
    ///   * the anchor must land on the same u it had in the game's canvas.
    ///
    /// The trap is that the two anchors are measured in DIFFERENT canvases -- Anchor in the author's,
    /// DefaultAnchor in the game's. Measuring both in the author's makes the difference meaningless and
    /// drags every expanded part toward one spot, which is the elfin drawn with its pieces crammed
    /// together (2026-11-19).
    /// </summary>
    private static void ElfinCanvasPlacementRules()
    {
        // The adapter's formula, exactly as it is written in ElfinMeshCanvasMod.DesiredWorld.
        static double U(double uv, int normal, int baseNormal, float anchor, float defaultAnchor) =>
            (defaultAnchor - anchor + 0f + uv * normal) / baseNormal;

        // 1. Unexpanded: canvas unchanged, so both anchors are the same number and nothing moves.
        foreach ((int size, float anchor) in new[] { (110, 55f), (31, 15.5f), (182, 91f), (13, 6.5f) })
            foreach (double uv in new[] { 0.0, 0.25, 0.5, 1.0 })
                Check($"{size}px unexpanded at uv {uv}: the part does not move",
                      Math.Abs(U(uv, size, size, anchor, anchor) - uv) < 1e-9);

        // 2. Expanded, with the player's real elfin numbers. The anchor must map to the u it had in the
        //    game's canvas, and the part must span exactly png / canvas.
        foreach ((string part, int baseW, int pngW, float anchorInAuthor, float anchorInGame) in new[]
                 {
                     ("wing_l", 110, 130,  0.0f,  55.0f),
                     ("body",    31,  40, 19.5f,  15.5f),
                     ("head",   115, 138, 92.0f,  57.5f),
                 })
        {
            double atAnchor = U(anchorInAuthor / pngW, pngW, baseW, anchorInAuthor, anchorInGame);
            // 1e-6, not 1e-9: the inputs are floats, so the formula carries about 1e-8 of noise.
            Check($"{part}: the anchor stays where the game had it",
                  Math.Abs(atAnchor - anchorInGame / baseW) < 1e-6, atAnchor.ToString());

            double span = U(1.0, pngW, baseW, anchorInAuthor, anchorInGame) -
                          U(0.0, pngW, baseW, anchorInAuthor, anchorInGame);
            Check($"{part}: the part spans {pngW}/{baseW} of the original",
                  Math.Abs(span - pngW / (double)baseW) < 1e-6, span.ToString());

            // 3. The mistake itself: both anchors read in the author's canvas.
            double wrong = U(anchorInAuthor / pngW, pngW, baseW, anchorInAuthor, anchorInAuthor);
            Check($"{part}: measuring both anchors in one canvas would misplace it",
                  Math.Abs(wrong - anchorInGame / baseW) > 1e-6,
                  "if this ever passes, the two canvases have stopped differing and the test is vacuous");
        }

        // 4. AnchorOf is canvas-relative, which is WHY the two must be measured separately.
        // A MESH attachment has no quad, so its anchor is the canvas centre -- and the centre of the
        // game's canvas is not the centre of the author's bigger one. That is precisely why the two
        // anchors have to be measured separately.
        var meshPart = new PartSpec("a", "p", "p", new PixelRect(0, 0, 110, 81), false, 0,
            110, 81, 0f, 0f, 0f, 0f, 1f, 1f, 1024, 256);
        (float X, float Y) inGame = RegionGeometry.AnchorOf(meshPart, 110, 81);
        (float X, float Y) inAuthor = RegionGeometry.AnchorOf(meshPart, 130, 90);
        Check("the same part has a different anchor in a different canvas",
              Math.Abs(inGame.X - inAuthor.X) > 0.5f, $"{inGame.X} vs {inAuthor.X}");
    }

    /// <summary>
    /// Two skeletons that share an atlas page share its REGION OBJECTS, so they cannot be given two
    /// different layouts of that page. This measures whether the composer's layout actually depends on
    /// which parts a skeleton asks for — if it does, composing per renderer is unsound.
    /// </summary>
    private static void SharedRegionRules()
    {
        byte[] atlas = new byte[64 * 64 * PixelMath.BytesPerPixel];
        Replacement Part(string name, int w, int h) =>
            new(name, new byte[w * h * PixelMath.BytesPerPixel], w, h);

        var full = new List<Replacement>
        {
            Part("a|hair_1|hair_1", 20, 40),
            Part("a|hair_3|hair_3", 30, 10),
            Part("a|body|body", 16, 16),
            Part("a|hand|hand", 12, 24),
        };
        var fewer = new List<Replacement> { full[0], full[2] };   // what a second skeleton might use

        AtlasComposition whole = AtlasComposer.Compose(atlas, 64, 64, full);
        AtlasComposition part = AtlasComposer.Compose(atlas, 64, 64, fewer);

        PixelRect InWhole(string id) => whole.Placed.First(p => p.Identity == id).Rect;
        PixelRect InPart(string id) => part.Placed.First(p => p.Identity == id).Rect;

        bool sameRect = InWhole("a|hair_1|hair_1").Equals(InPart("a|hair_1|hair_1"));
        Console.WriteLine($"  hair_1 in the full layout {InWhole("a|hair_1|hair_1")}, " +
                          $"in the smaller one {InPart("a|hair_1|hair_1")}");
        Console.WriteLine($"  atlas height {whole.Height} vs {part.Height}");
        Check("a part keeps its rectangle whichever skeleton composed the page", sameRect);

        // The rectangle is stable, but the atlas HEIGHT is not -- and v is the rectangle measured against
        // that height. So the same shared region gets a different v depending on which skeleton composed
        // the page last, while each renderer is bound to its own texture. Whoever is not the last writer
        // samples at the wrong height.
        float vWhole = whole.Placed.First(p => p.Identity == "a|hair_1|hair_1").V;
        float vPart = part.Placed.First(p => p.Identity == "a|hair_1|hair_1").V;
        Console.WriteLine($"  hair_1 v: {vWhole} against height {whole.Height}, {vPart} against {part.Height}");

        // This difference is correct FOR THE COMPOSER -- a smaller set packs shorter -- and it is exactly
        // why RuntimeAtlas keeps one layout per (skin, page) and shares it. The composer is a pure
        // function of its input; the thing that must not vary is which input is used, and that is the
        // caller's job. Recorded here so the next reader does not "fix" the composer.
        Check("v follows the atlas height, which follows the part set (why one layout is shared)",
              Math.Abs(vWhole - vPart) > 1e-6f,
              $"{vWhole} vs {vPart} -- if these were equal the sharing would be unnecessary");

        // Same set, composed twice: must be byte-identical, or even one skeleton is unstable.
        AtlasComposition again = AtlasComposer.Compose(atlas, 64, 64, full);
        Check("composing the same set twice gives the same layout",
              again.Height == whole.Height &&
              again.Placed.All(p => p.Rect.Equals(InWhole(p.Identity))));

        // Order must not matter either: two skeletons may walk their skins in a different order.
        var shuffled = new List<Replacement> { full[3], full[1], full[0], full[2] };
        AtlasComposition reordered = AtlasComposer.Compose(atlas, 64, 64, shuffled);
        Check("the order the parts arrive in does not change the layout",
              reordered.Height == whole.Height &&
              reordered.Placed.All(p => p.Rect.Equals(InWhole(p.Identity))));
    }

    /// <summary>
    /// After a replacement, a region claims no trim and no rotation — and the mesh must be told the same
    /// eight numbers.
    ///
    /// The real case this pins down is char_1_rock/hair_1 from the player's own Bassist dump: the game
    /// packs it 181x176 turned 270 degrees out of a 182x176 canvas, and the dumped PNG is the full
    /// 182x176. While only the region was updated and the mesh kept its own copy, Spine went on mapping
    /// every hair vertex through "one pixel trimmed, turned a quarter turn" — the hair in a song.
    /// </summary>
    private static void ReplacedRegionRules()
    {
        // char_1_rock/hair_1, exactly as the manifest and the PNG on disk have it: the canvas is 182x176
        // and the replacement is the same size, so nothing about the part may move.
        ReplacedRegion hair = RegionGeometry.AfterReplacement(new PixelRect(0, 0, 182, 176), 182, 176);
        Check("hair_1: packed size is the whole canvas", hair.Width == 182 && hair.Height == 176);
        Check("hair_1: no trim is claimed", hair.OriginalWidth == 182 && hair.OriginalHeight == 176);
        Check("hair_1: the trim offset is cleared", hair.OffsetX == 0f && hair.OffsetY == 0f);
        Check("hair_1: the 270-degree rotation is gone", !hair.Rotate && hair.Degrees == 0);

        // The property that makes region and mesh agree: packed == original, always.
        foreach ((int width, int height) in new[] { (1, 1), (181, 176), (65, 152), (2048, 3728) })
        {
            ReplacedRegion any = RegionGeometry.AfterReplacement(new PixelRect(7, 9, width, height), width, height);
            Check($"{width}x{height}: packed equals original, so nothing is trimmed",
                  any.Width == any.OriginalWidth && any.Height == any.OriginalHeight &&
                  any.Width == width && any.Height == height);
            Check($"{width}x{height}: never turned", !any.Rotate && any.Degrees == 0);
            Check($"{width}x{height}: the rectangle's position is not part of the geometry",
                  any.OffsetX == 0f && any.OffsetY == 0f);
        }

        // The canvas-grew case, with the player's own elfin numbers. The quad has to grow by exactly
        // png / canvas and stay centred, or the part is drawn squeezed into the old quad -- the flattened
        // elfin of 2026-11-19.
        foreach ((string part, int canvasW, int canvasH, int pngW, int pngH) in new[]
                 {
                     ("body",     31,  54,  40,  54),
                     ("wing_l",  110,  81, 130,  90),
                     ("head",    115, 138, 138, 143),
                     ("leg_r_a",  13,  33,  14,  33),
                 })
        {
            ReplacedRegion grown = RegionGeometry.AfterReplacement(new PixelRect(0, 0, pngW, pngH), canvasW, canvasH);
            Check($"{part}: the canvas the game knows is kept",
                  grown.OriginalWidth == canvasW && grown.OriginalHeight == canvasH);
            Check($"{part}: the replacement's own size is the packed size",
                  grown.Width == pngW && grown.Height == pngH);

            // Spine: span = attachmentSize * regionWidth / regionOriginalWidth, offset by
            // regionOffset / regionOriginalWidth on each side.
            float spanX = grown.Width / (float)grown.OriginalWidth;
            float leftX = grown.OffsetX / grown.OriginalWidth;
            Check($"{part}: the quad grows by {pngW}/{canvasW}", Math.Abs(spanX - pngW / (float)canvasW) < 1e-6f);
            Check($"{part}: the growth is even on both sides",
                  Math.Abs(leftX + spanX + leftX - 1f) < 1e-6f, $"left {leftX}, span {spanX}");
            Check($"{part}: a grown canvas means a negative trim", grown.OffsetX <= 0f && grown.OffsetY <= 0f);
        }

        // THE invariant. A part that was only redrawn -- same canvas -- must not move by so much as a
        // pixel. 470 of the player's 807 parts were moved on 2026-11-19 by reading the sidecar as if it
        // were a displacement; leg_left_1 went 65 pixels sideways. This is the check that forbids it.
        foreach ((string part, int w, int h) in new[]
                 {
                     ("leg_left_1", 134, 48), ("box_2", 141, 275), ("arm_left", 74, 91),
                     ("guita", 204, 568), ("hand_left_thumb_4", 22, 16),
                 })
        {
            ReplacedRegion still = RegionGeometry.AfterReplacement(new PixelRect(0, 0, w, h), w, h);
            Check($"{part}: an untouched part does not move", still.OffsetX == 0f && still.OffsetY == 0f,
                  $"({still.OffsetX}, {still.OffsetY})");
            Check($"{part}: an untouched part keeps its size",
                  still.Width == w && still.OriginalWidth == w && still.Height == h && still.OriginalHeight == h);
        }

        // The player's six expanded elfin parts, with their REAL numbers. The sidecar anchors are read
        // from SkinCreator\Images\Lilith\elfin_devil\<part>.json; the DEFAULT anchors are the ones
        // SkinForger measured IN GAME and logged on 2026-11-20 14:01 --
        //
        //     head: the game's 115x138 canvas starts at (11.5, 2.5) of a 138x143 picture
        //           (anchor 92,157 against 61.65,130.869)
        //
        // -- which is the only honest source for a quad's default anchor, because it is computed from the
        // attachment the game hands over.
        //
        // A CORRECTION: earlier that day these fixtures used <atlas>\replaced\<part>.json as the default
        // anchor, on the belief that it was what the Dump originally wrote. It is not. It is SkinForger's
        // backup of an EARLIER version of the author's own sidecar -- head's copy carries the author's
        // hand-set offset (-18, 30), which a dumped file never would. For body and wing_r it happens to
        // equal the game's anchor (those two were never hand-edited before the auto-anchor tool rewrote
        // them); for head, wing_l, fork and leg_r_a it does not, and believing it made four parts look
        // describable when they are not.
        //
        //     left = anchorX - defaultAnchorX,   top = anchorY - defaultAnchorY
        //
        // Two of the six land inside their picture and are honoured. The other four do not -- the author
        // enlarged those canvases and never updated their anchors -- so they are centred, which is what
        // the game shows today.
        foreach ((string part, int cw, int ch, int pw, int ph, float ax, float ay,
                  float dx, float dy, float ox, float oy, double wantLeft, double wantTop) in new[]
                 {
                     // honoured: the anchor describes where the game's canvas sits
                     ("body",    31,  54,  40,  54,  19.5f,      27.0f,      15.5f,    27.0f,      0f,  0f,  4.0,  0.0),
                     ("wing_r", 125,  89, 130,  90, 126.3127f,   86.5187f,  121.313f,  85.519f,   -5f,  0f,  5.0,  1.0),
                     // centred: the anchor cannot place the canvas inside the picture
                     ("head",   115, 138, 138, 143,  92.0f,     157.0f,      61.65f,  130.869f,  -18f, 30f, 11.5,  2.5),
                     ("wing_l", 110,  81, 130,  90,   0.0f,     130.0f,       1.027f,  77.353f,  -50f, 20f, 10.0,  4.5),
                     ("fork",   191,  94, 208, 121,  93.0526f,   49.2638f,   61.475f,  25.855f,   70f, 20f,  8.5, 13.5),
                     ("leg_r_a", 13,  33,  14,  33,   6.5213914f, 5.0f,       6.521f,   6.087f,   -7f, -5f,  0.5,  0.0),
                 })
        {
            (double left, double top) = RegionGeometry.CanvasStart(new PixelRect(0, 0, pw, ph), cw, ch,
                ax, ay, dx, dy);
            Check($"{part}: the placement SkinForger logged in game",
                  Math.Abs(left - wantLeft) < 1e-3 && Math.Abs(top - wantTop) < 1e-3, $"({left}, {top})");

            // The author's rigid nudge is Core's PartTransforms business for a mesh and Main's own for a
            // quad, but it is NEVER a canvas shift. Subtracting it -- which Main did on 2026-11-20 --
            // gives a different answer for every part that carries one.
            if (ox != 0f || oy != 0f)
                Check($"{part}: subtracting the nudge is not the same placement",
                      Math.Abs((ax - dx - ox) - left) > 1e-3 || Math.Abs((ay - dy - oy) - top) > 1e-3);

            ReplacedRegion grown = RegionGeometry.AfterReplacement(new PixelRect(0, 0, pw, ph), cw, ch, left, top);
            Check($"{part}: the quad grows by {pw}/{cw}",
                  Math.Abs(grown.Width / (float)grown.OriginalWidth - pw / (float)cw) < 1e-6);
            Check($"{part}: the picture's border is where the placement put it",
                  Math.Abs(grown.OffsetX - (float)-left) < 1e-3 &&
                  Math.Abs(grown.OffsetY - (float)(top + ch - ph)) < 1e-3);
        }

        // What those four anchors WOULD have to say to be honoured. The author enlarged each of these to
        // the right and the bottom (their autoContent bounding boxes start at the picture's top-left), so
        // the canvas starts at (0,0) and the anchor is simply the one the game has. Printed in the report
        // rather than written into anyone's files.
        foreach ((string part, int cw, int ch, int pw, int ph, float dx, float dy) in new[]
                 {
                     ("head",   115, 138, 138, 143,  61.65f,  130.869f),
                     ("wing_l", 110,  81, 130,  90,   1.027f,  77.353f),
                     ("fork",   191,  94, 208, 121,  61.475f,  25.855f),
                     ("leg_r_a", 13,  33,  14,  33,   6.521f,   6.087f),
                 })
        {
            (double left, double top) = RegionGeometry.CanvasStart(new PixelRect(0, 0, pw, ph), cw, ch,
                dx, dy, dx, dy);
            Check($"{part}: the game's own anchor would place the canvas at the top-left",
                  Math.Abs(left) < 1e-6 && Math.Abs(top) < 1e-6, $"({left}, {top})");
        }

        // The regression itself: measuring a QUAD's anchor against the canvas CENTRE throws the canvas
        // out of the picture and recentres a part that was perfectly well described. head and wing_l are
        // the two that showed it worst in game.
        foreach ((string part, int cw, int ch, int pw, int ph, float ax, float ay) in new[]
                 {
                     ("head",   115, 138, 138, 143, 92.0f, 157.0f),
                     ("wing_l", 110,  81, 130,  90,  0.0f, 130.0f),
                 })
        {
            Check($"{part}: the canvas centre is not a quad's default anchor",
                  !RegionGeometry.Inside(ax - cw / 2.0, ay - ch / 2.0, cw, ch, pw, ph));
        }

        // A part whose anchor cannot describe the canvas at all is still centred rather than left to
        // place the picture out of frame -- the same fallback Core's resolver uses.
        (double lostLeft, double lostTop) = RegionGeometry.CanvasStart(new PixelRect(0, 0, 130, 90), 110, 81,
            0f, 0f, 500f, 500f);
        Check("an undescribable canvas is centred",
              Math.Abs(lostLeft - 10.0) < 1e-9 && Math.Abs(lostTop - 4.5) < 1e-9, $"({lostLeft}, {lostTop})");

        // ---- the author's rigid nudge on a QUAD, which only Main can apply
        //
        // SkinForger.Core's PartTransforms moves MESH parts and says it leaves "region parts: unchanged
        // (Main moves region quads by offset itself)". Main did not, so every hand-placed quad ignored
        // its sidecar -- 15 of the player's parts, the elfin's head (-18, 30) and fork (70, 20) included.
        //
        // The test that ties it down is the round trip against QuadAnchor: the anchor says where the bone
        // origin falls inside the picture, so moving the DRAWING right by offsetX px must move the anchor
        // LEFT by exactly offsetX px, and down by offsetY must move it up by offsetY. If the conversion
        // has the wrong scale, the wrong sign or the wrong turn, this cannot hold.
        // SkinForger's own quad rule, ported from the player's ReplacementDispatcher
        // (AnchorFromAttachment / CenterFromAnchor / ApplyGeometry). The elfin's REAL quads, as the game
        // logged them on 2026-11-20, with their real canvases, pictures and hand-tuned numbers.
        foreach ((string part, float x, float y, float w, float h, float rot,
                  int cw, int ch, int pw, int ph, float ax, float ay, float ox, float oy) in new[]
                 {
                     ("head",    0.61f,   0.10f,  1.15f,  1.375f, -84.55f, 115, 138, 138, 143,  92.0f,     157.0f,    -18f, 30f),
                     ("wing_l",  0.625f, -0.192f, 1.1f,   0.81f,  -51.39f, 110,  81, 130,  90,   0.0f,     130.0f,    -50f, 20f),
                     ("wing_r",  0.654f,  0.29f,  1.25f,  0.885f,-121.35f, 125,  89, 130,  90, 126.3127f,   86.5187f,  -5f,  0f),
                     ("fork",    0.339f, -0.211f, 1.905f, 0.94f,    0.00f, 191,  94, 208, 121,  93.0526f,   49.2638f,  70f, 20f),
                     ("leg_r_a", 0.103f,  0.003f, 0.125f, 0.325f,  91.72f,  13,  33,  14,  33,   6.5213914f, 5.0f,     -7f, -5f),
                 })
        {
            (double unitX, double unitY) = RegionGeometry.QuadUnits(w, h, cw, ch);

            // The pair is an exact round trip: ask where the bone origin is, put it back there, and the
            // attachment has not moved. This is what makes "a part with no sidecar never moves" true.
            (float X, float Y) here = RegionGeometry.QuadAnchorIn(x, y, rot, 1f, 1f, cw, ch, unitX, unitY);
            (float X, float Y) back = RegionGeometry.QuadCentre(rot, 1f, 1f, cw, ch, here.X, here.Y,
                0f, 0f, unitX, unitY);
            Check($"{part}: anchor and centre are an exact round trip",
                  Math.Abs(back.X - x) < 1e-4f && Math.Abs(back.Y - y) < 1e-4f,
                  $"({back.X}, {back.Y}) from ({x}, {y}) via ({here.X:0.##}, {here.Y:0.##})");

            // The quad grows with the picture and keeps the drawing's size: picture px x units-per-pixel.
            double grownWidth = pw * unitX, grownHeight = ph * unitY;
            Check($"{part}: the quad grows to the picture, so the drawing keeps its size",
                  Math.Abs(grownWidth / unitX - pw) < 1e-6 && Math.Abs(grownHeight / unitY - ph) < 1e-6);
            Check($"{part}: the quad grows by {pw}/{cw} across",
                  Math.Abs(grownWidth / w - pw / (double)cw) < 1e-6);

            // The author's anchor is honoured AS IT STANDS -- no fit test, no centring. head's (92, 157)
            // is 157 pixels down a 143-pixel picture, and that is allowed: an anchor is where the BONE
            // origin goes, and a bone origin is routinely outside its own drawing.
            (float X, float Y) placed = RegionGeometry.QuadCentre(rot, 1f, 1f, pw, ph, ax, ay,
                0f, 0f, unitX, unitY);
            (float X, float Y) read = RegionGeometry.QuadAnchorIn(placed.X, placed.Y, rot, 1f, 1f,
                pw, ph, unitX, unitY);
            Check($"{part}: the author's anchor ({ax:0.##}, {ay:0.##}) is where the bone origin lands",
                  Math.Abs(read.X - ax) < 0.01f && Math.Abs(read.Y - ay) < 0.01f,
                  $"({read.X:0.##}, {read.Y:0.##})");

            // The nudge is added in the bone's axes, not turned, and is exactly the author's pixels.
            (float X, float Y) nudged = RegionGeometry.QuadCentre(rot, 1f, 1f, pw, ph, ax, ay,
                ox, oy, unitX, unitY);
            Check($"{part}: the nudge ({ox}, {oy}) px is added unturned",
                  Math.Abs((nudged.X - placed.X) - ox * unitX) < 1e-5 &&
                  Math.Abs((nudged.Y - placed.Y) + oy * unitY) < 1e-5,
                  $"moved ({nudged.X - placed.X:0.####}, {nudged.Y - placed.Y:0.####})");
        }

        // A part with no sidecar and no expansion keeps the placement the game gave it, to the bit.
        foreach ((float x, float y, float w, float h, float rot, int cw, int ch) in new[]
                 {
                     (0.115f, -0.023f, 0.285f, 0.235f,  25.50f, 29, 24),
                     (0.133f,  0.017f, 0.22f,  0.33f,  115.26f, 22, 33),
                     (0.0f,    0.0f,   1.0f,   1.0f,     0.00f, 100, 100),
                 })
        {
            (double unitX, double unitY) = RegionGeometry.QuadUnits(w, h, cw, ch);
            (float X, float Y) anchor = RegionGeometry.QuadAnchorIn(x, y, rot, 1f, 1f, cw, ch, unitX, unitY);
            (float X, float Y) back = RegionGeometry.QuadCentre(rot, 1f, 1f, cw, ch, anchor.X, anchor.Y,
                0f, 0f, unitX, unitY);
            Check("an untouched quad comes back to exactly where it was",
                  Math.Abs(back.X - x) < 1e-5f && Math.Abs(back.Y - y) < 1e-5f, $"({back.X}, {back.Y})");
        }

        // THE RULE THE OLD SKINFORGER USED, written out by the player's own build tool
        // (RegionAttachmentFix\Build\Program.cs): an expanded region attachment is given
        //
        //     regionOffsetX / Y   = 0
        //     regionWidth  / Height         = the AUTHOR's picture
        //     regionOriginalWidth / Height  = the AUTHOR's picture
        //
        // so Spine computes the quad as -width/2 .. +width/2 -- the same size, in the same place, with
        // the whole picture drawn into it. The quad NEVER MOVES AND NEVER GROWS, which is why these parts
        // were never once in the wrong place before the cut.
        foreach ((string part, int gw, int gh, int pw, int ph) in new[]
                 {
                     ("head",    115, 138, 138, 143),
                     ("wing_l",  110,  81, 130,  90),
                     ("fork",    191,  94, 208, 121),
                     ("leg_r_a",  13,  33,  14,  33),
                     ("body",     31,  54,  40,  54),
                     ("wing_r",  125,  89, 130,  90),
                     ("bady",   1320,1383,1320,1551),
                     ("hair_1",  182, 176, 182, 176),   // not expanded at all
                 })
        {
            ReplacedRegion shown = RegionGeometry.AfterReplacement(new PixelRect(0, 0, pw, ph), pw, ph);
            Check($"{part}: the region claims no trim at all",
                  shown.OffsetX == 0f && shown.OffsetY == 0f, $"({shown.OffsetX}, {shown.OffsetY})");
            Check($"{part}: the picture IS the canvas",
                  shown.Width == pw && shown.OriginalWidth == pw &&
                  shown.Height == ph && shown.OriginalHeight == ph);

            // Spine's own quad arithmetic, on those numbers: localX = -w/2 + offsetX/originalW * w, and
            // localX2 = localX + width/originalW * w. The quad has to come out unchanged.
            const float w = 2f, h = 3f;      // the attachment's own size, whatever it is
            float localX = -w / 2f + shown.OffsetX / shown.OriginalWidth * w;
            float localX2 = localX + shown.Width / (float)shown.OriginalWidth * w;
            float localY = -h / 2f + shown.OffsetY / shown.OriginalHeight * h;
            float localY2 = localY + shown.Height / (float)shown.OriginalHeight * h;
            Check($"{part}: the quad does not move or grow",
                  Math.Abs(localX + w / 2f) < 1e-5f && Math.Abs(localX2 - w / 2f) < 1e-5f &&
                  Math.Abs(localY + h / 2f) < 1e-5f && Math.Abs(localY2 - h / 2f) < 1e-5f,
                  $"x {localX}..{localX2}, y {localY}..{localY2}");
        }

        // The elfin's REAL quads, as SkinForger measured them in game on 2026-11-20: their sizes, their
        // rotations, and the author's own nudges. The rotations are the point -- the head is spun -84.55
        // degrees and yet drawn upright, which can only mean its BONE carries +84.55 and the two cancel.
        // So the picture's axes are the screen's axes, and turning the author's pixels by the
        // attachment's rotation is the step from the picture's axes into the bone's, where x / y live.
        foreach ((string part, float x, float y, float w, float h, float rot, int cw, int ch,
                  float ox, float oy, double wantScreenX, double wantScreenY) in new[]
                 {
                     ("head",    0.61f,   0.10f,  1.15f,  1.375f, -84.55f, 115, 138, -18f, 30f, -18.0,  30.0),
                     ("wing_l",  0.625f, -0.192f, 1.1f,   0.81f,  -51.39f, 110,  81, -50f, 20f, -50.0,  20.0),
                     ("wing_r",  0.654f,  0.29f,  1.25f,  0.885f,-121.35f, 125,  89,  -5f,  0f,  -5.0,   0.0),
                     ("fork",    0.339f, -0.211f, 1.905f, 0.94f,    0.00f, 191,  94,  70f, 20f,  70.0,  20.0),
                     ("leg_r_a", 0.103f,  0.003f, 0.125f, 0.325f,  91.72f,  13,  33,  -7f, -5f,  -7.0,  -5.0),
                     ("hand_l_b",0.115f, -0.023f, 0.285f, 0.235f,  25.50f,  29,  24, -10f,  0f, -10.0,   0.0),
                 })
        {
            (float X, float Y) nudge = RegionGeometry.QuadNudge(ox, oy, w, h, rot, cw, ch);
            (float nx, float ny, float nrot) = RegionGeometry.QuadPlacement(x, y, rot, nudge.X, nudge.Y, 0f);

            // The bone turns the other way by exactly as much, so undoing the attachment's rotation is
            // what the screen sees. In pixels, that has to come back as the author's own numbers.
            double radians = -rot * Math.PI / 180.0;
            double cos = Math.Cos(radians), sin = Math.Sin(radians);
            double screenX = (nudge.X * cos - nudge.Y * sin) / (w / cw);
            double screenY = -(nudge.X * sin + nudge.Y * cos) / (h / ch);
            Check($"{part}: the author's ({ox}, {oy}) px is ({ox}, {oy}) px on screen",
                  Math.Abs(screenX - wantScreenX) < 0.05 && Math.Abs(screenY - wantScreenY) < 0.05,
                  $"screen ({screenX:0.##}, {screenY:0.##})");

            Check($"{part}: a nudge does not turn the part", Math.Abs(nrot - rot) < 1e-6);

            // Applying it again from the pristine placement lands in the same place; the part never walks.
            (float ax, float ay, float _ar) = RegionGeometry.QuadPlacement(x, y, rot, nudge.X, nudge.Y, 0f);
            Check($"{part}: applying the nudge again does not move it further",
                  Math.Abs(ax - nx) < 1e-9 && Math.Abs(ay - ny) < 1e-9);

            // The anchor round trip: moving the drawing right by offsetX px moves the bone origin left by
            // the same offsetX px, in the picture's own axes, whatever the part is spun by.
            (float X, float Y) before = RegionGeometry.QuadAnchor(x, y, w, h, rot, cw, ch);
            (float X, float Y) after = RegionGeometry.QuadAnchor(nx, ny, w, h, nrot, cw, ch);
            Check($"{part}: the nudge moves the drawing by exactly the author's pixels",
                  Math.Abs((before.X - after.X) - ox) < 0.05 &&
                  Math.Abs((before.Y - after.Y) - oy) < 0.05,
                  $"anchor moved ({before.X - after.X:0.##}, {before.Y - after.Y:0.##}), asked ({ox}, {oy})");
        }

        // The head, in full, against the numbers the game logged. This is the one the player called the
        // worst, and the one that showed the 718B24B4 mistake.
        {
            (float X, float Y) nudge = RegionGeometry.QuadNudge(-18f, 30f, 1.15f, 1.375f, -84.55f, 115, 138);
            Check("head: the nudge in bone space is (-0.316, +0.152)",
                  Math.Abs(nudge.X + 0.316f) < 0.002f && Math.Abs(nudge.Y - 0.152f) < 0.002f,
                  $"({nudge.X:0.####}, {nudge.Y:0.####})");
        }

        // The published directions: negative moves LEFT and UP, so a positive offsetY moves the part DOWN
        // (SkinForge dev log, 2026: "负数往左 / 往上"). Spine's y points up, so down means a smaller y.
        (float X, float Y) down = RegionGeometry.QuadNudge(0f, 30f, 115f, 138f, 0f, 115, 138);
        Check("a positive offsetY moves the part down", down.Y < 0f && Math.Abs(down.Y + 30f) < 1e-3,
              down.Y.ToString());
        (float X, float Y) right = RegionGeometry.QuadNudge(18f, 0f, 115f, 138f, 0f, 115, 138);
        Check("a positive offsetX moves the part right", Math.Abs(right.X - 18f) < 1e-3, right.X.ToString());

        // Nothing asked for, nothing moved -- the invariant for the 800-odd parts with no nudge at all.
        (float X, float Y) unmoved = RegionGeometry.QuadNudge(0f, 0f, 115f, 138f, 0f, 115, 138);
        (float sx, float sy, float srot) = RegionGeometry.QuadPlacement(12.5f, -40f, 7f, unmoved.X, unmoved.Y, 0f);
        Check("a part with no nudge does not move",
              unmoved.X == 0f && unmoved.Y == 0f &&
              Math.Abs(sx - 12.5f) < 1e-6 && Math.Abs(sy + 40f) < 1e-6 && Math.Abs(srot - 7f) < 1e-6);

        // A turn is clockwise on screen, which is negative in Spine, and it is taken about the anchor --
        // the bone origin, which is the point x / y are measured from, so x / y turn with it.
        (float tx, float ty, float trot) = RegionGeometry.QuadPlacement(10f, 0f, 0f, 0f, 0f, 90f);
        Check("a turn is clockwise on screen", Math.Abs(trot + 90f) < 1e-6, trot.ToString());
        Check("a turn carries the quad around its anchor",
              Math.Abs(tx) < 1e-5 && Math.Abs(ty + 10f) < 1e-5, $"({tx}, {ty})");

        // A MESH gets the whole image with no trim, whatever the canvas did. Its shape is its vertices;
        // the six numbers only say which part of the picture to sample, so any claimed trim makes it
        // sample less than the picture and the drawing is pulled inward -- the elfin "挨得更近".
        foreach ((string part, int canvasW, int canvasH, int pngW, int pngH) in new[]
                 {
                     ("body",     31,  54,  40,  54),
                     ("wing_l",  110,  81, 130,  90),
                     ("head",    115, 138, 138, 143),
                     ("hair_1",  182, 176, 182, 176),
                 })
        {
            ReplacedRegion mesh = RegionGeometry.AfterReplacement(new PixelRect(0, 0, pngW, pngH), pngW, pngH);
            Check($"{part}: a mesh samples the whole picture",
                  mesh.Width == pngW && mesh.OriginalWidth == pngW &&
                  mesh.Height == pngH && mesh.OriginalHeight == pngH);
            Check($"{part}: a mesh is told of no trim at all", mesh.OffsetX == 0f && mesh.OffsetY == 0f);

            // The quad form still grows, for the attachments that are quads.
            ReplacedRegion quad = RegionGeometry.AfterReplacement(new PixelRect(0, 0, pngW, pngH), canvasW, canvasH);
            Check($"{part}: a quad still scales by {pngW}/{canvasW}",
                  Math.Abs(quad.Width / (float)quad.OriginalWidth - pngW / (float)canvasW) < 1e-6f);
        }

        // An unknown canvas can only mean "the replacement is the canvas".
        ReplacedRegion unknown = RegionGeometry.AfterReplacement(new PixelRect(0, 0, 64, 64), 0, 0);
        Check("an unmeasured region falls back to no trim",
              unknown.OriginalWidth == 64 && unknown.OffsetX == 0f);
    }

    /// <summary>
    /// The custom loading pictures the player actually installed must be where the feature looks.
    ///
    /// In the old Main the folder was moved into SkinCreator by rewriting a string in the finished binary,
    /// so the source still names the old place. The authored Main read the old place, found it empty, wrote
    /// a fresh template config into it and reported "Loaded 0 loading pictures" while 14 pictures sat in
    /// the new one (2026-11-19 16:31 trial).
    /// </summary>
    private static void LoadingPictureRules(string gameRoot)
    {

        string folder = LoadingPaths.LoadingPictures(gameRoot);
        Console.WriteLine($"  {folder}");
        Check("the folder the feature reads exists", Directory.Exists(folder));
        if (!Directory.Exists(folder)) return;

        string config = Path.Combine(folder, "config.json");
        Check("config.json is in it", File.Exists(config));
        if (!File.Exists(config)) return;

        // Same rule the feature applies: a bare name means <name>.png, next to config.json.
        var names = new List<string>();
        using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(config)))
            if (document.RootElement.TryGetProperty("LoadingPictures", out JsonElement listed))
                foreach (JsonElement entry in listed.EnumerateArray())
                {
                    string name = (entry.GetString() ?? string.Empty).Trim();
                    if (name.Length == 0) continue;
                    names.Add(Path.GetExtension(name).Length == 0 ? name + ".png" : name);
                }

        Check("config.json names at least one picture", names.Count > 0);
        int present = names.Count(name => File.Exists(Path.Combine(folder, name)));
        Check($"every name in config.json is a file in that folder ({present} of {names.Count})",
              names.Count > 0 && present == names.Count,
              string.Join(", ", names.Where(name => !File.Exists(Path.Combine(folder, name)))));

        int pngs = Directory.EnumerateFiles(folder, "*.png").Count();
        Console.WriteLine($"  {names.Count} named, {present} present, {pngs} png file(s) in the folder");
    }

    // ---------------------------------------------------------------- helpers

    private static void Set(byte[] pixels, int width, int x, int y, byte r, byte g, byte b, byte a)
    {
        int i = (y * width + x) * PixelMath.BytesPerPixel;
        pixels[i] = r; pixels[i + 1] = g; pixels[i + 2] = b; pixels[i + 3] = a;
    }

    private static byte At(byte[] pixels, int width, int x, int y, int channel) =>
        pixels[(y * width + x) * PixelMath.BytesPerPixel + channel];
}
