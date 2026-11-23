using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSpine;
using Il2CppSpine.Unity;
using Il2CppSpine.Unity.AttachmentTools;
using System.Collections;
using UnityEngine;

namespace SkinForger.AtlasOwnershipFix;

public static class AtlasOwnershipManager
{
    private static readonly Dictionary<int, Texture2D> RuntimeRoots = new();
    private static readonly Hashtable SkeletonBindings = new();
    private static readonly Hashtable RendererStates = new();
    private static readonly Hashtable SkeletonOwners = new();
    private static int ActiveRendererId;
    private static string? ActiveOwnershipToken;

    public static void Prepare(SkeletonRenderer renderer)
    {
        Reconcile();
        if (renderer == null || renderer.Skeleton == null) return;

        int rendererId = renderer.GetInstanceID();
        long rendererPointer = renderer.Pointer.ToInt64();
        long skeletonId = renderer.Skeleton.Pointer.ToInt64();
        var state = RendererStates[rendererId] as RendererState;
        if (state != null && state.RendererPointer != rendererPointer)
        {
            CleanupState(state, true);
            RendererStates.Remove(rendererId);
            state = null;
        }

        if (state == null)
        {
            state = new RendererState(renderer, rendererId, rendererPointer);
            RendererStates[rendererId] = state;
        }
        else
        {
            ResetForApply(state);
        }

        if (state.SkeletonId != 0 && state.SkeletonId != skeletonId)
            RemoveSkeletonOwner(state);

        state.Skeleton = renderer.Skeleton;
        state.SkeletonId = skeletonId;
        state.OriginalSkin = renderer.Skeleton.skin;
        state.SourceSkin = state.OriginalSkin ?? renderer.Skeleton.Data?.defaultSkin;
        state.OwnershipToken = rendererId + ":" + rendererPointer.ToString("X");
        SkeletonOwners[skeletonId] = state;
        ActiveRendererId = rendererId;
        ActiveOwnershipToken = state.OwnershipToken;
    }

    public static string ScopeCacheKey(string skin)
    {
        if (string.IsNullOrEmpty(ActiveOwnershipToken)) return skin;
        return skin + "|renderer=" + ActiveOwnershipToken;
    }

    public static Texture2D? ResolveSource(Texture2D? source)
    {
        if (source == null) return source;
        Texture2D current = source;
        for (int i = 0; i < 16; i++)
        {
            if (!RuntimeRoots.TryGetValue(current.GetInstanceID(), out var root) || root == null || root == current)
                break;
            current = root;
        }
        return current;
    }

    public static Texture2D? RegisterRuntime(Texture2D? runtimeAtlas, Texture2D? source)
    {
        if (runtimeAtlas == null) return runtimeAtlas;
        Texture2D? root = ResolveSource(source);
        if (root != null && runtimeAtlas != root)
            RuntimeRoots[runtimeAtlas.GetInstanceID()] = root;
        if (RendererStates[ActiveRendererId] is RendererState state)
            state.RuntimeAtlases[runtimeAtlas.GetInstanceID()] = runtimeAtlas;
        return runtimeAtlas;
    }

    public static void BeginSkeleton(Skeleton skeleton)
    {
        ActiveRendererId = 0;
        ActiveOwnershipToken = null;
        if (skeleton == null) return;
        long skeletonId = skeleton.Pointer.ToInt64();
        SkeletonBindings.Remove(skeletonId);
        if (SkeletonOwners[skeletonId] is RendererState state)
        {
            ActiveRendererId = state.RendererId;
            ActiveOwnershipToken = state.OwnershipToken;
        }
    }

