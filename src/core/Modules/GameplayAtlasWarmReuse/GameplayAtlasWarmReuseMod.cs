using System.Collections;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSpine;
using Il2CppSpine.Unity;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SkinForger.GameplayAtlasWarmReuse;

public sealed class GameplayAtlasWarmReuseMod : global::SkinForger.Core.SfModule
{
    private const string Prefix = "[SkinForge][GameplayAtlasWarmReuse]";
    // 2026-11-16: 112 MB evicted Maid's 62 MB battle snapshot while loading the ghost, so the battle object rebuilt
    // its atlas from scratch (694 ms). 256 MB keeps a character's battle, ghost and main show warm.
    private const long MaxCacheBytes = 256L * 1024L * 1024L;
    private const int MaxCacheEntries = 12;

    private static readonly Dictionary<string, AtlasSnapshot> Snapshots = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> ReloadFingerprints = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Texture2D> CompletedReloadBuilds = new(StringComparer.Ordinal);
    /// <summary>
    /// 2026-11-16 (log 21-09, Maid after Original -> Root): a restored CHARACTER snapshot lost the R4-12 canvas of every
    /// part packed rotated 90/270 (hair_front, braid_R, ribbon_R, hair_L, hair_R, hand_L_2, hand_R_1: 25 -> 18 targets,
    /// fringe squeezed into the original size). Such snapshots are not restored; that atlas is built normally.
    /// </summary>
    private static readonly HashSet<string> RotatedSnapshots = new(StringComparer.Ordinal);
    private static Type? _host;
    private static Type? _ownership;
    private static Type? _executionContext;
    private static FieldInfo? _gameplayScope;
    private static MethodInfo? _scopeCacheKey;
    private static MethodInfo? _resolveSource;
    private static MethodInfo? _getAtlasKey;
    private static MethodInfo? _shouldUsePremultipliedAlpha;
    [ThreadStatic] private static Stack<ConsumerFrame>? _consumerFrames;
    [ThreadStatic] private static Stack<BuildState>? _buildFrames;
    [ThreadStatic] private static Stack<ApplyAlphaState>? _applyFrames;
    private static long _cacheBytes;
    private static long _accessSequence;
    private static ReloadMetrics? _reload;

    public override void OnInitializeMelon()
    {
        if (StandDownForAuthoredMain()) return;
        _host = AccessTools.TypeByName("SkinForger.SkinForgerMod");
        _ownership = AccessTools.TypeByName("SkinForger.AtlasOwnershipFix.AtlasOwnershipManager");
        _executionContext = AccessTools.TypeByName("SkinForger.CharacterReplacement.ReplacementExecutionContext");
        MethodInfo? build = Find(_host, "TryBuildSynthesizedAtlasForSkeleton", 5);
        MethodInfo? apply = Find(_host, "ApplyRegionPngToAtlas", 8);
        MethodInfo? pmaDecision = Find(_host, "ShouldUsePremultipliedAlpha", 1);
        MethodInfo? premultiply = Find(_host, "PremultiplyAlphaInPlace", 1);
        MethodInfo? rendererEntry = Find(_host, "SkeletonRendererAwake", 1);
        MethodInfo? graphicEntry = Find(_host, "SkeletonGraphicAwake", 1);
        MethodInfo? characterEntry = Find(_host, "CharacterApplyOnEnable", 1);
        MethodInfo? reload = Find(_host, "ReloadAssets", 0);
        MethodInfo? bind = Find(_ownership, "Bind", 1);
        _gameplayScope = _host?.GetField("GameplayScope", BindingFlags.Static | BindingFlags.NonPublic);
        _scopeCacheKey = Find(_ownership, "ScopeCacheKey", 1);
        _resolveSource = Find(_ownership, "ResolveSource", 1);
        _getAtlasKey = Find(_host, "GetSynthesizedAtlasKey", 3);

        if (build == null || apply == null || pmaDecision == null || premultiply == null ||
            rendererEntry == null || graphicEntry == null || characterEntry == null ||
            reload == null || bind == null || _gameplayScope == null ||
            _scopeCacheKey == null || _resolveSource == null || _getAtlasKey == null)
        {
            MelonLogger.Warning(Prefix + " disabled reason=host-contract-unavailable");
            return;
        }

        _shouldUsePremultipliedAlpha = pmaDecision;

        HarmonyInstance.Patch(build,
            prefix: Patch(nameof(BuildPrefix), HarmonyLib.Priority.First),
            postfix: Patch(nameof(BuildPostfix), HarmonyLib.Priority.Last),
            finalizer: Patch(nameof(BuildFinalizer), HarmonyLib.Priority.Last));
        HarmonyInstance.Patch(build,
            prefix: Patch(nameof(BuildDedupPrefix), HarmonyLib.Priority.Last));
        HarmonyInstance.Patch(apply,
            prefix: Patch(nameof(ApplyAlphaPrefix), HarmonyLib.Priority.First),
            postfix: Patch(nameof(ApplyAlphaPostfix), HarmonyLib.Priority.Last),
            finalizer: Patch(nameof(ApplyAlphaFinalizer), HarmonyLib.Priority.Last));
        HarmonyInstance.Patch(pmaDecision,
            postfix: Patch(nameof(PmaDecisionPostfix), HarmonyLib.Priority.Last));
        HarmonyInstance.Patch(premultiply,
            prefix: Patch(nameof(PremultiplyPrefix), HarmonyLib.Priority.First));
        foreach (MethodInfo entry in new[] { rendererEntry, graphicEntry, characterEntry })
            HarmonyInstance.Patch(entry,
                prefix: Patch(nameof(ConsumerEntryPrefix), HarmonyLib.Priority.First),
                finalizer: Patch(nameof(ConsumerEntryFinalizer), HarmonyLib.Priority.Last));
        HarmonyInstance.Patch(reload,
            prefix: Patch(nameof(ReloadPrefix), HarmonyLib.Priority.First),
            postfix: Patch(nameof(ReloadPostfix), HarmonyLib.Priority.Last),
            finalizer: Patch(nameof(ReloadFinalizer), HarmonyLib.Priority.Last));
        HarmonyInstance.Patch(bind, postfix: Patch(nameof(BindPostfix), HarmonyLib.Priority.Last));
        MelonLogger.Msg(Prefix + " ready version=0.1.4 maxEntries=" + MaxCacheEntries +
            " maxManagedBytes=" + MaxCacheBytes + " unityObjectReuse=false alphaCompatibility=fail-closed");
    }

