using HarmonyLib;
using Il2CppAssets.Scripts.ReplaceTools;
using Il2CppSpine;
using Il2CppSpine.Unity;
using MelonLoader;
using UnityEngine;
using TouhouLogicApi = Il2CppAssets.Scripts.TouhouLogic.TouhouLogic;

namespace SkinForger.TouhouCompat;

/// <summary>
/// Bad Apple (Touhou) silhouettes for SkinForger skins. Ported into Core on 2026-11-16 from TouhouCompat V2 0.3.0
/// (separate DLL, 2026-09-28, BUG-017 / BUG-018); the DLL was no longer deployed after the Mods\SkinForger
/// reorganisation, so replaced parts stayed in colour during Bad Apple (Lilith, user screenshot 2026-11-15).
///
/// The game turns characters black with SkeletonRendererCustomMaterials: each entry maps an original atlas material
/// to a "touhou_black" / BadApple-shader material, applied through SkeletonRenderer.CustomMaterialOverride.
/// SkinForger draws replaced parts from its own runtime atlas, so
///  (V2) the BadApple material sampled the original atlas with SF's UVs: black fragments. The BadApple material is
///       copied with SF's runtime atlas as _MainTex ("compat material");
///  (new) SF's runtime atlas materials are not keys of the override dictionary, so those parts kept their normal
///       material: colour. Each SF page material whose texture has the same name as an entry's original texture is
///       mapped to that entry's compat material too.
/// Hooks: ReplaceSkeletonCustomMaterials.Replace, TouhouLogic.ReplaceBadApple, SkinForgerMod.ApplySkeleton, and one
/// SkeletonRenderer.OnMeshAndMaterialsUpdated callback after Bad Apple starts (final refresh). No global Renderer
/// material setter hooks (BUG-017: StackOverflow).
/// </summary>
public sealed class TouhouCompatMod : global::SkinForger.Core.SfModule
{
    private const string Prefix = "[SkinForge][TouhouCompat]";

    private static readonly Dictionary<long, SkeletonRendererCustomMaterials> ComponentsBySkeleton = new();
    private static readonly HashSet<long> SkinForgerSkeletons = new();
    /// <summary>SF skeletons of elfins (their untouched parts keep the game's atlas and UVs).</summary>
    private static readonly HashSet<long> ElfinSkeletons = new();
    private static readonly Dictionary<string, Material> OriginalReplacementMaterials = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Material> CompatMaterials = new(StringComparer.Ordinal);
    private static readonly Dictionary<int, List<Material>> AddedOverrideKeys = new();
    private static readonly HashSet<string> Logged = new(StringComparer.Ordinal);
    private static readonly Dictionary<int, (SkeletonRenderer Renderer, SkeletonRenderer.SkeletonRendererDelegate Callback)> Pending = new();

    public override void OnInitializeMelon()
    {
        int patched = 0;
        patched += Patch(typeof(ReplaceSkeletonCustomMaterials), "Replace", nameof(AfterReplace));
        patched += Patch(typeof(TouhouLogicApi), "ReplaceBadApple", nameof(AfterReplaceBadApple));
        Type? host = AccessTools.TypeByName("SkinForger.SkinForgerMod");
        if (host != null) patched += Patch(host, "ApplySkeleton", nameof(AfterApplySkeleton));
        MelonLogger.Msg($"{Prefix} ready version=0.4.5 (Core port of V2 0.3.0 + SF runtime material mapping) patches={patched}/3");
    }

    public override void OnDeinitializeMelon()
    {
        foreach (var pair in Pending)
            try { pair.Value.Renderer?.remove_OnMeshAndMaterialsUpdated(pair.Value.Callback); } catch { }
        Pending.Clear();
    }

