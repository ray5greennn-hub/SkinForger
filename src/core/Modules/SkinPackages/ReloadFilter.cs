using System.Reflection;
using HarmonyLib;
using MelonLoader;
using Il2CppSpine.Unity;
using UnityEngine;

namespace SkinForger.SkinPackages;

/// <summary>
/// Faster full Reload, especially with Preview open (2026-11-16, target &lt; 1 s; logs 16-44..16-53: 3.2-3.7 s).
/// Main's ReloadAssets re-applies skins to every Spine object Resources.FindObjectsOfTypeAll returns:
///  - the Preview's own skeleton (~1 s, a full gameplay atlas build of 2048x11058), although PreviewManager rebuilds the
///    preview right after the Reload anyway: skipped during ReloadAssets;
///  - prefab ASSETS that are in no scene (maid_girl_battle / maid_girl_main_show kept loaded as Preview templates,
///    ~0.8 s): nothing shows them, and their instances are skinned when they are created: skipped during ReloadAssets;
///  - every other object paid ~6 ms in Main's Scan (name looked up against every character / elfin config row,
///    twice): Scan's own answer is remembered per object name (the config does not change while the game runs).
/// Real Awake / OnEnable calls (objects created later) are never filtered.
///
/// Decoded images (2026-11-16, round 2; log 20-10: a full Reload decoded all 76 Maid PNGs again, ~0.7 s): Main's
/// ReloadSkins empties GameplayScope.DecodedTextures (without destroying them). Images whose file has the same size and
/// write time as when it was decoded are put back afterwards; only changed files are decoded again.
/// </summary>
internal static class ReloadFilter
{
    private const string Prefix = "[SkinForge][ReloadFilter]";
    private static bool _reloading;
    private static int _assets, _preview, _scanHits, _scanMisses;
    private static readonly Dictionary<(string Name, bool Elfin), int> ScanMemo = new();
    [ThreadStatic] private static (string Name, bool Elfin)? _pendingScan;

