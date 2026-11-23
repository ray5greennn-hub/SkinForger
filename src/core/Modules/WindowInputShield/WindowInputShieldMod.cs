using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using UnityEngine.EventSystems;

namespace SkinForger.WindowInputShield;

/// <summary>
/// Stops clicks/drags on the SkinForger IMGUI windows from reaching the game.
/// IMGUI only consumes its own events; the game's uGUI EventSystem still processes the same mouse input, so dragging
/// the SkinForger window clicked the character behind it. While the cursor is over an open SkinForger window, or a
/// mouse button that was pressed over one is still held (window drag), EventSystem.Update is skipped for that frame.
/// The creator window's state is read through SkinForger.MainBridge.MainHost, which answers for both the
/// legacy Main (private _showMenu/_windowRect) and the authored one (WindowShown/WindowRect); the Preview's
/// own state still comes from PreviewManager, whose runtime is the same in both. Nothing in Main is written.
/// </summary>
public sealed class WindowInputShieldMod : global::SkinForger.Core.SfModule
{
    private static WindowInputShieldMod? _current;
    private FieldInfo? _previewOpen, _previewRect;
    private bool _latched;
    private int _blockedFrames;
    private int _lastLoggedFrame = -1000;

    public override void OnInitializeMelon()
    {
        try
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            Type preview = AccessTools.TypeByName("SkinForger.Preview.PreviewManager") ?? throw new TypeLoadException("PreviewManager");
            if (!global::SkinForger.MainBridge.MainHost.WindowStateAvailable)
                throw new MissingMemberException("MainHost window state");
            _previewOpen = preview.GetField("_open", flags) ?? throw new MissingFieldException("PreviewManager._open");
            _previewRect = preview.GetField("_windowRect", flags) ?? throw new MissingFieldException("PreviewManager._windowRect");

            MethodInfo update = AccessTools.GetDeclaredMethods(typeof(EventSystem))
                .Single(m => m.Name == "Update" && m.GetParameters().Length == 0);
            HarmonyInstance.Patch(update, prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(WindowInputShieldMod), nameof(UpdatePrefix))));
            _current = this;
            // 2026-11-17 (user): the mouse wheel over a SkinForger window also scrolled the game (song list). The game
            // reads the wheel from UnityEngine.Input (directly or through Rewired), not from the EventSystem: over a
            // SkinForger window the wheel reads 0 for everyone but SkinForger's own scrolling (OwnWheelRead).
            int wheel = 0;
            PropertyInfo? delta = AccessTools.Property(typeof(Input), nameof(Input.mouseScrollDelta));
            if (delta?.GetGetMethod() is { } getter) { HarmonyInstance.Patch(getter, postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(WindowInputShieldMod), nameof(ScrollDeltaPostfix)))); wheel++; }
            foreach (string axis in new[] { nameof(Input.GetAxis), nameof(Input.GetAxisRaw) })
                if (AccessTools.DeclaredMethod(typeof(Input), axis, new[] { typeof(string) }) is { } method)
                { HarmonyInstance.Patch(method, postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(WindowInputShieldMod), nameof(AxisPostfix)))); wheel++; }
            MelonLogger.Msg($"[SkinForge][WindowInputShield] ready version=0.2.0 target=EventSystem.Update+wheel({wheel}) windows=menu,preview,library");
        }
        catch (Exception exception)
        {
            MelonLogger.Error("[SkinForge][WindowInputShield] DISABLED " + exception.GetBaseException().Message);
        }
    }

    public override void OnDeinitializeMelon()
    {
        _current = null;
        MelonLogger.Msg($"[SkinForge][WindowInputShield] shutdown blockedFrames={_blockedFrames}");
    }

    private static bool UpdatePrefix()
    {
        WindowInputShieldMod? current = _current;
        if (current == null) return true;
        try { return !current.ShouldBlock(); }
        catch { return true; }   // never break the game's input on an error
    }

    [ThreadStatic] internal static bool OwnWheelRead;
    private static int _overFrame = -1;
    private static bool _overCached;

    private static bool OverNow()
    {
        WindowInputShieldMod? current = _current;
        if (current == null || OwnWheelRead) return false;
        int frame = Time.frameCount;
        if (frame != _overFrame)
        {
            _overFrame = frame;
            try { _overCached = current.CursorOverWindow(); } catch { _overCached = false; }
        }
        return _overCached;
    }

    private static void ScrollDeltaPostfix(ref Vector2 __result)
    {
        if (__result != Vector2.zero && OverNow()) __result = Vector2.zero;
    }

    private static void AxisPostfix(string __0, ref float __result)
    {
        if (__result == 0f || string.IsNullOrEmpty(__0)) return;
        if ((__0.IndexOf("Scroll", StringComparison.OrdinalIgnoreCase) >= 0 || __0.IndexOf("Wheel", StringComparison.OrdinalIgnoreCase) >= 0) && OverNow())
            __result = 0f;
    }

    private bool ShouldBlock()
    {
        bool anyButton = Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2);
        bool over = CursorOverWindow();
        if (over && (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1) || Input.GetMouseButtonDown(2))) _latched = true;
        if (!anyButton) _latched = false;
        bool block = over || _latched;
        if (block)
        {
            _blockedFrames++;
            if (Time.frameCount - _lastLoggedFrame > 600)
            {
                _lastLoggedFrame = Time.frameCount;
                MelonLogger.Msg($"[SkinForge][WindowInputShield] BLOCKING game UI input over SkinForger window (over={over} drag={_latched})");
            }
        }
        return block;
    }

    private bool CursorOverWindow()
    {
        Vector3 mouse = Input.mousePosition;
        float x = mouse.x, y = Screen.height - mouse.y;            // IMGUI: origin top-left
        // Typing in a SkinForger text field: Enter / arrows must not drive the game's UI.
        if (global::SkinForger.SkinPackages.SkinPackagesMod.Typing) return true;
        // Skin Library (key 0) is its own window, open with or without the creator window.
        if (global::SkinForger.SkinPackages.SkinLibraryWindow.Covers(new Vector2(x, y))) return true;
        // Preview is drawn only inside the open creator window.
        if (!global::SkinForger.MainBridge.MainHost.TryWindowShown(out bool shown) || !shown) return false;
        if (global::SkinForger.MainBridge.MainHost.TryWindowRect(out Rect menu) && Contains(menu, x, y)) return true;
        return _previewOpen?.GetValue(null) is true && Contains(_previewRect, x, y);
    }

    private static bool Contains(FieldInfo? field, float x, float y) =>
        field?.GetValue(null) is Rect rect && Contains(rect, x, y);

    private static bool Contains(Rect rect, float x, float y) =>
        x >= rect.x && x <= rect.x + rect.width && y >= rect.y && y <= rect.y + rect.height;
}
