using System.Text.RegularExpressions;
using HarmonyLib;
using MelonLoader;

namespace SkinForger.SkinPackages;

/// <summary>One character sound of the game (voice line or character BGM).</summary>
internal sealed record SoundEntry(string Name, string Category, string Extension);

/// <summary>All sounds of one character (costume): the game folder (char_3_maid_girl), its costume name (Maid) and
/// costume index (-1 when no costume uses it).</summary>
internal sealed class SoundCharacter
{
    public string GameFolder = string.Empty;   // char_3_maid_girl
    public string Key = string.Empty;          // maid_girl
    public string Costume = string.Empty;      // Maid (folder name in SkinCreator\Sounds)
    public int Index = -1;
    public List<SoundEntry> Entries = new();
}

/// <summary>
/// The game's character sounds (2026-11-17, voice replacement), read from the Addressables catalog
/// (StreamingAssets\aa\catalog.json): every "Assets/Static Resources/Data/Characters/&lt;char&gt;/audio/.../&lt;Name&gt;.wav|ogg"
/// (1149 on 6.8.0: 962 wav, 187 ogg). The clip is played by its asset name (VoiceMaidMain01, char_3_maid_bgm).
/// Censored (_hexie) sounds are never listed (Steam only). Characters are matched to costumes by the costume's
/// "mainShow" (maid_girl_main_show -> maid_girl = char_3_maid_girl without "char_3_").
/// </summary>
internal static class SoundCatalog
{
    private const string Prefix = "[SkinForge][Sounds]";
    private static volatile List<SoundCharacter>? _raw;
    private static List<SoundCharacter>? _characters;
    private static bool _started;
    private static Dictionary<string, (SoundCharacter Character, SoundEntry Entry)>? _byName;

    internal static bool Ready => _characters != null;
    internal static IReadOnlyList<SoundCharacter> Characters => _characters ?? (IReadOnlyList<SoundCharacter>)Array.Empty<SoundCharacter>();

    internal static (SoundCharacter Character, SoundEntry Entry)? Find(string name)
    {
        if (_byName == null || string.IsNullOrEmpty(name)) return null;
        return _byName.TryGetValue(name, out var found) ? found : null;
    }

    /// <summary>Starts reading the catalog (background) and, on the main thread once it is read, matches costumes.</summary>
    internal static void Poll()
    {
        if (!_started)
        {
            _started = true;
            Task.Run(Read);
        }
        if (_characters == null && _raw is { } raw) Match(raw);
    }

