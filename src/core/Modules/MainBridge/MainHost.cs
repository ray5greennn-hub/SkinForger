using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace SkinForger.MainBridge;

/// <summary>Which Main is loaded. Core supports both while the port runs (SF切割SkinChanger计划 route B).</summary>
public enum MainGeneration
{
    /// <summary>No SkinForger.SkinForgerMod at all — Core does not start in this state.</summary>
    Missing,

    /// <summary>The Cecil-patched SkinForger.Melon.dll that carries upstream expression. Private fields.</summary>
    Legacy,

    /// <summary>The authored Main (SkinForgerMain-Authored). Public members, named by CONTRACT.md.</summary>
    Authored,
}

/// <summary>
/// The one place Core asks Main about itself.
///
/// Why this exists: the 2026-11-17 trial of the authored Main disabled 13 Core modules, because each of
/// them reflected into the OLD Main's private shape on its own — <c>_showMenu</c>, <c>_windowRect</c>,
/// <c>ApplySkinForgerTextureSettings</c> and so on. Both Mains are the author's own code, so the fix is
/// not to grow the old private shape back inside the new Main; it is for Core to ask through one bridge
/// that knows both generations.
///
/// Deliberately dual-path, not a switch: the old Main is still the deployed one, so every accessor here
/// answers for Legacy as well as Authored. Nothing in Core may read Main's private members directly any
/// more; a module that needs something not offered here adds it here first.
///
/// Resolution is lazy and cached on first success — Core's modules initialise in their own order and some
/// of them run before the window exists.
/// </summary>
public static class MainHost
{
    private const string Prefix = "[SkinForge][MainBridge]";
    private const string HostTypeName = "SkinForger.SkinForgerMod";

    private static Type? _host;
    private static MainGeneration _generation;
    private static bool _resolved;
    private static bool _announced;

    // Authored: public static properties. Legacy: private static fields.
    private static PropertyInfo? _windowShownProperty, _windowRectProperty, _windowScrollProperty;
    private static FieldInfo? _showMenuField, _windowRectField, _scrollField;

    private const BindingFlags Statics =
        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>The Main type, or null when it is not loaded yet.</summary>
    public static Type? Host
    {
        get { Resolve(); return _host; }
    }

    /// <summary>Which Main answered. <see cref="MainGeneration.Missing"/> until one is loaded.</summary>
    public static MainGeneration Generation
    {
        get { Resolve(); return _generation; }
    }

    /// <summary>Whether window state can be read at all (both generations offer it once Main is loaded).</summary>
    public static bool WindowStateAvailable
    {
        get
        {
            Resolve();
            return _windowShownProperty != null || _showMenuField != null;
        }
    }

    /// <summary>
    /// Whether SkinForger's own window is on screen. False with no Main loaded, which is the safe answer:
    /// callers use it to decide whether a click belongs to SkinForger rather than to the game.
    /// </summary>
    public static bool TryWindowShown(out bool shown)
    {
        Resolve();
        shown = false;
        try
        {
            if (_windowShownProperty != null) { shown = _windowShownProperty.GetValue(null) is true; return true; }
            if (_showMenuField != null) { shown = _showMenuField.GetValue(null) is true; return true; }
        }
        catch (Exception) { /* a Main mid-initialisation: answer "unavailable" rather than throw */ }
        return false;
    }

    /// <summary>
    /// Opens or closes SkinForger's own window (2026-11-18: the number keys drive it from Core, so the
    /// hotkeys are one set of rules instead of Main's key plus Core's). False when no Main offers it.
    /// </summary>
    public static bool TrySetWindowShown(bool shown)
    {
        Resolve();
        try
        {
            if (_windowShownProperty?.GetSetMethod(nonPublic: true) != null)
            { _windowShownProperty.SetValue(null, shown); return true; }
            if (_showMenuField != null) { _showMenuField.SetValue(null, shown); return true; }
        }
        catch (Exception) { }
        return false;
    }

