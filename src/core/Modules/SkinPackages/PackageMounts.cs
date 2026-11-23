using System.Text;

namespace SkinForger.SkinPackages;

/// <summary>The Skin Library skin of one character / elfin: every enabled package that changes it, layered.</summary>
/// <param name="PackageLabel">Button label: the highest enabled skin ("A" or "A & 1 more").</param>
/// <param name="Layers">Names of the enabled skins in this costume, highest priority first.</param>
/// <param name="Overlaid">Images from SkinCreator\&lt;Costume&gt; that the creator edited and that are drawn on top.</param>
/// <param name="WithCreator">True for the "skins + my work" variant (used only after the player clicked Root).</param>
public sealed record MountedSkin(string Costume, string VirtualDirectory, string PackageLabel, IReadOnlyList<string> Layers, int Overlaid = 0, bool WithCreator = false)
{
    public string ButtonLabel => Overlaid > 0 ? $"{PackageLabel} +{Overlaid}" : PackageLabel;
}

/// <summary>A package that could not be mounted (damaged, unsupported, unreadable).</summary>
public sealed record MountProblem(string PackageFile, string Reason);

/// <summary>
/// Reads every SkinForger/Skins/*.sfskin (decoded in memory only; never written to disk), publishes them to the
/// <see cref="SkinLibrary"/>, and mounts ONE layered skin per character / elfin into <see cref="VirtualFiles"/>:
///   Skins\sfpkg-library\&lt;Costume&gt;
/// (a fixed safe name: Main refuses resource paths whose folders contain words such as "atlas", "cache" or "backup").
///
/// Layers of a costume, lowest first: the enabled packages in enable order, then the creator's edited images in
/// SkinCreator\&lt;Costume&gt;. A higher layer replaces only the images it contains. Images are matched by PART
/// (manifest: atlas + region + attachment), not by file name: two parts can share a name and Dump numbers the second
/// one (hair_B / hair_B_2), and that numbering can differ between two dumps. Parts no layer has show the game's art.
/// Pure .NET (testable outside the game).
/// </summary>
public static class PackageMounts
{
    public const string LibraryFolder = "sfpkg-library";
    /// <summary>Skin Library + the creator's Root work on top (selected only by clicking Root).</summary>
    public const string MyWorkFolder = "sfpkg-library-mywork";
    /// <summary>Where the packages' Images\Other pictures are mounted (2026-11-20).</summary>
    public const string OtherPicturesFolder = "sfpkg-library-other";

    /// <summary>False since 2026-11-16: Root = the creator folder only (no "skins + my work" variant).</summary>
    public static bool MixCreatorIntoSkins { get; set; } = false;

    /// <summary>The Skin Library skin of a costume: skins only, or skins + the creator's work.</summary>
    public static MountedSkin? Find(string? costume, bool withCreator) =>
        string.IsNullOrEmpty(costume) ? null : Skins.FirstOrDefault(s => s.WithCreator == withCreator && s.Costume.Equals(costume, StringComparison.OrdinalIgnoreCase));

    private sealed record Decoded(long Length, DateTime WriteUtc, SfSkinFormat.PackageInfo Info, List<SfSkinFormat.PackageEntry> Entries);

    private static readonly Dictionary<string, Decoded> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Sync = new();

    public static IReadOnlyList<MountedSkin> Skins { get; private set; } = Array.Empty<MountedSkin>();
    public static IReadOnlyList<MountProblem> Problems { get; private set; } = Array.Empty<MountProblem>();
    /// <summary>audio/&lt;Costume&gt;/&lt;file&gt; of the enabled packages, lowest priority first (SoundRuntime, 2026-11-17).</summary>
    public static IReadOnlyList<(string Path, byte[] Data)> PackageSounds { get; private set; } = Array.Empty<(string, byte[])>();
    /// <summary>
    /// The folder holding the enabled packages' Images\Other pictures, or null when none carry any.
    ///
    /// Kept SEPARATE from the creator's own Images\Other instead of merged into it. The creator's folder
    /// is a real one on disk, and mounting over it would hide every file in it -- a virtual directory
    /// answers for its whole path. The Main reads both and lets the creator's own win, which is the
    /// priority the Skin Library already documents: SkinCreator > enabled packages > the game's own art.
    /// </summary>
    public static string? OtherPictures { get; private set; }
    public static string MountedSignature { get; private set; } = string.Empty;
    /// <summary><see cref="SkinLibrary.Version"/> of the mounted layers (the library window compares it).</summary>
    public static int MountedLibraryVersion { get; private set; } = -1;

