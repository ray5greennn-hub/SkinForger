using System.Text.Json;

namespace SkinForger.SkinPackages;

/// <summary>One installed .sfskin as the Skin Library shows it.</summary>
public sealed class LibraryPackage
{
    public string File = string.Empty;          // full path
    public string FileName = string.Empty;      // "Name.sfskin" (the library's key)
    public string Name = string.Empty;
    public string Author = string.Empty;
    public string Description = string.Empty;
    /// <summary>The game / platform the creator made this package for (package.json "Game").</summary>
    public string MadeFor = string.Empty;
    /// <summary>True when the creator allowed unofficial builds / ports to load this package.</summary>
    public bool SharingAllowed;
    public string Created = string.Empty;
    /// <summary>package.json CreatedUtc, exactly as written: the "which version is this" half of the readme key.</summary>
    public string CreatedUtc = string.Empty;
    /// <summary>The creator's notes, already decoded; empty when the package carries none (2026-11-21).</summary>
    public string Readme = string.Empty;
    public long Bytes;
    public DateTime WriteUtc;
    public int Images, Audio, Text, Fonts, LoadingPictures, OtherPictures;
    /// <summary>Character / elfin folder -> number of images it replaces.</summary>
    public readonly SortedDictionary<string, int> Costumes = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Costume -> part identities ("atlas|region|attachment") that have an image; used for overlap counts.</summary>
    public readonly Dictionary<string, HashSet<string>> Parts = new(StringComparer.OrdinalIgnoreCase);
    public byte[]? Preview;
    /// <summary>The card background picture, raw PNG bytes; null when the package carries none (2026-11-22).</summary>
    public byte[]? Cover;
    /// <summary>Null when the package is fine, otherwise why it cannot be used.</summary>
    public string? Problem;
}

/// <summary>Another enabled skin that also changes a character of the package being enabled.</summary>
public sealed record SkinConflict(string Costume, string OtherName, int SharedImages);

/// <summary>
/// Skin Library state (pure .NET, testable): which packages are enabled and in what order.
/// Loading priority, highest first: SkinCreator (the player's own work) > enabled packages, the most recently
/// enabled first > Muse Dash's own art. Within one character, images are layered: a newer skin replaces only the
/// images it contains, everything else still comes from the skins below it.
/// Stored in UserData\SkinForger\skin-library.json  { "Enabled": [lowest .. highest], "Known": [...] }.
/// </summary>
public static class SkinLibrary
{
    private sealed class State
    {
        public List<string> Enabled { get; set; } = new();
        public List<string> Known { get; set; } = new();
        /// <summary>"FileName|CreatedUtc" of every package whose readme the player has already been shown.</summary>
        public List<string> ReadmeSeen { get; set; } = new();
    }

    private static readonly object Sync = new();
    private static State? _state;
    private static string _file = string.Empty;

    public static IReadOnlyList<LibraryPackage> Packages { get; private set; } = Array.Empty<LibraryPackage>();

    /// <summary>Costumes whose selection must switch to the library skin at the next reload (the player just enabled
    /// a skin for them, or a new package arrived).</summary>
    public static readonly HashSet<string> PendingSelect = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Package files that appeared since the last session and were left off because they overlap an
    /// enabled skin (shown as "New" until the player decides).</summary>
    public static readonly HashSet<string> NewlyAdded = new(StringComparer.OrdinalIgnoreCase);

    public static int Version { get; private set; }

    public static void Initialize(string stateFile)
    {
        lock (Sync)
        {
            _file = stateFile;
            _state = null;
            try
            {
                if (System.IO.File.Exists(stateFile))
                    _state = JsonSerializer.Deserialize<State>(System.IO.File.ReadAllText(stateFile));
            }
            catch { _state = null; }
        }
    }

