using System.Text.Json;

namespace SkinForger.Main.Pure;

/// <summary>
/// <c>SkinCreator\SkinForgeFpsLimiter.json</c>: the frame-rate cap SkinForger applies.
///
/// The format is FROZEN and matches the authored FpsLimiterPatch runtime that is embedded in the old Main
/// (SkinForger.FpsProtection.FpsProtectionConfig), including its two legacy field names, so a file a player
/// already has keeps working:
///   { "FPSLimit": 60 }            the current form; 0 means "do not cap"
///   { "enabled": false }          legacy: off
///   { "targetFps": 120 }          legacy: the cap
/// A value other than 0 outside 15..360 is refused and the default is used, because a cap of 1 would look
/// like the game had frozen.
/// Pure (no Unity), so the rules are checked offline.
/// </summary>
public static class FpsLimiterSettings
{
    public const int DefaultLimit = 60;
    public const int MinimumLimit = 15;
    public const int MaximumLimit = 360;
    /// <summary>Written when the file does not exist yet.</summary>
    public const string Template = "{\r\n  \"FPSLimit\": 60\r\n}\r\n";

    /// <summary>The cap to apply: 0 for "do not cap". Creates the file with the default when it is missing.</summary>
    public static int Load(string path, Action<string>? warning = null)
    {
        if (!File.Exists(path))
        {
            try
            {
                string? directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(path, Template);
            }
            catch (Exception exception) { warning?.Invoke("Could not write the FPS limiter config: " + exception.Message); }
            return DefaultLimit;
        }

        try { return Parse(File.ReadAllText(path), warning); }
        catch (Exception exception)
        {
            warning?.Invoke("Could not read the FPS limiter config: " + exception.Message + "; using " + DefaultLimit + ".");
            return DefaultLimit;
        }
    }

    /// <summary>The cap a config text asks for, with the same rules as <see cref="Load"/>.</summary>
    public static int Parse(string json, Action<string>? warning = null)
    {
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return DefaultLimit;

        int value;
        if (TryInt(root, "FPSLimit", out int limit)) value = limit;
        else if (root.TryGetProperty("enabled", out JsonElement enabled) &&
                 enabled.ValueKind == JsonValueKind.False) value = 0;
        else if (TryInt(root, "targetFps", out int legacy)) value = legacy;
        else value = DefaultLimit;

        if (value == 0) return 0;
        if (value < MinimumLimit || value > MaximumLimit)
        {
            warning?.Invoke($"FPSLimit must be 0 or between {MinimumLimit} and {MaximumLimit}; using {DefaultLimit}.");
            return DefaultLimit;
        }
        return value;
    }

    private static bool TryInt(JsonElement root, string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out JsonElement element) &&
               element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value);
    }

    /// <summary>
    /// Whether the cap still has to be set. The game raises its own target now and then, so this is checked
    /// every so often rather than once: a cap is applied when there is none, or when the current one is higher.
    /// </summary>
    public static bool ShouldApply(int currentTarget, int limit) =>
        limit > 0 && (currentTarget <= 0 || currentTarget > limit);
}
