using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppSpine;
using Il2CppSpine.Unity;
using MelonLoader;
using UnityEngine;
using TouhouLogicApi = Il2CppAssets.Scripts.TouhouLogic.TouhouLogic;

namespace SkinForger.Diagnostics;

public sealed class TouhouRenderStateProbeMod : MelonMod
{
    private const string Prefix = "[SkinForge][TouhouRenderState]";

    private static readonly HashSet<int> ArmedRendererIds = new();
    private static readonly HashSet<int> CapturedRendererIds = new();
    private static readonly Dictionary<int, SkeletonRenderer.SkeletonRendererDelegate> Callbacks = new();
    private static HarmonyLib.Harmony? _harmony;

    public override void OnInitializeMelon()
    {
        _harmony = new HarmonyLib.Harmony("skinforger.touhou-render-state-probe");
        var target = AccessTools.Method(typeof(TouhouLogicApi), "ReplaceBadApple");
        var postfix = AccessTools.Method(typeof(TouhouRenderStateProbeMod), nameof(AfterReplaceBadApple));
        if (target == null || postfix == null)
        {
            MelonLogger.Warning($"{Prefix} ReplaceBadApple patch unavailable; probe is inactive.");
            return;
        }

        _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
        MelonLogger.Msg($"{Prefix} v0.1.0 loaded; one-shot mesh/material capture is armed.");
    }

    public override void OnDeinitializeMelon()
    {
        foreach (var pair in Callbacks)
        {
            TryDetach(pair.Key, pair.Value);
        }
        Callbacks.Clear();
        ArmedRendererIds.Clear();
        CapturedRendererIds.Clear();
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
                var renderer = customMaterials.skeletonRenderer;
                if (renderer == null || !IsLilithTarget(customMaterials, renderer))
                {
                    continue;
                }

                ArmOneShot(customMaterials, renderer);
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"{Prefix} arm failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ArmOneShot(SkeletonRendererCustomMaterials customMaterials, SkeletonRenderer renderer)
    {
        int rendererId = ((UnityEngine.Object)renderer).GetInstanceID();
        if (CapturedRendererIds.Contains(rendererId) || !ArmedRendererIds.Add(rendererId))
        {
            return;
        }

        SkeletonRenderer.SkeletonRendererDelegate callback = null!;
        callback = (Action<SkeletonRenderer>)(updated => CaptureOnce(customMaterials, updated ?? renderer, callback));
        Callbacks[rendererId] = callback;
        renderer.add_OnMeshAndMaterialsUpdated(callback);
        MelonLogger.Msg($"{Prefix} ARMED renderer={DescribeRenderer(renderer)} trigger=TouhouLogic.ReplaceBadApple");
    }

    private static void CaptureOnce(
        SkeletonRendererCustomMaterials customMaterials,
        SkeletonRenderer renderer,
        SkeletonRenderer.SkeletonRendererDelegate callback)
    {
        int rendererId = SafeInt(() => ((UnityEngine.Object)renderer).GetInstanceID());
        try
        {
            if (rendererId == 0 || !CapturedRendererIds.Add(rendererId))
            {
                return;
            }

            MelonLogger.Msg($"{Prefix} BEGIN renderer={DescribeRenderer(renderer)} sample={InferSample(renderer)}");
            DumpSerializedOverrides(customMaterials);
            DumpActiveOverrideDictionaries(renderer);
            DumpAttachments(renderer.Skeleton);
            DumpFinalRenderer(renderer);
            MelonLogger.Msg($"{Prefix} END rendererId={rendererId}");
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"{Prefix} capture failed rendererId={rendererId}: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try
            {
                renderer.remove_OnMeshAndMaterialsUpdated(callback);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"{Prefix} detach failed rendererId={rendererId}: {ex.Message}");
            }
            ArmedRendererIds.Remove(rendererId);
            Callbacks.Remove(rendererId);
            MelonLogger.Msg($"{Prefix} DETACHED rendererId={rendererId}");
        }
    }

