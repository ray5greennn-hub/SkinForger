using System;
using System.Collections.Generic;
using System.IO;

namespace SkinForger.Preview;

internal static class PreviewSharedCoreCacheKey
{
    internal static string BuildSourceIdentity(
        string textureName,
        int width,
        int height,
        string format,
        string shaderName) =>
        (textureName ?? string.Empty) + "|" + width + "x" + height + "|" +
        (format ?? string.Empty) + "|" + (shaderName ?? string.Empty);

    internal static string BuildGameplaySourceIdentity(string textureName, int width, int height, string format) =>
        (textureName ?? string.Empty) + "|" + width + "x" + height;

    internal static string BuildManifestFingerprint(IEnumerable<string> paths)
    {
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            var info = new FileInfo(path);
            if (!info.Exists) return string.Empty;
            identities.Add(Path.GetFullPath(path).ToUpperInvariant() + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks);
        }
        var ordered = new List<string>(identities);
        ordered.Sort(StringComparer.Ordinal);
        return string.Join("\n", ordered);
    }

    internal static string Build(
        string skinPath,
        IEnumerable<string> sourceIdentities,
        IEnumerable<string> fileIdentities)
    {
        var sources = new List<string>(sourceIdentities);
        var files = new List<string>(fileIdentities);
        sources.Sort(StringComparer.Ordinal);
        files.Sort(StringComparer.OrdinalIgnoreCase);
        return (skinPath ?? string.Empty).ToUpperInvariant() + "\nS:" +
            string.Join("\nS:", sources) + "\nF:" + string.Join("\nF:", files);
    }
}

internal static class PreviewSharedCoreCachePolicy
{
    internal static bool RetainForOriginalOnly => true;

    internal static bool CanReuse(string cachedKey, string requestedKey, bool resourcesAlive) =>
        resourcesAlive && !string.IsNullOrEmpty(cachedKey) &&
        string.Equals(cachedKey, requestedKey, StringComparison.Ordinal);
}
