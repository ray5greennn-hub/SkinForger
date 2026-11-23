using System.Reflection;
using HarmonyLib;
using MelonLoader;

namespace SkinForger.SkinPackages;

/// <summary>
/// replaced\ history rules (2026-11-16, user):
///  - only images are kept: JSON files are never copied into replaced\;
///  - Settings "Record edit history" off: nothing is copied at all.
/// Prefix on Main's ReplacedBackup.BackupFile(source, protectUnknown) (used by Reload, FastReload and SkinForger's
/// own writes). When skipped, the backup state is NOT advanced, so an edited image still differs from its recorded
/// hash and is still recognised as edited (exports, other-skin atlases).
/// </summary>
internal static class EditHistory
{
    private static int _skippedJson, _skippedOff;

    internal static int Install(HarmonyLib.Harmony harmony)
    {
        Type? backup = AccessTools.TypeByName("SkinForger.ReplacedBackup");
        MethodInfo? method = backup == null ? null : AccessTools.DeclaredMethod(backup, "BackupFile", new[] { typeof(string), typeof(bool) });
        if (method == null) { MelonLogger.Warning("[SkinForge][EditHistory] ReplacedBackup.BackupFile not found"); return 0; }
        harmony.Patch(method, prefix: new HarmonyMethod(typeof(EditHistory), nameof(BackupPrefix)) { priority = HarmonyLib.Priority.First });
        return 1;
    }

    private static bool BackupPrefix(string source, ref string __result)
    {
        if (!string.IsNullOrEmpty(source) && source.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            _skippedJson++;
            __result = null!;
            return false;
        }
        if (!SfSettings.RecordEditHistory)
        {
            if (_skippedOff++ == 0) MelonLogger.Msg("[SkinForge][EditHistory] edit history is off (Settings): images are not copied to replaced\\");
            __result = null!;
            return false;
        }
        return true;
    }
}
