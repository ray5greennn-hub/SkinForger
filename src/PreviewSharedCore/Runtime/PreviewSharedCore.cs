using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppSpine;
using Il2CppSpine.Unity;
using Il2CppSpine.Unity.AttachmentTools;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SkinForger.Preview;

public static class PreviewSharedCore
{
    private const string Prefix = "[PreviewSharedCore] ";
    private static readonly object Sync = new();
    private static object? _scope;
    private static string _scopeId = string.Empty;
    private static string _scopeCacheKey = string.Empty;
    private static bool _ownsScope;
    private static Type? _publicationRegistry;
    private static readonly List<object> BorrowedPublications = new();
    private static int _scopeSequence;
    private static PreviewSharedCoreAttemptState? _attempt;

    public static bool Apply(Skeleton skeleton, int listIndex, SkeletonAnimation renderer)
    {
        lock (Sync)
        {
            long applyStarted = Stopwatch.GetTimestamp();
            double resolvePathMs = 0d;
            double baselineMs = 0d;
            double discoveryMs = 0d;
            double cacheFingerprintMs = 0d;
            double cacheLookupMs = 0d;
            double sharedCoreMs = 0d;
            ReleaseBorrowedPublications();
            ClearAttempt();
            var attempt = new PreviewSharedCoreAttemptState();
            _attempt = attempt;
            try
            {
                if (skeleton == null || renderer == null) throw new InvalidOperationException("live Preview Skeleton/renderer is unavailable");
                Type host = FindHostType();
                long stageStarted = Stopwatch.GetTimestamp();
                PreviewSharedCoreSkinPathResolution pathResolution = ResolveSkinPath(host, listIndex);
                string skinPath = pathResolution.ResolvedPath;
                resolvePathMs = ElapsedMilliseconds(stageStarted);
                if (string.IsNullOrEmpty(skinPath))
                {
                    LogBuildDecision(pathResolution, 0, 0, "original-only", "no-selected-skin",
                        enteredScope: false, calledTryBuild: false, committed: false, fallback: false);
                    return CompleteOriginalOnly(attempt, "skin-path-unavailable", applyStarted,
                        resolvePathMs, baselineMs, discoveryMs);
                }

                stageStarted = Stopwatch.GetTimestamp();
                List<Attachment> targets = CollectTargets(host, skeleton, skinPath, attempt, out Dictionary<long, string> files, out PreviewSharedCoreCoverage coverage);
                discoveryMs = ElapsedMilliseconds(stageStarted);
                attempt.Coverage = coverage;
                int pngCount = CountPngFiles(skinPath);
                if (PreviewSharedCoreDecision.Decide(targets.Count) == PreviewSharedCoreDisposition.OriginalOnly)
                {
                    LogBuildDecision(pathResolution, pngCount, targets.Count, "original-only", "no-valid-replacement-target",
                        enteredScope: false, calledTryBuild: false, committed: false, fallback: false);
                    return CompleteOriginalOnly(attempt, "no-valid-replacement-png", applyStarted,
                        resolvePathMs, baselineMs, discoveryMs);
                }

                stageStarted = Stopwatch.GetTimestamp();
                PrepareImmutableBaseline(skeleton, attempt);
                baselineMs = ElapsedMilliseconds(stageStarted);

                ClassifyTargetRegions(attempt);
                var pageMaterials = new Dictionary<int, Material>();
                for (int i = 0; i < targets.Count; i++)
                {
                    Material material = GetRegion(targets[i])!.page!.rendererObject!.TryCast<Material>()!;
                    pageMaterials[material.GetInstanceID()] = material;
                }
                PrepareExpectedTargetPlacements(attempt, skinPath, files, pageMaterials);
                stageStarted = Stopwatch.GetTimestamp();
                string scopeCacheKey = BuildScopeCacheKey(skinPath, files, pageMaterials, attempt);
                cacheFingerprintMs = ElapsedMilliseconds(stageStarted);
                string cacheReason;
                string reuseMissReason;
                string assetSource;
                long cacheGeneration = 0;
                string reuseAssemblyIdentity;
                var borrowedAtlases = new Dictionary<int, Texture2D>();
                var borrowed = new List<object>();
                stageStarted = Stopwatch.GetTimestamp();
                bool gameplayReuse = TryAcquireGameplayResources(host.Assembly, skinPath, pageMaterials, attempt,
                    borrowedAtlases, borrowed, out object? gameplayScope, out cacheGeneration, out reuseMissReason,
                    out reuseAssemblyIdentity);
                bool reusedScope = false;
                if (gameplayReuse)
                {
                    if (_scope != null && _ownsScope) ReleaseScopeResources();
                    ReleaseBorrowedPublications();
                    _scope = gameplayScope;
                    _ownsScope = false;
                    _scopeId = "GameplayRuntime-" + cacheGeneration;
                    _scopeCacheKey = string.Empty;
                    BorrowedPublications.AddRange(borrowed);
                    assetSource = "GameplayRuntimeReuse";
                    cacheReason = "gameplay-publication-hit";
                }
                else
                {
                    reusedScope = _scope != null && _ownsScope && PreviewSharedCoreCachePolicy.CanReuse(
                        _scopeCacheKey, scopeCacheKey, resourcesAlive: true);
                    if (reusedScope && !TryPrepareCachedScope(_scope!, skinPath, files, pageMaterials, attempt, out cacheReason))
                    {
                        reusedScope = false;
                        ReleaseScopeResources();
                    }
                    else if (reusedScope)
                    {
                        cacheReason = "validated-hit";
                    }
                    else
                    {
                        cacheReason = _scope == null ? "empty" : "key-changed";
                    }
                    if (!reusedScope)
                    {
                        if (_scope != null) ReleaseScopeResources();
                        _scope = CreatePreviewScope(host.Assembly);
                        _ownsScope = true;
                        _scopeId = ReadString(_scope, "ScopeId");
                        _scopeCacheKey = scopeCacheKey;
                    }
                    assetSource = reusedScope ? "PreviewCache" : "PreviewColdBuild";
                }
                cacheLookupMs = ElapsedMilliseconds(stageStarted);
                object scope = _scope!;
                int decodedBefore = Count(scope, "DecodedTextures");
                int atlasesBefore = Count(scope, "AtlasCache");
                object context = CreatePreviewContext(host.Assembly, scope);
                Type contextType = context.GetType();
                MethodInfo push = FindMethod(contextType, "Push", 1);
                MethodInfo tryBuild = FindMethod(host, "TryBuildSynthesizedAtlasForSkeleton", 5);
                IDisposable? token = null;
                var borrowedAliases = new List<string>();
                bool borrowedAtlasPreflightPassed = false;
                stageStarted = Stopwatch.GetTimestamp();
                try
                {
                    token = push.Invoke(null, new[] { context }) as IDisposable
                        ?? throw new InvalidOperationException("Preview execution context push failed");
                    if (gameplayReuse)
                    {
                        InstallBorrowedAtlasAliases(host, scope, skinPath, pageMaterials, borrowedAtlases, borrowedAliases);
                        borrowedAtlasPreflightPassed = true;
                    }
                    foreach (Material material in pageMaterials.Values)
                    {
                        attempt.ActiveMaterialId = material.GetInstanceID();
                        Texture2D source = material.mainTexture?.TryCast<Texture2D>()
                            ?? throw new InvalidOperationException("Preview page source Texture2D is unavailable");
                        Texture2D atlas = tryBuild.Invoke(null, new object[]
                        {
                            skeleton,
                            source,
                            skinPath,
                            source.name ?? string.Empty,
                            false
                        }) as Texture2D ?? throw new InvalidOperationException("Shared Core returned no atlas");
                        if (gameplayReuse && (!borrowedAtlases.TryGetValue(material.GetInstanceID(), out Texture2D? expectedAtlas) ||
                            atlas.GetInstanceID() != expectedAtlas.GetInstanceID()))
                            throw new InvalidOperationException("Shared Core did not return the leased Gameplay atlas");
                        material.mainTexture = atlas;
                        RecordExpectedTargetKeys(attempt, material.GetInstanceID(), atlas.GetInstanceID());
                    }
                }
                finally
                {
                    attempt.ActiveMaterialId = 0;
                    try { token?.Dispose(); }
                    finally { RemoveBorrowedAtlasAliases(scope, borrowedAliases); }
                }
                sharedCoreMs = ElapsedMilliseconds(stageStarted);

                if (!gameplayReuse) AdoptScopeTextures(scope);

                int repaired270Meshes = RepairFinal270MeshUvs(scope, targets, files, skinPath);

                ValidateCommittedTargets(attempt, scope, gameplayReuse);

                ValidateTopology(renderer, targets);
                MelonLogger.Msg(Prefix + "scope=Preview scopeId=" + _scopeId +
                    " replacementTargets=" + targets.Count +
                    " pngFilesEnumerated=" + pngCount +
                    " replacementPngsFound=" + attempt.Coverage.ReplacementPngsFound +
                    " pageMaterials=" + pageMaterials.Count +
                    " rendererMaterials=" + RendererMaterials(renderer).Length +
                    " atlasCache=" + Count(scope, "AtlasCache") +
                    " atlasStates=" + Count(scope, "AtlasStates") +
                    " placements=" + Count(scope, "RegionPlacements") +
                    " decodedTextures=" + Count(scope, "DecodedTextures") +
                    " pngReads=" + Math.Max(0, Count(scope, "DecodedTextures") - decodedBefore) +
                    " pngDecodes=" + Math.Max(0, Count(scope, "DecodedTextures") - decodedBefore) +
                    " persistentTexturesCreated=" + Math.Max(0,
                        Count(scope, "DecodedTextures") - decodedBefore + Count(scope, "AtlasCache") - atlasesBefore) +
                    " reusedTextures=" + (gameplayReuse ? borrowedAtlases.Count :
                        reusedScope ? Count(scope, "DecodedTextures") + Count(scope, "AtlasCache") : 0) +
                    " atlasBuilds=" + Math.Max(0, Count(scope, "AtlasCache") - atlasesBefore) +
                    " ownedTextures=" + Count(scope, "OwnedTextures") +
                    " ownedMaterials=" + Count(scope, "OwnedMaterials") +
                    " rebasedPages=" + CountRebasedPages(attempt) +
                    " repaired270Meshes=" + repaired270Meshes +
                    " assetSource=" + assetSource +
                    " reuseLookup=true" +
                    " reuseHit=" + gameplayReuse.ToString().ToLowerInvariant() +
                    " reuseMissReason=" + (gameplayReuse ? "none" : reuseMissReason) +
                    " borrowedAtlasPreflight=" + (gameplayReuse ? borrowedAtlasPreflightPassed.ToString().ToLowerInvariant() : "not-applicable") +
                    " skinKey=" + Path.GetFullPath(skinPath).ToUpperInvariant() +
                    " assemblyIdentity=" + reuseAssemblyIdentity +
                    " cacheGeneration=" + cacheGeneration +
                    " scopeCache=" + (reusedScope ? "hit" : "miss") +
                    " cacheReason=" + cacheReason);
                MelonLogger.Msg(Prefix + "COVERAGE totalAttachmentContexts=" + attempt.Coverage.TotalAttachmentContexts +
                    " replacementPngsFound=" + attempt.Coverage.ReplacementPngsFound +
                    " validReplacementTargets=" + attempt.Coverage.ValidReplacementTargets +
                    " uniqueTargetRegions=" + attempt.ExpectedTargetRegionKeys.Count +
                    " targetsSuccessfullyPlaced=" + attempt.Coverage.TargetsSuccessfullyPlaced +
                    " untouchedRegionsRequiringRebase=" + attempt.Coverage.UntouchedRegionsRequiringRebase +
                    " failedOrMissingPngs=" + attempt.Coverage.FailedOrMissingPngs +
                    " targetRegions=" + attempt.TargetRegionIds.Count +
                    " missingTargetRegions=0 unexpectedTargetRegions=0 duplicatePlacements=0 invalidPlacements=0");
                LogBuildDecision(pathResolution, pngCount, targets.Count, "replacement", "valid-replacement-targets",
                    enteredScope: true, calledTryBuild: true, committed: true, fallback: false);
                MelonLogger.Msg(Prefix + "TIMING mode=replacement" +
                    " resolvePath=" + resolvePathMs.ToString("0.###") + "ms" +
                    " baseline=" + baselineMs.ToString("0.###") + "ms" +
                    " targetDiscovery=" + discoveryMs.ToString("0.###") + "ms" +
                    " cacheFingerprint=" + cacheFingerprintMs.ToString("0.###") + "ms" +
                    " cacheLookup=" + cacheLookupMs.ToString("0.###") + "ms" +
                    " sharedCore=" + sharedCoreMs.ToString("0.###") + "ms" +
                    " total=" + ElapsedMilliseconds(applyStarted).ToString("0.###") + "ms");
                return true;
            }
            catch (Exception exception)
            {
                Exception error = exception is TargetInvocationException invocation && invocation.InnerException != null
                    ? invocation.InnerException
                    : exception;
                MelonLogger.Warning(Prefix + "FAILED scope=Preview scopeId=" + _scopeId +
                    " elapsed=" + ElapsedMilliseconds(applyStarted).ToString("0.###") + "ms error=" + error);
                MelonLogger.Warning(Prefix + "BUILD_DECISION result=failed replacementCommit=false fallback=true");
                ReleaseCore();
                return false;
            }
        }
    }

