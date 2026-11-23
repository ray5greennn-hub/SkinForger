using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace SkinForger.AtlasBuildAccelerator;

/// <summary>
/// 2026-11-16 (Reload round 3, user: "still too slow"). Two parts:
///
/// 1. Focus-delta skip. For every ENLARGED part Main's ApplyRegionPngToAtlas cut the original out of the atlas
///    (CreateEditableRegionTexture) and scanned the original and the new PNG pixel by pixel (CalculateAlphaBounds x2)
///    only to fill placement.OriginalBounds / ModifiedBounds / FocusDeltaX/Y. Checked with Cecil on Main 962A257B:
///    those values are read only inside ApplyRegionPngToAtlas itself, by debug log lines; in Core only FastReload's
///    placement diff looks at FocusDelta (now always 0 on both sides). Inside ApplyRegionPngToAtlas (and not inside
///    the 4-argument CreateEditableRegionTexture that Dump / AutoAnchor use) the crop returns null and the bounds scan
///    gets null, which Main already handles as "no pixels" -> delta 0.
///
/// 2. Profiler: inclusive time of Main's build steps, one log line per Reload ("[BuildProfile]"), so the next round
///    is measured, not guessed.
/// </summary>
internal static class BuildProfiler
{
    private const string Prefix = "[SkinForge][BuildProfile]";
    [ThreadStatic] private static int _applyDepth, _cropDepth;
    private static int _skippedCrops, _skippedScans;
    private static readonly Dictionary<string, (int Calls, double Ms)> Times = new();

    internal static void Install(HarmonyLib.Harmony harmony)
    {
        Type host = AccessTools.TypeByName("SkinForger.SkinForgerMod") ?? throw new TypeLoadException("SkinForgerMod");
        MethodInfo[] all = host.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        MethodInfo apply = all.Single(m => m.Name == "ApplyRegionPngToAtlas");
        MethodInfo crop4 = all.Single(m => m.Name == "CreateEditableRegionTexture" && m.GetParameters().Length == 4);
        MethodInfo crop5 = all.Single(m => m.Name == "CreateEditableRegionTexture" && m.GetParameters().Length == 5);
        MethodInfo bounds = all.Single(m => m.Name == "CalculateAlphaBounds");
        harmony.Patch(apply, prefix: H(nameof(ApplyEnter), Priority.First), finalizer: H(nameof(ApplyExit), Priority.Last));
        harmony.Patch(crop4, prefix: H(nameof(CropEnter), Priority.First), finalizer: H(nameof(CropExit), Priority.Last));
        harmony.Patch(crop5, prefix: H(nameof(Crop5Prefix), Priority.First));
        harmony.Patch(bounds, prefix: H(nameof(BoundsSkipPrefix), Priority.First));

        // ---- profiler
        int timed = 0;
        void Time(MethodBase? method)
        {
            if (method == null || method.IsAbstract || method.IsGenericMethodDefinition) return;
            try { harmony.Patch(method, prefix: H(nameof(TimeEnter), Priority.First), finalizer: H(nameof(TimeExit), Priority.Last)); timed++; }
            catch (Exception exception) { MelonLogger.Warning($"{Prefix} {method.Name} not timed: {exception.GetBaseException().Message}"); }
        }
        foreach (string name in new[]
        {
            "ApplyRegionPngToAtlas", "GetTexture", "GetRegionAnchor", "CreateEditableRegionTexture", "CalculateAlphaBounds",
            "CropEditableRegionForPacking", "AllocateExpandedPackedRect", "GetPackedPixelRect", "FindManifestEntry",
            "GetOrCreateSynthesizedAtlas", "EnsureAtlasCapacity", "RotateTexture", "PremultiplyAlphaInPlace",
            "CreateTransparentTexture", "ApplyRegionPlacementToAttachment", "ApplyRegionPlacementToRegion",
            "TryBuildSynthesizedAtlasForSkeleton", "ApplySkeleton", "SkeletonRendererAwake", "SkeletonGraphicAwake",
            "CharacterApplyOnEnable", "ReloadSkins", "GetMaterial",
        })
            foreach (MethodInfo method in all.Where(m => m.Name == name)) Time(method);
        foreach ((string type, string name) in new[]
        {
            ("SkinForger.CharacterReplacement.CharacterReplacementCore", "ApplyWithContext"),
            ("SkinForger.CharacterReplacement.GameplayReplacementPublicationRegistry", "Publish"),
            ("SkinForger.CharacterMeshGeometry.CharacterMeshGeometry", "Apply"),
            ("SkinForger.AtlasOwnershipFix.AtlasOwnershipManager", "Bind"),
        })
            // Looked up in Main's own assembly by name: AccessTools.TypeByName scans every assembly when a name misses
            // (2026-11-17 log: UIElementsModule type-load warning + "CharacterReplacementCore not found").
            if (host.Assembly.GetTypes().FirstOrDefault(x => x.FullName == type || x.Name == type.Substring(type.LastIndexOf('.') + 1)) is { } t)
                foreach (MethodInfo method in t.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Where(m => m.Name == name))
                    Time(method);
        MethodInfo? reload = all.FirstOrDefault(m => m.Name == "ReloadAssets" && m.GetParameters().Length == 0);
        if (reload != null) harmony.Patch(reload, prefix: H(nameof(ReloadStart), Priority.First), finalizer: H(nameof(ReloadReport), Priority.Last - 1));
        MelonLogger.Msg($"{Prefix} ready: focus-delta pixel scan skipped while building; {timed} Main methods timed (one line per Reload)");
    }

