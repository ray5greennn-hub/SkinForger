namespace SkinForger.Main.Pure;

/// <summary>One difference between the old Dump's output and the authored one.</summary>
/// <param name="Kind">Short machine-readable kind, e.g. "missing", "manifest", "pixels", "anchor".</param>
public sealed record DumpDifference(string Kind, string Part, string Detail)
{
    public override string ToString() => $"[{Kind}] {Part}: {Detail}";
}

/// <summary>
/// Compares what the authored Dump produced with what the old Dump left on disk. This is the acceptance
/// test of the whole Dump port (SF日志\SF切割SkinChanger计划.md, stage 3): the authored Dump writes into a
/// SHADOW folder, nothing the player owns is touched, and the two are compared part by part.
///
/// PNG files are never compared byte for byte: two encoders produce different bytes for the same picture.
/// Pixels are compared after decoding, which needs Unity, so the caller passes the decoded buffers in;
/// everything that can be judged without decoding (the manifest, the sidecars, which files exist) is
/// compared here. Pure, so Tests\ can check the comparison itself.
/// </summary>
public static class DumpComparison
{
    /// <summary>Decoded pixels of a PNG, or null when it could not be read.</summary>
    public delegate byte[]? PixelsOf(string path, out int width, out int height);

    /// <summary>
    /// Compares the manifests. Entries are matched by part identity (atlas|region|attachment), never by file
    /// name, because the numbering of two same-named parts may differ between dumps — a different name for
    /// the same part is reported as information, not as a difference.
    /// </summary>
    public static List<DumpDifference> Manifests(RegionManifest old, RegionManifest authored,
        IReadOnlyCollection<string>? atlasesInScope = null)
    {
        var differences = new List<DumpDifference>();
        bool InScope(RegionManifestEntry entry) =>
            atlasesInScope == null || atlasesInScope.Contains(entry.Atlas, StringComparer.OrdinalIgnoreCase);

        List<RegionManifestEntry> oldEntries = old.Regions.Where(InScope).ToList();
        List<RegionManifestEntry> newEntries = authored.Regions.Where(InScope).ToList();
        var newByIdentity = newEntries.GroupBy(e => e.Identity, StringComparer.Ordinal)
                                      .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var oldIdentities = new HashSet<string>(oldEntries.Select(e => e.Identity), StringComparer.Ordinal);

        foreach (RegionManifestEntry entry in oldEntries)
        {
            if (!newByIdentity.TryGetValue(entry.Identity, out RegionManifestEntry? mine))
            {
                // A censored part is listed by the old Dump but never written; the authored Dump omits it.
                string kind = DumpNaming.IsCensoredName(entry.File) || DumpNaming.IsCensoredName(entry.AttachmentName)
                    ? "censored-omitted" : "missing";
                differences.Add(new DumpDifference(kind, entry.Identity, $"not in the authored manifest (was {entry.File})"));
                continue;
            }
            Compare(differences, entry, mine);
        }
        foreach (RegionManifestEntry entry in newEntries.Where(e => !oldIdentities.Contains(e.Identity)))
            differences.Add(new DumpDifference("extra", entry.Identity, $"only in the authored manifest ({entry.File})"));

        return differences;
    }

    private static void Compare(List<DumpDifference> into, RegionManifestEntry old, RegionManifestEntry mine)
    {
        void Field(string name, object a, object b)
        {
            if (!Equals(a, b)) into.Add(new DumpDifference("manifest", old.Identity, $"{name}: old {a}, authored {b}"));
        }
        Field("x", old.X, mine.X);
        Field("y", old.Y, mine.Y);
        Field("width", old.Width, mine.Width);
        Field("height", old.Height, mine.Height);
        Field("rotate", old.Rotate, mine.Rotate);
        Field("degrees", old.Degrees, mine.Degrees);
        Field("originalWidth", old.OriginalWidth, mine.OriginalWidth);
        Field("originalHeight", old.OriginalHeight, mine.OriginalHeight);
        Field("offsetX", old.OffsetX, mine.OffsetX);
        Field("offsetY", old.OffsetY, mine.OffsetY);
        Field("u", old.U, mine.U);
        Field("v", old.V, mine.V);
        Field("u2", old.U2, mine.U2);
        Field("v2", old.V2, mine.V2);
        Field("displayName", old.DisplayName, mine.DisplayName);
        if (!RegionManifest.Normalize(old.File).Equals(RegionManifest.Normalize(mine.File), StringComparison.Ordinal))
            into.Add(new DumpDifference("renumbered", old.Identity, $"file: old {old.File}, authored {mine.File}"));
    }

