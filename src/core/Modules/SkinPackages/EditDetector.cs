using System.Security.Cryptography;
using System.Text.Json;

namespace SkinForger.SkinPackages;

/// <summary>
/// Decides which images in a creator folder the creator actually changed. Used by the package export (only these
/// go into a .sfskin) and by the package overlay. Pure .NET (testable outside the game).
///
/// An image counts as edited when any of these holds:
///   1. its folder's replaced\ history has a backup of it (Reload saw it change);
///   2. its content differs from the hash SkinForger's backup state recorded (changed since the last Reload / Dump,
///      so an export right after editing works without pressing Reload first);
///   3. its size differs from the original region in manifest.json (an expanded image);
///   4. its sidecar JSON was customized (Scale, MovementAmplitude / X / Y / Rotate, offset or rotation not default);
///   5. every pixel of it is transparent -- the way a part is made INVISIBLE (2026-11-21). This is the one
///      edit none of the four rules above can see: same size, no sidecar value, and once a Reload records
///      the erased file the content hash matches too. See <see cref="PngAlpha"/>.
/// Freshly dumped, untouched originals match none of these.
/// </summary>
public static class EditDetector
{
    private static readonly object Sync = new();
    private static string _stateFile = string.Empty;
    private static DateTime _stateTime;
    private static Dictionary<string, string> _state = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>UserData\SkinForger\replaced-state-v1.json (path -> SHA-256). Empty string disables rule 2.</summary>
    public static string StateFile { get; set; } = string.Empty;

