using System.Security.Cryptography;
using System.Text;

namespace SkinForger.GameplayAtlasWarmReuse;

internal enum AlphaEncoding
{
    Unknown,
    Straight,
    Premultiplied,
    Mixed
}

internal static class PreparedAtlasCachePolicy
{
    internal static string NormalizePath(string path) => string.IsNullOrWhiteSpace(path)
        ? string.Empty
        : Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    internal static string BuildKey(
        string skinPath,
        string textureName,
        string sourceName,
        int sourceWidth,
        int sourceHeight,
        string sourceFormat) => string.Join("|",
            NormalizePath(skinPath).ToUpperInvariant(),
            (textureName ?? string.Empty).ToUpperInvariant(),
            sourceName ?? string.Empty,
            sourceWidth + "x" + sourceHeight,
            sourceFormat ?? string.Empty);

    internal static string BuildInputFingerprint(string skinPath)
    {
        string normalized = NormalizePath(skinPath);
        if (string.IsNullOrEmpty(normalized) || !global::SkinForger.SkinPackages.VirtualFiles.DirectoryExists(normalized)) return string.Empty;

        string[] files = global::SkinForger.SkinPackages.VirtualFiles.GetFiles(normalized, "*", SearchOption.AllDirectories)   // includes .sfskin packages (in memory)
            .Where(path => IsReplacementInput(path))
            .Select(Path.GetFullPath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0) return string.Empty;

        var text = new StringBuilder(files.Length * 96);
        foreach (string file in files)
        {
            var info = new FileInfo(file);
            if (!global::SkinForger.SkinPackages.VirtualFiles.InfoExists(info)) return string.Empty;
            text.Append(file.ToUpperInvariant()).Append('|')
                .Append(global::SkinForger.SkinPackages.VirtualFiles.FileLength(info)).Append('|')
                .Append(global::SkinForger.SkinPackages.VirtualFiles.LastWriteTimeUtc(info).Ticks).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    internal static bool CanReuse(string cachedFingerprint, string currentFingerprint) =>
        !string.IsNullOrEmpty(cachedFingerprint) &&
        string.Equals(cachedFingerprint, currentFingerprint, StringComparison.Ordinal);

    internal static string BuildAlphaCompatibleKey(string baseKey, AlphaEncoding consumerEncoding) =>
        string.IsNullOrEmpty(baseKey) || !IsUniform(consumerEncoding)
            ? string.Empty
            : baseKey + "|ALPHA=" + consumerEncoding.ToString().ToUpperInvariant();

    internal static string BuildCacheIdentity(
        string baseKey,
        bool alphaGuarded,
        AlphaEncoding consumerEncoding) =>
        alphaGuarded ? BuildAlphaCompatibleKey(baseKey, consumerEncoding) : baseKey;

    internal static bool CanReuse(
        string cachedFingerprint,
        string currentFingerprint,
        AlphaEncoding snapshotEncoding,
        AlphaEncoding consumerEncoding) =>
        CanReuse(cachedFingerprint, currentFingerprint) &&
        IsUniform(snapshotEncoding) &&
        snapshotEncoding == consumerEncoding;

    internal static AlphaEncoding ResolveAppliedEncoding(bool? pmaDecision, bool pmaExecuted)
    {
        if (!pmaDecision.HasValue || pmaDecision.Value != pmaExecuted) return AlphaEncoding.Unknown;
        return pmaDecision.Value ? AlphaEncoding.Premultiplied : AlphaEncoding.Straight;
    }

    internal static bool TryResolvePixelWriteEncoding(
        bool succeeded,
        bool? pmaDecision,
        bool pmaExecuted,
        out AlphaEncoding encoding)
    {
        encoding = AlphaEncoding.Unknown;
        if (!succeeded) return false;

        // ApplyRegionPngToAtlas returns true for an already committed placement
        // without reading, transforming, or writing pixels again. Such calls do
        // not execute ShouldUsePremultipliedAlpha and are not additional Alpha
        // evidence. Fresh writes always execute that decision in the host.
        if (!pmaDecision.HasValue && !pmaExecuted) return false;

        encoding = ResolveAppliedEncoding(pmaDecision, pmaExecuted);
        return true;
    }

    internal static AlphaEncoding ResolveSnapshotEncoding(
        AlphaEncoding consumerEncoding,
        IReadOnlyCollection<AlphaEncoding> placementEncodings)
    {
        if (!IsUniform(consumerEncoding) || placementEncodings.Count == 0) return AlphaEncoding.Unknown;
        if (placementEncodings.Any(value => value is AlphaEncoding.Unknown or AlphaEncoding.Mixed))
            return AlphaEncoding.Unknown;

        bool straight = placementEncodings.Contains(AlphaEncoding.Straight);
        bool premultiplied = placementEncodings.Contains(AlphaEncoding.Premultiplied);
        if (straight && premultiplied) return AlphaEncoding.Mixed;
        AlphaEncoding actual = premultiplied ? AlphaEncoding.Premultiplied : AlphaEncoding.Straight;
        return actual == consumerEncoding ? actual : AlphaEncoding.Unknown;
    }

    internal static bool IsUniform(AlphaEncoding encoding) =>
        encoding is AlphaEncoding.Straight or AlphaEncoding.Premultiplied;

    internal static string BuildInvocationKey(long skeletonPointer, string effectiveAtlasKey) =>
        skeletonPointer <= 0 || string.IsNullOrEmpty(effectiveAtlasKey)
            ? string.Empty
            : skeletonPointer.ToString("X") + "|" + effectiveAtlasKey;

    internal static bool CanDeduplicateBuild(string invocationKey, int completedAtlasId, int currentAtlasId) =>
        !string.IsNullOrEmpty(invocationKey) && completedAtlasId != 0 && completedAtlasId == currentAtlasId;

    private static bool IsReplacementInput(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".json", StringComparison.OrdinalIgnoreCase);
    }
}
