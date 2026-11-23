namespace SkinForger.Preview;

internal static class PreviewSharedCoreOriginalContractDecision
{
    internal static bool IsFiniteUv(float value) =>
        !float.IsNaN(value) && !float.IsInfinity(value);

    internal static bool ShouldRestore(
        int pageTextureMismatches,
        int invalidRegionUvs,
        int invalidAttachmentUvs) =>
        pageTextureMismatches > 0 || invalidRegionUvs > 0 || invalidAttachmentUvs > 0;
}