    public override void OnDeinitializeMelon()
    {
        Snapshots.Clear();
        ReloadFingerprints.Clear();
        CompletedReloadBuilds.Clear();
        _cacheBytes = 0;
        _reload = null;
        _consumerFrames?.Clear();
        _buildFrames?.Clear();
        _applyFrames?.Clear();
    }

    private static HarmonyMethod Patch(string name, int priority)
    {
        var patch = new HarmonyMethod(AccessTools.Method(typeof(GameplayAtlasWarmReuseMod), name));
        patch.priority = priority;
        return patch;
    }

    private static MethodInfo? Find(Type? type, string name, int parameterCount) =>
        type?.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .SingleOrDefault(method => method.Name == name && method.GetParameters().Length == parameterCount);

    private static void ConsumerEntryPrefix(object __0, out ConsumerFrame __state)
    {
        __state = new ConsumerFrame(__0);
        (_consumerFrames ??= new Stack<ConsumerFrame>()).Push(__state);
    }

    private static Exception? ConsumerEntryFinalizer(Exception? __exception, ConsumerFrame __state)
    {
        if (_consumerFrames is { Count: > 0 } && ReferenceEquals(_consumerFrames.Peek(), __state))
            _consumerFrames.Pop();
        return __exception;
    }

    private static void ApplyAlphaPrefix(out ApplyAlphaState? __state)
    {
        BuildState? build = CurrentBuild();
        __state = build is { AlphaGuarded: true } ? new ApplyAlphaState(build) : null;
        if (__state != null) (_applyFrames ??= new Stack<ApplyAlphaState>()).Push(__state);
    }

    private static void PmaDecisionPostfix(bool __result)
    {
        ApplyAlphaState? apply = CurrentApply();
        if (apply != null && !apply.PmaDecision.HasValue) apply.PmaDecision = __result;
    }

    private static void PremultiplyPrefix()
    {
        ApplyAlphaState? apply = CurrentApply();
        if (apply != null) apply.PmaExecuted = true;
    }

    private static void ApplyAlphaPostfix(bool __result, ApplyAlphaState? __state) =>
        CompleteApply(__state, __result);

    private static Exception? ApplyAlphaFinalizer(Exception? __exception, ApplyAlphaState? __state)
    {
        CompleteApply(__state, false);
        return __exception;
    }

    private static void CompleteApply(ApplyAlphaState? state, bool succeeded)
    {
        if (state == null || state.Completed) return;
        state.Completed = true;
        if (_applyFrames is { Count: > 0 } && ReferenceEquals(_applyFrames.Peek(), state))
            _applyFrames.Pop();
        if (!PreparedAtlasCachePolicy.TryResolvePixelWriteEncoding(
                succeeded, state.PmaDecision, state.PmaExecuted, out AlphaEncoding encoding)) return;
        state.Build.PlacementEncodings.Add(encoding);
    }