    /// <summary>
    /// Compares the images and sidecars of a plan against the files the old Dump left in
    /// <paramref name="oldCostumeFolder"/>. <paramref name="pixelsOf"/> decodes a PNG (Unity does this);
    /// pass null to compare sizes and sidecars only.
    /// </summary>
    public static List<DumpDifference> Files(IEnumerable<PlannedPart> planned, string oldCostumeFolder,
        PixelsOf? pixelsOf = null, int tolerance = 0)
    {
        var differences = new List<DumpDifference>();
        foreach (PlannedPart part in planned)
        {
            string oldPng = Path.Combine(oldCostumeFolder, part.RelativePath);
            if (!SkinFiles.Exists(oldPng))
            {
                differences.Add(new DumpDifference("missing-file", part.Part.Identity, $"{part.RelativePath} is not in the old dump"));
                continue;
            }

            if (pixelsOf != null)
            {
                byte[]? oldPixels = pixelsOf(oldPng, out int oldWidth, out int oldHeight);
                if (oldPixels == null)
                {
                    differences.Add(new DumpDifference("unreadable", part.Part.Identity, $"{part.RelativePath} could not be decoded"));
                }
                else if (oldWidth != part.Width || oldHeight != part.Height)
                {
                    // An enlarged canvas is the creator's own work, not a difference in the Dump.
                    string kind = oldWidth >= part.Width && oldHeight >= part.Height ? "enlarged-canvas" : "size";
                    differences.Add(new DumpDifference(kind, part.Part.Identity,
                        $"{part.RelativePath}: old {oldWidth}x{oldHeight}, authored {part.Width}x{part.Height}"));
                }
                else
                {
                    int differing = CountDifferingPixels(oldPixels, part.Image, tolerance);
                    if (differing > 0)
                        differences.Add(new DumpDifference("pixels", part.Part.Identity,
                            $"{part.RelativePath}: {differing} of {part.Width * part.Height} pixels differ by more than {tolerance}"));
                }
            }

            RegionAnchorFile? oldAnchor = RegionAnchorFile.ReadIfExists(oldPng);
            if (oldAnchor == null)
            {
                differences.Add(new DumpDifference("missing-anchor", part.Part.Identity, $"{part.RelativePath} has no sidecar in the old dump"));
                continue;
            }
            if (Math.Abs(oldAnchor.AnchorX - part.Anchor.AnchorX) > 0.01f ||
                Math.Abs(oldAnchor.AnchorY - part.Anchor.AnchorY) > 0.01f)
                differences.Add(new DumpDifference("anchor", part.Part.Identity,
                    $"old ({oldAnchor.AnchorX}, {oldAnchor.AnchorY}), authored ({part.Anchor.AnchorX}, {part.Anchor.AnchorY})"));
        }
        return differences;
    }

    /// <summary>Pixels whose any channel differs by more than <paramref name="tolerance"/>.</summary>
    public static int CountDifferingPixels(byte[] a, byte[] b, int tolerance = 0)
    {
        if (a.Length != b.Length) return Math.Max(a.Length, b.Length) / PixelMath.BytesPerPixel;
        int differing = 0;
        for (int i = 0; i < a.Length; i += PixelMath.BytesPerPixel)
        {
            for (int channel = 0; channel < PixelMath.BytesPerPixel; channel++)
                if (Math.Abs(a[i + channel] - b[i + channel]) > tolerance) { differing++; break; }
        }
        return differing;
    }

    /// <summary>A short report: how many differences of each kind, worst kinds first, then up to 20 lines.</summary>
    public static string Report(IReadOnlyList<DumpDifference> differences)
    {
        if (differences.Count == 0) return "identical: no differences";
        var byKind = differences.GroupBy(d => d.Kind).OrderByDescending(g => g.Count()).ToList();
        var text = new System.Text.StringBuilder();
        text.Append(differences.Count).Append(" difference(s): ");
        text.AppendJoin(", ", byKind.Select(g => $"{g.Key} x{g.Count()}"));
        foreach (DumpDifference difference in differences.Take(20)) text.Append('\n').Append("  ").Append(difference);
        if (differences.Count > 20) text.Append($"\n  ... and {differences.Count - 20} more");
        return text.ToString();
    }

    /// <summary>
    /// True when nothing in the list means the authored Dump got something wrong. A censored part the old
    /// Dump listed but never wrote, a canvas the creator enlarged, and a part renumbered because the order
    /// of two same-named parts differs are all expected.
    /// </summary>
    public static bool IsClean(IReadOnlyList<DumpDifference> differences) =>
        differences.All(d => d.Kind is "censored-omitted" or "enlarged-canvas" or "renumbered");
}
