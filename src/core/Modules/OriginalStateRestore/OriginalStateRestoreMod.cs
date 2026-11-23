using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSpine;
using MelonLoader;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace SkinForger.OriginalStateRestore;

public sealed class OriginalStateRestoreMod : global::SkinForger.Core.SfModule
{
    // 1.1.1: also accepts Main 11C58361 (StableCanvas); the seven patched Main signatures are identical (Cecil-verified 2026-11-13).
    private static readonly string[] ExpectedMainHashes = { "5BD92AF8E6DE00DA833F5DD27FFA3444E84D3D2212DAAFE46A74C0EAECABAC9A", "11C58361D9E0A5A430D0F3FB6F64DF1147DAED0CC5AF6371DC957A9236B182EF",
        // 2026-11-14: Steam-only hardened Main (11C58361 + MainGuard binding; the seven patched signatures are unchanged).
        "A248B1FA5942815B7081A8C168E4C11AEC4574BDF95874BE28F929C48DF60C49",
        // 2026-11-14: + SkinCreator workspace path (3 path literals; patched signatures unchanged).
        "50CFF97EF9776721D468133A2176BB4956D45ECB8D567393EC456EEAA921F715",
        // 2026-11-14: + .sfskin loading (System.IO calls -> VirtualFiles) and title "SkinForger".
        "E14A65406AB7367A1448B3DD414165E404D3B13B9C86E35EE5F0F96E9268DF55",
        // 2026-11-15: Main v4: skins in SkinCreator\Images, settings in SkinCreator, CustomLodingPic in SkinCreator.
        "962A257B7780AA938105A803E5037FBE752158BBA71CAABD6D609145A2754FD0" };
    private const string Prefix = "[SkinForge][OriginalStateRestore]";

    /// <summary>
    /// This module patches the OLD Main. On the authored Main it stands down, because this puts the game's own art back before a skin changes; the authored Main composes every atlas from the original source each time, so nothing it builds on can have been modified.
    ///
    /// A patch that cannot find the IL it rewrites fails loudly and takes other modules with it, so saying
    /// so once and doing nothing is the honest behaviour while both Mains exist (SF切割SkinChanger计划).
    /// </summary>
    private static bool StandDownForAuthoredMain()
    {
        if (global::SkinForger.MainBridge.MainHost.Generation != global::SkinForger.MainBridge.MainGeneration.Authored)
            return false;
        MelonLoader.MelonLogger.Msg($"{Prefix} not installed: the authored Main does not need it");
        return true;
    }

    public override void OnInitializeMelon()
    {
        if (StandDownForAuthoredMain()) return;
        string mainPath = Path.Combine(MelonLoader.Utils.MelonEnvironment.GameRootDirectory,
            "Mods", "SkinForger", "SkinForger.Melon.dll");
        string hash = File.Exists(mainPath)
            ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(mainPath)))
            : "MISSING";
        if (!ExpectedMainHashes.Any(expected => hash.Equals(expected, StringComparison.OrdinalIgnoreCase)))
        {
            MelonLogger.Warning($"{Prefix} disabled: expected Main {string.Join("/", ExpectedMainHashes)}, actual={hash}");
            return;
        }

        Type host = AccessTools.TypeByName("SkinForger.SkinForgerMod")
            ?? throw new TypeLoadException("SkinForger.SkinForgerMod");
        Type geometry = AccessTools.TypeByName("SkinForger.CharacterMeshGeometry.CharacterMeshGeometry")
            ?? throw new TypeLoadException("SkinForger.CharacterMeshGeometry.CharacterMeshGeometry");
        OriginalStateRuntime.Configure(host);

        Patch(host, "TryBuildSynthesizedAtlasForSkeleton", 5,
            prefix: nameof(BuildPatch.Prefix), prefixType: typeof(BuildPatch),
            finalizer: nameof(BuildPatch.Finalizer), finalizerType: typeof(BuildPatch));
        Patch(host, "ApplyRegionPlacementToRegion", 4,
            prefix: nameof(RegionPatch.Prefix), prefixType: typeof(RegionPatch));
        Patch(host, "ApplyRegionPlacementToAttachment", 6,
            prefix: nameof(AttachmentPatch.Prefix), prefixType: typeof(AttachmentPatch));
        Patch(host, "ApplySkeleton", 4,
            prefix: nameof(ApplySkeletonPatch.Prefix), prefixType: typeof(ApplySkeletonPatch));
        Patch(host, "ReloadSkins", 0,
            prefix: nameof(ReloadSkinsPatch.Prefix), prefixType: typeof(ReloadSkinsPatch));
        HarmonyInstance.Patch(host.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                .Single(method => method.Name == "ReloadSkins" && method.GetParameters().Length == 0),
            transpiler: new HarmonyMethod(typeof(ReloadSkinsPatch), nameof(ReloadSkinsPatch.Transpiler)));
        Patch(host, "ReloadAssets", 0,
            prefix: nameof(ReloadAssetsPatch.Prefix), prefixType: typeof(ReloadAssetsPatch));
        Patch(geometry, "Apply", 6,
            prefix: nameof(GeometryPatch.Prefix), prefixType: typeof(GeometryPatch));

        MelonLogger.Msg($"{Prefix} ready version=1.1.3 main={hash} transactional=true cacheCoherentReload=true toggle=false");
    }

    private void Patch(Type targetType, string name, int parameterCount,
        string prefix, Type prefixType, string? finalizer = null, Type? finalizerType = null)
    {
        MethodInfo original = targetType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Single(method => method.Name == name && method.GetParameters().Length == parameterCount);
        HarmonyInstance.Patch(original,
            prefix: new HarmonyMethod(prefixType, prefix) { priority = HarmonyLib.Priority.First },
            finalizer: finalizer == null ? null : new HarmonyMethod(finalizerType!, finalizer));
    }
}