    /// <summary>Enabled installed packages, lowest priority first.</summary>
    public static List<LibraryPackage> EnabledInOrder()
    {
        lock (Sync)
        {
            if (_state == null) return new List<LibraryPackage>();
            var result = new List<LibraryPackage>();
            foreach (string name in _state.Enabled)
            {
                LibraryPackage? package = Packages.FirstOrDefault(p => p.FileName.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (package != null && package.Problem == null) result.Add(package);
            }
            return result;
        }
    }

    /// <summary>
    /// Readme state (2026-11-21). The key is the file name AND the time the creator packed it, so a package the
    /// creator re-exports shows its notes again -- a new version's notes are exactly what a player should read.
    /// </summary>
    private static string ReadmeKey(LibraryPackage package) => package.FileName + "|" + package.CreatedUtc;

    /// <summary>True when this exact version's readme has already been put in front of the player.</summary>
    public static bool ReadmeSeen(LibraryPackage package)
    {
        lock (Sync) return _state != null && _state.ReadmeSeen.Contains(ReadmeKey(package), StringComparer.Ordinal);
    }

    /// <summary>Remembers that the player has been shown this version's readme; nothing opens by itself again.</summary>
    public static void MarkReadmeSeen(LibraryPackage package)
    {
        lock (Sync)
        {
            _state ??= new State();
            string key = ReadmeKey(package);
            if (_state.ReadmeSeen.Contains(key, StringComparer.Ordinal)) return;
            _state.ReadmeSeen.Add(key);
            // Keys of packages that are no longer installed are dropped, so the list cannot grow forever.
            if (_state.ReadmeSeen.Count > 200)
            {
                var alive = new HashSet<string>(Packages.Select(ReadmeKey), StringComparer.Ordinal);
                _state.ReadmeSeen.RemoveAll(k => !alive.Contains(k));
            }
            SaveLocked();
        }
    }

    public static bool IsEnabled(LibraryPackage package)
    {
        lock (Sync) return _state != null && _state.Enabled.Contains(package.FileName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>1 = highest enabled package (SkinCreator is always above it), 0 = not enabled.</summary>
    public static int Priority(LibraryPackage package)
    {
        List<LibraryPackage> order = EnabledInOrder();
        int index = order.FindIndex(p => p.FileName.Equals(package.FileName, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? 0 : order.Count - index;
    }

    /// <summary>Called after the packages were (re)read. First run: everything that is installed is enabled (the
    /// behaviour before the library existed). Later: a new package is enabled when it does not overlap an enabled
    /// skin; otherwise it stays off and is marked New so the player decides in the library.</summary>
    public static void Update(IReadOnlyList<LibraryPackage> packages)
    {
        lock (Sync)
        {
            Packages = packages;
            bool firstRun = _state == null;
            _state ??= new State();
            bool changed = false;
            foreach (LibraryPackage package in packages.OrderBy(p => p.WriteUtc))
            {
                if (_state.Known.Contains(package.FileName, StringComparer.OrdinalIgnoreCase)) continue;
                _state.Known.Add(package.FileName);
                changed = true;
                if (package.Problem != null) continue;
                bool overlaps = !firstRun && ConflictsLocked(package).Count > 0;
                if (overlaps)
                {
                    NewlyAdded.Add(package.FileName);
                    continue;
                }
                _state.Enabled.RemoveAll(n => n.Equals(package.FileName, StringComparison.OrdinalIgnoreCase));
                _state.Enabled.Add(package.FileName);
                // First run (upgrade): keep the player's current choices; a costume that was on a package moves to
                // the library skin through the remembered selection (PackageLoaderHooks.RestoreSelectedPaths).
                if (!firstRun) foreach (string costume in package.Costumes.Keys) PendingSelect.Add(costume);
            }
            if (changed || firstRun) SaveLocked();
            Version++;
        }
    }

    /// <summary>Turns a skin on (it becomes the highest enabled package) or off.</summary>
    public static void SetEnabled(LibraryPackage package, bool enabled)
    {
        lock (Sync)
        {
            _state ??= new State();
            _state.Enabled.RemoveAll(n => n.Equals(package.FileName, StringComparison.OrdinalIgnoreCase));
            if (enabled)
            {
                _state.Enabled.Add(package.FileName);
                foreach (string costume in package.Costumes.Keys) PendingSelect.Add(costume);
            }
            if (!_state.Known.Contains(package.FileName, StringComparer.OrdinalIgnoreCase)) _state.Known.Add(package.FileName);
            NewlyAdded.Remove(package.FileName);
            SaveLocked();
            Version++;
        }
    }

    public static void DisableAll()
    {
        lock (Sync)
        {
            _state ??= new State();
            _state.Enabled.Clear();
            NewlyAdded.Clear();
            SaveLocked();
            Version++;
        }
    }

    /// <summary>Enabled skins that change the same characters as <paramref name="package"/>.</summary>
    public static List<SkinConflict> Conflicts(LibraryPackage package)
    {
        lock (Sync) return ConflictsLocked(package);
    }

    private static List<SkinConflict> ConflictsLocked(LibraryPackage package)
    {
        var result = new List<SkinConflict>();
        if (_state == null) return result;
        foreach (string name in _state.Enabled)
        {
            if (name.Equals(package.FileName, StringComparison.OrdinalIgnoreCase)) continue;
            LibraryPackage? other = Packages.FirstOrDefault(p => p.FileName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (other == null || other.Problem != null) continue;
            foreach (string costume in package.Costumes.Keys)
            {
                if (!other.Costumes.ContainsKey(costume)) continue;
                int shared = package.Parts.TryGetValue(costume, out HashSet<string>? mine) && other.Parts.TryGetValue(costume, out HashSet<string>? theirs)
                    ? mine.Count(theirs.Contains) : 0;
                result.Add(new SkinConflict(costume, other.Name, shared));
            }
        }
        return result;
    }

    private static void SaveLocked()
    {
        if (string.IsNullOrEmpty(_file) || _state == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            System.IO.File.WriteAllText(_file, JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
