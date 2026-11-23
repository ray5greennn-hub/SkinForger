using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SkinForger.SkinPackages;

/// <summary>
/// The .sfskin skin package (format 1). Pure .NET: no Unity / MelonLoader types, so it is testable outside the game.
///
/// Layout (little-endian):
///   magic       8  "SFSKIN" 0x1A 0x0A
///   version     2  = 1
///   flags       2  = 0
///   salt       16  random per package
///   iv         16  random per package
///   length      8  payload byte count
///   payload     n  AES-256-CBC(PKCS7) of a ZIP archive; key = SHA-256(KeySeed || salt)
///   checksum   32  SHA-256 of every byte above
///
/// ZIP content:
///   package.json            name, created time, generator, skin list
///   readme.txt              the creator's own notes, UTF-8 (optional)
///   skins/&lt;Folder&gt;/...   manifest.json and the folder's PNG / JSON files
///
/// Purpose: a package is one opaque file, not a folder of PNGs that can be repacked into another build of the game,
/// and SkinForger decodes it only into memory. KeySeed is a constant in published source -- and would be a plain
/// string inside the DLL even if the source were closed -- so this is a container that ordinary tools cannot open,
/// NOT cryptographic secrecy. What protects a skin's creator is the sharing choice recorded below plus the
/// project's terms, never this key.
/// </summary>
public static class SfSkinFormat
{
    public const string Extension = ".sfskin";
    public const ushort FormatVersion = 1;
    private static readonly byte[] Magic = { (byte)'S', (byte)'F', (byte)'S', (byte)'K', (byte)'I', (byte)'N', 0x1A, 0x0A };
    private static readonly byte[] KeySeed = Encoding.UTF8.GetBytes("SkinForger/sfskin/format-1/Muse Dash Steam release");
    private const int HeaderLength = 8 + 2 + 2 + 16 + 16 + 8;

    public sealed record PackageEntry(string Path, byte[] Data);

    public sealed class PackageInfo
    {
        public string Format { get; set; } = "sfskin";
        public int FormatVersion { get; set; } = SfSkinFormat.FormatVersion;
        public string Name { get; set; } = string.Empty;
        public string CreatedUtc { get; set; } = string.Empty;
        public string Generator { get; set; } = string.Empty;
        public string Game { get; set; } = "Muse Dash (Steam)";
        /// <summary>"edited-only": only the creator's changed images; every other part shows the player's own game art.
        /// Empty (older packages): the full folder.</summary>
        public string Contents { get; set; } = string.Empty;
        /// <summary>Optional, shown in the Skin Library (2026-11-15). Empty = not credited / no description.</summary>
        public string Author { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        /// <summary>ZIP entry of the preview picture ("preview.png"), empty when the package has none.</summary>
        public string Preview { get; set; } = string.Empty;
        /// <summary>
        /// ZIP entry of the cover picture ("cover.png"), empty when the package has none (2026-11-22).
        ///
        /// The wide picture drawn behind this skin's card in the Skin Library, cropped to fill the card and
        /// darkened from the left so the skin's name stays readable. A separate file from preview.png: the
        /// preview is a small square thumbnail, the cover is a banner.
        /// </summary>
        public string Cover { get; set; } = string.Empty;
        /// <summary>
        /// ZIP entry of the creator's notes ("readme.txt"), empty when the package has none (2026-11-21).
        ///
        /// Written by hand into SkinForger/SkinCreator/readme.txt rather than typed into the export page: a
        /// readme is long, and a text box inside an IMGUI window is a bad place to write anything long. Always
        /// stored as UTF-8 here whatever the author's editor saved -- see <see cref="DecodeText"/>.
        /// </summary>
        public string Readme { get; set; } = string.Empty;
        /// <summary>
        /// The creator's sharing choice (2026-11-17). <see cref="RedistributionAny"/> = the creator allows this
        /// package to be loaded by builds other than official SkinForger on <see cref="Game"/>. Anything else,
        /// empty included (every package made before this field existed), means official SkinForger on the platform
        /// the creator exported from, and nothing else. The skin images belong to their creator, not to SkinForger's
        /// own license, so this field is the creator's own permission and is honoured in <see cref="MayLoad"/>.
        /// </summary>
        public string Redistribution { get; set; } = string.Empty;
        /// <summary>Edited texts (text/CN|EN/...), font files (text/Fonts/...), loading pictures (loading/...). 2026-11-15.</summary>
        public int Texts { get; set; }
        public int Fonts { get; set; }
        public int LoadingPictures { get; set; }
        /// <summary>Pictures that belong to no costume (other/...), matched by texture name. 2026-11-20.</summary>
        public int OtherPictures { get; set; }
        public List<PackageSkin> Skins { get; set; } = new();
    }

