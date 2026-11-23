using System;
using MelonLoader;
using UnityEngine;

namespace SkinForger.UI;

/// <summary>
/// Initializes Unity's IMGUI window style once while Muse Dash is already in
/// its existing Loading scene. No window or control is drawn and no SkinForger
/// UI, skin list, file-system, or replacement work is executed.
/// </summary>
public sealed class UIFirstOpenLoadingPrewarmMod : global::SkinForger.Core.SfModule
{
    private const string Prefix = "[SkinForge][UILoadingPrewarm]";

    private static bool _inLoadingScene;
    private static bool _complete;
    private static GUIStyle? _windowStyle;

    public override void OnInitializeMelon()
    {
        MelonLogger.Msg($"{Prefix} loaded; waiting for the existing Loading scene.");
    }

    public override void OnSceneWasInitialized(int buildIndex, string sceneName)
    {
        _inLoadingScene = string.Equals(sceneName, "Loading", StringComparison.OrdinalIgnoreCase);
    }

    public override void OnGUI()
    {
        if (_complete || !_inLoadingScene)
            return;

        Event? current = Event.current;
        if (current == null || current.type != EventType.Repaint)
            return;

        try
        {
            // GUI.skin is only valid during OnGUI. Access exactly the one style
            // whose atomic Unity initialization caused the first-open hitch.
            _windowStyle = GUI.skin.window;
            _complete = _windowStyle != null;

            if (_complete)
                MelonLogger.Msg($"{Prefix} GUI.skin.window initialized during the existing Loading scene.");
        }
        catch (Exception ex)
        {
            // Leave the operation incomplete so a later Loading scene can retry.
            _inLoadingScene = false;
            MelonLogger.Warning($"{Prefix} skipped safely: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
