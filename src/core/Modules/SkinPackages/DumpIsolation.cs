using System.Reflection;
using HarmonyLib;
using MelonLoader;

namespace SkinForger.SkinPackages;

/// <summary>
/// Dump must read the game's original atlases. With a skin applied (a .sfskin package or the creator's Root),
/// the live atlas is SkinForger's synthesized one, so cutting it with the original region rects produced fragments
/// (2026-11-14: face contained hair, hair_3 contained the speaker) — and it would also export a package's art.
///
/// Main's DumpCostume / ExtractAll run synchronously. Before them, every costume being dumped that has a skin
/// selected is switched to Original in memory and Main's ReloadAssets restores the original atlases (the verified
/// Original path). A finalizer (runs even on failure) puts the selections back and reapplies them.
/// Saved.json is not touched while suspended.
/// </summary>
internal static class DumpIsolation
{
    private const string Prefix = "[SkinForge][DumpIsolation]";
    private static FieldInfo? _selectedCharacters, _selectedElfins, _savedCharactersPath, _savedElfinsPath;
    private static MethodInfo? _reloadAssets, _saveSelection;
    private static readonly Dictionary<(bool Elfin, int Index), int> Suspended = new();
    private static int _depth;

    internal static bool Active => _depth > 0;

    internal static int Install(HarmonyLib.Harmony harmony)
    {
        Type host = AccessTools.TypeByName("SkinForger.SkinForgerMod") ?? throw new TypeLoadException("SkinForger.SkinForgerMod");
        const BindingFlags all = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        _selectedCharacters = host.GetField("SelectedCharacters", all);
        _selectedElfins = host.GetField("SelectedElfins", all);
        _savedCharactersPath = host.GetField("_savedCharactersPath", all);
        _savedElfinsPath = host.GetField("_savedElfinsPath", all);
        _reloadAssets = AccessTools.DeclaredMethod(host, "ReloadAssets", Type.EmptyTypes);
        _saveSelection = host.GetMethods(all).FirstOrDefault(m => m.Name == "SaveSelection" && m.GetParameters().Length == 2);
        if (_selectedCharacters == null || _selectedElfins == null || _reloadAssets == null || _saveSelection == null)
            throw new MissingMemberException("Main selection contract");

        int patched = 0;
        MethodInfo? dumpCostume = AccessTools.DeclaredMethod(host, "DumpCostume", new[] { typeof(int), typeof(bool) });
        if (dumpCostume != null)
        {
            harmony.Patch(dumpCostume,
                prefix: new HarmonyMethod(typeof(DumpIsolation), nameof(DumpCostumePrefix)) { priority = HarmonyLib.Priority.First },
                finalizer: new HarmonyMethod(typeof(DumpIsolation), nameof(DumpFinalizer)) { priority = HarmonyLib.Priority.Last });
            patched++;
        }
        MethodInfo? getSkinPath = AccessTools.DeclaredMethod(host, "GetSkinPath", new[] { typeof(int), typeof(int), typeof(bool) });
        if (getSkinPath != null)
        {
            harmony.Patch(getSkinPath, prefix: new HarmonyMethod(typeof(DumpIsolation), nameof(GetSkinPathPrefix)) { priority = HarmonyLib.Priority.First });
            patched++;
        }
        // Completeness: Main's ExtractSkeleton exports only each slot's CURRENT attachment. Expression / action parts
        // (angry_*, shy_*, face_2..6, fx_atk_* ...) are swapped in by animations and were missing (129 instead of 197
        // for Bassist). The postfix exports every Region / Mesh attachment of every skin, like LiveAttachmentDump 1.7.0.
        MethodInfo? extractSkeleton = AccessTools.DeclaredMethod(host, "ExtractSkeleton");
        _extractAttachment = AccessTools.DeclaredMethod(host, "ExtractAttachment");
        if (extractSkeleton != null && _extractAttachment != null)
        {
            harmony.Patch(extractSkeleton, postfix: new HarmonyMethod(typeof(DumpIsolation), nameof(ExtractSkeletonPostfix)) { priority = HarmonyLib.Priority.Last });
            patched++;
        }
        MethodInfo? extractAll = AccessTools.DeclaredMethod(host, "ExtractAll", Type.EmptyTypes);
        if (extractAll != null)
        {
            harmony.Patch(extractAll,
                prefix: new HarmonyMethod(typeof(DumpIsolation), nameof(DumpAllPrefix)) { priority = HarmonyLib.Priority.First },
                finalizer: new HarmonyMethod(typeof(DumpIsolation), nameof(DumpFinalizer)) { priority = HarmonyLib.Priority.Last });
            patched++;
        }
        return patched;
    }

    private static bool _dumpAll;
    private static MethodInfo? _extractAttachment;

