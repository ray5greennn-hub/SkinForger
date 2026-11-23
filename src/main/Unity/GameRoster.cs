using Il2CppAssets.Scripts.PeroTools.Commons;
using Il2CppAssets.Scripts.PeroTools.Managers;
using MelonLoader;
using SkinForger.Main.Pure;

namespace SkinForger.Main.Unity;

/// <summary>
/// The game's character and elfin lists, fetched once and cached. Everything but the fetch is in
/// <see cref="Roster"/>, so the parsing rules are checked outside the game.
///
/// The table has to be asked for with the statically typed call: reaching GetJson through reflection and
/// then calling object.ToString() returns the TYPE NAME rather than the JSON (SkinForger.Core hit this on
/// 2026-11-15 and left a note about it in GameConfigJson).
/// </summary>
public static class GameRoster
{
    private const string Prefix = "[SkinForge][Main][Roster]";

    private static List<RosterEntry>? _characters, _elfins;
    private static int _characterRows, _elfinRows;

    /// <summary>The characters, or an empty list while the game has not loaded its config yet.</summary>
    public static IReadOnlyList<RosterEntry> Characters => Load(false);

    /// <summary>The elfins, or an empty list while the game has not loaded its config yet.</summary>
    public static IReadOnlyList<RosterEntry> Elfins => Load(true);

    public static IReadOnlyList<RosterEntry> Of(bool elfin) => elfin ? Elfins : Characters;

    /// <summary>Rows in the table, censored ones included: the length the selection files are sized against.</summary>
    public static int RowCount(bool elfin)
    {
        Load(elfin);
        return elfin ? _elfinRows : _characterRows;
    }

    /// <summary>Forgets the cached lists; the next use fetches them again (after a Reload, or a language change).</summary>
    public static void Forget()
    {
        _characters = _elfins = null;
        _characterRows = _elfinRows = 0;
    }

    private static List<RosterEntry> Load(bool elfin)
    {
        List<RosterEntry>? cached = elfin ? _elfins : _characters;
        if (cached != null) return cached;

        string? json = TableText(Roster.TableOf(elfin));
        var entries = Roster.Parse(json, Roster.NameFieldOf(elfin));
        int rows = Roster.Count(json);
        if (elfin) { _elfins = entries; _elfinRows = rows; }
        else { _characters = entries; _characterRows = rows; }

        if (json == null)
            MelonLogger.Warning($"{Prefix} {Roster.TableOf(elfin)} is not available yet");
        else
            MelonLogger.Msg($"{Prefix} {Roster.TableOf(elfin)}: {entries.Count} of {rows} rows listed");
        return entries;
    }

    /// <summary>The config table as JSON text, or null when the game cannot give it yet.</summary>
    private static string? TableText(string table)
    {
        try
        {
            ConfigManager? config = Singleton<ConfigManager>.instance;
            var rows = config?.GetJson(table, false);
            if (rows == null) return null;
            string text = rows.ToString();
            return text.Length > 0 && (text[0] == '[' || text[0] == '{') ? text : null;
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"{Prefix} {table}: {exception.GetBaseException().Message}");
            return null;
        }
    }
}
