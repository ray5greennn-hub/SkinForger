using System.Text.Json;
using System.Text.Json.Nodes;
using MelonLoader;

namespace SkinForger.SkinPackages;

/// <summary>
/// SkinForger settings (Settings page, 2026-11-16), saved in SkinForger\SkinCreator\Settings.json:
///  - uiLanguage: "auto" (follow the game language, default), "zh" or "en";
///  - (file: SkinForger\Settings.json since 2026-11-17)
///  - recordEditHistory: true (default) = every Reload keeps the previous version of an edited image in replaced\;
///    false = nothing is copied (the backup state is not advanced either, so edited images are still recognised).
/// </summary>
internal static class SfSettings
{
    private const string Prefix = "[SkinForge][Settings]";
    // 2026-11-17: SkinForger\Settings.json (a player who never creates must not get a SkinCreator folder);
    // SkinCreator\Settings.json (1.13-1.18) is still read once.
    private static string FilePath => Path.Combine(SkinForgerPaths.Root, "Settings.json");
    private static string OldFilePath => Path.Combine(SkinForgerPaths.CreatorRoot, "Settings.json");
    private static bool _loaded;
    private static string _uiLanguage = "auto";
    private static bool _recordEditHistory = true;

    internal static string UiLanguage { get { Load(); return _uiLanguage; } set { Load(); _uiLanguage = value is "zh" or "en" ? value : "auto"; Save(); } }
    internal static bool RecordEditHistory { get { Load(); return _recordEditHistory; } set { Load(); _recordEditHistory = value; Save(); } }

    /// <summary>True when the SkinForger interface is shown in Chinese right now.</summary>
    internal static bool Chinese
    {
        get
        {
            string setting = UiLanguage;
            if (setting == "zh") return true;
            if (setting == "en") return false;
            try
            {
                string language = Il2CppAssets.Scripts.Database.DataHelper.userLanguage ?? string.Empty;
                return language.StartsWith("Chinese", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            string file = File.Exists(FilePath) ? FilePath : OldFilePath;
            if (!File.Exists(file)) return;
            if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject node) return;
            if (node["uiLanguage"]?.GetValue<string>() is { } language && language is "auto" or "zh" or "en") _uiLanguage = language;
            if (node["recordEditHistory"] is JsonValue record && record.TryGetValue(out bool value)) _recordEditHistory = value;
        }
        catch (Exception exception) { MelonLogger.Warning($"{Prefix} Settings.json not read: {exception.GetBaseException().Message}"); }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var node = new JsonObject { ["uiLanguage"] = _uiLanguage, ["recordEditHistory"] = _recordEditHistory };
            File.WriteAllText(FilePath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            MelonLogger.Msg($"{Prefix} saved: uiLanguage={_uiLanguage} recordEditHistory={_recordEditHistory}");
        }
        catch (Exception exception) { MelonLogger.Warning($"{Prefix} Settings.json not saved: {exception.GetBaseException().Message}"); }
    }
}
