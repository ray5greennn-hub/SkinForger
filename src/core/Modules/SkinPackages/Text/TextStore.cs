using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SkinForger.SkinPackages;

/// <summary>
/// One editable game text: a place in a game table (table / owner / field / group / line).
/// Takes effect only when <see cref="Text"/> differs from <see cref="Original"/>.
/// </summary>
public sealed class TextEntry
{
    public const string Characters = "character", Elfins = "elfin", Tips = "tips";

    public string Language = "EN";          // folder: CN / EN
    public string Table = Characters;       // character / elfin / tips
    public string Owner = string.Empty;     // English costume / elfin name; empty for loading tips
    public string Field = string.Empty;     // game field: description, skill, cv, expressions, tip ...
    public int Group = -1, Line = -1;       // list index (Line) and list-of-lists index (Group); -1 = not a list
    public string Original = string.Empty;
    public string Text = string.Empty;

    public bool Edited => !string.Equals(Text, Original, StringComparison.Ordinal);

    public string Key => $"{Language}|{Table}|{Owner}|{Field}|{Group}|{Line}";

    public string Category => TextFields.Category(Table, Field);

    public string Title
    {
        get
        {
            if (Table == Tips) return $"Loading Tip {Line + 1}";
            string label = TextFields.Label(Field);
            string place = Group >= 0 ? $" {Group + 1}-{Line + 1}" : Line >= 0 ? $" {Line + 1}" : string.Empty;
            return $"{Owner} · {label}{place}";
        }
    }
}

/// <summary>The game fields SkinForger edits, and how they are shown.</summary>
public static class TextFields
{
    /// <summary>LocalCharacterInfo text fields (names as in the game's tables).</summary>
    public static readonly string[] Character =
    {
        "characterName", "cosName", "cosNames", "skinNames", "description", "chipName", "chipDescription",
        "skill", "skills", "thiefSkill", "cv", "cvs",
        "expressions", "autoExpressions", "reviewExpressions", "catExpressions", "foolishExpressions"
    };

    /// <summary>LocalElfinInfo text fields; "skins" is a list of { skinName }.</summary>
    public static readonly string[] Elfin = { "name", "skins", "description", "skill", "chipName", "chipDescription" };

    /// <summary>
    /// Festival dialogue (2026-11-17): the game's characterFestivalVoice table. Its texts are stored as character
    /// texts with Field "festivalExpressions#&lt;row&gt;:&lt;Festival&gt;" / "festivalReviewExpressions#&lt;row&gt;:&lt;Festival&gt;"
    /// (row = the table row, one per character and festival).
    /// </summary>
    public static string FestivalField(string field, int row, string festival) =>
        "festival" + char.ToUpperInvariant(field[0]) + field[1..] + "#" + row + ":" + festival;

    public static string Label(string field) => field.StartsWith("festival", StringComparison.Ordinal) && field.Contains('#')
        ? (field.StartsWith("festivalReview", StringComparison.Ordinal) ? "Festival Review Dialogue (" : "Festival Dialogue (") + field[(field.IndexOf(':') + 1)..] + ")"
        : LabelOf(field);

    private static string LabelOf(string field) => field switch
    {
        "characterName" => "Name",
        "cosName" => "Costume Name",
        "cosNames" => "Costume Name",
        "skinNames" => "Skin Name",
        "name" => "Name",
        "skins" => "Skin Name",
        "description" => "Description",
        "chipName" => "Card Name",
        "chipDescription" => "Card Description",
        "skill" => "Skill",
        "skills" => "Skill",
        "thiefSkill" => "Thief Skill",
        "cv" => "CV",
        "cvs" => "CV",
        "expressions" => "Dialogue",
        "autoExpressions" => "Idle Dialogue",
        "reviewExpressions" => "Review Dialogue",
        "catExpressions" => "Cat Dialogue",
        "foolishExpressions" => "April Fools Dialogue",
        "tip" => "Loading Tip",
        _ => field
    };

