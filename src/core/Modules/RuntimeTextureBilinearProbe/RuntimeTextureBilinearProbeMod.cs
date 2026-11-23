using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace SkinForger.RuntimeTextureBilinearProbe;

/// <summary>
/// Minimal sampling-only validation. The host keeps all existing texture
/// settings, then this postfix changes only FilterMode.Point to Bilinear.
/// </summary>
public sealed class RuntimeTextureBilinearProbeMod : global::SkinForger.Core.SfModule
{
    private const string Prefix = "[SkinForge][RuntimeTextureBilinearProbe]";

    public override void OnInitializeMelon()
    {
        // The hook's name differs between the two Mains (ApplySkinForgerTextureSettings on the legacy one,
        // ApplyTextureSettings on the authored one), so MainBridge resolves it.
        MethodInfo? applySettings = global::SkinForger.MainBridge.MainHost.TextureSettingsHook;

        if (applySettings == null)
        {
            MelonLogger.Warning($"{Prefix} disabled: Main offers no runtime texture-settings hook.");
            return;
        }

        HarmonyInstance.Patch(
            applySettings,
            postfix: new HarmonyMethod(AccessTools.Method(
                typeof(RuntimeTextureBilinearProbeMod),
                nameof(ApplySettingsPostfix))));

        MelonLogger.Msg($"{Prefix} active on {applySettings.Name}: SkinForger texture FilterMode Point -> Bilinear; all other settings unchanged.");
    }

    private static void ApplySettingsPostfix(Texture2D __0)
    {
        if (__0 == null)
            return;

        __0.filterMode = FilterMode.Bilinear;
    }
}