    private static void ReloadPrefix(out ReloadState __state)
    {
        __state = new ReloadState(Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread());
        ReloadFingerprints.Clear();
        CompletedReloadBuilds.Clear();
        _reload = new ReloadMetrics();
    }

    private static void ReloadPostfix(ReloadState __state)
    {
        CompleteReload(__state, null);
    }

    private static Exception? ReloadFinalizer(Exception? __exception, ReloadState __state)
    {
        if (__exception != null) CompleteReload(__state, __exception);
        return __exception;
    }

    private static void CompleteReload(ReloadState state, Exception? exception)
    {
        ReloadMetrics? metrics = _reload;
        if (metrics == null || metrics.Completed) return;
        metrics.Completed = true;
        double totalMs = ElapsedMilliseconds(state.Started);
        double lastBindMs = metrics.LastBindTicks == 0 ? -1 : ElapsedMilliseconds(state.Started, metrics.LastBindTicks);
        long allocated = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - state.AllocatedBefore);
        MelonLogger.Msg(Prefix + " RELOAD_SUMMARY" +
            " status=" + (exception == null ? "completed" : "failed") +
            " totalMs=" + totalMs.ToString("0.###") +
            " allocatedBytes=" + allocated +
            " buildCalls=" + metrics.BuildCalls +
            " warmHits=" + metrics.WarmHits +
            " warmMisses=" + metrics.WarmMisses +
            " snapshotsCaptured=" + metrics.Captures +
            " restoredAtlases=" + metrics.RestoredAtlases +
            " restoredPlacements=" + metrics.RestoredPlacements +
            " duplicateBuildsSkipped=" + metrics.DuplicateBuildsSkipped +
            " pngDecodePathsBypassed=" + metrics.RestoredPlacements +
            " atlasBuildsAvoided=" + metrics.RestoredAtlases +
            " rendererBinds=" + metrics.BindCalls +
            " lastBindMs=" + lastBindMs.ToString("0.###") +
            " cacheEntries=" + Snapshots.Count +
            " cacheBytes=" + _cacheBytes +
            (exception == null ? string.Empty : " error=" + exception.GetType().Name));
        _reload = null;
    }

    private static void BindPostfix()
    {
        ReloadMetrics? metrics = _reload;
        if (metrics == null) return;
        metrics.BindCalls++;
        metrics.LastBindTicks = Stopwatch.GetTimestamp();
    }

    private static void BuildPrefix(
        Skeleton __0,
        Texture2D __1,
        string __2,
        string __3,
        bool __4,
        out BuildState __state)
    {
        __state = new BuildState();
        ReloadMetrics? metrics = _reload;
        if (metrics != null) metrics.BuildCalls++;
        if (__1 == null || string.IsNullOrWhiteSpace(__2) || string.IsNullOrWhiteSpace(__3) || IsPreview()) return;

        try
        {
            object scope = _gameplayScope!.GetValue(null) ?? throw new InvalidOperationException("GameplayScope unavailable");
            Texture2D canonical = _resolveSource!.Invoke(null, new object?[] { __1 }) as Texture2D ?? __1;
            string baseKey = PreparedAtlasCachePolicy.BuildKey(__2, __3, canonical.name ?? string.Empty,
                canonical.width, canonical.height, canonical.format.ToString());
            string fingerprint = GetFingerprint(__2);
            __state.AlphaGuarded = !__4;
            __state.ConsumerEncoding = __state.AlphaGuarded ? ResolveConsumerEncoding() : AlphaEncoding.Unknown;
            __state.Key = PreparedAtlasCachePolicy.BuildCacheIdentity(
                baseKey, __state.AlphaGuarded, __state.ConsumerEncoding);
            __state.Fingerprint = fingerprint;
            __state.Scope = scope;
            __state.Skin = __2;
            __state.TextureName = __3;
            __state.CanonicalSource = canonical;
            __state.TrackAlpha = true;
            (_buildFrames ??= new Stack<BuildState>()).Push(__state);

            IDictionary atlasCache = Read(scope, "AtlasCache") as IDictionary
                ?? throw new InvalidOperationException("AtlasCache unavailable");
            string scopedSkin = _scopeCacheKey!.Invoke(null, new object?[] { __2 }) as string
                ?? throw new InvalidOperationException("ScopeCacheKey failed");
            string effectiveKey = _getAtlasKey!.Invoke(null, new object?[] { scopedSkin, __3, canonical }) as string
                ?? throw new InvalidOperationException("GetSynthesizedAtlasKey failed");
            __state.EffectiveAtlasKey = effectiveKey;
            string invocationAtlasKey = PreparedAtlasCachePolicy.BuildCacheIdentity(
                effectiveKey, __state.AlphaGuarded, __state.ConsumerEncoding);
            __state.InvocationKey = PreparedAtlasCachePolicy.BuildInvocationKey(
                __0 == null ? 0 : __0.Pointer.ToInt64(), invocationAtlasKey);
            if (atlasCache[effectiveKey] is Texture2D existing && existing != null)
            {
                __state.AtlasPresentAtEntry = true;
                return;
            }

            if (__state.AlphaGuarded && string.IsNullOrEmpty(__state.Key))
            {
                if (metrics != null) metrics.WarmMisses++;
                __state.MissReason = "alpha-consumer-unknown";
                return;
            }

            if (__state.AlphaGuarded && RotatedSnapshots.Contains(__state.Key))
            {
                if (metrics != null) metrics.WarmMisses++;
                __state.MissReason = "rotated-parts-not-restorable";
                return;
            }
            if (!Snapshots.TryGetValue(__state.Key, out AtlasSnapshot? snapshot))
            {
                if (metrics != null) metrics.WarmMisses++;
                __state.MissReason = "snapshot-not-found";
                return;
            }
            bool reusable = __state.AlphaGuarded
                ? PreparedAtlasCachePolicy.CanReuse(snapshot.Fingerprint, fingerprint,
                    snapshot.Encoding, __state.ConsumerEncoding)
                : PreparedAtlasCachePolicy.CanReuse(snapshot.Fingerprint, fingerprint);
            if (!reusable)
            {
                RemoveSnapshot(__state.Key);
                if (metrics != null) metrics.WarmMisses++;
                __state.MissReason = PreparedAtlasCachePolicy.CanReuse(snapshot.Fingerprint, fingerprint)
                    ? "alpha-contract-mismatch"
                    : "input-fingerprint-changed";
                return;
            }

            long started = Stopwatch.GetTimestamp();
            RestoreSnapshot(scope, effectiveKey, snapshot, out int placements);
            snapshot.LastAccess = ++_accessSequence;
            __state.Restored = true;
            __state.RestoredPlacements = placements;
            __state.RestoreMs = ElapsedMilliseconds(started);
            if (metrics != null)
            {
                metrics.WarmHits++;
                metrics.RestoredAtlases++;
                metrics.RestoredPlacements += placements;
            }
        }
        catch (Exception exception)
        {
            __state.MissReason = "restore-error:" + exception.GetType().Name;
            if (metrics != null) metrics.WarmMisses++;
            MelonLogger.Warning(Prefix + " warmRestore=false reason=" + __state.MissReason);
        }
    }

    private static bool BuildDedupPrefix(ref Texture2D? __result, BuildState __state)
    {
        if (_reload == null || __state.Scope == null || string.IsNullOrEmpty(__state.InvocationKey) || IsPreview())
            return true;
        if (!CompletedReloadBuilds.TryGetValue(__state.InvocationKey, out Texture2D? completed) || completed == null)
            return true;

        IDictionary atlasCache = Read(__state.Scope, "AtlasCache") as IDictionary
            ?? throw new InvalidOperationException("AtlasCache unavailable during duplicate-build preflight");
        if (atlasCache[__state.EffectiveAtlasKey] is not Texture2D current || current == null ||
            !PreparedAtlasCachePolicy.CanDeduplicateBuild(
                __state.InvocationKey, completed.GetInstanceID(), current.GetInstanceID()) || current != completed)
            return true;

        __result = current;
        __state.Deduplicated = true;
        _reload.DuplicateBuildsSkipped++;
        return false;
    }

    private static void BuildPostfix(Texture2D? __result, BuildState __state)
    {
        if (!string.IsNullOrEmpty(__state.MissReason) && __result != null)
            MelonLogger.Msg(Prefix + " BUILD_MISS atlas=" + __state.TextureName + " reason=" + __state.MissReason +
                            " consumer=" + AlphaText(__state.ConsumerEncoding) + " (full build)");
        try
        {
            if (__result == null || __state.Scope == null || IsPreview()) return;
            if (__state.Deduplicated)
            {
                MelonLogger.Msg(Prefix + " BUILD_DEDUP" +
                    " skin=" + __state.Skin +
                    " atlas=" + __state.TextureName +
                    " skeletonKey=" + __state.InvocationKey +
                    " atlasId=" + __result.GetInstanceID());
                return;
            }

            if (_reload != null && !string.IsNullOrEmpty(__state.InvocationKey))
                CompletedReloadBuilds[__state.InvocationKey] = __result;
            if (__state.Restored)
            {
                MelonLogger.Msg(Prefix + " BUILD_REUSE" +
                    " kind=" + (__state.TextureName.StartsWith("elfin_", StringComparison.OrdinalIgnoreCase) ? "Elfin" : "Character") +
                    " skin=" + __state.Skin +
                    " atlas=" + __state.TextureName +
                    " alpha=" + AlphaText(__state.ConsumerEncoding) +
                    " reuseHit=true restoreMs=" + __state.RestoreMs.ToString("0.###") +
                    " pngReads=0 pngDecodes=0 persistentTexturesCreated=1 atlasBuilds=0" +
                    " placements=" + __state.RestoredPlacements +
                    " atlasSize=" + __result.width + "x" + __result.height +
                    " atlasId=" + __result.GetInstanceID());
                return;
            }
            if (string.IsNullOrEmpty(__state.Key) || Snapshots.ContainsKey(__state.Key) || RotatedSnapshots.Contains(__state.Key) || __state.AtlasPresentAtEntry) return;

            try
            {
                AlphaEncoding encoding = __state.AlphaGuarded
                    ? PreparedAtlasCachePolicy.ResolveSnapshotEncoding(
                        __state.ConsumerEncoding, __state.PlacementEncodings)
                    : AlphaEncoding.Unknown;
                if (__state.AlphaGuarded && !PreparedAtlasCachePolicy.IsUniform(encoding))
                {
                    MelonLogger.Msg(Prefix + " SNAPSHOT_SKIPPED" +
                        " skin=" + __state.Skin +
                        " atlas=" + __state.TextureName +
                        " reason=alpha-contract-unverified" +
                        " consumer=" + AlphaText(__state.ConsumerEncoding) +
                        " placements=" + AlphaSummary(__state.PlacementEncodings));
                    return;
                }

                long started = Stopwatch.GetTimestamp();
                __state.SnapshotEncoding = encoding;
                AtlasSnapshot snapshot = CaptureSnapshot(__state, __result);
                AddSnapshot(__state.Key, snapshot);
                if (_reload != null) _reload.Captures++;
                MelonLogger.Msg(Prefix + " SNAPSHOT_CAPTURED" +
                    " skin=" + __state.Skin +
                    " atlas=" + __state.TextureName +
                    " alpha=" + AlphaText(snapshot.Encoding) +
                    " captureMs=" + ElapsedMilliseconds(started).ToString("0.###") +
                    " placements=" + snapshot.Placements.Count +
                    " atlasSize=" + snapshot.Width + "x" + snapshot.Height +
                    " managedBytes=" + snapshot.ManagedBytes);
            }
            catch (Exception exception)
            {
                MelonLogger.Warning(Prefix + " snapshotCaptured=false reason=" + exception.GetType().Name +
                    " message=" + exception.Message);
            }
        }
        finally
        {
            EndBuild(__state);
        }
    }

    private static Exception? BuildFinalizer(Exception? __exception, BuildState __state)
    {
        EndBuild(__state);
        return __exception;
    }

    private static AtlasSnapshot CaptureSnapshot(BuildState state, Texture2D atlas)
    {
        IDictionary placements = Read(state.Scope!, "RegionPlacements") as IDictionary
            ?? throw new InvalidOperationException("RegionPlacements unavailable");
        IEnumerable applied = Read(state.Scope!, "AppliedRegionKeys") as IEnumerable
            ?? throw new InvalidOperationException("AppliedRegionKeys unavailable");
        IDictionary atlasStates = Read(state.Scope!, "AtlasStates") as IDictionary
            ?? throw new InvalidOperationException("AtlasStates unavailable");
        var appliedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (object? item in applied) if (item is string text) appliedKeys.Add(text);

        string suffix = "|rt=" + atlas.GetInstanceID();
        string prefix = PreparedAtlasCachePolicy.NormalizePath(state.Skin) + "|" + state.TextureName + "|";
        var placementSnapshots = new List<PlacementSnapshot>();
        bool rotated = false;
        foreach (DictionaryEntry entry in placements)
        {
            string fullKey = entry.Key as string ?? string.Empty;
            if (entry.Value == null || !fullKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !fullKey.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || !appliedKeys.Contains(fullKey)) continue;
            placementSnapshots.Add(PlacementSnapshot.Capture(fullKey[..^suffix.Length], entry.Value));
            try
            {
                int degrees = Convert.ToInt32(entry.Value.GetType().GetProperty("Degrees")?.GetValue(entry.Value) ?? 0);
                if (((degrees % 360) + 360) % 360 is 90 or 270) rotated = true;
            }
            catch { }
        }
        if (placementSnapshots.Count == 0) throw new InvalidOperationException("No committed placements found");
        if (rotated && state.AlphaGuarded && !string.IsNullOrEmpty(state.Key)) RotatedSnapshots.Add(state.Key);

        Il2CppStructArray<Color32> sourcePixels = atlas.GetPixels32();
        var pixels = new Color32[sourcePixels.Length];
        sourcePixels.AsSpan().CopyTo(pixels);
        ObjectSnapshot? atlasState = atlasStates[atlas.GetInstanceID()] is object value
            ? ObjectSnapshot.Capture(value)
            : null;
        return new AtlasSnapshot(
            state.Fingerprint,
            state.SnapshotEncoding,
            atlas.width,
            atlas.height,
            atlas.name ?? state.TextureName,
            atlas.filterMode,
            atlas.wrapMode,
            atlas.anisoLevel,
            pixels,
            placementSnapshots,
            atlasState,
            ++_accessSequence);
    }

    private static void RestoreSnapshot(object scope, string effectiveAtlasKey, AtlasSnapshot snapshot, out int placementCount)
    {
        IDictionary atlasCache = Read(scope, "AtlasCache") as IDictionary
            ?? throw new InvalidOperationException("AtlasCache unavailable");
        IDictionary placements = Read(scope, "RegionPlacements") as IDictionary
            ?? throw new InvalidOperationException("RegionPlacements unavailable");
        object applied = Read(scope, "AppliedRegionKeys")
            ?? throw new InvalidOperationException("AppliedRegionKeys unavailable");
        IDictionary atlasStates = Read(scope, "AtlasStates") as IDictionary
            ?? throw new InvalidOperationException("AtlasStates unavailable");
        IList ownedTextures = Read(scope, "OwnedTextures") as IList
            ?? throw new InvalidOperationException("OwnedTextures unavailable");

        var atlas = new Texture2D(snapshot.Width, snapshot.Height, TextureFormat.RGBA32, false)
        {
            name = snapshot.Name,
            filterMode = snapshot.FilterMode,
            wrapMode = snapshot.WrapMode,
            anisoLevel = snapshot.AnisoLevel
        };
        var insertedPlacementKeys = new List<string>();
        bool atlasInstalled = false;
        try
        {
            atlas.SetPixels32(snapshot.Pixels);
            atlas.Apply(false, false);
            atlasCache[effectiveAtlasKey] = atlas;
            atlasInstalled = true;
            ownedTextures.Add(atlas);
            if (snapshot.AtlasState != null)
                atlasStates[atlas.GetInstanceID()] = snapshot.AtlasState.Create();
            foreach (PlacementSnapshot placement in snapshot.Placements)
            {
                string key = placement.BaseKey + "|rt=" + atlas.GetInstanceID();
                placements[key] = placement.Value.Create();
                CollectionAdd(applied, key);
                insertedPlacementKeys.Add(key);
            }
            placementCount = insertedPlacementKeys.Count;
        }
        catch
        {
            foreach (string key in insertedPlacementKeys)
            {
                placements.Remove(key);
                CollectionRemove(applied, key);
            }
            atlasStates.Remove(atlas.GetInstanceID());
            ownedTextures.Remove(atlas);
            if (atlasInstalled) atlasCache.Remove(effectiveAtlasKey);
            Object.Destroy(atlas);
            throw;
        }
    }

    private static string GetFingerprint(string skin)
    {
        string normalized = PreparedAtlasCachePolicy.NormalizePath(skin);
        if (ReloadFingerprints.TryGetValue(normalized, out string? fingerprint)) return fingerprint;
        fingerprint = PreparedAtlasCachePolicy.BuildInputFingerprint(normalized);
        ReloadFingerprints[normalized] = fingerprint;
        return fingerprint;
    }

    private static BuildState? CurrentBuild() =>
        _buildFrames is { Count: > 0 } ? _buildFrames.Peek() : null;

    private static ApplyAlphaState? CurrentApply() =>
        _applyFrames is { Count: > 0 } ? _applyFrames.Peek() : null;

    private static void EndBuild(BuildState state)
    {
        if (!state.TrackAlpha || state.Completed) return;
        state.Completed = true;
        if (_buildFrames is { Count: > 0 } && ReferenceEquals(_buildFrames.Peek(), state))
            _buildFrames.Pop();
    }

    private static AlphaEncoding ResolveConsumerEncoding()
    {
        if (_consumerFrames is not { Count: > 0 } || _shouldUsePremultipliedAlpha == null)
            return AlphaEncoding.Unknown;
        Material? material = ResolveConsumerMaterial(_consumerFrames.Peek().Owner);
        if (material == null) return AlphaEncoding.Unknown;
        try
        {
            bool pma = Convert.ToBoolean(_shouldUsePremultipliedAlpha.Invoke(null, new object?[] { material }));
            return pma ? AlphaEncoding.Premultiplied : AlphaEncoding.Straight;
        }
        catch
        {
            return AlphaEncoding.Unknown;
        }
    }

    private static Material? ResolveConsumerMaterial(object? owner)
    {
        if (owner is SkeletonRenderer renderer) return RendererMaterial(renderer);
        if (Read(owner, "m_SkeletonMecanim") is SkeletonRenderer mecanim)
            return RendererMaterial(mecanim);
        return Read(owner, "material") as Material;
    }

    /// <summary>
    /// 2026-11-16 (Reload round 3, log 20-28): a renderer met in Awake / OnEnable before its first mesh update has no
    /// sharedMaterial yet -> alpha "unknown" -> no warm reuse, a second full 0.6 s atlas build for the second
    /// main_show clone. Spine gives that renderer its atlas's primary material, so that one decides the alpha.
    /// </summary>
    private static Material? RendererMaterial(SkeletonRenderer renderer)
    {
        Material? material = null;
        try { material = renderer.meshRenderer?.sharedMaterial; } catch { }
        if (material != null) return material;
        try
        {
            var assets = renderer.skeletonDataAsset?.atlasAssets;
            if (assets != null && assets.Length > 0 && assets[0] != null) material = assets[0].PrimaryMaterial;
        }
        catch { }
        return material;
    }

    private static string AlphaText(AlphaEncoding encoding) => encoding.ToString().ToLowerInvariant();

    private static string AlphaSummary(IReadOnlyCollection<AlphaEncoding> values) =>
        string.Join(',', values.GroupBy(value => value).OrderBy(group => group.Key)
            .Select(group => AlphaText(group.Key) + ":" + group.Count()));

    private static void AddSnapshot(string key, AtlasSnapshot snapshot)
    {
        if (Snapshots.TryGetValue(key, out AtlasSnapshot? previous)) _cacheBytes -= previous.ManagedBytes;
        Snapshots[key] = snapshot;
        _cacheBytes += snapshot.ManagedBytes;
        while (Snapshots.Count > MaxCacheEntries || _cacheBytes > MaxCacheBytes)
        {
            KeyValuePair<string, AtlasSnapshot> oldest = Snapshots.OrderBy(entry => entry.Value.LastAccess).First();
            MelonLogger.Msg(Prefix + " CACHE_EVICT reason=capacity" +
                " key=" + oldest.Key +
                " entryBytes=" + oldest.Value.ManagedBytes +
                " cacheBytesBefore=" + _cacheBytes +
                " maxCacheBytes=" + MaxCacheBytes);
            RemoveSnapshot(oldest.Key);
        }
    }

    private static void RemoveSnapshot(string key)
    {
        if (!Snapshots.Remove(key, out AtlasSnapshot? snapshot)) return;
        _cacheBytes -= snapshot.ManagedBytes;
    }

    private static bool IsPreview()
    {
        try
        {
            object? current = _executionContext?.GetProperty("Current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(null);
            return current != null && Convert.ToBoolean(current.GetType()
                .GetProperty("Preview", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(current) ?? false);
        }
        catch { return false; }
    }

    private static object? Read(object? instance, string name)
    {
        if (instance == null) return null;
        Type type = instance.GetType();
        return type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance) ??
               type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance);
    }

    private static void CollectionAdd(object collection, string value)
    {
        MethodInfo add = collection.GetType().GetMethod("Add", new[] { typeof(string) })
            ?? throw new MissingMethodException(collection.GetType().FullName, "Add");
        add.Invoke(collection, new object?[] { value });
    }

    private static void CollectionRemove(object collection, string value)
    {
        MethodInfo? remove = collection.GetType().GetMethod("Remove", new[] { typeof(string) });
        remove?.Invoke(collection, new object?[] { value });
    }

    private static double ElapsedMilliseconds(long started) =>
        (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;

    private static double ElapsedMilliseconds(long started, long ended) =>
        (ended - started) * 1000d / Stopwatch.Frequency;

    private sealed class BuildState
    {
        internal string Key = string.Empty;
        internal string Fingerprint = string.Empty;
        internal string Skin = string.Empty;
        internal string TextureName = string.Empty;
        internal string EffectiveAtlasKey = string.Empty;
        internal string InvocationKey = string.Empty;
        internal string MissReason = string.Empty;
        internal object? Scope;
        internal Texture2D? CanonicalSource;
        internal bool AlphaGuarded;
        internal bool TrackAlpha;
        internal bool Completed;
        internal AlphaEncoding ConsumerEncoding;
        internal AlphaEncoding SnapshotEncoding;
        internal List<AlphaEncoding> PlacementEncodings { get; } = new();
        internal bool AtlasPresentAtEntry;
        internal bool Restored;
        internal bool Deduplicated;
        internal int RestoredPlacements;
        internal double RestoreMs;
    }

    private sealed class ConsumerFrame
    {
        internal ConsumerFrame(object owner) => Owner = owner;
        internal object Owner { get; }
    }

    private sealed class ApplyAlphaState
    {
        internal ApplyAlphaState(BuildState build) => Build = build;
        internal BuildState Build { get; }
        internal bool? PmaDecision { get; set; }
        internal bool PmaExecuted { get; set; }
        internal bool Completed { get; set; }
    }

    private sealed class ReloadMetrics
    {
        internal int BuildCalls;
        internal int WarmHits;
        internal int WarmMisses;
        internal int Captures;
        internal int RestoredAtlases;
        internal int RestoredPlacements;
        internal int DuplicateBuildsSkipped;
        internal int BindCalls;
        internal long LastBindTicks;
        internal bool Completed;
    }

    private readonly struct ReloadState
    {
        internal ReloadState(long started, long allocatedBefore)
        {
            Started = started;
            AllocatedBefore = allocatedBefore;
        }
        internal long Started { get; }
        internal long AllocatedBefore { get; }
    }

    private sealed class AtlasSnapshot
    {
        internal AtlasSnapshot(string fingerprint, AlphaEncoding encoding, int width, int height, string name, FilterMode filterMode,
            TextureWrapMode wrapMode, int anisoLevel, Color32[] pixels, List<PlacementSnapshot> placements,
            ObjectSnapshot? atlasState, long lastAccess)
        {
            Fingerprint = fingerprint;
            Encoding = encoding;
            Width = width;
            Height = height;
            Name = name;
            FilterMode = filterMode;
            WrapMode = wrapMode;
            AnisoLevel = anisoLevel;
            Pixels = pixels;
            Placements = placements;
            AtlasState = atlasState;
            LastAccess = lastAccess;
        }
        internal string Fingerprint { get; }
        internal AlphaEncoding Encoding { get; }
        internal int Width { get; }
        internal int Height { get; }
        internal string Name { get; }
        internal FilterMode FilterMode { get; }
        internal TextureWrapMode WrapMode { get; }
        internal int AnisoLevel { get; }
        internal Color32[] Pixels { get; }
        internal List<PlacementSnapshot> Placements { get; }
        internal ObjectSnapshot? AtlasState { get; }
        internal long LastAccess { get; set; }
        internal long ManagedBytes => (long)Pixels.Length * 4L;
    }

    private sealed class PlacementSnapshot
    {
        private PlacementSnapshot(string baseKey, ObjectSnapshot value)
        {
            BaseKey = baseKey;
            Value = value;
        }
        internal string BaseKey { get; }
        internal ObjectSnapshot Value { get; }
        internal static PlacementSnapshot Capture(string baseKey, object value) =>
            new(baseKey, ObjectSnapshot.Capture(value));
    }

    private sealed class ObjectSnapshot
    {
        private readonly Type _type;
        private readonly Dictionary<string, object?> _values;

        private ObjectSnapshot(Type type, Dictionary<string, object?> values)
        {
            _type = type;
            _values = values;
        }

        internal static ObjectSnapshot Capture(object value)
        {
            Type type = value.GetType();
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length != 0) continue;
                object? captured = property.GetValue(value);
                if (captured != null && captured is not string && !captured.GetType().IsValueType)
                {
                    throw new InvalidOperationException(
                        "Prepared atlas snapshots may only retain immutable value data: " +
                        type.FullName + "." + property.Name + " is " + captured.GetType().FullName);
                }

                values[property.Name] = captured;
            }
            return new ObjectSnapshot(type, values);
        }

        internal object Create()
        {
            object clone = Activator.CreateInstance(_type, nonPublic: true)
                ?? throw new InvalidOperationException("Could not create " + _type.FullName);
            foreach ((string name, object? value) in _values)
            {
                PropertyInfo property = _type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? throw new MissingMemberException(_type.FullName, name);
                property.SetValue(clone, value);
            }
            return clone;
        }
    }

    /// <summary>
    /// This module patches the OLD Main, which writes its atlas one region at a time. The authored Main
    /// composes the whole atlas in one pass instead, where the composer builds the atlas once from the original, so there is no per-region rebuild to warm up.
    ///
    /// Patching it anyway would be worse than useless: the transpilers here look for IL the authored Main
    /// does not contain, and teaching it to write region by region would bring back the very design the cut
    /// removes — with it the 2026-10-01 endlessly growing atlas and the 2026-10-04 overlapping rectangles.
    /// So when the authored Main is the one loaded, this module stands down and says so.
    /// </summary>
    private static bool StandDownForAuthoredMain()
    {
        if (global::SkinForger.MainBridge.MainHost.Generation != global::SkinForger.MainBridge.MainGeneration.Authored)
            return false;
        MelonLoader.MelonLogger.Msg($"{Prefix} not needed: the authored Main composes the atlas itself");
        return true;
    }

}