    public static string Category(string table, string field)
    {
        if (table == TextEntry.Tips) return "Loading Tips";
        return field switch
        {
            "characterName" or "cosName" or "cosNames" or "skinNames" or "name" or "skins" => "Names",
            "description" or "chipName" or "chipDescription" => "Descriptions",
            "skill" or "skills" or "thiefSkill" => "Skills",
            "cv" or "cvs" => "CV",
            _ when field.StartsWith("festival", StringComparison.Ordinal) && field.Contains('#') => "Festival",
            _ when field.EndsWith("xpressions", StringComparison.Ordinal) => "Dialogues",
            _ => "Other"
        };
    }
}

/// <summary>
/// SkinCreator\Text (pure .NET, testable outside the game).
///   Text\CN|EN\Characters\&lt;Costume&gt;.json   { "Costume": "...", "Texts": [ {Field, Group, Line, Original, Text} ] }
///   Text\CN|EN\Elfins\&lt;Elfin&gt;.json         { "Elfin": "...",   "Texts": [ ... ] }
///   Text\CN|EN\LoadingTips.json               { "Tips": [ {Index, Original, Text} ] }
///   Text\Fonts\CN|EN\*.ttf|*.otf
/// Every entry keeps the game's Original next to the creator's Text, so the files can be edited by hand and an
/// untouched entry (Text == Original) never replaces anything. Files of the first version (CharacterName /
/// CostumeName / Description / Dialogues) are still read.
/// </summary>
public static class TextStore
{
    public static readonly string[] Languages = { "CN", "EN" };

    public static string? FolderFor(string? gameLanguage) => gameLanguage switch
    {
        "ChineseS" => "CN",
        "English" => "EN",
        _ => null
    };

    public static string GameLanguage(string folder) => folder == "CN" ? "ChineseS" : "English";