    private static HarmonyMethod H(string name, int priority) =>
        new(AccessTools.DeclaredMethod(typeof(BuildProfiler), name)) { priority = priority };

    // ------------------------------------------------------------ focus-delta skip

    private static void ApplyEnter() => _applyDepth++;
    private static Exception? ApplyExit(Exception? __exception) { if (_applyDepth > 0) _applyDepth--; return __exception; }
    private static void CropEnter() => _cropDepth++;
    private static Exception? CropExit(Exception? __exception) { if (_cropDepth > 0) _cropDepth--; return __exception; }

    /// <summary>The 5-argument crop called directly by ApplyRegionPngToAtlas (focus delta only): skipped.</summary>
    private static bool Crop5Prefix(ref Texture2D? __result)
    {
        if (_applyDepth == 0 || _cropDepth > 0) return true;
        __result = null;
        _skippedCrops++;
        return false;
    }

    /// <summary>Bounds scan inside ApplyRegionPngToAtlas: Main returns "no pixels" for null.</summary>
    private static void BoundsSkipPrefix(ref Texture2D? __0)
    {
        if (_applyDepth == 0 || _cropDepth > 0 || __0 == null) return;
        __0 = null;
        _skippedScans++;
    }

    // ------------------------------------------------------------ profiler

    private static void TimeEnter(out long __state) => __state = Stopwatch.GetTimestamp();

    private static Exception? TimeExit(Exception? __exception, long __state, MethodBase __originalMethod)
    {
        double ms = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
        string key = __originalMethod.DeclaringType?.Name + "." + __originalMethod.Name;
        lock (Times) { Times.TryGetValue(key, out var t); Times[key] = (t.Calls + 1, t.Ms + ms); }
        return __exception;
    }

    private static long _reloadStart;

    private static void ReloadStart()
    {
        lock (Times) Times.Clear();
        _skippedCrops = _skippedScans = 0;
        _reloadStart = Stopwatch.GetTimestamp();
    }

    private static Exception? ReloadReport(Exception? __exception)
    {
        try
        {
            double total = (Stopwatch.GetTimestamp() - _reloadStart) * 1000.0 / Stopwatch.Frequency;
            string parts;
            lock (Times)
                parts = string.Join(" ", Times.OrderByDescending(p => p.Value.Ms).Take(24).Select(p => $"{p.Key}={p.Value.Calls}x/{p.Value.Ms:0.#}ms"));
            MelonLogger.Msg($"{Prefix} reload ReloadAssets={total:0.#}ms focusSkipped=crops:{_skippedCrops},scans:{_skippedScans} | {parts}");
            lock (Times) Times.Clear();
        }
        catch { }
        return __exception;
    }
}
