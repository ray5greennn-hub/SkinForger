namespace SkinForger.Main.Pure;

/// <summary>
/// Where everything lives under the game folder. The layout is FROZEN: it is what players' and skin creators'
/// existing folders look like, and SkinForger.Core's SkinForgerPaths names the same places. Taking a game root
/// as a parameter (instead of asking MelonLoader) keeps this pure and testable.
///
///   &lt;game&gt;\SkinForger\
///     Skins\                     .sfskin packages the player installed  (+ sfpkg-library\ mounted in memory)
///     ExportSkin\                .sfskin files the creator exported
///     SkinCreator\               creator workspace and settings
///       Images\&lt;Costume&gt;\   Dump output and the images being edited (Main's skins root)
///         manifest.json, &lt;atlas&gt;\&lt;part&gt;.png + .json
///       Text\  Sounds\  CustomLodingPic\  UserData\
///       Saved.json  SavedElfin.json  Menukey.txt  Settings.json  SkinForgeFpsLimiter.json  preview.png
/// </summary>
public sealed class MainLayout
{
    public const string SkinForgerFolder = "SkinForger";
    public const string SavedCharacters = "Saved.json";
    public const string SavedElfins = "SavedElfin.json";
    public const string MenuKeyFile = "Menukey.txt";
    public const string SettingsFile = "Settings.json";
    public const string FpsLimiterFile = "SkinForgeFpsLimiter.json";
    public const string PreviewPicture = "preview.png";

    public MainLayout(string gameRoot) => GameRoot = gameRoot;

    public string GameRoot { get; }

    public string Root => Path.Combine(GameRoot, SkinForgerFolder);
    public string Player => Path.Combine(Root, "Skins");
    public string Export => Path.Combine(Root, "ExportSkin");
    public string CreatorRoot => Path.Combine(Root, "SkinCreator");

    /// <summary>Main's skins root: one folder per character / elfin.</summary>
    public string Images => Path.Combine(CreatorRoot, "Images");
    public string Text => Path.Combine(CreatorRoot, "Text");
    public string Sounds => Path.Combine(CreatorRoot, "Sounds");
    public string LoadingPictures => Path.Combine(CreatorRoot, "CustomLodingPic");

    public string SavedCharactersFile => Path.Combine(CreatorRoot, SavedCharacters);
    public string SavedElfinsFile => Path.Combine(CreatorRoot, SavedElfins);
    public string MenuKeyPath => Path.Combine(CreatorRoot, MenuKeyFile);
    public string SettingsPath => Path.Combine(CreatorRoot, SettingsFile);
    public string FpsLimiterPath => Path.Combine(CreatorRoot, FpsLimiterFile);
    public string PreviewPicturePath => Path.Combine(CreatorRoot, PreviewPicture);

    public string CostumeFolder(string costume) => Path.Combine(Images, costume);
    public string ManifestOf(string costume) => Path.Combine(CostumeFolder(costume), RegionManifest.FileName);
    public string AtlasFolder(string costume, string atlas) => Path.Combine(CostumeFolder(costume), atlas);

    /// <summary>Creates the folders a first run needs. Never touches anything that already exists.</summary>
    public void EnsureFolders()
    {
        foreach (string folder in new[] { Root, Player, Export, CreatorRoot, Images, Text, Sounds, LoadingPictures })
            Directory.CreateDirectory(folder);
    }

    /// <summary>
    /// Folder names that must never be treated as a costume: SkinForger's own working folders and the mount
    /// point of the Skin Library. Main refuses resource paths containing "atlas", "cache" or "backup", and
    /// Core mounts packages under Skins\sfpkg-library — neither is a character.
    /// </summary>
    public static bool IsReservedFolderName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        (name!.StartsWith("sfpkg-", StringComparison.OrdinalIgnoreCase) ||
         name.Equals("replaced", StringComparison.OrdinalIgnoreCase) ||
         name.Equals("UserData", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("atlas", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("cache", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("backup", StringComparison.OrdinalIgnoreCase));

    /// <summary>A file name safe on Windows, for a part name coming from the game's data.</summary>
    public static string SafeFileName(string? name)
    {
        string value = (name ?? string.Empty).Trim();
        if (value.Length == 0) return "unnamed";
        foreach (char invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
        value = value.TrimEnd('.', ' ');
        return value.Length == 0 ? "unnamed" : value;
    }
}
