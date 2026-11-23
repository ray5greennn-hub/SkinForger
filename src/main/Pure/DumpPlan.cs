namespace SkinForger.Main.Pure;

/// <summary>
/// Turns the parts of one atlas into everything the Dump will write: a PNG per part, its manifest entry and
/// its anchor sidecar. Pure: it takes the atlas as a top-left RGBA buffer and gives back buffers, so the
/// plan for a real atlas can be built, compared and tested without the game encoding a single PNG.
///
/// Order matters and is kept: the file-name numbering follows the order the parts arrive in, which is the
/// order the skins and slots declare them (see <see cref="DumpNaming"/>). Two dumps of the same costume can
/// therefore number two same-named parts differently, which is why Core matches by the part triple.
/// </summary>
public static class DumpPlan
{
    /// <summary>
    /// The anchor of a part. The default is <see cref="RegionGeometry.AnchorOf"/>, which reproduces the old
    /// Dump's anchors exactly (measured in game, 2026-11-17); a caller may override it to compare.
    /// </summary>
    public delegate (float X, float Y) AnchorOf(PartSpec part, int canvasWidth, int canvasHeight);

    /// <summary>The anchor of a part with no placement of its own.</summary>
    public static (float X, float Y) CanvasCentre(PartSpec part, int canvasWidth, int canvasHeight) =>
        (canvasWidth / 2f, canvasHeight / 2f);

    /// <summary>
    /// Plans every part of one atlas. <paramref name="atlas"/> is the atlas texture as a top-left RGBA
    /// buffer; parts whose rectangle does not fit it are skipped (a grown or swapped atlas).
    /// </summary>
    public static List<PlannedPart> ForAtlas(IEnumerable<PartSpec> parts, byte[] atlas, int atlasWidth, int atlasHeight,
        AnchorOf? anchorOf = null, Action<PartSpec, string>? skipped = null)
    {
        anchorOf ??= RegionGeometry.AnchorOf;
        var naming = new DumpNaming();
        var planned = new List<PlannedPart>();

        foreach (PartSpec part in parts)
        {
            if (DumpNaming.IsCensoredName(part.AttachmentName) || DumpNaming.IsCensoredName(part.RegionName) ||
                DumpNaming.IsCensoredName(part.Atlas))
            {
                skipped?.Invoke(part, "censored");
                continue;
            }
            if (part.Stored.IsEmpty || !part.Stored.FitsIn(atlasWidth, atlasHeight))
            {
                skipped?.Invoke(part, $"rectangle {part.Stored} is outside the {atlasWidth}x{atlasHeight} atlas");
                continue;
            }
            if (part.OriginalWidth <= 0 || part.OriginalHeight <= 0)
            {
                skipped?.Invoke(part, "no untrimmed size");
                continue;
            }

            byte[] image = RegionGeometry.BuildPartImage(atlas, atlasWidth, atlasHeight, part.Stored, part.Degrees,
                part.Rotate, part.OffsetX, part.OffsetY, part.OriginalWidth, part.OriginalHeight,
                out int width, out int height);

            string fileName = naming.NextFileName(part.Atlas, part.AttachmentName);
            (int logicalWidth, int logicalHeight) = part.LogicalSize;
            RegionManifestEntry entry = DumpNaming.Entry(part.Atlas, fileName, part.AttachmentName, part.RegionName,
                new PixelRect(part.Stored.X, part.Stored.Y, logicalWidth, logicalHeight),
                part.Rotate, part.Degrees, part.OriginalWidth, part.OriginalHeight,
                part.OffsetX, part.OffsetY, part.U, part.V, part.U2, part.V2);

            (float anchorX, float anchorY) = anchorOf(part, width, height);
            RegionAnchorFile anchor = RegionAnchorFile.ForDump(anchorX, anchorY, 0f);

            planned.Add(new PlannedPart(part, fileName, DumpNaming.ManifestFile(part.Atlas, fileName),
                image, width, height, entry, anchor));
        }
        return planned;
    }

    /// <summary>
    /// The manifest for a whole costume: the entries of every atlas planned for it, in order. An existing
    /// manifest is merged, so dumping one atlas does not drop the atlases dumped before it.
    /// </summary>
    public static RegionManifest Manifest(IEnumerable<PlannedPart> planned, RegionManifest? existing = null)
    {
        RegionManifest manifest = existing ?? new RegionManifest();
        manifest.Version = RegionManifest.CurrentVersion;
        foreach (PlannedPart part in planned) manifest.Put(part.Entry);
        return manifest;
    }

    /// <summary>
    /// Entries of an existing manifest that belong to atlases being re-dumped but that the new plan does not
    /// contain any more (a part the game no longer has). They are reported rather than removed silently.
    /// </summary>
    public static List<RegionManifestEntry> Stale(RegionManifest existing, IEnumerable<PlannedPart> planned)
    {
        var atlases = new HashSet<string>(planned.Select(p => p.Part.Atlas), StringComparer.OrdinalIgnoreCase);
        var kept = new HashSet<string>(planned.Select(p => p.Entry.Identity), StringComparer.Ordinal);
        return existing.Regions
            .Where(entry => atlases.Contains(entry.Atlas) && !kept.Contains(entry.Identity))
            .ToList();
    }
}
