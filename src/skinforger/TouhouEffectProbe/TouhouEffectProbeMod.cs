using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using Il2CppAssets.Scripts.UI.Specials;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppSpine;
using Il2CppSpine.Unity;
using MelonLoader;
using UnityEngine;

namespace SkinForger.Diagnostics;

public sealed class TouhouEffectProbeMod : MelonMod
{
    private static readonly HashSet<string> DumpedReasons = new(StringComparer.Ordinal);
    private static HarmonyLib.Harmony? _harmony;

    public override void OnInitializeMelon()
    {
        _harmony = new HarmonyLib.Harmony("skinforger.touhou-effect-probe");
        TryPatch(typeof(FeverEffectManager), "InitFeverBadApple", nameof(AfterInitFeverBadApple));
        TryPatch(typeof(FeverEffectManager), "InvokeBadAppleFever", nameof(AfterInvokeBadAppleFever));
        TryPatch(typeof(FeverEffectManager), "CancelBadAppleFeverEffect", nameof(AfterCancelBadAppleFeverEffect));
        TryPatch(typeof(ReverseColor), "SetInverse", nameof(AfterReverseColorSetInverse));
        TryPatch(typeof(ReverseColor), "OnRenderImage", nameof(AfterReverseColorOnRenderImage));
        TryPatch(typeof(TheEastNeverSleepsScreenEffect), "Play", nameof(AfterEastNeverSleepsPlay));
        TryPatch(typeof(TheEastNeverSleepsScreenEffect), "OnRenderImage", nameof(AfterEastNeverSleepsOnRenderImage));
        TryPatch(typeof(BadAppleImageChange), "Start", nameof(AfterBadAppleImageChangeStart));
        MelonLogger.Msg("[SkinForge][TouhouEffect] probe loaded; low-frequency effect hooks installed.");
    }

    private static void TryPatch(Type type, string targetMethod, string postfixMethod)
    {
        try
        {
            var target = AccessTools.Method(type, targetMethod);
            var postfix = AccessTools.Method(typeof(TouhouEffectProbeMod), postfixMethod);
            if (target == null || postfix == null)
            {
                MelonLogger.Warning($"[SkinForge][TouhouEffect] patch skipped: {type.FullName}.{targetMethod}");
                return;
            }
            _harmony!.Patch(target, postfix: new HarmonyMethod(postfix));
            MelonLogger.Msg($"[SkinForge][TouhouEffect] patched {type.FullName}.{targetMethod}");
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[SkinForge][TouhouEffect] patch failed: {type.FullName}.{targetMethod}: {ex.Message}");
        }
    }

    private static void AfterInitFeverBadApple(FeverEffectManager __instance) =>
        DumpEffectState("FeverEffectManager.InitFeverBadApple", __instance);

    private static void AfterInvokeBadAppleFever(FeverEffectManager __instance) =>
        DumpEffectState("FeverEffectManager.InvokeBadAppleFever", __instance);

    private static void AfterCancelBadAppleFeverEffect(FeverEffectManager __instance) =>
        DumpEffectState("FeverEffectManager.CancelBadAppleFeverEffect", __instance);

    private static void AfterReverseColorSetInverse(ReverseColor __instance, bool inverse) =>
        DumpEffectState($"ReverseColor.SetInverse({inverse})", __instance);

    private static void AfterReverseColorOnRenderImage(ReverseColor __instance, object[] __args) =>
        DumpEffectState($"ReverseColor.OnRenderImage args={DescribeArgs(__args)}", __instance);

    private static void AfterEastNeverSleepsPlay(TheEastNeverSleepsScreenEffect __instance) =>
        DumpEffectState("TheEastNeverSleepsScreenEffect.Play", __instance);

    private static void AfterEastNeverSleepsOnRenderImage(TheEastNeverSleepsScreenEffect __instance, object[] __args) =>
        DumpEffectState($"TheEastNeverSleepsScreenEffect.OnRenderImage args={DescribeArgs(__args)}", __instance);

    private static void AfterBadAppleImageChangeStart(BadAppleImageChange __instance) =>
        DumpEffectState("BadAppleImageChange.Start", __instance);