internal static class BuildPatch
{
    internal static void Prefix(Skeleton __0, string __2, string __3, bool __4)
    {
        // Every build starts from the game's own art, never from what the last build left behind
        // (2026-11-18). Until now the snapshot was only put back when a skin was switched OFF, so a second
        // build of the same atlas read regions that the first build had already enlarged, and derived the
        // next canvas from those. The canvas then grew on every rebuild while the artist's PNG never
        // changed — the Maid's headhoop went 171x173 -> 180x180 -> 185x184 -> 187x186 inside one session,
        // each step using the middle of the previous canvas as its anchor. The expansion band was built for
        // that canvas, so the surplus ring hung outside the drawing as thin stretched slivers.
        //
        // This is the same rule the atlas composition already follows: the original is the only input.
        // Restoring is best-effort here: a build that cannot be given the original state is still better
        // run than refused, so a failure is logged and the build goes ahead.
        // 2026-11-18: OFF. Restoring before a build is the right idea but not safely reachable from here:
        // the log showed "skeleton=all ... attachments=139 -> 196 -> 253 -> 310", i.e. it put back EVERY
        // character's shared data on every build, including data another renderer was drawing from that
        // frame. Parts vanished and the Maid's face collapsed. The shared-data ownership problem has to be
        // solved first (the authored Main owns the composed texture per renderer for exactly this reason);
        // until then this stays off and the canvas drift is the lesser evil.
        // RestoreBeforeBuild(__3, __4);
        OriginalStateRuntime.EnterBuild(__0, __2, __3, __4);
    }

    private static void RestoreBeforeBuild(string? atlas, bool elfin)
    {
        if (elfin || string.IsNullOrEmpty(atlas)) return;
        try
        {
            RestoreResult result = OriginalStateRuntime.RestoreForAtlas(atlas);
            if (!result.Succeeded && result.Detail != "no-snapshot")
                MelonLogger.Warning($"[SkinForge][OriginalStateRestore] rebuild could not start from the original: {result.Status}:{result.Detail}");
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"[SkinForge][OriginalStateRestore] rebuild restore skipped: {exception.GetBaseException().Message}");
        }
    }

    internal static Exception? Finalizer(Exception? __exception)
    {
        OriginalStateRuntime.ExitBuild(__exception);
        return __exception;
    }
}

internal static class RegionPatch
{
    internal static void Prefix(AtlasRegion __0) => OriginalStateRuntime.Capture(__0);
}

internal static class AttachmentPatch
{
    internal static void Prefix(Attachment __0, AtlasRegion __1) => OriginalStateRuntime.Capture(__0, __1);
}

internal static class GeometryPatch
{
    internal static void Prefix(Skeleton __0, bool __1, Attachment __3)
    {
        if (!__1) OriginalStateRuntime.Capture(__0, __3);
    }
}

internal static class ApplySkeletonPatch
{
    internal static void Prefix(Skeleton __0, int __2, bool __3)
    {
        if (__3 || __2 >= 0) return;
        RestoreResult result = OriginalStateRuntime.Restore(__0);
        if (!result.Succeeded && result.Status != RestoreStatus.ValidationFailed)
            throw new InvalidOperationException($"Original state restore failed: {result.Status}:{result.Detail}");
        if (result.Status == RestoreStatus.ValidationFailed && result.Detail != "no-snapshot")
            throw new InvalidOperationException($"Original state restore validation failed: {result.Detail}");
    }
}

internal static class ReloadSkinsPatch
{
    // Main's Dump buttons (DumpCostume / ExtractAll) call RequestIndexOnlyRefresh() and then ReloadSkins(); Main's
    // ReloadSkins consumes the flag and only re-indexes skin directories (replacement caches and live skeletons are
    // left alone, nothing re-applies afterwards). Restoring the original baseline there left gameplay on the
    // original skin until the next full reload (BUG-053), so an index-only refresh is passed through untouched.
    private static MethodInfo? _indexOnlyRequested;
    private static bool _indexOnlyResolved;

