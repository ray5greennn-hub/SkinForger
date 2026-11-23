using System.Text.Json;

namespace SkinForger.Main.Pure;

/// <summary>
/// Which skin each character / elfin is showing: <c>Saved.json</c> and <c>SavedElfin.json</c> in SkinCreator.
///
/// The format is FROZEN — a JSON object of character index to chosen position, which is what players' existing
/// files contain: <c>{ "0": -1, "1": 2 }</c>. <see cref="Original"/> (-1) means the game's own art.
/// Core reads the choice through Main (GetSelected / SaveSelection are hooked in four places), so the meaning
/// of the numbers may not change.
///
/// A damaged or hand-edited file must never stop the game from starting: anything unreadable loads as "no
/// selection" instead of throwing (the upstream mod showed the player an exception window here).
/// Pure (no Unity).
/// </summary>
public sealed class SelectionStore
{
    /// <summary>The game's own art, no replacement.</summary>
    public const int Original = -1;

    private readonly Dictionary<int, int> _characters;
    private readonly Dictionary<int, int> _elfins;
    private readonly string _charactersFile;
    private readonly string _elfinsFile;

    public SelectionStore(MainLayout layout) : this(layout, new Dictionary<int, int>(), new Dictionary<int, int>())
    {
    }

    /// <summary>
    /// Stores the selections in dictionaries the caller owns. SkinForgerMod passes its own
    /// SelectedCharacters / SelectedElfins fields, because SkinForger.Core reads AND writes those two
    /// (DumpIsolation swaps every selection to Original while a Dump runs, CreatorWindowHooks rewrites a
    /// Root click into the Skin Library position). They must therefore be the live storage, not copies.
    /// </summary>
    public SelectionStore(MainLayout layout, Dictionary<int, int> characters, Dictionary<int, int> elfins)
    {
        _charactersFile = layout.SavedCharactersFile;
        _elfinsFile = layout.SavedElfinsFile;
        _characters = characters;
        _elfins = elfins;
    }

    /// <summary>The file one half of the selections is kept in.</summary>
    public string FileOf(bool elfin) => elfin ? _elfinsFile : _charactersFile;

    /// <summary>The live selections, as SkinForger.Core expects to be able to change them.</summary>
    public Dictionary<int, int> Live(bool elfin) => elfin ? _elfins : _characters;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public void Load()
    {
        Read(_charactersFile, _characters);
        Read(_elfinsFile, _elfins);
    }

    private static void Read(string path, Dictionary<int, int> into)
    {
        into.Clear();
        if (!File.Exists(path)) return;
        try
        {
            var stored = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllBytes(path), Options);
            if (stored == null) return;
            foreach ((string key, int value) in stored)
                if (int.TryParse(key, out int index)) into[index] = value;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // Unreadable selections are not worth a crash: everything falls back to Original.
            into.Clear();
        }
    }

    /// <summary>The chosen position of a character / elfin, or <see cref="Original"/>.</summary>
    public int Get(int index, bool elfin) =>
        (elfin ? _elfins : _characters).TryGetValue(index, out int value) ? value : Original;

    /// <summary>Sets one choice and writes the file (the choice must survive a crash, as it always has).</summary>
    public void Set(int index, int position, bool elfin)
    {
        (elfin ? _elfins : _characters)[index] = position;
        Save(elfin);
    }

    /// <summary>Puts every character and elfin back to the game's own art.</summary>
    public void Clear()
    {
        foreach (int index in _characters.Keys.ToList()) _characters[index] = Original;
        foreach (int index in _elfins.Keys.ToList()) _elfins[index] = Original;
        Save(false);
        Save(true);
    }

    public void Save(bool elfin) => SaveTo(elfin ? _elfins : _characters, elfin ? _elfinsFile : _charactersFile);

    /// <summary>
    /// Writes one set of selections to one file, in the frozen format. Taking both as arguments is what
    /// SkinForger.Core's contract needs: it asks Main to write a dictionary it has just changed to a
    /// path it names (FastReload after an incremental reload, DumpIsolation when a Dump ends).
    /// </summary>
    public static void SaveTo(Dictionary<int, int> selection, string path)
    {
        if (selection == null || string.IsNullOrEmpty(path)) return;
        var stored = selection.OrderBy(pair => pair.Key)
                              .ToDictionary(pair => pair.Key.ToString(), pair => pair.Value);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".partial";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(stored, Options));
        if (File.Exists(path)) File.Delete(path);
        File.Move(temporary, path);
    }

    /// <summary>Every index that is not showing the game's own art.</summary>
    public IEnumerable<int> Replaced(bool elfin) =>
        (elfin ? _elfins : _characters).Where(pair => pair.Value != Original).Select(pair => pair.Key).OrderBy(i => i);

    public int Count(bool elfin) => (elfin ? _elfins : _characters).Count;
}
