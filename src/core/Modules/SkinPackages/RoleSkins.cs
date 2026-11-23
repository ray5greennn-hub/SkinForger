using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using MelonLoader;
using SkinForger.HexieBlock;
using Il2CppAssets.Scripts.UI.Panels.PnlRole;
using Il2CppPeroTools2.Resources;
using Il2CppSpine.Unity;
using UnityEngine;
using UnityEngine.UI;

namespace SkinForger.SkinPackages;

/// <summary>
/// Second (third ...) character skins (2026-11-15). Characters such as Maid or Bassist have more than one skin
/// (character config "skins": RoleSkin { mainShow, battleShow, battleSubShow, victoryShow, failShow, hexie ... }).
/// Main dumps and recognises only the character's own show prefabs, so the other skins were neither dumped nor
/// replaced. Two hooks on Main:
///  - DumpCostumeResources postfix: also spawns every other skin's show prefabs for the Dump, through Main's own
///    TrySpawnForExtract, so their atlases land in the same costume folder (Images\Bassist\&lt;atlas&gt;\...);
///  - Scan postfix: a skeleton whose object name contains one of those prefab names belongs to that character, so
///    Main finds the costume folder for it (Bassist's second skin has no "bassist" in its names).
/// Skin switch icons (the round buttons of the character page that switch between a character's skins,
/// PnlRoleSkinContainer / PnlRoleSkinItem.m_ImgSkin). The config does not name them (headName = "Avatar_..." is a
/// different picture, 2026-11-15), so they are learned from the page itself: when it lists a character's skins, each
/// icon is exported to Images\&lt;Costume&gt;\Icon\&lt;sprite&gt;.png (if that character was dumped and the file
/// is missing) and remembered (UserData\SkinForger\skin-icons.json) so the Dump exports them too. In game the PNG
/// of the character's selected SkinForger skin (Icon folder) is shown instead of the icon.
/// Freeze 2026-11-15: a freshly dumped other-skin battle atlas (Maid cat, 104 parts) took 50 s to rebuild because
/// every dumped PNG is placed again. For atlases of other skins only images the creator changed (EditDetector) are
/// used; untouched dumped originals are skipped.
/// Censored skins (skins[].hexie == true) and any hexie resource (headNameHexie ...) are never touched.
/// </summary>
internal static class RoleSkins
{
    private const string Prefix = "[SkinForge][RoleSkins]";
    private static readonly string[] ShowFields = { "mainShow", "battleShow", "victoryShow", "failShow" };

    private static MethodInfo? _trySpawn, _getCostumeName, _configString;
    private static object? _config;
    private static PropertyInfo? _configProperty;
    private static FieldInfo? _configField;

    /// <summary>Per character index: show prefabs of its other skins (lower case), without the character's own ones.</summary>
    private static List<List<string>>? _extra;
    /// <summary>Per character index: selection icon sprite names of all its (uncensored) skins.</summary>
    private static List<List<string>> _icons = new();
    /// <summary>icon name (lower case) -> character index.</summary>
    private static Dictionary<string, int> _iconOwners = new(StringComparer.OrdinalIgnoreCase);
    // 2026-11-17 (user): "Icon" was too vague -> SkinSwitchIcons. The old name is still read (older dumps / packages).
    internal const string IconFolder = "SkinSwitchIcons";
    internal const string LegacyIconFolder = "Icon";
    /// <summary>Show prefabs of cat skins (RoleSkin.isCat: frame-by-frame animation), lower case.</summary>
    private static HashSet<string> _catPrefabs = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Atlas pages of cat skins: no anchor sidecar JSON is written for their images.</summary>
    private static readonly HashSet<string> CatAtlases = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Per character index: the show prefabs of each of its uncensored skins (skins order).</summary>
    private static List<List<Dictionary<string, string>>> _skinShows = new();
    private static MethodInfo? _writeResource;
    private static bool _loggedSkinKeys;
    /// <summary>(prefab name, character index), longest name first.</summary>
    private static List<(string Name, int Index)> _owners = new();
    private static int _attempts;
    private static readonly HashSet<string> Logged = new(StringComparer.Ordinal);