    // Main's ReloadSkins starts with GameplayReplacementPublicationRegistry.InvalidateAll() and
    // AtlasOwnershipManager.InvalidateAll() BEFORE it checks the index-only flag. AtlasOwnershipManager.InvalidateAll
    // runs CleanupState(state, true) which Object.Destroy()s every live renderer's runtime atlas texture. A normal Reload
    // rebuilds them in ReloadAssets right after; the Dump refresh does not, so the live character showed broken
    // textures and elfins vanished (BUG-053, Cecil-verified 2026-11-13). Both calls are skipped for an index-only refresh.
    private static Action? _invalidatePublications, _invalidateOwnership;

    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo? publications = AccessTools.TypeByName("SkinForger.CharacterReplacement.GameplayReplacementPublicationRegistry")?
            .GetMethod("InvalidateAll", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        MethodInfo? ownership = AccessTools.TypeByName("SkinForger.AtlasOwnershipFix.AtlasOwnershipManager")?
            .GetMethod("InvalidateAll", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        if (publications != null) _invalidatePublications = (Action)Delegate.CreateDelegate(typeof(Action), publications);
        if (ownership != null) _invalidateOwnership = (Action)Delegate.CreateDelegate(typeof(Action), ownership);
        int replaced = 0;
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.opcode == System.Reflection.Emit.OpCodes.Call && instruction.operand is MethodInfo m)
            {
                if (publications != null && m == publications) { instruction.operand = AccessTools.Method(typeof(ReloadSkinsPatch), nameof(GuardedInvalidatePublications)); replaced++; }
                else if (ownership != null && m == ownership) { instruction.operand = AccessTools.Method(typeof(ReloadSkinsPatch), nameof(GuardedInvalidateOwnership)); replaced++; }
            }
            yield return instruction;
        }
        if (replaced != 2) MelonLogger.Warning($"[SkinForge][OriginalStateRestore] ReloadSkins invalidate guard replaced={replaced} (expected 2)");
    }

    public static void GuardedInvalidatePublications()
    {
        if (IsIndexOnlyRefresh()) return;
        _invalidatePublications?.Invoke();
    }

    public static void GuardedInvalidateOwnership()
    {
        if (IsIndexOnlyRefresh())
        {
            MelonLogger.Msg("[SkinForge][OriginalStateRestore] RELOAD_SKINS index-only: live runtime atlases kept (Main InvalidateAll skipped)");
            return;
        }
        _invalidateOwnership?.Invoke();
    }

    private static bool IsIndexOnlyRefresh()
    {
        if (!_indexOnlyResolved)
        {
            _indexOnlyResolved = true;
            _indexOnlyRequested = AccessTools.TypeByName("SkinForger.DumpSystem.DumpSessionManager")?
                .GetMethod("IsIndexOnlyRefreshRequested", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (_indexOnlyRequested == null)
                MelonLogger.Warning("[SkinForge][OriginalStateRestore] DumpSessionManager.IsIndexOnlyRefreshRequested not found; dump refresh guard off");
        }
        try { return _indexOnlyRequested?.Invoke(null, null) is true; }
        catch { return false; }
    }

    internal static bool Prefix()
    {
        if (IsIndexOnlyRefresh())
        {
            MelonLogger.Msg("[SkinForge][OriginalStateRestore] RELOAD_SKINS index-only (dump refresh) baseline=kept");
            return true;
        }
        RestoreResult result = OriginalStateRuntime.RestoreAllAndClear();
        if (result.Succeeded || result.Detail == "no-snapshot") return true;
        MelonLogger.Error($"[SkinForge][OriginalStateRestore] RELOAD_SKINS_BLOCKED status={result.Status} reason={result.Detail}");
        return false;
    }
}

internal static class ReloadAssetsPatch
{
    internal static bool Prefix() => OriginalStateRuntime.PrepareAssetReload();
}

internal static class OriginalStateRuntime
{
    private const string Prefix = "[SkinForge][OriginalStateRestore]";
    private static readonly object Sync = new();
    private static readonly Dictionary<long, AttachmentBaseline> Attachments = new();
    private static readonly Dictionary<long, RegionBaseline> Regions = new();
    private static readonly Dictionary<long, PageBaseline> Pages = new();
    private static readonly Dictionary<int, MaterialBaseline> Materials = new();
    private static MethodInfo? _mainReloadSkins;
    private static bool _cacheInvalidationPending;

    [ThreadStatic]
    private static Stack<BuildContext>? _builds;

    private static BuildContext? Current => _builds is { Count: > 0 } ? _builds.Peek() : null;

    internal static void Configure(Type host)
    {
        _mainReloadSkins = host.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Single(method => method.Name == "ReloadSkins" && method.GetParameters().Length == 0);
    }

    internal static bool PrepareAssetReload()
    {
        bool hasSnapshots;
        lock (Sync)
            hasSnapshots = Attachments.Count != 0 || Regions.Count != 0 || Pages.Count != 0 || Materials.Count != 0;
        ReloadPreparationResult result = SnapshotCore.PrepareAssetReload(
            hasSnapshots,
            _cacheInvalidationPending,
            RestoreAllAndClear,
            () =>
            {
                try
                {
                    (_mainReloadSkins ?? throw new MissingMethodException("SkinForgerMod.ReloadSkins")).Invoke(null, null);
                }
                catch (TargetInvocationException ex) when (ex.InnerException != null)
                {
                    throw ex.InnerException;
                }
            });
        _cacheInvalidationPending = result.CacheInvalidationPending;
        if (!result.Proceed)
        {
            MelonLogger.Error($"{Prefix} ASSET_RELOAD_REBASE status=blocked stage={result.Stage} reason={result.Detail}");
            return false;
        }
        if (result.Rebased)
            MelonLogger.Msg($"{Prefix} ASSET_RELOAD_REBASE status=ready baseline=restored replacementCaches=invalidated");
        return true;
    }

    internal static void EnterBuild(Skeleton? skeleton, string? skin, string? textureName, bool elfin)
    {
        bool active = !elfin && skeleton != null && skeleton.Pointer != IntPtr.Zero && !IsPreviewExecution();
        (_builds ??= new Stack<BuildContext>()).Push(new BuildContext(
            active ? skeleton : null, skin ?? string.Empty, textureName ?? string.Empty, active));
    }

    internal static void ExitBuild(Exception? error)
    {
        if (_builds is not { Count: > 0 }) return;
        BuildContext context = _builds.Pop();
        if (!context.Active) return;
        MelonLogger.Msg($"{Prefix} CAPTURE_SUMMARY skeleton=0x{context.SkeletonKey:X} skin={Display(context.Skin)} " +
            $"atlas={Display(context.TextureName)} attachments={context.Attachments} regions={context.Regions} " +
            $"materials={context.Materials} result={(error == null ? "captured" : "build-failed")}");
    }

    /// <summary>True inside a Character Gameplay build that this module records (not Preview, not Elfin).</summary>
    internal static bool CaptureActive => Current is { Active: true };

    internal static void Capture(AtlasRegion? region)
    {
        BuildContext? context = Current;
        if (context is not { Active: true } || region == null || region.Pointer == IntPtr.Zero) return;
        lock (Sync)
        {
            long key = region.Pointer.ToInt64();
            if (!Regions.TryGetValue(key, out RegionBaseline? baseline))
            {
                baseline = RegionBaseline.Capture(region);
                baseline.Atlas = AtlasNameOf(region, context);
                Regions.Add(key, baseline);
                context.Regions++;
            }
            baseline.Owners.Add(context.SkeletonKey);
            CapturePage(region.page, context);
            CaptureMaterial(MaterialOf(region), context);
        }
    }

