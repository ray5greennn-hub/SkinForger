using System.Reflection;
using HarmonyLib;
using Il2CppSpine;
using MelonLoader;

namespace SkinForger.SkinPackages;

/// <summary>
/// Load-time caches (2026-11-16, "faster, smoother, lower CPU"). Log 12-21: every Maid battle object ran Main's
/// atlas build 4 times; the 3 builds with nothing new to place still cost 160-190 ms each, much of it looking up
/// which PNG belongs to each part (GetExistingRegionPath: file-existence checks) and reading PNG headers again.
/// Files only change on Reload / Dump / package changes, so both answers are remembered until then:
///  - GetExistingRegionPath(skin, attachment, region, textureName): final result (after every other postfix,
///    e.g. RoleSkins' "untouched other-skin image" filter) per skin | texture | attachment | region;
///  - PNG header sizes per path (CharacterPolygonPackedRectRepair).
/// Cleared by Main's ReloadAssets / ReloadSkins (prefix), FastReload's incremental Reload and every Dump.
/// </summary>
internal static class LoadCaches
{
    private const string Prefix = "[SkinForge][LoadCaches]";
    private static readonly Dictionary<string, string?> RegionPaths = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, (int W, int H)> PngSizes = new(StringComparer.OrdinalIgnoreCase);
    private static int _hits, _misses;
    [ThreadStatic] private static string? _pendingKey;

    internal static int Install(HarmonyLib.Harmony harmony)
    {
        Type host = AccessTools.TypeByName("SkinForger.SkinForgerMod") ?? throw new TypeLoadException("SkinForgerMod");
        int patched = 0;
        MethodInfo? regionPath = AccessTools.DeclaredMethod(host, "GetExistingRegionPath");
        if (regionPath != null)
        {
            harmony.Patch(regionPath,
                prefix: new HarmonyMethod(typeof(LoadCaches), nameof(RegionPathPrefix)) { priority = HarmonyLib.Priority.First },
                postfix: new HarmonyMethod(typeof(LoadCaches), nameof(RegionPathPostfix)) { priority = HarmonyLib.Priority.Last });
            patched++;
        }
        foreach (string name in new[] { "ReloadAssets", "ReloadSkins", "DumpCostume", "ExtractAll" })
        {
            MethodInfo? method = host.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault(m => m.Name == name);
            if (method == null) continue;
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(LoadCaches), nameof(Clear)) { priority = HarmonyLib.Priority.First },
                                  postfix: new HarmonyMethod(typeof(LoadCaches), nameof(Clear)));
            patched++;
        }
        return patched;
    }

    /// <summary>Forget everything (files may have changed).</summary>
    internal static void Clear()
    {
        if (RegionPaths.Count > 0 || PngSizes.Count > 0)
            MelonLogger.Msg($"{Prefix} cleared: {RegionPaths.Count} region paths, {PngSizes.Count} PNG sizes (hits {_hits}, misses {_misses})");
        RegionPaths.Clear();
        PngSizes.Clear();
        _hits = _misses = 0;
    }

    private static string Key(string skin, Attachment? attachment, AtlasRegion? region, string texture)
    {
        string attachmentName = attachment?.Name ?? string.Empty;
        string regionName = region?.name ?? string.Empty;
        return skin + "|" + texture + "|" + attachmentName + "|" + regionName;
    }

    private static bool RegionPathPrefix(string __0, Attachment __1, AtlasRegion __2, string __3, ref string __result)
    {
        _pendingKey = null;
        if (string.IsNullOrEmpty(__0) || __1 == null || __2 == null || DumpIsolation.Active) return true;
        string key = Key(__0, __1, __2, __3 ?? string.Empty);
        if (RegionPaths.TryGetValue(key, out string? cached))
        {
            _hits++;
            __result = cached!;
            return false;   // the postfixes still run; they leave a final answer unchanged
        }
        _misses++;
        _pendingKey = key;
        return true;
    }

    private static void RegionPathPostfix(string __result)
    {
        if (_pendingKey == null) return;
        RegionPaths[_pendingKey] = __result;
        _pendingKey = null;
    }

    /// <summary>PNG header size of a skin image, read once until the next Reload / Dump.</summary>
    internal static bool TryGetPngSize(string path, Func<string, (bool Ok, int W, int H)> read, out int width, out int height)
    {
        if (PngSizes.TryGetValue(path, out var size)) { width = size.W; height = size.H; return true; }
        (bool ok, int w, int h) = read(path);
        width = w;
        height = h;
        if (ok) PngSizes[path] = (w, h);
        return ok;
    }
}