    public static Attachment EnsurePrivateRegionAttachment(Skeleton skeleton, int slotIndex,
        Attachment attachment, AtlasRegion region, Texture2D runtimeAtlas)
    {
        if (skeleton == null || attachment == null || region == null || runtimeAtlas == null)
            return attachment!;
        var original = attachment.TryCast<RegionAttachment>();
        if (original == null) return attachment;
        long skeletonId = skeleton.Pointer.ToInt64();
        if (SkeletonOwners[skeletonId] is not RendererState state || state.Skeleton?.Pointer != skeleton.Pointer)
            return attachment;

        long attachmentId = attachment.Pointer.ToInt64();
        if (state.PrivateByAnyPointer[attachmentId] is PrivateRegionResource existing)
            return existing.PrivateAttachment;
        Material? sourceMaterial = region.page?.rendererObject?.TryCast<Material>();
        if (sourceMaterial == null) return attachment;

        AtlasBinding binding = EnsureBinding(skeleton, sourceMaterial, runtimeAtlas);
        AppliedBinding applied = EnsureApplied(state, binding);
        int width = runtimeAtlas.width;
        int height = runtimeAtlas.height;
        string suffix = state.RendererId + "_" + Math.Abs(attachmentId);
        string regionName = "SkinForger_private_region_" + suffix;
        var atlasText = new TextAsset($"\n{runtimeAtlas.name}\nsize: {width}, {height}\nformat: RGBA8888\nfilter: Linear,Linear\nrepeat: none\n{regionName}\n  rotate: false\n  xy: 0,0\n  size: {width},{height}\n  orig: {width},{height}\n  offset: 0,0\n  index: -1\n");
        var materials = (Il2CppReferenceArray<Material>)new[] { applied.Material };
        var atlasAsset = SpineAtlasAsset.CreateRuntimeInstance(atlasText, materials, true);
        AtlasRegion? privateRegion = atlasAsset.GetAtlas().FindRegion(regionName);
        if (privateRegion == null)
        {
            UnityEngine.Object.Destroy(atlasAsset);
            UnityEngine.Object.Destroy(atlasText);
            return attachment;
        }

        // Preserve Spine's exact rotation contract. In this runtime `rotate`
        // is not equivalent to `degrees != 0` (for example, 180/270 degree
        // regions may have rotate=false), so copy both source values rather
        // than deriving one from the other.
        privateRegion.degrees = region.degrees;
        privateRegion.rotate = region.rotate;

        var privateAttachment = new RegionAttachment(original.Name)
        {
            x = original.x,
            y = original.y,
            scaleX = original.scaleX,
            scaleY = original.scaleY,
            rotation = original.rotation,
            width = original.width,
            height = original.height,
            r = original.r,
            g = original.g,
            b = original.b,
            a = original.a,
            Path = privateRegion.name
        };
        AttachmentRegionExtensions.SetRegion(privateAttachment, privateRegion, true);

        var resource = new PrivateRegionResource(slotIndex, attachment, privateAttachment,
            privateRegion, atlasText, atlasAsset, runtimeAtlas.GetInstanceID());
        state.PrivateByOriginal[attachmentId] = resource;
        state.PrivateByAnyPointer[attachmentId] = resource;
        state.PrivateByAnyPointer[privateAttachment.Pointer.ToInt64()] = resource;
        state.RuntimeAtlases[runtimeAtlas.GetInstanceID()] = runtimeAtlas;
        RebuildPrivateSkin(state);
        return privateAttachment;
    }

    public static AtlasRegion? GetPrivateRegion(Attachment attachment) =>
        attachment?.TryCast<IHasRendererObject>()?.RendererObject?.TryCast<AtlasRegion>();

    public static void Record(Skeleton skeleton, Material pageMaterial, Texture2D runtimeAtlas)
    {
        if (skeleton == null || pageMaterial == null || runtimeAtlas == null) return;
        _ = EnsureBinding(skeleton, pageMaterial, runtimeAtlas);
        if (SkeletonOwners[skeleton.Pointer.ToInt64()] is RendererState state)
            state.RuntimeAtlases[runtimeAtlas.GetInstanceID()] = runtimeAtlas;
    }

    public static void Bind(SkeletonRenderer renderer)
    {
        Reconcile();
        if (renderer == null || renderer.Skeleton == null) return;
        int rendererId = renderer.GetInstanceID();
        long skeletonId = renderer.Skeleton.Pointer.ToInt64();
        var state = RendererStates[rendererId] as RendererState;
        if (state == null || state.RendererPointer != renderer.Pointer.ToInt64()) return;

        var bindings = SkeletonBindings[skeletonId] as ArrayList;
        if (bindings == null || bindings.Count == 0)
        {
            CleanupState(state, true);
            RendererStates.Remove(rendererId);
            return;
        }

        state.UpdatesRemaining = 2;
        ApplyOverrides(state, bindings);
        if (state.Callback != null) return;
        SkeletonRenderer.SkeletonRendererDelegate callback = (Action<SkeletonRenderer>)OnMeshAndMaterialsUpdated;
        state.Callback = callback;
        renderer.add_OnMeshAndMaterialsUpdated(callback);
    }

