using System.Reflection;
using HarmonyLib;
using MelonLoader;

namespace SkinForger.SkinPackages;

/// <summary>
/// Selection thumbnail dumped together with the character / elfin (2026-11-17, user: "it is one picture, no separate
/// page"). Main's creator window had a "Sprites | Selection Thumbnail" switch above each list and a separate Dump for
/// the thumbnail (SelectionThumbnailManager.Dump(listIndex, elfin, hexie) -> &lt;Costume&gt;\SelectionThumbnail.png, the
/// same folder level as Lilith\SelectionThumbnail.png). Now:
///  - DumpCostume(index, elfin) postfix also dumps that thumbnail (listIndex = the same index: both come from
///    GetCostumeName(i, elfin)); an existing SelectionThumbnail.png is kept (it may be edited). Never the hexie one.
///  - DrawCategory(elfin) is not drawn and always answers "Sprites", so the list is the normal one.
/// The skin-switch icons (Icon\) were already dumped with the character (RoleSkins).
/// </summary>
internal static class ThumbnailWithDump
{
    private const string Prefix = "[SkinForge][Thumbnail]";
    private static MethodInfo? _dump, _directory;

    internal static int Install(HarmonyLib.Harmony harmony)
    {
        Type? host = AccessTools.TypeByName("SkinForger.SkinForgerMod");
        Type? manager = AccessTools.TypeByName("SkinForger.SelectionThumbnail.SelectionThumbnailManager");
        if (host == null || manager == null) { MelonLogger.Warning($"{Prefix} Main types not found"); return 0; }
        _dump = AccessTools.DeclaredMethod(manager, "Dump", new[] { typeof(int), typeof(bool), typeof(bool) });
        _directory = AccessTools.DeclaredMethod(manager, "ActiveOrDumpDirectory", new[] { typeof(int), typeof(bool) });
        MethodInfo? costume = AccessTools.DeclaredMethod(host, "DumpCostume", new[] { typeof(int), typeof(bool) });
        MethodInfo? category = AccessTools.DeclaredMethod(manager, "DrawCategory", new[] { typeof(bool) });
        int patched = 0;
        if (costume != null && _dump != null)
        {
            harmony.Patch(costume, postfix: new HarmonyMethod(typeof(ThumbnailWithDump), nameof(DumpPostfix)) { priority = Priority.Low });
            patched++;
        }
        if (category != null)
        {
            harmony.Patch(category, prefix: new HarmonyMethod(typeof(ThumbnailWithDump), nameof(CategoryPrefix)) { priority = Priority.First });
            patched++;
        }
        return patched;
    }

    private static void DumpPostfix(int __0, bool __1)
    {
        try
        {
            if (_directory?.Invoke(null, new object[] { __0, __1 }) is string folder && folder.Length > 0 &&
                File.Exists(Path.Combine(folder, "SelectionThumbnail.png")))
                return;   // already there (maybe edited): kept
            _dump!.Invoke(null, new object[] { __0, __1, false });
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"{Prefix} selection thumbnail not dumped: {exception.GetBaseException().Message}");
        }
    }

    /// <summary>No "Sprites | Selection Thumbnail" switch: always the normal list.</summary>
    private static bool CategoryPrefix(ref bool __result)
    {
        __result = true;
        return false;
    }
}
