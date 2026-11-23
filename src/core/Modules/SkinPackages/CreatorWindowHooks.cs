using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SkinForger.SkinPackages;

/// <summary>
/// Keeps skin packages out of the creator window (key 1); the Skin Library (key 0) is the only place for them.
///
/// Main still has the Skin Library skin in its list (that is how it gets loaded), but its button is not drawn:
///  - GetSkinLabel postfix returns a marker label for Skin Library paths;
///  - a transpiler on DrawSelectionList replaces the skin-button call with <see cref="SkinButton"/>, which draws
///    nothing for that marker and draws Root green while the Skin Library skin (which contains Root's edits on top)
///    is in use.
/// Each character with enabled skins has two hidden Skin Library skins: "skins only" and "skins + my work"
/// (PackageMounts.MyWorkFolder). Clicking Root selects "skins + my work" (SaveSelection prefix) and Root is drawn green
/// while it is in use. Root's work is never used unless the player clicked Root (2026-11-15).
/// Turning the skins off in the Skin Library gives the plain Root again.
/// </summary>
internal static class CreatorWindowHooks
{
    private const string Hidden = "\u0001sf-skin-library";
    private static FieldInfo? _skins, _selectedCharacters, _selectedElfins;
    private static MethodInfo? _getCostumeName;
    private static string _lastCostume = string.Empty, _lastPath = string.Empty;

    internal static int Install(HarmonyLib.Harmony harmony)
    {
        Type host = AccessTools.TypeByName("SkinForger.SkinForgerMod") ?? throw new TypeLoadException("SkinForger.SkinForgerMod");
        const BindingFlags all = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        _skins = host.GetField("Skins", all);
        _selectedCharacters = host.GetField("SelectedCharacters", all);
        _selectedElfins = host.GetField("SelectedElfins", all);
        _getCostumeName = AccessTools.DeclaredMethod(host, "GetCostumeName", new[] { typeof(int), typeof(bool) });
        MethodInfo label = AccessTools.DeclaredMethod(host, "GetSkinLabel") ?? throw new MissingMethodException("GetSkinLabel");
        MethodInfo list = AccessTools.DeclaredMethod(host, "DrawSelectionList") ?? throw new MissingMethodException("DrawSelectionList");
        MethodInfo? save = host.GetMethods(all).FirstOrDefault(m => m.Name == "SaveSelection" && m.GetParameters().Length == 2);
        harmony.Patch(label, postfix: new HarmonyMethod(typeof(CreatorWindowHooks), nameof(LabelPostfix)) { priority = HarmonyLib.Priority.Last });
        harmony.Patch(list, transpiler: new HarmonyMethod(typeof(CreatorWindowHooks), nameof(ListTranspiler)));
        if (save != null)
            harmony.Patch(save, prefix: new HarmonyMethod(typeof(CreatorWindowHooks), nameof(SavePrefix)) { priority = HarmonyLib.Priority.First });
        return save != null ? 3 : 2;
    }

    private static void LabelPostfix(string costume, string skinPath, ref string __result)
    {
        _lastCostume = costume ?? string.Empty;
        _lastPath = skinPath ?? string.Empty;
        if (Same(skinPath, PackageMounts.Find(costume, false)?.VirtualDirectory) || Same(skinPath, PackageMounts.Find(costume, true)?.VirtualDirectory))
            __result = Hidden;
    }