    internal static void Capture(Attachment? attachment, AtlasRegion? region)
    {
        BuildContext? context = Current;
        if (context is not { Active: true } || attachment == null || attachment.Pointer == IntPtr.Zero) return;
        Capture(region);
        lock (Sync)
        {
            long key = attachment.Pointer.ToInt64();
            if (!Attachments.TryGetValue(key, out AttachmentBaseline? baseline))
            {
                baseline = AttachmentBaseline.Capture(attachment, region);
                baseline.Atlas = AtlasNameOf(region, context);
                Attachments.Add(key, baseline);
                context.Attachments++;
            }
            baseline.Owners.Add(context.SkeletonKey);
            CaptureMaterial(MaterialOf(region), context);
        }
    }

    internal static void Capture(Skeleton? skeleton, Attachment? attachment)
    {
        BuildContext? context = Current;
        if (context is not { Active: true } || skeleton == null || attachment == null ||
            skeleton.Pointer.ToInt64() != context.SkeletonKey) return;
        Capture(attachment, RegionOf(attachment));
    }

    internal static RestoreResult Restore(Skeleton? skeleton)
    {
        if (skeleton == null || skeleton.Pointer == IntPtr.Zero)
            return new RestoreResult(RestoreStatus.ValidationFailed, "no-snapshot");
        return RestoreCore(skeleton.Pointer.ToInt64(), clear: false);
    }

    internal static RestoreResult RestoreAllAndClear() => RestoreCore(null, clear: true);

    /// <summary>
    /// Puts back the game's own state for one atlas, whichever skeleton captured it. Called before every
    /// build so that a build never reads what the previous build left behind: deriving the next canvas from
    /// an already enlarged region is what made the Maid's parts grow a little on each rebuild.
    /// </summary>
    internal static RestoreResult RestoreForAtlas(string? atlas)
    {
        string name = Normalize(atlas);
        return name.Length == 0 ? new RestoreResult(RestoreStatus.ValidationFailed, "no-snapshot") : RestoreCore(null, clear: false, atlas: name);
    }

