using System;

namespace SkinForger.CharacterReplacement;

internal static class GameplayReplacementPublicationPolicy
{
    internal static string SelectCanonicalSourceIdentity(
        string rawSourceIdentity,
        string resolvedSourceIdentity,
        bool rawSourceIsFinalAtlas,
        string existingSourceIdentity)
    {
        string raw = rawSourceIdentity ?? string.Empty;
        string resolved = resolvedSourceIdentity ?? string.Empty;
        string existing = existingSourceIdentity ?? string.Empty;

        if (!rawSourceIsFinalAtlas) return string.IsNullOrEmpty(resolved) ? raw : resolved;
        if (!string.IsNullOrEmpty(resolved) && !string.Equals(resolved, raw, StringComparison.Ordinal))
            return resolved;
        return existing;
    }

    internal static bool RequiresNewGeneration(
        string previousSourceIdentity,
        string canonicalSourceIdentity,
        string previousManifestFingerprint,
        string manifestFingerprint,
        int previousPlacementCount,
        int placementCount,
        int previousAtlasWidth,
        int previousAtlasHeight,
        int atlasWidth,
        int atlasHeight) =>
        !string.Equals(previousSourceIdentity, canonicalSourceIdentity, StringComparison.Ordinal) ||
        !string.Equals(previousManifestFingerprint, manifestFingerprint, StringComparison.Ordinal) ||
        previousPlacementCount != placementCount ||
        previousAtlasWidth != atlasWidth ||
        previousAtlasHeight != atlasHeight;
}