    private int Patch(Type type, string method, string postfix)
    {
        try
        {
            var target = AccessTools.Method(type, method);
            if (target == null) { MelonLogger.Warning($"{Prefix} {type.Name}.{method} not found"); return 0; }
            HarmonyInstance.Patch(target, postfix: new HarmonyMethod(typeof(TouhouCompatMod), postfix));
            return 1;
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"{Prefix} {type.Name}.{method} not patched: {exception.GetBaseException().Message}");
            return 0;
        }
    }

    // ------------------------------------------------------------ hooks

    private static void AfterReplace(ReplaceSkeletonCustomMaterials __instance, int index)
    {
        try
        {
            var custom = __instance?.skeletonCustomMaterials;
            if (custom == null) return;
            long key = Register(custom);
            if (!__instance!.enable) { Restore(custom); return; }
            if (key != 0 && SkinForgerSkeletons.Contains(key)) Sync(custom, $"Replace({index})");
        }
        catch (Exception exception) { Warn("replace", exception); }
    }

    private static void AfterReplaceBadApple(GameObject go)
    {
        if (go == null) return;
        try
        {
            foreach (SkeletonRendererCustomMaterials custom in go.GetComponentsInChildren<SkeletonRendererCustomMaterials>(true))
            {
                if (custom == null) continue;
                long key = Register(custom);
                bool ours = key != 0 && SkinForgerSkeletons.Contains(key);
                if (Logged.Add("badapple:" + PathOf(custom.gameObject)))
                    MelonLogger.Msg($"{Prefix} Bad Apple on {PathOf(custom.gameObject)} skinForger={ours} overrides={custom.customMaterialOverrides?.Count ?? 0}");
                if (!ours) continue;
                Sync(custom, "ReplaceBadApple");
                ArmFinalSync(custom);
            }
        }
        catch (Exception exception) { Warn("bad apple", exception); }
    }

    /// <summary>SkinForgerMod.ApplySkeleton(skeleton, index, selected, elfin): SF just applied a skin to this skeleton.</summary>
    private static void AfterApplySkeleton(Skeleton skeleton, int selected, bool elfin)
    {
        if (selected < 0 || skeleton == null) return;
        long key = skeleton.Pointer.ToInt64();
        SkinForgerSkeletons.Add(key);
        if (elfin) ElfinSkeletons.Add(key); else ElfinSkeletons.Remove(key);
        if (ComponentsBySkeleton.TryGetValue(key, out var custom) && custom != null) Sync(custom, "ApplySkeleton");
    }

    private static long Register(SkeletonRendererCustomMaterials custom)
    {
        try
        {
            Skeleton? skeleton = custom.skeletonRenderer?.Skeleton;
            if (skeleton == null) return 0;
            long key = skeleton.Pointer.ToInt64();
            ComponentsBySkeleton[key] = custom;
            return key;
        }
        catch { return 0; }
    }

    // ------------------------------------------------------------ sync

    private static void Sync(SkeletonRendererCustomMaterials custom, string reason)
    {
        try
        {
            SkeletonRenderer? renderer = custom.skeletonRenderer;
            Skeleton? skeleton = renderer?.Skeleton;
            var overrides = custom.customMaterialOverrides;
            if (renderer == null || skeleton == null || overrides == null || overrides.Count == 0) return;
            int componentId = custom.GetInstanceID();
            List<Material> pages = PageMaterials(skeleton);

            // (V2) entries whose BadApple material should sample SF's runtime atlas.
            int changed = 0;
            var mappings = new List<(Material Source, string Texture)>();
            for (int i = 0; i < overrides.Count; i++)
            {
                var entry = overrides[i];
                if (entry == null || entry.overrideDisabled || entry.replacementMaterial == null) continue;
                string entryKey = componentId + "|" + i;
                if (!OriginalReplacementMaterials.TryGetValue(entryKey, out Material? source) || source == null)
                    OriginalReplacementMaterials[entryKey] = source = entry.replacementMaterial;
                if (!IsBadApple(source)) continue;
                string originalTexture = entry.originalMaterial?.mainTexture?.name ?? source.mainTexture?.name ?? string.Empty;
                mappings.Add((source, originalTexture));
                // Which atlas the parts on the ORIGINAL material read depends on the kind (2026-11-16, logs 11-36 / 11-48):
                //  - characters: SF rebases every region (UntouchedRegionRebase) onto its runtime atlas and redirects
                //    the original material there; BadApple must read SF's atlas too, else parts vanish (Maid
                //    death / jump attack: BadApple on 2048x512 with UVs for 2048x7675);
                //  - elfins: untouched parts keep the game's atlas and UVs; BadApple stays on the game's atlas, else
                //    parts go missing / appear on the head (Lilith).
                bool elfin = ElfinSkeletons.Contains(skeleton.Pointer.ToInt64());
                Material? runtime = elfin ? null : pages.FirstOrDefault(pg => TextureName(pg) == originalTexture && pg.mainTexture != null && !SameObject(pg.mainTexture, source.mainTexture));
                Material wanted = runtime?.mainTexture != null ? Compat(source, runtime.mainTexture) : source;
                if (!SameObject(entry.replacementMaterial, wanted)) { entry.replacementMaterial = wanted; changed++; }
            }
            if (changed > 0)
            {
                custom.SetCustomMats(false);
                custom.SetCustomMats(true);
            }

            // (new) SF's own page materials are keys of the override dictionary as well.
            int added = 0;
            int rendererId = renderer.GetInstanceID();
            if (!AddedOverrideKeys.TryGetValue(rendererId, out List<Material>? keys)) AddedOverrideKeys[rendererId] = keys = new List<Material>();
            foreach (Material page in pages)
            {
                (Material Source, string Texture) mapping = mappings.FirstOrDefault(m => m.Texture.Length > 0 && m.Texture == TextureName(page));
                if (mapping.Source == null || page.mainTexture == null || IsBadApple(page)) continue;
                if (SameObject(page.mainTexture, mapping.Source.mainTexture)) continue;   // the game's own atlas: the entry handles it
                Material compat = Compat(mapping.Source, page.mainTexture);
                var dictionary = renderer.CustomMaterialOverride;
                if (dictionary == null) continue;
                if (dictionary.ContainsKey(page) && SameObject(dictionary[page], compat)) continue;
                dictionary[page] = compat;
                if (!keys.Any(k => SameObject(k, page))) keys.Add(page);
                added++;
            }
            if (changed + added > 0 && Logged.Add($"sync:{componentId}:{changed}:{added}"))
                MelonLogger.Msg($"{Prefix} {reason}: {PathOf(custom.gameObject)} entries={changed} SF materials mapped={added} " +
                                $"pages=[{string.Join(", ", pages.Select(p => $"{p.name}:{TextureName(p)}"))}] badApple=[{string.Join(", ", mappings.Select(m => m.Texture))}]");
        }
        catch (Exception exception) { Warn("sync " + reason, exception); }
    }

    private static void Restore(SkeletonRendererCustomMaterials custom)
    {
        try
        {
            var overrides = custom.customMaterialOverrides;
            int componentId = custom.GetInstanceID();
            for (int i = 0; overrides != null && i < overrides.Count; i++)
                if (overrides[i] != null && OriginalReplacementMaterials.TryGetValue(componentId + "|" + i, out Material? original) && original != null)
                    overrides[i].replacementMaterial = original;
            SkeletonRenderer? renderer = custom.skeletonRenderer;
            if (renderer != null && Pending.Remove(renderer.GetInstanceID(), out var watch))
                try { renderer.remove_OnMeshAndMaterialsUpdated(watch.Callback); } catch { }
            if (renderer != null && AddedOverrideKeys.Remove(renderer.GetInstanceID(), out List<Material>? keys))
                foreach (Material key in keys)
                    try { renderer.CustomMaterialOverride?.Remove(key); } catch { }
        }
        catch (Exception exception) { Warn("restore", exception); }
    }

    /// <summary>
    /// While Bad Apple runs, every mesh / material refresh of this renderer is checked (2026-11-16: Maid parts vanished
    /// during some actions only). A page material not seen before is mapped at once (Sync), and each new set of final
    /// materials is logged once with shader and textures, so a part that still goes wrong can be traced.
    /// Detached when Bad Apple ends (Restore) or the renderer is gone.
    /// </summary>
    private static void ArmFinalSync(SkeletonRendererCustomMaterials custom)
    {
        SkeletonRenderer? renderer = custom.skeletonRenderer;
        if (renderer == null) return;
        int id = renderer.GetInstanceID();
        if (Pending.ContainsKey(id)) return;
        SkeletonRenderer.SkeletonRendererDelegate callback = null!;
        int pageCount = -1;
        callback = (Action<SkeletonRenderer>)(updated =>
        {
            try
            {
                if (renderer == null || renderer.Pointer == IntPtr.Zero || custom == null || custom.Pointer == IntPtr.Zero)
                {
                    Pending.Remove(id);
                    return;
                }
                Skeleton? skeleton = renderer.Skeleton;
                int pages = skeleton == null ? 0 : PageMaterials(skeleton).Count;
                if (pages != pageCount) { pageCount = pages; Sync(custom, "refresh"); }
                // 2026-11-16 (log 11-55): even with the entry changed, the final slots kept getting the game's BadApple
                // material on the game's small atlas for characters. Correct the final slots on every refresh.
                if (skeleton != null && !ElfinSkeletons.Contains(skeleton.Pointer.ToInt64())) FixFinalSlots(renderer);
                LogFinalMaterials(renderer);
            }
            catch (Exception exception) { Warn("refresh", exception); }
        });
        Pending[id] = (renderer, callback);
        renderer.add_OnMeshAndMaterialsUpdated(callback);
    }

    private static readonly HashSet<string> LoggedMaterialSets = new(StringComparer.Ordinal);
    /// <summary>"rendererId|texture name" -> SF's runtime atlas of that renderer (seen once, reused in every pose).</summary>
    private static readonly Dictionary<string, Texture> RuntimeAtlases = new(StringComparer.Ordinal);
    private static int _slotFixLogs;

    private static void LogFinalMaterials(SkeletonRenderer renderer)
    {
        if (LoggedMaterialSets.Count > 40) return;
        var materials = renderer.meshRenderer?.sharedMaterials;
        if (materials == null) return;
        string signature = renderer.GetInstanceID() + ":" + string.Join(",", materials.Select(m => m == null ? 0 : m.GetInstanceID()));
        if (!LoggedMaterialSets.Add(signature)) return;
        MelonLogger.Msg($"{Prefix} materials of {PathOf(renderer.gameObject)}: " + string.Join(" | ", materials.Select(Describe)));
    }

    private static string Describe(Material? material)
    {
        if (material == null) return "<null>";
        try
        {
            var textures = new List<string>();
            foreach (string property in material.GetTexturePropertyNames())
            {
                Texture? texture = material.GetTexture(property);
                if (texture != null) textures.Add($"{property}={texture.name}({texture.width}x{texture.height})");
            }
            return $"{material.name} shader={material.shader?.name} badApple={IsBadApple(material)} [{string.Join(", ", textures)}]";
        }
        catch { return material.name ?? "?"; }
    }

    /// <summary>(V2) final MeshRenderer slots that still hold a BadApple material on the original atlas.</summary>
    private static void FixFinalSlots(SkeletonRenderer renderer)
    {
        try
        {
            Skeleton? skeleton = renderer.Skeleton;
            var meshRenderer = renderer.meshRenderer;
            var materials = meshRenderer == null ? null : meshRenderer.sharedMaterials;
            if (skeleton == null || materials == null) return;
            List<Material> pages = PageMaterials(skeleton);
            int changed = 0;
            for (int i = 0; i < materials.Length; i++)
            {
                Material material = materials[i];
                if (material == null || !IsBadApple(material)) continue;
                // SF's own page (its runtime atlas): the one whose material name carries the SkinForger marker.
                // 2026-11-16 (log 11-59): in the poses that lost parts no shown attachment used SF's page material, so the
                // atlas was not found and the fix ran only once. SF's atlas is remembered per renderer and texture name.
                Material? runtime = pages.FirstOrDefault(p => TextureName(p) == TextureName(material) &&
                                                              (p.name ?? string.Empty).IndexOf("SkinForger", StringComparison.OrdinalIgnoreCase) >= 0);
                string atlasKey = renderer.GetInstanceID() + "|" + TextureName(material);
                if (runtime?.mainTexture != null) RuntimeAtlases[atlasKey] = runtime.mainTexture;
                if (!RuntimeAtlases.TryGetValue(atlasKey, out Texture? atlas) || atlas == null || atlas.Pointer == IntPtr.Zero) continue;
                if (SameObject(atlas, material.mainTexture)) continue;   // already on SF's atlas
                materials[i] = Compat(material, atlas);
                changed++;
            }
            if (changed > 0)
            {
                meshRenderer!.sharedMaterials = materials;
                if (_slotFixLogs++ < 12) MelonLogger.Msg($"{Prefix} final slots of {PathOf(renderer.gameObject)}: {changed} BadApple slot(s) moved to SkinForger's atlas");
            }
        }
        catch (Exception exception) { Warn("final slots", exception); }
    }

    // ------------------------------------------------------------ helpers

    /// <summary>Distinct page materials of the attachments the skeleton shows (SF's runtime atlas materials).</summary>
    private static List<Material> PageMaterials(Skeleton skeleton)
    {
        var result = new List<Material>();
        var slots = skeleton.Slots;
        for (int i = 0; slots != null && i < slots.Count; i++)
        {
            Attachment? attachment = slots.Items[i]?.Attachment;
            Material? material = attachment?.TryCast<IHasRendererObject>()?.RendererObject?.TryCast<AtlasRegion>()?.page?.rendererObject?.TryCast<Material>();
            if (material != null && !result.Any(m => SameObject(m, material))) result.Add(material);
        }
        return result;
    }

    private static Material Compat(Material source, Texture texture)
    {
        string key = source.GetInstanceID() + "|" + texture.GetInstanceID();
        if (CompatMaterials.TryGetValue(key, out Material? cached) && cached != null && cached.Pointer != IntPtr.Zero) return cached;
        var material = new Material(source) { name = source.name + "_SkinForgerRuntime", mainTexture = texture, renderQueue = source.renderQueue };
        // Any other texture slot that held the original atlas (e.g. a separate alpha / mask lookup) follows too.
        try
        {
            Texture? original = source.mainTexture;
            foreach (string property in source.GetTexturePropertyNames())
                if (original != null && source.GetTexture(property) is { } t && t.GetInstanceID() == original.GetInstanceID())
                    material.SetTexture(property, texture);
            MelonLogger.Msg($"{Prefix} BadApple copy for {texture.name}({texture.width}x{texture.height}): {Describe(material)}");
        }
        catch { }
        CompatMaterials[key] = material;
        return material;
    }

    private static bool IsBadApple(Material material)
    {
        try
        {
            return (material.name ?? string.Empty).IndexOf("touhou_black", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (material.shader?.name ?? string.Empty).IndexOf("BadApple", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { return false; }
    }

    private static string TextureName(Material? material)
    {
        try { return material?.mainTexture?.name ?? string.Empty; } catch { return string.Empty; }
    }

    private static bool SameObject(UnityEngine.Object? left, UnityEngine.Object? right) =>
        left != null && right != null && left.GetInstanceID() == right.GetInstanceID();

    private static string PathOf(GameObject? go)
    {
        var names = new List<string>();
        for (Transform? t = go?.transform; t != null && names.Count < 8; t = t.parent) names.Add(t.name);
        names.Reverse();
        return string.Join("/", names);
    }

    private static void Warn(string what, Exception exception)
    {
        if (Logged.Add("warn:" + what)) MelonLogger.Warning($"{Prefix} {what} failed: {exception.GetBaseException().Message}");
    }
}