    public static void Reconcile()
    {
        var stale = new ArrayList();
        foreach (DictionaryEntry entry in RendererStates)
        {
            var state = entry.Value as RendererState;
            if (state == null || !IsAlive(state)) stale.Add(entry.Key);
        }
        foreach (object key in stale)
        {
            if (RendererStates[key] is RendererState state) CleanupState(state, true);
            RendererStates.Remove(key);
        }
    }

    public static void InvalidateAll()
    {
        foreach (RendererState state in RendererStates.Values) CleanupState(state, true);
        RendererStates.Clear();
        SkeletonBindings.Clear();
        SkeletonOwners.Clear();
        RuntimeRoots.Clear();
        ActiveRendererId = 0;
        ActiveOwnershipToken = null;
    }

    public static void Shutdown() => InvalidateAll();

    private static AtlasBinding EnsureBinding(Skeleton skeleton, Material sourceMaterial, Texture2D runtimeAtlas)
    {
        long skeletonId = skeleton.Pointer.ToInt64();
        int materialId = sourceMaterial.GetInstanceID();
        var bindings = SkeletonBindings[skeletonId] as ArrayList;
        if (bindings == null)
        {
            bindings = new ArrayList();
            SkeletonBindings[skeletonId] = bindings;
        }
        foreach (AtlasBinding item in bindings)
        {
            if (item.MaterialId != materialId) continue;
            item.SourceMaterial = sourceMaterial;
            item.RuntimeAtlas = runtimeAtlas;
            return item;
        }
        var created = new AtlasBinding(materialId, sourceMaterial, runtimeAtlas);
        bindings.Add(created);
        return created;
    }

    private static AppliedBinding EnsureApplied(RendererState state, AtlasBinding binding)
    {
        Material source = binding.SourceMaterial;
        Texture2D atlas = binding.RuntimeAtlas;
        var applied = state.Applied[binding.MaterialId] as AppliedBinding;
        bool hadPrevious = state.Renderer.CustomMaterialOverride.TryGetValue(source, out var existing) && existing != null;
        Material template = hadPrevious && (applied == null || existing != applied.Material)
            ? existing!
            : applied?.Template ?? source;
        int templateId = template.GetInstanceID();
        int atlasId = atlas.GetInstanceID();
        if (applied != null && applied.TemplateId == templateId && applied.AtlasId == atlasId)
            return applied;

        if (applied?.Material != null) UnityEngine.Object.Destroy(applied.Material);
        var replacement = new Material(template)
        {
            name = template.name + " [SkinForger Private Runtime Atlas]",
            mainTexture = atlas
        };
        applied = new AppliedBinding(source, template, replacement,
            hadPrevious && existing != null && existing != replacement ? existing : null,
            templateId, atlasId);
        state.Applied[binding.MaterialId] = applied;
        return applied;
    }

    private static void ApplyOverrides(RendererState state, ArrayList bindings)
    {
        foreach (AtlasBinding binding in bindings)
        {
            if (binding.SourceMaterial == null || binding.RuntimeAtlas == null) continue;
            AppliedBinding applied = EnsureApplied(state, binding);
            state.Renderer.CustomMaterialOverride[binding.SourceMaterial] = applied.Material;
        }
    }

