using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Il2CppAssets.Scripts.Database;
using Il2CppAssets.Scripts.UI.Panels.PnlRole;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using SkinForger.Main.Pure;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SkinForger.SelectionThumbnail;

public static class SelectionThumbnailManager
{
    private const string Prefix = "[SkinForge][SelectionThumbnail]";
    private const string NormalFile = "SelectionThumbnail.png";
    private const string HexieFile = "SelectionThumbnail_hexie.png";
    private static readonly Dictionary<string, ThumbnailCacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, ThumbnailImageBinding> Bindings = new();
    private static readonly Dictionary<string, ThumbnailOriginal> Originals = new(StringComparer.OrdinalIgnoreCase);
    private static Type? _host;
    private static HarmonyLib.Harmony? _harmony;
    private static bool _initialized;
    private static bool _characterSprites = true;
    private static bool _elfinSprites = true;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            _host = typeof(SelectionThumbnailManager).Assembly.GetType("SkinForger.SkinForgerMod", true);
            _harmony = new HarmonyLib.Harmony("skinforger.selection-thumbnail");
            Patch(typeof(PnlRoleNavigationItem), "ShowByNavigationInfo", nameof(AfterCharacterItem));
            Patch(typeof(Il2Cpp.ElfinItem), "Refresh", nameof(AfterElfinItem));
            Patch(typeof(Il2Cpp.ElfinItem), "UpdateContent", nameof(AfterElfinUpdate));
            Patch(typeof(Il2Cpp.LevelConfigItem), "Refresh", nameof(AfterLevelConfig));
            Patch(typeof(Il2Cpp.LevelConfigItem), "UpdateContent", nameof(AfterLevelConfigUpdate));
            Log("initialized: Character char_thumb and Elfin thumb adapters; navigation and level-config UI postfixes only");
        }
        catch (Exception ex)
        {
            Warning("initialization skipped: " + ex.Message);
        }
    }

    private static void Patch(Type type, string name, string postfixName)
    {
        MethodInfo? target = AccessTools.Method(type, name);
        MethodInfo? postfix = AccessTools.Method(typeof(SelectionThumbnailManager), postfixName);
        if (target == null || postfix == null) throw new MissingMethodException(type.FullName, name);
        _harmony!.Patch(target, postfix: new HarmonyMethod(postfix));
    }

    public static bool IsSelectionThumbnailPath(string path)
    {
        string name = Path.GetFileName(path ?? string.Empty);
        return name.Equals(NormalFile, StringComparison.OrdinalIgnoreCase) ||
               name.Equals(HexieFile, StringComparison.OrdinalIgnoreCase);
    }

    public static void AfterCharacterItem(PnlRoleNavigationItem __instance, PnlRoleNavigationView.NavigationInfo __0)
    {
        try
        {
            if (__instance == null || __0 == null || __instance.m_ImgRole == null) return;
            // NavigationInfo.characterIndex is the Character config/listIndex. The
            // expected Addressables key below guards this game-side mapping.
            int listIndex = __0.characterIndex;
            string key = __instance.m_ImgRole.sprite != null ? __instance.m_ImgRole.sprite.name : string.Empty;
            bool hexie = IsHexie(key);
            CaptureAndApply(__instance.m_ImgRole, listIndex, false, hexie, key);
        }
        catch (Exception ex) { Warning("Character UI apply failed: " + ex.Message); }
    }

    public static void AfterElfinItem(Il2Cpp.ElfinItem __instance)
    {
        CaptureElfin(__instance);
    }

    public static void AfterElfinUpdate(Il2Cpp.ElfinItem __instance, Il2Cpp.ElfinItemData __0)
    {
        CaptureElfin(__instance);
    }

    private static void CaptureElfin(Il2Cpp.ElfinItem instance)
    {
        try
        {
            if (instance == null || instance.m_ImgElfin == null || instance.m_ItemData?.elfinInfo == null) return;
            var info = instance.m_ItemData.elfinInfo;
            string key = info.GetThumbImgName() ?? string.Empty;
            CaptureAndApply(instance.m_ImgElfin, info.listIndex, true, IsHexie(key), key);
        }
        catch (Exception ex) { Warning("Elfin UI apply failed: " + ex.Message); }
    }

    public static void AfterLevelConfig(Il2Cpp.LevelConfigItem __instance)
    {
        CaptureLevelConfig(__instance);
    }

    public static void AfterLevelConfigUpdate(Il2Cpp.LevelConfigItem __instance, Il2Cpp.LevelConfigItemData __0)
    {
        CaptureLevelConfig(__instance);
    }

    private static void CaptureLevelConfig(Il2Cpp.LevelConfigItem instance)
    {
        try
        {
            if (instance == null || instance.m_ImgShow == null || instance.m_LevelConfigItemData?.dbData == null) return;
            var data = instance.m_LevelConfigItemData;
            CharacterInfo? character = data.dbData.TryCast<CharacterInfo>();
            if (character != null)
            {
                string key = instance.m_ImgShow.sprite != null ? instance.m_ImgShow.sprite.name :
                    (character.mainShow ?? string.Empty).Replace("main_show", "char_thumb");
                CaptureAndApply(instance.m_ImgShow, character.listIndex, false, IsHexie(key), key);
                return;
            }
            ElfinInfo? elfin = data.dbData.TryCast<ElfinInfo>();
            if (elfin != null)
            {
                string key = elfin.GetThumbImgName() ?? (instance.m_ImgShow.sprite != null ? instance.m_ImgShow.sprite.name : string.Empty);
                CaptureAndApply(instance.m_ImgShow, elfin.listIndex, true, IsHexie(key), key);
            }
        }
        catch (Exception ex) { Warning("Level-config UI apply failed: " + ex.Message); }
    }

    private static void CaptureAndApply(Image image, int listIndex, bool elfin, bool hexie, string observedKey)
    {
        Sprite? original = image.sprite;
        int imageId = image.GetInstanceID();
        if (original != null && !IsOwned(original))
        {
            original = ValidateOriginal(listIndex, elfin, hexie, observedKey, original);
            if (original == null) return;
            string expectedKey = AddressKey(listIndex, elfin, hexie);
            Bindings[imageId] = new ThumbnailImageBinding { Image = image, Original = original, ListIndex = listIndex, Elfin = elfin, Hexie = hexie };
            Originals[OriginalKey(listIndex, elfin, hexie)] = new ThumbnailOriginal { Sprite = original, AddressKey = expectedKey, ListIndex = listIndex, Elfin = elfin, Hexie = hexie };
            LogMetadataOnce(listIndex, elfin, hexie, expectedKey, original, image);
        }
        else if (Bindings.TryGetValue(imageId, out ThumbnailImageBinding? prior) && prior.ListIndex == listIndex &&
                 prior.Elfin == elfin && prior.Hexie == hexie)
        {
            original = prior.Original;
        }
        else
        {
            original = ResolveOriginal(listIndex, elfin, hexie);
            if (original == null) return;
            Bindings[imageId] = new ThumbnailImageBinding { Image = image, Original = original, ListIndex = listIndex, Elfin = elfin, Hexie = hexie };
        }

        string path = ReplacementPath(listIndex, elfin, hexie);
        // SkinFiles, not File: when a .sfskin is the selected skin, GetSkinPath returns the package's
        // in-memory directory, and the thumbnail lives there and nowhere on disk (2026-11-21).
        if (!string.IsNullOrEmpty(path) && SkinFiles.Exists(path) && original != null)
        {
            Sprite? replacement = Load(path, original);
            if (replacement != null) image.sprite = replacement;
        }
        else if (original != null)
        {
            image.sprite = original;
        }
    }

    public static void Reload()
    {
        try
        {
            foreach (ThumbnailImageBinding binding in Bindings.Values)
                if (binding.Image != null && binding.Original != null) binding.Image.sprite = binding.Original;
            DestroyCache();
            foreach (ThumbnailImageBinding binding in new List<ThumbnailImageBinding>(Bindings.Values))
                if (binding.Image != null) CaptureAndApply(binding.Image, binding.ListIndex, binding.Elfin, binding.Hexie,
                    binding.Original != null ? binding.Original.name : string.Empty);
            Log("Reload completed: replacement cache rebuilt; tracked UI images=" + Bindings.Count);
        }
        catch (Exception ex) { Warning("Reload failed: " + ex.Message); }
    }

    public static bool DrawCategory(bool elfin)
    {
        GUILayout.BeginHorizontal((Il2CppReferenceArray<GUILayoutOption>?)null);
        bool sprites = elfin ? _elfinSprites : _characterSprites;
        Color old = GUI.contentColor;
        if (sprites) GUI.contentColor = Color.green;
        if (GUILayout.Button("Sprites", new[] { GUILayout.Width(150f) })) sprites = true;
        GUI.contentColor = old;
        if (!sprites) GUI.contentColor = Color.green;
        if (GUILayout.Button("Selection Thumbnail", new[] { GUILayout.Width(220f) })) sprites = false;
        GUI.contentColor = old;
        GUILayout.EndHorizontal();
        if (elfin) _elfinSprites = sprites; else _characterSprites = sprites;
        if (!sprites) DrawThumbnailList(elfin);
        return sprites;
    }

    private static void DrawThumbnailList(bool elfin)
    {
        int count = HostInt("GetCount", elfin ? "elfin" : "character");
        for (int i = 0; i < count; i++)
        {
            string name = HostString("GetCostumeName", i, elfin);
            if (string.IsNullOrEmpty(name)) name = HostString("GetDisplayName", i, elfin);
            GUILayout.BeginHorizontal((Il2CppReferenceArray<GUILayoutOption>?)null);
            GUILayout.Label(name, new[] { GUILayout.Width(220f) });
            if (GUILayout.Button("Dump", new[] { GUILayout.Width(90f) })) Dump(i, elfin, false);
            if (HasHexieOriginal(i, elfin))
            {
                if (GUILayout.Button("Dump Hexie", new[] { GUILayout.Width(110f) })) Dump(i, elfin, true);
            }
            string normal = ReplacementPath(i, elfin, false);
            string status = SkinFiles.Exists(normal) ? "Replacement ready" : "Original";
            GUILayout.Label(status, new[] { GUILayout.Width(160f) });
            GUILayout.EndHorizontal();
        }
    }

    private static bool HasHexieOriginal(int listIndex, bool elfin)
    {
        if (Originals.ContainsKey(OriginalKey(listIndex, elfin, true))) return true;
        string key = AddressKey(listIndex, elfin, true);
        return !string.IsNullOrEmpty(key);
    }

    private static void Dump(int listIndex, bool elfin, bool hexie)
    {
        try
        {
            Sprite? sprite = ResolveOriginal(listIndex, elfin, hexie);
            if (sprite == null) { Warning("Dump unavailable: original " + (elfin ? "Elfin" : "Character") + " thumbnail was not found for listIndex=" + listIndex); return; }
            string directory = ActiveOrDumpDirectory(listIndex, elfin);
            if (string.IsNullOrEmpty(directory)) { Warning("Dump unavailable: SkinForger folder mapping was not found for listIndex=" + listIndex); return; }
            Directory.CreateDirectory(directory);
            string output = Path.Combine(directory, hexie ? HexieFile : NormalFile);
            byte[] png = EncodeSprite(sprite);
            HostInvoke("WriteThumbnailBytes", output, png);
            Log("Dumped " + (elfin ? "Elfin" : "Character") + " listIndex=" + listIndex + " key=" + sprite.name + " -> " + output);
        }
        catch (Exception ex) { Warning("Dump failed: " + ex.Message); }
    }

    private static Sprite? ResolveOriginal(int listIndex, bool elfin, bool hexie)
    {
        string key = AddressKey(listIndex, elfin, hexie);
        string originalKey = OriginalKey(listIndex, elfin, hexie);
        if (Originals.TryGetValue(originalKey, out ThumbnailOriginal? original) && original.Sprite != null)
        {
            if (ResourceMatches(key, original.Sprite.name) && ResourceMatches(key, original.AddressKey))
                return original.Sprite;
            Warning("SelectionThumbnail identity mismatch in Originals: listIndex=" + listIndex +
                " expected=" + key + " actual=" + original.Sprite.name + "; ignoring stale binding");
            Originals.Remove(originalKey);
        }
        if (string.IsNullOrEmpty(key)) return null;
        try
        {
            Sprite? loaded = HostInvoke("LoadThumbnailSprite", key) as Sprite;
            if (loaded != null && !ResourceMatches(key, loaded.name))
            {
                Warning("SelectionThumbnail Addressables mismatch: listIndex=" + listIndex +
                    " expected=" + key + " actual=" + loaded.name);
                return null;
            }
            return loaded;
        }
        catch (Exception ex)
        {
            Warning("Addressable Sprite lookup failed for " + key + ": " + ex.Message);
            return null;
        }
    }

    private static Sprite? ValidateOriginal(int listIndex, bool elfin, bool hexie, string observedKey, Sprite sprite)
    {
        string expectedKey = AddressKey(listIndex, elfin, hexie);
        if (string.IsNullOrEmpty(expectedKey) || ResourceMatches(expectedKey, sprite.name)) return sprite;

        Warning("SelectionThumbnail identity mismatch: listIndex=" + listIndex + " expected=" + expectedKey +
            " actual=" + sprite.name + " observedKey=" + observedKey + "; using Addressables fallback");
        try
        {
            Sprite? fallback = HostInvoke("LoadThumbnailSprite", expectedKey) as Sprite;
            if (fallback != null && ResourceMatches(expectedKey, fallback.name)) return fallback;
            Warning("SelectionThumbnail fallback rejected: listIndex=" + listIndex + " expected=" + expectedKey +
                " actual=" + (fallback != null ? fallback.name : "<null>"));
        }
        catch (Exception ex)
        {
            Warning("SelectionThumbnail fallback failed for " + expectedKey + ": " + ex.Message);
        }
        return null;
    }

    private static bool ResourceMatches(string expected, string actual)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(actual)) return false;
        return NormalizeResourceName(expected).Equals(NormalizeResourceName(actual), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeResourceName(string value)
    {
        string name = value.Replace('\\', '/');
        int slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];
        return name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private static string AddressKey(int listIndex, bool elfin, bool hexie)
    {
        if (elfin)
        {
            string key = HostString("ConfigString", "elfin", listIndex, hexie ? "thumbHexie" : "thumb");
            return key ?? string.Empty;
        }
        string mainShow = HostString("ConfigString", "character", listIndex, "mainShow");
        if (string.IsNullOrEmpty(mainShow)) return string.Empty;
        string key2 = mainShow.Replace("main_show", "char_thumb");
        return hexie ? key2 + "_hexie" : key2;
    }

    private static Sprite? Load(string path, Sprite original)
    {
        // A file inside a mounted package has no FileInfo. Its contents cannot change while it stays
        // mounted, and enabling or disabling a package goes through ReloadAssets -> Reload(), which empties
        // this cache, so the path alone is a sound stamp there.
        string stamp;
        if (SkinFiles.IsInPackage(path))
        {
            stamp = "package";
        }
        else
        {
            var info = new FileInfo(path);
            stamp = info.Length + ":" + info.LastWriteTimeUtc.Ticks;
        }
        if (Cache.TryGetValue(path, out ThumbnailCacheEntry? cached) && cached.Stamp == stamp && cached.Sprite != null)
            return cached.Sprite;
        Destroy(path);
        byte[] bytes = SkinFiles.ReadAllBytes(path);
        if (bytes.Length < 24 || bytes[0] != 137 || bytes[1] != 80 || bytes[2] != 78 || bytes[3] != 71)
            throw new FormatException("Invalid PNG: " + path);
        Texture2D texture = new(2, 2, TextureFormat.RGBA32, false);
        try
        {
            if (!Il2CppImageConversionManager.LoadImage(texture, (Il2CppStructArray<byte>)bytes))
                throw new FormatException("PNG decode failed: " + path);
            texture.name = "SkinForger.SelectionThumbnail." + Path.GetFileNameWithoutExtension(path);
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = original.texture != null ? original.texture.filterMode : FilterMode.Bilinear;
            texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
            Rect originalRect = original.rect;
            Vector2 pivot = originalRect.width > 0f && originalRect.height > 0f
                ? new Vector2(original.pivot.x / originalRect.width, original.pivot.y / originalRect.height)
                : new Vector2(0.5f, 0.5f);
            Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height), pivot,
                original.pixelsPerUnit, 0, SpriteMeshType.FullRect, original.border, false);
            sprite.name = original.name;
            sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
            Cache[path] = new ThumbnailCacheEntry { Path = path, Stamp = stamp, Texture = texture, Sprite = sprite };
            return sprite;
        }
        catch
        {
            Object.Destroy(texture);
            throw;
        }
    }

    private static byte[] EncodeSprite(Sprite sprite)
    {
        Texture2D source = sprite.texture;
        Rect rect = sprite.textureRect;
        RenderTexture previous = RenderTexture.active;
        RenderTexture temporary = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
        Texture2D? crop = null;
        try
        {
            Graphics.Blit(source, temporary);
            RenderTexture.active = temporary;
            int width = Math.Max(1, (int)Math.Round(rect.width));
            int height = Math.Max(1, (int)Math.Round(rect.height));
            crop = new Texture2D(width, height, TextureFormat.RGBA32, false);
            crop.ReadPixels(new Rect(rect.x, rect.y, width, height), 0, 0, false);
            crop.Apply(false, false);
            ApplyPackingRotation(crop, sprite.packed ? sprite.packingRotation.ToString() : "None");
            return Il2CppImageConversionManager.EncodeToPNG(crop);
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(temporary);
            if (crop != null) Object.Destroy(crop);
        }
    }

    private static void ApplyPackingRotation(Texture2D texture, string rotation)
    {
        if (rotation == "None" || rotation == "Any") return;
        Color32[] source = texture.GetPixels32();
        Color32[] result = new Color32[source.Length];
        int w = texture.width;
        int h = texture.height;
        if (rotation == "FlipHorizontal")
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) result[y * w + x] = source[y * w + (w - 1 - x)];
        else if (rotation == "FlipVertical")
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) result[y * w + x] = source[(h - 1 - y) * w + x];
        else if (rotation == "Rotate180")
            for (int i = 0; i < source.Length; i++) result[i] = source[source.Length - 1 - i];
        else { Warning("Unsupported Sprite packingRotation=" + rotation + "; dumped textureRect without transform"); return; }
        texture.SetPixels32(result);
        texture.Apply(false, false);
    }

    private static string ReplacementPath(int listIndex, bool elfin, bool hexie)
    {
        string directory = ActiveDirectory(listIndex, elfin);
        return string.IsNullOrEmpty(directory) ? string.Empty : Path.Combine(directory, hexie ? HexieFile : NormalFile);
    }

    private static string ActiveOrDumpDirectory(int listIndex, bool elfin)
    {
        string active = ActiveDirectory(listIndex, elfin);
        return string.IsNullOrEmpty(active) ? HostString("GetDumpSkinDirectory", listIndex, elfin) : active;
    }

    private static string ActiveDirectory(int listIndex, bool elfin)
    {
        int selected = HostInt("GetSelected", listIndex, elfin);
        return selected < 0 ? string.Empty : HostString("GetSkinPath", listIndex, selected, elfin);
    }

    private static bool IsOwned(Sprite sprite) => sprite != null && sprite.name != null && sprite.texture != null &&
        sprite.texture.name.StartsWith("SkinForger.SelectionThumbnail.", StringComparison.Ordinal);
    private static bool IsHexie(string value) => !string.IsNullOrEmpty(value) && value.IndexOf("hexie", StringComparison.OrdinalIgnoreCase) >= 0;
    private static string OriginalKey(int listIndex, bool elfin, bool hexie) => (elfin ? "E" : "C") + ":" + listIndex + ":" + (hexie ? "H" : "N");

    private static void Destroy(string path)
    {
        if (!Cache.TryGetValue(path, out ThumbnailCacheEntry? item)) return;
        if (item.Sprite != null) Object.Destroy(item.Sprite);
        if (item.Texture != null) Object.Destroy(item.Texture);
        Cache.Remove(path);
    }

    private static void DestroyCache()
    {
        foreach (string path in new List<string>(Cache.Keys)) Destroy(path);
    }

    private static void LogMetadataOnce(int listIndex, bool elfin, bool hexie, string key, Sprite sprite, Image image)
    {
        string token = "meta:" + OriginalKey(listIndex, elfin, hexie) + ":" + sprite.GetInstanceID();
        if (Originals.ContainsKey(token)) return;
        Originals[token] = new ThumbnailOriginal();
        Texture2D texture = sprite.texture;
        Log("Captured " + (elfin ? "Elfin" : "Character") + " listIndex=" + listIndex + " variant=" + (hexie ? "hexie" : "normal") +
            " key=" + key + " sprite=" + sprite.name + " rect=" + RectText(sprite.rect) + " textureRect=" + RectText(sprite.textureRect) +
            " pivot=" + sprite.pivot.x + "," + sprite.pivot.y + " ppu=" + sprite.pixelsPerUnit + " border=" + sprite.border +
            " packed=" + sprite.packed + " packingRotation=" + sprite.packingRotation + " texture=" + texture.name +
            "#" + texture.GetInstanceID() + " size=" + texture.width + "x" + texture.height + " format=" + texture.format +
            " filter=" + texture.filterMode + " wrap=" + texture.wrapMode + " image=" + image.name);
    }

    private static string RectText(Rect r) => r.x + "," + r.y + "," + r.width + "," + r.height;

    private static object? HostInvoke(string method, params object?[] args)
    {
        MethodInfo? target = _host?.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        if (target == null) throw new MissingMethodException("SkinForgerMod." + method);
        return target.Invoke(null, args);
    }

    private static string HostString(string method, params object?[] args) => HostInvoke(method, args) as string ?? string.Empty;
    private static int HostInt(string method, params object?[] args) => Convert.ToInt32(HostInvoke(method, args));
    private static void Log(string text) => MelonLogger.Msg(Prefix + " " + text);
    private static void Warning(string text) => MelonLogger.Warning(Prefix + " " + text);
}