    private static void DumpEffectState(string reason, Il2CppObjectBase? effect)
    {
        try
        {
            if (!DumpedReasons.Add(reason))
            {
                return;
            }

            MelonLogger.Msg($"[SkinForge][TouhouEffect] BEGIN reason={reason} effect={DescribeObject(effect)}");
            DumpKnownEffect(effect);
            DumpSkeletonRenderers(reason);
            DumpSkeletonGraphics(reason);
            MelonLogger.Msg($"[SkinForge][TouhouEffect] END reason={reason}");
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[SkinForge][TouhouEffect] dump failed reason={reason}: {ex.Message}");
        }
    }

    private static void DumpKnownEffect(Il2CppObjectBase? effect)
    {
        if (effect == null)
        {
            return;
        }

        var fever = effect.TryCast<FeverEffectManager>();
        if (fever != null)
        {
            MelonLogger.Msg($"[SkinForge][TouhouEffect] Fever fields reverseColor={DescribeObject(fever.m_ReverseColor)} background={DescribeObject(fever.m_Background)} whiteBoard={DescribeRenderer(fever.m_WhitBoardRender)} feverIndex={Safe(() => fever.m_FeverIndex.ToString())}");
        }

        var reverse = effect.TryCast<ReverseColor>();
        if (reverse != null)
        {
            MelonLogger.Msg($"[SkinForge][TouhouEffect] ReverseColor shader={DescribeObject(reverse.screenEffectShader)} material={DescribeMaterial(reverse.material)} reverseValue={FormatFloat(reverse.reverseValue)} pass={Safe(() => reverse.pass.ToString())}");
        }

        var east = effect.TryCast<TheEastNeverSleepsScreenEffect>();
        if (east != null)
        {
            MelonLogger.Msg($"[SkinForge][TouhouEffect] EastNeverSleeps shader={DescribeObject(east.screenEffectShader)} material={DescribeMaterial(east.material)} blackDegree={FormatFloat(east.blackDegree)} whiteAlpha={FormatFloat(east.whiteAlpha)} isBlackSide={Safe(() => east.m_IsBlackSide.ToString())}");
        }
    }

    private static void DumpSkeletonRenderers(string reason)
    {
        var renderers = Resources.FindObjectsOfTypeAll<SkeletonRenderer>();
        int count = renderers == null ? 0 : renderers.Length;
        MelonLogger.Msg($"[SkinForge][TouhouEffect] SkeletonRenderer count={count} reason={reason}");
        if (renderers == null)
        {
            return;
        }

        int emitted = 0;
        for (int i = 0; i < count; i++)
        {
            var renderer = renderers[i];
            if (renderer == null)
            {
                continue;
            }
            var go = ((Component)renderer).gameObject;
            var meshRenderer = renderer.meshRenderer;
            var material = meshRenderer == null ? null : ((Renderer)meshRenderer).sharedMaterial;
            var path = PathOf(go);
            if (!ShouldDumpRenderer(path, material, go.activeInHierarchy) || emitted >= 80)
            {
                continue;
            }
            emitted++;
            MelonLogger.Msg($"[SkinForge][TouhouEffect] SR index={i} object={path} active={Safe(() => go.activeInHierarchy.ToString())} material={DescribeMaterial(material)}");
            if (IsFocusPath(path) || IsFocusMaterial(material))
            {
                DumpSkeleton(renderer.Skeleton, $"SR:{path}");
            }
        }
    }