    internal static int Install(HarmonyLib.Harmony harmony)
    {
        Type host = AccessTools.TypeByName("SkinForger.SkinForgerMod") ?? throw new TypeLoadException("SkinForger.SkinForgerMod");
        _trySpawn = AccessTools.DeclaredMethod(host, "TrySpawnForExtract");
        _getCostumeName = AccessTools.DeclaredMethod(host, "GetCostumeName", new[] { typeof(int), typeof(bool) });
        _configString = AccessTools.DeclaredMethod(host, "ConfigString", new[] { typeof(string), typeof(int), typeof(string) });
        _configProperty = AccessTools.Property(host, "Config");
        _configField = _configProperty == null ? AccessTools.Field(host, "Config") : null;
        int patched = 0;
        MethodInfo? dump = AccessTools.DeclaredMethod(host, "DumpCostumeResources");
        if (dump != null && _trySpawn != null)
        {
            harmony.Patch(dump, postfix: new HarmonyMethod(typeof(RoleSkins), nameof(DumpPostfix)));
            patched++;
        }
        else MelonLogger.Warning($"{Prefix} Main dump methods not found: other character skins are not dumped");
        MethodInfo? scan = AccessTools.DeclaredMethod(host, "Scan");
        if (scan != null)
        {
            harmony.Patch(scan, postfix: new HarmonyMethod(typeof(RoleSkins), nameof(ScanPostfix)) { priority = HarmonyLib.Priority.Last });
            patched++;
        }
        else MelonLogger.Warning($"{Prefix} Main Scan not found: other character skins are not replaced");
        try
        {
            harmony.Patch(AccessTools.PropertySetter(typeof(Image), nameof(Image.sprite)), postfix: new HarmonyMethod(typeof(RoleSkins), nameof(ImageSpritePostfix)));
            patched++;
        }
        catch (Exception exception) { MelonLogger.Warning($"{Prefix} Image.sprite setter not patched: skin icons are not replaced ({exception.GetBaseException().Message})"); }
        try
        {
            harmony.Patch(AccessTools.Method(typeof(PnlRoleSkinContainer), nameof(PnlRoleSkinContainer.RefreshSkinItems)),
                postfix: new HarmonyMethod(typeof(RoleSkins), nameof(RefreshSkinItemsPostfix)));
            patched++;
        }
        catch (Exception exception) { MelonLogger.Warning($"{Prefix} character page skin list not patched: {exception.GetBaseException().Message}"); }
        MethodInfo? regionPath = AccessTools.DeclaredMethod(host, "GetExistingRegionPath");
        if (regionPath != null)
        {
            harmony.Patch(regionPath, postfix: new HarmonyMethod(typeof(RoleSkins), nameof(RegionPathPostfix)) { priority = HarmonyLib.Priority.Low });   // before LoadCaches records the answer
            patched++;
        }
        MethodInfo? rootData = AccessTools.DeclaredMethod(host, "GetRootSkinDataDirectories");
        if (rootData != null) { harmony.Patch(rootData, postfix: new HarmonyMethod(typeof(RoleSkins), nameof(RootDataPostfix))); patched++; }
        MethodInfo? reload = AccessTools.DeclaredMethod(host, "ReloadAssets", Type.EmptyTypes);
        if (reload != null)
        {
            harmony.Patch(reload, prefix: new HarmonyMethod(typeof(RoleSkins), nameof(ReloadPrefix)), postfix: new HarmonyMethod(typeof(RoleSkins), nameof(ReloadPostfix)));
            patched++;
        }
        // Main's Dump asks this before it writes anchors: a cat folder keeps one anchors.json, not one
        // JSON per frame. Only this module knows which atlases are cat skins (2026-11-21).
        FieldInfo? oneFile = AccessTools.Field(host, "FolderKeepsAnchorsInOneFile");
        if (oneFile != null)
        {
            oneFile.SetValue(null, new Func<string, bool>(KeepsAnchorsInOneFile));
            patched++;
        }
        else MelonLogger.Warning($"{Prefix} the host has no FolderKeepsAnchorsInOneFile: cat folders will get one JSON per image");

        MethodInfo? anchorFile = AccessTools.DeclaredMethod(host, "WriteAnchorMetadataIfMissing");
        if (anchorFile != null) { harmony.Patch(anchorFile, prefix: new HarmonyMethod(typeof(RoleSkins), nameof(AnchorSidecarPrefix)) { priority = HarmonyLib.Priority.First }); patched++; }
        _writeResource = AccessTools.TypeByName("SkinForger.ReplacedBackup") is { } backup
            ? AccessTools.DeclaredMethod(backup, "WriteResourceBytes", new[] { typeof(string), typeof(byte[]) }) : null;
        patched += InstallPreview(harmony);
        LoadLearnedIcons();
        _getSkinPath = AccessTools.DeclaredMethod(host, "GetSkinPath", new[] { typeof(int), typeof(int), typeof(bool) });
        _getSelected = AccessTools.DeclaredMethod(host, "GetSelected", new[] { typeof(int), typeof(bool) });
        return patched;
    }

    private static MethodInfo? _getSkinPath, _getSelected;

    // ------------------------------------------------------------ config