    /// <summary>Where SkinForger's window is. False when no Main offers it.</summary>
    public static bool TryWindowRect(out Rect rect)
    {
        Resolve();
        rect = default;
        try
        {
            object? value = _windowRectProperty != null
                ? _windowRectProperty.GetValue(null)
                : _windowRectField?.GetValue(null);
            if (value is Rect found) { rect = found; return true; }
        }
        catch (Exception) { }
        return false;
    }

    /// <summary>
    /// The Image page's scroll position, which Core's wheel handling moves (IMGUI scroll views in this game
    /// never receive wheel events). False when no Main offers it.
    /// </summary>
    public static bool TryWindowScroll(out Vector2 scroll)
    {
        Resolve();
        scroll = default;
        try
        {
            object? value = _windowScrollProperty != null
                ? _windowScrollProperty.GetValue(null)
                : _scrollField?.GetValue(null);
            if (value is Vector2 found) { scroll = found; return true; }
        }
        catch (Exception) { }
        return false;
    }

    /// <summary>Moves the Image page's scroll position. False when no Main offers it.</summary>
    public static bool TrySetWindowScroll(Vector2 scroll)
    {
        Resolve();
        try
        {
            if (_windowScrollProperty?.GetSetMethod(nonPublic: true) != null)
            { _windowScrollProperty.SetValue(null, scroll); return true; }
            if (_scrollField != null) { _scrollField.SetValue(null, scroll); return true; }
        }
        catch (Exception) { }
        return false;
    }

    /// <summary>
    /// The method called once on each runtime texture SkinForger creates, for modules that adjust sampling
    /// (RuntimeTextureBilinearProbe patches it). Null when no Main offers the hook.
    /// </summary>
    public static MethodInfo? TextureSettingsHook
    {
        get
        {
            Resolve();
            if (_host == null) return null;
            return Single(_host, "ApplyTextureSettings")            // authored
                ?? Single(_host, "ApplySkinForgerTextureSettings"); // legacy
        }
    }

    /// <summary>A one-parameter static method taking a Texture2D, or null.</summary>
    private static MethodInfo? Single(Type type, string name) =>
        type.GetMethods(Statics).FirstOrDefault(m =>
            m.Name == name &&
            m.GetParameters().Length == 1 &&
            m.GetParameters()[0].ParameterType == typeof(Texture2D));

    private static void Resolve()
    {
        if (_resolved) return;

        _host = AccessTools.TypeByName(HostTypeName);
        if (_host == null) return;   // try again on the next call: Main may still be loading

        _windowShownProperty = _host.GetProperty("WindowShown", Statics);
        _windowRectProperty = _host.GetProperty("WindowRect", Statics);
        _windowScrollProperty = _host.GetProperty("WindowScroll", Statics);
        _showMenuField = _host.GetField("_showMenu", Statics);
        _scrollField = _host.GetField("_scroll", Statics);
        _windowRectField = _host.GetField("_windowRect", Statics);

        if (_windowShownProperty?.PropertyType != typeof(bool)) _windowShownProperty = null;
        if (_windowRectProperty?.PropertyType != typeof(Rect)) _windowRectProperty = null;
        if (_showMenuField?.FieldType != typeof(bool)) _showMenuField = null;
        if (_windowRectField?.FieldType != typeof(Rect)) _windowRectField = null;
        if (_windowScrollProperty?.PropertyType != typeof(Vector2)) _windowScrollProperty = null;
        if (_scrollField?.FieldType != typeof(Vector2)) _scrollField = null;

        _generation = _windowShownProperty != null ? MainGeneration.Authored
            : _showMenuField != null ? MainGeneration.Legacy
            : MainGeneration.Missing;

        if (_generation == MainGeneration.Missing) return;   // a Main that answers neither: keep retrying

        _resolved = true;
        if (_announced) return;
        _announced = true;
        MelonLogger.Msg($"{Prefix} Main is {_generation}, assembly={_host.Assembly.GetName().Name}, " +
                        $"window-state={(WindowStateAvailable ? "yes" : "no")}, " +
                        $"texture-hook={TextureSettingsHook?.Name ?? "none"}");
    }
}