    /// <summary>Replaces the call GUILayout.Button(string, options) that follows GetSkinLabel(...).</summary>
    private static IEnumerable<CodeInstruction> ListTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo replacement = AccessTools.DeclaredMethod(typeof(CreatorWindowHooks), nameof(SkinButton));
        bool afterLabel = false, replaced = false;
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.operand is MethodInfo method)
            {
                if (method.Name == "GetSkinLabel") afterLabel = true;
                else if (afterLabel && !replaced && method.Name == "Button" && method.DeclaringType == typeof(GUILayout) &&
                         method.GetParameters().Length == 2 && method.GetParameters()[0].ParameterType == typeof(string) &&
                         method.GetParameters()[1].ParameterType == typeof(GUILayoutOption[]))
                {
                    instruction.operand = replacement;
                    instruction.opcode = System.Reflection.Emit.OpCodes.Call;
                    replaced = true;
                }
            }
            yield return instruction;
        }
        if (!replaced) MelonLoader.MelonLogger.Warning("[SkinForge][SkinPackages] creator window: skin button call not found, Skin Library buttons stay visible");
    }

    private static bool SkinButton(string text, GUILayoutOption[] options)
    {
        if (text == Hidden) return false;   // the Skin Library skin: no button in the creator window
        Color content = GUI.contentColor;
        if (RootStandsForLibrary()) GUI.contentColor = Color.green;
        bool clicked = GUILayout.Button(text, options);
        GUI.contentColor = content;
        return clicked;
    }

    /// <summary>True when the button being drawn is Root of a character whose Skin Library skin is selected.</summary>
    private static bool RootStandsForLibrary()
    {
        if (PackageMounts.Find(_lastCostume, true) is not { } skin) return false;   // "skins + my work" = Root is on
        if (!Same(_lastPath, Path.Combine(SkinForgerPaths.Creator, _lastCostume))) return false;
        if (_skins?.GetValue(null) is not Dictionary<string, List<string>> skins || !skins.TryGetValue(_lastCostume, out List<string>? list)) return false;
        int position = list.FindIndex(p => Same(p, skin.VirtualDirectory));
        return position >= 0 && IndexesOf(_lastCostume).Any(k => Selection(k.Elfin)?.TryGetValue(k.Index, out int s) == true && s == position);
    }

    /// <summary>Clicking Root on a character with a Skin Library skin selects the Skin Library skin (Root on top).</summary>
    private static void SavePrefix(Dictionary<int, int> source)
    {
        try
        {
            if (DumpIsolation.Active || source == null || _getCostumeName == null) return;
            if (_skins?.GetValue(null) is not Dictionary<string, List<string>> skins) return;
            bool elfin = ReferenceEquals(source, Selection(true));
            if (!elfin && !ReferenceEquals(source, Selection(false))) return;
            foreach (int index in source.Keys.ToList())
            {
                int position = source[index];
                if (position < 0) continue;
                string costume = _getCostumeName.Invoke(null, new object[] { index, elfin }) as string ?? string.Empty;
                if (PackageMounts.Find(costume, true) is not { } skin || !skins.TryGetValue(costume, out List<string>? list) || position >= list.Count) continue;
                if (!Same(list[position], Path.Combine(SkinForgerPaths.Creator, costume))) continue;
                int library = list.FindIndex(p => Same(p, skin.VirtualDirectory));
                if (library >= 0) source[index] = library;
            }
        }
        catch { }
    }

    // ------------------------------------------------------------ helpers

    private static readonly Dictionary<string, List<(bool Elfin, int Index)>> CostumeIndexes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Called after every ReloadSkins: costume name -> its rows (character / elfin index).</summary>
    internal static void RefreshCostumeIndexes(Func<int, bool, string> costumeName, int characters, int elfins)
    {
        CostumeIndexes.Clear();
        foreach ((bool elfin, int count) in new[] { (false, characters), (true, elfins) })
            for (int i = 0; i < count; i++)
            {
                string name = costumeName(i, elfin);
                if (string.IsNullOrEmpty(name)) continue;
                if (!CostumeIndexes.TryGetValue(name, out var rows)) CostumeIndexes[name] = rows = new();
                rows.Add((elfin, i));
            }
    }

    private static IEnumerable<(bool Elfin, int Index)> IndexesOf(string costume) =>
        CostumeIndexes.TryGetValue(costume, out var rows) ? rows : Enumerable.Empty<(bool, int)>();

    private static Dictionary<int, int>? Selection(bool elfin) => (elfin ? _selectedElfins : _selectedCharacters)?.GetValue(null) as Dictionary<int, int>;

    private static bool Same(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try { return string.Equals(VirtualFiles.Normalize(a), VirtualFiles.Normalize(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}