    /// <summary>Exports the attachments of every skin of this skeleton that the slots are not showing right now.
    /// Main's ExtractAttachment skips files that already exist and HexieBlock still blocks censored parts.</summary>
    private static void ExtractSkeletonPostfix(Il2CppSpine.Skeleton skeleton, int index, bool elfin)
    {
        try
        {
            Il2CppSpine.SkeletonData? data = skeleton?.Data;
            if (data == null || _extractAttachment == null) return;
            int slotCount = data.slots?.Count ?? 0;
            var skins = new List<Il2CppSpine.Skin>();
            if (data.defaultSkin != null) skins.Add(data.defaultSkin);
            for (int i = 0; i < (data.skins?.Count ?? 0); i++)
            {
                Il2CppSpine.Skin? skin = data.skins!.Items[i];
                if (skin != null && !skins.Any(s => s.Pointer == skin.Pointer)) skins.Add(skin);
            }
            var done = new HashSet<IntPtr>();
            var slots = skeleton!.Slots;
            for (int i = 0; i < (slots?.Count ?? 0); i++)
            {
                var current = slots!.Items[i]?.Attachment;
                if (current != null) done.Add(current.Pointer);   // Main already exported these
            }
            var entries = new Il2CppSystem.Collections.Generic.List<Il2CppSpine.Skin.SkinEntry>();
            int extra = 0;
            foreach (Il2CppSpine.Skin skin in skins)
                for (int slot = 0; slot < slotCount; slot++)
                {
                    entries.Clear();
                    skin.GetAttachments(slot, entries);
                    for (int e = 0; e < entries.Count; e++)
                    {
                        Il2CppSpine.Attachment? attachment = entries[e]?.Attachment;
                        if (attachment == null || !done.Add(attachment.Pointer)) continue;
                        if (attachment.TryCast<Il2CppSpine.MeshAttachment>() == null && attachment.TryCast<Il2CppSpine.RegionAttachment>() == null) continue;
                        _extractAttachment.Invoke(null, new object[] { attachment, index, elfin });
                        extra++;
                    }
                }
            if (extra > 0)
                MelonLogger.Msg($"{Prefix} skeleton {data.name}: {extra} attachment(s) not on screen (expressions, actions, other skins) offered to Dump");
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"{Prefix} complete dump skipped: {exception.GetBaseException().Message}");
        }
    }

    /// <summary>While a costume is being dumped Main sees "no skin" for it, so spawned prefabs keep the original atlas.</summary>
    private static bool GetSkinPathPrefix(int index, bool elfin, ref string __result)
    {
        if (_depth <= 0 || !(_dumpAll || Isolated.Contains((elfin, index)))) return true;
        __result = string.Empty;
        return false;
    }

    private static readonly HashSet<(bool Elfin, int Index)> Isolated = new();

    private static Dictionary<int, int>? Selection(bool elfin) =>
        (elfin ? _selectedElfins : _selectedCharacters)!.GetValue(null) as Dictionary<int, int>;

    private static void DumpCostumePrefix(int index, bool elfin)
    {
        if (_depth++ > 0) return;   // already isolated (Dump All -> DumpCostume)
        Isolated.Add((elfin, index));
        try
        {
            if (Selection(elfin) is { } selection && selection.TryGetValue(index, out int current) && current >= 0)
            {
                Suspended[(elfin, index)] = current;
                selection[index] = -1;
            }
            Isolate("Dump Costume");
        }
        catch (Exception exception) { MelonLogger.Warning($"{Prefix} isolate failed: {exception.GetBaseException().Message}"); }
    }

    private static void DumpAllPrefix()
    {
        if (_depth++ > 0) return;
        _dumpAll = true;
        try
        {
            foreach (bool elfin in new[] { false, true })
                if (Selection(elfin) is { } selection)
                    foreach (int index in selection.Keys.ToList())
                        if (selection[index] >= 0)
                        {
                            Suspended[(elfin, index)] = selection[index];
                            selection[index] = -1;
                        }
            Isolate("Dump All");
        }
        catch (Exception exception) { MelonLogger.Warning($"{Prefix} isolate failed: {exception.GetBaseException().Message}"); }
    }

    private static void Isolate(string mode)
    {
        // Even without a selection the shared atlas may still carry a previous skin: restore whenever anything is isolated.
        if (Suspended.Count == 0) return;
        MelonLogger.Msg($"{Prefix} {mode}: {Suspended.Count} skin(s) set to Original while dumping so only the game's original images are read");
        _reloadAssets!.Invoke(null, null);
    }

    private static Exception? DumpFinalizer(Exception? __exception)
    {
        if (--_depth > 0) return __exception;
        _depth = 0;
        Isolated.Clear();
        _dumpAll = false;
        if (Suspended.Count == 0) return __exception;
        try
        {
            foreach (((bool elfin, int index), int position) in Suspended)
                if (Selection(elfin) is { } selection) selection[index] = position;
            foreach (bool elfin in new[] { false, true })
                if (Suspended.Keys.Any(k => k.Elfin == elfin))
                    _saveSelection!.Invoke(null, new object?[] { Selection(elfin), (elfin ? _savedElfinsPath : _savedCharactersPath)?.GetValue(null) });
            MelonLogger.Msg($"{Prefix} dump finished: {Suspended.Count} skin(s) restored");
            Suspended.Clear();
            _reloadAssets!.Invoke(null, null);
        }
        catch (Exception exception)
        {
            Suspended.Clear();
            MelonLogger.Warning($"{Prefix} restore failed: {exception.GetBaseException().Message}");
        }
        return __exception;
    }
}
