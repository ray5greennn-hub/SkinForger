namespace SkinForger.Main.Pure;

/// <summary>
/// <c>SkinCreator\Menukey.txt</c>: the key that opens and closes SkinForger's window. One line holding a
/// Unity KeyCode name, "Insert" by default — the file and its contents are what players already have, so
/// both are kept as they are. The name is only normalised here; Unity turns it into a KeyCode.
/// Pure (no Unity).
/// </summary>
public static class MenuKeySettings
{
    public const string DefaultKey = "Insert";

    /// <summary>The key name in the file, creating it with the default when it is missing or unusable.</summary>
    public static string Load(string path, Action<string>? warning = null)
    {
        try
        {
            if (File.Exists(path))
            {
                string name = Normalize(File.ReadAllText(path));
                if (name.Length > 0) return name;
                warning?.Invoke($"{Path.GetFileName(path)} is empty; using {DefaultKey}.");
            }
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, DefaultKey);
        }
        catch (Exception exception)
        {
            warning?.Invoke($"Could not read the menu key: {exception.Message}; using {DefaultKey}.");
        }
        return DefaultKey;
    }

    /// <summary>
    /// A key name as Unity spells it: no surrounding space, no quotes, and no trailing lines (players edit
    /// this file by hand, and a stray newline or a quoted name should still work).
    /// </summary>
    public static string Normalize(string? text)
    {
        string value = (text ?? string.Empty).Replace("\r", string.Empty);
        int newline = value.IndexOf('\n');
        if (newline >= 0) value = value[..newline];
        return value.Trim().Trim('"', '\'').Trim();
    }
}