    public static string CharactersDir(string root, string language) => Path.Combine(root, language, "Characters");
    public static string ElfinsDir(string root, string language) => Path.Combine(root, language, "Elfins");
    public static string TipsFile(string root, string language) => Path.Combine(root, language, "LoadingTips.json");
    public static string FontsDir(string root, string language) => Path.Combine(root, "Fonts", language);

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping   // keep Chinese readable
    };

    // ------------------------------------------------------------ the baseline, out of the author's way

    /// <summary>
    /// The game's own wording for each place, kept where the author never has to look at it.
    ///
    /// 2026-11-21, the author: "I want Original to disappear completely. There is no reason for it to be
    /// there -- someone else will open the file and think they have to edit both lines." He is right. The
    /// field had one honest job, telling an edited line from an untouched one, and a file the author edits
    /// is the wrong place for SkinForger's bookkeeping. It lives here now:
    ///
    ///     UserData\SkinForger\text-baseline\&lt;language&gt;.json      { "table|owner|field|group|line": "the game's words" }
    ///
    /// The text files themselves keep only what the author writes. Files that still carry "Original" are
    /// read as before (older folders, and packages made by older builds) and are rewritten without it.
    /// </summary>
    public static class TextBaseline
    {
        private static readonly Dictionary<string, Dictionary<string, string>> Loaded = new(StringComparer.OrdinalIgnoreCase);

        public static string Folder =>
            Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "SkinForger", "text-baseline");

        private static string FileOf(string language) => Path.Combine(Folder, language + ".json");

        private static Dictionary<string, string> Of(string language)
        {
            if (Loaded.TryGetValue(language, out Dictionary<string, string>? known)) return known;
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                string file = FileOf(language);
                if (File.Exists(file) && JsonNode.Parse(File.ReadAllText(file, Encoding.UTF8)) is JsonObject root)
                    foreach (KeyValuePair<string, JsonNode?> pair in root)
                        map[pair.Key] = pair.Value?.GetValue<string>() ?? string.Empty;
            }
            catch (Exception) { /* a damaged baseline costs nothing: it is rebuilt from the game */ }
            Loaded[language] = map;
            return map;
        }

        public static string Get(string language, string key) =>
            Of(language).TryGetValue(key, out string? text) ? text : string.Empty;

        public static void Set(string language, string key, string original)
        {
            Dictionary<string, string> map = Of(language);
            if (map.TryGetValue(key, out string? known) && known == original) return;
            map[key] = original;
            _dirty.Add(language);
        }

        private static readonly HashSet<string> _dirty = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Writes the languages whose baseline changed. Cheap and safe to call often.</summary>
        public static void Flush()
        {
            foreach (string language in _dirty.ToList())
            {
                try
                {
                    var root = new JsonObject();
                    foreach (KeyValuePair<string, string> pair in Of(language).OrderBy(p => p.Key, StringComparer.Ordinal))
                        root[pair.Key] = pair.Value;
                    Directory.CreateDirectory(Folder);
                    string file = FileOf(language);
                    string temporary = file + ".partial";
                    File.WriteAllText(temporary, root.ToJsonString(Pretty), new UTF8Encoding(false));
                    if (File.Exists(file)) File.Delete(file);
                    File.Move(temporary, file);
                }
                catch (Exception) { /* bookkeeping must never break a load */ }
                _dirty.Remove(language);
            }
        }
    }

    // ------------------------------------------------------------ reading

    public static List<TextEntry> Load(string root, string language)
    {
        var entries = new List<TextEntry>();
        foreach ((string dir, string table, string ownerKey) in new[] { (CharactersDir(root, language), TextEntry.Characters, "Costume"), (ElfinsDir(root, language), TextEntry.Elfins, "Elfin") })
            if (Directory.Exists(dir))
                foreach (string file in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    try { entries.AddRange(ReadOwner(file, language, table, ownerKey)); } catch { /* unreadable file: skipped */ }
        string tips = TipsFile(root, language);
        if (File.Exists(tips))
            try { entries.AddRange(ReadTips(tips, language)); } catch { }

        // "Original" is no longer written into the author's files. One that still has it (an older folder,
        // or a package from an older build) seeds the hidden baseline; everything else reads from there.
        foreach (TextEntry entry in entries)
        {
            if (entry.Original.Length > 0) TextBaseline.Set(language, entry.Key, entry.Original);
            else entry.Original = TextBaseline.Get(language, entry.Key);
        }
        TextBaseline.Flush();
        return entries;
    }

    private static IEnumerable<TextEntry> ReadOwner(string file, string language, string table, string ownerKey)
    {
        JsonNode root = JsonNode.Parse(File.ReadAllText(file, Encoding.UTF8))!;
        string owner = Str(root[ownerKey], Path.GetFileNameWithoutExtension(file));
        TextEntry Make(string field, int group, int line, JsonNode? pair) => new()
        {
            Language = language, Table = table, Owner = owner, Field = field, Group = group, Line = line,
            Original = Str(pair?["Original"]), Text = Str(pair?["Text"], Str(pair?["Original"]))
        };
        if (root["Texts"] is JsonArray texts)
            foreach (JsonNode? node in texts)
                if (node is JsonObject item && Str(item["Field"]) is { Length: > 0 } field)
                    yield return Make(field, Int(item["Group"], -1), Int(item["Line"], -1), item);
        // version 1 (2026-11-15 first build)
        foreach ((string old, string field) in new[] { ("CharacterName", "characterName"), ("CostumeName", "cosName"), ("Description", "description") })
            if (root[old] is JsonObject pair) yield return Make(field, -1, -1, pair);
        if (root["Dialogues"] is JsonArray dialogues)
            foreach (JsonNode? node in dialogues)
                if (node is JsonObject line) yield return Make("expressions", Int(line["Group"], 0), Int(line["Line"], 0), line);
    }

    private static IEnumerable<TextEntry> ReadTips(string file, string language)
    {
        JsonNode root = JsonNode.Parse(File.ReadAllText(file, Encoding.UTF8))!;
        if (root["Tips"] is JsonArray tips)
            foreach (JsonNode? node in tips)
                if (node is JsonObject tip)
                    yield return new TextEntry
                    {
                        Language = language, Table = TextEntry.Tips, Field = "tip", Line = Int(tip["Index"], 0),
                        Original = Str(tip["Original"]), Text = Str(tip["Text"], Str(tip["Original"]))
                    };
    }

    private static string Str(JsonNode? node, string fallback = "")
    {
        try { return node?.GetValue<string>() ?? fallback; } catch { return fallback; }
    }

    private static int Int(JsonNode? node, int fallback)
    {
        try { return node?.GetValue<int>() ?? fallback; } catch { return fallback; }
    }

    // ------------------------------------------------------------ writing

    /// <summary>The files of one language as (path relative to the Text folder, JSON). <paramref name="editedOnly"/>:
    /// only edited entries (skin package export).</summary>
    public static List<(string RelativePath, string Json)> Build(string language, IEnumerable<TextEntry> entries, bool editedOnly = false)
    {
        var files = new List<(string, string)>();
        List<TextEntry> mine = entries.Where(e => e.Language == language && (!editedOnly || e.Edited)).ToList();
        foreach ((string table, string ownerKey, string dir) in new[] { (TextEntry.Characters, "Costume", "Characters"), (TextEntry.Elfins, "Elfin", "Elfins") })
            foreach (IGrouping<string, TextEntry> owner in mine.Where(e => e.Table == table).GroupBy(e => e.Owner, StringComparer.OrdinalIgnoreCase))
            {
                var texts = new JsonArray();
                foreach (TextEntry e in owner.OrderBy(e => Array.IndexOf(table == TextEntry.Elfins ? TextFields.Elfin : TextFields.Character, e.Field)).ThenBy(e => e.Group).ThenBy(e => e.Line))
                    texts.Add(new JsonObject { ["Field"] = e.Field, ["Group"] = e.Group, ["Line"] = e.Line, ["Text"] = e.Text });
                files.Add((Path.Combine(language, dir, SafeFileName(owner.Key) + ".json"), new JsonObject { [ownerKey] = owner.Key, ["Texts"] = texts }.ToJsonString(Pretty)));
            }
        List<TextEntry> tips = mine.Where(e => e.Table == TextEntry.Tips).OrderBy(e => e.Line).ToList();
        if (tips.Count > 0)
        {
            var array = new JsonArray();
            foreach (TextEntry tip in tips) array.Add(new JsonObject { ["Index"] = tip.Line, ["Text"] = tip.Text });
            files.Add((Path.Combine(language, "LoadingTips.json"), new JsonObject { ["Language"] = language, ["Tips"] = array }.ToJsonString(Pretty)));
        }
        return files;
    }

    /// <summary>Writes the files of one language built from <paramref name="entries"/>.</summary>
    public static void Save(string root, string language, IEnumerable<TextEntry> entries)
    {
        foreach ((string relative, string json) in Build(language, entries))
            WriteAtomic(Path.Combine(root, relative), json);
    }

    /// <summary>Saves the one file that contains <paramref name="entry"/>.</summary>
    public static void SaveFileOf(string root, TextEntry entry, IEnumerable<TextEntry> all) =>
        Save(root, entry.Language, all.Where(e => e.Language == entry.Language && e.Table == entry.Table &&
            (entry.Table == TextEntry.Tips || e.Owner.Equals(entry.Owner, StringComparison.OrdinalIgnoreCase))));

    private static void WriteAtomic(string file, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temporary = file + ".partial";
        File.WriteAllText(temporary, json, new UTF8Encoding(false));
        File.Move(temporary, file, overwrite: true);
    }

    public static string SafeFileName(string name)
    {
        var text = new StringBuilder();
        foreach (char c in name) text.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
        return text.ToString().Trim().TrimEnd('.');
    }

    // ------------------------------------------------------------ dump merge

    /// <summary>
    /// Merges freshly read game texts into the existing entries of one language: new texts are added with
    /// Text = Original; existing entries get the current Original (an edit the creator made is kept).
    /// </summary>
    public static (int Added, int Updated) Merge(List<TextEntry> existing, IEnumerable<TextEntry> game)
    {
        var byKey = new Dictionary<string, TextEntry>(StringComparer.Ordinal);
        foreach (TextEntry e in existing) byKey[e.Key] = e;
        int added = 0, updated = 0;
        foreach (TextEntry fresh in game)
        {
            if (!byKey.TryGetValue(fresh.Key, out TextEntry? old))
            {
                fresh.Text = fresh.Original;
                existing.Add(fresh);
                byKey[fresh.Key] = fresh;
                added++;
                continue;
            }
            if (old.Original == fresh.Original) continue;
            if (!old.Edited) old.Text = fresh.Original;
            old.Original = fresh.Original;
            updated++;
        }
        foreach (TextEntry entry in existing)
            if (entry.Original.Length > 0) TextBaseline.Set(entry.Language, entry.Key, entry.Original);
        TextBaseline.Flush();
        return (added, updated);
    }

    /// <summary>Names, sizes and write times of every text and font file (changes when a file is edited by hand).</summary>
    public static string Signature(string root)
    {
        if (!Directory.Exists(root)) return string.Empty;
        var text = new StringBuilder();
        foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (file.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)) continue;
            var info = new FileInfo(file);
            text.Append(file).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
        }
        return text.ToString();
    }
}