    private static string Normalize(string? atlas)
    {
        if (string.IsNullOrEmpty(atlas)) return string.Empty;
        string name = atlas!.Replace('\\', '/');
        int slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];
        int dot = name.LastIndexOf('.');
        return (dot > 0 ? name[..dot] : name).Trim();
    }

    private static string AtlasNameOf(AtlasRegion? region, BuildContext context)
    {
        try
        {
            string page = Normalize(region?.page?.name);
            if (page.Length > 0) return page;
        }
        catch (Exception) { }
        return Normalize(context.TextureName);
    }

    private static RestoreResult RestoreCore(long? skeletonKey, bool clear) => RestoreCore(skeletonKey, clear, atlas: null);

    private static RestoreResult RestoreCore(long? skeletonKey, bool clear, string? atlas)
    {
        lock (Sync)
        {
            int staleMaterials = PruneDestroyedMaterials();
            if (staleMaterials != 0)
                MelonLogger.Warning($"{Prefix} STALE_BASELINE_PRUNED materials={staleMaterials} reason=destroyed-runtime-resource");
            AttachmentBaseline[] attachments = Select(Attachments.Values, skeletonKey, atlas);
            RegionBaseline[] regions = Select(Regions.Values, skeletonKey, atlas);
            PageBaseline[] pages = Select(Pages.Values, skeletonKey, atlas);
            MaterialBaseline[] materials = Select(Materials.Values, skeletonKey, atlas);
            if (attachments.Length == 0 && regions.Length == 0 && pages.Length == 0 && materials.Length == 0)
                return clear && staleMaterials != 0
                    ? new RestoreResult(RestoreStatus.Restored, "stale-pruned")
                    : new RestoreResult(RestoreStatus.ValidationFailed, "no-snapshot");

            string? validation = ValidateAll(attachments, regions, pages, materials);
            if (validation != null)
            {
                MelonLogger.Warning($"{Prefix} ORIGINAL_RESTORE status=validation-failed reason={validation}");
                return new RestoreResult(RestoreStatus.ValidationFailed, validation);
            }

            AttachmentMutable[] currentAttachments = attachments.Select(item => item.Current()).ToArray();
            RegionMutable[] currentRegions = regions.Select(item => item.Current()).ToArray();
            PageMutable[] currentPages = pages.Select(item => item.Current()).ToArray();
            MaterialMutable[] currentMaterials = materials.Select(item => item.Current()).ToArray();
            var timer = Stopwatch.StartNew();
            RestoreResult result = SnapshotCore.Execute(
                () => ValidateAll(attachments, regions, pages, materials),
                () => ApplyAll(attachments, regions, pages, materials),
                () => VerifyAll(attachments, regions, pages, materials),
                () => ApplyAll(currentAttachments, currentRegions, currentPages, currentMaterials));
            timer.Stop();

            MelonLogger.Msg($"{Prefix} ORIGINAL_RESTORE skeleton={(skeletonKey.HasValue ? $"0x{skeletonKey.Value:X}" : "all")} " +
                $"status={result.Status} attachments={attachments.Length} regions={regions.Length} pages={pages.Length} " +
                $"materials={materials.Length} elapsedMs={timer.Elapsed.TotalMilliseconds:0.###} detail={result.Detail}");
            if (result.Succeeded && clear)
            {
                Attachments.Clear(); Regions.Clear(); Pages.Clear(); Materials.Clear();
            }
            return result;
        }
    }

    private static int PruneDestroyedMaterials()
    {
        int[] stale = Materials.Where(pair => pair.Value.IsDestroyed()).Select(pair => pair.Key).ToArray();
        foreach (int key in stale) Materials.Remove(key);
        return stale.Length;
    }

    private static T[] Select<T>(IEnumerable<T> values, long? owner, string? atlas = null) where T : IOwnedBaseline =>
        values.Where(item => (!owner.HasValue || item.Owners.Contains(owner.Value)) &&
                             (atlas == null || string.Equals(item.Atlas, atlas, StringComparison.OrdinalIgnoreCase))).ToArray();

    private static string? ValidateAll(IEnumerable<AttachmentBaseline> attachments,
        IEnumerable<RegionBaseline> regions, IEnumerable<PageBaseline> pages, IEnumerable<MaterialBaseline> materials)
    {
        foreach (AttachmentBaseline item in attachments) { string? error = item.Validate(); if (error != null) return error; }
        foreach (RegionBaseline item in regions) { string? error = item.Validate(); if (error != null) return error; }
        foreach (PageBaseline item in pages) { string? error = item.Validate(); if (error != null) return error; }
        foreach (MaterialBaseline item in materials) { string? error = item.Validate(); if (error != null) return error; }
        return null;
    }

    private static string? VerifyAll(IEnumerable<AttachmentBaseline> attachments,
        IEnumerable<RegionBaseline> regions, IEnumerable<PageBaseline> pages, IEnumerable<MaterialBaseline> materials)
    {
        foreach (AttachmentBaseline item in attachments) { string? error = item.Verify(); if (error != null) return error; }
        foreach (RegionBaseline item in regions) { string? error = item.Verify(); if (error != null) return error; }
        foreach (PageBaseline item in pages) { string? error = item.Verify(); if (error != null) return error; }
        foreach (MaterialBaseline item in materials) { string? error = item.Verify(); if (error != null) return error; }
        return null;
    }

    private static void ApplyAll(IEnumerable<AttachmentBaseline> attachments,
        IEnumerable<RegionBaseline> regions, IEnumerable<PageBaseline> pages, IEnumerable<MaterialBaseline> materials)
    {
        foreach (PageBaseline item in pages) item.Apply();
        foreach (RegionBaseline item in regions) item.Apply();
        foreach (AttachmentBaseline item in attachments) item.Apply();
        foreach (MaterialBaseline item in materials) item.Apply();
    }

    private static void ApplyAll(IEnumerable<AttachmentMutable> attachments,
        IEnumerable<RegionMutable> regions, IEnumerable<PageMutable> pages, IEnumerable<MaterialMutable> materials)
    {
        foreach (PageMutable item in pages) item.Apply();
        foreach (RegionMutable item in regions) item.Apply();
        foreach (AttachmentMutable item in attachments) item.Apply();
        foreach (MaterialMutable item in materials) item.Apply();
    }

    private static void CapturePage(AtlasPage? page, BuildContext context)
    {
        if (page == null || page.Pointer == IntPtr.Zero) return;
        long key = page.Pointer.ToInt64();
        if (!Pages.TryGetValue(key, out PageBaseline? baseline))
        {
            baseline = PageBaseline.Capture(page);
            Pages.Add(key, baseline);
        }
        baseline.Owners.Add(context.SkeletonKey);
    }

    private static void CaptureMaterial(Material? material, BuildContext context)
    {
        if (material == null || (UnityObject)material == null) return;
        int key = material.GetInstanceID();
        if (!Materials.TryGetValue(key, out MaterialBaseline? baseline))
        {
            baseline = MaterialBaseline.Capture(material);
            Materials.Add(key, baseline);
            context.Materials++;
        }
        baseline.Owners.Add(context.SkeletonKey);
    }

    private static AtlasRegion? RegionOf(Attachment attachment) =>
        attachment.TryCast<IHasRendererObject>()?.RendererObject?.TryCast<AtlasRegion>();

    private static Material? MaterialOf(AtlasRegion? region) =>
        region?.page?.rendererObject?.TryCast<Material>();

    private static bool IsPreviewExecution()
    {
        Type? type = AccessTools.TypeByName("SkinForger.CharacterReplacement.ReplacementExecutionContext");
        object? current = type?.GetProperty("Current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(null);
        object? preview = current?.GetType().GetProperty("Preview",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(current);
        return preview != null && Convert.ToBoolean(preview);
    }

    private static string Display(string value) => string.IsNullOrEmpty(value) ? "<none>" : value;

    private sealed class BuildContext
    {
        internal BuildContext(Skeleton? skeleton, string skin, string textureName, bool active)
        {
            SkeletonKey = skeleton?.Pointer.ToInt64() ?? 0;
            Skin = skin; TextureName = textureName; Active = active;
        }
        internal long SkeletonKey { get; }
        internal string Skin { get; }
        internal string TextureName { get; }
        internal bool Active { get; }
        internal int Attachments { get; set; }
        internal int Regions { get; set; }
        internal int Materials { get; set; }
    }
}

internal interface IOwnedBaseline
{
    HashSet<long> Owners { get; }

    /// <summary>
    /// The atlas this baseline belongs to, without extension. Ownership alone is not enough to find the
    /// baselines a build must put back: Spine skeleton DATA — attachments and atlas regions — is SHARED by
    /// every skeleton of the same character, so the second skeleton of the Maid has no ownership of the
    /// very objects the first skeleton already enlarged, and restoring "its own" baselines restored
    /// nothing (2026-11-18). The atlas is what a build is about, so that is what gets put back.
    /// </summary>
    string Atlas { get; set; }
}

internal sealed class AttachmentBaseline : IOwnedBaseline
{
    private readonly Attachment _attachment;
    private readonly long _pointer;
    private readonly string _name;
    private readonly Il2CppSystem.Object? _rendererObject;
    private readonly long _rendererPointer;
    private readonly MeshMutable? _mesh;
    private readonly RegionAttachmentMutable? _quad;
    private readonly int[] _bones;
    private readonly int[] _triangles;
    private readonly int _worldVerticesLength;
    private readonly long _deformPointer;
    private readonly long _parentPointer;

    private AttachmentBaseline(Attachment attachment, AtlasRegion? region)
    {
        _attachment = attachment;
        _pointer = attachment.Pointer.ToInt64();
        _name = attachment.Name ?? string.Empty;
        IHasRendererObject? renderer = attachment.TryCast<IHasRendererObject>();
        _rendererObject = renderer?.RendererObject;
        _rendererPointer = Ptr(_rendererObject);
        MeshAttachment? mesh = attachment.TryCast<MeshAttachment>();
        RegionAttachment? quad = attachment.TryCast<RegionAttachment>();
        if (mesh != null)
        {
            _mesh = MeshMutable.Capture(mesh, _rendererObject);
            _bones = Copy(mesh.bones);
            _triangles = Copy(mesh.triangles);
            _worldVerticesLength = mesh.worldVerticesLength;
            _deformPointer = Ptr(mesh.deformAttachment);
            _parentPointer = Ptr(mesh.parentMesh);
        }
        else
        {
            _bones = Array.Empty<int>(); _triangles = Array.Empty<int>();
            _quad = quad == null ? null : RegionAttachmentMutable.Capture(quad, _rendererObject);
        }
    }

    public HashSet<long> Owners { get; } = new();
    public string Atlas { get; set; } = string.Empty;

    internal static AttachmentBaseline Capture(Attachment attachment, AtlasRegion? region) => new(attachment, region);

    internal string? Validate()
    {
        if (_attachment.Pointer.ToInt64() != _pointer) return $"attachment-destroyed:{_name}";
        IHasRendererObject? renderer = _attachment.TryCast<IHasRendererObject>();
        if (renderer == null || Ptr(renderer.RendererObject) != _rendererPointer)
            return $"attachment-region-identity-changed:{_name}";
        MeshAttachment? mesh = _attachment.TryCast<MeshAttachment>();
        if (_mesh != null)
        {
            if (mesh == null) return $"attachment-type-changed:{_name}";
            if (mesh.worldVerticesLength != _worldVerticesLength ||
                !SnapshotCore.Equal(_bones, Copy(mesh.bones)) || !SnapshotCore.Equal(_triangles, Copy(mesh.triangles)) ||
                Ptr(mesh.deformAttachment) != _deformPointer || Ptr(mesh.parentMesh) != _parentPointer)
                return $"mesh-contract-changed:{_name}";
            if (!_mesh.LengthsMatch(mesh)) return $"mesh-array-length-changed:{_name}";
        }
        else if (_quad != null)
        {
            RegionAttachment? quad = _attachment.TryCast<RegionAttachment>();
            if (quad == null) return $"attachment-type-changed:{_name}";
            if (!_quad.LengthsMatch(quad)) return $"quad-array-length-changed:{_name}";
        }
        return null;
    }

    internal AttachmentMutable Current() => AttachmentMutable.Capture(_attachment);

    internal void Apply()
    {
        IHasRendererObject? renderer = _attachment.TryCast<IHasRendererObject>();
        if (renderer != null) renderer.RendererObject = _rendererObject;
        if (_mesh != null) _mesh.Apply(_attachment.TryCast<MeshAttachment>()!);
        if (_quad != null) _quad.Apply(_attachment.TryCast<RegionAttachment>()!);
    }

    internal string? Verify()
    {
        string? validation = Validate();
        if (validation != null) return validation;
        if (_mesh != null && !_mesh.Equals(_attachment.TryCast<MeshAttachment>()!)) return $"mesh-verify-failed:{_name}";
        if (_quad != null && !_quad.Equals(_attachment.TryCast<RegionAttachment>()!)) return $"quad-verify-failed:{_name}";
        return null;
    }

    private static long Ptr(Il2CppObjectBase? value) => value?.Pointer.ToInt64() ?? 0;
    private static int[] Copy(Il2CppStructArray<int>? source)
    {
        if (source == null) return Array.Empty<int>();
        int[] result = new int[source.Length];
        for (int index = 0; index < result.Length; index++) result[index] = source[index];
        return result;
    }
}

internal sealed class RegionBaseline : IOwnedBaseline
{
    private readonly RegionMutable _state;
    private RegionBaseline(AtlasRegion region) => _state = RegionMutable.Capture(region);
    public HashSet<long> Owners { get; } = new();
    public string Atlas { get; set; } = string.Empty;
    internal static RegionBaseline Capture(AtlasRegion region) => new(region);
    internal string? Validate() => _state.ValidateIdentity();
    internal RegionMutable Current() => RegionMutable.Capture(_state.Region);
    internal void Apply() => _state.Apply();
    internal string? Verify() => _state.EqualsCurrent() ? null : "region-verify-failed";
}

internal sealed class PageBaseline : IOwnedBaseline
{
    private readonly PageMutable _state;
    private PageBaseline(AtlasPage page) => _state = PageMutable.Capture(page);
    public HashSet<long> Owners { get; } = new();
    public string Atlas { get; set; } = string.Empty;
    internal static PageBaseline Capture(AtlasPage page) => new(page);
    internal string? Validate() => _state.ValidateIdentity();
    internal PageMutable Current() => PageMutable.Capture(_state.Page);
    internal void Apply() => _state.Apply();
    internal string? Verify() => _state.EqualsCurrent() ? null : "page-verify-failed";
}

internal sealed class MaterialBaseline : IOwnedBaseline
{
    private readonly MaterialMutable _state;
    private MaterialBaseline(Material material) => _state = MaterialMutable.Capture(material);
    public HashSet<long> Owners { get; } = new();
    public string Atlas { get; set; } = string.Empty;
    internal static MaterialBaseline Capture(Material material) => new(material);
    internal bool IsDestroyed() => _state.IsDestroyed();
    internal string? Validate() => _state.ValidateIdentity();
    internal MaterialMutable Current() => MaterialMutable.Capture(_state.Material);
    internal void Apply() => _state.Apply();
    internal string? Verify() => _state.EqualsCurrent() ? null : "material-verify-failed";
}

internal sealed class AttachmentMutable
{
    private readonly Attachment _attachment;
    private readonly Il2CppSystem.Object? _renderer;
    private readonly MeshMutable? _mesh;
    private readonly RegionAttachmentMutable? _quad;

    private AttachmentMutable(Attachment attachment)
    {
        _attachment = attachment;
        _renderer = attachment.TryCast<IHasRendererObject>()?.RendererObject;
        MeshAttachment? mesh = attachment.TryCast<MeshAttachment>();
        RegionAttachment? quad = attachment.TryCast<RegionAttachment>();
        _mesh = mesh == null ? null : MeshMutable.Capture(mesh, _renderer);
        _quad = quad == null ? null : RegionAttachmentMutable.Capture(quad, _renderer);
    }

    internal static AttachmentMutable Capture(Attachment attachment) => new(attachment);
    internal void Apply()
    {
        IHasRendererObject? renderer = _attachment.TryCast<IHasRendererObject>();
        if (renderer != null) renderer.RendererObject = _renderer;
        if (_mesh != null) _mesh.Apply(_attachment.TryCast<MeshAttachment>()!);
        if (_quad != null) _quad.Apply(_attachment.TryCast<RegionAttachment>()!);
    }
}

internal sealed class MeshMutable
{
    private readonly float[] _vertices;
    private readonly float[] _regionUvs;
    private readonly float[] _uvs;
    private readonly Il2CppSystem.Object? _renderer;
    private readonly float _u, _v, _u2, _v2;
    private readonly bool _rotate;
    private readonly int _degrees;

    private MeshMutable(MeshAttachment mesh, Il2CppSystem.Object? renderer)
    {
        _vertices = Copy(mesh.vertices); _regionUvs = Copy(mesh.regionUVs); _uvs = Copy(mesh.uvs);
        _renderer = renderer;
        _u = mesh.RegionU; _v = mesh.RegionV; _u2 = mesh.RegionU2; _v2 = mesh.RegionV2;
        _rotate = mesh.RegionRotate; _degrees = mesh.RegionDegrees;
    }

    internal static MeshMutable Capture(MeshAttachment mesh, Il2CppSystem.Object? renderer) => new(mesh, renderer);
    internal bool LengthsMatch(MeshAttachment mesh) =>
        _vertices.Length == (mesh.vertices?.Length ?? 0) &&
        _regionUvs.Length == (mesh.regionUVs?.Length ?? 0) && _uvs.Length == (mesh.uvs?.Length ?? 0);

    internal void Apply(MeshAttachment mesh)
    {
        mesh.vertices = Floats(_vertices); mesh.regionUVs = Floats(_regionUvs);
        mesh.RegionU = _u; mesh.RegionV = _v; mesh.RegionU2 = _u2; mesh.RegionV2 = _v2;
        mesh.RegionRotate = _rotate; mesh.RegionDegrees = _degrees;
        mesh.uvs = Floats(_uvs);
    }

    internal bool Equals(MeshAttachment mesh) =>
        Equal(_vertices, mesh.vertices) && Equal(_regionUvs, mesh.regionUVs) && Equal(_uvs, mesh.uvs) &&
        Bits(_u, mesh.RegionU) && Bits(_v, mesh.RegionV) && Bits(_u2, mesh.RegionU2) && Bits(_v2, mesh.RegionV2) &&
        _rotate == mesh.RegionRotate && _degrees == mesh.RegionDegrees;

    private static float[] Copy(Il2CppStructArray<float>? source)
    {
        if (source == null) return Array.Empty<float>();
        float[] result = new float[source.Length];
        for (int index = 0; index < result.Length; index++) result[index] = source[index];
        return result;
    }
    private static Il2CppStructArray<float> Floats(float[] values)
    {
        var result = new Il2CppStructArray<float>(values.Length);
        for (int index = 0; index < values.Length; index++) result[index] = values[index];
        return result;
    }
    private static bool Equal(float[] expected, Il2CppStructArray<float>? actual)
    {
        if (actual == null || expected.Length != actual.Length) return false;
        for (int i = 0; i < expected.Length; i++) if (!Bits(expected[i], actual[i])) return false;
        return true;
    }
    private static bool Bits(float left, float right) => BitConverter.SingleToInt32Bits(left) == BitConverter.SingleToInt32Bits(right);
}

internal sealed class RegionAttachmentMutable
{
    private readonly float _x, _y, _scaleX, _scaleY, _rotation, _width, _height;
    private readonly float[] _offset, _uvs;
    private readonly Il2CppSystem.Object? _renderer;

    private RegionAttachmentMutable(RegionAttachment quad, Il2CppSystem.Object? renderer)
    {
        _x = quad.x; _y = quad.y; _scaleX = quad.scaleX; _scaleY = quad.scaleY;
        _rotation = quad.rotation; _width = quad.width; _height = quad.height;
        _offset = Copy(quad.offset); _uvs = Copy(quad.uvs); _renderer = renderer;
    }

    internal static RegionAttachmentMutable Capture(RegionAttachment quad, Il2CppSystem.Object? renderer) => new(quad, renderer);
    internal bool LengthsMatch(RegionAttachment quad) =>
        _offset.Length == (quad.offset?.Length ?? 0) && _uvs.Length == (quad.uvs?.Length ?? 0);
    internal void Apply(RegionAttachment quad)
    {
        quad.x = _x; quad.y = _y; quad.scaleX = _scaleX; quad.scaleY = _scaleY;
        quad.rotation = _rotation; quad.width = _width; quad.height = _height;
        quad.offset = Floats(_offset); quad.uvs = Floats(_uvs);
    }
    internal bool Equals(RegionAttachment quad) =>
        Bits(_x, quad.x) && Bits(_y, quad.y) && Bits(_scaleX, quad.scaleX) && Bits(_scaleY, quad.scaleY) &&
        Bits(_rotation, quad.rotation) && Bits(_width, quad.width) && Bits(_height, quad.height) &&
        Equal(_offset, quad.offset) && Equal(_uvs, quad.uvs);

    private static float[] Copy(Il2CppStructArray<float>? source)
    {
        if (source == null) return Array.Empty<float>();
        float[] result = new float[source.Length];
        for (int index = 0; index < result.Length; index++) result[index] = source[index];
        return result;
    }
    private static Il2CppStructArray<float> Floats(float[] values)
    {
        var result = new Il2CppStructArray<float>(values.Length);
        for (int index = 0; index < values.Length; index++) result[index] = values[index];
        return result;
    }
    private static bool Equal(float[] expected, Il2CppStructArray<float>? actual)
    {
        if (actual == null || expected.Length != actual.Length) return false;
        for (int i = 0; i < expected.Length; i++) if (!Bits(expected[i], actual[i])) return false;
        return true;
    }
    private static bool Bits(float left, float right) => BitConverter.SingleToInt32Bits(left) == BitConverter.SingleToInt32Bits(right);
}

internal sealed class RegionMutable
{
    internal AtlasRegion Region { get; }
    private readonly long _pointer, _pagePointer;
    private readonly int _x, _y, _width, _height, _originalWidth, _originalHeight, _degrees;
    private readonly float _offsetX, _offsetY, _u, _v, _u2, _v2;
    private readonly bool _rotate;

    private RegionMutable(AtlasRegion region)
    {
        Region = region; _pointer = region.Pointer.ToInt64(); _pagePointer = region.page?.Pointer.ToInt64() ?? 0;
        _x = region.x; _y = region.y; _width = region.width; _height = region.height;
        _originalWidth = region.originalWidth; _originalHeight = region.originalHeight;
        _offsetX = region.offsetX; _offsetY = region.offsetY; _u = region.u; _v = region.v; _u2 = region.u2; _v2 = region.v2;
        _rotate = region.rotate; _degrees = region.degrees;
    }
    internal static RegionMutable Capture(AtlasRegion region) => new(region);
    internal string? ValidateIdentity() => Region.Pointer.ToInt64() != _pointer ? "region-destroyed" :
        (Region.page?.Pointer.ToInt64() ?? 0) != _pagePointer ? "region-page-identity-changed" : null;
    internal void Apply()
    {
        Region.x = _x; Region.y = _y; Region.width = _width; Region.height = _height;
        Region.originalWidth = _originalWidth; Region.originalHeight = _originalHeight;
        Region.offsetX = _offsetX; Region.offsetY = _offsetY; Region.u = _u; Region.v = _v; Region.u2 = _u2; Region.v2 = _v2;
        Region.rotate = _rotate; Region.degrees = _degrees;
    }
    internal bool EqualsCurrent() => ValidateIdentity() == null && Region.x == _x && Region.y == _y &&
        Region.width == _width && Region.height == _height && Region.originalWidth == _originalWidth &&
        Region.originalHeight == _originalHeight && Bits(Region.offsetX, _offsetX) && Bits(Region.offsetY, _offsetY) &&
        Bits(Region.u, _u) && Bits(Region.v, _v) && Bits(Region.u2, _u2) && Bits(Region.v2, _v2) &&
        Region.rotate == _rotate && Region.degrees == _degrees;
    private static bool Bits(float left, float right) => BitConverter.SingleToInt32Bits(left) == BitConverter.SingleToInt32Bits(right);
}

internal sealed class PageMutable
{
    internal AtlasPage Page { get; }
    private readonly long _pointer, _rendererPointer;
    private readonly int _width, _height;
    private PageMutable(AtlasPage page)
    {
        Page = page; _pointer = page.Pointer.ToInt64(); _rendererPointer = page.rendererObject?.Pointer.ToInt64() ?? 0;
        _width = page.width; _height = page.height;
    }
    internal static PageMutable Capture(AtlasPage page) => new(page);
    internal string? ValidateIdentity() => Page.Pointer.ToInt64() != _pointer ? "page-destroyed" :
        (Page.rendererObject?.Pointer.ToInt64() ?? 0) != _rendererPointer ? "page-renderer-identity-changed" : null;
    internal void Apply() { Page.width = _width; Page.height = _height; }
    internal bool EqualsCurrent() => ValidateIdentity() == null && Page.width == _width && Page.height == _height;
}

internal sealed class MaterialMutable
{
    internal Material Material { get; }
    private readonly int _instanceId, _renderQueue;
    private readonly Texture? _texture;
    private readonly long _texturePointer;
    private MaterialMutable(Material material)
    {
        Material = material; _instanceId = material.GetInstanceID(); _renderQueue = material.renderQueue;
        _texture = material.mainTexture; _texturePointer = _texture?.Pointer.ToInt64() ?? 0;
    }
    internal static MaterialMutable Capture(Material material) => new(material);
    internal bool IsDestroyed() => (UnityObject)Material == null ||
        (_texture != null && (UnityObject)_texture == null);
    internal string? ValidateIdentity() => (UnityObject)Material == null || Material.GetInstanceID() != _instanceId
        ? "material-destroyed" : _texture != null && (UnityObject)_texture == null ? "source-texture-destroyed" : null;
    internal void Apply() { Material.mainTexture = _texture; Material.renderQueue = _renderQueue; }
    internal bool EqualsCurrent() => ValidateIdentity() == null && Material.renderQueue == _renderQueue &&
        (Material.mainTexture?.Pointer.ToInt64() ?? 0) == _texturePointer;
}