    private static bool EnsureLoaded()
    {
        if (_extra != null) return true;
        if (_attempts >= 30) return false;
        _attempts++;
        try
        {
            string? json = global::SkinForger.GameConfigJson.Text("character");
            if (string.IsNullOrEmpty(json)) return false;
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return false;

            var extra = new List<List<string>>();
            var cats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var skinShows = new List<List<Dictionary<string, string>>>();
            var icons = new List<List<string>>();
            var iconOwners = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var owners = new List<(string, int)>();
            int index = 0, skins = 0, censored = 0;
            foreach (JsonElement character in document.RootElement.EnumerateArray())
            {
                var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string name in ShowNames(character)) own.Add(name);
                var names = new List<string>();
                var shows = new List<Dictionary<string, string>>();
                var heads = new List<string>();
                if (character.TryGetProperty("skins", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement skin in list.EnumerateArray())
                    {
                        if (skin.ValueKind != JsonValueKind.Object) continue;
                        if (skin.TryGetProperty("hexie", out JsonElement h) && h.ValueKind == JsonValueKind.True) { censored++; continue; }
                        skins++;
                        // 19-55 log: "0 of cat skins" with isCat only. RoleSkinType { Default, Cat, Foolish }: skinType 1 / "Cat";
                        // the game's cat prefabs also carry "_cat" in their names.
                        bool cat = (skin.TryGetProperty("isCat", out JsonElement c) && c.ValueKind == JsonValueKind.True) ||
                                   (skin.TryGetProperty("skinType", out JsonElement st) &&
                                    ((st.ValueKind == JsonValueKind.Number && st.TryGetInt32(out int type) && type == 1) ||
                                     (st.ValueKind == JsonValueKind.String && string.Equals(st.GetString(), "Cat", StringComparison.OrdinalIgnoreCase)))) ||
                                   ShowNames(skin).Any(n => n.IndexOf("_cat", StringComparison.OrdinalIgnoreCase) >= 0);
                        if (!_loggedSkinKeys && skins == 1)
                        {
                            _loggedSkinKeys = true;
                            MelonLogger.Msg($"{Prefix} skin fields in the game config: {string.Join(", ", skin.EnumerateObject().Select(o => o.Name))}");
                        }
                        // Keyed by field name for the Preview contexts; every *Show field that holds a
                        // single name, not just the four that used to be listed.
                        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                        foreach (JsonProperty property in skin.EnumerateObject())
                            if (property.Name.EndsWith("show", StringComparison.OrdinalIgnoreCase) &&
                                property.Value.ValueKind == JsonValueKind.String &&
                                property.Value.GetString() is { Length: > 0 } show && !HexiePolicy.IsHexieResource(show))
                                fields[property.Name] = show;
                        if (fields.Count > 0) shows.Add(fields);
                        if (cat) foreach (string show in ShowNames(skin)) cats.Add(show.ToLowerInvariant());
                        foreach (string name in ShowNames(skin))
                        {
                            if (own.Contains(name) || HexiePolicy.IsHexieResource(name) || names.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                            names.Add(name);
                            owners.Add((name.ToLowerInvariant(), index));

                            // 2026-11-18: a battle prefab comes with companions the config never names —
                            // <name>_shadow and <name>_ghost, each with an atlas of its own. Bassist's muscle
                            // skin has char_1_rock_muscle_shadow and char_1_musclerin_shadow that way, and
                            // they were missing from every Dump. The names follow the game's own convention,
                            // and TrySpawnForExtract simply reports one that does not exist.
                            // Companions the config never names, found by the game's own naming:
                            //   <x>_battle      -> <x>_battle_shadow, <x>_battle_ghost
                            //   <x>_main_show   -> <x>_battle_show   (the entrance picture beside the HP bar;
                            //                      2026-11-18: musclerin_girl_battle_show exists in the game
                            //                      but no *Show field of the skin mentions it)
                            var companions = new List<string>();
                            if (name.EndsWith("_battle", StringComparison.OrdinalIgnoreCase))
                            {
                                companions.Add(name + "_shadow");
                                companions.Add(name + "_ghost");
                            }
                            if (name.EndsWith("_main_show", StringComparison.OrdinalIgnoreCase))
                            {
                                string stem = name[..^"_main_show".Length];
                                companions.Add(stem + "_battle_show");
                                companions.Add(stem + "_battle_show_shadow");
                            }
                            foreach (string companion in companions)
                            {
                                if (HexiePolicy.IsHexieResource(companion) ||
                                    own.Contains(companion) ||
                                    names.Contains(companion, StringComparer.OrdinalIgnoreCase)) continue;
                                names.Add(companion);
                                owners.Add((companion.ToLowerInvariant(), index));
                            }
                        }
                    }
                extra.Add(names);
                skinShows.Add(shows);
                icons.Add(heads);
                index++;
            }
            _extra = extra;
            _owners = owners.OrderByDescending(o => o.Item1.Length).ToList();
            _catPrefabs = cats;
            _skinShows = skinShows;
            _icons = icons;
            foreach (var pair in _iconOwners) iconOwners.TryAdd(pair.Key, pair.Value);   // learned before the config was read
            _iconOwners = iconOwners;
            ApplyLearnedIcons();
            MelonLogger.Msg($"{Prefix} {index} characters, {skins} skins ({censored} censored skipped), {owners.Count} extra show prefabs ({_catPrefabs.Count} of cat skins) " +
                            $"for {extra.Count(e => e.Count > 0)} characters");
            return true;
        }
        catch (Exception exception)
        {
            if (_attempts == 1) MelonLogger.Warning($"{Prefix} config read failed: {exception.GetBaseException().Message}");
            return false;
        }
    }

    /// <summary>
    /// Every show prefab a skin names. Read generically rather than from a fixed list of four fields:
    /// any property whose name ends in "Show" counts, and it may hold a single name OR an array of them
    /// (battleSubShow does). 2026-11-18: musclerin_girl_battle_show — the entrance picture beside the HP
    /// bar — was missing from every Dump because the fixed list did not mention battleSubShow and the
    /// array branch only looked at that one name.
    /// </summary>
    private static IEnumerable<string> ShowNames(JsonElement skin)
    {
        if (skin.ValueKind != JsonValueKind.Object) yield break;
        foreach (JsonProperty property in skin.EnumerateObject())
        {
            if (!property.Name.EndsWith("show", StringComparison.OrdinalIgnoreCase)) continue;
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.String:
                    if (property.Value.GetString() is { Length: > 0 } name) yield return name;
                    break;
                case JsonValueKind.Array:
                    foreach (JsonElement value in property.Value.EnumerateArray())
                        if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } item)
                            yield return item;
                    break;
            }
        }
    }

    // ------------------------------------------------------------ Dump

    /// <summary>Main's DumpCostumeResources(ResourcesManager, index, elfin): also dump the character's other skins.</summary>
    private static void DumpPostfix(object __0, int __1, bool __2, ref int __result)
    {
        if (__2 || !EnsureLoaded() || _extra == null || __1 < 0 || __1 >= _extra.Count) return;
        string costume = _getCostumeName?.Invoke(null, new object[] { __1, false }) as string ?? $"#{__1}";
        if (SfSkinFormat.IsCensoredName(costume)) return;
        int icons = DumpIcons(__0, __1, costume);
        int spawned = 0;
        foreach (string name in _extra[__1])
        {
            try
            {
                _trySpawn!.Invoke(null, new object[] { __0, name, costume, __1, false });
                spawned++;
            }
            catch (Exception exception)
            {
                MelonLogger.Warning($"{Prefix} dump of {costume}/{name} failed: {exception.GetBaseException().Message}");
            }
        }
        __result += spawned;
        if (icons > 0) MelonLogger.Msg($"{Prefix} Dump {costume}: {icons} skin icon(s) written to {IconFolder}");
        if (spawned > 0) MelonLogger.Msg($"{Prefix} Dump {costume}: {spawned} show(s) of its other skins added ({string.Join(", ", _extra[__1])})");
    }

    // ------------------------------------------------------------ skin icons

    /// <summary>Writes Images\&lt;Costume&gt;\Icon\&lt;headName&gt;.png for each skin; existing files are kept (may be edited).</summary>
    private static int DumpIcons(object resources, int index, string costume)
    {
        if (!LearnedIcons.TryGetValue(costume, out List<string>? learned) || learned.Count == 0)
        {
            MelonLogger.Msg($"{Prefix} Dump {costume}: skin switch icons not known yet; open this character's page once (they are exported then)");
            return 0;
        }
        if (resources is not ResourcesManager manager) return 0;
        string folder = Path.Combine(SkinForgerPaths.Creator, costume, IconFolder);
        int written = 0;
        foreach (string name in learned)
        {
            try
            {
                string file = Path.Combine(folder, name + ".png");
                if (File.Exists(file)) continue;
                Sprite? sprite = manager.LoadFromName<Sprite>(name);
                if (sprite == null) { MelonLogger.Warning($"{Prefix} icon {name} of {costume} not found"); continue; }
                if (HexiePolicy.IsHexieName(sprite.name) || HexiePolicy.IsHexieName(sprite.texture?.name)) continue;
                byte[]? png = SpritePng(sprite);
                if (png == null) continue;
                WriteIcon(file, png);
                written++;
            }
            catch (Exception exception) { MelonLogger.Warning($"{Prefix} icon {name} of {costume} not dumped: {exception.GetBaseException().Message}"); }
        }
        return written;
    }


    /// <summary>
    /// The whole sprite as PNG, sprite.rect sized. 2026-11-15: cutting sprite.textureRect out of the atlas cut the
    /// bottom of maid_girl_skin_thumb (a tight-packed sprite: its mesh, not a rectangle, says where its pixels are).
    /// The atlas is read back once (game textures are not readable) and every triangle of the sprite mesh is copied
    /// pixel by pixel (barycentric UV, nearest texel), so trimmed, tight or rotated packing all come out right.
    /// </summary>
    internal static byte[]? SpritePng(Sprite sprite)
    {
        Texture2D? source = sprite.texture;
        if (source == null) return null;
        Color32[] atlas = ReadBack(source);
        int tw = source.width, th = source.height;
        Rect rect = sprite.rect;
        int w = Math.Max(1, (int)Math.Round(rect.width)), h = Math.Max(1, (int)Math.Round(rect.height));
        var output = new Color32[w * h];
        Vector2[] vertices = sprite.vertices;
        Vector2[] uv = sprite.uv;
        ushort[] triangles = sprite.triangles;
        float ppu = sprite.pixelsPerUnit;
        Vector2 pivot = sprite.pivot;
        for (int t = 0; t + 2 < triangles.Length; t += 3)
        {
            int i0 = triangles[t], i1 = triangles[t + 1], i2 = triangles[t + 2];
            Vector2 p0 = vertices[i0] * ppu + pivot, p1 = vertices[i1] * ppu + pivot, p2 = vertices[i2] * ppu + pivot;
            float area = (p1.x - p0.x) * (p2.y - p0.y) - (p2.x - p0.x) * (p1.y - p0.y);
            if (Math.Abs(area) < 1e-6f) continue;
            int minX = Math.Max(0, (int)Math.Floor(Math.Min(p0.x, Math.Min(p1.x, p2.x))));
            int maxX = Math.Min(w - 1, (int)Math.Ceiling(Math.Max(p0.x, Math.Max(p1.x, p2.x))));
            int minY = Math.Max(0, (int)Math.Floor(Math.Min(p0.y, Math.Min(p1.y, p2.y))));
            int maxY = Math.Min(h - 1, (int)Math.Ceiling(Math.Max(p0.y, Math.Max(p1.y, p2.y))));
            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    float b1 = ((px - p0.x) * (p2.y - p0.y) - (p2.x - p0.x) * (py - p0.y)) / area;
                    float b2 = ((p1.x - p0.x) * (py - p0.y) - (px - p0.x) * (p1.y - p0.y)) / area;
                    float b0 = 1f - b1 - b2;
                    const float e = -0.001f;
                    if (b0 < e || b1 < e || b2 < e) continue;
                    float u = b0 * uv[i0].x + b1 * uv[i1].x + b2 * uv[i2].x;
                    float v = b0 * uv[i0].y + b1 * uv[i1].y + b2 * uv[i2].y;
                    int sx = Math.Clamp((int)(u * tw), 0, tw - 1), sy = Math.Clamp((int)(v * th), 0, th - 1);
                    output[y * w + x] = atlas[sy * tw + sx];
                }
        }
        var copy = new Texture2D(w, h, TextureFormat.RGBA32, false);
        copy.SetPixels32(output);
        copy.Apply();
        byte[] png = ImageConversion.EncodeToPNG(copy);
        UnityEngine.Object.Destroy(copy);
        return png;
    }

    internal static Color32[] ReadBack(Texture2D source)
    {
        RenderTexture previous = RenderTexture.active;
        RenderTexture temporary = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        try
        {
            temporary.filterMode = FilterMode.Point;
            Graphics.Blit(source, temporary);
            RenderTexture.active = temporary;
            var copy = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            copy.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            copy.Apply();
            Color32[] pixels = copy.GetPixels32();
            UnityEngine.Object.Destroy(copy);
            return pixels;
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(temporary);
        }
    }

    // ------------------------------------------------------------ skin switch icons learned from the character page

    private static readonly Dictionary<string, List<string>> LearnedIcons = new(StringComparer.OrdinalIgnoreCase);   // costume -> sprite names
    private static string LearnedFile => Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "SkinForger", "skin-icons.json");

    private static void LoadLearnedIcons()
    {
        try
        {
            if (!File.Exists(LearnedFile)) return;
            var data = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(LearnedFile));
            if (data != null) foreach (var pair in data) LearnedIcons[pair.Key] = pair.Value;
        }
        catch { }
    }

    private static void SaveLearnedIcons()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LearnedFile)!);
            File.WriteAllText(LearnedFile, JsonSerializer.Serialize(LearnedIcons, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>Learned icons need the character index (from the costume name) for the in-game replacement.</summary>
    private static void ApplyLearnedIcons()
    {
        if (_getCostumeName == null || _extra == null) return;
        for (int i = 0; i < _extra.Count; i++)
        {
            string costume = _getCostumeName.Invoke(null, new object[] { i, false }) as string ?? string.Empty;
            if (costume.Length > 0 && LearnedIcons.TryGetValue(costume, out List<string>? names))
                foreach (string name in names) _iconOwners[name] = i;
        }
    }


    // ------------------------------------------------------------ skin switch icons: apply

    private static bool _settingIcon;
    private static readonly Dictionary<string, (long Stamp, Sprite Sprite)> IconCache = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Image -> the game's own icon sprite and the Image's own size (to put back / to grow from).</summary>
    private static readonly Dictionary<IntPtr, (Sprite Sprite, Vector2 Size)> GameIcons = new();

    /// <summary>The character page listed a character's skins: learn / export / replace their switch icons.</summary>
    private static void RefreshSkinItemsPostfix(PnlRoleSkinContainer __instance) => RefreshContainer(__instance, log: true);

    private static void RefreshContainer(PnlRoleSkinContainer container, bool log)
    {
        try
        {
            int index = container.m_CurrentCharacterIndex;
            var items = container.m_SkinItems;
            if (index < 0 || items == null || _getCostumeName == null) return;
            string costume = _getCostumeName.Invoke(null, new object[] { index, false }) as string ?? string.Empty;
            if (costume.Length == 0 || SfSkinFormat.IsCensoredName(costume)) return;
            if (!LearnedIcons.TryGetValue(costume, out List<string>? names)) LearnedIcons[costume] = names = new List<string>();
            bool changed = false;
            string folder = Path.Combine(SkinForgerPaths.Creator, costume);
            int count = Math.Min(items.Count, container.m_CurrentSkinList?.Count ?? items.Count);
            for (int i = 0; i < count; i++)
            {
                if (items[i] == null || !items[i].gameObject.activeSelf) continue;
                Image? image = items[i].m_ImgSkin;
                if (image == null) continue;
                Sprite? game = GameSprite(image);
                if (game == null || string.IsNullOrEmpty(game.name)) continue;
                string name = game.name;
                if (HexiePolicy.IsHexieName(name) || HexiePolicy.IsHexieName(game.texture?.name)) continue;
                if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) { names.Add(name); changed = true; }
                _iconOwners[name] = index;
                // Export the game's icon next to the dumped character (only a character that was dumped; never overwrite).
                string file = Path.Combine(folder, IconFolder, name + ".png");
                if (Directory.Exists(folder) && !File.Exists(file) && SpritePng(game) is { } png)
                {
                    WriteIcon(file, png);
                    MelonLogger.Msg($"{Prefix} {costume}: skin switch icon {name} exported to {IconFolder} ({game.rect.width}x{game.rect.height})");
                }
                ApplyIcon(image, game, index);
            }
            if (changed)
            {
                SaveLearnedIcons();
                if (log) MelonLogger.Msg($"{Prefix} {costume}: skin switch icons [{string.Join(", ", names)}]");
            }
        }
        catch (Exception exception)
        {
            if (Logged.Add("refresh:" + exception.GetType().Name)) MelonLogger.Warning($"{Prefix} skin switch icons skipped: {exception.GetBaseException().Message}");
        }
    }

    /// <summary>The game's own sprite of this Image (also when SkinForger's icon is showing).</summary>
    private static Sprite? GameSprite(Image image)
    {
        Sprite? current = image.sprite;
        if (current != null && !IsOurs(current)) return current;
        return GameIcons.TryGetValue(image.Pointer, out var game) && game.Sprite != null && game.Sprite.Pointer != IntPtr.Zero ? game.Sprite : current;
    }

    private static bool IsOurs(Sprite sprite) => sprite.texture != null && (sprite.texture.name ?? string.Empty).EndsWith("(SkinForger)", StringComparison.Ordinal);

    /// <summary>Shows the selected skin's Icon PNG (or the game icon again). A PNG bigger or smaller than the game
    /// icon keeps the game's pixel scale: the Image grows / shrinks around its pivot ("expansion" like body parts).</summary>
    private static void ApplyIcon(Image image, Sprite game, int index)
    {
        RectTransform rt = image.rectTransform;
        if (!GameIcons.TryGetValue(image.Pointer, out var known) || known.Sprite == null || known.Sprite.Pointer != game.Pointer)
            GameIcons[image.Pointer] = known = (game, known.Sprite != null && known.Sprite.Pointer != IntPtr.Zero ? known.Size : rt.rect.size);
        Sprite? replacement = Replacement(index, game.name, game);
        Sprite show = replacement ?? game;
        Vector2 size = known.Size;
        if (replacement != null)
        {
            float gw = Math.Max(1f, game.rect.width), gh = Math.Max(1f, game.rect.height);
            size = new Vector2(known.Size.x * replacement.rect.width / gw, known.Size.y * replacement.rect.height / gh);
        }
        _settingIcon = true;
        try
        {
            if (image.sprite == null || image.sprite.Pointer != show.Pointer) image.sprite = show;
            if (Math.Abs(rt.rect.width - size.x) > 0.5f) rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, size.x);
            if (Math.Abs(rt.rect.height - size.y) > 0.5f) rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, size.y);
        }
        finally { _settingIcon = false; }
    }

    /// <summary>The game put a sprite on an Image: a skin icon with a PNG in the selected skin's Icon folder is swapped.</summary>
    private static void ImageSpritePostfix(Image __instance, Sprite value)
    {
        if (_settingIcon || value == null || _iconOwners.Count == 0) return;
        try
        {
            string name = value.name;
            if (string.IsNullOrEmpty(name) || IsOurs(value) || !_iconOwners.TryGetValue(name, out int index) || HexiePolicy.IsHexieName(name)) return;
            ApplyIcon(__instance, value, index);
        }
        catch { }
    }

    private static Sprite? Replacement(int index, string name, Sprite original)
    {
        if (_getSkinPath == null || _getSelected == null) return null;
        int selected = _getSelected.Invoke(null, new object[] { index, false }) is int s ? s : -1;
        if (selected < 0) return null;
        string skin = _getSkinPath.Invoke(null, new object[] { index, selected, false }) as string ?? string.Empty;
        if (skin.Length == 0) return null;
        string file = Path.Combine(skin, IconFolder, name + ".png");
        if (!VirtualFiles.FileExists(file) && VirtualFiles.FileExists(Path.Combine(skin, LegacyIconFolder, name + ".png")))
            file = Path.Combine(skin, LegacyIconFolder, name + ".png");
        if (!VirtualFiles.FileExists(file))
        {
            if (Logged.Add("noicon:" + file)) MelonLogger.Msg($"{Prefix} skin icon {name}: the selected skin has no {IconFolder}\\{name}.png ({skin})");
            return null;
        }
        // Package files have no write time: their content is the stamp (an edited Root icon remounted under the same path).
        byte[]? content = VirtualFiles.IsVirtual(file) ? VirtualFiles.ReadAllBytes(file) : null;
        long stamp = content != null ? ContentStamp(content) : File.GetLastWriteTimeUtc(file).Ticks;
        if (IconCache.TryGetValue(file, out var cached) && cached.Stamp == stamp && cached.Sprite != null && cached.Sprite.Pointer != IntPtr.Zero)
            return cached.Sprite;
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!ImageConversion.LoadImage(texture, content ?? File.ReadAllBytes(file))) return null;
        texture.name = name + " (SkinForger)";
        texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
        // The game's pixel scale; centred like the game sprite (pivot as a fraction).
        Rect rect = original.rect;
        Vector2 pivot = new(original.pivot.x / Math.Max(1f, rect.width), original.pivot.y / Math.Max(1f, rect.height));
        Sprite sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), pivot, original.pixelsPerUnit, 0, SpriteMeshType.FullRect);
        sprite.name = name;   // keeps being recognised when the game hands it on
        sprite.hideFlags = HideFlags.DontUnloadUnusedAsset;
        IconCache[file] = (stamp, sprite);
        MelonLogger.Msg($"{Prefix} skin icon {name} from {file} ({texture.width}x{texture.height}, game {rect.width}x{rect.height})");
        return sprite;
    }

    /// <summary>Writes an exported icon through Main's ReplacedBackup (its baseline: no "changed" backup on the next
    /// Reload) and records it as the game image (EditDetector: not edited until the creator changes it).</summary>
    private static void WriteIcon(string file, byte[] png)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        if (_writeResource != null) _writeResource.Invoke(null, new object[] { file, png });
        else File.WriteAllBytes(file, png);
        EditDetector.RecordIconOriginal(file, png);
    }

    /// <summary>
    /// Whether this image's folder keeps every anchor in ONE file (anchors.json) instead of one JSON per
    /// image. Cat skins do: they are frame-by-frame, hundreds of pictures, and the anchor is the only thing
    /// they need written down (user, 2026-11-20: "all the images in one folder share a single JSON").
    /// Core's AutoAnchor asks here before it writes an anchor.
    /// </summary>
    internal static bool KeepsAnchorsInOneFile(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        // The caller may hand over an IMAGE (...\<atlas>\part.png) or the ATLAS FOLDER itself
        // (...\<atlas>). 2026-11-21: Main's Dump asks with the folder while this only looked at the
        // parent, so it compared "Bassist" against the atlas names, answered no, and the cat folder got
        // 52 per-image JSONs all over again. Both shapes are answered now.
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string last = Path.GetFileName(trimmed);
        if (!string.IsNullOrEmpty(last) && CatAtlases.Contains(last)) return true;
        string parent = Path.GetFileName(Path.GetDirectoryName(trimmed) ?? string.Empty);
        return !string.IsNullOrEmpty(parent) && CatAtlases.Contains(parent);
    }

    /// <summary>
    /// Main's WriteAnchorMetadataIfMissing(imagePath, attachment, region, textureName). Main writes into the
    /// folder's one anchors.json when that file is there, and one JSON per image when it is not -- so for a
    /// cat skin the file is CREATED here first, empty, and Main then puts the anchor inside it. That is the
    /// whole of the cat-skin rule now: not "no anchor file" (2026-11-15 .. 2026-11-20, which is what let a
    /// part drift), but "one anchor file for the folder".
    /// </summary>
    private static bool AnchorSidecarPrefix(string __0, string __3)
    {
        try
        {
            if (!KeepsAnchorsInOneFile(__0) && !(!string.IsNullOrEmpty(__3) && CatAtlases.Contains(__3))) return true;
            string folder = Path.GetDirectoryName(__0) ?? string.Empty;
            if (folder.Length == 0 || VirtualFiles.IsVirtual(__0)) return true;
            string file = Path.Combine(folder, "anchors.json");
            if (File.Exists(file) || File.Exists(Path.ChangeExtension(__0, ".json"))) return true;
            Directory.CreateDirectory(folder);
            File.WriteAllText(file,
                "{\r\n" +
                "  \"version\": 2,\r\n" +
                "  \"origin\": \"top-left\",\r\n" +
                "  \"canvasAnchor\": \"center\",\r\n" +
                "  \"_howToPinAPart\": \"Enlarged a canvas? Put the pixels of room you added on the left and on the top " +
                "into that part's entry, as \\\"extraLeft\\\" and \\\"extraTop\\\" (or here, for every part). They are read " +
                "on every load, so changing them moves the part.\",\r\n" +
                "  \"parts\": {}\r\n" +
                "}\r\n");
            global::SkinForger.FastReload.FastReloadMod.NoteWrittenBySkinForger(file);
            MelonLogger.Msg($"{Prefix} {Path.GetFileName(folder)}: one anchors.json for the whole folder (frame-by-frame skin)");
        }
        catch (Exception exception) { MelonLogger.Warning($"{Prefix} anchors.json for {Path.GetFileName(__0)}: {exception.GetBaseException().Message}"); }
        return true;   // Main writes the anchor itself, now into the file that exists
    }

    // ------------------------------------------------------------ Preview: switch between a character's skins

    private static Type? _preview;
    private static MethodInfo? _loadContexts;
    private static readonly Dictionary<int, int> PreviewSkin = new();

    private static int InstallPreview(HarmonyLib.Harmony harmony)
    {
        int patched = 0;
        try
        {
            _preview = AccessTools.TypeByName("SkinForger.Preview.PreviewManager");
            if (_preview == null) return 0;
            _loadContexts = AccessTools.DeclaredMethod(_preview, "LoadContextsAndPreview");
            MethodInfo? hostString = AccessTools.DeclaredMethod(_preview, "HostString");
            MethodInfo? drawCharacter = AccessTools.DeclaredMethod(_preview, "DrawCharacterSelector");
            if (hostString != null) { harmony.Patch(hostString, prefix: new HarmonyMethod(typeof(RoleSkins), nameof(PreviewHostStringPrefix))); patched++; }
            if (drawCharacter != null) { harmony.Patch(drawCharacter, postfix: new HarmonyMethod(typeof(RoleSkins), nameof(PreviewSkinRow))); patched++; }
            if (_loadContexts != null) { harmony.Patch(_loadContexts, postfix: new HarmonyMethod(typeof(RoleSkins), nameof(PreviewContextsPostfix))); patched++; }
        }
        catch (Exception exception) { MelonLogger.Warning($"{Prefix} Preview skin switch not added: {exception.GetBaseException().Message}"); }
        return patched;
    }

    private static int PreviewCharacterId()
    {
        if (_preview == null) return -1;
        var ids = AccessTools.Field(_preview, "CharacterIds")?.GetValue(null) as System.Collections.IList;
        int index = AccessTools.Field(_preview, "_characterIndex")?.GetValue(null) is int i ? i : -1;
        return ids != null && index >= 0 && index < ids.Count && ids[index] is int id ? id : -1;
    }

    /// <summary>The show prefab of the skin chosen in Preview's Skin row (null = the character's own).</summary>
    internal static string? PreviewShow(int id, string field)
    {
        if (!PreviewSkin.TryGetValue(id, out int skin) || skin <= 0 || skin >= SkinCount(id)) return null;
        return _skinShows[id][skin].TryGetValue(field, out string? show) ? show : null;
    }

    private static int SkinCount(int id) => id >= 0 && id < _skinShows.Count ? _skinShows[id].Count : 0;

    /// <summary>PreviewManager.HostString(ConfigString, "character", id, field): the selected skin's show prefab.</summary>
    private static bool PreviewHostStringPrefix(MethodInfo? method, object[] args, ref string __result)
    {
        try
        {
            if (method?.Name != "ConfigString" || args == null || args.Length < 3 || args[0] as string != "character" || args[1] is not int id || args[2] is not string field) return true;
            if (!PreviewSkin.TryGetValue(id, out int skin) || skin <= 0 || skin >= SkinCount(id)) return true;
            if (!_skinShows[id][skin].TryGetValue(field, out string? show)) return true;
            __result = show;
            return false;
        }
        catch { return true; }
    }

    /// <summary>"Skin  &lt;  2 / 2  &gt;" row under the Character row when the character has more than one skin.</summary>
    private static void PreviewSkinRow()
    {
        try
        {
            EnsureLoaded();
            int id = PreviewCharacterId();
            int count = SkinCount(id);
            if (count <= 1) return;
            int skin = PreviewSkin.TryGetValue(id, out int s) ? Math.Clamp(s, 0, count - 1) : 0;
            GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
            GUILayout.Label("Skin", GUILayout.Width(90f));
            int step = 0;
            if (GUILayout.Button("<", GUILayout.Width(34f))) step = -1;
            GUILayout.Label($"{skin + 1} / {count}", GUILayout.Width(60f));
            if (GUILayout.Button(">", GUILayout.Width(34f))) step = 1;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (step == 0) return;
            PreviewSkin[id] = (skin + step + count) % count;
            _loadContexts?.Invoke(null, null);
        }
        catch (Exception exception)
        {
            if (Logged.Add("previewrow")) MelonLogger.Warning($"{Prefix} Preview skin row failed: {exception.GetBaseException().Message}");
        }
    }

    /// <summary>After the Preview loaded a character: atlases of other skins only use edited images there too.</summary>
    private static void PreviewContextsPostfix()
    {
        try
        {
            DiagnosePreviewSkin();
            if (_preview == null || _owners.Count == 0) return;
            var resources = AccessTools.Field(_preview, "ContextResources")?.GetValue(null) as System.Collections.IList;
            var templates = AccessTools.Field(_preview, "ContextTemplates")?.GetValue(null) as System.Collections.IList;
            if (resources == null || templates == null) return;
            for (int i = 0; i < resources.Count && i < templates.Count; i++)
            {
                string resource = (resources[i] as string ?? string.Empty).ToLowerInvariant();
                if (!_owners.Any(o => resource.Contains(o.Name)) || templates[i] is not SkeletonRenderer template) continue;
                List<string> atlases = AtlasesOf(template.gameObject);
                if (_catPrefabs.Any(resource.Contains)) foreach (string atlas in atlases) CatAtlases.Add(atlas);
            }
        }
        catch { }
    }

    /// <summary>
    /// 2026-11-15: Preview showed "Unavailable" for Bassist's cat skin and logged nothing (Preview skips a prefab
    /// silently when it has no usable SkeletonRenderer). Logs what the selected skin's show prefabs contain, once per
    /// prefab, so the cat preview can be built on the right components.
    /// </summary>
    private static void DiagnosePreviewSkin()
    {
        int id = PreviewCharacterId();
        if (id < 0 || !PreviewSkin.TryGetValue(id, out int skin) || skin <= 0 || skin >= SkinCount(id)) return;
        var contexts = AccessTools.Field(_preview, "ContextNames")?.GetValue(null) as System.Collections.IList;
        ResourcesManager? manager = ResourcesManager.instance;
        foreach (var pair in _skinShows[id][skin])
        {
            if (!Logged.Add("previewdiag:" + pair.Value)) continue;
            string report;
            try
            {
                GameObject? prefab = manager?.LoadFromName<GameObject>(pair.Value);
                if (prefab == null) report = "resource not found";
                else
                {
                    var renderers = prefab.GetComponentsInChildren<SkeletonRenderer>(true);
                    var graphics = prefab.GetComponentsInChildren<SkeletonGraphic>(true);
                    var parts = new List<string>();
                    foreach (SkeletonRenderer r in renderers)
                        parts.Add($"{r.GetIl2CppType().Name}:{r.gameObject.name}(data={(r.skeletonDataAsset != null ? r.skeletonDataAsset.name : "none")},anims={r.skeletonDataAsset?.GetSkeletonData(true)?.Animations?.Count ?? -1})");
                    foreach (SkeletonGraphic g in graphics)
                        parts.Add($"SkeletonGraphic:{g.gameObject.name}(data={(g.skeletonDataAsset != null ? g.skeletonDataAsset.name : "none")})");
                    report = $"renderers={renderers.Length} graphics={graphics.Length} [{string.Join("; ", parts.Take(6))}]";
                }
            }
            catch (Exception exception) { report = "error " + exception.GetBaseException().Message; }
            MelonLogger.Msg($"{Prefix} Preview skin {skin + 1}: {pair.Key}={pair.Value}: {report} (contexts loaded: {contexts?.Count ?? -1})");
        }
    }

    private static long ContentStamp(byte[] data)
    {
        unchecked
        {
            long hash = (long)14695981039346656037UL;
            foreach (byte b in data) hash = (hash ^ b) * 1099511628211L;
            return hash ^ data.Length;
        }
    }

    // ------------------------------------------------------------ Reload

    /// <summary>Main's ReloadAssets prefix: edits on disk count again (icons, other-skin edit checks).</summary>
    private static void ReloadPrefix()
    {
        IconCache.Clear();
        EditedCache.Clear();
        AtlasEdits.Clear();
    }

    /// <summary>FastReload's incremental Reload (Main's ReloadAssets is not called): same as a full Reload for icons.</summary>
    internal static void AfterReload()
    {
        LoadCaches.Clear();
        ReloadPrefix();
        ReloadPostfix();
    }

    /// <summary>Main's ReloadAssets postfix: the character page on screen shows the new icons at once.</summary>
    private static void ReloadPostfix()
    {
        try
        {
            foreach (PnlRoleSkinContainer container in UnityEngine.Object.FindObjectsOfType<PnlRoleSkinContainer>())
                RefreshContainer(container, log: false);
        }
        catch { }
    }

    // ------------------------------------------------------------ other-skin atlases: only edited images

    private static readonly HashSet<string> OtherSkinAtlases = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (long Stamp, bool Edited)> EditedCache = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>"skin|atlas" -> does that atlas folder hold an edited image (until the next Reload).</summary>
    private static readonly Dictionary<string, bool> AtlasEdits = new(StringComparer.OrdinalIgnoreCase);
    private static int _skippedUntouched, _skippedObjects;

    /// <summary>Texture names of a skeleton object (its atlas pages).</summary>
    private static List<string> AtlasesOf(GameObject obj)
    {
        var names = new List<string>();
        try
        {
            SkeletonDataAsset? data = obj.GetComponent<SkeletonRenderer>()?.skeletonDataAsset ?? obj.GetComponent<SkeletonGraphic>()?.skeletonDataAsset;
            var assets = data?.atlasAssets;
            if (assets == null) return names;
            foreach (AtlasAssetBase? asset in assets)
            {
                var materials = asset?.TryCast<SpineAtlasAsset>()?.materials;
                if (materials == null) continue;
                foreach (Material? material in materials)
                {
                    string? texture = material?.mainTexture?.name;
                    if (string.IsNullOrEmpty(texture)) continue;
                    names.Add(texture);
                    if (OtherSkinAtlases.Add(texture)) MelonLogger.Msg($"{Prefix} atlas {texture} is another skin's: only edited images of it are used");
                }
            }
        }
        catch { }
        return names;
    }

    private static readonly HashSet<string> BaselineLogged = new(StringComparer.OrdinalIgnoreCase);

    private static bool IsEdited(string png, string root)
    {
        // 2026-11-18: say it out loud when a folder has no recorded baseline. Without one the edit rules
        // cannot tell an untouched dump from an image edited at its original size, so every image in the
        // folder is used — and the log should show that rather than leave it looking like a coincidence.
        string atlasFolder = Path.GetDirectoryName(png) ?? root;
        if (BaselineLogged.Add(atlasFolder) && !EditDetector.HasBaseline(atlasFolder))
            MelonLogger.Msg($"{Prefix} {Path.GetFileName(atlasFolder)}: no recorded baseline for this atlas folder, " +
                            "so every image in it is treated as the creator's work");

        long stamp = File.GetLastWriteTimeUtc(png).Ticks;
        if (!EditedCache.TryGetValue(png, out var known) || known.Stamp != stamp)
            EditedCache[png] = known = (stamp, EditDetector.IsEditedImage(png, root));
        return known.Edited;
    }

    /// <summary>Does the selected skin of this character hold an edited image for this atlas?</summary>
    private static bool AtlasEdited(int index, string atlas)
    {
        if (_getSkinPath == null || _getSelected == null) return true;
        int selected = _getSelected.Invoke(null, new object[] { index, false }) is int s ? s : -1;
        if (selected < 0) return false;
        string skin = _getSkinPath.Invoke(null, new object[] { index, selected, false }) as string ?? string.Empty;
        if (skin.Length == 0) return false;
        string key = skin + "|" + atlas;
        if (AtlasEdits.TryGetValue(key, out bool known)) return known;
        string folder = Path.Combine(skin, atlas);
        bool edited;
        if (VirtualFiles.IsVirtual(folder)) edited = VirtualFiles.DirectoryExists(folder);   // packages hold edited images only
        else edited = Directory.Exists(folder) && Directory.GetFiles(folder, "*.png").Any(png => IsEdited(png, skin));
        AtlasEdits[key] = edited;
        return edited;
    }

    /// <summary>Main's GetExistingRegionPath(skin, attachment, region, textureName): for other-skin atlases an
    /// untouched dumped PNG counts as absent (the game image is kept, no rebuild for it).</summary>
    private static void RegionPathPostfix(string __0, string __3, ref string __result)
    {
        if (string.IsNullOrEmpty(__result) || string.IsNullOrEmpty(__3) || !OtherSkinAtlases.Contains(__3)) return;
        try
        {
            if (VirtualFiles.IsVirtual(__result) || !File.Exists(__result) || !Directory.Exists(__0)) return;
            if (IsEdited(__result, __0)) return;
            __result = null!;
            _skippedUntouched++;
        }
        catch { }
    }

    /// <summary>Main's GetRootSkinDataDirectories(costumeFolder): the Icon folder is part of the Root skin, not a
    /// skin of its own (2026-11-15: "Icon" appeared as a skin button next to Root).</summary>
    private static void RootDataPostfix(HashSet<string> __result)
    {
        try { __result?.Add(IconFolder); __result?.Add(LegacyIconFolder); } catch { }
    }

    // ------------------------------------------------------------ recognise

    /// <summary>Main's Scan(GameObject, elfin): an object of another skin belongs to its character. When the selected
    /// skin has no edited image for any of its atlases, it is left to the game (-1): no SkinForger work at all
    /// (2026-11-15: an untouched cat battle object still cost 170-200 ms each).</summary>
    private static void ScanPostfix(GameObject obj, bool elfin, ref int __result)
    {
        if (elfin || obj == null || !EnsureLoaded() || _owners.Count == 0) return;
        try
        {
            string text = obj.name ?? string.Empty;
            if (text == "default" && obj.transform != null && obj.transform.parent != null) text = obj.transform.parent.name;
            text = text.Replace("(Clone)", string.Empty).ToLowerInvariant();
            if (HexiePolicy.IsHexieName(text)) return;
            foreach ((string name, int index) in _owners)
            {
                if (!text.Contains(name)) continue;
                List<string> atlases = AtlasesOf(obj);
                if (_catPrefabs.Contains(name)) foreach (string atlas in atlases) CatAtlases.Add(atlas);
                if (!DumpIsolation.Active && atlases.Count > 0 && !atlases.Any(a => AtlasEdited(index, a)))
                {
                    if (_skippedObjects++ < 10) MelonLogger.Msg($"{Prefix} {text}: no edited image in [{string.Join(", ", atlases)}], left to the game");
                    __result = -1;
                    return;
                }
                if (__result != index && Logged.Add(text))
                    MelonLogger.Msg($"{Prefix} {text} belongs to character #{index} (other skin){(__result >= 0 ? $", Main had #{__result}" : "")}");
                __result = index;
                return;
            }
        }
        catch { }
    }
}