/// <summary>The replacements of one language, looked up by the game-text hooks.</summary>
public sealed class TextOverrides
{
    public static readonly TextOverrides Empty = new(Array.Empty<TextEntry>());

    private readonly Dictionary<string, TextEntry> _byKey;

    public TextOverrides(IEnumerable<TextEntry> entries)
    {
        _byKey = new Dictionary<string, TextEntry>(StringComparer.Ordinal);
        foreach (TextEntry entry in entries) _byKey[KeyOf(entry.Table, entry.Owner, entry.Field, entry.Group, entry.Line)] = entry;
    }

    public int Count => _byKey.Values.Count(e => e.Edited);

    private static string KeyOf(string table, string owner, string field, int group, int line) =>
        $"{table}|{owner.ToUpperInvariant()}|{field}|{group}|{line}";

    /// <summary>
    /// What the game should show at one place, given <paramref name="baseline"/> = the game's own text there.
    ///
    /// An edit applies because the author made it. "Original" is the copy as EXPORTED, and its job is to
    /// tell an edited line from an untouched one (<see cref="TextEntry.Edited"/>) -- that is the only
    /// reason it is in the file.
    ///
    /// 2026-11-21, the author's question, and he is right: "why does Original have to match the game? if
    /// it is not mine to change, why is it in the file at all?" Until today it was also a GATE -- the edit
    /// was dropped, in silence, unless Original was word for word what the game says. One edited loading
    /// tip did nothing and said nothing. A rule nobody can see is not a safeguard, it is a trap.
    ///
    /// So the gate is gone: an edited line is written. When Original no longer matches the game's wording,
    /// the edit still applies and TextHooks says so in the log, which is how a game update that reworded a
    /// line should reach the author -- as a message, not as silence.
    /// </summary>
    /// <summary>The entry written down for one place, or null when the author never touched it.</summary>
    public TextEntry? EntryAt(string table, string owner, string field, int group, int line) =>
        _byKey.TryGetValue(KeyOf(table, owner, field, group, line), out TextEntry? entry) ? entry : null;

    public string Desired(string table, string owner, string field, int group, int line, string baseline)
    {
        if (!_byKey.TryGetValue(KeyOf(table, owner, field, group, line), out TextEntry? entry)) return baseline;
        return entry.Edited ? entry.Text : baseline;
    }
}

/// <summary>
/// Remembers, per place in game data, the game's own text and what SkinForger wrote there, so an edit can be changed
/// or undone later (pure .NET).
/// </summary>
public sealed class TextWriteLog
{
    private readonly Dictionary<string, (string Original, string Written)> _places = new(StringComparer.Ordinal);

    /// <summary>The game's own text at <paramref name="place"/> (undoing what SkinForger wrote).</summary>
    public string Baseline(string place, string current) =>
        _places.TryGetValue(place, out var entry) && entry.Written == current ? entry.Original : current;

    /// <summary>Returns the value to write, or null when <paramref name="current"/> is already right.</summary>
    public string? Update(string place, string current, string desired)
    {
        string baseline = Baseline(place, current);
        if (desired == baseline) _places.Remove(place); else _places[place] = (baseline, desired);
        return desired == current ? null : desired;
    }
}
