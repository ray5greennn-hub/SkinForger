using System.IO;
using System;
namespace SkinForger.Main.Pure;

/// <summary>
/// How the Dump names what it writes. These rules are FROZEN: players' existing folders, every published
/// .sfskin, and Core's part matching all depend on them.
///
/// They were DERIVED FROM THE REAL FILES, not from the old Main's code: 1134 manifest entries across four
/// costumes (Bassist, Bunny Girl, Lilith, Maid) on 2026-11-17, every rule below with zero violations.
/// Tests\ re-checks them against whatever folders are on the machine it runs on.
///
/// The rules:
///   1. One folder per atlas, one PNG per part:  &lt;Costume&gt;\&lt;atlas&gt;\&lt;file&gt;.png
///      and the manifest's "file" is exactly "&lt;atlas&gt;\&lt;file&gt;.png" (backslash, two segments).
///   2. displayName = the last '/' segment of the attachment name. Spine attachment and region names can
///      carry a path-like prefix ("1_images_rock/body" -> "body"); the file is named after the tail only.
///   3. The PNG's stem is the displayName, and gets "_2", "_3" ... when a later part in the SAME atlas
///      folder wants a name that is already taken (Bassist char_1_rock_fail has two hair_B: one from
///      1_images_rock, one from 1_images_bunny -> hair_B.png and hair_B_2.png). A name used once never
///      carries a suffix.
///   4. The numbering follows the order the parts are dumped in, so it can differ between two dumps of the
///      same costume. That is why Core matches images by the part triple (atlas|region|attachment) and
///      never by file name.
///
/// Also observed, and required of the new Dump:
///   - a censored (hexie) part may be listed in an older manifest while its PNG is absent, because
///     HexieBlock refuses to write it. Reading a manifest must tolerate a missing file.
///   - "replaced\" (the incremental backups) and "SkinSwitchIcons\" (selection thumbnails) are not parts
///     and never belong in the manifest.
/// Pure (no Unity).
/// </summary>
public sealed class DumpNaming
{
    /// <summary>Folder of the incremental backups of images the creator replaced; never a part.</summary>
    public const string ReplacedFolder = "replaced";
    /// <summary>Folder of the selection thumbnails; never a part.</summary>
    public const string ThumbnailFolder = "SkinSwitchIcons";

    /// <summary>Names already used per atlas folder, lower case (the suffix is decided per folder).</summary>
    private readonly Dictionary<string, HashSet<string>> _taken = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The display name of a part: the last segment of the attachment name, made safe as a file name.
    /// </summary>
    /// <summary>
    /// A frame-by-frame ("cat") atlas, recognised the way the whole project has always recognised one:
    /// its resource name carries "_cat". The game's config has NO isCat field -- that was measured on
    /// 2026-11-16 ("配置里皮肤字段没有 isCat / skinType，是靠资源名 _cat 认出来的") -- so the name is the
    /// only thing there is to go on, and SkinForger.Core's RoleSkins uses the same rule.
    ///
    /// It decides one thing: such a folder keeps ONE anchors.json for all its frames instead of a JSON
    /// per image. 2026-11-21: Main used to ask Core for this through a hook, Core answered from a list it
    /// builds while scanning prefabs, and for a folder dumped straight after deletion the answer came back
    /// false -- three times in a row. Main now answers for itself and the hook stays as a second opinion.
    /// </summary>
    public static bool IsFrameByFrameAtlas(string? atlasOrFolder)
    {
        string name = (atlasOrFolder ?? string.Empty).TrimEnd('\\', '/');
        name = Path.GetFileName(name);
        return name.IndexOf("_cat", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static string DisplayNameOf(string? attachmentName)
    {
        string name = (attachmentName ?? string.Empty).Replace('\\', '/');
        int slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];
        return MainLayout.SafeFileName(name);
    }

    /// <summary>
    /// The PNG file name for the next part of an atlas: the display name, with "_2", "_3" ... when that
    /// name is already used in this atlas folder. Call once per part, in dump order.
    /// </summary>
    public string NextFileName(string atlas, string attachmentName)
    {
        string display = DisplayNameOf(attachmentName);
        if (!_taken.TryGetValue(atlas, out HashSet<string>? used))
            _taken[atlas] = used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string candidate = display;
        for (int suffix = 2; !used.Add(candidate); suffix++)
            candidate = display + "_" + suffix;
        return candidate + ".png";
    }

    /// <summary>Forgets what has been used (a new dump of a costume starts its numbering from scratch).</summary>
    public void Reset() => _taken.Clear();

    /// <summary>The manifest's "file" value: "&lt;atlas&gt;\&lt;file&gt;.png", always with a backslash.</summary>
    public static string ManifestFile(string atlas, string fileName) => atlas + "\\" + fileName;

    /// <summary>
    /// Builds the manifest entry of a part. <paramref name="fileName"/> comes from
    /// <see cref="NextFileName"/>, so the suffix and the entry always agree.
    /// </summary>
    public static RegionManifestEntry Entry(string atlas, string fileName, string attachmentName, string regionName,
        PixelRect packedRect, bool rotate, int degrees, int originalWidth, int originalHeight,
        float offsetX, float offsetY, float u, float v, float u2, float v2) => new()
        {
            File = ManifestFile(atlas, fileName),
            Atlas = atlas,
            AttachmentName = attachmentName,
            RegionName = regionName,
            DisplayName = DisplayNameOf(attachmentName),
            X = packedRect.X,
            Y = packedRect.Y,
            Width = packedRect.Width,
            Height = packedRect.Height,
            Rotate = rotate,
            Degrees = degrees,
            OriginalWidth = originalWidth,
            OriginalHeight = originalHeight,
            OffsetX = offsetX,
            OffsetY = offsetY,
            U = u,
            V = v,
            U2 = u2,
            V2 = v2
        };

    /// <summary>
    /// True when a path inside a costume folder is a part image: a PNG directly in an atlas folder, and not
    /// in "replaced\", not a thumbnail, not a censored name.
    /// </summary>
    public static bool IsPartImage(string relativePath)
    {
        string[] parts = (relativePath ?? string.Empty).Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;
        if (!parts[1].EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return false;
        if (parts[0].Equals(ReplacedFolder, StringComparison.OrdinalIgnoreCase)) return false;
        if (parts[0].Equals(ThumbnailFolder, StringComparison.OrdinalIgnoreCase)) return false;
        return !IsCensoredName(parts[0]) && !IsCensoredName(parts[1]);
    }

    /// <summary>The censored-content rule, the same word HexieBlock and the package exporter use.</summary>
    public static bool IsCensoredName(string? value) =>
        !string.IsNullOrEmpty(value) && value!.Contains("hexie", StringComparison.OrdinalIgnoreCase);
}
