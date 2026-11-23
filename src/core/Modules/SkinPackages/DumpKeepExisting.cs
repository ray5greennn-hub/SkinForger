using System.Reflection;
using HarmonyLib;
using MelonLoader;

namespace SkinForger.SkinPackages;

/// <summary>
/// A Dump never overwrites an image that is already there (2026-11-18, user rule).
///
/// Pressing Dump again used to rewrite every PNG of the costume with the game's own art, which quietly
/// destroyed whatever the creator had painted since the last Dump. The reason to press it again is almost
/// always the opposite one: something is missing — a part that was never dumped, or a file that was
/// deleted on purpose to get it back. So a Dump now FILLS IN, and only that:
///
///   * an image that is not on disk is written, as before;
///   * an image that exists is kept exactly as it is, and not even encoded (so a repeat Dump is quick);
///   * manifest.json is still rewritten, because it is metadata about the game's regions, not artwork;
///   * the anchor sidecars were already written only when missing (Main: WriteAnchorMetadataIfMissing).
///
/// To get the game's version of one part back, delete that PNG and press Dump. To start over, delete the
/// costume folder.
///
/// Every Dump path goes through DumpSessionManager.EncodeAndWritePng(path, texture), so one prefix there
/// covers the normal Dump, the other skins' atlases and Dump All alike.
/// </summary>
internal static class DumpKeepExisting
{
    private const string Prefix = "[SkinForge][Dump]";

    private static int _kept, _written;

    internal static int Install(HarmonyLib.Harmony harmony)
    {
        try
        {
            Type session = AccessTools.TypeByName("SkinForger.DumpSystem.DumpSessionManager")
                           ?? throw new TypeLoadException("DumpSessionManager");
            MethodInfo write = AccessTools.DeclaredMethod(session, "EncodeAndWritePng",
                                   new[] { typeof(string), typeof(UnityEngine.Texture2D) })
                               ?? throw new MissingMethodException("EncodeAndWritePng");
            MethodInfo? end = AccessTools.DeclaredMethod(session, "End", Type.EmptyTypes);

            harmony.Patch(write, prefix: new HarmonyMethod(typeof(DumpKeepExisting), nameof(WritePrefix)));
            int patched = 1;
            if (end != null)
            {
                harmony.Patch(end, postfix: new HarmonyMethod(typeof(DumpKeepExisting), nameof(EndPostfix)));
                patched++;
            }
            MelonLogger.Msg($"{Prefix} a Dump fills in what is missing and never overwrites an image that is already there");
            return patched;
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"{Prefix} images are NOT protected from being overwritten: {exception.GetBaseException().Message}");
            return 0;
        }
    }

    /// <summary>False keeps the file that is already on disk (and skips encoding it).</summary>
    private static bool WritePrefix(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                _written++;
                return true;
            }
        }
        catch (Exception)
        {
            return true;   // cannot tell: write, as before
        }
        _kept++;
        return false;
    }

    private static void EndPostfix()
    {
        if (_kept > 0 || _written > 0)
            MelonLogger.Msg($"{Prefix} {_written} image(s) written, {_kept} kept " +
                            (_kept > 0 ? "(already on disk — delete a file and dump again to get the game's version back)" : string.Empty));
        _kept = _written = 0;
    }
}