    private static void DumpSkeletonGraphics(string reason)
    {
        var graphics = Resources.FindObjectsOfTypeAll<SkeletonGraphic>();
        int count = graphics == null ? 0 : graphics.Length;
        MelonLogger.Msg($"[SkinForge][TouhouEffect] SkeletonGraphic count={count} reason={reason}");
        if (graphics == null)
        {
            return;
        }

        int emitted = 0;
        for (int i = 0; i < count; i++)
        {
            var graphic = graphics[i];
            if (graphic == null)
            {
                continue;
            }
            var go = ((Component)graphic).gameObject;
            var path = PathOf(go);
            if (!ShouldDumpRenderer(path, graphic.material, go.activeInHierarchy) || emitted >= 40)
            {
                continue;
            }
            emitted++;
            MelonLogger.Msg($"[SkinForge][TouhouEffect] SG index={i} object={path} active={Safe(() => go.activeInHierarchy.ToString())} material={DescribeMaterial(graphic.material)} mainTexture={DescribeTexture(graphic.mainTexture)} overrideTexture={DescribeTexture(graphic.OverrideTexture)}");
            if (IsFocusPath(path) || IsFocusMaterial(graphic.material))
            {
                DumpSkeleton(graphic.Skeleton, $"SG:{path}");
            }
        }
    }

    private static void DumpSkeleton(Skeleton? skeleton, string owner)
    {
        if (skeleton == null)
        {
            return;
        }
        var slots = skeleton.Slots;
        if (slots == null)
        {
            return;
        }

        int limit = Math.Min(slots.Count, 40);
        for (int i = 0; i < limit; i++)
        {
            var slot = slots.Items[i];
            var attachment = slot?.Attachment;
            if (attachment == null)
            {
                continue;
            }

            var slotName = Safe(() => slot!.data?.name ?? "<slot-null>");
            var attachmentName = Safe(() => attachment.Name ?? "<attachment-null>");
            var region = GetRegion(attachment);
            var page = region?.page;
            var pageMaterial = page?.rendererObject == null ? null : page.rendererObject.TryCast<Material>();
            MelonLogger.Msg("[SkinForge][TouhouEffect] Attachment " +
                            $"owner={owner} slot={slotName} index={i} attachment={attachmentName} type={Il2CppTypeName(attachment)} " +
                            $"region={DescribeRegion(region)} pageTex={DescribeTexture(pageMaterial?.mainTexture)} pageMaterial={DescribeMaterial(pageMaterial)}");
        }
    }

    private static AtlasRegion? GetRegion(Attachment attachment)
    {
        var hasRendererObject = attachment.TryCast<IHasRendererObject>();
        if (hasRendererObject?.RendererObject == null)
        {
            return null;
        }
        return hasRendererObject.RendererObject.TryCast<AtlasRegion>();
    }

    private static string DescribeRegion(AtlasRegion? region)
    {
        if (region == null)
        {
            return "<null>";
        }
        return $"{Safe(() => region.name ?? "<unnamed>")}#ptr={Safe(() => region.Pointer.ToInt64().ToString("X"))} " +
               $"rect={region.x},{region.y},{region.width}x{region.height} original={region.originalWidth}x{region.originalHeight} offset={region.offsetX:0.###},{region.offsetY:0.###} rotate={region.rotate} degrees={region.degrees} uv={region.u:0.######},{region.v:0.######},{region.u2:0.######},{region.v2:0.######}";
    }

    private static string DescribeMaterial(Material? material)
    {
        if (material == null)
        {
            return "<null>";
        }
        return $"{material.name}#{material.GetInstanceID()} shader={DescribeObject(material.shader)} mainTexture={DescribeTexture(material.mainTexture)} color={MaterialColor(material)} " +
               $"alpha={Prop(material, "_Alpha")} cutoff={Prop(material, "_Cutoff")} srcBlend={Prop(material, "_SrcBlend")} dstBlend={Prop(material, "_DstBlend")} " +
               $"blendOp={Prop(material, "_BlendOp")} stencil={Prop(material, "_Stencil")} stencilComp={Prop(material, "_StencilComp")} stencilOp={Prop(material, "_StencilOp")} " +
               $"darkColorAlphaAdditive={Prop(material, "DarkColorAlphaAdditive")} thresholdEnd={Prop(material, "ThresholdEnd")}";
    }

    private static string DescribeTexture(Texture? texture)
    {
        if (texture == null)
        {
            return "<null>";
        }
        string format = "N/A";
        var tex2D = texture.TryCast<Texture2D>();
        if (tex2D != null)
        {
            format = Safe(() => tex2D.format.ToString());
        }
        return $"{texture.name}#{texture.GetInstanceID()} size={texture.width}x{texture.height} type={Il2CppTypeName(texture)} format={format} filter={texture.filterMode} wrap={texture.wrapMode}";
    }

