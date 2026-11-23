using MelonLoader;

namespace SkinForger.SkinPackages;

/// <summary>
/// The creator's texts in memory (SkinCreator\Text), shared by the Text page and the game hooks.
/// <see cref="Version"/> changes whenever a text changes (Save in the page, Dump, or a file edited by hand), which
/// makes the hooks re-apply. Files are re-read when their names / sizes / times change (checked every 2 s).
/// </summary>
internal static class TextRuntime
{
    private static readonly Dictionary<string, List<TextEntry>> Entries = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, TextOverrides> OverridesByLanguage = new(StringComparer.Ordinal);
    private static string _signature = "\u0001";
    private static float _nextCheck;

    internal static int Version { get; private set; }

    internal static string Root => SkinForgerPaths.Text;

    internal static IReadOnlyList<TextEntry> Of(string language)
    {
        EnsureLoaded();
        return Entries.TryGetValue(language, out List<TextEntry>? list) ? list : new List<TextEntry>();
    }

    internal static TextOverrides Overrides(string language)
    {
        EnsureLoaded();
        return OverridesByLanguage.TryGetValue(language, out TextOverrides? overrides) ? overrides : TextOverrides.Empty;
    }

    private static void EnsureLoaded()
    {
        if (_signature != "\u0001") return;
        Reload();
    }

    private static void Reload()
    {
        try
        {
            _signature = TextStore.Signature(Root);
            foreach (string language in TextStore.Languages)
            {
                List<TextEntry> list = TextStore.Load(Root, language);
                Entries[language] = list;
                OverridesByLanguage[language] = new TextOverrides(list);
            }
            Version++;
            int edited = Entries.Values.Sum(l => l.Count(e => e.Edited));
            MelonLogger.Msg($"[SkinForge][Text] texts loaded: " +
                string.Join(", ", TextStore.Languages.Select(l => $"{l}={Entries[l].Count}")) + $" ({edited} edited)");
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"[SkinForge][Text] texts not loaded: {exception.GetBaseException().Message}");
        }
    }

    /// <summary>Every frame; checks the folder every 2 s for files edited outside the game.</summary>
    internal static void Poll(float time)
    {
        // 2026-11-16: a FileSystemWatcher marks changes; the folder is scanned only then (and every 30 s as a
        // fallback) instead of every 2 s.
        EnsureWatcher();
        if (!_changed && time < _nextCheck) return;
        if (_changed && Environment.TickCount64 - _changedAt < 500) return;   // let the editor finish writing
        _changed = false;
        _nextCheck = time + (_watcher != null ? 30f : 2f);
        try
        {
            if (_signature == "\u0001" || TextStore.Signature(Root) != _signature) { Reload(); FontHooks.Refresh(); }
        }
        catch { }
    }

    private static FileSystemWatcher? _watcher;
    private static volatile bool _changed;
    private static long _changedAt;   // set on the watcher thread: no Unity calls there
    private static bool _watcherTried;

    private static long _nextWatcherTry;

    private static void EnsureWatcher()
    {
        if (_watcherTried) return;
        // 2026-11-17: the Text folder is not created just by starting the game (players who never create);
        // the watcher starts once it exists (Dump Text / Text page).
        if (!Directory.Exists(Root))
        {
            if (Environment.TickCount64 < _nextWatcherTry) return;
            _nextWatcherTry = Environment.TickCount64 + 10000;
            if (!Directory.Exists(Root)) return;
        }
        _watcherTried = true;
        try
        {
            _watcher = new FileSystemWatcher(Root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
            FileSystemEventHandler mark = (_, _) => { _changed = true; _changedAt = Environment.TickCount64; };
            _watcher.Changed += mark;
            _watcher.Created += mark;
            _watcher.Deleted += mark;
            _watcher.Renamed += (_, _) => { _changed = true; _changedAt = Environment.TickCount64; };
            _watcher.EnableRaisingEvents = true;
        }
        catch { _watcher = null; }
    }

    /// <summary>Saves one edited entry (its whole file) and makes the game use it.</summary>
    internal static void Save(TextEntry entry)
    {
        TextStore.SaveFileOf(Root, entry, Of(entry.Language));
        Rebuild(entry.Language);
    }

    internal static (int Added, int Updated) MergeAndSave(string language, List<TextEntry> game)
    {
        EnsureLoaded();
        if (!Entries.TryGetValue(language, out List<TextEntry>? list)) Entries[language] = list = new List<TextEntry>();
        (int added, int updated) = TextStore.Merge(list, game);
        TextStore.Save(Root, language, list);
        Rebuild(language);
        return (added, updated);
    }

    private static void Rebuild(string language)
    {
        OverridesByLanguage[language] = new TextOverrides(Entries[language]);
        _signature = TextStore.Signature(Root);   // our own write: no reload needed
        Version++;
    }
}