    private static void DumpSerializedOverrides(SkeletonRendererCustomMaterials customMaterials)
    {
        var materialOverrides = customMaterials.customMaterialOverrides;
        int materialCount = materialOverrides == null ? 0 : materialOverrides.Count;
        MelonLogger.Msg($"{Prefix} A.SerializedCustomMaterialOverride count={materialCount}");
        for (int i = 0; i < materialCount; i++)
        {
            var entry = materialOverrides![i];
            MelonLogger.Msg($"{Prefix} A.MaterialOverride index={i} disabled={Safe(() => entry.overrideDisabled.ToString())} original={DescribeMaterial(entry.originalMaterial)} replacement={DescribeMaterial(entry.replacementMaterial)}");
        }

        var slotOverrides = customMaterials.customSlotMaterials;
        int slotCount = slotOverrides == null ? 0 : slotOverrides.Count;
        MelonLogger.Msg($"{Prefix} B.SerializedCustomSlotMaterials count={slotCount}");
        for (int i = 0; i < slotCount; i++)
        {
            var entry = slotOverrides![i];
            MelonLogger.Msg($"{Prefix} B.SlotOverride index={i} slot={Safe(() => entry.slotName ?? "<null>")} material={DescribeMaterial(entry.material)}");
        }
    }

    private static void DumpActiveOverrideDictionaries(SkeletonRenderer renderer)
    {
        try
        {
            var overrides = renderer.CustomMaterialOverride;
            MelonLogger.Msg($"{Prefix} A.ActiveCustomMaterialOverride count={overrides?.Count ?? 0}");
            if (overrides != null)
            {
                foreach (var pair in overrides)
                {
                    MelonLogger.Msg($"{Prefix} A.ActiveMaterial original={DescribeMaterial(pair.Key)} replacement={DescribeMaterial(pair.Value)}");
                }
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"{Prefix} A.ActiveCustomMaterialOverride unreadable: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            var slots = renderer.CustomSlotMaterials;
            MelonLogger.Msg($"{Prefix} B.ActiveCustomSlotMaterials count={slots?.Count ?? 0}");
            if (slots != null)
            {
                foreach (var pair in slots)
                {
                    string slotName = Safe(() => pair.Key?.data?.name ?? "<null>");
                    string attachmentName = Safe(() => pair.Key?.Attachment?.Name ?? "<null>");
                    MelonLogger.Msg($"{Prefix} B.ActiveSlot slot={slotName} attachment={attachmentName} material={DescribeMaterial(pair.Value)}");
                }
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"{Prefix} B.ActiveCustomSlotMaterials unreadable: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void DumpFinalRenderer(SkeletonRenderer renderer)
    {
        var meshRenderer = renderer.meshRenderer;
        var meshFilter = renderer.meshFilter;
        var mesh = meshFilter?.sharedMesh;
        if (meshRenderer == null)
        {
            MelonLogger.Warning($"{Prefix} C.MeshRenderer=<null>");
            return;
        }

        var materials = ((Renderer)meshRenderer).sharedMaterials;
        int materialCount = materials == null ? 0 : materials.Length;
        MelonLogger.Msg($"{Prefix} C.MeshRenderer object={PathOf(((Component)meshRenderer).gameObject)} sharedMaterials.Length={materialCount} materials=SKIPPED(no-instantiation)");
        for (int i = 0; i < materialCount; i++)
        {
            MelonLogger.Msg($"{Prefix} C.SharedMaterial slot={i} value={DescribeMaterial(materials![i])}");
        }

        DumpPropertyBlocks(meshRenderer, materials);

        if (mesh == null)
        {
            MelonLogger.Warning($"{Prefix} D.Mesh=<null>");
            return;
        }

        MelonLogger.Msg($"{Prefix} D.Mesh name={mesh.name}#{mesh.GetInstanceID()} vertexCount={mesh.vertexCount} subMeshCount={mesh.subMeshCount}");
        int subMeshCount = mesh.subMeshCount;
        for (int i = 0; i < subMeshCount; i++)
        {
            int indexCount = SafeInt(() => mesh.GetIndices(i).Length);
            string mapped = i < materialCount ? DescribeMaterial(materials![i]) : "<no-material-slot>";
            MelonLogger.Msg($"{Prefix} D.Submesh index={i} indexCount={indexCount} materialSlot={i} material={mapped}");
        }

        DumpMeshUv(mesh);
        DumpVertexColors(mesh);
        DumpUniqueTextures(materials);
    }

    private static void DumpPropertyBlocks(MeshRenderer meshRenderer, Material[]? materials)
    {
        try
        {
            bool has = ((Renderer)meshRenderer).HasPropertyBlock();
            MelonLogger.Msg($"{Prefix} E.MaterialPropertyBlock rendererHasBlock={has}");
            if (!has)
            {
                return;
            }

            var block = new MaterialPropertyBlock();
            ((Renderer)meshRenderer).GetPropertyBlock(block);
            MelonLogger.Msg($"{Prefix} E.MPB scope=renderer {DescribePropertyBlock(block)}");

            int count = materials == null ? 0 : materials.Length;
            for (int i = 0; i < count; i++)
            {
                block.Clear();
                ((Renderer)meshRenderer).GetPropertyBlock(block, i);
                MelonLogger.Msg($"{Prefix} E.MPB scope=materialSlot:{i} {DescribePropertyBlock(block)}");
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"{Prefix} E.MaterialPropertyBlock unreadable: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string DescribePropertyBlock(MaterialPropertyBlock block)
    {
        string[] textures = { "_MainTex" };
        string[] colors = { "_Color", "_MainColor", "_TintColor" };
        string[] floats = { "_Alpha", "_Cutoff", "_Stencil", "_StencilComp", "_StencilOp", "_StencilReadMask", "_StencilWriteMask", "_ColorMask", "_Blend", "_SrcBlend", "_DstBlend", "_ZWrite", "_Cull" };
        var values = new List<string> { $"isEmpty={block.isEmpty}" };
        foreach (string property in textures)
        {
            values.Add($"{property}={DescribeTexture(block.GetTexture(property))}");
        }
        foreach (string property in colors)
        {
            values.Add($"{property}={block.GetColor(property)}");
        }
        foreach (string property in floats)
        {
            values.Add($"{property}={block.GetFloat(property):0.###}");
        }
        values.Add("note=getters-return-defaults-for-unset-properties");
        return string.Join(" ", values);
    }

    private static void DumpMeshUv(Mesh mesh)
    {
        try
        {
            var uv = mesh.uv;
            int count = uv == null ? 0 : uv.Length;
            float minX = float.PositiveInfinity;
            float minY = float.PositiveInfinity;
            float maxX = float.NegativeInfinity;
            float maxY = float.NegativeInfinity;
            int nonFinite = 0;
            int outside = 0;
            var sample = new List<string>();
            for (int i = 0; i < count; i++)
            {
                Vector2 value = uv![i];
                if (float.IsNaN(value.x) || float.IsNaN(value.y) || float.IsInfinity(value.x) || float.IsInfinity(value.y))
                {
                    nonFinite++;
                    continue;
                }
                minX = Math.Min(minX, value.x);
                minY = Math.Min(minY, value.y);
                maxX = Math.Max(maxX, value.x);
                maxY = Math.Max(maxY, value.y);
                if (value.x < 0f || value.x > 1f || value.y < 0f || value.y > 1f)
                {
                    outside++;
                }
                if (sample.Count < 12)
                {
                    sample.Add($"{i}:{value.x:0.######},{value.y:0.######}");
                }
            }
            string range = count == 0 ? "N/A" : $"{minX:0.######},{minY:0.######}..{maxX:0.######},{maxY:0.######}";
            MelonLogger.Msg($"{Prefix} F.MeshUV count={count} range={range} nonFinite={nonFinite} outside01={outside} sample=[{string.Join(";", sample)}]");
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"{Prefix} F.MeshUV unreadable: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void DumpVertexColors(Mesh mesh)
    {
        try
        {
            var colors = mesh.colors32;
            int count = colors == null ? 0 : colors.Length;
            int alphaZero = 0;
            int alphaFull = 0;
            int alphaPartial = 0;
            int minA = 255;
            int maxA = 0;
            int minR = 255;
            int minG = 255;
            int minB = 255;
            int maxR = 0;
            int maxG = 0;
            int maxB = 0;
            for (int i = 0; i < count; i++)
            {
                Color32 value = colors![i];
                minA = Math.Min(minA, value.a);
                maxA = Math.Max(maxA, value.a);
                minR = Math.Min(minR, value.r);
                minG = Math.Min(minG, value.g);
                minB = Math.Min(minB, value.b);
                maxR = Math.Max(maxR, value.r);
                maxG = Math.Max(maxG, value.g);
                maxB = Math.Max(maxB, value.b);
                if (value.a == 0) alphaZero++;
                else if (value.a == 255) alphaFull++;
                else alphaPartial++;
            }
            MelonLogger.Msg($"{Prefix} G.VertexColor count={count} alphaRange={(count == 0 ? "N/A" : $"{minA}..{maxA}")} alpha0={alphaZero} alpha255={alphaFull} alphaPartial={alphaPartial} rgbRange={(count == 0 ? "N/A" : $"R{minR}..{maxR},G{minG}..{maxG},B{minB}..{maxB}")}");
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"{Prefix} G.VertexColor unreadable: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void DumpAttachments(Skeleton? skeleton)
    {
        var slots = skeleton?.Slots;
        if (slots == null)
        {
            MelonLogger.Warning($"{Prefix} Attachment skeleton/slots unavailable");
            return;
        }

        MelonLogger.Msg($"{Prefix} Attachment activeSlotCount={slots.Count}");
        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots.Items[i];
            var attachment = slot?.Attachment;
            if (attachment == null)
            {
                continue;
            }

            string slotName = Safe(() => slot!.data?.name ?? "<null>");
            string attachmentName = Safe(() => attachment.Name ?? "<null>");
            string attachmentType = attachment.GetType().FullName ?? "<unknown>";
            var region = GetRegion(attachment);
            var material = region?.page?.rendererObject?.TryCast<Material>();
            string attachmentUv = DescribeAttachmentUv(attachment);
            MelonLogger.Msg($"{Prefix} Attachment slotIndex={i} slot={slotName} attachment={attachmentName} type={attachmentType} region={DescribeRegion(region)} pageMaterial={DescribeMaterial(material)} attachmentUV={attachmentUv}");
        }
    }

    private static string DescribeAttachmentUv(Attachment attachment)
    {
        try
        {
            var regionAttachment = attachment.TryCast<RegionAttachment>();
            if (regionAttachment != null)
            {
                return $"RegionAttachment:{DescribeFloatArray(regionAttachment.uvs)}";
            }
            var meshAttachment = attachment.TryCast<MeshAttachment>();
            if (meshAttachment != null)
            {
                return $"MeshAttachment:{DescribeFloatArray(meshAttachment.uvs)}";
            }
            return "N/A";
        }
        catch (Exception ex)
        {
            return $"<err:{ex.GetType().Name}>";
        }
    }

    private static string DescribeFloatArray(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<float>? values)
    {
        if (values == null)
        {
            return "<null>";
        }
        int count = values.Length;
        float min = float.PositiveInfinity;
        float max = float.NegativeInfinity;
        int nonFinite = 0;
        var sample = new List<string>();
        for (int i = 0; i < count; i++)
        {
            float value = values[i];
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                nonFinite++;
                continue;
            }
            min = Math.Min(min, value);
            max = Math.Max(max, value);
            if (sample.Count < 16)
            {
                sample.Add(value.ToString("0.######"));
            }
        }
        return $"count={count} range={(count == 0 ? "N/A" : $"{min:0.######}..{max:0.######}")} nonFinite={nonFinite} sample=[{string.Join(",", sample)}]";
    }

    private static AtlasRegion? GetRegion(Attachment attachment)
    {
        var hasRendererObject = attachment.TryCast<IHasRendererObject>();
        return hasRendererObject?.RendererObject?.TryCast<AtlasRegion>();
    }

    private static string DescribeRegion(AtlasRegion? region)
    {
        if (region == null)
        {
            return "<null>";
        }
        return $"{Safe(() => region.name ?? "<unnamed>")}#ptr={Safe(() => region.Pointer.ToInt64().ToString("X"))} rect={region.x},{region.y},{region.width}x{region.height} original={region.originalWidth}x{region.originalHeight} offset={region.offsetX:0.###},{region.offsetY:0.###} rotate={region.rotate} degrees={region.degrees} uv={region.u:0.######},{region.v:0.######},{region.u2:0.######},{region.v2:0.######}";
    }

    private static void DumpUniqueTextures(Material[]? materials)
    {
        if (materials == null)
        {
            return;
        }
        var seen = new HashSet<int>();
        for (int i = 0; i < materials.Length; i++)
        {
            var texture = materials[i]?.mainTexture;
            if (texture == null || !seen.Add(texture.GetInstanceID()))
            {
                continue;
            }
            DumpTexture(texture);
        }
    }

    private static void DumpTexture(Texture texture)
    {
        var texture2D = texture.TryCast<Texture2D>();
        if (texture2D == null)
        {
            MelonLogger.Msg($"{Prefix} H.Texture {DescribeTexture(texture)} alpha=unavailable(non-Texture2D)");
            return;
        }

        bool readable = SafeBool(() => texture2D.isReadable);
        string mipmap = Safe(() => texture2D.mipmapCount.ToString());
        string alphaReport = readable ? DescribeTextureAlpha(texture2D) : "unavailable(not-readable)";
        MelonLogger.Msg($"{Prefix} H.Texture {DescribeTexture(texture)} readable={readable} mipmapCount={mipmap} alpha={alphaReport}");
    }

    private static string DescribeTextureAlpha(Texture2D texture)
    {
        try
        {
            var pixels = texture.GetPixels32();
            if (pixels == null || pixels.Length == 0)
            {
                return "empty";
            }
            int min = 255;
            int max = 0;
            int transparent = 0;
            int opaque = 0;
            int partial = 0;
            int transparentWithRgb = 0;
            int[] bins = new int[8];
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 pixel = pixels[i];
                min = Math.Min(min, pixel.a);
                max = Math.Max(max, pixel.a);
                if (pixel.a == 0)
                {
                    transparent++;
                    if (pixel.r != 0 || pixel.g != 0 || pixel.b != 0) transparentWithRgb++;
                }
                else if (pixel.a == 255) opaque++;
                else partial++;
                bins[Math.Min(7, pixel.a / 32)]++;
            }
            return $"range={min}..{max} pixels={pixels.Length} zero={transparent} partial={partial} full={opaque} transparentWithRgb={transparentWithRgb} bins32=[{string.Join(",", bins)}]";
        }
        catch (Exception ex)
        {
            return $"<err:{ex.GetType().Name}:{ex.Message}>";
        }
    }

    private static string DescribeMaterial(Material? material)
    {
        if (material == null)
        {
            return "<null>";
        }
        return $"{material.name}#{material.GetInstanceID()} shader={Safe(() => material.shader?.name ?? "<null>")}#{SafeInt(() => material.shader == null ? 0 : material.shader.GetInstanceID())} mainTexture={DescribeTexture(material.mainTexture)} keywords=[{Safe(() => string.Join(",", material.shaderKeywords))}] props={DescribeKnownProperties(material)}";
    }

    private static string DescribeKnownProperties(Material material)
    {
        string[] colors = { "_Color", "_MainColor", "_TintColor" };
        string[] floats = { "_Alpha", "_Cutoff", "_Stencil", "_StencilComp", "_StencilOp", "_StencilReadMask", "_StencilWriteMask", "_ColorMask", "_Blend", "_SrcBlend", "_DstBlend", "_ZWrite", "_Cull", "_StraightAlphaInput" };
        var values = new List<string>();
        foreach (string property in colors)
        {
            values.Add(material.HasProperty(property) ? $"{property}={material.GetColor(property)}" : $"{property}=N/A");
        }
        foreach (string property in floats)
        {
            values.Add(material.HasProperty(property) ? $"{property}={material.GetFloat(property):0.###}" : $"{property}=N/A");
        }
        return string.Join(",", values);
    }

    private static string DescribeTexture(Texture? texture)
    {
        if (texture == null)
        {
            return "<null>";
        }
        var texture2D = texture.TryCast<Texture2D>();
        string format = texture2D == null ? "N/A" : Safe(() => texture2D.format.ToString());
        return $"{texture.name}#{texture.GetInstanceID()} type={texture.GetType().FullName} size={texture.width}x{texture.height} format={format} filter={texture.filterMode} wrap={texture.wrapMode}";
    }

    private static bool IsLilithTarget(SkeletonRendererCustomMaterials customMaterials, SkeletonRenderer renderer)
    {
        string path = PathOf(((Component)renderer).gameObject);
        if (ContainsLilith(path))
        {
            return true;
        }

        var entries = customMaterials.customMaterialOverrides;
        if (entries != null)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry != null &&
                    (ContainsLilith(Safe(() => entry.originalMaterial?.name ?? string.Empty)) ||
                     ContainsLilith(Safe(() => entry.replacementMaterial?.name ?? string.Empty))))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ContainsLilith(string value) =>
        value.IndexOf("elfin_devil", StringComparison.OrdinalIgnoreCase) >= 0 ||
        value.IndexOf("lilith", StringComparison.OrdinalIgnoreCase) >= 0;

    private static string InferSample(SkeletonRenderer renderer)
    {
        var slots = renderer.Skeleton?.Slots;
        if (slots != null)
        {
            for (int i = 0; i < slots.Count; i++)
            {
                var attachment = slots.Items[i]?.Attachment;
                var texture = attachment == null ? null : GetRegion(attachment)?.page?.rendererObject?.TryCast<Material>()?.mainTexture;
                if (texture != null && texture.height != 256)
                {
                    return "SkinForgerRuntime(inferred)";
                }
            }
        }
        return "Original(inferred)";
    }

    private static string DescribeRenderer(SkeletonRenderer renderer)
    {
        var go = ((Component)renderer).gameObject;
        return $"{PathOf(go)}#{((UnityEngine.Object)renderer).GetInstanceID()} skeletonPtr={Safe(() => renderer.Skeleton?.Pointer.ToInt64().ToString("X") ?? "<null>")}";
    }

    private static void TryDetach(int rendererId, SkeletonRenderer.SkeletonRendererDelegate callback)
    {
        try
        {
            // Normal operation detaches in the callback. Deinitialization only clears managed roots.
            MelonLogger.Msg($"{Prefix} deinit pending rendererId={rendererId}; callback root released.");
        }
        catch
        {
            // No renderer lookup is performed here by design.
        }
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
            return Safe(() => gameObject.name ?? "<unnamed>");
        }
    }

    private static string Safe(Func<string> read)
    {
        try { return read(); }
        catch (Exception ex) { return $"<err:{ex.GetType().Name}>"; }
    }

    private static int SafeInt(Func<int> read)
    {
        try { return read(); }
        catch { return 0; }
    }

    private static bool SafeBool(Func<bool> read)
    {
        try { return read(); }
        catch { return false; }
    }
}