    private static bool CompleteOriginalOnly(
        PreviewSharedCoreAttemptState attempt,
        string reason,
        long applyStarted,
        double resolvePathMs,
        double baselineMs,
        double discoveryMs)
    {
        PreviewSharedCoreCoverage coverage = attempt.Coverage;
        MelonLogger.Msg(Prefix + "ORIGINAL_ONLY reason=" + reason +
            " totalAttachmentContexts=" + coverage.TotalAttachmentContexts +
            " replacementPngsFound=" + coverage.ReplacementPngsFound +
            " validReplacementTargets=" + coverage.ValidReplacementTargets +
            " replacementCommit=false fallback=false");
        MelonLogger.Msg(Prefix + "TIMING mode=original-only" +
            " resolvePath=" + resolvePathMs.ToString("0.###") + "ms" +
            " baseline=" + baselineMs.ToString("0.###") + "ms" +
            " targetDiscovery=" + discoveryMs.ToString("0.###") + "ms" +
            " sharedCore=0ms" +
            " total=" + ElapsedMilliseconds(applyStarted).ToString("0.###") + "ms");
        ClearAttempt();
        return true;
    }

    private static double ElapsedMilliseconds(long started) =>
        (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;

    public static void Release()
    {
        lock (Sync)
        {
            ClearAttempt();
            ReleaseBorrowedPublications();
            if (_scope != null && _ownsScope)
                MelonLogger.Msg(Prefix + "CACHE_RETAIN scope=Preview scopeId=" + _scopeId +
                    " atlasCache=" + Count(_scope, "AtlasCache") +
                    " placements=" + Count(_scope, "RegionPlacements"));
            else if (!_ownsScope)
            {
                _scope = null;
                _scopeId = string.Empty;
                _scopeCacheKey = string.Empty;
            }
        }
    }

    public static void RebaseUntouched(Texture2D atlas)
    {
        lock (Sync)
        {
            PreviewSharedCoreAttemptState? attempt = _attempt;
            if (_scope == null || attempt == null || attempt.ActiveMaterialId == 0 || atlas == null) return;
            if (!attempt.PageBaselines.TryGetValue(attempt.ActiveMaterialId, out List<PreviewSharedCorePageBaseline>? pages)) return;

            for (int pageIndex = 0; pageIndex < pages.Count; pageIndex++)
            {
                PreviewSharedCorePageBaseline page = pages[pageIndex];
                if (page.Rebased) continue;
                page.Rebased = true;
                if (atlas.width == page.Width && atlas.height == page.Height) continue;

                page.Page.width = atlas.width;
                page.Page.height = atlas.height;
                int rebased = 0;
                int targets = 0;
                for (int regionIndex = 0; regionIndex < page.Regions.Count; regionIndex++)
                {
                    PreviewSharedCoreRegionBaseline baseline = page.Regions[regionIndex];
                    if (attempt.TargetRegionIds.Contains(baseline.RegionId))
                    {
                        targets++;
                        continue;
                    }

                    ApplyRegionBaseline(baseline, atlas.width, atlas.height);
                    for (int attachmentIndex = 0; attachmentIndex < baseline.Attachments.Count; attachmentIndex++)
                        RefreshAttachmentUvs(baseline.Attachments[attachmentIndex], baseline.Region);
                    rebased++;

                    if (ContainsHandLeft(baseline.Attachments))
                    {
                        MelonLogger.Msg(Prefix + "REBASE hand_left originalPage=" + page.Width + "x" + page.Height +
                            " finalPage=" + atlas.width + "x" + atlas.height +
                            " packed=" + baseline.PackedX + "," + baseline.PackedY + "," +
                            baseline.PackedWidth + "x" + baseline.PackedHeight +
                            " region=" + baseline.Region.x + "," + baseline.Region.y +
                            " uv=" + baseline.Region.u.ToString("0.#########") + "," +
                            baseline.Region.v.ToString("0.#########") + "," +
                            baseline.Region.u2.ToString("0.#########") + "," +
                            baseline.Region.v2.ToString("0.#########") +
                            " geometryPreserved=true");
                    }
                }

                attempt.Coverage.UntouchedRegionsRequiringRebase += rebased;

                MelonLogger.Msg(Prefix + "REBASE scopeId=" + _scopeId +
                    " materialId=" + attempt.ActiveMaterialId +
                    " originalPage=" + page.Width + "x" + page.Height +
                    " finalPage=" + atlas.width + "x" + atlas.height +
                    " untouchedRegions=" + rebased +
                    " targetRegions=" + targets +
                    " immutableBaseline=true");
            }
        }
    }

    private static object CreatePreviewScope(Assembly assembly)
    {
        Type scopeType = assembly.GetType("SkinForger.CharacterReplacement.ReplacementResourceScope", true)!;
        MethodInfo create = FindMethod(scopeType, "CreateGameplay", 0);
        object scope = create.Invoke(null, null)!;
        string id = "Preview-" + Interlocked.Increment(ref _scopeSequence).ToString();
        Set(scope, "ScopeId", id);
        Set(scope, "Preview", true);
        return scope;
    }

    private static string BuildScopeCacheKey(
        string skinPath,
        Dictionary<long, string> files,
        Dictionary<int, Material> pageMaterials,
        PreviewSharedCoreAttemptState attempt)
    {
        var sourceIdentities = new List<string>();
        foreach (Material material in pageMaterials.Values)
        {
            Texture2D? source = material.mainTexture?.TryCast<Texture2D>();
            sourceIdentities.Add(PreviewSharedCoreCacheKey.BuildSourceIdentity(
                source?.name ?? string.Empty,
                source?.width ?? 0,
                source?.height ?? 0,
                source == null ? "unknown" : source.format.ToString(),
                material.shader?.name ?? string.Empty));
        }

        foreach (List<PreviewSharedCorePageBaseline> pages in attempt.PageBaselines.Values)
        {
            for (int pageIndex = 0; pageIndex < pages.Count; pageIndex++)
            {
                PreviewSharedCorePageBaseline page = pages[pageIndex];
                sourceIdentities.Add("PAGE|" + page.Width + "x" + page.Height + "|regions=" + page.Regions.Count);
                for (int regionIndex = 0; regionIndex < page.Regions.Count; regionIndex++)
                {
                    PreviewSharedCoreRegionBaseline region = page.Regions[regionIndex];
                    sourceIdentities.Add("REGION|" + (region.Region.name ?? string.Empty) + "|" +
                        region.PackedX + "," + region.PackedY + "," + region.PackedWidth + "x" + region.PackedHeight + "|" +
                        region.OriginalWidth + "x" + region.OriginalHeight + "|" + region.Degrees);
                }
            }
        }

        var fileIdentities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files.Values)
        {
            var info = new FileInfo(file);
            fileIdentities.Add(Path.GetFullPath(file).ToUpperInvariant() + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks);
        }
        return PreviewSharedCoreCacheKey.Build(skinPath, sourceIdentities, fileIdentities);
    }