    /// <summary>Non-image content of a package, built in memory by the exporter.</summary>
    public sealed class PackageExtras
    {
        public List<(string Path, byte[] Data)> Files { get; } = new();
        public int Texts, Fonts, LoadingPictures, OtherPictures;
    }

    /// <summary>Picture shown on the Skin Library card when SkinCreator\preview.png exists at export time.</summary>
    public const string PreviewEntry = "preview.png";
    /// <summary>Card background, read from SkinForger\SkinCreator\cover.png at export time (2026-11-22).</summary>
    public const string CoverEntry = "cover.png";
    public const int MaxAuthorLength = 60;
    public const int MaxDescriptionLength = 300;

    /// <summary>The creator's notes, read from SkinForger/SkinCreator/readme.txt at export time.</summary>
    public const string ReadmeEntry = "readme.txt";
    /// <summary>A readme travels to every player who installs the package, so it is notes, not a log file.</summary>
    public const int MaxReadmeLength = 20000;

    /// <summary>
    /// Text a creator typed in whatever their editor saved, as a string.
    ///
    /// Chinese Windows' Notepad still offers "ANSI", which is code page 936, and a readme saved that way is
    /// mojibake when read as UTF-8. Strict UTF-8 first (a file that decodes as UTF-8 IS UTF-8 for anything but
    /// the shortest strings), then the system's Chinese code page, then UTF-8 with replacement so a strange file
    /// still shows something instead of throwing. The byte-order mark, if any, is dropped.
    /// </summary>
    public static string DecodeText(byte[] data)
    {
        if (data.Length == 0) return string.Empty;
        int start = data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? 3 : 0;
        try { return new UTF8Encoding(false, true).GetString(data, start, data.Length - start); }
        catch (DecoderFallbackException) { }
        EnsureCodePages();
        try { return Encoding.GetEncoding(936).GetString(data, start, data.Length - start); }
        catch (Exception) { }
        return Encoding.UTF8.GetString(data, start, data.Length - start);
    }

    private static bool _codePagesTried;

    /// <summary>
    /// Makes code page 936 (Chinese "ANSI") available to <see cref="Encoding.GetEncoding(int)"/>.
    ///
    /// .NET Core knows only Unicode out of the box; the legacy code pages live in
    /// System.Text.Encoding.CodePages, which IS part of the shared framework the game runs on but is not in the
    /// compile-time reference set. Reached by reflection so this file keeps its "pure .NET, no extra
    /// dependencies" promise, and so a runtime without that assembly simply falls through to UTF-8 instead of
    /// failing to load. Without this a readme saved from Chinese Notepad as "ANSI" reaches players as mojibake.
    /// </summary>
    private static void EnsureCodePages()
    {
        if (_codePagesTried) return;
        _codePagesTried = true;
        try
        {
            Type? type = Type.GetType("System.Text.CodePagesEncodingProvider, System.Text.Encoding.CodePages");
            object? instance = type?.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null);
            if (instance is EncodingProvider provider) Encoding.RegisterProvider(provider);
        }
        catch (Exception) { }
    }

    /// <summary>The game and platform this build of SkinForger is: the only one a package may be made for.</summary>
    public const string OfficialGame = "Muse Dash (Steam)";
    /// <summary><see cref="PackageInfo.Redistribution"/>: the creator allows unofficial builds / ports to load it.</summary>
    public const string RedistributionAny = "any";
    /// <summary><see cref="PackageInfo.Redistribution"/>: official SkinForger on <see cref="PackageInfo.Game"/> only.</summary>
    public const string RedistributionOfficialOnly = "official-only";

