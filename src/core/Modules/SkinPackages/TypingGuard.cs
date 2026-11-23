using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using MelonLoader;
using MelonLoader.NativeUtils;
using UnityEngine;

namespace SkinForger.SkinPackages;

/// <summary>
/// While a SkinForger text field has keyboard focus, the game must not see the keyboard (2026-11-15: Enter cancelled
/// the elfin choice, Space pressed buttons). 1.9.1 paused the Rewired players and patched UnityEngine.Input's managed
/// wrappers, and the log proved the guard was active while keys still reached the game: the game's compiled code
/// does not go through those wrappers. All layers now, undone when typing stops:
///  1. native: Unity's input functions themselves (Input::GetKeyInt / GetKeyDownInt / GetKeyUpInt / GetKey*String /
///     GetButton* / get_anyKey*), detoured with MelonLoader NativeHook: every caller is covered;
///  2. Rewired: every bool button / key query of Player, Controller (keyboard, mouse, joysticks) and Keyboard returns
///     false (Harmony), players paused, keyboard controller disabled;
///  3. managed UnityEngine.Input wrappers (Harmony);
///  4. uGUI: WindowInputShield skips EventSystem.Update.
///  5. Unity's NEW Input System (2026-11-15 log: layers 1-3 blocked 100 000+ queries and Enter / Space still reached the
///     game): the game also reads the keyboard through Unity.InputSystem (InputActionAsset in PeroTools2,
///     InputSystemUIInputModule for uGUI). Its keyboard device is disabled while typing and enabled again afterwards.
/// IMGUI text fields get their keys from IMGUI events, so typing keeps working. Each block is counted and the counts
/// are logged when typing ends (shows which path the game uses).
/// </summary>
internal static class TypingGuard
{
    private const string Prefix = "[SkinForge][TypingGuard]";
    internal static bool Active { get; private set; }

    private static readonly List<(Il2CppRewired.Player Player, bool WasPlaying)> Paused = new();
    private static bool? _keyboardWasEnabled;
    private static UnityEngine.InputSystem.Keyboard? _disabledKeyboard;
    private static int _sessions;
    private static readonly Dictionary<string, int> Blocked = new(StringComparer.Ordinal);

    internal static int Install(HarmonyLib.Harmony harmony)
    {
        int managed = 0, rewired = 0, native = 0;
        var block = new HarmonyMethod(typeof(TypingGuard), nameof(BlockPrefix)) { priority = HarmonyLib.Priority.First };
        const BindingFlags any = BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        foreach (MethodInfo method in typeof(Input).GetMethods(any))
            if (method.ReturnType == typeof(bool) && method.Name is "GetKey" or "GetKeyDown" or "GetKeyUp" or "GetButton" or "GetButtonDown" or "GetButtonUp" or "get_anyKey" or "get_anyKeyDown")
                if (TryPatch(harmony, method, block)) managed++;

        foreach (Type type in new[] { typeof(Il2CppRewired.Player), typeof(Il2CppRewired.Controller), typeof(Il2CppRewired.Keyboard), typeof(Il2CppRewired.ReInput.ControllerHelper) })
            foreach (MethodInfo method in type.GetMethods(any))
                if (method.ReturnType == typeof(bool) && !method.IsStatic &&
                    (method.Name.StartsWith("GetButton", StringComparison.Ordinal) || method.Name.StartsWith("GetNegativeButton", StringComparison.Ordinal) ||
                     method.Name.StartsWith("GetAny", StringComparison.Ordinal) || method.Name.StartsWith("GetKey", StringComparison.Ordinal)))
                    if (TryPatch(harmony, method, block)) rewired++;

        native = InstallNative();
        MelonLogger.Msg($"{Prefix} ready: native Unity input={native}/11, Rewired queries={rewired}, Unity Input wrappers={managed}");
        return managed + rewired + native;
    }

    private static bool TryPatch(HarmonyLib.Harmony harmony, MethodInfo method, HarmonyMethod prefix)
    {
        try { harmony.Patch(method, prefix: prefix); return true; }
        catch (Exception exception)
        {
            MelonLogger.Warning($"{Prefix} {method.DeclaringType?.Name}.{method.Name} not patched: {exception.GetBaseException().Message}");
            return false;
        }
    }

    private static bool BlockPrefix(MethodBase __originalMethod, ref bool __result)
    {
        if (!Active) return true;
        Count((__originalMethod.DeclaringType?.Name ?? "?") + "." + __originalMethod.Name);
        __result = false;
        return false;
    }

    private static void Count(string what) => Blocked[what] = Blocked.TryGetValue(what, out int n) ? n + 1 : 1;