    internal static int Install(HarmonyLib.Harmony harmony)
    {
        Type host = AccessTools.TypeByName("SkinForger.SkinForgerMod") ?? throw new TypeLoadException("SkinForgerMod");
        int patched = 0;
        patched += InstallDecodeKeep(harmony, host);
        MethodInfo? reload = AccessTools.DeclaredMethod(host, "ReloadAssets", Type.EmptyTypes);
        if (reload != null)
        {
            harmony.Patch(reload, prefix: new HarmonyMethod(typeof(ReloadFilter), nameof(ReloadPrefix)) { priority = HarmonyLib.Priority.First },
                                  finalizer: new HarmonyMethod(typeof(ReloadFilter), nameof(ReloadFinalizer)));
            patched++;
        }
        foreach ((string name, string prefix) in new[] { ("SkeletonRendererAwake", nameof(RendererPrefix)), ("SkeletonGraphicAwake", nameof(GraphicPrefix)) })
        {
            MethodInfo? method = AccessTools.DeclaredMethod(host, name);
            if (method == null) continue;
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(ReloadFilter), prefix) { priority = HarmonyLib.Priority.First });
            patched++;
        }
        MethodInfo? scan = AccessTools.DeclaredMethod(host, "Scan");
        if (scan != null)
        {
            harmony.Patch(scan, prefix: new HarmonyMethod(typeof(ReloadFilter), nameof(ScanPrefix)) { priority = HarmonyLib.Priority.First },
                                postfix: new HarmonyMethod(typeof(ReloadFilter), nameof(ScanRecord)) { priority = HarmonyLib.Priority.First });
            patched++;
        }
        return patched;
    }

    private static void ReloadPrefix()
    {
        _reloading = true;
        _assets = _preview = _scanHits = _scanMisses = 0;
    }

    private static Exception? ReloadFinalizer(Exception? __exception)
    {
        _reloading = false;
        MelonLogger.Msg($"{Prefix} reload skipped {_preview} Preview object(s) (rebuilt after the reload) and {_assets} prefab asset(s) in no scene; " +
                        $"Scan answers reused {_scanHits}x, looked up {_scanMisses}x");
        return __exception;
    }

    private static bool Skip(Component? component)
    {
        if (!_reloading || component == null) return false;
        try
        {
            GameObject go = component.gameObject;
            if (!go.scene.IsValid()) { _assets++; return true; }
            for (Transform? t = go.transform; t != null; t = t.parent)
                if ((t.name ?? string.Empty).StartsWith("SkinForger Preview", StringComparison.Ordinal)) { _preview++; return true; }
        }
        catch { }
        return false;
    }

    private static bool RendererPrefix(SkeletonRenderer __0) => !Skip(__0);
    private static bool GraphicPrefix(SkeletonGraphic __0) => !Skip(__0);

    // ------------------------------------------------------------ decoded images kept across ReloadSkins

    private static FieldInfo? _gameplayScope;
    private static PropertyInfo? _decoded;
    /// <summary>path -> (texture, file length, write time) when Main decoded it.</summary>
    private static readonly Dictionary<string, (IntPtr Texture, long Length, long Ticks)> DecodedStamps = new(StringComparer.OrdinalIgnoreCase);
    private static List<(string Path, Texture2D Texture)>? _kept;

    private static int InstallDecodeKeep(HarmonyLib.Harmony harmony, Type host)
    {
        try
        {
            _gameplayScope = AccessTools.Field(host, "GameplayScope");
            _decoded = _gameplayScope == null ? null : AccessTools.Property(_gameplayScope.FieldType, "DecodedTextures");
            MethodInfo? reloadSkins = AccessTools.DeclaredMethod(host, "ReloadSkins", Type.EmptyTypes);
            MethodInfo? getTexture = AccessTools.GetDeclaredMethods(host).FirstOrDefault(m => m.Name == "GetTexture" && m.GetParameters().Length == 3);
            if (_decoded == null)
            {
                // 2026-11-20: the authored Main has no GameplayScope -- it keeps decoded images itself
                // (TextureBridge, keyed by each file's length and write time, so an edited file is read
                // again and an unchanged one is not). Nothing to add here, and nothing is missing.
                MelonLogger.Msg($"{Prefix} decoded-image reuse not needed: the authored Main keeps its own decoded images");
                return 0;
            }
            if (reloadSkins == null || getTexture == null) { MelonLogger.Warning($"{Prefix} decoded-image reuse not installed"); return 0; }
            harmony.Patch(getTexture, postfix: new HarmonyMethod(typeof(ReloadFilter), nameof(GetTexturePostfix)) { priority = HarmonyLib.Priority.Last });
            harmony.Patch(reloadSkins, prefix: new HarmonyMethod(typeof(ReloadFilter), nameof(ReloadSkinsPrefix)) { priority = HarmonyLib.Priority.First },
                                       finalizer: new HarmonyMethod(typeof(ReloadFilter), nameof(ReloadSkinsFinalizer)));
            return 2;
        }
        catch (Exception exception) { MelonLogger.Warning($"{Prefix} decoded-image reuse not installed: {exception.GetBaseException().Message}"); return 0; }
    }

    private static Dictionary<string, Texture2D>? Decoded() =>
        _gameplayScope?.GetValue(null) is { } scope ? _decoded!.GetValue(scope) as Dictionary<string, Texture2D> : null;

    private static void GetTexturePostfix(string __0, Texture2D __result)
    {
        try
        {
            if (__result == null || string.IsNullOrEmpty(__0) || VirtualFiles.IsVirtual(__0)) return;
            IntPtr pointer = __result.Pointer;
            if (DecodedStamps.TryGetValue(__0, out var known) && known.Texture == pointer) return;
            if (Decoded() is not { } decoded || !decoded.TryGetValue(__0, out Texture2D? cached) || cached == null || cached.Pointer != pointer) return;
            var file = new FileInfo(__0);
            if (file.Exists) DecodedStamps[__0] = (pointer, file.Length, file.LastWriteTimeUtc.Ticks);
        }
        catch { }
    }

    private static void ReloadSkinsPrefix()
    {
        _kept = null;
        try
        {
            if (Decoded() is not { Count: > 0 } decoded) return;
            _kept = decoded.Select(p => (p.Key, p.Value)).ToList();
        }
        catch { _kept = null; }
    }

    private static Exception? ReloadSkinsFinalizer(Exception? __exception)
    {
        List<(string Path, Texture2D Texture)>? kept = _kept;
        _kept = null;
        if (__exception != null || kept == null) return __exception;
        int restored = 0, changed = 0;
        try
        {
            if (Decoded() is not { } decoded) return null;
            foreach ((string path, Texture2D texture) in kept)
            {
                if (decoded.ContainsKey(path) || texture == null || texture.Pointer == IntPtr.Zero) continue;
                if (!DecodedStamps.TryGetValue(path, out var stamp) || stamp.Texture != texture.Pointer) { changed++; continue; }
                var file = new FileInfo(path);
                if (!file.Exists || file.Length != stamp.Length || file.LastWriteTimeUtc.Ticks != stamp.Ticks) { changed++; DecodedStamps.Remove(path); continue; }
                decoded[path] = texture;
                restored++;
            }
            MelonLogger.Msg($"{Prefix} kept {restored} decoded image(s) of unchanged files ({changed} decoded again)");
        }
        catch (Exception exception) { MelonLogger.Warning($"{Prefix} decoded-image reuse failed: {exception.GetBaseException().Message}"); }
        return null;
    }

    // ------------------------------------------------------------ Scan memo

    private static string ScanName(GameObject obj)
    {
        string text = obj.name ?? string.Empty;
        if (text == "default" && obj.transform != null && obj.transform.parent != null) text = obj.transform.parent.name;
        return text.Replace("(Clone)", string.Empty).ToLowerInvariant();
    }

    /// <summary>Main's Scan(GameObject, elfin): a remembered raw answer skips Main's config loops (other postfixes,
    /// e.g. RoleSkins, still run on it).</summary>
    private static bool ScanPrefix(GameObject __0, bool __1, ref int __result)
    {
        _pendingScan = null;
        if (__0 == null) return true;
        try
        {
            var key = (ScanName(__0), __1);
            if (ScanMemo.TryGetValue(key, out int known)) { __result = known; _scanHits++; return false; }
            _pendingScan = key;
            _scanMisses++;
        }
        catch { }
        return true;
    }

    /// <summary>Runs first among the postfixes, i.e. on Main's own answer.</summary>
    private static void ScanRecord(int __result)
    {
        if (_pendingScan is { } key) ScanMemo[key] = __result;
        _pendingScan = null;
    }
}