    /// <summary>True when the creator ticked "may be loaded by unofficial builds" at export time.</summary>
    public static bool AllowsUnofficialBuilds(PackageInfo info) =>
        string.Equals((info.Redistribution ?? string.Empty).Trim(), RedistributionAny, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Null when <paramref name="info"/> may be loaded by a build of SkinForger whose game / platform is
    /// <paramref name="game"/>, otherwise the reason it may not. A package made for one platform and not released
    /// by its creator for others is refused here, and the refusal is shown in the Skin Library.
    /// </summary>
    public static string? MayLoad(PackageInfo info, string game = OfficialGame)
    {
        string made = (info.Game ?? string.Empty).Trim();
        if (made.Length == 0 || string.Equals(made, game, StringComparison.OrdinalIgnoreCase)) return null;
        if (AllowsUnofficialBuilds(info)) return null;
        string who = string.IsNullOrWhiteSpace(info.Author) ? "Its creator" : info.Author.Trim();
        return $"Not loaded: this package was made for {made}. {who} did not release it for {game}. " +
               "Only the creator can change that, by exporting it again with sharing allowed.";
    }

    public sealed class PackageSkin
    {
        public string Folder { get; set; } = string.Empty;
        public int Images { get; set; }
        public int Files { get; set; }
        public long Bytes { get; set; }
    }

    /// <summary>Files of one skin folder that go into a package: PNG and JSON only, never replaced/ history or censored files.</summary>
    public static IEnumerable<string> PackableFiles(string skinFolder)
    {
        foreach (string file in Directory.EnumerateFiles(skinFolder, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(skinFolder, file);
            string[] parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (parts.Any(p => p.Equals("replaced", StringComparison.OrdinalIgnoreCase))) continue;
            if (parts.Any(IsCensoredName)) continue;
            string extension = Path.GetExtension(file);
            if (!extension.Equals(".png", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".json", StringComparison.OrdinalIgnoreCase)) continue;
            yield return file;
        }
    }

    public static bool IsCensoredName(string? value) =>
        !string.IsNullOrEmpty(value) && value.IndexOf("hexie", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>"Name.sfskin", then "Name (1).sfskin", "Name (2).sfskin" ... never overwrites.</summary>
    public static string UniquePath(string directory, string name)
    {
        string path = Path.Combine(directory, name + Extension);
        for (int i = 1; File.Exists(path); i++)
            path = Path.Combine(directory, $"{name} ({i}){Extension}");
        return path;
    }

    /// <summary>Null when the name is usable as a file name, otherwise the reason.</summary>
    public static string? ValidateName(string? name)
    {
        string trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0) return "A package name is required before exporting.";
        if (trimmed.Length > 80) return "The package name is too long (80 characters at most).";
        char[] invalid = Path.GetInvalidFileNameChars();
        if (trimmed.IndexOfAny(invalid) >= 0)
            return "The name cannot contain  \\ / : * ? \" < > |";
        if (trimmed.EndsWith(".", StringComparison.Ordinal))
            return "The name cannot end with a dot.";
        string upper = trimmed.ToUpperInvariant();
        if (upper is "CON" or "PRN" or "AUX" or "NUL" || (upper.Length == 4 && (upper.StartsWith("COM") || upper.StartsWith("LPT")) && char.IsDigit(upper[3])))
            return "This name is reserved by Windows.";
        return null;
    }

    /// <summary>
    /// Writes a package of the given skin folders. Returns the path written. The file appears only when complete
    /// (written to a temporary name, then renamed), so a failed export never leaves a half package behind.
    /// </summary>
    public static string Write(string outputDirectory, string name, IReadOnlyList<string> skinFolders, string generator,
        Action<int, int>? progress = null, Func<string, IEnumerable<string>>? filesOf = null,
        string author = "", string description = "", string? previewFile = null, PackageExtras? extras = null,
        bool allowUnofficialBuilds = false, string? readmeFile = null, string? coverFile = null)
    {
        bool editedOnly = filesOf != null;
        filesOf ??= PackableFiles;
        string? problem = ValidateName(name);
        if (problem != null) throw new ArgumentException(problem);
        if (skinFolders.Count == 0 && (extras == null || extras.Files.Count == 0)) throw new ArgumentException("Nothing selected.");
        name = name.Trim();

        var files = new List<(string Folder, string Source)>();
        foreach (string folder in skinFolders)
            foreach (string file in filesOf(folder))
                files.Add((folder, file));

        var info = new PackageInfo
        {
            Name = name,
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            Generator = generator,
            Contents = editedOnly ? "edited-only" : string.Empty,
            Author = Clip(author, MaxAuthorLength),
            Description = Clip(description, MaxDescriptionLength),
            Redistribution = allowUnofficialBuilds ? RedistributionAny : RedistributionOfficialOnly
        };
        byte[]? preview = previewFile != null && File.Exists(previewFile) ? File.ReadAllBytes(previewFile) : null;
        if (preview != null) info.Preview = PreviewEntry;
        byte[]? cover = coverFile != null && File.Exists(coverFile) ? File.ReadAllBytes(coverFile) : null;
        if (cover != null) info.Cover = CoverEntry;
        byte[]? readme = null;
        if (readmeFile != null && File.Exists(readmeFile))
        {
            string notes = Clip(DecodeText(File.ReadAllBytes(readmeFile)).Replace("\u000D\u000A", "\u000A").Replace('\u000D', '\u000A'), MaxReadmeLength);
            if (notes.Trim().Length > 0) { readme = new UTF8Encoding(false).GetBytes(notes); info.Readme = ReadmeEntry; }
        }
        if (extras != null) { info.Texts = extras.Texts; info.Fonts = extras.Fonts; info.LoadingPictures = extras.LoadingPictures; info.OtherPictures = extras.OtherPictures; }

        byte[] zipBytes;
        using (var zipStream = new MemoryStream())
        {
            using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                int done = 0;
                foreach (string folder in skinFolders)
                {
                    string folderName = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    var skin = new PackageSkin { Folder = folderName };
                    foreach ((string owner, string source) in files.Where(f => f.Folder == folder))
                    {
                        byte[] data = File.ReadAllBytes(source);
                        string relative = Path.GetRelativePath(folder, source).Replace('\\', '/');
                        bool png = source.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
                        ZipArchiveEntry entry = zip.CreateEntry("skins/" + folderName + "/" + relative,
                            png ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                        using (Stream stream = entry.Open()) stream.Write(data, 0, data.Length);
                        skin.Files++;
                        skin.Bytes += data.Length;
                        if (png) skin.Images++;
                        progress?.Invoke(++done, files.Count);
                    }
                    info.Skins.Add(skin);
                }
                foreach ((string extraPath, byte[] data) in extras?.Files ?? new List<(string, byte[])>())
                {
                    string entryName = extraPath.Replace('\\', '/').TrimStart('/');
                    if (entryName.Split('/').Any(part => part is ".." or "." || IsCensoredName(part))) continue;
                    bool packed = entryName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || entryName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                  entryName.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || entryName.EndsWith(".otf", StringComparison.OrdinalIgnoreCase);
                    ZipArchiveEntry entry = zip.CreateEntry(entryName, packed ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                    using Stream stream = entry.Open();
                    stream.Write(data, 0, data.Length);
                }
                if (preview != null)
                {
                    ZipArchiveEntry picture = zip.CreateEntry(PreviewEntry, CompressionLevel.NoCompression);
                    using Stream stream = picture.Open();
                    stream.Write(preview, 0, preview.Length);
                }
                if (cover != null)
                {
                    ZipArchiveEntry banner = zip.CreateEntry(CoverEntry, CompressionLevel.NoCompression);
                    using Stream stream = banner.Open();
                    stream.Write(cover, 0, cover.Length);
                }
                if (readme != null)
                {
                    ZipArchiveEntry notes = zip.CreateEntry(ReadmeEntry, CompressionLevel.Optimal);
                    using Stream stream = notes.Open();
                    stream.Write(readme, 0, readme.Length);
                }
                ZipArchiveEntry manifest = zip.CreateEntry("package.json", CompressionLevel.Optimal);
                using (Stream stream = manifest.Open())
                    JsonSerializer.Serialize(stream, info, new JsonSerializerOptions { WriteIndented = true });
            }
            zipBytes = zipStream.ToArray();
        }

        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] iv = RandomNumberGenerator.GetBytes(16);
        byte[] payload;
        using (Aes aes = Aes.Create())
        {
            aes.Key = DeriveKey(salt);
            aes.IV = iv;
            payload = aes.EncryptCbc(zipBytes, iv, PaddingMode.PKCS7);
        }

        Directory.CreateDirectory(outputDirectory);
        string path = UniquePath(outputDirectory, name);
        string temporary = path + ".partial";
        using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            void Put(byte[] bytes) { output.Write(bytes, 0, bytes.Length); hash.AppendData(bytes); }
            Put(Magic);
            Put(BitConverter.GetBytes(FormatVersion));
            Put(BitConverter.GetBytes((ushort)0));
            Put(salt);
            Put(iv);
            Put(BitConverter.GetBytes((ulong)payload.LongLength));
            Put(payload);
            byte[] checksum = hash.GetHashAndReset();
            output.Write(checksum, 0, checksum.Length);
        }
        File.Move(temporary, path);
        return path;
    }

    /// <summary>Reads a package fully into memory (nothing is extracted to disk). Throws on any corruption.</summary>
    public static (PackageInfo Info, List<PackageEntry> Entries) Read(string path)
    {
        byte[] all = File.ReadAllBytes(path);
        if (all.Length < HeaderLength + 32 || !all.AsSpan(0, 8).SequenceEqual(Magic))
            throw new InvalidDataException("Not a SkinForger skin package.");
        ushort version = BitConverter.ToUInt16(all, 8);
        if (version != FormatVersion) throw new InvalidDataException($"Unsupported package format {version}.");
        byte[] salt = all.AsSpan(12, 16).ToArray();
        byte[] iv = all.AsSpan(28, 16).ToArray();
        long length = (long)BitConverter.ToUInt64(all, 44);
        if (length < 0 || HeaderLength + length + 32 != all.Length) throw new InvalidDataException("Package length mismatch.");

        byte[] expected = SHA256.HashData(all.AsSpan(0, (int)(HeaderLength + length)));
        if (!expected.AsSpan().SequenceEqual(all.AsSpan((int)(HeaderLength + length), 32)))
            throw new InvalidDataException("Package checksum mismatch (damaged or modified).");

        byte[] zipBytes;
        using (Aes aes = Aes.Create())
        {
            aes.Key = DeriveKey(salt);
            zipBytes = aes.DecryptCbc(all.AsSpan(HeaderLength, (int)length), iv, PaddingMode.PKCS7);
        }

        var entries = new List<PackageEntry>();
        PackageInfo? info = null;
        using (var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read))
        {
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                using Stream stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                if (entry.FullName == "package.json")
                    info = JsonSerializer.Deserialize<PackageInfo>(buffer.ToArray());
                else
                    entries.Add(new PackageEntry(entry.FullName, buffer.ToArray()));
            }
        }
        if (info == null || info.Format != "sfskin") throw new InvalidDataException("Package description missing.");
        return (info, entries);
    }

    private static string Clip(string? text, int max)
    {
        string value = (text ?? string.Empty).Trim().Replace("\r", string.Empty);
        return value.Length > max ? value[..max] : value;
    }

    private static byte[] DeriveKey(byte[] salt)
    {
        byte[] material = new byte[KeySeed.Length + salt.Length];
        KeySeed.CopyTo(material, 0);
        salt.CopyTo(material, KeySeed.Length);
        return SHA256.HashData(material);
    }
}
