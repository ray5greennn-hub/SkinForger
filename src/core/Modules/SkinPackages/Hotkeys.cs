using System.Reflection;
using HarmonyLib;
using MelonLoader;
using SkinForger.MainBridge;
using UnityEngine;

namespace SkinForger.SkinPackages;

/// <summary>
/// One set of keys for every SkinForger window (2026-11-18, user):
///
///   1  Image      2  Text      3  Sound      4  Settings      0  Skin Library
///
/// Preview has no key: F1 is the game's own (2026-11-18), and opening Preview by setting its flag
/// skipped the build Main does when its button is pressed, which left the window saying "Unavailable".
/// Preview is opened by its button, and the creator window now has a Close button so it can be put away
/// without closing Preview with it.
///
/// A number key opens the creator window on that page. Pressing the key of the page already on screen
/// closes the window again, so 1 still works the way Main's single menu key always did.
///
/// Before this, the keys were scattered: Main toggled the window on the key in Menukey.txt, Core opened
/// the Skin Library on 2 — which was also Main's window key territory — and the pages had no keys at all.
/// Main's own key handler is switched off here (its key field is set to None) so that there is exactly one
/// place deciding what a key does; Menukey.txt is still read, and whatever key it names becomes the Image
/// key in place of 1.
///
/// Keys are ignored while an IMGUI text field has focus, or typing a package name would open pages.
/// </summary>
internal static class Hotkeys
{
    private const string Prefix = "[SkinForge][Keys]";

    private static bool _installed;
    private static FieldInfo? _menuKey;
    private static PropertyInfo? _menuKeyProperty;

    /// <summary>The key that opens the Image page: Menukey.txt's key, or 1.</summary>
    private static KeyCode _imageKey = KeyCode.Alpha1;

    internal static int Install()
    {
        if (_installed) return 0;
        _installed = true;
        int wired = 0;
        try
        {
            Resolve();
            if (MainsKeyFound)
            {
                // Whatever Menukey.txt says becomes the Image key, then Main's own handler is silenced:
                // two handlers on one key cancel each other out (open then close in the same frame).
                if (MainsKey is var configured && configured != KeyCode.None) _imageKey = configured;
                MainsKey = KeyCode.None;
                wired++;
            }

            MelonLogger.Msg($"{Prefix} {_imageKey}=Image 2=Text 3=Sound 4=Settings 0=Skin Library (Preview has no key)");
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"{Prefix} not fully wired: {exception.GetBaseException().Message}");
        }
        return wired;
    }

    internal static void Update()
    {
        // Main re-reads Menukey.txt when the file changes, which would hand its key handler back. Keeping
        // the field at None costs one field read a frame and keeps every key in one place.
        //
        // Resolved here and not only at install: Core initialises before Main has finished, so the field can
        // still be missing then. Left unresolved, Main's own handler would stay live and key 1 would open
        // and close the window in the same frame.
        if (!MainsKeyFound) Resolve();
        if (MainsKeyFound && MainsKey is var live && live != KeyCode.None)
        {
            _imageKey = live;
            MainsKey = KeyCode.None;
        }

        // Typing "2" into the package-name field must stay a "2".
        if (GUIUtility.keyboardControl != 0) return;

        if (Down(_imageKey) || Down(KeyCode.Alpha1, KeyCode.Keypad1)) { Page(0); return; }
        if (Down(KeyCode.Alpha2, KeyCode.Keypad2)) { Page(1); return; }
        if (Down(KeyCode.Alpha3, KeyCode.Keypad3)) { Page(2); return; }
        if (Down(KeyCode.Alpha4, KeyCode.Keypad4)) { Page(4); return; }   // Settings sits apart from the tab row
        if (Down(KeyCode.Alpha0, KeyCode.Keypad0)) SkinLibraryWindow.Toggle("key 0");
    }

    /// <summary>Finds Main's menu-key field. Safe to call repeatedly: it stops once it has it.</summary>
    private static void Resolve()
    {
        try
        {
            Type? host = MainHost.Host;
            if (host == null) return;
            // The authored Main offers it as a property; the old one keeps it private.
            PropertyInfo? property = host.GetProperty("MenuKey", BindingFlags.Static | BindingFlags.Public);
            if (property != null && property.PropertyType == typeof(KeyCode) && property.GetSetMethod() != null)
            {
                _menuKeyProperty = property;
                return;
            }
            FieldInfo? field = host.GetField("_menuKey", BindingFlags.Static | BindingFlags.NonPublic);
            if (field != null && field.FieldType == typeof(KeyCode)) _menuKey = field;
        }
        catch (Exception) { /* Main mid-initialisation: try again next frame */ }
    }

    /// <summary>The key Main would open on, whichever generation is loaded; None once Core has taken over.</summary>
    private static KeyCode MainsKey
    {
        get
        {
            object? value = _menuKeyProperty != null ? _menuKeyProperty.GetValue(null) : _menuKey?.GetValue(null);
            return value is KeyCode key ? key : KeyCode.None;
        }
        set
        {
            if (_menuKeyProperty != null) _menuKeyProperty.SetValue(null, value);
            else _menuKey?.SetValue(null, value);
        }
    }

    private static bool MainsKeyFound => _menuKeyProperty != null || _menuKey != null;

    private static bool Down(params KeyCode[] keys)
    {
        foreach (KeyCode key in keys)
            if (key != KeyCode.None && Input.GetKeyDown(key)) return true;
        return false;
    }

    /// <summary>
    /// Opens the window on a page; the page already shown closes the window. Nothing happens when Main
    /// does not let its window state be set — better than a key that half works.
    /// </summary>
    private static void Page(int tab)
    {
        if (!MainHost.TryWindowShown(out bool shown)) return;
        if (shown && SkinPackagesMod.CurrentTab == tab)
        {
            MainHost.TrySetWindowShown(false);
            return;
        }
        SkinPackagesMod.RequestTab(tab);
        if (!shown) MainHost.TrySetWindowShown(true);
    }

}