    private static void RebuildPrivateSkin(RendererState state)
    {
        if (state.Skeleton == null || state.SourceSkin == null) return;
        var privateSkin = new Skin("SkinForger_private_" + state.OwnershipToken);
        privateSkin.bones = state.SourceSkin.bones;
        privateSkin.constraints = state.SourceSkin.constraints;
        var entries = new Il2CppSystem.Collections.Generic.List<Skin.SkinEntry>();
        var found = new Hashtable();
        for (int slotIndex = 0; slotIndex < state.Skeleton.slots.Count; slotIndex++)
        {
            entries.Clear();
            state.SourceSkin.GetAttachments(slotIndex, entries);
            for (int i = 0; i < entries.Count; i++)
            {
                Skin.SkinEntry entry = entries[i];
                Attachment current = entry.Attachment;
                Attachment mapped = current;
                if (current != null && state.PrivateByOriginal[current.Pointer.ToInt64()] is PrivateRegionResource resource)
                {
                    mapped = resource.PrivateAttachment;
                    found[resource.OriginalPointer] = true;
                }
                privateSkin.SetAttachment(slotIndex, entry.Name, mapped);
            }
        }

        foreach (PrivateRegionResource resource in state.PrivateByOriginal.Values)
            if (!found.ContainsKey(resource.OriginalPointer))
                privateSkin.SetAttachment(resource.SlotIndex, resource.Original.Name, resource.PrivateAttachment);

        state.PrivateSkin = privateSkin;
        state.Skeleton.skin = privateSkin;
        for (int i = 0; i < state.Skeleton.slots.Count; i++)
        {
            Slot? slot = state.Skeleton.slots.Items[i];
            Attachment? current = slot?.Attachment;
            if (current == null) continue;
            if (state.PrivateByAnyPointer[current.Pointer.ToInt64()] is PrivateRegionResource resource)
                slot!.Attachment = resource.PrivateAttachment;
        }
    }

    private static void OnMeshAndMaterialsUpdated(SkeletonRenderer renderer)
    {
        int rendererId = renderer.GetInstanceID();
        var state = RendererStates[rendererId] as RendererState;
        if (state == null || state.Callback == null || state.RendererPointer != renderer.Pointer.ToInt64()) return;
        if (SkeletonBindings[state.SkeletonId] is ArrayList bindings) ApplyOverrides(state, bindings);
        state.UpdatesRemaining--;
        if (state.UpdatesRemaining <= 0) Detach(state);
    }

    private static bool IsAlive(RendererState state)
    {
        try { return state.Renderer != null && state.Renderer.Pointer != IntPtr.Zero; }
        catch { return false; }
    }

    private static void ResetForApply(RendererState state)
    {
        Detach(state);
        RestoreAttachments(state);
        DestroyPrivateRegions(state);
        RestoreOverrides(state);
    }

    private static void CleanupState(RendererState state, bool destroyRuntime)
    {
        ResetForApply(state);
        RemoveSkeletonOwner(state);
        SkeletonBindings.Remove(state.SkeletonId);
        if (!destroyRuntime) return;
        foreach (Texture2D texture in state.RuntimeAtlases.Values)
        {
            if (texture == null) continue;
            RuntimeRoots.Remove(texture.GetInstanceID());
            UnityEngine.Object.Destroy(texture);
        }
        state.RuntimeAtlases.Clear();
    }

    private static void RestoreAttachments(RendererState state)
    {
        Skeleton? skeleton = state.Skeleton;
        if (skeleton == null) return;
        if (state.PrivateSkin != null && skeleton.skin?.Pointer == state.PrivateSkin.Pointer)
            skeleton.skin = state.OriginalSkin;
        for (int i = 0; i < skeleton.slots.Count; i++)
        {
            Slot? slot = skeleton.slots.Items[i];
            Attachment? current = slot?.Attachment;
            if (current != null && state.PrivateByAnyPointer[current.Pointer.ToInt64()] is PrivateRegionResource resource &&
                current.Pointer == resource.PrivateAttachment.Pointer)
                slot!.Attachment = resource.Original;
        }
    }

    private static void DestroyPrivateRegions(RendererState state)
    {
        foreach (PrivateRegionResource resource in state.PrivateByOriginal.Values)
        {
            if (resource.AtlasAsset != null) UnityEngine.Object.Destroy(resource.AtlasAsset);
            if (resource.AtlasText != null) UnityEngine.Object.Destroy(resource.AtlasText);
        }
        state.PrivateByOriginal.Clear();
        state.PrivateByAnyPointer.Clear();
        state.PrivateSkin = null;
    }

