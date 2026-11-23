using System.Reflection;
using HarmonyLib;
using Il2CppAssets.Scripts.Database;
using Il2CppAssets.Scripts.PeroTools.Commons;
using Il2CppAssets.Scripts.PeroTools.Managers;
using Il2CppNewtonsoft.Json.Linq;
using MelonLoader;

namespace SkinForger.SkinPackages;

/// <summary>
/// Game-side text replacement. Edits from SkinCreator\Text are written into the game's own text data:
///  - the JSON tables ConfigManager caches (GetJson postfix): character_&lt;Language&gt;, elfin_&lt;Language&gt;, the tips table;
///  - the typed tables (DBConfigLocalCharacter / DBConfigLocalElfin / DBConfigLocalLoadingTips, Deserialize postfix).
/// Every text field listed in <see cref="TextFields"/> is visited the same way (string, list of strings, list of lists,
/// list of { skinName }). A place is changed only if the game's own text there is the entry's Original
/// (TextOverrides.Desired); TextWriteLog keeps the game text so changed / removed edits restore it.
/// Characters / elfins are identified by their English name (character_English cosName / elfin_English name).
/// </summary>
internal static class TextHooks
{
    private const string Prefix = "[SkinForge][Text]";

    private static readonly TextWriteLog Log = new();
    private static readonly Dictionary<IntPtr, int> AppliedVersion = new();
    private static readonly List<Il2CppSystem.Object> TypedTables = new();
    private static readonly Dictionary<string, List<string>> EnglishNames = new(StringComparer.Ordinal);
    private static string _tipsBase = string.Empty;
    private static bool _busy, _objectsDirty;
    private static readonly HashSet<string> Logged = new();
    private static readonly HashSet<string> LoadedConfigNames = new(StringComparer.Ordinal);

