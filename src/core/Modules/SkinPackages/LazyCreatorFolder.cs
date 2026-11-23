using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MelonLoader;

namespace SkinForger.SkinPackages;

/// <summary>
/// First start of a player who only plays skins (2026-11-17, user): the game folder gets SkinForger\ and
/// SkinForger\Skins\ only. SkinForger\SkinCreator\ appears when the player starts creating (Dump, Root / Original,
/// Text page, settings saved ...), never just by starting the game.
///
/// Main (Cecil-read, 962A257B) builds SkinCreator at start-up: EnsureInitialized creates SkinCreator\Images and writes
/// Menukey.txt, ReloadSkins creates SkinCreator\Images, CustomLoadingPicManager.Initialize / LoadingConfig.Read create
/// CustomLodingPic and write its config.json, FpsProtectionConfig.Load writes SkinForgeFpsLimiter.json.
/// In those methods only (transpiled before Main initializes) Directory.CreateDirectory / File.WriteAllText for a path
/// inside SkinCreator do nothing while SkinCreator does not exist; the default file contents are kept in memory and
/// VirtualFiles serves them (FileExists / ReadAllText). Once SkinCreator exists (first Dump ...), they act normally.
/// VirtualFiles also lists a missing SkinCreator folder as empty instead of throwing.
///
/// LoadingPaths.Skins also MIGRATES an old layout: when SkinCreator\Images is missing and SkinForger\Skins exists,
/// Main moves Skins to SkinCreator\Images. Since Skins is now the PLAYER folder (.sfskin packages), that move would
/// take every installed skin away. It only runs when Skins holds no .sfskin file (a real old creator layout).
/// </summary>
internal static class LazyCreatorFolder
{
    private const string Prefix = "[SkinForge][LazyCreatorFolder]";
    private static readonly ConcurrentDictionary<string, string> Pending = new(StringComparer.OrdinalIgnoreCase);
    private static string? _creatorRoot;

    private static string CreatorRoot => _creatorRoot ??= Path.GetFullPath(SkinForgerPaths.CreatorRoot);

    internal static bool InCreator(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            string full = Path.GetFullPath(path);
            return full.Equals(CreatorRoot, StringComparison.OrdinalIgnoreCase) ||
                   full.StartsWith(CreatorRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool CreatorMissing => !Directory.Exists(CreatorRoot);

    // ------------------------------------------------------------ replacements called from Main's transpiled IL

    public static DirectoryInfo CreateDirectory(string path)
    {
        if (InCreator(path) && CreatorMissing) return new DirectoryInfo(path);
        return Directory.CreateDirectory(path);
    }

    public static void WriteAllText(string path, string? contents)
    {
        if (InCreator(path) && !Directory.Exists(Path.GetDirectoryName(Path.GetFullPath(path))))
        {
            Pending[Path.GetFullPath(path)] = contents ?? string.Empty;
            return;
        }
        File.WriteAllText(path, contents);
    }

    public static void Move(string source, string destination)
    {
        try
        {
            if (Directory.Exists(source) && Directory.EnumerateFiles(source, "*.sfskin", SearchOption.AllDirectories).Any())
            {
                MelonLogger.Msg($"{Prefix} not moving {source} to {destination}: it is the player's skin folder (.sfskin packages)");
                return;
            }
            if (Directory.Exists(source) && !Directory.EnumerateFileSystemEntries(source).Any()) return;   // empty: nothing to migrate
        }
        catch { return; }
        Directory.Move(source, destination);
    }

    // ------------------------------------------------------------ VirtualFiles fall-backs

    internal static bool TryPending(string? path, out string contents)
    {
        contents = string.Empty;
        if (string.IsNullOrEmpty(path) || Pending.IsEmpty) return false;
        try { return Pending.TryGetValue(Path.GetFullPath(path), out contents!); } catch { return false; }
    }

    internal static bool ListAsEmpty(string? path) => InCreator(path) && !Directory.Exists(path);

    // ------------------------------------------------------------ install (before Main's OnInitializeMelon)

    internal static void InstallEarly(HarmonyLib.Harmony harmony)
    {
        var targets = new List<MethodBase>();
        void Add(string type, string name)
        {
            Type? t = AccessTools.TypeByName(type);
            if (t == null) { MelonLogger.Warning($"{Prefix} {type} not found"); return; }
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (m.Name == name && m.GetMethodBody() != null) targets.Add(m);
        }
        Add("SkinForger.SkinForgerMod", "EnsureInitialized");
        Add("SkinForger.SkinForgerMod", "ReloadSkins");
        Add("SkinForger.LoadingPaths", "Skins");
        Add("SkinForger.CustomLoadingPicManager", "Initialize");
        Add("SkinForger.LoadingConfig", "Read");
        Add("SkinForger.FpsProtection.FpsProtectionConfig", "Load");
        int patched = 0;
        foreach (MethodBase method in targets)
        {
            try { harmony.Patch(method, transpiler: new HarmonyMethod(typeof(LazyCreatorFolder), nameof(Transpiler))); patched++; }
            catch (Exception exception) { MelonLogger.Warning($"{Prefix} {method.DeclaringType?.Name}.{method.Name} not patched: {exception.GetBaseException().Message}"); }
        }
        MelonLogger.Msg($"{Prefix} ready: {patched} start-up methods; SkinForger\\SkinCreator is created only when the player starts creating");
    }

    private static readonly MethodInfo CreateDir = AccessTools.Method(typeof(Directory), nameof(Directory.CreateDirectory), new[] { typeof(string) });
    private static readonly MethodInfo WriteText = AccessTools.Method(typeof(File), nameof(File.WriteAllText), new[] { typeof(string), typeof(string) });
    private static readonly MethodInfo MoveDir = AccessTools.Method(typeof(Directory), nameof(Directory.Move), new[] { typeof(string), typeof(string) });

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.operand is MethodInfo called && (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt))
            {
                if (called == CreateDir) instruction.operand = AccessTools.Method(typeof(LazyCreatorFolder), nameof(CreateDirectory));
                else if (called == WriteText) instruction.operand = AccessTools.Method(typeof(LazyCreatorFolder), nameof(WriteAllText));
                else if (called == MoveDir) instruction.operand = AccessTools.Method(typeof(LazyCreatorFolder), nameof(Move));
            }
            yield return instruction;
        }
    }
}
