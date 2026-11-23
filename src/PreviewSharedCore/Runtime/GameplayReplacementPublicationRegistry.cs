using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SkinForger.CharacterReplacement;

internal sealed class GameplayReplacementPublication
{
    internal object Scope { get; set; } = null!;
    internal Texture2D Atlas { get; set; } = null!;
    internal string SkinPath { get; set; } = string.Empty;
    internal string TextureName { get; set; } = string.Empty;
    internal string SourceIdentity { get; set; } = string.Empty;
    internal string ManifestFingerprint { get; set; } = string.Empty;
    internal int PlacementCount { get; set; }
    internal int AtlasWidth { get; set; }
    internal int AtlasHeight { get; set; }
    internal long Generation { get; set; }
    internal bool Valid { get; set; }
    internal bool PendingDestroy { get; set; }
    internal int LeaseCount { get; set; }
}

internal static class GameplayReplacementPublicationRegistry
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, GameplayReplacementPublication> Entries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, GameplayReplacementPublication> AtlasEntries = new();
    private static long _generation;

    internal static void Publish(object scope, string skinPath, string textureName, Texture2D source, Texture2D atlas)
    {
        const string prefix = "[SkinForger][GameplayPublication] ";
        string assemblyIdentity = AssemblyIdentity();
        if (scope == null || source == null || atlas == null)
        {
            MelonLogger.Warning(prefix + "publicationAttempt=true publicationSucceeded=false skinKey=<unavailable>" +
                " generation=0 atlasTextureCount=0 placementCount=0 assemblyIdentity=" + assemblyIdentity +
                " reason=resource-context-unavailable");
            return;
        }
        if (IsPreviewExecution()) return;
        string normalizedSkin = NormalizePath(skinPath);
        string normalizedTexture = textureName ?? string.Empty;
        string key = BuildKey(normalizedSkin, normalizedTexture);
        if (string.IsNullOrEmpty(normalizedSkin) || string.IsNullOrEmpty(normalizedTexture))
        {
            MelonLogger.Warning(prefix + "publicationAttempt=true publicationSucceeded=false skinKey=" + key +
                " generation=0 atlasTextureCount=1 atlasSize=" + atlas.width + "x" + atlas.height +
                " placementCount=0 assemblyIdentity=" + assemblyIdentity + " reason=skin-or-texture-key-unavailable");
            return;
        }

        if (!TryReadCommittedPaths(scope, normalizedSkin, normalizedTexture, atlas.GetInstanceID(),
                out List<string> files, out int placementCount))
        {
            MelonLogger.Warning(prefix + "publicationAttempt=true publicationSucceeded=false skinKey=" + key +
                " generation=0 atlasTextureCount=1 atlasSize=" + atlas.width + "x" + atlas.height +
                " placementCount=" + placementCount + " assemblyIdentity=" + assemblyIdentity +
                " reason=committed-placement-state-unavailable");
            return;
        }

        string rawSourceIdentity = BuildSourceIdentity(source);
        Texture2D canonicalSource = ResolveCanonicalSource(source);
        string resolvedSourceIdentity = BuildSourceIdentity(canonicalSource);
        string manifestFingerprint = BuildManifestFingerprint(files);
        long publishedGeneration;
        string canonicalSourceIdentity;
        string publicationAction;
        bool publicationSucceeded = true;

        lock (Sync)
        {
            Entries.TryGetValue(key, out GameplayReplacementPublication? previous);
            bool sameAtlas = previous?.Atlas != null && previous.Atlas.GetInstanceID() == atlas.GetInstanceID();
            bool rawSourceIsFinalAtlas = source.GetInstanceID() == atlas.GetInstanceID();
            canonicalSourceIdentity = GameplayReplacementPublicationPolicy.SelectCanonicalSourceIdentity(
                rawSourceIdentity,
                resolvedSourceIdentity,
                rawSourceIsFinalAtlas,
                previous?.SourceIdentity ?? string.Empty);
            if (string.IsNullOrEmpty(canonicalSourceIdentity))
            {
                publishedGeneration = previous?.Generation ?? 0;
                publicationAction = "rejected-unverified-output-source";
                publicationSucceeded = false;
            }
            else if (sameAtlas && previous != null)
            {
                bool changed = GameplayReplacementPublicationPolicy.RequiresNewGeneration(
                    previous.SourceIdentity,
                    canonicalSourceIdentity,
                    previous.ManifestFingerprint,
                    manifestFingerprint,
                    previous.PlacementCount,
                    placementCount,
                    previous.AtlasWidth,
                    previous.AtlasHeight,
                    atlas.width,
                    atlas.height);
                previous.Scope = scope;
                previous.SkinPath = normalizedSkin;
                previous.TextureName = normalizedTexture;
                previous.SourceIdentity = canonicalSourceIdentity;
                previous.ManifestFingerprint = manifestFingerprint;
                previous.PlacementCount = placementCount;
                previous.AtlasWidth = atlas.width;
                previous.AtlasHeight = atlas.height;
                if (changed) previous.Generation = ++_generation;
                previous.Valid = true;
                previous.PendingDestroy = false;
                AtlasEntries[atlas.GetInstanceID()] = previous;
                publishedGeneration = previous.Generation;
                publicationAction = changed ? "updated-resource" :
                    rawSourceIdentity == canonicalSourceIdentity ? "reused-unchanged" : "reused-canonical-source";
            }
            else
            {
                long generation = ++_generation;
                if (previous != null) previous.Valid = false;
                var publication = new GameplayReplacementPublication
                {
                    Scope = scope,
                    Atlas = atlas,
                    SkinPath = normalizedSkin,
                    TextureName = normalizedTexture,
                    SourceIdentity = canonicalSourceIdentity,
                    ManifestFingerprint = manifestFingerprint,
                    PlacementCount = placementCount,
                    AtlasWidth = atlas.width,
                    AtlasHeight = atlas.height,
                    Generation = generation,
                    Valid = true
                };
                Entries[key] = publication;
                AtlasEntries[atlas.GetInstanceID()] = publication;
                publishedGeneration = generation;
                publicationAction = "created";
            }
        }
        if (!publicationSucceeded)
        {
            MelonLogger.Warning(prefix + "publicationAttempt=true publicationSucceeded=false skinKey=" + key +
                " rawSourceIdentity=" + rawSourceIdentity + " canonicalSourceIdentity=<unavailable>" +
                " publicationAction=" + publicationAction + " generation=" + publishedGeneration +
                " atlasTextureCount=1 atlasSize=" + atlas.width + "x" + atlas.height +
                " placementCount=" + placementCount + " assemblyIdentity=" + assemblyIdentity +
                " reason=canonical-source-unavailable");
            return;
        }
        MelonLogger.Msg(prefix + "publicationAttempt=true publicationSucceeded=true skinKey=" + key +
            " rawSourceIdentity=" + rawSourceIdentity + " canonicalSourceIdentity=" + canonicalSourceIdentity +
            " publicationAction=" + publicationAction +
            " generation=" + publishedGeneration + " atlasTextureCount=1 atlasSize=" + atlas.width + "x" + atlas.height +
            " placementCount=" + placementCount + " assemblyIdentity=" + assemblyIdentity +
            " sourceIdentity=" + canonicalSourceIdentity + " sourceFormat=" + source.format);
    }

    internal static GameplayReplacementPublication? Acquire(
        string skinPath,
        string textureName,
        string sourceIdentity,
        string manifestFingerprint,
        int expectedPlacements,
        out string reason)
    {
        lock (Sync)
        {
            if (!Entries.TryGetValue(BuildKey(NormalizePath(skinPath), textureName), out GameplayReplacementPublication? publication))
            {
                reason = "gameplay-skin-not-published";
                return null;
            }
            if (!publication.Valid)
            {
                reason = "gameplay-publication-invalid";
                return null;
            }
            if (publication.Atlas == null)
            {
                reason = "gameplay-atlas-destroyed";
                return null;
            }
            if (!string.Equals(publication.SourceIdentity, sourceIdentity, StringComparison.Ordinal))
            {
                reason = DescribeSourceMismatch(publication.SourceIdentity, sourceIdentity);
                return null;
            }
            if (!string.Equals(publication.ManifestFingerprint, manifestFingerprint, StringComparison.Ordinal))
            {
                reason = "replacement-generation-mismatch";
                return null;
            }
            if (publication.PlacementCount != expectedPlacements)
            {
                reason = "placement-count-mismatch";
                return null;
            }
            if (!HasCommittedState(publication))
            {
                reason = "gameplay-commit-state-missing";
                return null;
            }
            publication.LeaseCount++;
            reason = "matched-gameplay-runtime";
            return publication;
        }
    }

    internal static void Release(object? value)
    {
        if (value is not GameplayReplacementPublication publication) return;
        lock (Sync)
        {
            if (publication.LeaseCount > 0) publication.LeaseCount--;
            if (publication.LeaseCount != 0 || !publication.PendingDestroy || publication.Atlas == null) return;
            int id = publication.Atlas.GetInstanceID();
            DestroyOnce(publication.Atlas);
            AtlasEntries.Remove(id);
        }
    }

    internal static void InvalidateAll()
    {
        lock (Sync)
        {
            _generation++;
            foreach (GameplayReplacementPublication publication in Entries.Values) publication.Valid = false;
            Entries.Clear();
        }
    }

    internal static void DestroyOrDefer(Object resource)
    {
        if (resource == null) return;
        lock (Sync)
        {
            int id = resource.GetInstanceID();
            if (AtlasEntries.TryGetValue(id, out GameplayReplacementPublication? publication) && publication.LeaseCount > 0)
            {
                publication.Valid = false;
                publication.PendingDestroy = true;
                return;
            }
            DestroyOnce(resource);
            AtlasEntries.Remove(id);
        }
    }

    private static bool HasCommittedState(GameplayReplacementPublication publication)
    {
        if (Read(publication.Scope, "AtlasCache") is not IDictionary atlases ||
            Read(publication.Scope, "RegionPlacements") is not IDictionary placements ||
            Read(publication.Scope, "AppliedRegionKeys") is not IEnumerable applied) return false;

        bool atlasFound = false;
        foreach (DictionaryEntry entry in atlases)
            if (ReferenceEquals(entry.Value, publication.Atlas) || entry.Value is Texture2D texture && texture != null &&
                texture.GetInstanceID() == publication.Atlas.GetInstanceID()) atlasFound = true;
        if (!atlasFound) return false;

        string suffix = "|rt=" + publication.Atlas.GetInstanceID();
        int committed = 0;
        var appliedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (object? key in applied) if (key is string text) appliedKeys.Add(text);
        foreach (DictionaryEntry entry in placements)
        {
            string key = entry.Key as string ?? string.Empty;
            if (key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && entry.Value != null && appliedKeys.Contains(key)) committed++;
        }
        return committed == publication.PlacementCount;
    }

    private static bool TryReadCommittedPaths(
        object scope,
        string skinPath,
        string textureName,
        int atlasId,
        out List<string> files,
        out int placementCount)
    {
        files = new List<string>();
        placementCount = 0;
        if (Read(scope, "RegionPlacements") is not IDictionary placements ||
            Read(scope, "AppliedRegionKeys") is not IEnumerable applied) return false;

        var appliedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (object? key in applied) if (key is string text) appliedKeys.Add(text);
        string prefix = skinPath + "|" + textureName + "|";
        string suffix = "|rt=" + atlasId;
        foreach (DictionaryEntry entry in placements)
        {
            string key = entry.Key as string ?? string.Empty;
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || entry.Value == null || !appliedKeys.Contains(key)) continue;
            string file = key.Substring(prefix.Length, key.Length - prefix.Length - suffix.Length);
            if (!File.Exists(file)) return false;
            files.Add(file);
            placementCount++;
        }
        return placementCount > 0;
    }

    // Texture format is intentionally diagnostic-only: Preview owns an RGBA clone of the same logical atlas page.
    internal static string BuildSourceIdentity(Texture2D source) =>
        (source.name ?? string.Empty) + "|" + source.width + "x" + source.height;

    private static Texture2D ResolveCanonicalSource(Texture2D source)
    {
        try
        {
            Type? ownership = typeof(GameplayReplacementPublicationRegistry).Assembly.GetType(
                "SkinForger.AtlasOwnershipFix.AtlasOwnershipManager", false);
            MethodInfo? resolve = ownership?.GetMethod(
                "ResolveSource",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(Texture2D) },
                null);
            return resolve?.Invoke(null, new object?[] { source }) as Texture2D ?? source;
        }
        catch (Exception exception)
        {
            MelonLogger.Warning("[SkinForger][GameplayPublication] canonicalSourceResolution=false reason=" +
                exception.GetType().Name);
            return source;
        }
    }

    internal static string BuildManifestFingerprint(IEnumerable<string> files)
    {
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files)
        {
            var info = new FileInfo(file);
            if (!info.Exists) return string.Empty;
            identities.Add(Path.GetFullPath(file).ToUpperInvariant() + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks);
        }
        var ordered = new List<string>(identities);
        ordered.Sort(StringComparer.Ordinal);
        return string.Join("\n", ordered);
    }

    private static bool IsPreviewExecution()
    {
        Type? contextType = typeof(GameplayReplacementPublicationRegistry).Assembly.GetType(
            "SkinForger.CharacterReplacement.ReplacementExecutionContext", false);
        object? current = contextType?.GetProperty("Current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
        return current != null && Convert.ToBoolean(current.GetType()
            .GetProperty("Preview", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(current) ?? false);
    }

    private static string BuildKey(string skinPath, string textureName) =>
        skinPath.ToUpperInvariant() + "|" + (textureName ?? string.Empty).ToUpperInvariant();

    private static string DescribeSourceMismatch(string published, string requested)
    {
        string[] left = (published ?? string.Empty).Split('|');
        string[] right = (requested ?? string.Empty).Split('|');
        if (left.Length == 0 || right.Length == 0 || !string.Equals(left[0], right[0], StringComparison.Ordinal))
            return "source-name-mismatch:published=" + published + ",requested=" + requested;
        if (left.Length < 2 || right.Length < 2 || !string.Equals(left[1], right[1], StringComparison.Ordinal))
            return "source-dimensions-mismatch:published=" + published + ",requested=" + requested;
        return "source-identity-mismatch:published=" + published + ",requested=" + requested;
    }

    private static string AssemblyIdentity()
    {
        Assembly assembly = typeof(GameplayReplacementPublicationRegistry).Assembly;
        return (assembly.GetName().Name ?? "<unknown>") + "@" + assembly.ManifestModule.ModuleVersionId;
    }

    private static string NormalizePath(string path) => string.IsNullOrEmpty(path) ? string.Empty : Path.GetFullPath(path).TrimEnd(
        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static object? Read(object instance, string property) => instance.GetType()
        .GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance);

    private static void DestroyOnce(Object resource)
        => Object.Destroy(resource);
}
