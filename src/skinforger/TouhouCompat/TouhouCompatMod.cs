using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppSpine;
using Il2CppSpine.Unity;
using MelonLoader;
using UnityEngine;

namespace SkinForger.TouhouCompat;

public sealed class TouhouCompatMod : MelonMod
{
    private static HarmonyLib.Harmony? _harmony;
    private static bool _badAppleActive;
    private static bool _applyingMaterial;

    private static readonly Dictionary<string, Material> CompatMaterials = new(StringComparer.Ordinal);
    private static readonly Dictionary<int, Material> OriginalRendererMaterials = new();
    private static readonly HashSet<string> LoggedSyncs = new(StringComparer.Ordinal);

    public override void OnInitializeMelon()
    {
        _harmony = new HarmonyLib.Harmony("skinforger.touhou-compat");
        TryPatch(typeof(ReverseColor), "SetInverse", nameof(AfterReverseColorSetInverse));
        TryPatchRendererSetter("sharedMaterial", nameof(AfterRendererSharedMaterialSet));
        TryPatchRendererSetter("material", nameof(AfterRendererMaterialSet));
        TryPatchSkinForgerApplySkeleton();
        MelonLogger.Msg("[SkinForge][TouhouCompat] loaded; BadApple material/runtime-atlas sync is armed.");
    }

    private static void TryPatch(Type type, string targetMethod, string postfixMethod)
    {
        try
        {
            var target = AccessTools.Method(type, targetMethod);
            var postfix = AccessTools.Method(typeof(TouhouCompatMod), postfixMethod);
            if (target == null || postfix == null)
            {
                MelonLogger.Warning($"[SkinForge][TouhouCompat] patch skipped: {type.FullName}.{targetMethod}");
                return;
            }
            _harmony!.Patch(target, postfix: new HarmonyMethod(postfix));
            MelonLogger.Msg($"[SkinForge][TouhouCompat] patched {type.FullName}.{targetMethod}");
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[SkinForge][TouhouCompat] patch failed: {type.FullName}.{targetMethod}: {ex.Message}");
        }
    }

    private static void TryPatchRendererSetter(string propertyName, string postfixMethod)
    {
        try
        {
            var target = AccessTools.PropertySetter(typeof(Renderer), propertyName);
            var postfix = AccessTools.Method(typeof(TouhouCompatMod), postfixMethod);
            if (target == null || postfix == null)
            {
                MelonLogger.Warning($"[SkinForge][TouhouCompat] renderer setter patch skipped: {propertyName}");
                return;
            }
            _harmony!.Patch(target, postfix: new HarmonyMethod(postfix));
            MelonLogger.Msg($"[SkinForge][TouhouCompat] patched UnityEngine.Renderer.set_{propertyName}");
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[SkinForge][TouhouCompat] renderer setter patch failed: {propertyName}: {ex.Message}");
        }
    }

    private static void TryPatchSkinForgerApplySkeleton()
    {
        try
        {
            var type = AccessTools.TypeByName("SkinForger.SkinForgerMod");
            var target = type == null ? null : AccessTools.Method(type, "ApplySkeleton");
            var postfix = AccessTools.Method(typeof(TouhouCompatMod), nameof(AfterSkinForgerApplySkeleton));
            if (target == null || postfix == null)
            {
                MelonLogger.Warning("[SkinForge][TouhouCompat] SkinForger ApplySkeleton patch skipped.");
                return;
            }
            _harmony!.Patch(target, postfix: new HarmonyMethod(postfix));
            MelonLogger.Msg("[SkinForge][TouhouCompat] patched SkinForger.SkinForgerMod.ApplySkeleton");
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[SkinForge][TouhouCompat] SkinForger ApplySkeleton patch failed: {ex.Message}");
        }
    }

    private static void AfterReverseColorSetInverse(ReverseColor __instance, bool inverse)
    {
        _badAppleActive = inverse;
        if (inverse)
        {
            ApplyAllActiveSkeletonRenderers("ReverseColor.SetInverse(true)");
        }
        else
        {
            RestoreCompatMaterials("ReverseColor.SetInverse(false)");
        }
    }

    private static void AfterRendererSharedMaterialSet(Renderer __instance, Material value)
    {
        if (_applyingMaterial)
        {
            return;
        }
        TryApplyRenderer(__instance, value, "Renderer.sharedMaterial");
    }

    private static void AfterRendererMaterialSet(Renderer __instance, Material value)
    {
        if (_applyingMaterial)
        {
            return;
        }
        TryApplyRenderer(__instance, value, "Renderer.material");
    }

    private static void AfterSkinForgerApplySkeleton(Skeleton skeleton, int selected)
    {
        if (selected < 0 || skeleton == null)
        {
            return;
        }
        ApplySkeletonRenderersUsingSkeleton(skeleton, "SkinForger.ApplySkeleton");
    }

