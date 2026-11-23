using Il2CppAssets.Scripts.PeroTools.Commons;
using Il2CppAssets.Scripts.PeroTools.Managers;

namespace SkinForger;

/// <summary>
/// The game's config table as JSON text. 2026-11-15: calling GetJson through reflection and then object.ToString()
/// gave the type name ("Il2CppNewtonsoft.Json.Linq.JArray"), not the JSON, so HexiePolicy's config scan and the
/// first RoleSkins build failed ("'I' is an invalid start of a value"). The statically typed call reaches
/// JToken.ToString() inside the game.
/// </summary>
internal static class GameConfigJson
{
    internal static string? Text(string table)
    {
        try
        {
            ConfigManager? config = Singleton<ConfigManager>.instance;
            var rows = config?.GetJson(table, false);
            if (rows == null) return null;
            string text = rows.ToString();
            return text.Length > 0 && (text[0] == '[' || text[0] == '{') ? text : null;
        }
        catch { return null; }
    }
}