    private static void Read()
    {
        try
        {
            string catalog = Path.Combine(MelonLoader.Utils.MelonEnvironment.GameRootDirectory, "MuseDash_Data", "StreamingAssets", "aa", "catalog.json");
            string text = File.ReadAllText(catalog);
            var regex = new Regex(@"""Assets/Static Resources/Data/Characters/([^/""]+)/audio/([^""]+)\.(wav|ogg|mp3)""", RegexOptions.IgnoreCase);
            var byFolder = new Dictionary<string, SoundCharacter>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in regex.Matches(text))
            {
                string folder = m.Groups[1].Value, rest = m.Groups[2].Value, ext = "." + m.Groups[3].Value.ToLowerInvariant();
                string name = rest.Contains('/') ? rest[(rest.LastIndexOf('/') + 1)..] : rest;
                if (name.Contains("_hexie", StringComparison.OrdinalIgnoreCase) || global::SkinForger.HexieBlock.HexiePolicy.IsHexieName(name)) continue;
                if (!seen.Add(folder + "|" + name)) continue;
                string sub = rest.Contains('/') ? rest[..rest.LastIndexOf('/')].ToLowerInvariant() : string.Empty;
                if (!byFolder.TryGetValue(folder, out SoundCharacter? character))
                    byFolder[folder] = character = new SoundCharacter { GameFolder = folder, Key = Regex.Replace(folder, @"^char_\d+_", string.Empty) };
                character.Entries.Add(new SoundEntry(name, Category(sub, name), ext));
            }
            foreach (SoundCharacter c in byFolder.Values)
                c.Entries = c.Entries.OrderBy(e => Array.IndexOf(Order, e.Category)).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
            _raw = byFolder.Values.ToList();
            MelonLogger.Msg($"{Prefix} catalog: {_raw.Count} characters, {_raw.Sum(c => c.Entries.Count)} sounds");
        }
        catch (Exception exception)
        {
            _raw = new List<SoundCharacter>();
            MelonLogger.Warning($"{Prefix} catalog not read: {exception.GetBaseException().Message}");
        }
    }

    internal static readonly string[] Order = { "Apply", "Start", "Main", "Hidden", "Festival", "Hurt", "EmptyHit", "Lost", "Victory", "Transform", "Other", "BGM" };

    private static string Category(string sub, string name)
    {
        if (sub.Length == 0 && name.EndsWith("_bgm", StringComparison.OrdinalIgnoreCase)) return "BGM";
        // Some characters keep their voices directly in audio\ with no subfolder at all — the muscle skin's
        // nine VoiceMuscleRinMain* are like that (2026-11-17). With nothing to read the kind from, they all
        // landed in "Other"; the name says it just as well.
        if (sub.Length == 0) return CategoryOfName(name);
        if (sub.Contains("hidden") || sub.Contains("hideen")) return "Hidden";
        if (sub.Contains("interactive")) return "Main";
        if (sub.Contains("festival")) return "Festival";
        if (sub.Contains("start")) return "Start";
        if (sub.Contains("fail")) return "Lost";
        if (sub.Contains("victory")) return "Victory";
        if (sub.Contains("hurt")) return "Hurt";
        if (sub.Contains("apply")) return "Apply";
        if (sub.Contains("transform")) return "Transform";
        return "Other";
    }

    /// <summary>The kind of a sound read from its own name, for the characters that use no subfolders.</summary>
    private static string CategoryOfName(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.EndsWith("_bgm")) return "BGM";
        if (n.Contains("hidden") || n.Contains("hideen")) return "Hidden";
        if (n.Contains("festival")) return "Festival";
        if (n.Contains("apply")) return "Apply";
        if (n.Contains("start")) return "Start";
        if (n.Contains("lost") || n.Contains("fail")) return "Lost";
        if (n.Contains("victory")) return "Victory";
        if (n.Contains("hurt")) return "Hurt";
        if (n.Contains("transform")) return "Transform";
        if (n.Contains("emptyhit") || n.Contains("empty_hit")) return "EmptyHit";
        if (n.Contains("main") || n.Contains("interactive")) return "Main";
        return "Other";
    }

    /// <summary>Main thread: costume names / indexes from Main's config (GetCount, GetCostumeName, ConfigString).</summary>
    private static void Match(List<SoundCharacter> raw)
    {
        try
        {
            Type host = AccessTools.TypeByName("SkinForger.SkinForgerMod")!;
            var getCount = AccessTools.DeclaredMethod(host, "GetCount", new[] { typeof(string) });
            var getName = AccessTools.DeclaredMethod(host, "GetCostumeName", new[] { typeof(int), typeof(bool) });
            var config = AccessTools.DeclaredMethod(host, "ConfigString", new[] { typeof(string), typeof(int), typeof(string) });
            int count = getCount?.Invoke(null, new object[] { "character" }) is int n ? n : 0;
            if (count <= 0) return;   // config not ready yet: try again next frame
            var byKey = raw.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
            var result = new List<SoundCharacter>();
            for (int i = 0; i < count; i++)
            {
                string costume = getName?.Invoke(null, new object[] { i, false }) as string ?? string.Empty;
                string show = config?.Invoke(null, new object[] { "character", i, "mainShow" }) as string ?? string.Empty;
                string key = show.EndsWith("_main_show", StringComparison.OrdinalIgnoreCase) ? show[..^"_main_show".Length] : show;
                if (costume.Length == 0 || SfSkinFormat.IsCensoredName(costume) || !byKey.TryGetValue(key, out SoundCharacter? c) || c.Index >= 0) continue;
                c.Costume = SafeFolder(costume);
                c.Index = i;
                result.Add(c);
            }
            // A character's OTHER SKINS have their own sound folder, and no costume row names it: Bassist's
            // muscle skin keeps nine VoiceMuscleRin* in char_1_musclerin_girl, which matched nothing and was
            // listed on its own at the bottom (2026-11-17, user: the festival voice is not in the list).
            // The costume is found the same way RoleSkins finds a skin's images: through every show prefab
            // the character declares, its skins' included.
            // Festival voices are a config concept of their own: a character's foolishExpressions (April
            // Fools) and the festival voice tables name them, and nothing in the file path says so. A voice
            // the config calls festival is shown as Festival wherever its file happens to live.
            HashSet<string> festival = FestivalVoiceNames();
            if (festival.Count > 0)
                foreach (SoundCharacter c in raw)
                    for (int e = 0; e < c.Entries.Count; e++)
                        if (festival.Contains(c.Entries[e].Name) && c.Entries[e].Category != "Festival")
                            c.Entries[e] = c.Entries[e] with { Category = "Festival" };

            Dictionary<string, int> skinOwners = SkinFolderOwners();
            var byIndex = result.Where(c => c.Index >= 0).GroupBy(c => c.Index).ToDictionary(g => g.Key, g => g.First());
            foreach (SoundCharacter c in raw.Where(c => c.Index < 0))
            {
                string core = c.Key;
                foreach (string suffix in new[] { "_girl", "_boy" })
                    if (core.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) core = core[..^suffix.Length];

                if (skinOwners.TryGetValue(core, out int owner) && byIndex.TryGetValue(owner, out SoundCharacter? target))
                {
                    int added = 0;
                    foreach (SoundEntry e in c.Entries)
                        if (!target.Entries.Any(x => x.Name.Equals(e.Name, StringComparison.OrdinalIgnoreCase)))
                        {
                            target.Entries.Add(e);
                            added++;
                        }
                    target.Entries = target.Entries.OrderBy(e => Array.IndexOf(Order, e.Category))
                                                   .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
                    MelonLogger.Msg($"{Prefix} {c.GameFolder}: {added} sounds of another skin listed under {target.Costume}");
                    continue;
                }

                c.Costume = SafeFolder(c.Key);
                result.Add(c);   // sounds no costume row uses (kept at the end, by game folder name)
            }
            AddVoiceSetting(result);
            var byName = new Dictionary<string, (SoundCharacter, SoundEntry)>(StringComparer.OrdinalIgnoreCase);
            foreach (SoundCharacter c in result) foreach (SoundEntry e in c.Entries) byName.TryAdd(e.Name, (c, e));
            _byName = byName;
            _characters = result;
            MelonLogger.Msg($"{Prefix} {result.Count(c => c.Index >= 0)} costumes matched, {result.Count(c => c.Index < 0)} sound folders without a costume");
        }
        catch (Exception exception)
        {
            _characters = raw;
            MelonLogger.Warning($"{Prefix} costume match failed: {exception.GetBaseException().Message}");
        }
    }

    /// <summary>
    /// Voice names the character config marks as festival ones: foolishExpressions (April Fools) and any
    /// other property whose name speaks of a festival. The expressions are objects, so the search walks
    /// into them and keeps every string that names a voice.
    /// Empty while the config is not loaded; then the category simply stays what the file path said.
    /// </summary>
    private static HashSet<string> FestivalVoiceNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? json = global::SkinForger.GameConfigJson.Text("character");
        if (string.IsNullOrWhiteSpace(json)) return names;
        try
        {
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(json!);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return names;
            foreach (System.Text.Json.JsonElement row in document.RootElement.EnumerateArray())
            {
                if (row.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                foreach (System.Text.Json.JsonProperty property in row.EnumerateObject())
                {
                    if (property.Name.IndexOf("foolish", StringComparison.OrdinalIgnoreCase) < 0 &&
                        property.Name.IndexOf("festival", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    Walk(property.Value, names);
                }
            }
        }
        catch (System.Text.Json.JsonException exception)
        {
            MelonLogger.Warning($"{Prefix} festival voices could not be read: {exception.Message}");
        }
        if (names.Count > 0) MelonLogger.Msg($"{Prefix} {names.Count} festival / April Fools voices named by the config");
        return names;

        static void Walk(System.Text.Json.JsonElement element, HashSet<string> into)
        {
            switch (element.ValueKind)
            {
                case System.Text.Json.JsonValueKind.String:
                    string value = (element.GetString() ?? string.Empty).Trim();
                    if (value.StartsWith("Voice", StringComparison.OrdinalIgnoreCase) &&
                        !global::SkinForger.HexieBlock.HexiePolicy.IsHexieName(value)) into.Add(value);
                    break;
                case System.Text.Json.JsonValueKind.Array:
                    foreach (System.Text.Json.JsonElement item in element.EnumerateArray()) Walk(item, into);
                    break;
                case System.Text.Json.JsonValueKind.Object:
                    foreach (System.Text.Json.JsonProperty child in element.EnumerateObject()) Walk(child.Value, into);
                    break;
            }
        }
    }

    /// <summary>Words that say nothing about WHICH character a show prefab belongs to.</summary>
    private static readonly HashSet<string> GenericShowWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "show", "main", "battle", "sub", "victory", "fail", "girl", "boy", "cat", "hexie", "char", "role", "skin"
    };

    /// <summary>
    /// Name part of a show prefab -> the costume index that declares it, its skins included. "1_musclerin_show"
    /// gives "musclerin", which is how the sound folder char_1_musclerin_girl finds its way to Bassist.
    /// Empty while the game's config is not loaded yet; a word two costumes share is left with the first.
    /// </summary>
    private static Dictionary<string, int> SkinFolderOwners()
    {
        var owners = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string? json = global::SkinForger.GameConfigJson.Text("character");
        if (string.IsNullOrWhiteSpace(json)) return owners;
        try
        {
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(json!);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return owners;
            int index = 0;
            foreach (System.Text.Json.JsonElement row in document.RootElement.EnumerateArray())
            {
                int here = index++;
                if (row.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                Collect(row, here);
                if (!row.TryGetProperty("skins", out System.Text.Json.JsonElement skins) ||
                    skins.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                foreach (System.Text.Json.JsonElement skin in skins.EnumerateArray())
                {
                    if (skin.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                    if (skin.TryGetProperty("hexie", out System.Text.Json.JsonElement hexie) &&
                        hexie.ValueKind == System.Text.Json.JsonValueKind.True) continue;
                    Collect(skin, here);
                }
            }
        }
        catch (System.Text.Json.JsonException exception)
        {
            MelonLogger.Warning($"{Prefix} character config could not be read for skin sounds: {exception.Message}");
        }
        return owners;

        void Collect(System.Text.Json.JsonElement row, int costume)
        {
            foreach (System.Text.Json.JsonProperty property in row.EnumerateObject())
            {
                if (property.Value.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                if (!property.Name.EndsWith("show", StringComparison.OrdinalIgnoreCase)) continue;
                string value = property.Value.GetString() ?? string.Empty;
                if (SfSkinFormat.IsCensoredName(value)) continue;
                foreach (string word in value.Split('_', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (word.All(char.IsDigit) || GenericShowWords.Contains(word)) continue;
                    owners.TryAdd(word, costume);
                }
            }
        }
    }

    /// <summary>
    /// The game's VoiceSetting (in-song voices: Apply / Start / Victory / Fail / Hurt / EmptyHit per character index,
    /// incl. cat-form names). Names the catalog does not list for that character are added (e.g. empty-hit sounds).
    /// </summary>
    private static void AddVoiceSetting(List<SoundCharacter> characters)
    {
        try
        {
            var setting = Il2CppAssets.Scripts.PeroTools.Commons.SingletonScriptableObject<Il2CppAssets.Scripts.GameCore.Managers.VoiceSetting>.instance;
            if (setting == null) return;
            int added = 0;
            void Add(Il2CppSystem.Collections.Generic.List<Il2CppAssets.Scripts.GameCore.Managers.VoiceSetting.Voice>? voices, string category)
            {
                for (int i = 0; voices != null && i < voices.Count; i++)
                {
                    var voice = voices[i];
                    if (voice == null) continue;
                    SoundCharacter? c = characters.FirstOrDefault(x => x.Index == voice.index);
                    if (c == null) continue;
                    foreach (var names in new[] { voice.voiceNames, voice.catNames })
                        for (int n = 0; names != null && n < names.Count; n++)
                        {
                            string name = names[n];
                            if (string.IsNullOrEmpty(name) || name.Contains("_hexie", StringComparison.OrdinalIgnoreCase) ||
                                c.Entries.Any(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                            c.Entries.Add(new SoundEntry(name, category, ".wav"));
                            added++;
                        }
                }
            }
            Add(setting.m_ApplyVoices, "Apply");
            Add(setting.m_StartVoices, "Start");
            Add(setting.m_VictoryVoices, "Victory");
            Add(setting.m_FailVoices, "Lost");
            Add(setting.m_HurtVoices, "Hurt");
            Add(setting.m_EmptyHits, "EmptyHit");
            foreach (SoundCharacter c in characters)
                c.Entries = c.Entries.OrderBy(e => Array.IndexOf(Order, e.Category)).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
            MelonLogger.Msg($"{Prefix} VoiceSetting: {added} in-song sound name(s) added");
        }
        catch (Exception exception) { MelonLogger.Warning($"{Prefix} VoiceSetting not read: {exception.GetBaseException().Message}"); }
    }

    internal static string SafeFolder(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim().TrimEnd('.');
    }
}