    private static void ApplyAllActiveSkeletonRenderers(string reason)
    {
        try
        {
            var renderers = Resources.FindObjectsOfTypeAll<SkeletonRenderer>();
            if (renderers == null)
            {
                return;
            }

            for (int i = 0; i < renderers.Length; i++)
            {
                var skeletonRenderer = renderers[i];
                if (skeletonRenderer == null)
                {
                    continue;
                }
                var go = ((Component)skeletonRenderer).gameObject;
                if (go == null || !go.activeInHierarchy)
                {
                    continue;
                }
                TryApplySkeletonRenderer(skeletonRenderer, reason);
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[SkinForge][TouhouCompat] active renderer scan failed: {ex.Message}");
        }
    }

    private static void ApplySkeletonRenderersUsingSkeleton(Skeleton skeleton, string reason)
    {
        try
        {
            var renderers = Resources.FindObjectsOfTypeAll<SkeletonRenderer>();
            if (renderers == null)
            {
                return;
            }

            for (int i = 0; i < renderers.Length; i++)
            {
                var skeletonRenderer = renderers[i];
                if (skeletonRenderer == null || skeletonRenderer.Skeleton == null)
                {
                    continue;
                }
                if (skeletonRenderer.Skeleton.Pointer != skeleton.Pointer)
                {
                    continue;
                }
                TryApplySkeletonRenderer(skeletonRenderer, reason);
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[SkinForge][TouhouCompat] skeleton renderer lookup failed: {ex.Message}");
        }
    }

    private static void TryApplyRenderer(Renderer renderer, Material material, string reason)
    {
        try
        {
            if (renderer == null || material == null || !IsBadAppleMaterial(material))
            {
                return;
            }

            var skeletonRenderer = FindSkeletonRenderer(renderer);
            if (skeletonRenderer == null)
            {
                return;
            }

            TryApplySkeletonRenderer(skeletonRenderer, reason);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[SkinForge][TouhouCompat] renderer sync failed reason={reason}: {ex.Message}");
        }
    }

    private static SkeletonRenderer? FindSkeletonRenderer(Renderer renderer)
    {
        var direct = ((Component)renderer).GetComponent<SkeletonRenderer>();
        if (direct != null)
        {
            return direct;
        }

        var parent = ((Component)renderer).GetComponentInParent<SkeletonRenderer>();
        if (parent != null)
        {
            return parent;
        }

        var all = Resources.FindObjectsOfTypeAll<SkeletonRenderer>();
        if (all == null)
        {
            return null;
        }
        int rendererId = ((UnityEngine.Object)renderer).GetInstanceID();
        for (int i = 0; i < all.Length; i++)
        {
            var skeletonRenderer = all[i];
            if (skeletonRenderer?.meshRenderer == null)
            {
                continue;
            }
            if (((UnityEngine.Object)(Renderer)skeletonRenderer.meshRenderer).GetInstanceID() == rendererId)
            {
                return skeletonRenderer;
            }
        }
        return null;
    }

    private static void TryApplySkeletonRenderer(SkeletonRenderer skeletonRenderer, string reason)
    {
        var meshRenderer = skeletonRenderer.meshRenderer;
        if (meshRenderer == null)
        {
            return;
        }

        var renderer = (Renderer)meshRenderer;
        var badAppleMaterial = renderer.sharedMaterial;
        if (badAppleMaterial == null || !IsBadAppleMaterial(badAppleMaterial))
        {
            return;
        }

        var runtimeTexture = FindRuntimeAtlasTexture(skeletonRenderer.Skeleton, badAppleMaterial);
        if (runtimeTexture == null)
        {
            return;
        }

        if (badAppleMaterial.mainTexture != null &&
            ((UnityEngine.Object)badAppleMaterial.mainTexture).GetInstanceID() == ((UnityEngine.Object)runtimeTexture).GetInstanceID())
        {
            return;
        }

        var compatMaterial = GetCompatMaterial(badAppleMaterial, runtimeTexture);
        int rendererId = ((UnityEngine.Object)renderer).GetInstanceID();
        if (!OriginalRendererMaterials.ContainsKey(rendererId))
        {
            OriginalRendererMaterials[rendererId] = badAppleMaterial;
        }

        _applyingMaterial = true;
        try
        {
            renderer.sharedMaterial = compatMaterial;
        }
        finally
        {
            _applyingMaterial = false;
        }

        string path = PathOf(((Component)skeletonRenderer).gameObject);
        string logKey = $"{rendererId}|{((UnityEngine.Object)badAppleMaterial).GetInstanceID()}|{((UnityEngine.Object)runtimeTexture).GetInstanceID()}";
        if (LoggedSyncs.Add(logKey))
        {
            MelonLogger.Msg("[SkinForge][TouhouCompat] synced " +
                            $"reason={reason} object={path} " +
                            $"badMaterial={DescribeMaterial(badAppleMaterial)} " +
                            $"runtimeTexture={DescribeTexture(runtimeTexture)} " +
                            $"compatMaterial={DescribeMaterial(compatMaterial)}");
        }
    }

    private static Texture? FindRuntimeAtlasTexture(Skeleton? skeleton, Material badAppleMaterial)
    {
        if (skeleton?.Slots == null)
        {
            return null;
        }

        string originalTextureName = Safe(() => badAppleMaterial.mainTexture?.name ?? string.Empty);
        int originalTextureId = SafeInt(() => badAppleMaterial.mainTexture == null ? 0 : ((UnityEngine.Object)badAppleMaterial.mainTexture).GetInstanceID());
        var slots = skeleton.Slots;
        for (int i = 0; i < slots.Count; i++)
        {
            var attachment = slots.Items[i]?.Attachment;
            if (attachment == null)
            {
                continue;
            }

            var texture = GetAttachmentPageTexture(attachment);
            if (texture == null)
            {
                continue;
            }

            int textureId = ((UnityEngine.Object)texture).GetInstanceID();
            if (textureId == originalTextureId)
            {
                continue;
            }

            string textureName = Safe(() => texture.name ?? string.Empty);
            if (!string.IsNullOrEmpty(originalTextureName) &&
                !string.Equals(textureName, originalTextureName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return texture;
        }

        return null;
    }

    private static Texture? GetAttachmentPageTexture(Attachment attachment)
    {
        var hasRendererObject = attachment.TryCast<IHasRendererObject>();
        var region = hasRendererObject?.RendererObject?.TryCast<AtlasRegion>();
        var material = region?.page?.rendererObject?.TryCast<Material>();
        return material?.mainTexture;
    }

    private static Material GetCompatMaterial(Material source, Texture runtimeTexture)
    {
        string key = $"{((UnityEngine.Object)source).GetInstanceID()}|{((UnityEngine.Object)runtimeTexture).GetInstanceID()}";
        if (CompatMaterials.TryGetValue(key, out var cached) && cached != null)
        {
            if (cached.mainTexture != runtimeTexture)
            {
                cached.mainTexture = runtimeTexture;
            }
            return cached;
        }

        var material = new Material(source)
        {
            name = $"{source.name}_SkinForgerRuntime",
            mainTexture = runtimeTexture,
            renderQueue = source.renderQueue
        };
        CompatMaterials[key] = material;
        return material;
    }

    private static void RestoreCompatMaterials(string reason)
    {
        try
        {
            var renderers = Resources.FindObjectsOfTypeAll<Renderer>();
            if (renderers == null)
            {
                return;
            }

            int restored = 0;
            for (int i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer == null)
                {
                    continue;
                }

                var material = renderer.sharedMaterial;
                if (material == null || !IsCompatMaterial(material))
                {
                    continue;
                }

                int rendererId = ((UnityEngine.Object)renderer).GetInstanceID();
                if (!OriginalRendererMaterials.TryGetValue(rendererId, out var original) || original == null)
                {
                    continue;
                }

                _applyingMaterial = true;
                try
                {
                    renderer.sharedMaterial = original;
                    restored++;
                }
                finally
                {
                    _applyingMaterial = false;
                }
            }

            if (restored > 0)
            {
                MelonLogger.Msg($"[SkinForge][TouhouCompat] restored {restored} renderer material(s) reason={reason}");
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[SkinForge][TouhouCompat] restore failed: {ex.Message}");
        }
    }

    private static bool IsBadAppleMaterial(Material material)
    {
        string materialName = Safe(() => material.name ?? string.Empty);
        string shaderName = Safe(() => material.shader?.name ?? string.Empty);
        return materialName.IndexOf("touhou_black", StringComparison.OrdinalIgnoreCase) >= 0 ||
               shaderName.IndexOf("BadApple", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsCompatMaterial(Material material)
    {
        return Safe(() => material.name ?? string.Empty)
            .IndexOf("_SkinForgerRuntime", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string DescribeMaterial(Material? material)
    {
        if (material == null)
        {
            return "<null>";
        }

        return $"{material.name}#{material.GetInstanceID()} shader={DescribeObject(material.shader)} mainTexture={DescribeTexture(material.mainTexture)}";
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

        return $"{texture.name}#{texture.GetInstanceID()} size={texture.width}x{texture.height} type={texture.GetIl2CppType().FullName} format={format} filter={texture.filterMode} wrap={texture.wrapMode}";
    }

    private static string DescribeObject(UnityEngine.Object? obj)
    {
        if (obj == null)
        {
            return "<null>";
        }
        return $"{obj.name}#{obj.GetInstanceID()} type={obj.GetIl2CppType().FullName}";
    }

    private static string PathOf(GameObject? gameObject)
    {
        if (gameObject == null)
        {
            return "<null>";
        }

        var names = new Stack<string>();
        var transform = gameObject.transform;
        while (transform != null)
        {
            names.Push(transform.name);
            transform = transform.parent;
        }
        return string.Join("/", names);
    }

    private static string Safe(Func<string> fn)
    {
        try
        {
            return fn();
        }
        catch
        {
            return "<err>";
        }
    }

    private static int SafeInt(Func<int> fn)
    {
        try
        {
            return fn();
        }
        catch
        {
            return 0;
        }
    }
}
