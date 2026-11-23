using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppAssets.Scripts.ReplaceTools;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppSpine;
using Il2CppSpine.Unity;
using MelonLoader;
using UnityEngine;
using TouhouLogicApi = Il2CppAssets.Scripts.TouhouLogic.TouhouLogic;

namespace SkinForger.TouhouCompat;

public sealed class TouhouCompatMod : MelonMod
{
    private static HarmonyLib.Harmony? _harmony;

    private static readonly Dictionary<long, SkeletonRendererCustomMaterials> ComponentsBySkeleton = new();
    private static readonly HashSet<long> SkinForgerSkeletons = new();
    private static readonly Dictionary<string, Material> OriginalReplacementMaterials = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Material> CompatMaterials = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedSyncs = new(StringComparer.Ordinal);
    private static readonly Dictionary<int, SkeletonRenderer> PendingFinalSyncRenderers = new();
    private static readonly Dictionary<int, SkeletonRenderer.SkeletonRendererDelegate> PendingFinalSyncCallbacks = new();

    public override void OnInitializeMelon()
    {
        _harmony = new HarmonyLib.Harmony("skinforger.touhou-compat.v2");
        TryPatch(
            typeof(ReplaceSkeletonCustomMaterials),
            "Replace",
            nameof(AfterReplaceSkeletonCustomMaterials));
        TryPatch(typeof(TouhouLogicApi), "ReplaceBadApple", nameof(AfterReplaceBadApple));
        TryPatchSkinForgerApplySkeleton();
        MelonLogger.Msg("[SkinForge][TouhouCompat] v0.3.0 loaded; event-driven final-renderer material sync is armed.");
    }