    private static bool TryAcquireGameplayResources(
        Assembly hostAssembly,
        string skinPath,
        Dictionary<int, Material> pageMaterials,
        PreviewSharedCoreAttemptState attempt,
        Dictionary<int, Texture2D> atlases,
        List<object> publications,
        out object? scope,
        out long generation,
        out string reason,
        out string assemblyIdentity)
    {
        scope = null;
        generation = 0;
        assemblyIdentity = hostAssembly.GetName().Name + "@" + hostAssembly.ManifestModule.ModuleVersionId;
        Type? registry = hostAssembly.GetType(
            "SkinForger.CharacterReplacement.GameplayReplacementPublicationRegistry", false);
        if (registry == null)
        {
            reason = "gameplay-registry-unavailable";
            return false;
        }
        _publicationRegistry = registry;
        assemblyIdentity = registry.Assembly.GetName().Name + "@" + registry.Assembly.ManifestModule.ModuleVersionId;
        MethodInfo acquire = FindMethod(registry, "Acquire", 6);
        MethodInfo release = FindMethod(registry, "Release", 1);
        foreach (KeyValuePair<int, Material> entry in pageMaterials)
        {
            Texture2D? source = entry.Value.mainTexture?.TryCast<Texture2D>();
            if (source == null || !attempt.ExpectedFilesByMaterial.TryGetValue(entry.Key, out List<string>? files))
            {
                ReleasePublications(release, publications);
                reason = "preview-source-context-unavailable";
                return false;
            }
            string fingerprint = PreviewSharedCoreCacheKey.BuildManifestFingerprint(files);
            if (string.IsNullOrEmpty(fingerprint))
            {
                ReleasePublications(release, publications);
                reason = "replacement-manifest-invalid";
                return false;
            }
            object?[] arguments =
            {
                skinPath,
                source.name ?? string.Empty,
                PreviewSharedCoreCacheKey.BuildGameplaySourceIdentity(
                    source.name ?? string.Empty, source.width, source.height, source.format.ToString()),
                fingerprint,
                files.Count,
                null
            };
            object? publication = acquire.Invoke(null, arguments);
            if (publication == null)
            {
                ReleasePublications(release, publications);
                reason = arguments[5] as string ?? "gameplay-reuse-not-available";
                return false;
            }
            object publicationScope = Read(publication, "Scope")
                ?? throw new InvalidOperationException("Gameplay publication has no resource scope");
            Texture2D atlas = Read(publication, "Atlas") as Texture2D
                ?? throw new InvalidOperationException("Gameplay publication has no atlas");
            if (scope != null && !ReferenceEquals(scope, publicationScope))
            {
                release.Invoke(null, new[] { publication });
                ReleasePublications(release, publications);
                reason = "gameplay-scope-mismatch";
                return false;
            }
            scope = publicationScope;
            generation = Math.Max(generation, Convert.ToInt64(Read(publication, "Generation") ?? 0L));
            publications.Add(publication);
            atlases[entry.Key] = atlas;
        }
        reason = "none";
        return scope != null && atlases.Count == pageMaterials.Count;
    }

    private static void InstallBorrowedAtlasAliases(
        Type host,
        object scope,
        string skinPath,
        Dictionary<int, Material> pageMaterials,
        Dictionary<int, Texture2D> atlases,
        List<string> installed)
    {
        if (Read(scope, "AtlasCache") is not IDictionary atlasCache)
            throw new InvalidOperationException("Gameplay AtlasCache is unavailable");

        Type ownership = host.Assembly.GetType(
            "SkinForger.AtlasOwnershipFix.AtlasOwnershipManager", true)!;
        MethodInfo scopeCacheKey = FindMethod(ownership, "ScopeCacheKey", 1);
        MethodInfo resolveSource = FindMethod(ownership, "ResolveSource", 1);
        MethodInfo getSynthesizedAtlasKey = FindMethod(host, "GetSynthesizedAtlasKey", 3);

        foreach (KeyValuePair<int, Material> entry in pageMaterials)
        {
            Texture2D source = entry.Value.mainTexture?.TryCast<Texture2D>()
                ?? throw new InvalidOperationException("Preview page source Texture2D is unavailable");
            if (!atlases.TryGetValue(entry.Key, out Texture2D? atlas) || atlas == null)
                throw new InvalidOperationException("Gameplay atlas lease is unavailable for Preview material " + entry.Key);

            string textureName = source.name ?? string.Empty;
            string scopedSkin = scopeCacheKey.Invoke(null, new object[] { skinPath }) as string
                ?? throw new InvalidOperationException("Atlas ownership ScopeCacheKey returned no value");
            Texture2D resolvedSource = resolveSource.Invoke(null, new object[] { source }) as Texture2D
                ?? throw new InvalidOperationException("Atlas ownership ResolveSource returned no Texture2D");
            string effectiveKey = getSynthesizedAtlasKey.Invoke(null, new object[]
            {
                scopedSkin,
                textureName,
                resolvedSource
            }) as string ?? throw new InvalidOperationException("GetSynthesizedAtlasKey returned no value");

            bool cacheEntryExisted = atlasCache.Contains(effectiveKey);
            if (cacheEntryExisted)
            {
                Texture2D? current = atlasCache[effectiveKey] as Texture2D;
                if (current == null || current.GetInstanceID() != atlas.GetInstanceID())
                {
                    int actualId = current == null ? 0 : current.GetInstanceID();
                    LogBorrowedAtlasPreflight(skinPath, scopedSkin, source, resolvedSource, effectiveKey,
                        true, actualId, atlas.GetInstanceID(), false);
                    throw new InvalidOperationException("Gameplay atlas alias collides with another texture: " + effectiveKey);
                }
            }
            else
            {
                atlasCache[effectiveKey] = atlas;
                installed.Add(effectiveKey);
            }

            Texture2D? cachedAtlas = atlasCache[effectiveKey] as Texture2D;
            int cachedAtlasId = cachedAtlas == null ? 0 : cachedAtlas.GetInstanceID();
            bool preflightPassed = cachedAtlasId == atlas.GetInstanceID();
            LogBorrowedAtlasPreflight(skinPath, scopedSkin, source, resolvedSource, effectiveKey,
                true, cachedAtlasId, atlas.GetInstanceID(), preflightPassed);
            if (!preflightPassed)
                throw new InvalidOperationException("Borrowed Gameplay atlas preflight failed before Shared Core build");
        }
    }

