using System.Collections.Generic;

namespace SkinForger.Preview;

internal enum PreviewSharedCoreDisposition
{
    OriginalOnly,
    Replacement
}

internal static class PreviewSharedCoreDecision
{
    internal static PreviewSharedCoreDisposition Decide(int validTargetCount) =>
        validTargetCount <= 0
            ? PreviewSharedCoreDisposition.OriginalOnly
            : PreviewSharedCoreDisposition.Replacement;

    internal static string ChooseSkinPath(string? selectedPath, IReadOnlyList<string> registeredPaths)
    {
        return string.IsNullOrEmpty(selectedPath) ? string.Empty : selectedPath;
    }
}
