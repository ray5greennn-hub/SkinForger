using System.Text.Json;
using Il2CppAssets.Scripts.PeroTools.Commons;
using Il2CppAssets.Scripts.PeroTools.Managers;
using MelonLoader;

namespace SkinForger.Main.Unity;

/// <summary>
/// One field of the game's config tables, as text. SkinForger.Core asks Main for this (RoleSkins resolves
/// ConfigString on the Main type), so it is part of the contract rather than a convenience.
///
/// The table is fetched as JSON text and read with System.Text.Json: asking the game object for a single
/// field through Il2Cpp means a managed-to-native round trip per field, while a table is fetched once and
/// then stays in memory here.
/// </summary>
public static class GameConfig
{
    private const string Prefix = "[SkinForge][Main][Config]";
    private static readonly Dictionary<string, string?> Tables = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Forgets the fetched tables (a Reload, or a language change).</summary>
    public static void Forget() => Tables.Clear();

    /// <summary>
    /// False until the game's own loop has run a frame; SkinForgerMod.OnUpdate sets it.
    ///
    /// Asking Singleton&lt;ConfigManager&gt; for its instance CREATES the game's ConfigManager when there is
    /// none yet, and that construction walks into PeroTools' ResourcesManager and PeroLog. During
    /// OnInitializeMelon the game has not built either, and any other mod that has patched PeroLog but not
    /// yet finished its own start-up throws inside that construction -- which leaves the game's
    /// ConfigManager permanently half-built: no character art, no texts, no nickname, for the whole
    /// session. CustomAlbums 4.2.2 does exactly this (its SilenceLogPatch reads its own settings, which are
    /// null until its OnInitializeMelon has run), and on 2026-11-21 it cost the player a completely broken
    /// game. Nothing here may touch the singleton before the game is actually running.
    /// </summary>
    public static bool GameLoopRunning { get; set; }

    /// <summary>
    /// How many rows a config table has, or 0 while the game has not loaded it yet. This is what
    /// SkinForgerMod.GetCount(table) answers, and what the Preview, the Sound catalogue and SkinPackages
    /// count characters and elfins with.
    /// </summary>
    public static int RowCount(string table)
    {
        string? json = Text(table);
        if (string.IsNullOrEmpty(json)) return 0;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.GetArrayLength() : 0;
        }
        catch (JsonException) { return 0; }
    }

    /// <summary>A field of one row, or an empty string when the table, the row or the field is missing.</summary>
    public static string String(string table, int index, string field)
    {
        string? json = Text(table);
        if (json == null || index < 0 || string.IsNullOrEmpty(field)) return string.Empty;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return string.Empty;
            if (index >= document.RootElement.GetArrayLength()) return string.Empty;
            JsonElement row = document.RootElement[index];
            if (row.ValueKind != JsonValueKind.Object) return string.Empty;
            if (!row.TryGetProperty(field, out JsonElement value)) return string.Empty;
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
                _ => string.Empty
            };
        }
        catch (JsonException) { return string.Empty; }
    }

    /// <summary>A config table as JSON text; see GameRoster for why this call must be statically typed.</summary>
    public static string? Text(string table)
    {
        if (Tables.TryGetValue(table, out string? cached)) return cached;
        // Before the game's first frame this would build ConfigManager itself; see GameLoopRunning.
        if (!GameLoopRunning) return null;
        string? text = null;
        try
        {
            ConfigManager? config = Singleton<ConfigManager>.instance;
            var rows = config?.GetJson(table, false);
            if (rows != null)
            {
                string value = rows.ToString();
                if (value.Length > 0 && (value[0] == '[' || value[0] == '{')) text = value;
            }
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"{Prefix} {table}: {exception.GetBaseException().Message}");
        }
        // Only a real answer is remembered. A failure used to be cached as null, so one early miss made
        // the table empty for the rest of the session even after the game had loaded it.
        if (text != null) Tables[table] = text;
        return text;
    }
}
