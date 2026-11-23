using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using MelonLoader;

namespace SkinForger.HexieBlock;

/// <summary>
/// SkinForger supports only the Steam release of Muse Dash. Censored ("hexie" / å’Œè°) resources exist for the
/// Chinese mobile release; SkinForger never dumps, reads or replaces them, even when matching files are on disk.
///
/// A resource is hexie when
///  - any of its names (atlas / texture / attachment / region / page / skeleton / prefab / file path) contains "hexie"
///    (the game's own convention: body_1_hexie, elfin_devil_hexie, thumbHexie ...), or
///  - it is a prefab used only by a censored RoleSkin (skins[].hexie == true) or by a *Hexie config field,
///    read from the game's own character / elfin config.
/// </summary>
public static class HexiePolicy
{
    private const string Prefix = "[SkinForge][HexieBlock]";
    private static readonly HashSet<string> ConfigResources = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Logged = new(StringComparer.OrdinalIgnoreCase);
    private static bool _configLoaded;
    private static int _configAttempts;
    internal static int Blocked;

    public static bool IsHexieName(string? value) =>
        !string.IsNullOrEmpty(value) && value.IndexOf("hexie", StringComparison.OrdinalIgnoreCase) >= 0;

    public static bool IsHexieResource(string? name)
    {
        if (IsHexieName(name)) return true;
        if (string.IsNullOrEmpty(name)) return false;
        EnsureConfig();
        return ConfigResources.Contains(Normalize(name));
    }


    /// <summary>
    /// Attachments that appear only in a censored Spine skin of this skeleton (a skin whose name contains "hexie",
    /// e.g. Zombie Girl battle: skins "origin" / "hexie"). Their own names need not contain "hexie".
    /// Attachments shared with any normal skin or the default skin are not included.
    /// </summary>
    public static HashSet<IntPtr> HexieOnlyAttachments(Il2CppSpine.SkeletonData? data)
    {
        if (data == null || data.Pointer == IntPtr.Zero) return new HashSet<IntPtr>();
        // Not cached: Preview clones SkeletonData and a destroyed clone's address can be reused (R4-10).
        var hexie = new HashSet<IntPtr>();
        var normal = new HashSet<IntPtr>();
        try
        {
            int slotCount = data.slots?.Count ?? 0;
            var entries = new Il2CppSystem.Collections.Generic.List<Il2CppSpine.Skin.SkinEntry>();
            var skins = data.skins;
            for (int s = 0; s < (skins?.Count ?? 0); s++)
            {
                Il2CppSpine.Skin? skin = skins!.Items[s];
                if (skin == null) continue;
                bool censored = IsHexieName(skin.name);
                for (int slot = 0; slot < slotCount; slot++)
                {
                    entries.Clear();
                    skin.GetAttachments(slot, entries);
                    for (int e = 0; e < entries.Count; e++)
                    {
                        Il2CppSpine.Attachment? attachment = entries[e]?.Attachment;
                        if (attachment == null) continue;
                        (censored ? hexie : normal).Add(attachment.Pointer);
                    }
                }
            }
            hexie.ExceptWith(normal);
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"{Prefix} skin scan failed: {exception.GetBaseException().Message}");
        }
        if (hexie.Count > 0 && Logged.Add("skinscan|" + data.name + "|" + hexie.Count))
            MelonLogger.Msg($"{Prefix} skeleton {data.name}: {hexie.Count} attachments belong only to censored skins and are excluded");
        return hexie;
    }

    internal static void Block(string where, string what)
    {
        Blocked++;
        if (Logged.Add(where + "|" + what) && Logged.Count <= 200)
            MelonLogger.Msg($"{Prefix} blocked {where}: {what}");
    }

    private static string Normalize(string value)
    {
        string name = value.Replace('\\', '/');
        int slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];
        if (name.EndsWith("(Clone)", StringComparison.Ordinal)) name = name[..^7];
        return name.Trim();
    }

    internal static void EnsureConfig()
    {
        if (_configLoaded || _configAttempts >= 20) return;
        _configAttempts++;
        try
        {

            var hexie = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var normal = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int tables = 0;
            foreach (string table in new[] { "character", "elfin" })
            {
                string? json = global::SkinForger.GameConfigJson.Text(table);   // typed: reflection ToString gave the type name
                if (string.IsNullOrEmpty(json)) continue;
                using JsonDocument document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Array) continue;
                tables++;
                foreach (JsonElement entry in document.RootElement.EnumerateArray())
                    Collect(entry, censored: false, hexie, normal);
            }
            if (tables == 0) return;

            foreach (string name in hexie)
                if (!normal.Contains(name)) ConfigResources.Add(name);
            _configLoaded = true;
            MelonLogger.Msg($"{Prefix} config resources: censoredOnly={ConfigResources.Count} " +
                            $"sample=[{string.Join(", ", ConfigResources.Take(12))}]");
        }
        catch (Exception exception)
        {
            if (_configAttempts == 1) MelonLogger.Warning($"{Prefix} config scan failed: {exception.GetBaseException().Message}");
        }
    }

    /// <summary>Strings of censored skins and of *hexie* keys go to <paramref name="hexie"/>, everything else to <paramref name="normal"/>.</summary>
    private static void Collect(JsonElement element, bool censored, HashSet<string> hexie, HashSet<string> normal)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                bool objectCensored = censored ||
                    (element.TryGetProperty("hexie", out JsonElement flag) && flag.ValueKind == JsonValueKind.True);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    bool keyCensored = objectCensored || IsHexieName(property.Name);
                    Collect(property.Value, keyCensored, hexie, normal);
                }
                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray()) Collect(item, censored, hexie, normal);
                break;
            case JsonValueKind.String:
                string? value = element.GetString();
                if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Contains(' ')) return;
                (censored ? hexie : normal).Add(Normalize(value));
                break;
        }
    }
}