    public static List<string> EditedImages(string creatorDirectory)
    {
        var edits = new List<string>();
        if (!Directory.Exists(creatorDirectory)) return edits;
        Dictionary<string, (int W, int H)> originalSizes = OriginalSizes(creatorDirectory);
        Dictionary<string, string> state = LoadState();
        foreach (string png in SfSkinFormat.PackableFiles(creatorDirectory).Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
            if (IsEdited(png, creatorDirectory, originalSizes, state)) edits.Add(png);
        return edits;
    }

    /// <summary>Files a package of this folder contains: manifest.json, every edited image and its sidecar JSON.</summary>
    public static List<string> ExportFiles(string creatorDirectory)
    {
        var files = new List<string>();
        string manifest = Path.Combine(creatorDirectory, "manifest.json");
        if (File.Exists(manifest)) files.Add(manifest);
        foreach (string png in EditedImages(creatorDirectory))
        {
            files.Add(png);
            string json = Path.ChangeExtension(png, ".json");
            if (File.Exists(json)) files.Add(json);
        }
        return files;
    }

    private static bool IsEdited(string png, string root, Dictionary<string, (int W, int H)> originalSizes, Dictionary<string, string> state)
    {
        // Skin switch icons (Icon\*.png, 2026-11-15) have no manifest entry, and ReplacedBackup snapshots freshly
        // exported icons as "changed": an icon is edited when it differs from the game image SkinForger exported.
        string relative = Path.GetRelativePath(root, png);
        if (relative.StartsWith(RoleSkins.IconFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            relative.StartsWith(RoleSkins.LegacyIconFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return IconEdited(png);
        if (HasBackup(png)) return true;
        if (state.TryGetValue(Path.GetFullPath(png), out string? known) && !string.IsNullOrEmpty(known) &&
            !string.Equals(known, Hash(png), StringComparison.OrdinalIgnoreCase))
            return true;
        if (originalSizes.TryGetValue(Path.GetRelativePath(root, png), out var original) && ReadPngSize(png, out int w, out int h) &&
            (w != original.W || h != original.H))
            return true;
        if (SidecarCustomized(Path.ChangeExtension(png, ".json"))) return true;
        // Erased on purpose: a part the author wanted gone. Last, because it reads the whole image.
        return PngAlpha.IsFullyTransparent(png);
    }

    private static bool SidecarCustomized(string json)
    {
        if (!File.Exists(json)) return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(json));
            JsonElement root = document.RootElement;
            static double Get(JsonElement e, string name, double fallback) =>
                e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;
            const double tolerance = 1e-6;
            return Math.Abs(Get(root, "Scale", 1) - 1) > tolerance ||
                   new[] { "MovementAmplitude", "X", "Y", "Rotate", "offsetX", "offsetY", "rotation" }
                       .Any(name => Math.Abs(Get(root, name, 0)) > tolerance);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>UserData\SkinForger\icon-originals.json (icon path -> SHA-256 of the exported game image).</summary>
    public static string IconOriginalsFile { get; set; } = string.Empty;
    private static Dictionary<string, string>? _iconOriginals;

    private static Dictionary<string, string> IconOriginals()
    {
        lock (Sync)
        {
            if (_iconOriginals != null) return _iconOriginals;
            _iconOriginals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!string.IsNullOrEmpty(IconOriginalsFile) && File.Exists(IconOriginalsFile))
                    foreach (var pair in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(IconOriginalsFile)) ?? new())
                        _iconOriginals[pair.Key] = pair.Value;
            }
            catch { }
            return _iconOriginals;
        }
    }

    /// <summary>Records the exported game image of an icon (its content is "not edited").</summary>
    public static void RecordIconOriginal(string png, byte[] content)
    {
        lock (Sync)
        {
            Dictionary<string, string> originals = IconOriginals();
            originals[Path.GetFullPath(png)] = Convert.ToHexString(SHA256.HashData(content));
            try
            {
                if (string.IsNullOrEmpty(IconOriginalsFile)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(IconOriginalsFile)!);
                File.WriteAllText(IconOriginalsFile, JsonSerializer.Serialize(originals, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }

    /// <summary>An icon is edited when its content is not the recorded game image (unknown icons count as edited).</summary>
    public static bool IconEdited(string png)
    {
        if (!IconOriginals().TryGetValue(Path.GetFullPath(png), out string? original)) return true;
        try { return !string.Equals(original, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(png))), StringComparison.OrdinalIgnoreCase); }
        catch { return true; }
    }

    private static readonly Dictionary<string, (DateTime Time, Dictionary<string, (int W, int H)> Sizes)> SizeCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One image of a creator folder (same rules as <see cref="EditedImages"/>); manifest sizes cached per folder.</summary>
    public static bool IsEditedImage(string png, string creatorDirectory)
    {
        string manifest = Path.Combine(creatorDirectory, "manifest.json");
        DateTime time = File.Exists(manifest) ? File.GetLastWriteTimeUtc(manifest) : DateTime.MinValue;
        Dictionary<string, (int W, int H)> sizes;
        lock (Sync)
        {
            if (!SizeCache.TryGetValue(creatorDirectory, out var cached) || cached.Time != time)
                SizeCache[creatorDirectory] = cached = (time, OriginalSizes(creatorDirectory));
            sizes = cached.Sizes;
        }
        Dictionary<string, string> state = LoadState();
        if (IsEdited(png, creatorDirectory, sizes, state)) return true;

        // 2026-11-18: a folder SkinForger never recorded a baseline for cannot be judged by rule 2 at all,
        // and an image edited in place at its original size then matches none of the four rules. That is
        // exactly what happened to the other skins: replaced-state-v1.json held 194 entries for Bassist's
        // own 1_rock_main_show and ZERO for char_1_rock_muscle, so every edit in the muscle folder was read
        // as "untouched" and dropped by the other-skin filter — the images simply never appeared.
        // Without a baseline the honest answer is "cannot tell", and the safe one is to use the creator's
        // image: showing work that was not edited costs a little placing, hiding work that was is a bug.
        //
        // The question is asked of the image's OWN folder, not the costume folder. A costume mixes both
        // kinds: Bassist\1_rock_main_show has 194 recorded hashes while Bassist\char_1_rock_muscle has
        // none, so asking about "Bassist" answers yes and hides exactly the case this is here to catch
        // (2026-11-18: the first version of this fix asked the costume folder and therefore did nothing).
        return !HasBaseline(Path.GetDirectoryName(png) ?? creatorDirectory);
    }

    private static readonly Dictionary<string, (DateTime Stamp, bool Has)> BaselineCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether SkinForger has ever recorded what the game's own images looked like for this folder. False
    /// for a folder whose images were dumped by a path that does not write the backup state (the other
    /// skins of a character), which is what makes rule 2 unusable there.
    /// </summary>
    public static bool HasBaseline(string creatorDirectory)
    {
        if (string.IsNullOrEmpty(creatorDirectory)) return false;
        string root;
        try { root = Path.GetFullPath(creatorDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar; }
        catch { return false; }

        Dictionary<string, string> state = LoadState();
        lock (Sync)
        {
            if (BaselineCache.TryGetValue(root, out (DateTime Stamp, bool Has) cached) && cached.Stamp == _stateTime)
                return cached.Has;
            bool has = state.Keys.Any(key => key.StartsWith(root, StringComparison.OrdinalIgnoreCase));
            BaselineCache[root] = (_stateTime, has);
            return has;
        }
    }

    public static bool HasBackup(string png)
    {
        string folder = Path.GetDirectoryName(png)!;
        string history = Path.Combine(folder, "replaced");
        if (!Directory.Exists(history)) return false;
        string stem = Path.GetFileNameWithoutExtension(png);
        if (File.Exists(Path.Combine(history, stem + ".png"))) return true;
        foreach (string backup in Directory.GetFiles(history, stem + "_*.png"))
        {
            string suffix = Path.GetFileNameWithoutExtension(backup)[(stem.Length + 1)..];
            // "hair_1.png" in replaced\ is a backup of "hair.png" only if no real part is itself named "hair_1.png".
            if (suffix.Length > 0 && suffix.All(char.IsDigit) && !File.Exists(Path.Combine(folder, Path.GetFileName(backup))))
                return true;
        }
        return false;
    }

    private static Dictionary<string, (int W, int H)> OriginalSizes(string creatorDirectory)
    {
        var sizes = new Dictionary<string, (int W, int H)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string manifest = Path.Combine(creatorDirectory, "manifest.json");
            if (!File.Exists(manifest)) return sizes;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifest));
            if (!document.RootElement.TryGetProperty("regions", out JsonElement regions)) return sizes;
            foreach (JsonElement region in regions.EnumerateArray())
            {
                string file = region.TryGetProperty("file", out JsonElement f) ? f.GetString() ?? "" : "";
                int w = region.TryGetProperty("originalWidth", out JsonElement ow) && ow.TryGetInt32(out int a) ? a : 0;
                int h = region.TryGetProperty("originalHeight", out JsonElement oh) && oh.TryGetInt32(out int b) ? b : 0;
                if (file.Length > 0 && w > 0 && h > 0)
                    sizes[file.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)] = (w, h);
            }
        }
        catch { /* unreadable manifest: other rules still apply */ }
        return sizes;
    }

    private static Dictionary<string, string> LoadState()
    {
        lock (Sync)
        {
            string file = StateFile;
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            DateTime time = File.GetLastWriteTimeUtc(file);
            if (file == _stateFile && time == _stateTime) return _state;
            try
            {
                var state = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? new Dictionary<string, string>();
                _state = new Dictionary<string, string>(state, StringComparer.OrdinalIgnoreCase);
                _stateFile = file;
                _stateTime = time;
            }
            catch
            {
                _state = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            return _state;
        }
    }

    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using SHA256 sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    private static bool ReadPngSize(string path, out int width, out int height)
    {
        width = height = 0;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var header = new byte[24];
            if (stream.Read(header, 0, 24) != 24 || header[12] != (byte)'I' || header[15] != (byte)'R') return false;
            width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
            height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
            return width > 0 && height > 0;
        }
        catch { return false; }
    }
}