    private static void LogBorrowedAtlasPreflight(
        string rawSkin,
        string scopedSkin,
        Texture2D rawSource,
        Texture2D resolvedSource,
        string effectiveKey,
        bool cacheEntryExists,
        int actualAtlasId,
        int borrowedAtlasId,
        bool preflightPassed)
    {
        string message = Prefix + "BORROWED_ATLAS_PREFLIGHT" +
            " rawSkin=" + rawSkin +
            " rawTextureName=" + (rawSource.name ?? string.Empty) +
            " rawSource=" + rawSource.width + "x" + rawSource.height + "#" + rawSource.GetInstanceID() +
            " scopedSkin=" + scopedSkin +
            " resolvedSource=" + resolvedSource.width + "x" + resolvedSource.height + "#" + resolvedSource.GetInstanceID() +
            " effectiveKey=" + effectiveKey +
            " cacheEntryExists=" + cacheEntryExists.ToString().ToLowerInvariant() +
            " cacheAtlasId=" + actualAtlasId +
            " borrowedAtlasId=" + borrowedAtlasId +
            " preflightPassed=" + preflightPassed.ToString().ToLowerInvariant();
        if (preflightPassed) MelonLogger.Msg(message);
        else MelonLogger.Warning(message);
    }

    private static void RemoveBorrowedAtlasAliases(object scope, List<string> installed)
    {
        if (Read(scope, "AtlasCache") is not IDictionary atlasCache) return;
        for (int index = 0; index < installed.Count; index++) atlasCache.Remove(installed[index]);
        installed.Clear();
    }

    private static void ReleasePublications(MethodInfo release, List<object> publications)
    {
        for (int index = publications.Count - 1; index >= 0; index--)
            release.Invoke(null, new[] { publications[index] });
        publications.Clear();
    }

    private static void ReleaseBorrowedPublications()
    {
        if (BorrowedPublications.Count == 0) return;
        Type? registry = _publicationRegistry;
        if (registry != null)
            ReleasePublications(FindMethod(registry, "Release", 1), BorrowedPublications);
        else
            BorrowedPublications.Clear();
        if (!_ownsScope)
        {
            _scope = null;
            _scopeId = string.Empty;
            _scopeCacheKey = string.Empty;
        }
    }

    private static bool TryPrepareCachedScope(
        object scope,
        string skinPath,
        Dictionary<long, string> files,
        Dictionary<int, Material> pageMaterials,
        PreviewSharedCoreAttemptState attempt,
        out string reason)
    {
        if (Read(scope, "AtlasCache") is not IDictionary atlasCache || atlasCache.Count == 0)
        {
            reason = "atlas-cache-empty";
            return false;
        }
        if (Read(scope, "DecodedTextures") is not IDictionary decodedTextures)
        {
            reason = "decoded-cache-unavailable";
            return false;
        }

        var expectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files.Values) expectedFiles.Add(Path.GetFullPath(file));
        foreach (string file in expectedFiles)
        {
            if (!decodedTextures.Contains(file) || !IsAlive(decodedTextures[file]))
            {
                reason = "decoded-texture-missing:" + Path.GetFileName(file);
                return false;
            }
        }
        if (Count(scope, "RegionPlacements") < attempt.TargetRegionIds.Count)
        {
            reason = "placement-cache-incomplete";
            return false;
        }

        var aliases = new List<KeyValuePair<string, Texture2D>>();
        foreach (Material material in pageMaterials.Values)
        {
            Texture2D? source = material.mainTexture?.TryCast<Texture2D>();
            if (source == null)
            {
                reason = "source-texture-unavailable";
                return false;
            }
            string prefix = skinPath + "|" + (source.name ?? string.Empty) + "|";
            Texture2D? cachedAtlas = null;
            foreach (DictionaryEntry entry in atlasCache)
            {
                if (entry.Key is string key && key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    entry.Value is Texture2D atlas && atlas != null)
                {
                    cachedAtlas = atlas;
                    break;
                }
            }
            if (cachedAtlas == null)
            {
                reason = "atlas-alias-missing:" + (source.name ?? string.Empty);
                return false;
            }
            aliases.Add(new KeyValuePair<string, Texture2D>(prefix + source.GetInstanceID(), cachedAtlas));
        }

        foreach (KeyValuePair<string, Texture2D> alias in aliases)
        {
            string prefix = alias.Key.Substring(0, alias.Key.LastIndexOf('|') + 1);
            var stale = new List<object>();
            foreach (DictionaryEntry entry in atlasCache)
                if (entry.Key is string key && key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && key != alias.Key)
                    stale.Add(entry.Key);
            for (int index = 0; index < stale.Count; index++) atlasCache.Remove(stale[index]);
            atlasCache[alias.Key] = alias.Value;
        }