    public override void OnDeinitializeMelon()
    {
        foreach (var pair in PendingFinalSyncCallbacks)
        {
            if (PendingFinalSyncRenderers.TryGetValue(pair.Key, out var renderer) && renderer != null)
            {
                try
                {
                    renderer.remove_OnMeshAndMaterialsUpdated(pair.Value);
                }
                catch
                {
                    // The renderer may already be destroyed during scene teardown.
                }
            }
        }
        PendingFinalSyncCallbacks.Clear();
        PendingFinalSyncRenderers.Clear();
        ComponentsBySkeleton.Clear();
        SkinForgerSkeletons.Clear();
        OriginalReplacementMaterials.Clear();
        CompatMaterials.Clear();
        LoggedSyncs.Clear();
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

    private static void AfterReplaceSkeletonCustomMaterials(ReplaceSkeletonCustomMaterials __instance, int index)
    {
        if (__instance == null || __instance.skeletonCustomMaterials == null)
        {
            return;
        }

        var customMaterials = __instance.skeletonCustomMaterials;
        long skeletonKey = RegisterComponent(customMaterials);

        if (!__instance.enable)
        {
            RestoreReplacementEntries(customMaterials);
            return;
        }

        if (skeletonKey != 0 && SkinForgerSkeletons.Contains(skeletonKey))
        {
            SyncReplacementEntries(customMaterials, $"ReplaceSkeletonCustomMaterials.Replace({index})");
        }
    }

    private static void AfterReplaceBadApple(GameObject go)
    {
        if (go == null)
        {
            return;
        }

        try
        {
            var components = go.GetComponentsInChildren<SkeletonRendererCustomMaterials>(true);
            if (components == null)
            {
                return;
            }

            for (int i = 0; i < components.Length; i++)
            {
                var customMaterials = components[i];
                if (customMaterials == null)
                {
                    continue;
                }

                long skeletonKey = RegisterComponent(customMaterials);
                if (skeletonKey != 0 && SkinForgerSkeletons.Contains(skeletonKey))
                {
                    SyncReplacementEntries(customMaterials, "TouhouLogic.ReplaceBadApple");
                    ArmFinalRendererSync(customMaterials);
                }
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[SkinForge][TouhouCompat] Bad Apple hierarchy inspection failed: {ex.Message}");
        }
    }

    private static void ArmFinalRendererSync(SkeletonRendererCustomMaterials customMaterials)
    {
        var renderer = customMaterials.skeletonRenderer;
        if (renderer == null)
        {
            return;
        }

        int rendererId = ((UnityEngine.Object)renderer).GetInstanceID();
        if (PendingFinalSyncCallbacks.ContainsKey(rendererId))
        {
            return;
        }

        SkeletonRenderer.SkeletonRendererDelegate callback = null!;
        callback = (Action<SkeletonRenderer>)(updated =>
            FinalizeRendererMaterials(customMaterials, updated ?? renderer, callback));
        PendingFinalSyncRenderers[rendererId] = renderer;
        PendingFinalSyncCallbacks[rendererId] = callback;
        renderer.add_OnMeshAndMaterialsUpdated(callback);
        MelonLogger.Msg(
            "[SkinForge][TouhouCompat] armed final renderer sync " +
            $"object={PathOf(((Component)renderer).gameObject)} rendererId={rendererId}");
    }

    private static void FinalizeRendererMaterials(
        SkeletonRendererCustomMaterials customMaterials,
        SkeletonRenderer renderer,
        SkeletonRenderer.SkeletonRendererDelegate callback)
    {
        int rendererId = SafeInt(() => ((UnityEngine.Object)renderer).GetInstanceID());
        try
        {
            // Detach before touching material state so this remains strictly one-shot.
            renderer.remove_OnMeshAndMaterialsUpdated(callback);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[SkinForge][TouhouCompat] final sync detach failed rendererId={rendererId}: {ex.Message}");
        }
        finally
        {
            PendingFinalSyncCallbacks.Remove(rendererId);
            PendingFinalSyncRenderers.Remove(rendererId);
        }

        try
        {
            var skeleton = renderer.Skeleton;
            var overrides = customMaterials.customMaterialOverrides;
            if (skeleton == null || overrides == null)
            {
                return;
            }

            int componentId = ((UnityEngine.Object)customMaterials).GetInstanceID();
            int dictionaryChanges = 0;
            for (int i = 0; i < overrides.Count; i++)
            {
                var entry = overrides[i];
                if (entry == null || entry.overrideDisabled || entry.originalMaterial == null || entry.replacementMaterial == null)
                {
                    continue;
                }

                string entryKey = EntryKey(componentId, i);
                if (!OriginalReplacementMaterials.TryGetValue(entryKey, out var sourceMaterial) || sourceMaterial == null)
                {
                    sourceMaterial = entry.replacementMaterial;
                    OriginalReplacementMaterials[entryKey] = sourceMaterial;
                }
                if (!IsBadAppleMaterial(sourceMaterial))
                {
                    continue;
                }

                var runtimeTexture = FindRuntimeAtlasTexture(skeleton, sourceMaterial);
                if (runtimeTexture == null)
                {
                    continue;
                }

                var compat = GetCompatMaterial(sourceMaterial, runtimeTexture);
                entry.replacementMaterial = compat;
                renderer.CustomMaterialOverride[entry.originalMaterial] = compat;
                dictionaryChanges++;
            }

            int finalSlotChanges = 0;
            var meshRenderer = renderer.meshRenderer;
            var finalMaterials = meshRenderer == null ? null : ((Renderer)meshRenderer).sharedMaterials;
            if (finalMaterials != null)
            {
                for (int i = 0; i < finalMaterials.Length; i++)
                {
                    var material = finalMaterials[i];
                    if (material == null || !IsBadAppleMaterial(material))
                    {
                        continue;
                    }

                    var runtimeTexture = FindRuntimeAtlasTexture(skeleton, material);
                    if (runtimeTexture == null ||
                        (material.mainTexture != null && SameObject(material.mainTexture, runtimeTexture)))
                    {
                        continue;
                    }

                    finalMaterials[i] = GetCompatMaterial(material, runtimeTexture);
                    finalSlotChanges++;
                }

                if (finalSlotChanges > 0)
                {
                    ((Renderer)meshRenderer!).sharedMaterials = finalMaterials;
                }
            }

            MelonLogger.Msg(
                "[SkinForge][TouhouCompat] finalized renderer materials " +
                $"object={PathOf(((Component)renderer).gameObject)} rendererId={rendererId} " +
                $"overrideChanges={dictionaryChanges} finalSlotChanges={finalSlotChanges} " +
                $"finalMaterials={DescribeMaterials(finalMaterials)}");
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[SkinForge][TouhouCompat] final renderer sync failed rendererId={rendererId}: {ex.Message}");
        }
    }

    private static void AfterSkinForgerApplySkeleton(Skeleton skeleton, int selected)
    {
        if (selected < 0 || skeleton == null)
        {
            return;
        }

        long skeletonKey = skeleton.Pointer.ToInt64();
        SkinForgerSkeletons.Add(skeletonKey);
        if (!ComponentsBySkeleton.TryGetValue(skeletonKey, out var customMaterials) || customMaterials == null)
        {
            return;
        }

        SyncReplacementEntries(customMaterials, "SkinForger.ApplySkeleton");
    }

    private static long RegisterComponent(SkeletonRendererCustomMaterials customMaterials)
    {
        try
        {
            var skeleton = customMaterials.skeletonRenderer?.Skeleton;
            if (skeleton != null)
            {
                long skeletonKey = skeleton.Pointer.ToInt64();
                ComponentsBySkeleton[skeletonKey] = customMaterials;
                return skeletonKey;
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[SkinForge][TouhouCompat] component registration failed: {ex.Message}");
        }
        return 0;
    }

    private static void SyncReplacementEntries(SkeletonRendererCustomMaterials customMaterials, string reason)
    {
        try
        {
            var skeletonRenderer = customMaterials.skeletonRenderer;
            var skeleton = skeletonRenderer?.Skeleton;
            var overrides = customMaterials.customMaterialOverrides;
            if (skeleton == null || overrides == null || overrides.Count == 0)
            {
                return;
            }

            if (!SkinForgerSkeletons.Contains(skeleton.Pointer.ToInt64()))
            {
                return;
            }

            int componentId = ((UnityEngine.Object)customMaterials).GetInstanceID();
            int changed = 0;
            for (int i = 0; i < overrides.Count; i++)
            {
                var entry = overrides[i];
                if (entry == null || entry.overrideDisabled || entry.replacementMaterial == null)
                {
                    continue;
                }

                string entryKey = EntryKey(componentId, i);
                if (!OriginalReplacementMaterials.TryGetValue(entryKey, out var sourceMaterial) || sourceMaterial == null)
                {
                    sourceMaterial = entry.replacementMaterial;
                    OriginalReplacementMaterials[entryKey] = sourceMaterial;
                }

                if (!IsBadAppleMaterial(sourceMaterial))
                {
                    continue;
                }

                var runtimeTexture = FindRuntimeAtlasTexture(skeleton, sourceMaterial);
                if (runtimeTexture == null)
                {
                    continue;
                }

                var current = entry.replacementMaterial;
                if (current.mainTexture != null && SameObject(current.mainTexture, runtimeTexture))
                {
                    continue;
                }

                var compat = GetCompatMaterial(sourceMaterial, runtimeTexture);
                entry.replacementMaterial = compat;
                changed++;

                string logKey = $"{componentId}|{i}|{sourceMaterial.GetInstanceID()}|{runtimeTexture.GetInstanceID()}";
                if (LoggedSyncs.Add(logKey))
                {
                    MelonLogger.Msg(
                        "[SkinForge][TouhouCompat] synced " +
                        $"reason={reason} object={PathOf(((Component)customMaterials).gameObject)} " +
                        $"original={DescribeMaterial(entry.originalMaterial)} " +
                        $"replacement={DescribeMaterial(sourceMaterial)} " +
                        $"runtimeTexture={DescribeTexture(runtimeTexture)} " +
                        $"compat={DescribeMaterial(compat)}");
                }
            }

            if (changed > 0)
            {
                // Refresh Spine's material override dictionary once after all entries are updated.
                customMaterials.SetCustomMats(false);
                customMaterials.SetCustomMats(true);
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[SkinForge][TouhouCompat] sync failed reason={reason}: {ex.Message}");
        }
    }

    private static void RestoreReplacementEntries(SkeletonRendererCustomMaterials customMaterials)
    {
        try
        {
            var overrides = customMaterials.customMaterialOverrides;
            if (overrides == null)
            {
                return;
            }

            int componentId = ((UnityEngine.Object)customMaterials).GetInstanceID();
            for (int i = 0; i < overrides.Count; i++)
            {
                var entry = overrides[i];
                if (entry == null)
                {
                    continue;
                }

                if (OriginalReplacementMaterials.TryGetValue(EntryKey(componentId, i), out var original) && original != null)
                {
                    entry.replacementMaterial = original;
                }
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[SkinForge][TouhouCompat] restore failed: {ex.Message}");
        }
    }

    private static Texture? FindRuntimeAtlasTexture(Skeleton skeleton, Material badAppleMaterial)
    {
        if (skeleton.Slots == null)
        {
            return null;
        }

        string originalTextureName = Safe(() => badAppleMaterial.mainTexture?.name ?? string.Empty);
        int originalTextureId = SafeInt(() => badAppleMaterial.mainTexture == null
            ? 0
            : ((UnityEngine.Object)badAppleMaterial.mainTexture).GetInstanceID());

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
        string key = $"{source.GetInstanceID()}|{runtimeTexture.GetInstanceID()}";
        if (CompatMaterials.TryGetValue(key, out var cached) && cached != null)
        {
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

    private static bool IsBadAppleMaterial(Material material)
    {
        string materialName = Safe(() => material.name ?? string.Empty);
        string shaderName = Safe(() => material.shader?.name ?? string.Empty);
        return materialName.IndexOf("touhou_black", StringComparison.OrdinalIgnoreCase) >= 0 ||
               shaderName.IndexOf("BadApple", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool SameObject(UnityEngine.Object left, UnityEngine.Object right) =>
        left.GetInstanceID() == right.GetInstanceID();

    private static string EntryKey(int componentId, int index) => $"{componentId}|{index}";

    private static string DescribeMaterial(Material? material) => material == null
        ? "<null>"
        : $"{material.name}#{material.GetInstanceID()} shader={Safe(() => material.shader?.name ?? "<null>")} mainTexture={DescribeTexture(material.mainTexture)}";

    private static string DescribeTexture(Texture? texture) => texture == null
        ? "<null>"
        : $"{texture.name}#{texture.GetInstanceID()} size={texture.width}x{texture.height}";

    private static string DescribeMaterials(Material[]? materials)
    {
        if (materials == null)
        {
            return "<null>";
        }
        var values = new List<string>(materials.Length);
        for (int i = 0; i < materials.Length; i++)
        {
            values.Add($"{i}:{DescribeMaterial(materials[i])}");
        }
        return "[" + string.Join(";", values) + "]";
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