    // ------------------------------------------------------------ native Unity input

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte KeyFn(int key);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte NameFn(IntPtr name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate byte NoArgFn();

    // Keeps hooks and detour delegates alive (the native side holds raw pointers to them).
    private static readonly List<object> Alive = new();

    private static int InstallNative()
    {
        int hooked = 0;
        foreach (string name in new[] { "GetKeyInt", "GetKeyDownInt", "GetKeyUpInt" })
            hooked += HookKey(name, "(UnityEngine.KeyCode)");
        foreach (string name in new[] { "GetKeyString", "GetKeyDownString", "GetKeyUpString", "GetButton", "GetButtonDown", "GetButtonUp" })
            hooked += HookName(name, "(System.String)");
        foreach (string name in new[] { "get_anyKey", "get_anyKeyDown" })
            hooked += HookNoArg(name);
        return hooked;
    }

    private static IntPtr Resolve(string name, string signature)
    {
        foreach (string candidate in new[] { $"UnityEngine.Input::{name}{signature}", $"UnityEngine.Input::{name}" })
        {
            IntPtr pointer = Il2CppInterop.Runtime.IL2CPP.il2cpp_resolve_icall(candidate);
            if (pointer != IntPtr.Zero) return pointer;
        }
        MelonLogger.Warning($"{Prefix} native Input::{name} not found");
        return IntPtr.Zero;
    }

    private static int HookKey(string name, string signature)
    {
        IntPtr target = Resolve(name, signature);
        if (target == IntPtr.Zero) return 0;
        NativeHook<KeyFn>? hook = null;
        KeyFn detour = key => Active ? Block("Input::" + name) : hook!.Trampoline(key);
        hook = new NativeHook<KeyFn>(target, Marshal.GetFunctionPointerForDelegate(detour));
        return Attach(hook, detour, name);
    }

    private static int HookName(string name, string signature)
    {
        IntPtr target = Resolve(name, signature);
        if (target == IntPtr.Zero) return 0;
        NativeHook<NameFn>? hook = null;
        NameFn detour = text => Active ? Block("Input::" + name) : hook!.Trampoline(text);
        hook = new NativeHook<NameFn>(target, Marshal.GetFunctionPointerForDelegate(detour));
        return Attach(hook, detour, name);
    }

    private static int HookNoArg(string name)
    {
        IntPtr target = Resolve(name, "()");
        if (target == IntPtr.Zero) return 0;
        NativeHook<NoArgFn>? hook = null;
        NoArgFn detour = () => Active ? Block("Input::" + name) : hook!.Trampoline();
        hook = new NativeHook<NoArgFn>(target, Marshal.GetFunctionPointerForDelegate(detour));
        return Attach(hook, detour, name);
    }

    private static int Attach<T>(NativeHook<T> hook, T detour, string name) where T : Delegate
    {
        try
        {
            hook.Attach();
            Alive.Add(hook);
            Alive.Add(detour);
            return 1;
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"{Prefix} native Input::{name} not hooked: {exception.GetBaseException().Message}");
            return 0;
        }
    }

    private static byte Block(string what)
    {
        Count(what);
        return 0;
    }

    // ------------------------------------------------------------ on / off

    /// <summary>Called every frame with whether a SkinForger window has a focused text field.</summary>
    internal static void Set(bool typing)
    {
        if (typing == Active) return;
        Active = typing;
        try
        {
            if (typing) Pause(); else Resume();
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"{Prefix} {(typing ? "pause" : "resume")} failed: {exception.GetBaseException().Message}");
        }
    }

    private static void Pause()
    {
        Paused.Clear();
        Blocked.Clear();
        int players = 0;
        if (Il2CppRewired.ReInput.isReady)
        {
            var helper = Il2CppRewired.ReInput.players;
            var all = new List<Il2CppRewired.Player>();
            for (int i = 0; i < helper.playerCount; i++) if (helper.GetPlayer(i) is { } p) all.Add(p);
            if (helper.GetSystemPlayer() is { } system) all.Add(system);
            foreach (Il2CppRewired.Player player in all)
            {
                Paused.Add((player, player.isPlaying));
                player.isPlaying = false;
                players++;
            }
            var keyboard = Il2CppRewired.ReInput.controllers?.Keyboard;
            if (keyboard != null) { _keyboardWasEnabled = keyboard.enabled; keyboard.enabled = false; }
        }
        string inputSystem = "no keyboard";
        try
        {
            UnityEngine.InputSystem.Keyboard? keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard != null && keyboard.Pointer != IntPtr.Zero)
            {
                if (keyboard.enabled) { UnityEngine.InputSystem.InputSystem.DisableDevice(keyboard, false); _disabledKeyboard = keyboard; inputSystem = "keyboard disabled"; }
                else inputSystem = "keyboard already off";
            }
        }
        catch (Exception exception) { inputSystem = "failed: " + exception.GetBaseException().Message; }
        if (_sessions < 8) MelonLogger.Msg($"{Prefix} typing: game keyboard blocked (Rewired players paused={players}, Input System {inputSystem})");
    }

    private static void Resume()
    {
        foreach ((Il2CppRewired.Player player, bool wasPlaying) in Paused)
            try { if (player.Pointer != IntPtr.Zero) player.isPlaying = wasPlaying; } catch { }
        Paused.Clear();
        if (_keyboardWasEnabled is { } was && Il2CppRewired.ReInput.isReady && Il2CppRewired.ReInput.controllers?.Keyboard is { } keyboard)
            keyboard.enabled = was;
        _keyboardWasEnabled = null;
        try
        {
            if (_disabledKeyboard != null && _disabledKeyboard.Pointer != IntPtr.Zero) UnityEngine.InputSystem.InputSystem.EnableDevice(_disabledKeyboard);
        }
        catch (Exception exception) { MelonLogger.Warning($"{Prefix} Input System keyboard not enabled again: {exception.GetBaseException().Message}"); }
        _disabledKeyboard = null;
        if (_sessions++ < 8)
            MelonLogger.Msg($"{Prefix} typing ended, keyboard restored. Blocked while typing: " +
                (Blocked.Count == 0 ? "nothing asked" : string.Join(", ", Blocked.OrderByDescending(b => b.Value).Take(12).Select(b => $"{b.Key}={b.Value}"))));
    }
}