        reason = "validated-hit";
        return true;
    }

    private static void AdoptScopeTextures(object scope)
    {
        object? ownedObject = Read(scope, "OwnedTextures");
        if (ownedObject is not IList owned) return;
        var known = new HashSet<int>();
        foreach (object? value in owned)
            if (value is Object resource && resource != null) known.Add(resource.GetInstanceID());
        AdoptTextures(Read(scope, "AtlasCache"), owned, known);
        AdoptTextures(Read(scope, "DecodedTextures"), owned, known);
    }

    private static void AdoptTextures(object? values, IList owned, HashSet<int> known)
    {
        if (values is not IDictionary dictionary) return;
        foreach (DictionaryEntry entry in dictionary)
        {
            if (entry.Value is not Texture2D texture || texture == null) continue;
            if (known.Add(texture.GetInstanceID())) owned.Add(texture);
        }
    }

    private static bool IsAlive(object? value) => value is Object resource && resource != null;

    private static object CreatePreviewContext(Assembly assembly, object scope)
    {
        Type contextType = assembly.GetType("SkinForger.CharacterReplacement.ReplacementExecutionContext", true)!;
        MethodInfo create = FindMethod(contextType, "CreatePreview", 1);
        return create.Invoke(null, new[] { scope })
            ?? throw new InvalidOperationException("Preview execution context construction failed");
    }

    private static List<Attachment> CollectTargets(
        Type host,
        Skeleton skeleton,
        string skinPath,
        PreviewSharedCoreAttemptState attempt,
        out Dictionary<long, string> files,
        out PreviewSharedCoreCoverage coverage)
    {
        ClearTargetState(attempt);
        var result = new List<Attachment>();
        files = new Dictionary<long, string>();
        coverage = new PreviewSharedCoreCoverage();
        var seen = new HashSet<long>();
        var replacementFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new Il2CppSystem.Collections.Generic.List<Skin.SkinEntry>();
        ExposedList<Skin>? skins = skeleton.Data?.skins;
        int slots = skeleton.Data?.slots?.Count ?? 0;
        if (skins == null) return result;
        MethodInfo getExistingRegionPath = FindMethod(host, "GetExistingRegionPath", 4);
        for (int skinIndex = 0; skinIndex < skins.Count; skinIndex++)
        {
            Skin skin = skins.Items[skinIndex];
            for (int slot = 0; slot < slots; slot++)
            {
                entries.Clear();
                skin.GetAttachments(slot, entries);
                for (int index = 0; index < entries.Count; index++)
                {
                    Attachment attachment = entries[index].Attachment;
                    if (attachment == null || attachment.TryCast<MeshAttachment>() == null && attachment.TryCast<RegionAttachment>() == null)
                        continue;
                    long pointer = attachment.Pointer.ToInt64();
                    if (!seen.Add(pointer)) continue;
                    coverage.TotalAttachmentContexts++;
                    AtlasRegion? region = GetRegion(attachment);
                    Material? material = region?.page?.rendererObject?.TryCast<Material>();
                    Texture2D? source = material?.mainTexture?.TryCast<Texture2D>();
                    if (region == null || material == null || source == null)
                    {
                        coverage.FailedOrMissingPngs++;
                        continue;
                    }
                    string file = getExistingRegionPath.Invoke(null, new object[]
                    {
                        skinPath,
                        attachment,
                        region,
                        source.name ?? string.Empty
                    }) as string ?? string.Empty;
                    if (string.IsNullOrEmpty(file) || !File.Exists(file))
                    {
                        coverage.FailedOrMissingPngs++;
                        continue;
                    }
                    file = Path.GetFullPath(file);
                    replacementFiles.Add(file);
                    result.Add(attachment);
                    files[pointer] = file;
                    attempt.TargetAttachmentIds.Add(pointer);
                }
            }
        }
        coverage.ReplacementPngsFound = replacementFiles.Count;
        coverage.ValidReplacementTargets = result.Count;
        return result;
    }

    private static void ValidateTopology(SkeletonAnimation renderer, List<Attachment> targets)
    {
        Material[] rendererMaterials = RendererMaterials(renderer);
        var rendererIds = new HashSet<int>();
        for (int i = 0; i < rendererMaterials.Length; i++)
            if (rendererMaterials[i] != null) rendererIds.Add(rendererMaterials[i].GetInstanceID());
        for (int i = 0; i < targets.Count; i++)
        {
            Attachment attachment = targets[i];
            AtlasRegion? region = GetRegion(attachment);
            Material? pageMaterial = region?.page?.rendererObject?.TryCast<Material>();
            if (pageMaterial == null || !rendererIds.Contains(pageMaterial.GetInstanceID()))
                throw new InvalidOperationException("Preview region page material is absent from renderer topology for " + attachment.Name);
            if (pageMaterial.mainTexture == null)
                throw new InvalidOperationException("Preview region page material has no atlas texture for " + attachment.Name);
        }
    }

    private static int RepairFinal270MeshUvs(
        object scope,
        List<Attachment> targets,
        Dictionary<long, string> files,
        string skinPath)
    {
        if (Read(scope, "RegionPlacements") is not IDictionary placements) return 0;
        int repaired = 0;
        for (int index = 0; index < targets.Count; index++)
        {
            Attachment attachment = targets[index];
            MeshAttachment? mesh = attachment.TryCast<MeshAttachment>();
            AtlasRegion? region = GetRegion(attachment);
            if (mesh == null || region == null || NormalizeDegrees(region.degrees) != 270) continue;
            if (!files.TryGetValue(attachment.Pointer.ToInt64(), out string? file)) continue;
            Material? material = region.page?.rendererObject?.TryCast<Material>();
            Texture2D? atlas = material?.mainTexture?.TryCast<Texture2D>();
            if (atlas == null) throw new InvalidOperationException("Preview final atlas is unavailable for 270-degree MeshAttachment " + mesh.Name);

            object placement = FindPlacement(placements, skinPath, file, atlas)
                ?? throw new InvalidOperationException("Preview final placement is unavailable for 270-degree MeshAttachment " + mesh.Name);
            object packed = ReadRequired(placement, "PackedRect");
            int packedX = ReadInt(packed, "X");
            int packedY = ReadInt(packed, "Y");
            int packedWidth = ReadInt(packed, "Width");
            int packedHeight = ReadInt(packed, "Height");
            if (!PreviewSharedCoreRegionUvMath.TryBuild270(
                    atlas.width,
                    atlas.height,
                    packedX,
                    packedY,
                    packedWidth,
                    packedHeight,
                    region.width,
                    region.height,
                    region.originalWidth,
                    region.offsetX,
                    region.offsetY,
                    out PreviewSharedCoreUvContract contract))
                throw new InvalidOperationException("Preview 270-degree UV contract is outside its packed rect for " + mesh.Name);

            int worldVerticesLength = mesh.worldVerticesLength;
            int verticesLength = mesh.vertices?.Length ?? 0;
            int bonesLength = mesh.bones?.Length ?? 0;
            int trianglesLength = mesh.triangles?.Length ?? 0;
            int regionUvsLength = mesh.regionUVs?.Length ?? 0;
            long parent = mesh.parentMesh?.Pointer.ToInt64() ?? 0;
            long deform = mesh.deformAttachment?.Pointer.ToInt64() ?? 0;

            region.x = contract.AtlasX;
            region.y = contract.AtlasY;
            region.u = contract.U;
            region.v = contract.V;
            region.u2 = contract.U2;
            region.v2 = contract.V2;
            AttachmentRegionExtensions.SetRegion(mesh, region, true);

            if (mesh.worldVerticesLength != worldVerticesLength ||
                (mesh.vertices?.Length ?? 0) != verticesLength ||
                (mesh.bones?.Length ?? 0) != bonesLength ||
                (mesh.triangles?.Length ?? 0) != trianglesLength ||
                (mesh.regionUVs?.Length ?? 0) != regionUvsLength ||
                (mesh.parentMesh?.Pointer.ToInt64() ?? 0) != parent ||
                (mesh.deformAttachment?.Pointer.ToInt64() ?? 0) != deform)
                throw new InvalidOperationException("Preview 270-degree UV repair changed MeshAttachment geometry/topology for " + mesh.Name);

            repaired++;
            MelonLogger.Msg(Prefix + "ROTATION_270_REPAIR attachment=" + (mesh.Name ?? string.Empty) +
                " resource=" + Path.GetFileName(file) +
                " atlas=" + atlas.width + "x" + atlas.height +
                " packed=" + packedX + "," + packedY + "," + packedWidth + "x" + packedHeight +
                " trim=" + region.width + "x" + region.height +
                " uv=" + region.u.ToString("0.#########") + "," + region.v.ToString("0.#########") + "," +
                region.u2.ToString("0.#########") + "," + region.v2.ToString("0.#########") +
                " geometryPreserved=true");
        }
        return repaired;
    }

    private static object? FindPlacement(IDictionary placements, string skinPath, string file, Texture2D atlas)
    {
        string fullPath = Path.GetFullPath(file);
        string exact = skinPath + "|" + (atlas.name ?? string.Empty) + "|" + fullPath + "|rt=" + atlas.GetInstanceID();
        if (placements.Contains(exact)) return placements[exact];
        string suffix = "|" + fullPath + "|rt=" + atlas.GetInstanceID();
        object? result = null;
        foreach (DictionaryEntry entry in placements)
        {
            if (entry.Key is not string key || !key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            if (result != null) throw new InvalidOperationException("Preview placement lookup is ambiguous for " + fullPath);
            result = entry.Value;
        }
        return result;
    }

    private static object ReadRequired(object instance, string property) =>
        Read(instance, property) ?? throw new MissingMemberException(instance.GetType().FullName, property);

    private static int ReadInt(object instance, string property) =>
        Convert.ToInt32(ReadRequired(instance, property));

    private static int NormalizeDegrees(int degrees)
    {
        int normalized = degrees % 360;
        return normalized < 0 ? normalized + 360 : normalized;
    }

    private static AtlasRegion? GetRegion(Attachment attachment) =>
        attachment?.TryCast<IHasRendererObject>()?.RendererObject?.TryCast<AtlasRegion>();

    private static Material[] RendererMaterials(SkeletonAnimation renderer) =>
        renderer.meshRenderer == null ? Array.Empty<Material>() : renderer.meshRenderer.sharedMaterials;

    private static PreviewSharedCoreSkinPathResolution ResolveSkinPath(Type host, int listIndex)
    {
        MethodInfo selectedMethod = FindMethod(host, "GetSelected", 2);
        MethodInfo pathMethod = FindMethod(host, "GetSkinPath", 3);
        MethodInfo costumeMethod = FindMethod(host, "GetCostumeName", 2);
        int selected = (int)(selectedMethod.Invoke(null, new object[] { listIndex, false }) ?? -1);
        string costume = costumeMethod.Invoke(null, new object[] { listIndex, false }) as string ?? string.Empty;
        string selectedPath = selected < 0
            ? string.Empty
            : pathMethod.Invoke(null, new object[] { listIndex, selected, false }) as string ?? string.Empty;
        if (!string.IsNullOrEmpty(selectedPath) && !Directory.Exists(selectedPath)) selectedPath = string.Empty;

        var registered = new List<string>();
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        FieldInfo? skinsField = host.GetField("Skins", flags);
        if (!string.IsNullOrEmpty(costume) && skinsField?.GetValue(null) is IDictionary skins && skins.Contains(costume) && skins[costume] is IEnumerable paths)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object? value in paths)
            {
                if (value is not string path || string.IsNullOrEmpty(path) || !Directory.Exists(path)) continue;
                path = Path.GetFullPath(path);
                if (seen.Add(path)) registered.Add(path);
            }
        }

        string resolved = PreviewSharedCoreDecision.ChooseSkinPath(selectedPath, registered);
        int registeredPngFiles = 0;
        for (int index = 0; index < registered.Count; index++)
        {
            int count = CountPngFiles(registered[index]);
            if (count > 0) registeredPngFiles += count;
        }
        return new PreviewSharedCoreSkinPathResolution(listIndex, selected, costume, selectedPath, resolved,
            registered.Count, registeredPngFiles,
            !string.IsNullOrEmpty(selectedPath) ? "selected" : registered.Count == 0 ? "none" : "registered-not-selected");
    }

    private static int CountPngFiles(string path)
    {
        try
        {
            return string.IsNullOrEmpty(path) || !Directory.Exists(path)
                ? 0
                : Directory.GetFiles(path, "*.png", SearchOption.AllDirectories).Length;
        }
        catch
        {
            return -1;
        }
    }

    private static void LogBuildDecision(
        PreviewSharedCoreSkinPathResolution resolution,
        int pngCount,
        int targetCount,
        string result,
        string reason,
        bool enteredScope,
        bool calledTryBuild,
        bool committed,
        bool fallback)
    {
        MelonLogger.Msg(Prefix + "BUILD_DECISION characterIndex=" + resolution.ListIndex +
            " character=" + (string.IsNullOrEmpty(resolution.Costume) ? "<unknown>" : resolution.Costume) +
            " selected=" + resolution.Selected +
            " rawSkinPath=" + (string.IsNullOrEmpty(resolution.SelectedPath) ? "<empty>" : resolution.SelectedPath) +
            " resolvedSkinPath=" + (string.IsNullOrEmpty(resolution.ResolvedPath) ? "<empty>" : resolution.ResolvedPath) +
            " pathSource=" + resolution.Source +
            " registeredCandidates=" + resolution.RegisteredCandidates +
            " registeredPngFiles=" + resolution.RegisteredPngFiles +
            " directoryExists=" + (!string.IsNullOrEmpty(resolution.ResolvedPath) && Directory.Exists(resolution.ResolvedPath)) +
            " pngFiles=" + pngCount +
            " validTargets=" + targetCount +
            " result=" + result +
            " reason=" + reason +
            " enteredPreviewScope=" + enteredScope +
            " calledTryBuild=" + calledTryBuild +
            " replacementCommit=" + committed +
            " fallback=" + fallback);
    }

    private static Type FindHostType()
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type = assembly.GetType("SkinForger.SkinForgerMod", false);
            if (type != null) return type;
        }
        throw new TypeLoadException("SkinForger.SkinForgerMod");
    }

    private static MethodInfo FindMethod(Type type, string name, int parameterCount)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        MethodInfo? result = null;
        foreach (MethodInfo method in type.GetMethods(flags))
        {
            if (method.Name != name || method.GetParameters().Length != parameterCount) continue;
            if (result != null) throw new AmbiguousMatchException(type.FullName + "." + name);
            result = method;
        }
        return result ?? throw new MissingMethodException(type.FullName, name);
    }

    private static void PrepareImmutableBaseline(Skeleton skeleton, PreviewSharedCoreAttemptState attempt)
    {
        long skeletonId = skeleton.Pointer.ToInt64();
        if (attempt.BaselineSkeleton == skeletonId && attempt.PageBaselines.Count != 0)
        {
            ResetRebaseState(attempt);
            return;
        }

        ClearImmutableBaseline(attempt);
        attempt.BaselineSkeleton = skeletonId;
        var pagesById = new Dictionary<long, PreviewSharedCorePageBaseline>();
        var regionsById = new Dictionary<long, PreviewSharedCoreRegionBaseline>();
        var entries = new Il2CppSystem.Collections.Generic.List<Skin.SkinEntry>();
        ExposedList<Skin>? skins = skeleton.Data?.skins;
        int slots = skeleton.Data?.slots?.Count ?? 0;
        if (skins == null) return;

        for (int skinIndex = 0; skinIndex < skins.Count; skinIndex++)
        {
            Skin skin = skins.Items[skinIndex];
            for (int slot = 0; slot < slots; slot++)
            {
                entries.Clear();
                skin.GetAttachments(slot, entries);
                for (int index = 0; index < entries.Count; index++)
                    CaptureAttachment(entries[index].Attachment, pagesById, regionsById);
            }
        }

        foreach (PreviewSharedCorePageBaseline page in pagesById.Values)
        {
            if (!attempt.PageBaselines.TryGetValue(page.MaterialId, out List<PreviewSharedCorePageBaseline>? pages))
            {
                pages = new List<PreviewSharedCorePageBaseline>();
                attempt.PageBaselines[page.MaterialId] = pages;
            }
            pages.Add(page);
        }
        ResetRebaseState(attempt);
    }

    private static void ClassifyTargetRegions(PreviewSharedCoreAttemptState attempt)
    {
        attempt.TargetRegionIds.Clear();
        foreach (List<PreviewSharedCorePageBaseline> pages in attempt.PageBaselines.Values)
        {
            for (int pageIndex = 0; pageIndex < pages.Count; pageIndex++)
            {
                List<PreviewSharedCoreRegionBaseline> regions = pages[pageIndex].Regions;
                for (int regionIndex = 0; regionIndex < regions.Count; regionIndex++)
                {
                    PreviewSharedCoreRegionBaseline region = regions[regionIndex];
                    int targets = 0;
                    for (int attachmentIndex = 0; attachmentIndex < region.Attachments.Count; attachmentIndex++)
                    {
                        if (attempt.TargetAttachmentIds.Contains(region.Attachments[attachmentIndex].Pointer.ToInt64())) targets++;
                    }

                    if (targets != 0 && targets != region.Attachments.Count)
                        throw new InvalidOperationException("Preview region mixes replacement and untouched attachments: region=" +
                            (region.Region.name ?? "<unnamed>") + ", targets=" + targets + ", attachments=" + region.Attachments.Count);
                    if (targets != 0) attempt.TargetRegionIds.Add(region.RegionId);
                }
            }
        }

        if (attempt.TargetAttachmentIds.Count != 0 && attempt.TargetRegionIds.Count == 0)
            throw new InvalidOperationException("Preview replacement targets were not mapped to immutable AtlasRegion baselines.");
    }

    private static void PrepareExpectedTargetPlacements(
        PreviewSharedCoreAttemptState attempt,
        string skinPath,
        Dictionary<long, string> files,
        Dictionary<int, Material> pageMaterials)
    {
        attempt.ExpectedPlacementBasesByMaterial.Clear();
        attempt.ExpectedFilesByMaterial.Clear();
        attempt.ExpectedTargetRegionKeys.Clear();
        attempt.CommittedTargetRegionKeys.Clear();

        int classifiedAttachments = 0;
        foreach (KeyValuePair<int, List<PreviewSharedCorePageBaseline>> entry in attempt.PageBaselines)
        {
            if (!pageMaterials.TryGetValue(entry.Key, out Material? material)) continue;
            Texture2D? source = material.mainTexture?.TryCast<Texture2D>();
            if (source == null || string.IsNullOrEmpty(source.name))
                throw new InvalidOperationException("Preview target page source texture is unavailable for material " + entry.Key);

            var placementKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var placementBases = new List<string>();
            var expectedFiles = new List<string>();
            for (int pageIndex = 0; pageIndex < entry.Value.Count; pageIndex++)
            {
                List<PreviewSharedCoreRegionBaseline> regions = entry.Value[pageIndex].Regions;
                for (int regionIndex = 0; regionIndex < regions.Count; regionIndex++)
                {
                    PreviewSharedCoreRegionBaseline region = regions[regionIndex];
                    if (!attempt.TargetRegionIds.Contains(region.RegionId)) continue;
                    for (int attachmentIndex = 0; attachmentIndex < region.Attachments.Count; attachmentIndex++)
                    {
                        Attachment attachment = region.Attachments[attachmentIndex];
                        long pointer = attachment.Pointer.ToInt64();
                        if (!attempt.TargetAttachmentIds.Contains(pointer) || !files.TryGetValue(pointer, out string? file))
                            throw new InvalidOperationException("Preview target region contains an unclassified attachment: region=" +
                                (region.Region.name ?? "<unnamed>") + ", attachment=" + (attachment.Name ?? "<unnamed>"));

                        classifiedAttachments++;
                        file = Path.GetFullPath(file);
                        string placementBase = skinPath + "|" + (source.name ?? string.Empty) + "|" + file;
                        if (placementKeys.Add(placementBase))
                        {
                            placementBases.Add(placementBase);
                            expectedFiles.Add(file);
                        }
                    }
                }
            }
            attempt.ExpectedPlacementBasesByMaterial[entry.Key] = placementBases;
            attempt.ExpectedFilesByMaterial[entry.Key] = expectedFiles;
        }

        if (classifiedAttachments != attempt.TargetAttachmentIds.Count)
            throw new InvalidOperationException("Preview target attachment classification is incomplete before allocation: targets=" +
                attempt.TargetAttachmentIds.Count + ", classified=" + classifiedAttachments);
    }

    private static void RecordExpectedTargetKeys(PreviewSharedCoreAttemptState attempt, int materialId, int atlasId)
    {
        if (!attempt.ExpectedPlacementBasesByMaterial.TryGetValue(materialId, out List<string>? placementBases))
            throw new InvalidOperationException("Preview target placement keys are unavailable for material " + materialId);
        for (int index = 0; index < placementBases.Count; index++)
        {
            string key = placementBases[index] + "|rt=" + atlasId;
            if (!attempt.ExpectedTargetRegionKeys.Add(key))
                throw new InvalidOperationException("Preview duplicate expected target placement: " + key);
        }
    }

    private static void ValidateCommittedTargets(PreviewSharedCoreAttemptState attempt, object scope, bool borrowedGameplayScope)
    {
        object? placementsObject = Read(scope, "RegionPlacements");
        if (placementsObject is not IDictionary placements)
            throw new InvalidOperationException("Preview RegionPlacements dictionary is unavailable");

        var committed = new List<string>();
        var invalid = new List<string>();
        foreach (DictionaryEntry entry in placements)
        {
            string key = entry.Key as string ?? string.Empty;
            if (borrowedGameplayScope && !attempt.ExpectedTargetRegionKeys.Contains(key)) continue;
            committed.Add(key);
            if (entry.Value == null) invalid.Add(key);
        }

        HashSet<string> applied = ReadStringSet(Read(scope, "AppliedRegionKeys"));
        for (int index = 0; index < committed.Count; index++)
            if (!applied.Contains(committed[index])) invalid.Add(committed[index]);

        PreviewSharedCoreTransactionValidationResult validation = PreviewSharedCoreTransactionValidation.Validate(
            attempt.ExpectedTargetRegionKeys,
            committed,
            invalid);
        attempt.CommittedTargetRegionKeys.Clear();
        for (int index = 0; index < committed.Count; index++)
            if (attempt.ExpectedTargetRegionKeys.Contains(committed[index])) attempt.CommittedTargetRegionKeys.Add(committed[index]);
        attempt.Coverage.TargetsSuccessfullyPlaced = attempt.CommittedTargetRegionKeys.Count;

        if (!validation.Success)
            throw new InvalidOperationException("Preview target transaction mismatch: expected=" + validation.ExpectedCount +
                ", committed=" + validation.CommittedCount +
                ", missing=" + FormatKeys(validation.Missing) +
                ", unexpected=" + FormatKeys(validation.Unexpected) +
                ", duplicateExpected=" + FormatKeys(validation.DuplicateExpected) +
                ", duplicatePlacements=" + FormatKeys(validation.DuplicateCommitted) +
                ", invalidPlacements=" + FormatKeys(validation.InvalidPlacements));

        MelonLogger.Msg(Prefix + "TRANSACTION attachmentInstances=" + attempt.Coverage.ValidReplacementTargets +
            " expectedTargetRegions=" + validation.ExpectedCount +
            " committedTargetRegions=" + validation.CommittedCount +
            " missingTargetRegions=0 unexpectedTargetRegions=0 duplicatePlacements=0 invalidPlacements=0");
    }

    private static HashSet<string> ReadStringSet(object? value)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (value is not IEnumerable values) return result;
        foreach (object? item in values)
            if (item is string text && !string.IsNullOrEmpty(text)) result.Add(text);
        return result;
    }

    private static string FormatKeys(string[] values)
    {
        if (values.Length == 0) return "[]";
        int count = Math.Min(values.Length, 8);
        var visible = new string[count];
        Array.Copy(values, visible, count);
        return "[" + string.Join(", ", visible) + (values.Length > count ? ", ... total=" + values.Length : string.Empty) + "]";
    }

    private static void CaptureAttachment(
        Attachment attachment,
        Dictionary<long, PreviewSharedCorePageBaseline> pagesById,
        Dictionary<long, PreviewSharedCoreRegionBaseline> regionsById)
    {
        if (attachment == null ||
            attachment.TryCast<MeshAttachment>() == null && attachment.TryCast<RegionAttachment>() == null)
            return;

        AtlasRegion? region = attachment.TryCast<IHasRendererObject>()?.RendererObject?.TryCast<AtlasRegion>();
        AtlasPage? page = region?.page;
        Material? material = page?.rendererObject?.TryCast<Material>();
        if (region == null || page == null || material == null || page.width <= 0 || page.height <= 0) return;

        long pageId = page.Pointer.ToInt64();
        if (!pagesById.TryGetValue(pageId, out PreviewSharedCorePageBaseline? pageBaseline))
        {
            pageBaseline = new PreviewSharedCorePageBaseline(page, material.GetInstanceID(), page.width, page.height);
            pagesById[pageId] = pageBaseline;
        }

        long regionId = region.Pointer.ToInt64();
        if (!regionsById.TryGetValue(regionId, out PreviewSharedCoreRegionBaseline? regionBaseline))
        {
            regionBaseline = PreviewSharedCoreRegionBaseline.Capture(region, page.width, page.height);
            regionsById[regionId] = regionBaseline;
            pageBaseline.Regions.Add(regionBaseline);
        }

        long attachmentId = attachment.Pointer.ToInt64();
        if (regionBaseline.AttachmentIds.Add(attachmentId)) regionBaseline.Attachments.Add(attachment);
    }

    private static void ApplyRegionBaseline(PreviewSharedCoreRegionBaseline baseline, int finalWidth, int finalHeight)
    {
        AtlasRegion region = baseline.Region;
        region.x = baseline.PackedX;
        region.y = finalHeight - baseline.PackedY - baseline.PackedHeight;
        region.width = baseline.Width;
        region.height = baseline.Height;
        region.originalWidth = baseline.OriginalWidth;
        region.originalHeight = baseline.OriginalHeight;
        region.offsetX = baseline.OffsetX;
        region.offsetY = baseline.OffsetY;
        region.rotate = baseline.Rotate;
        region.degrees = baseline.Degrees;
        region.u = (float)baseline.PackedX / finalWidth;
        region.u2 = (float)(baseline.PackedX + baseline.PackedWidth) / finalWidth;
        region.v = (float)(baseline.PackedY + baseline.PackedHeight) / finalHeight;
        region.v2 = (float)baseline.PackedY / finalHeight;
    }

    private static void RefreshAttachmentUvs(Attachment attachment, AtlasRegion region)
    {
        MeshAttachment? mesh = attachment.TryCast<MeshAttachment>();
        if (mesh != null)
        {
            int worldVerticesLength = mesh.worldVerticesLength;
            int verticesLength = mesh.vertices?.Length ?? 0;
            int bonesLength = mesh.bones?.Length ?? 0;
            int trianglesLength = mesh.triangles?.Length ?? 0;
            long parent = mesh.parentMesh?.Pointer.ToInt64() ?? 0;
            long deform = mesh.deformAttachment?.Pointer.ToInt64() ?? 0;
            mesh.RegionU = region.u;
            mesh.RegionV = region.v;
            mesh.RegionU2 = region.u2;
            mesh.RegionV2 = region.v2;
            mesh.RegionRotate = region.rotate;
            mesh.RegionDegrees = region.degrees;
            mesh.UpdateUVs();
            if (mesh.worldVerticesLength != worldVerticesLength ||
                (mesh.vertices?.Length ?? 0) != verticesLength ||
                (mesh.bones?.Length ?? 0) != bonesLength ||
                (mesh.triangles?.Length ?? 0) != trianglesLength ||
                (mesh.parentMesh?.Pointer.ToInt64() ?? 0) != parent ||
                (mesh.deformAttachment?.Pointer.ToInt64() ?? 0) != deform)
                throw new InvalidOperationException("Preview untouched MeshAttachment geometry changed during UV refresh: " + mesh.Name);
            return;
        }

        RegionAttachment? quad = attachment.TryCast<RegionAttachment>();
        if (quad == null) return;
        float x = quad.x;
        float y = quad.y;
        float width = quad.width;
        float height = quad.height;
        float scaleX = quad.scaleX;
        float scaleY = quad.scaleY;
        float rotation = quad.rotation;
        quad.SetUVs(region.u, region.v, region.u2, region.v2, region.rotate);
        if (quad.x != x || quad.y != y || quad.width != width || quad.height != height ||
            quad.scaleX != scaleX || quad.scaleY != scaleY || quad.rotation != rotation)
            throw new InvalidOperationException("Preview untouched RegionAttachment geometry changed during UV refresh: " + quad.Name);
    }

    private static bool ContainsHandLeft(List<Attachment> attachments)
    {
        for (int i = 0; i < attachments.Count; i++)
            if (string.Equals(attachments[i]?.Name, "hand_left", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static void ResetRebaseState(PreviewSharedCoreAttemptState attempt)
    {
        foreach (List<PreviewSharedCorePageBaseline> pages in attempt.PageBaselines.Values)
            for (int i = 0; i < pages.Count; i++) pages[i].Rebased = false;
    }

    private static int CountRebasedPages(PreviewSharedCoreAttemptState attempt)
    {
        int count = 0;
        foreach (List<PreviewSharedCorePageBaseline> pages in attempt.PageBaselines.Values)
            for (int i = 0; i < pages.Count; i++) if (pages[i].Rebased) count++;
        return count;
    }

    private static void ReleaseScopeResources()
    {
        object? scope = _scope;
        if (scope == null) return;
        if (!_ownsScope)
        {
            ReleaseBorrowedPublications();
            _scope = null;
            _scopeId = string.Empty;
            _scopeCacheKey = string.Empty;
            return;
        }
        var destroyed = new HashSet<int>();
        DestroyOwned(scope, "OwnedMaterials", destroyed);
        DestroyOwned(scope, "OwnedTextures", destroyed);
        foreach (string property in new[] { "AtlasCache", "AtlasStates", "RegionPlacements", "AppliedRegionKeys", "DecodedTextures", "OwnedTextures", "OwnedMaterials" })
            (Read(scope, property) as IList)?.Clear();
        ClearCollection(Read(scope, "AtlasCache"));
        ClearCollection(Read(scope, "AtlasStates"));
        ClearCollection(Read(scope, "RegionPlacements"));
        ClearCollection(Read(scope, "AppliedRegionKeys"));
        ClearCollection(Read(scope, "DecodedTextures"));
        MelonLogger.Msg(Prefix + "RELEASE scope=Preview scopeId=" + _scopeId + " resources=" + destroyed.Count);
        _scope = null;
        _scopeId = string.Empty;
        _scopeCacheKey = string.Empty;
        _ownsScope = false;
    }

    private static void ReleaseCore()
    {
        ReleaseBorrowedPublications();
        ReleaseScopeResources();
        ClearAttempt();
    }

    private static void ClearAttempt()
    {
        PreviewSharedCoreAttemptState? attempt = _attempt;
        if (attempt == null) return;
        ClearImmutableBaseline(attempt);
        ClearTargetState(attempt);
        attempt.ActiveMaterialId = 0;
        attempt.Coverage = new PreviewSharedCoreCoverage();
        _attempt = null;
    }

    private static void ClearImmutableBaseline(PreviewSharedCoreAttemptState attempt)
    {
        attempt.PageBaselines.Clear();
        attempt.BaselineSkeleton = 0;
    }

    private static void ClearTargetState(PreviewSharedCoreAttemptState attempt)
    {
        attempt.TargetAttachmentIds.Clear();
        attempt.TargetRegionIds.Clear();
        attempt.ExpectedPlacementBasesByMaterial.Clear();
        attempt.ExpectedFilesByMaterial.Clear();
        attempt.ExpectedTargetRegionKeys.Clear();
        attempt.CommittedTargetRegionKeys.Clear();
    }

    private static void DestroyOwned(object scope, string property, HashSet<int> destroyed)
    {
        if (Read(scope, property) is not IEnumerable values) return;
        foreach (object? value in values)
        {
            if (value is not Object resource || resource == null) continue;
            int id = resource.GetInstanceID();
            if (destroyed.Add(id)) Object.Destroy(resource);
        }
    }

    private static void ClearCollection(object? collection) =>
        collection?.GetType().GetMethod("Clear", BindingFlags.Instance | BindingFlags.Public)?.Invoke(collection, null);

    private static int Count(object instance, string property) =>
        Convert.ToInt32(Read(instance, property)?.GetType().GetProperty("Count")?.GetValue(Read(instance, property)) ?? 0);

    private static string ReadString(object instance, string property) => Read(instance, property) as string ?? string.Empty;

    private static object? Read(object instance, string property) => instance.GetType()
        .GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance);

    private static void Set(object instance, string property, object? value)
    {
        PropertyInfo target = instance.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(instance.GetType().FullName, property);
        target.SetValue(instance, value);
    }

}

internal sealed class PreviewSharedCoreAttemptState
{
    internal long BaselineSkeleton { get; set; }
    internal int ActiveMaterialId { get; set; }
    internal Dictionary<int, List<PreviewSharedCorePageBaseline>> PageBaselines { get; } = new();
    internal HashSet<long> TargetAttachmentIds { get; } = new();
    internal HashSet<long> TargetRegionIds { get; } = new();
    internal Dictionary<int, List<string>> ExpectedPlacementBasesByMaterial { get; } = new();
    internal Dictionary<int, List<string>> ExpectedFilesByMaterial { get; } = new();
    internal HashSet<string> ExpectedTargetRegionKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal HashSet<string> CommittedTargetRegionKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal PreviewSharedCoreCoverage Coverage { get; set; } = new();
}

internal sealed class PreviewSharedCoreSkinPathResolution
{
    internal PreviewSharedCoreSkinPathResolution(
        int listIndex,
        int selected,
        string costume,
        string selectedPath,
        string resolvedPath,
        int registeredCandidates,
        int registeredPngFiles,
        string source)
    {
        ListIndex = listIndex;
        Selected = selected;
        Costume = costume;
        SelectedPath = selectedPath;
        ResolvedPath = resolvedPath;
        RegisteredCandidates = registeredCandidates;
        RegisteredPngFiles = registeredPngFiles;
        Source = source;
    }

    internal int ListIndex { get; }
    internal int Selected { get; }
    internal string Costume { get; }
    internal string SelectedPath { get; }
    internal string ResolvedPath { get; }
    internal int RegisteredCandidates { get; }
    internal int RegisteredPngFiles { get; }
    internal string Source { get; }
}

internal sealed class PreviewSharedCoreCoverage
{
    internal int TotalAttachmentContexts { get; set; }
    internal int ReplacementPngsFound { get; set; }
    internal int ValidReplacementTargets { get; set; }
    internal int TargetsSuccessfullyPlaced { get; set; }
    internal int UntouchedRegionsRequiringRebase { get; set; }
    internal int FailedOrMissingPngs { get; set; }
}

internal sealed class PreviewSharedCorePageBaseline
{
    internal PreviewSharedCorePageBaseline(AtlasPage page, int materialId, int width, int height)
    {
        Page = page;
        MaterialId = materialId;
        Width = width;
        Height = height;
    }

    internal AtlasPage Page { get; }
    internal int MaterialId { get; }
    internal int Width { get; }
    internal int Height { get; }
    internal List<PreviewSharedCoreRegionBaseline> Regions { get; } = new();
    internal bool Rebased { get; set; }
}

internal sealed class PreviewSharedCoreRegionBaseline
{
    internal static PreviewSharedCoreRegionBaseline Capture(AtlasRegion region, int pageWidth, int pageHeight)
    {
        int left = RoundToInt(Math.Min(region.u, region.u2) * pageWidth);
        int right = RoundToInt(Math.Max(region.u, region.u2) * pageWidth);
        int bottom = RoundToInt(Math.Min(region.v, region.v2) * pageHeight);
        int top = RoundToInt(Math.Max(region.v, region.v2) * pageHeight);
        int packedWidth = Math.Max(1, right - left);
        int packedHeight = Math.Max(1, top - bottom);
        return new PreviewSharedCoreRegionBaseline
        {
            Region = region,
            RegionId = region.Pointer.ToInt64(),
            PackedX = left,
            PackedY = bottom,
            PackedWidth = packedWidth,
            PackedHeight = packedHeight,
            Width = region.width,
            Height = region.height,
            OriginalWidth = region.originalWidth,
            OriginalHeight = region.originalHeight,
            OffsetX = region.offsetX,
            OffsetY = region.offsetY,
            Rotate = region.rotate,
            Degrees = region.degrees,
            U = region.u,
            V = region.v,
            U2 = region.u2,
            V2 = region.v2
        };
    }

    internal AtlasRegion Region { get; private init; } = null!;
    internal long RegionId { get; private init; }
    internal int PackedX { get; private init; }
    internal int PackedY { get; private init; }
    internal int PackedWidth { get; private init; }
    internal int PackedHeight { get; private init; }
    internal int Width { get; private init; }
    internal int Height { get; private init; }
    internal int OriginalWidth { get; private init; }
    internal int OriginalHeight { get; private init; }
    internal float OffsetX { get; private init; }
    internal float OffsetY { get; private init; }
    internal bool Rotate { get; private init; }
    internal int Degrees { get; private init; }
    internal float U { get; private init; }
    internal float V { get; private init; }
    internal float U2 { get; private init; }
    internal float V2 { get; private init; }
    internal List<Attachment> Attachments { get; } = new();
    internal HashSet<long> AttachmentIds { get; } = new();

    private static int RoundToInt(float value) =>
        (int)Math.Round(value, MidpointRounding.AwayFromZero);
}