    internal static int Install(HarmonyLib.Harmony harmony)
    {
        int patched = 0;
        void Post(MethodInfo? method, string postfix)
        {
            if (method == null) { MelonLogger.Warning($"{Prefix} hook target missing for {postfix}"); return; }
            harmony.Patch(method, postfix: new HarmonyMethod(typeof(TextHooks), postfix));
            patched++;
        }
        Post(AccessTools.Method(typeof(ConfigManager), nameof(ConfigManager.GetJson), new[] { typeof(string), typeof(bool) }), nameof(GetJsonPostfix));
        Post(AccessTools.Method(typeof(DBConfigLocalCharacter), nameof(DBConfigLocalCharacter.Deserialize)), nameof(TypedDeserializePostfix));
        Post(AccessTools.Method(typeof(DBConfigLocalElfin), nameof(DBConfigLocalElfin.Deserialize)), nameof(TypedDeserializePostfix));
        Post(AccessTools.Method(typeof(DBConfigLocalLoadingTips), nameof(DBConfigLocalLoadingTips.Deserialize)), nameof(TypedDeserializePostfix));
        Post(AccessTools.Method(typeof(DBConfigLocalCharacterFestivalVoice), nameof(DBConfigLocalCharacterFestivalVoice.Deserialize)), nameof(TypedDeserializePostfix));
        void Pre(MethodInfo? method, string prefix)
        {
            if (method == null) { MelonLogger.Warning($"{Prefix} hook target missing for {prefix}"); return; }
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(TextHooks), prefix));
            patched++;
        }
        // 2026-11-22: the very first loading screen still showed the game's own tip. The tips table is the one
        // table we never see Deserialize -- the game reads it before our hooks exist -- so it was only picked up
        // by the config sweep, which runs every 5 s and got there 2.3 s too late (log: label set 12:03:27.888,
        // table found 12:03:30.199). LoadingScene.OnEnable asks GetInfoByIndex for the exact line it is about to
        // put on screen, so the replacement is done inside that call. Nothing can read a tip earlier than the
        // call that fetches it, which is why this is a cure and not another race.
        Pre(AccessTools.Method(typeof(DBConfigLocalLoadingTips), nameof(DBConfigLocalLoadingTips.GetInfoByIndex), new[] { typeof(int) }), nameof(TipsReadPrefix));
        Pre(AccessTools.Method(typeof(DBConfigLocalLoadingTips), nameof(DBConfigLocalLoadingTips.GetAllTips)), nameof(TipsReadPrefix));
        MethodInfo? load = AccessTools.Method(typeof(ConfigManager), nameof(ConfigManager.LoadConfigFile));
        if (load != null) { harmony.Patch(load, prefix: new HarmonyMethod(typeof(TextHooks), nameof(LoadConfigPrefix))); patched++; }
        // 2026-11-15: renaming the English elfin "Lilith" broke her skin. Main finds a skin folder by the English
        // name it reads from elfin_English / character_English, the same table the text edits write into. Main (and
        // every Core module, which all ask Main's GetCostumeName) must keep seeing the game's own English names.
        MethodInfo? costumeName = AccessTools.TypeByName("SkinForger.SkinForgerMod") is { } host
            ? AccessTools.DeclaredMethod(host, "GetCostumeName", new[] { typeof(int), typeof(bool) }) : null;
        if (costumeName != null) { harmony.Patch(costumeName, postfix: new HarmonyMethod(typeof(TextHooks), nameof(CostumeNamePostfix)) { priority = HarmonyLib.Priority.First }); patched++; }
        else MelonLogger.Warning($"{Prefix} SkinForgerMod.GetCostumeName not found: renaming a character or elfin in English can break its skin");
        return patched;
    }

    internal static string? CurrentFolder => TextStore.FolderFor(SafeLanguage());

    private static string? SafeLanguage() { try { return DataHelper.userLanguage; } catch { return null; } }

    private static ConfigManager? Config { get { try { return Singleton<ConfigManager>.instance; } catch { return null; } } }

    // ------------------------------------------------------------ table names

    /// <summary>Records which config name the game loads for the loading tips (the name is not in s_TypeDic).</summary>
    private static void LoadConfigPrefix(string name, Il2CppSystem.Type type)
    {
        try
        {
            if (string.IsNullOrEmpty(name) || !LoadedConfigNames.Add(name)) return;
            string typeName = type?.Name ?? string.Empty;
            if (typeName == nameof(DBConfigLocalLoadingTips) && _tipsBase.Length == 0)
            {
                _tipsBase = StripLanguage(name);
                MelonLogger.Msg($"{Prefix} loading tips table: {name} (base {_tipsBase})");
            }
        }
        catch { }
    }

    private static string StripLanguage(string name)
    {
        foreach (string suffix in new[] { "_English", "_ChineseS", "_ChineseT", "_Japanese", "_Korean" })
            if (name.EndsWith(suffix, StringComparison.Ordinal)) return name[..^suffix.Length];
        return name;
    }

    /// <summary>Tips table base name: from LoadConfigFile, else any loaded table whose name contains "tip".</summary>
    private static string TipsBase()
    {
        DiscoverTips();
        if (_tipsBase.Length > 0) return _tipsBase;
        try
        {
            var names = new List<string>(LoadedConfigNames);
            if (Config?.m_Dictionary is { } cache) foreach (var pair in cache) names.Add(pair.Key);
            if (Config?.m_TextAssets is { } assets) foreach (var pair in assets) names.Add(pair.Key);
            string? found = names.Select(StripLanguage).FirstOrDefault(n => n.IndexOf("tip", StringComparison.OrdinalIgnoreCase) >= 0 && n.IndexOf("hide", StringComparison.OrdinalIgnoreCase) < 0);
            if (found != null) _tipsBase = found;
            if (Logged.Add("config-names"))
                MelonLogger.Msg($"{Prefix} config tables seen: {string.Join(", ", names.Distinct().OrderBy(n => n).Take(150))}");
        }
        catch { }
        return _tipsBase;
    }

    /// <summary>
    /// The game reads the loading tips at start-up, before SkinForger's hooks exist (2026-11-15 log: only "loading,
    /// unlockall, welcome" were seen), so they are taken from the game's config object: DBConfigLoadingTips keeps
    /// one DBConfigLocalLoadingTips per language in m_LocalDic.
    /// </summary>
    private static void DiscoverTips()
    {
        if (_tipsFound || UnityEngine.Time.realtimeSinceStartup < _nextTipsTry) return;
        // Start-up is exactly when this matters and exactly when five seconds is too long to wait, so the first
        // half-minute is looked at four times a second; after that a miss means the tips are elsewhere, not late.
        _nextTipsTry = UnityEngine.Time.realtimeSinceStartup + (_tipsTries++ < 120 ? 0.25f : 5f);
        try
        {
            ConfigManager? config = Config;
            if (config == null) return;
            BaseDBConfigObject? configObject = config.GetConfigObject(Il2CppInterop.Runtime.Il2CppType.Of<DBConfigLoadingTips>(), -1);
            DBConfigLoadingTips? tips = configObject?.TryCast<DBConfigLoadingTips>();
            if (tips?.m_LocalDic == null) return;
            var names = new List<string>();
            foreach (var pair in tips.m_LocalDic)
            {
                DBConfigLocalLoadingTips? local = pair.Value;
                if (local == null || local.Pointer == IntPtr.Zero) continue;
                string file = local.fileName ?? string.Empty;
                names.Add($"{pair.Key}:{file}");
                if (!TypedTables.Any(o => o.Pointer == local.Pointer)) { TypedTables.Add(local); _objectsDirty = true; }
                if (_tipsBase.Length == 0 && file.Length > 0) _tipsBase = StripLanguage(Path.GetFileNameWithoutExtension(file));
            }
            _tipsFound = names.Count > 0;
            if (_tipsFound) MelonLogger.Msg($"{Prefix} loading tips found in the game config: {string.Join(", ", names)} (table '{_tipsBase}')");
        }
        catch (Exception exception)
        {
            if (Logged.Add("tips-discover")) MelonLogger.Warning($"{Prefix} loading tips lookup failed: {exception.GetBaseException().Message}");
        }
    }

    private static bool _tipsFound;
    private static float _nextTipsTry;
    private static int _tipsTries;

    /// <summary>(table, text folder) of a GetJson request, or null when it is not one of ours.</summary>
    private static (string Table, string Folder)? Classify(string name, bool localization)
    {
        string baseName, language;
        if (localization) { baseName = name; language = SafeLanguage() ?? string.Empty; }
        else
        {
            int cut = name.LastIndexOf('_');
            if (cut <= 0) return null;
            baseName = name[..cut];
            language = name[(cut + 1)..];
        }
        string? table = baseName == "character" ? TextEntry.Characters : baseName == "elfin" ? TextEntry.Elfins
            : _tipsBase.Length > 0 && baseName == _tipsBase ? TextEntry.Tips : null;
        if (table == null) return null;
        string? folder = TextStore.FolderFor(language);
        return folder == null ? null : (table, folder);
    }

    /// <summary>Text folder of a localized festival table "characterFestivalVoice_&lt;Language&gt;", else null.</summary>
    private static string? FestivalFolder(string name, bool localization)
    {
        if (localization) return name == "characterFestivalVoice" ? TextStore.FolderFor(SafeLanguage() ?? string.Empty) : null;
        return name.StartsWith("characterFestivalVoice_", StringComparison.Ordinal) ? TextStore.FolderFor(name["characterFestivalVoice_".Length..]) : null;
    }

    // ------------------------------------------------------------ text slots

    /// <summary>One string in game data, readable and writable.</summary>
    private readonly record struct Slot(string Field, int Group, int Line, Func<string?> Get, Action<string> Set);

    /// <summary>A line of text, short enough to read in a log.</summary>
    private static string Short(string? text)
    {
        string value = (text ?? string.Empty).Replace("\n", " ").Trim();
        return value.Length <= 40 ? value : value[..40] + "...";
    }

    private const string FestivalKind = "festival";

    private static string[] FieldsOf(string table) =>
        table == TextEntry.Elfins ? TextFields.Elfin : table == TextEntry.Tips ? new[] { "tip" }
        : table == FestivalKind ? new[] { "expressions", "reviewExpressions" } : TextFields.Character;

    // ------------------------------------------------------------ voice <-> dialogue links (2026-11-17)

    internal readonly record struct VoiceLink(string Owner, string Field, int Group, int Line);
    private static Dictionary<string, VoiceLink>? _voiceLinks;
    private static float _nextLinkTry;

    /// <summary>
    /// Which dialogue line belongs to which voice: the game's CharacterInfo.&lt;field&gt;[group] is an Expression with
    /// audioNames[]; the localized LocalCharacterInfo.&lt;field&gt;[group][line] is its text, line i = audioNames[i].
    /// Festival rows the same (CharacterFestivalVoiceInfo.expressions = FestivalExpression : Expression).
    /// Voice name -> (owner, field as stored in SkinCreator\Text, group, line). Built once the config is loaded.
    /// </summary>
    internal static IReadOnlyDictionary<string, VoiceLink> VoiceLinks()
    {
        if (_voiceLinks != null) return _voiceLinks;
        if (UnityEngine.Time.realtimeSinceStartup < _nextLinkTry) return new Dictionary<string, VoiceLink>();
        _nextLinkTry = UnityEngine.Time.realtimeSinceStartup + 5f;
        var links = new Dictionary<string, VoiceLink>(StringComparer.OrdinalIgnoreCase);
        try
        {
            ConfigManager? config = Config;
            List<string> names = Names(TextEntry.Characters);
            DBConfigCharacter? characters = config?.GetConfigObject(Il2CppInterop.Runtime.Il2CppType.Of<DBConfigCharacter>(), -1)?.TryCast<DBConfigCharacter>();
            var list = characters?.list;
            if (list == null || names.Count == 0) return links;
            for (int i = 0; i < list.Count; i++)
            {
                CharacterInfo? info = list[i];
                if (info == null || i >= names.Count || names[i].Length == 0 || SfSkinFormat.IsCensoredName(names[i])) continue;
                void Add(Il2CppSystem.Collections.Generic.List<Expression>? expressions, string field)
                {
                    for (int g = 0; expressions != null && g < expressions.Count; g++)
                    {
                        var audio = expressions[g]?.audioNames;
                        for (int l = 0; audio != null && l < audio.Length; l++)
                            if (!string.IsNullOrEmpty(audio[l])) links.TryAdd(audio[l], new VoiceLink(names[i], field, g, l));
                    }
                }
                Add(info.expressions, "expressions");
                Add(info.reviewExpressions, "reviewExpressions");
                Add(info.catExpressions, "catExpressions");
                Add(info.foolishExpressions, "foolishExpressions");
                Add(info.autoExpressions, "autoExpressions");
            }
            List<(int Character, string Festival)> rows = FestivalRows();
            DBConfigCharacterFestivalVoice? festival = config?.GetConfigObject(Il2CppInterop.Runtime.Il2CppType.Of<DBConfigCharacterFestivalVoice>(), -1)?.TryCast<DBConfigCharacterFestivalVoice>();
            var festivalList = festival?.list;
            for (int r = 0; festivalList != null && r < festivalList.Count && r < rows.Count; r++)
            {
                if (rows[r].Character < 0 || rows[r].Character >= names.Count) continue;
                foreach ((var expressions, string field) in new[] { (festivalList[r]?.expressions, "expressions"), (festivalList[r]?.reviewExpressions, "reviewExpressions") })
                    for (int g = 0; expressions != null && g < expressions.Count; g++)
                    {
                        var audio = expressions[g]?.audioNames;
                        for (int l = 0; audio != null && l < audio.Length; l++)
                            if (!string.IsNullOrEmpty(audio[l]))
                                links.TryAdd(audio[l], new VoiceLink(names[rows[r].Character], TextFields.FestivalField(field, r, rows[r].Festival), g, l));
                    }
            }
            if (links.Count > 0)
            {
                _voiceLinks = links;
                MelonLogger.Msg($"{Prefix} {links.Count} voice lines linked to their dialogue text");
            }
        }
        catch (Exception exception)
        {
            if (Logged.Add("voice-links")) MelonLogger.Warning($"{Prefix} voice / dialogue links not read: {exception.GetBaseException().Message}");
        }
        return links;
    }

    /// <summary>The dialogue text entry of a voice in the current (or given) text language, or null.</summary>
    internal static TextEntry? TextOfVoice(string voice, string? language = null)
    {
        if (!VoiceLinks().TryGetValue(voice, out VoiceLink link)) return null;
        string folder = language ?? CurrentFolder ?? "CN";
        return TextRuntime.Of(folder).FirstOrDefault(e => e.Table == TextEntry.Characters && e.Owner == link.Owner &&
                                                          e.Field == link.Field && e.Group == link.Group && e.Line == link.Line);
    }

    /// <summary>The voice of a dialogue text entry, or null.</summary>
    internal static string? VoiceOfText(TextEntry entry)
    {
        foreach (var pair in VoiceLinks())
            if (pair.Value.Owner == entry.Owner && pair.Value.Field == entry.Field && pair.Value.Group == entry.Group && pair.Value.Line == entry.Line)
                return pair.Key;
        return null;
    }

    // ------------------------------------------------------------ festival dialogue (2026-11-17)

    private static List<(int Character, string Festival)>? _festivalRows;

    /// <summary>
    /// Owner and festival of each characterFestivalVoice row, from the game's typed table DBConfigCharacterFestivalVoice:
    /// "index" = character row, the expressions' dateFrom = the festival (Halloween 10-31, Christmas 12-24/25 ...).
    /// </summary>
    private static List<(int Character, string Festival)> FestivalRows()
    {
        if (_festivalRows is { Count: > 0 }) return _festivalRows;
        var rows = new List<(int, string)>();
        try
        {
            BaseDBConfigObject? configObject = Config?.GetConfigObject(Il2CppInterop.Runtime.Il2CppType.Of<DBConfigCharacterFestivalVoice>(), -1);
            DBConfigCharacterFestivalVoice? table = configObject?.TryCast<DBConfigCharacterFestivalVoice>();
            var list = table?.list;
            for (int i = 0; list != null && i < list.Count; i++)
            {
                CharacterFestivalVoiceInfo? info = list[i];
                string festival = "Festival";
                try
                {
                    var expressions = info?.expressions;
                    if (expressions != null && expressions.Count > 0 && expressions[0] != null)
                        festival = FestivalName(expressions[0].dateFrom.Month, expressions[0].dateFrom.Day);
                }
                catch { }
                rows.Add((info?.index ?? -1, festival));
            }
        }
        catch (Exception exception)
        {
            if (Logged.Add("festival-rows")) MelonLogger.Warning($"{Prefix} festival table not read: {exception.GetBaseException().Message}");
        }
        if (rows.Count > 0)
        {
            _festivalRows = rows;
            MelonLogger.Msg($"{Prefix} festival dialogue: {rows.Count} rows ({string.Join(", ", rows.GroupBy(r => r.Item2).Select(g => g.Key + " " + g.Count()))})");
        }
        return rows;
    }

    private static string FestivalName(int month, int day) => (month, day) switch
    {
        (10, >= 24) or (11, 1) => "Halloween",
        (12, >= 20) => "Christmas",
        (1, <= 7) or (12, 31) => "New Year",
        (2, >= 10 and <= 15) => "Valentine",
        (4, 1) => "April Fools",
        _ => $"{month:00}-{day:00}"
    };

    /// <summary>Owner (English costume name) and slots of festival row i; slot fields renamed (FestivalField).</summary>
    private static (string Owner, IEnumerable<Slot> Slots) FestivalSlots(int i, IEnumerable<Slot> slots)
    {
        List<(int Character, string Festival)> rows = FestivalRows();
        List<string> names = Names(TextEntry.Characters);
        if (i >= rows.Count || rows[i].Character < 0 || rows[i].Character >= names.Count) return (string.Empty, Enumerable.Empty<Slot>());
        string festival = rows[i].Festival;
        return (names[rows[i].Character], slots.Select(s => s with { Field = TextFields.FestivalField(s.Field, i, festival) }));
    }

    private static IEnumerable<Slot> JsonSlots(JObject row, string table)
    {
        foreach (string field in FieldsOf(table))
        {
            JToken? token = row[field];
            if (token == null) continue;
            string f = field;
            if (token.Type == JTokenType.String) { yield return new Slot(f, -1, -1, () => row[f]?.ToString(), v => row[f] = new JValue(v)); continue; }
            JArray? list = token.TryCast<JArray>();
            if (list == null) continue;
            for (int i = 0; i < list.Count; i++)
            {
                JToken? item = list[i];
                if (item == null) continue;
                int index = i;
                if (item.Type == JTokenType.String) { yield return new Slot(f, -1, index, () => list[index]?.ToString(), v => list[index] = new JValue(v)); continue; }
                JObject? obj = item.TryCast<JObject>();
                if (obj != null)
                {
                    if (obj["skinName"] is { Type: JTokenType.String })
                        yield return new Slot(f, -1, index, () => obj["skinName"]?.ToString(), v => obj["skinName"] = new JValue(v));
                    continue;
                }
                JArray? lines = item.TryCast<JArray>();
                for (int l = 0; lines != null && l < lines.Count; l++)
                {
                    int line = l;
                    if (lines[line] is { Type: JTokenType.String })
                        yield return new Slot(f, index, line, () => lines[line]?.ToString(), v => lines[line] = new JValue(v));
                }
            }
        }
    }

    /// <summary>The same fields on the game's typed objects (LocalCharacterInfo / LocalElfinInfo / LocalLoadingTipsInfo).</summary>
    private static IEnumerable<Slot> ObjectSlots(object info, string table)
    {
        Type type = info.GetType();
        foreach (string field in FieldsOf(table))
        {
            PropertyInfo? property = type.GetProperty(field);
            if (property == null) continue;
            object? value = property.GetValue(info);
            if (value == null) continue;
            string f = field;
            if (value is string) { yield return new Slot(f, -1, -1, () => property.GetValue(info) as string, v => property.SetValue(info, v)); continue; }
            if (!IsList(value, out int count)) continue;
            for (int i = 0; i < count; i++)
            {
                int index = i;
                object? item = ItemAt(value, index);
                if (item == null) continue;
                if (item is string) { yield return new Slot(f, -1, index, () => ItemAt(value, index) as string, v => SetItemAt(value, index, v)); continue; }
                PropertyInfo? skinName = item.GetType().GetProperty("skinName");
                if (skinName != null && skinName.PropertyType == typeof(string))
                { yield return new Slot(f, -1, index, () => skinName.GetValue(item) as string, v => skinName.SetValue(item, v)); continue; }
                if (!IsList(item, out int lineCount)) continue;
                for (int l = 0; l < lineCount; l++)
                {
                    int line = l;
                    if (ItemAt(item, line) is string)
                        yield return new Slot(f, index, line, () => ItemAt(item, line) as string, v => SetItemAt(item, line, v));
                }
            }
        }
    }

    private static bool IsList(object value, out int count)
    {
        count = 0;
        PropertyInfo? countProperty = value.GetType().GetProperty("Count");
        if (countProperty == null || value.GetType().GetProperty("Item") == null) return false;
        count = Convert.ToInt32(countProperty.GetValue(value));
        return true;
    }

    private static object? ItemAt(object list, int index) => list.GetType().GetProperty("Item")!.GetValue(list, new object[] { index });
    private static void SetItemAt(object list, int index, string value) => list.GetType().GetProperty("Item")!.SetValue(list, value, new object[] { index });

    // ------------------------------------------------------------ applying

    /// <summary>English names by row index (game values, never our edits): costume names / elfin names.</summary>
    /// <summary>Main's GetCostumeName(index, elfin): always the game's own English name, never an edited one.</summary>
    private static void CostumeNamePostfix(int __0, bool __1, ref string __result)
    {
        try
        {
            List<string> names = Names(__1 ? TextEntry.Elfins : TextEntry.Characters);
            if (__0 >= 0 && __0 < names.Count && names[__0].Length > 0 && names[__0] != __result)
            {
                if (Logged.Add($"identity:{__1}:{__0}"))
                    MelonLogger.Msg($"{Prefix} skin lookup keeps the game name \"{names[__0]}\" (text shows \"{__result}\")");
                __result = names[__0];
            }
        }
        catch { }
    }

    private static List<string> Names(string table)
    {
        if (table == TextEntry.Tips) return new List<string>();
        if (EnglishNames.TryGetValue(table, out List<string>? cached)) return cached;
        var names = new List<string>();
        string field = table == TextEntry.Elfins ? "name" : "cosName";
        bool busy = _busy;
        _busy = true;
        try
        {
            JArray? english = Config?.GetJson(table + "_English", false);
            for (int i = 0; english != null && i < english.Count; i++)
            {
                string current = english[i]?.TryCast<JObject>()?[field]?.ToString() ?? string.Empty;
                names.Add(Log.Baseline($"j|{english.Pointer}|{i}|{field}|-1|-1", current));
            }
        }
        finally { _busy = busy; }
        if (names.Count > 0) EnglishNames[table] = names;
        return names;
    }

    private static int Apply(string table, string folder, string placePrefix, int rows, Func<int, IEnumerable<Slot>> slotsOf)
    {
        TextOverrides overrides = TextRuntime.Overrides(folder);
        List<string> names = Names(table);
        int changed = 0;
        for (int i = 0; i < rows; i++)
        {
            string owner = table == TextEntry.Tips ? string.Empty : i < names.Count ? names[i] : string.Empty;
            IEnumerable<Slot> rowSlots = slotsOf(i);
            string textTable = table;
            if (table == FestivalKind) { (owner, rowSlots) = FestivalSlots(i, rowSlots); textTable = TextEntry.Characters; }
            if (table != TextEntry.Tips && (owner.Length == 0 || SfSkinFormat.IsCensoredName(owner))) continue;
            foreach (Slot slot in rowSlots)
            {
                string? current = slot.Get();
                if (current == null) continue;
                int line = table == TextEntry.Tips ? i : slot.Line;   // tips: one text per row
                string place = $"{placePrefix}|{i}|{slot.Field}|{slot.Group}|{slot.Line}";
                string baseline = Log.Baseline(place, current);
                string desired = overrides.Desired(textTable, owner, slot.Field, slot.Group, line, baseline);


                // A place SkinForger has no baseline for learns it HERE, from the game itself, the first
                // time it is seen. 2026-11-21: the baseline moved out of the author's files, so a folder
                // written by an older build (or one whose "Original" the author had typed over) arrives
                // with nothing to compare against -- and the text Dump would then treat every line as
                // untouched and overwrite the author's own words with the game's. Learning it here keeps
                // their work safe without them having to do anything.
                if (overrides.EntryAt(textTable, owner, slot.Field, slot.Group, line) is { } known &&
                    known.Original.Length == 0 && baseline.Length > 0)
                {
                    known.Original = baseline;
                    TextStore.TextBaseline.Set(known.Language, known.Key, baseline);
                    TextStore.TextBaseline.Flush();
                    if (Logged.Add("learned|" + place))
                        MelonLogger.Msg($"{Prefix} learned the game's own wording for {textTable} row {line}" +
                                        $"{(owner.Length > 0 ? " of " + owner : string.Empty)}, field {slot.Field}");
                    desired = overrides.Desired(textTable, owner, slot.Field, slot.Group, line, baseline);
                }

                // The edit is applied either way (2026-11-21); this only points out that the game's own
                // wording is no longer the "Original" in the file -- a game update, or an Original that was
                // edited by hand. Worth knowing, not worth refusing the edit over.
                if (overrides.EntryAt(textTable, owner, slot.Field, slot.Group, line) is { Edited: true } entry &&
                    entry.Original != baseline && Logged.Add("drifted|" + place))
                {
                    MelonLogger.Msg($"{Prefix} your edit applied, but the game's own wording has moved on " +
                                    $"({textTable} row {line}{(owner.Length > 0 ? " of " + owner : string.Empty)}, field {slot.Field}): " +
                                    $"the game says \"{Short(baseline)}\", your file's \"Original\" says \"{Short(entry.Original)}\". " +
                                    "Export the texts again if you want \"Original\" to match the game's copy.");
                }

                if (Log.Update(place, current, desired) is { } write) { slot.Set(write); changed++; }
            }
        }
        return changed;
    }

    private static void GetJsonPostfix(string name, bool localization, JArray __result)
    {
        if (_busy || __result == null) return;
        try
        {
            int version = TextRuntime.Version;
            if (AppliedVersion.TryGetValue(__result.Pointer, out int done) && done == version) return;
            if (Classify(name, localization) is not { } table)
            {
                if (FestivalFolder(name, localization) is { } festivalFolder) table = (FestivalKind, festivalFolder);
                else { AppliedVersion[__result.Pointer] = version; return; }
            }
            _busy = true;
            JArray rows = __result;
            int changed = Apply(table.Table, table.Folder, $"j|{rows.Pointer}", rows.Count,
                i => rows[i]?.TryCast<JObject>() is { } row ? JsonSlots(row, table.Table) : Enumerable.Empty<Slot>());
            AppliedVersion[__result.Pointer] = version;
            if (changed > 0) MelonLogger.Msg($"{Prefix} {name}{(localization ? " (current language)" : "")}: {changed} text(s) replaced");
        }
        catch (Exception exception)
        {
            if (Logged.Add("json:" + name)) MelonLogger.Warning($"{Prefix} table {name} skipped: {exception.GetBaseException().Message}");
        }
        finally { _busy = false; }
    }

    private static void TypedDeserializePostfix(Il2CppSystem.Object __instance)
    {
        if (__instance == null) return;
        TypedTables.RemoveAll(o => o.Pointer == __instance.Pointer || o.Pointer == IntPtr.Zero);
        TypedTables.Add(__instance);
        _objectsDirty = true;

        // Apply NOW, not on the next frame. Whoever just deserialised this table is usually about to read
        // it in the same frame: the elfin page built its labels from the original strings and only showed
        // the author's text the SECOND time it was opened, because the replacement landed 174 ms later
        // (2026-11-20 log: page at 11:41:48.551, "typed tables: 2 replaced" at 11:41:48.725).
        //
        // Update() keeps its own guards -- it does nothing until the language and the folder are known,
        // and leaves _objectsDirty set so the next frame tries again -- so calling it early can only make
        // the replacement happen sooner, never twice and never wrongly.
        try { Update(); }
        catch (Exception exception)
        {
            if (Logged.Add("typed-now:" + exception.GetType().Name))
                MelonLogger.Warning($"{Prefix} typed table not applied at load: {exception.GetBaseException().Message}");
        }
    }

    /// <summary>
    /// The game is about to read a loading tip. Register the table and replace its text first, in this call.
    ///
    /// Called from LoadingScene.OnEnable (GetInfoByIndex, one line) and from GetAllTips. Both are rare, and
    /// Update() does nothing once the table has been applied, so the standing cost is a pointer comparison.
    /// </summary>
    private static void TipsReadPrefix(Il2CppSystem.Object __instance)
    {
        if (__instance == null || __instance.Pointer == IntPtr.Zero || _busy) return;
        try
        {
            if (!TypedTables.Any(o => o.Pointer == __instance.Pointer))
            {
                TypedTables.Add(__instance);
                _objectsDirty = true;
                _tipsFound = true;
                MelonLogger.Msg($"{Prefix} loading tips table reached through the game's own read: replacing before the line is shown");
            }
            Update();
        }
        catch (Exception exception)
        {
            if (Logged.Add("tips-read:" + exception.GetType().Name))
                MelonLogger.Warning($"{Prefix} loading tip not replaced in time: {exception.GetBaseException().Message}");
        }
    }

    private static int _objectsVersion = -1;

    /// <summary>Every frame from the module; does work only after a table was loaded or the texts changed.</summary>
    internal static void Update()
    {
        if (!_tipsFound) DiscoverTips();
        if (!_objectsDirty && _objectsVersion == TextRuntime.Version) return;
        if (Config == null) return;
        string? folder = CurrentFolder;
        _objectsVersion = TextRuntime.Version;
        if (folder == null)
        {
            _objectsDirty = string.IsNullOrEmpty(SafeLanguage());   // language not known yet: try again next frame
            return;
        }
        _objectsDirty = false;
        TipsBase();
        int changed = 0;
        foreach (Il2CppSystem.Object table in TypedTables.ToList())
        {
            try
            {
                (string kind, object? list) = Typed(table);
                if (list == null || !IsList(list, out int rows)) continue;
                changed += Apply(kind, folder, $"o|{table.Pointer}", rows, i => ItemAt(list, i) is { } info ? ObjectSlots(info, kind) : Enumerable.Empty<Slot>());
            }
            catch (Exception exception)
            {
                if (Logged.Add("typed:" + exception.GetType().Name)) MelonLogger.Warning($"{Prefix} typed table skipped: {exception.GetBaseException().Message}");
            }
        }
        if (changed > 0) MelonLogger.Msg($"{Prefix} typed tables: {changed} text(s) replaced");
    }

    private static (string Kind, object? List) Typed(Il2CppSystem.Object table)
    {
        if (table.TryCast<DBConfigLocalCharacter>() is { } characters) return (TextEntry.Characters, characters.list);
        if (table.TryCast<DBConfigLocalElfin>() is { } elfins) return (TextEntry.Elfins, elfins.list);
        if (table.TryCast<DBConfigLocalLoadingTips>() is { } tips) return (TextEntry.Tips, tips.list);
        if (table.TryCast<DBConfigLocalCharacterFestivalVoice>() is { } festival) return (FestivalKind, festival.list);
        return (string.Empty, null);
    }

    // ------------------------------------------------------------ Dump Text

    /// <summary>Reads the game's own texts (CN = ChineseS, EN = English) and merges them into SkinCreator\Text.</summary>
    internal static string Dump()
    {
        ConfigManager? config = Config;
        if (config == null) return "The game is not ready yet. Try again on the main menu.";
        var report = new List<string>();
        bool busy = _busy;
        _busy = true;
        try
        {
            string tipsBase = TipsBase();
            foreach (string folder in TextStore.Languages)
            {
                string language = TextStore.GameLanguage(folder);
                var game = new List<TextEntry>();
                foreach (string table in new[] { TextEntry.Characters, TextEntry.Elfins, TextEntry.Tips })
                {
                    string tableName = table == TextEntry.Tips ? tipsBase : table;
                    JArray? rows = tableName.Length > 0 ? config.GetJson(tableName + "_" + language, false) : null;
                    if (rows != null)
                    {
                        Collect(game, folder, table, $"j|{rows.Pointer}", rows.Count,
                            i => rows[i]?.TryCast<JObject>() is { } row ? JsonSlots(row, table) : Enumerable.Empty<Slot>());
                        continue;
                    }
                    // No JSON table: use the typed table the game loaded for the current language.
                    Il2CppSystem.Object? typed = CurrentFolder == folder ? TypedTables.FirstOrDefault(t => Typed(t).Kind == table) : null;
                    if (typed != null && Typed(typed).List is { } list && IsList(list, out int count))
                    {
                        Collect(game, folder, table, $"o|{typed.Pointer}", count, i => ItemAt(list, i) is { } info ? ObjectSlots(info, table) : Enumerable.Empty<Slot>());
                        continue;
                    }
                    if (Logged.Add($"dump-{table}-{language}")) MelonLogger.Warning($"{Prefix} {table} texts for {language} not found (table '{tableName}')");
                }
                // Festival dialogue (2026-11-17)
                JArray? festivalRows = config.GetJson("characterFestivalVoice_" + language, false);
                if (festivalRows != null)
                    Collect(game, folder, FestivalKind, $"j|{festivalRows.Pointer}", festivalRows.Count,
                        i => festivalRows[i]?.TryCast<JObject>() is { } row ? JsonSlots(row, FestivalKind) : Enumerable.Empty<Slot>());
                else if (CurrentFolder == folder && TypedTables.FirstOrDefault(t => Typed(t).Kind == FestivalKind) is { } typedFestival &&
                         Typed(typedFestival).List is { } festivalList && IsList(festivalList, out int festivalCount))
                    Collect(game, folder, FestivalKind, $"o|{typedFestival.Pointer}", festivalCount,
                        i => ItemAt(festivalList, i) is { } info ? ObjectSlots(info, FestivalKind) : Enumerable.Empty<Slot>());
                else if (Logged.Add($"dump-festival-{language}")) MelonLogger.Warning($"{Prefix} festival texts for {language} not found");
                (int added, int updated) = TextRuntime.MergeAndSave(folder, game);
                report.Add($"{folder}: " + string.Join(", ", game.GroupBy(e => e.Category).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key}")) +
                           $" ({added} new, {updated} updated)");
            }
        }
        finally { _busy = busy; }
        string result = "Dumped to SkinCreator/Text.  " + string.Join("   ", report);
        MelonLogger.Msg($"{Prefix} {result}");
        return result;
    }

    private static void Collect(List<TextEntry> game, string folder, string table, string placePrefix, int rows, Func<int, IEnumerable<Slot>> slotsOf)
    {
        List<string> names = Names(table);
        for (int i = 0; i < rows; i++)
        {
            string owner = table == TextEntry.Tips ? string.Empty : i < names.Count ? names[i] : string.Empty;
            IEnumerable<Slot> rowSlots = slotsOf(i);
            string textTable = table;
            if (table == FestivalKind) { (owner, rowSlots) = FestivalSlots(i, rowSlots); textTable = TextEntry.Characters; }
            if (table != TextEntry.Tips && (owner.Length == 0 || SfSkinFormat.IsCensoredName(owner))) continue;
            foreach (Slot slot in rowSlots)
            {
                string? value = slot.Get();
                if (value == null || value.Length == 0) continue;
                string place = $"{placePrefix}|{i}|{slot.Field}|{slot.Group}|{slot.Line}";
                game.Add(new TextEntry
                {
                    Language = folder, Table = textTable, Owner = owner, Field = slot.Field,
                    Group = slot.Group, Line = table == TextEntry.Tips ? i : slot.Line,
                    Original = Log.Baseline(place, value)
                });
            }
        }
    }
}