    /// <summary>Names, sizes and write times of the packages in the folder; changes whenever a package is added,
    /// removed or replaced (also while a copy is still growing).</summary>
    public static string Signature(string playerRoot)
    {
        if (!Directory.Exists(playerRoot)) return string.Empty;
        var text = new StringBuilder();
        foreach (string file in Directory.GetFiles(playerRoot, "*" + SfSkinFormat.Extension, SearchOption.TopDirectoryOnly)
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var info = new FileInfo(file);
            text.Append(info.Name).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
        }
        return text.ToString();
    }

    public static string MountedCreatorSignature { get; private set; } = string.Empty;

    /// <summary>Character / elfin folders in SkinCreator (added or deleted folders change the skin lists).</summary>
    public static string CreatorSignature(string creatorRoot)
    {
        if (string.IsNullOrEmpty(creatorRoot) || !Directory.Exists(creatorRoot)) return string.Empty;
        return string.Join("|", Directory.GetDirectories(creatorRoot).Select(Path.GetFileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
    }

    public static bool Changed(string playerRoot)
    {
        try { return !string.Equals(Signature(playerRoot), MountedSignature, StringComparison.Ordinal); }
        catch { return false; }
    }

    private static string _creatorRoot = string.Empty;

    /// <summary>True when a changed creator file belongs to a costume that the library also provides (overlay must be rebuilt).</summary>
    public static bool TouchesOverlay(IEnumerable<string> changedPaths)
    {
        if (string.IsNullOrEmpty(_creatorRoot) || Skins.Count == 0) return false;
        string[] dirs = Skins.Select(s => VirtualFiles.Normalize(Path.Combine(_creatorRoot, s.Costume)) + Path.DirectorySeparatorChar).Distinct().ToArray();
        return changedPaths.Any(p => dirs.Any(d => p.StartsWith(d, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Images in a creator folder that the creator changed (see <see cref="EditDetector"/>). Freshly
    /// dumped, untouched originals are not included, so a Dump never hides the skins underneath.</summary>
    public static List<string> CreatorEdits(string creatorDirectory) => EditDetector.EditedImages(creatorDirectory);

    public static void Mount(string playerRoot) => Mount(playerRoot, string.Empty);

    /// <param name="creatorRoot">SkinCreator: images the creator edited in &lt;creatorRoot&gt;\&lt;Costume&gt; are the top
    /// layer of that costume (empty = no creator layer).</param>
    public static void Mount(string playerRoot, string creatorRoot)
    {
        lock (Sync)
        {
            string signature = Signature(playerRoot);
            var problems = new List<MountProblem>();
            var library = new List<LibraryPackage>();
            var decodedOf = new Dictionary<string, Decoded>(StringComparer.OrdinalIgnoreCase);   // file name -> package
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string[] packages = Directory.Exists(playerRoot)
                ? Directory.GetFiles(playerRoot, "*" + SfSkinFormat.Extension, SearchOption.TopDirectoryOnly)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray()
                : Array.Empty<string>();

            foreach (string package in packages)
            {
                seen.Add(package);
                var entry = new LibraryPackage { File = package, FileName = Path.GetFileName(package), Name = Path.GetFileNameWithoutExtension(package) };
                try
                {
                    var info = new FileInfo(package);
                    entry.Bytes = info.Length;
                    entry.WriteUtc = info.LastWriteTimeUtc;
                    if (!Cache.TryGetValue(package, out Decoded? decoded) || decoded.Length != info.Length || decoded.WriteUtc != info.LastWriteTimeUtc)
                    {
                        (SfSkinFormat.PackageInfo packageInfo, List<SfSkinFormat.PackageEntry> entries) = SfSkinFormat.Read(package);
                        decoded = new Decoded(info.Length, info.LastWriteTimeUtc, packageInfo, entries);
                        Cache[package] = decoded;
                    }
                    Describe(entry, decoded);
                    // The creator's sharing choice (2026-11-17): a package made for another platform and not
                    // released by its creator for this one is not loaded. The images belong to the creator.
                    string? refused = SfSkinFormat.MayLoad(decoded.Info);
                    if (refused != null)
                    {
                        entry.Problem = refused;
                        problems.Add(new MountProblem(package, refused));
                        library.Add(entry);
                        continue;
                    }
                    decodedOf[entry.FileName] = decoded;
                }
                catch (Exception exception)
                {
                    Cache.Remove(package);
                    entry.Problem = exception is InvalidDataException ? exception.Message : "Cannot read: " + exception.GetBaseException().Message;
                    problems.Add(new MountProblem(package, exception.GetBaseException().Message));
                }
                library.Add(entry);
            }
            foreach (string stale in Cache.Keys.Where(k => !seen.Contains(k)).ToList()) Cache.Remove(stale);

            SkinLibrary.Update(library);
            int libraryVersion = SkinLibrary.Version;
            List<LibraryPackage> enabled = SkinLibrary.EnabledInOrder();   // lowest priority first
            var packageSounds = new List<(string, byte[])>();
            foreach (LibraryPackage package in enabled)
                if (decodedOf.TryGetValue(package.FileName, out Decoded? withSounds))
                    foreach (SfSkinFormat.PackageEntry soundEntry in withSounds.Entries)
                    {
                        string[] parts = soundEntry.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length == 3 && parts[0].Equals("audio", StringComparison.OrdinalIgnoreCase) && !SfSkinFormat.IsCensoredName(parts[2]))
                            packageSounds.Add((soundEntry.Path, soundEntry.Data));
                    }
            PackageSounds = packageSounds;

            // ---- one layered skin per costume
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var times = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            var skins = new List<MountedSkin>();
            string root = Path.Combine(playerRoot, LibraryFolder);
            IEnumerable<string> costumes = enabled.SelectMany(p => p.Costumes.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase);
            foreach (string costume in costumes)
            {
                List<(LibraryPackage Package, Dictionary<string, byte[]> Files)> layers = enabled
                    .Where(p => p.Costumes.ContainsKey(costume))
                    .Select(p => (p, FolderFiles(decodedOf[p.FileName], costume)))
                    .Where(l => l.Item2.Count > 0)
                    .ToList();
                // The lowest layer that has a manifest provides the folder layout (file names) of the result.
                int baseIndex = layers.FindIndex(l => l.Files.ContainsKey("manifest.json"));
                if (baseIndex < 0)
                {
                    foreach (var layer in layers) problems.Add(new MountProblem(layer.Package.File, $"{costume}: manifest.json missing"));
                    continue;
                }
                string directory = VirtualFiles.Normalize(Path.Combine(root, costume));
                (LibraryPackage basePackage, Dictionary<string, byte[]> baseFiles) = layers[baseIndex];
                foreach ((string relative, byte[] data) in baseFiles)
                {
                    files[Path.Combine(directory, relative)] = data;
                    times[Path.Combine(directory, relative)] = basePackage.WriteUtc;
                }
                Dictionary<string, string> fileOfPart = Invert(PartsByFile(baseFiles["manifest.json"]));

                for (int i = 0; i < layers.Count; i++)
                {
                    if (i == baseIndex) continue;
                    (LibraryPackage package, Dictionary<string, byte[]> layerFiles) = layers[i];
                    if (i < baseIndex)
                    {
                        // Below the base (no manifest of its own): its files keep their names, the base wins.
                        foreach ((string relative, byte[] data) in layerFiles)
                            if (!files.ContainsKey(Path.Combine(directory, relative))) { files[Path.Combine(directory, relative)] = data; times[Path.Combine(directory, relative)] = package.WriteUtc; }
                        continue;
                    }
                    Dictionary<string, string> partOf = layerFiles.TryGetValue("manifest.json", out byte[]? manifest) ? PartsByFile(manifest) : new();
                    foreach ((string relative, byte[] data) in layerFiles)
                    {
                        if (!relative.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;
                        string target = partOf.TryGetValue(relative, out string? part) && fileOfPart.TryGetValue(part, out string? mapped) ? mapped : relative;
                        Put(files, times, directory, target, data, package.WriteUtc);
                        if (layerFiles.TryGetValue(Path.ChangeExtension(relative, ".json"), out byte[]? anchor))
                            Put(files, times, directory, Path.ChangeExtension(target, ".json"), anchor, package.WriteUtc);
                    }
                }

                // Second variant "skins + my work" (only when SkinCreator\<Costume> exists): the same layers, then the
                // creator's edited images on top. It is used only when the player clicked Root (2026-11-15: Root must
                // never switch on by itself, e.g. when a skin is turned on while the character is on Original).
                int overlaid = 0;
                string creatorDirectory = string.IsNullOrEmpty(creatorRoot) ? string.Empty : Path.Combine(creatorRoot, costume);
                string? mine = null;
                // 2026-11-16 (user decision): clicking Root shows ONLY the creator folder; skins are not mixed under it.
                // The "skins + my work" variant is no longer built, so every Root redirect falls back to plain Root.
                if (MixCreatorIntoSkins && creatorDirectory.Length > 0 && Directory.Exists(creatorDirectory))
                {
                    mine = VirtualFiles.Normalize(Path.Combine(playerRoot, MyWorkFolder, costume));
                    string prefix = directory + Path.DirectorySeparatorChar;
                    foreach (string key in files.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
                    {
                        string copy = Path.Combine(mine, key[prefix.Length..]);
                        files[copy] = files[key];
                        times[copy] = times[key];
                    }
                }
                if (mine != null)
                {
                    try
                    {
                        List<string> edits = CreatorEdits(creatorDirectory);
                        Dictionary<string, string> creatorPartOf = edits.Count == 0 ? new() :
                            PartsByFile(File.ReadAllBytes(Path.Combine(creatorDirectory, "manifest.json")));
                        foreach (string png in edits)
                        {
                            string relative = Path.GetRelativePath(creatorDirectory, png);
                            string target = creatorPartOf.TryGetValue(relative, out string? part) && fileOfPart.TryGetValue(part, out string? packaged)
                                ? packaged : relative;
                            foreach ((string source, string destination) in new[] { (png, target), (Path.ChangeExtension(png, ".json"), Path.ChangeExtension(target, ".json")) })
                            {
                                if (!File.Exists(source)) continue;
                                Put(files, times, mine, destination, File.ReadAllBytes(source), File.GetLastWriteTimeUtc(source));
                            }
                            overlaid++;
                        }
                    }
                    catch (Exception exception)
                    {
                        problems.Add(new MountProblem(creatorDirectory, $"{costume}: creator layer skipped ({exception.GetBaseException().Message})"));
                    }
                }

                List<string> names = layers.Select(l => l.Package.Name).Reverse().ToList();   // highest first
                string label = names.Count == 1 ? names[0] : $"{names[0]} & {names.Count - 1} more";
                skins.Add(new MountedSkin(costume, directory, label, names, 0, WithCreator: false));
                if (mine != null) skins.Add(new MountedSkin(costume, mine, label, names, overlaid, WithCreator: true));
            }

            // ---- the pictures that belong to no costume (other/<file>), lowest priority first so the
            // highest enabled package wins, exactly as a costume's layers do.
            string otherRoot = VirtualFiles.Normalize(Path.Combine(playerRoot, OtherPicturesFolder));
            bool anyOther = false;
            foreach (LibraryPackage package in enabled)
            {
                if (!decodedOf.TryGetValue(package.FileName, out Decoded? decodedOther)) continue;
                foreach (SfSkinFormat.PackageEntry other in decodedOther.Entries)
                {
                    string[] parts = other.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 2 || !parts[0].Equals("other", StringComparison.OrdinalIgnoreCase)) continue;
                    if (SfSkinFormat.IsCensoredName(parts[1])) continue;
                    Put(files, times, otherRoot, parts[1], other.Data, package.WriteUtc);
                    anyOther = true;
                }
            }
            OtherPictures = anyOther ? otherRoot : null;

            var tree = new VirtualFiles.Tree(files, times);
            if (skins.Any(s => !s.WithCreator)) tree.Index(root);
            if (skins.Any(s => s.WithCreator)) tree.Index(Path.Combine(playerRoot, MyWorkFolder));
            if (anyOther) tree.Index(otherRoot);
            VirtualFiles.Replace(tree);
            Skins = skins;
            Problems = problems;
            MountedSignature = signature;
            MountedLibraryVersion = libraryVersion;
            _creatorRoot = creatorRoot;
            MountedCreatorSignature = CreatorSignature(creatorRoot);
        }
    }

    private static void Put(Dictionary<string, byte[]> files, Dictionary<string, DateTime> times, string directory, string relative, byte[] data, DateTime time)
    {
        string path = VirtualFiles.Normalize(Path.Combine(directory, relative));
        if (!path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
        files[path] = data;
        times[path] = time;
    }

    /// <summary>Files of skins/&lt;costume&gt;/ in a package: relative path (OS separators) -> content.</summary>
    private static Dictionary<string, byte[]> FolderFiles(Decoded decoded, string costume)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (SfSkinFormat.IsCensoredName(costume)) return result;
        foreach (SfSkinFormat.PackageEntry entry in decoded.Entries)
        {
            string[] parts = entry.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 || parts[0] != "skins" || !parts[1].Equals(costume, StringComparison.OrdinalIgnoreCase)) continue;
            if (parts.Any(p => p == ".." || p == "." || SfSkinFormat.IsCensoredName(p))) continue;
            result[Path.Combine(parts.Skip(2).ToArray())] = entry.Data;
        }
        return result;
    }

    /// <summary>Library card data: names, counts, characters, parts, preview.</summary>
    private static void Describe(LibraryPackage entry, Decoded decoded)
    {
        SfSkinFormat.PackageInfo info = decoded.Info;
        if (!string.IsNullOrWhiteSpace(info.Name)) entry.Name = info.Name.Trim();
        entry.Author = info.Author ?? string.Empty;
        entry.Description = info.Description ?? string.Empty;
        entry.MadeFor = (info.Game ?? string.Empty).Trim();
        entry.SharingAllowed = SfSkinFormat.AllowsUnofficialBuilds(info);
        entry.Text = info.Texts;
        entry.CreatedUtc = info.CreatedUtc ?? string.Empty;
        if (DateTime.TryParse(info.CreatedUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime created))
            entry.Created = created.ToLocalTime().ToString("yyyy-MM-dd");
        foreach (SfSkinFormat.PackageEntry file in decoded.Entries)
        {
            string path = file.Path;
            if (path.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) { entry.Audio++; continue; }
            if (path.StartsWith("text/Fonts/", StringComparison.OrdinalIgnoreCase)) { entry.Fonts++; continue; }
            if (path.StartsWith("text/", StringComparison.OrdinalIgnoreCase)) continue;   // counted from package.json (edited texts)
            if (path.StartsWith("loading/", StringComparison.OrdinalIgnoreCase)) { if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) entry.LoadingPictures++; continue; }
            if (path.StartsWith("other/", StringComparison.OrdinalIgnoreCase)) { if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) entry.OtherPictures++; continue; }
            if (!string.IsNullOrEmpty(info.Preview) && path.Equals(info.Preview, StringComparison.OrdinalIgnoreCase)) { entry.Preview = file.Data; continue; }
            if (!string.IsNullOrEmpty(info.Cover) && path.Equals(info.Cover, StringComparison.OrdinalIgnoreCase)) { entry.Cover = file.Data; continue; }
            if (!string.IsNullOrEmpty(info.Readme) && path.Equals(info.Readme, StringComparison.OrdinalIgnoreCase))
            {
                // Written as UTF-8 by the exporter whatever the creator's editor used; decoded the same way
                // regardless, so a package hand-made by another tool still reads.
                entry.Readme = SfSkinFormat.DecodeText(file.Data).Trim();
                continue;
            }
        }
        foreach (string costume in info.Skins.Select(s => s.Folder).Where(f => !SfSkinFormat.IsCensoredName(f)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Dictionary<string, byte[]> folder = FolderFiles(decoded, costume);
            List<string> images = folder.Keys.Where(k => k.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).ToList();
            if (images.Count == 0) continue;
            entry.Costumes[costume] = images.Count;
            entry.Images += images.Count;
            Dictionary<string, string> partOf = folder.TryGetValue("manifest.json", out byte[]? manifest) ? PartsByFile(manifest) : new();
            entry.Parts[costume] = new HashSet<string>(images.Select(i => partOf.TryGetValue(i, out string? part) ? part : i), StringComparer.Ordinal);
        }
    }

    /// <summary>manifest.json: image file (relative, OS separators) -> part identity "atlas|region|attachment".</summary>
    internal static Dictionary<string, string> PartsByFile(byte[] manifest)
    {
        var parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(manifest);
            if (!document.RootElement.TryGetProperty("regions", out var regions)) return parts;
            static string Str(System.Text.Json.JsonElement e, string name) =>
                e.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";
            foreach (var region in regions.EnumerateArray())
            {
                string file = Str(region, "file").Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                if (file.Length == 0) continue;
                string atlas = Str(region, "atlas");
                if (atlas.Length == 0) atlas = Path.GetDirectoryName(file) ?? "";
                parts[file] = atlas + "|" + Str(region, "regionName") + "|" + Str(region, "attachmentName");
            }
        }
        catch { /* unreadable manifest: fall back to relative paths */ }
        return parts;
    }

    private static Dictionary<string, string> Invert(Dictionary<string, string> parts)
    {
        var inverse = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string file, string part) in parts) inverse.TryAdd(part, file);
        return inverse;
    }
}