    private static void RemoveSkeletonOwner(RendererState state)
    {
        if (state.SkeletonId != 0 && SkeletonOwners[state.SkeletonId] == state)
            SkeletonOwners.Remove(state.SkeletonId);
    }

    private static void Detach(RendererState state)
    {
        if (state.Callback == null) return;
        try { if (IsAlive(state)) state.Renderer.remove_OnMeshAndMaterialsUpdated(state.Callback); }
        catch { }
        state.Callback = null;
    }

    private static void RestoreOverrides(RendererState state)
    {
        foreach (AppliedBinding applied in state.Applied.Values)
        {
            if (applied.SourceMaterial != null && IsAlive(state) &&
                state.Renderer.CustomMaterialOverride.TryGetValue(applied.SourceMaterial, out var current) &&
                current == applied.Material)
            {
                if (applied.PreviousMaterial != null)
                    state.Renderer.CustomMaterialOverride[applied.SourceMaterial] = applied.PreviousMaterial;
                else
                    state.Renderer.CustomMaterialOverride.Remove(applied.SourceMaterial);
            }
            if (applied.Material != null) UnityEngine.Object.Destroy(applied.Material);
        }
        state.Applied.Clear();
    }
}

internal sealed class AtlasBinding
{
    public AtlasBinding(int materialId, Material sourceMaterial, Texture2D runtimeAtlas)
    {
        MaterialId = materialId;
        SourceMaterial = sourceMaterial;
        RuntimeAtlas = runtimeAtlas;
    }
    public int MaterialId { get; }
    public Material SourceMaterial { get; set; }
    public Texture2D RuntimeAtlas { get; set; }
}

internal sealed class AppliedBinding
{
    public AppliedBinding(Material sourceMaterial, Material template, Material material,
        Material? previousMaterial, int templateId, int atlasId)
    {
        SourceMaterial = sourceMaterial;
        Template = template;
        Material = material;
        PreviousMaterial = previousMaterial;
        TemplateId = templateId;
        AtlasId = atlasId;
    }
    public Material SourceMaterial { get; }
    public Material Template { get; }
    public Material Material { get; }
    public Material? PreviousMaterial { get; }
    public int TemplateId { get; }
    public int AtlasId { get; }
}

internal sealed class PrivateRegionResource
{
    public PrivateRegionResource(int slotIndex, Attachment original, RegionAttachment privateAttachment,
        AtlasRegion privateRegion, TextAsset atlasText, SpineAtlasAsset atlasAsset, int runtimeAtlasId)
    {
        SlotIndex = slotIndex;
        Original = original;
        PrivateAttachment = privateAttachment;
        PrivateRegion = privateRegion;
        AtlasText = atlasText;
        AtlasAsset = atlasAsset;
        RuntimeAtlasId = runtimeAtlasId;
        OriginalPointer = original.Pointer.ToInt64();
    }
    public int SlotIndex { get; }
    public long OriginalPointer { get; }
    public Attachment Original { get; }
    public RegionAttachment PrivateAttachment { get; }
    public AtlasRegion PrivateRegion { get; }
    public TextAsset AtlasText { get; }
    public SpineAtlasAsset AtlasAsset { get; }
    public int RuntimeAtlasId { get; }
}

internal sealed class RendererState
{
    public RendererState(SkeletonRenderer renderer, int rendererId, long rendererPointer)
    {
        Renderer = renderer;
        RendererId = rendererId;
        RendererPointer = rendererPointer;
    }
    public SkeletonRenderer Renderer { get; }
    public int RendererId { get; }
    public long RendererPointer { get; }
    public string OwnershipToken { get; set; } = string.Empty;
    public Skeleton? Skeleton { get; set; }
    public long SkeletonId { get; set; }
    public Skin? OriginalSkin { get; set; }
    public Skin? SourceSkin { get; set; }
    public Skin? PrivateSkin { get; set; }
    public int UpdatesRemaining { get; set; }
    public SkeletonRenderer.SkeletonRendererDelegate? Callback { get; set; }
    public Hashtable Applied { get; } = new();
    public Hashtable PrivateByOriginal { get; } = new();
    public Hashtable PrivateByAnyPointer { get; } = new();
    public Hashtable RuntimeAtlases { get; } = new();
}
