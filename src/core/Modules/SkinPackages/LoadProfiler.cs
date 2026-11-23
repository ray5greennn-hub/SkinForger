using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using MelonLoader;

namespace SkinForger.SkinPackages;

/// <summary>
/// Measures where skin loading spends its time (2026-11-15, for the next speed-up). Log only, no behaviour change.
///  - Main's ReloadAssets: one line per reload with the time of its three passes and the slowest objects;
///  - any single SkeletonRendererAwake / SkeletonGraphicAwake / CharacterApplyOnEnable over 100 ms (scene loads,
///    first use of a skin) is logged on its own.
/// Two Stopwatch reads per call; nothing runs per frame.
/// </summary>
internal static class LoadProfiler
{
    private const string Prefix = "[SkinForge][LoadProfile]";
    private const double SlowMs = 100.0;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static int _reloadDepth;
    private static double _reloadStart;
    private static readonly Dictionary<string, (int Calls, double Ms)> Passes = new();
    private static readonly List<(string What, double Ms)> Slowest = new();

    [ThreadStatic] private static Stack<double>? _starts;

    internal static int Install(HarmonyLib.Harmony harmony)
    {
        Type host = AccessTools.TypeByName("SkinForger.SkinForgerMod") ?? throw new TypeLoadException("SkinForger.SkinForgerMod");
        int patched = 0;
        MethodInfo? reload = AccessTools.DeclaredMethod(host, "ReloadAssets", Type.EmptyTypes);
        if (reload != null)
        {
            harmony.Patch(reload,
                prefix: new HarmonyMethod(typeof(LoadProfiler), nameof(ReloadPrefix)) { priority = HarmonyLib.Priority.First },
                finalizer: new HarmonyMethod(typeof(LoadProfiler), nameof(ReloadFinalizer)) { priority = HarmonyLib.Priority.Last });
            patched++;
        }
        foreach (string name in new[] { "SkeletonRendererAwake", "SkeletonGraphicAwake", "CharacterApplyOnEnable" })
        {
            MethodInfo? method = AccessTools.DeclaredMethod(host, name);
            if (method == null) continue;
            harmony.Patch(method,
                prefix: new HarmonyMethod(typeof(LoadProfiler), nameof(StepPrefix)) { priority = HarmonyLib.Priority.First },
                finalizer: new HarmonyMethod(typeof(LoadProfiler), nameof(StepFinalizer)) { priority = HarmonyLib.Priority.Last });
            patched++;
        }
        return patched;
    }

    private static double Now => Clock.Elapsed.TotalMilliseconds;

    private static void ReloadPrefix()
    {
        if (_reloadDepth++ > 0) return;
        _reloadStart = Now;
        Passes.Clear();
        Slowest.Clear();
    }

    private static Exception? ReloadFinalizer(Exception? __exception)
    {
        if (--_reloadDepth > 0) return __exception;
        _reloadDepth = 0;
        try
        {
            double total = Now - _reloadStart;
            string passes = string.Join(" ", Passes.Select(p => $"{p.Key}={p.Value.Ms:0}ms/{p.Value.Calls}"));
            string slow = string.Join(", ", Slowest.OrderByDescending(s => s.Ms).Take(4).Select(s => $"{s.What}:{s.Ms:0}ms"));
            MelonLogger.Msg($"{Prefix} reload totalMs={total:0} {passes} slowest=[{slow}]");
        }
        catch { }
        return __exception;
    }

    private static void StepPrefix()
    {
        (_starts ??= new Stack<double>()).Push(Now);
    }

    private static Exception? StepFinalizer(MethodBase __originalMethod, object[] __args, Exception? __exception)
    {
        try
        {
            if (_starts == null || _starts.Count == 0) return __exception;
            double ms = Now - _starts.Pop();
            string pass = __originalMethod.Name.Replace("Awake", string.Empty).Replace("OnEnable", string.Empty);
            string what = pass + ":" + Describe(__args.Length > 0 ? __args[0] : null);
            if (_reloadDepth > 0)
            {
                Passes[pass] = Passes.TryGetValue(pass, out var sum) ? (sum.Calls + 1, sum.Ms + ms) : (1, ms);
                if (ms >= 5.0) Slowest.Add((what, ms));
            }
            else if (ms >= SlowMs)
            {
                MelonLogger.Msg($"{Prefix} slow load {what} {ms:0}ms");
            }
        }
        catch { }
        return __exception;
    }

    private static string Describe(object? component)
    {
        try
        {
            if (component is UnityEngine.Component c && c.Pointer != IntPtr.Zero && c.gameObject != null) return c.gameObject.name;
        }
        catch { }
        return "?";
    }
}
