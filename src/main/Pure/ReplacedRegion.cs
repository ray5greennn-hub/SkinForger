namespace SkinForger.Main.Pure;

/// <summary>
/// The geometry an atlas region and its mesh attachments carry after a replacement image has been placed.
/// See <see cref="RegionGeometry.AfterReplacement"/> for why all eight numbers travel together.
/// </summary>
public sealed record ReplacedRegion(
    int Width, int Height,
    int OriginalWidth, int OriginalHeight,
    float OffsetX, float OffsetY,
    bool Rotate, int Degrees);
