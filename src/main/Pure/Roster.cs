using System.Text.Json;

namespace SkinForger.Main.Pure;

/// <summary>One character or elfin of the game's own list, at the position the selection files key on.</summary>
public sealed record RosterEntry(int Index, string Name)
{
    public override string ToString() => $"{Index}:{Name}";
}

/// <summary>
/// The game's character and elfin lists, read from its config tables.
///
/// The tables are <c>character_English</c> and <c>elfin_English</c>; a character's name is in "cosName" and an
/// elfin's in "name" — the English tables are used because that is what identifies a costume everywhere in
/// SkinForger (folder names, manifests, packages), whatever language the player sees.
///
/// The INDEX is the row's position in the table, and it is the key Saved.json / SavedElfin.json use, so a row
/// that is skipped must not shift the ones after it: censored rows keep their index and are simply not listed.
/// Pure: the game hands over the table as JSON text (ConfigManager.GetJson(...).ToString()), and everything
/// after that is checked offline.
/// </summary>
public static class Roster
{
    public const string CharacterTable = "character_English";
    public const string ElfinTable = "elfin_English";
    public const string CharacterNameField = "cosName";
    public const string ElfinNameField = "name";

    public static string TableOf(bool elfin) => elfin ? ElfinTable : CharacterTable;
    public static string NameFieldOf(bool elfin) => elfin ? ElfinNameField : CharacterNameField;

    /// <summary>
    /// The rows of a config table, in order, with their index kept. Rows with no usable name, and censored
    /// rows, are left out while every other row keeps the index it has in the table.
    /// </summary>
    public static List<RosterEntry> Parse(string? json, string nameField)
    {
        var entries = new List<RosterEntry>();
        if (string.IsNullOrWhiteSpace(json)) return entries;

        JsonDocument document;
        try { document = JsonDocument.Parse(json!, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException) { return entries; }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array) return entries;
            int index = 0;
            foreach (JsonElement row in document.RootElement.EnumerateArray())
            {
                int here = index++;
                if (row.ValueKind != JsonValueKind.Object) continue;
                if (!row.TryGetProperty(nameField, out JsonElement name) || name.ValueKind != JsonValueKind.String) continue;
                string value = (name.GetString() ?? string.Empty).Trim();
                if (value.Length == 0 || DumpNaming.IsCensoredName(value)) continue;
                entries.Add(new RosterEntry(here, value));
            }
        }
        return entries;
    }

    /// <summary>How many rows the table has, including the ones left out of <see cref="Parse"/>.</summary>
    public static int Count(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json!);
            return document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.GetArrayLength() : 0;
        }
        catch (JsonException) { return 0; }
    }

    /// <summary>The name of one row, or an empty string when there is none.</summary>
    public static string NameAt(IReadOnlyList<RosterEntry> entries, int index) =>
        entries.FirstOrDefault(e => e.Index == index)?.Name ?? string.Empty;
}
