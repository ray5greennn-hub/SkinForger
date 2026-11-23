namespace SkinForger.Main.Pure;

/// <summary>
/// One part of a skeleton as the Dump needs it: plain data, read off the Spine objects once and then
/// independent of them. Pure, so the whole Dump plan can be built and checked outside the game.
/// </summary>
public sealed record PartSpec(
    string Atlas,
    string AttachmentName,
    string RegionName,
    PixelRect Stored,
    bool Rotate,
    int Degrees,
    int OriginalWidth,
    int OriginalHeight,
    float OffsetX,
    float OffsetY,
    float U,
    float V,
    float U2,
    float V2,
    int PageWidth,
    int PageHeight,
    // ---- the attachment's own placement, when it is a region (quad) attachment. The anchor is computed
    // from these (RegionGeometry.QuadAnchor); a mesh attachment has none and is anchored by its centre.
    bool HasQuad = false,
    float QuadX = 0f,
    float QuadY = 0f,
    float QuadWidth = 0f,
    float QuadHeight = 0f,
    float QuadRotation = 0f)
{
    public string Identity => RegionManifest.PartIdentity(Atlas, RegionName, AttachmentName);

    /// <summary>The trimmed size of the part as drawn (manifest width / height).</summary>
    public (int Width, int Height) LogicalSize => RegionGeometry.LogicalSize(Stored, Rotate);
}

/// <summary>
/// What the Dump will write for one part: the PNG's path and pixels, its manifest entry, and the anchor
/// sidecar beside it. Built by <see cref="DumpPlan"/> before anything touches the disk, so the whole plan
/// can be inspected, compared or thrown away.
/// </summary>
public sealed record PlannedPart(
    PartSpec Part,
    string FileName,
    string RelativePath,
    byte[] Image,
    int Width,
    int Height,
    RegionManifestEntry Entry,
    RegionAnchorFile Anchor);