public sealed class HexieBlockMod : global::SkinForger.Core.SfModule
{
    private const string Prefix = "[SkinForge][HexieBlock]";

    public override void OnInitializeMelon()
    {
        Type host = AccessTools.TypeByName("SkinForger.SkinForgerMod") ?? throw new TypeLoadException("SkinForger.SkinForgerMod");
        Type thumbs = AccessTools.TypeByName("SkinForger.SelectionThumbnail.SelectionThumbnailManager")
                      ?? throw new TypeLoadException("SelectionThumbnailManager");
        int patched = 0;

        // Reading / replacement: a hexie region never has a PNG, a hexie atlas is never rebuilt.
        patched += Hook(host, "GetExistingRegionPath", nameof(RegionPathPrefix));
        patched += Hook(host, "GetTexture", nameof(GetTexturePrefix));
        patched += Hook(host, "ApplyRegionPngToAtlas", nameof(ApplyRegionPrefix));
        patched += Hook(host, "TryBuildSynthesizedAtlasForSkeleton", nameof(BuildAtlasPrefix));
        patched += Hook(host, "TryFindSelectedSkinByAtlas", nameof(FindSkinByAtlasPrefix));
        patched += Hook(host, "ApplySkeleton", nameof(RememberSkeletonPrefix));
        Type? preview = AccessTools.TypeByName("SkinForger.Preview.PreviewManager");
        if (preview != null) patched += Hook(preview, "ApplyCurrentReplacement", nameof(RememberSkeletonPrefix));
        // Preview Shared Core (Runtime library) collects its expected PNGs before any Main build runs.
        Type? sharedCore = AccessTools.TypeByName("SkinForger.Preview.PreviewSharedCore");
        if (sharedCore != null) patched += Hook(sharedCore, "Apply", nameof(RememberSkeletonPrefix));
        MethodInfo? enumerate = AccessTools.DeclaredMethod(host, "EnumerateAttachmentContexts");
        if (enumerate != null)
        {
            HarmonyInstance.Patch(enumerate, postfix: new HarmonyMethod(typeof(HexieBlockMod), nameof(EnumerateContextsPostfix)) { priority = HarmonyLib.Priority.First });
            patched++;
        }
        else MelonLogger.Warning($"{Prefix} target missing: EnumerateAttachmentContexts");

        // Dump: hexie prefabs, skeletons and attachments are never exported.
        patched += Hook(host, "TrySpawnForExtract", nameof(SpawnForExtractPrefix));
        patched += Hook(host, "ExtractSkeleton", nameof(ExtractSkeletonPrefix));
        patched += Hook(host, "ExtractAttachment", nameof(ExtractAttachmentPrefix));

        // Selection thumbnails: no "Dump Hexie" button, no hexie dump, no hexie replacement.
        patched += Hook(thumbs, "HasHexieOriginal", nameof(FalsePrefix));
        patched += Hook(thumbs, "Dump", nameof(ThumbnailHexiePrefix));
        patched += Hook(thumbs, "CaptureAndApply", nameof(ThumbnailHexiePrefix));
        patched += Hook(thumbs, "ReplacementPath", nameof(ThumbnailPathPrefix));

        MelonLogger.Msg($"{Prefix} ready version=1.2.0 patches={patched}/16: hexie resources are never dumped, read or replaced (Steam release only)");
    }

