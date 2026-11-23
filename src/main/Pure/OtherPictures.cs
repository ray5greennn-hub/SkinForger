using System.Text.Json;

namespace SkinForger.Main.Pure;

/// <summary>One picture the author put in Images\Other, ready to be matched against a live texture.</summary>
/// <param name="Stem">The file name without .png — what the texture was called when it was dumped.</param>
/// <param name="File">Full path to the PNG.</param>
/// <param name="DumpedWidth">The size the texture had when it was dumped, 0 when the sidecar does not say.</param>
public readonly record struct OtherPicture(string Stem, string File, int DumpedWidth, int DumpedHeight,
    string Kind);

/// <summary>
/// Images\Other: the pictures the game showed, dumped by name, and now put back.
///
/// Until 2026-11-20 this folder was WRITE-ONLY. SkinForger's "pictures seen on screen" list could dump a
/// texture into it, and nothing anywhere read it back: the whole-repository search for the folder name
/// found four uses and every one of them wrote. Worse, `Other` is not a reserved folder name, so the Main
/// listed it as if it were a COSTUME — and no costume in the game is called Other, so it could never be
/// selected either. An author could edit those pictures all day and never see a change.
///
/// What makes these different from a costume's parts: a part is found through a skeleton and an atlas,
/// while these are found by the texture's own NAME. That is the only thing the dump records, and it is
/// the only thing available again at runtime.
///
/// Pure (no Unity): the matching and the rectangle arithmetic are decided here so they can be checked
/// offline, and the Unity side only walks the scene and assigns.
/// </summary>
public static class OtherPictures
{
    /// <summary>The folder, under Images. Reserved: it is not a costume, whatever its name looks like.</summary>
    public const string FolderName = "Other";

    /// <summary>
    /// A texture name as the dump turns it into a file name.
    ///
    /// The dump replaces every character Windows forbids in a file name with '_', so a texture called
    /// "Font Texture" becomes "Font Texture.png" and one called "UI/Thing" becomes "UI_Thing.png".
    /// Matching has to undo nothing — it just applies the same rule to the live name and compares.
    /// </summary>
    public static string FileStemOf(string? textureName)
    {
        if (string.IsNullOrEmpty(textureName)) return string.Empty;
        char[] invalid = Path.GetInvalidFileNameChars();
        string name = textureName!;
        foreach (char bad in invalid) name = name.Replace(bad, '_');
        return name;
    }

    /// <summary>Whether a live texture is the one this picture was dumped from.</summary>
    public static bool Matches(OtherPicture picture, string? textureName) =>
        !string.IsNullOrEmpty(picture.Stem) &&
        string.Equals(picture.Stem, FileStemOf(textureName), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the sidecar the dump writes beside the picture. Everything in it is optional: a picture with
    /// no sidecar, or a hand-broken one, still works — it just has no recorded original size.
    /// </summary>
    public static OtherPicture Describe(string pngPath, byte[]? sidecarJson)
    {
        string stem = Path.GetFileNameWithoutExtension(pngPath);
        int width = 0, height = 0;
        string kind = string.Empty;
        if (sidecarJson is { Length: > 0 })
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(sidecarJson);
                JsonElement root = document.RootElement;
                if (root.TryGetProperty("dumpedWidth", out JsonElement w) && w.TryGetInt32(out int value))
                    width = value;
                if (root.TryGetProperty("dumpedHeight", out JsonElement h) && h.TryGetInt32(out int value2))
                    height = value2;
                if (root.TryGetProperty("kind", out JsonElement k) && k.ValueKind == JsonValueKind.String)
                    kind = k.GetString() ?? string.Empty;
            }
            catch (JsonException) { /* a hand-edited sidecar must not cost the author their picture */ }
        }
        return new OtherPicture(stem, pngPath, width, height, kind);
    }

    /// <summary>
    /// The sprite rectangle to use after the texture was replaced, so the picture stays the same SIZE on
    /// screen whatever size the author drew it.
    ///
    /// A sprite names a rectangle of texels inside its texture. Hand it a bigger texture and the same
    /// rectangle now covers a smaller part of the drawing; scaling the rectangle by how much the texture
    /// grew keeps it covering the same part. For the ordinary case — the author edited the picture at the
    /// size it was dumped — the factor is 1 and the rectangle is untouched, to the bit.
    /// </summary>
    public static PixelRect SpriteRect(PixelRect original, int fromWidth, int fromHeight,
        int toWidth, int toHeight)
    {
        if (fromWidth <= 0 || fromHeight <= 0 || toWidth <= 0 || toHeight <= 0) return original;
        if (fromWidth == toWidth && fromHeight == toHeight) return original;

        double across = toWidth / (double)fromWidth, down = toHeight / (double)fromHeight;
        int left = (int)Math.Round(original.X * across, MidpointRounding.AwayFromZero);
        int top = (int)Math.Round(original.Y * down, MidpointRounding.AwayFromZero);
        int width = (int)Math.Round(original.Width * across, MidpointRounding.AwayFromZero);
        int height = (int)Math.Round(original.Height * down, MidpointRounding.AwayFromZero);

        // Never leave the texture, and never collapse to nothing.
        width = Math.Clamp(width, 1, toWidth);
        height = Math.Clamp(height, 1, toHeight);
        left = Math.Clamp(left, 0, toWidth - width);
        top = Math.Clamp(top, 0, toHeight - height);
        return new PixelRect(left, top, width, height);
    }

    /// <summary>
    /// How much a sprite's pixels-per-unit must change so a resized picture still measures the same in
    /// world units. A sprite's world size is its rectangle divided by this, so growing the rectangle by a
    /// factor and the pixels-per-unit by the same factor leaves the size alone.
    /// </summary>
    public static float PixelsPerUnit(float original, int fromWidth, int toWidth)
    {
        if (original <= 0f || fromWidth <= 0 || toWidth <= 0 || fromWidth == toWidth) return original;
        return original * (toWidth / (float)fromWidth);
    }
}