    private static string DescribeRenderer(SpriteRenderer? renderer)
    {
        if (renderer == null)
        {
            return "<null>";
        }
        return $"{PathOf(((Component)renderer).gameObject)} material={DescribeMaterial(renderer.sharedMaterial)} sprite={DescribeObject(renderer.sprite)} color={Safe(() => renderer.color.ToString())}";
    }

    private static bool ShouldDumpRenderer(string path, Material? material, bool active)
    {
        return active || IsFocusPath(path) || IsFocusMaterial(material);
    }

    private static bool IsFocusPath(string path)
    {
        return path.IndexOf("elfin", StringComparison.OrdinalIgnoreCase) >= 0 ||
               path.IndexOf("thief", StringComparison.OrdinalIgnoreCase) >= 0 ||
               path.IndexOf("touhou", StringComparison.OrdinalIgnoreCase) >= 0 ||
               path.IndexOf("badapple", StringComparison.OrdinalIgnoreCase) >= 0 ||
               path.IndexOf("bad_apple", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsFocusMaterial(Material? material)
    {
        if (material == null)
        {
            return false;
        }
        var name = Safe(() => material.name ?? string.Empty);
        var shader = Safe(() => material.shader == null ? string.Empty : material.shader.name ?? string.Empty);
        var texture = Safe(() => material.mainTexture == null ? string.Empty : material.mainTexture.name ?? string.Empty);
        return name.IndexOf("touhou", StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.IndexOf("elfin", StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.IndexOf("thief", StringComparison.OrdinalIgnoreCase) >= 0 ||
               shader.IndexOf("BadApple", StringComparison.OrdinalIgnoreCase) >= 0 ||
               shader.IndexOf("touhou", StringComparison.OrdinalIgnoreCase) >= 0 ||
               texture.IndexOf("elfin", StringComparison.OrdinalIgnoreCase) >= 0 ||
               texture.IndexOf("thief", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string DescribeArgs(object[]? args)
    {
        if (args == null || args.Length == 0)
        {
            return "<none>";
        }

        var parts = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            var texture = args[i] as Texture;
            parts.Add(texture == null ? $"arg{i}={args[i]?.GetType().FullName ?? "<null>"}" : $"arg{i}={DescribeTexture(texture)}");
        }
        return string.Join("; ", parts);
    }

    private static string DescribeObject(UnityEngine.Object? obj)
    {
        if (obj == null)
        {
            return "<null>";
        }
        return $"{obj.name}#{obj.GetInstanceID()} type={Il2CppTypeName(obj)}";
    }

    private static string DescribeObject(Il2CppObjectBase? obj)
    {
        if (obj == null)
        {
            return "<null>";
        }
        var unityObject = obj.TryCast<UnityEngine.Object>();
        return unityObject != null ? DescribeObject(unityObject) : Il2CppTypeName(obj);
    }

    private static string Prop(Material material, string property)
    {
        return Safe(() => material.HasProperty(property) ? material.GetFloat(property).ToString("0.###") : "N/A");
    }

    private static string MaterialColor(Material material)
    {
        return Safe(() => material.HasProperty("_Color") ? material.GetColor("_Color").ToString() : "N/A");
    }

    private static string FormatFloat(float value)
    {
        return value.ToString("0.###");
    }

    private static string PathOf(GameObject? gameObject)
    {
        if (gameObject == null)
        {
            return "<null>";
        }
        try
        {
            var names = new Stack<string>();
            var transform = gameObject.transform;
            while (transform != null)
            {
                names.Push(transform.name);
                transform = transform.parent;
            }
            return string.Join("/", names);
        }
        catch
        {
            return gameObject.name ?? "<unnamed>";
        }
    }

    private static string Il2CppTypeName(Il2CppObjectBase? obj)
    {
        if (obj == null)
        {
            return "<null>";
        }
        return Safe(() => obj.GetType().FullName ?? "<unknown>");
    }

    private static string Safe(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            return "<err:" + ex.GetType().Name + ">";
        }
    }
}