    private int Hook(Type type, string name, string prefix)
    {
        MethodInfo? target = AccessTools.DeclaredMethod(type, name);
        if (target == null)
        {
            MelonLogger.Warning($"{Prefix} target missing: {type.Name}.{name}");
            return 0;
        }
        HarmonyInstance.Patch(target, prefix: new HarmonyMethod(typeof(HexieBlockMod), prefix) { priority = HarmonyLib.Priority.First });
        return 1;
    }

    private static bool IsHexie(Il2CppSpine.Attachment? attachment) =>
        attachment != null && (HexiePolicy.IsHexieName(attachment.Name) || CensoredSkinAttachments.Contains(attachment.Pointer));

    internal static bool IsCensored(Il2CppSpine.Attachment? attachment, Il2CppSpine.AtlasRegion? region)
    {
        if (IsHexie(attachment) || IsHexie(region)) return true;
        if (attachment == null || region != null) return false;
        try
        {
            var own = attachment.TryCast<Il2CppSpine.RegionAttachment>()?.RendererObject?.TryCast<Il2CppSpine.AtlasRegion>()
                      ?? attachment.TryCast<Il2CppSpine.MeshAttachment>()?.RendererObject?.TryCast<Il2CppSpine.AtlasRegion>();
            return IsHexie(own);
        }
        catch { return false; }
    }

    /// <summary>Learns a skeleton's censored-skin attachments before Main or Preview looks up its PNGs.</summary>
    private static void RememberSkeletonPrefix(Il2CppSpine.Skeleton skeleton)
    {
        try { RememberCensored(skeleton?.Data); } catch { }
    }

    private static bool IsHexie(Il2CppSpine.AtlasRegion? region) =>
        region != null && (HexiePolicy.IsHexieName(region.name) || HexiePolicy.IsHexieName(region.page?.name));

    private static bool RegionPathPrefix(Il2CppSpine.Attachment attachment, Il2CppSpine.AtlasRegion region, string textureName, ref string? __result)
    {
        if (!HexiePolicy.IsHexieName(textureName) && !IsHexie(attachment) && !IsHexie(region)) return true;
        HexiePolicy.Block("read", textureName + "/" + attachment?.Name);
        __result = null;
        return false;
    }

    private static bool GetTexturePrefix(string path, string textureName, ref UnityEngine.Texture2D? __result)
    {
        if (!HexiePolicy.IsHexieName(path) && !HexiePolicy.IsHexieName(textureName)) return true;
        HexiePolicy.Block("png", Path.GetFileName(path ?? textureName ?? string.Empty));
        __result = null;
        return false;
    }

    private static bool ApplyRegionPrefix(Il2CppSpine.Attachment attachment, Il2CppSpine.AtlasRegion region, string textureName, string regionPath, ref bool __result)
    {
        if (!HexiePolicy.IsHexieName(textureName) && !HexiePolicy.IsHexieName(regionPath) && !IsHexie(attachment) && !IsHexie(region)) return true;
        HexiePolicy.Block("replace", textureName + "/" + attachment?.Name);
        __result = false;
        return false;
    }

    private static bool BuildAtlasPrefix(Il2CppSpine.Skeleton skeleton, string textureName, ref UnityEngine.Texture2D? __result)
    {
        string? data = null;
        try { data = skeleton?.Data?.name; RememberCensored(skeleton?.Data); } catch { }
        if (!HexiePolicy.IsHexieName(textureName) && !HexiePolicy.IsHexieResource(data)) return true;
        HexiePolicy.Block("atlas", textureName);
        __result = null;
        return false;
    }

    private static bool FindSkinByAtlasPrefix(string atlasName, ref bool __result)
    {
        if (!HexiePolicy.IsHexieName(atlasName)) return true;
        HexiePolicy.Block("atlas-lookup", atlasName);
        __result = false;
        return false;
    }

    private static bool SpawnForExtractPrefix(string resourceName)
    {
        if (!HexiePolicy.IsHexieResource(resourceName)) return true;
        HexiePolicy.Block("dump-prefab", resourceName);
        return false;
    }

    // Attachments of censored Spine skins seen so far. Dump may export them frames after ExtractSkeleton ran.
    private static readonly HashSet<IntPtr> CensoredSkinAttachments = new();

    private static void RememberCensored(Il2CppSpine.SkeletonData? data)
    {
        if (CensoredSkinAttachments.Count > 50000) CensoredSkinAttachments.Clear();
        CensoredSkinAttachments.UnionWith(HexiePolicy.HexieOnlyAttachments(data));
    }

    private static bool ExtractSkeletonPrefix(Il2CppSpine.Skeleton skeleton)
    {
        string? data = null;
        try { data = skeleton?.Data?.name; } catch { }
        if (!HexiePolicy.IsHexieResource(data))
        {
            try { RememberCensored(skeleton?.Data); } catch { }
            return true;
        }
        HexiePolicy.Block("dump-skeleton", data ?? "?");
        return false;
    }

    private static PropertyInfo? _contextAttachment, _contextRegion;

    /// <summary>Removes censored attachments from the list every replacement / Preview build works on.</summary>
    private static void EnumerateContextsPostfix(Il2CppSpine.Skeleton skeleton, object __result)
    {
        if (__result is not System.Collections.IList list || list.Count == 0) return;
        try
        {
            HashSet<IntPtr> censored = HexiePolicy.HexieOnlyAttachments(skeleton?.Data);
            CensoredSkinAttachments.UnionWith(censored);
            Type contextType = list[0]!.GetType();
            _contextAttachment ??= contextType.GetProperty("Attachment");
            _contextRegion ??= contextType.GetProperty("Region");
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var attachment = _contextAttachment?.GetValue(list[i]) as Il2CppSpine.Attachment;
                var region = _contextRegion?.GetValue(list[i]) as Il2CppSpine.AtlasRegion;
                if (attachment == null) continue;
                if (censored.Contains(attachment.Pointer) || IsHexie(attachment) || IsHexie(region))
                {
                    HexiePolicy.Block("context", attachment.Name);
                    list.RemoveAt(i);
                }
            }
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"{Prefix} context filter failed: {exception.GetBaseException().Message}");
        }
    }

    private static bool ExtractAttachmentPrefix(Il2CppSpine.Attachment attachment)
    {
        if (attachment == null) return true;
        bool hexie = IsHexie(attachment) || CensoredSkinAttachments.Contains(attachment.Pointer);
        if (!hexie)
        {
            try
            {
                var region = attachment.TryCast<Il2CppSpine.RegionAttachment>()?.RendererObject?.TryCast<Il2CppSpine.AtlasRegion>()
                             ?? attachment.TryCast<Il2CppSpine.MeshAttachment>()?.RendererObject?.TryCast<Il2CppSpine.AtlasRegion>();
                hexie = IsHexie(region);
            }
            catch { }
        }
        if (!hexie) return true;
        HexiePolicy.Block("dump-attachment", attachment.Name);
        return false;
    }

    private static bool FalsePrefix(ref bool __result)
    {
        __result = false;
        return false;
    }

    private static bool ThumbnailHexiePrefix(bool hexie)
    {
        if (!hexie) return true;
        HexiePolicy.Block("thumbnail", "hexie variant");
        return false;
    }

    private static bool ThumbnailPathPrefix(bool hexie, ref string __result)
    {
        if (!hexie) return true;
        __result = string.Empty;
        return false;
    }
}
