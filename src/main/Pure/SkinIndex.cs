namespace SkinForger.Main.Pure;

/// <summary>
/// Which image of a skin folder replaces which part.
///
/// Images are matched BY PART (atlas|region|attachment) through the folder's manifest.json, never by file
/// name: the Dump numbers two parts that share a name (hair_B / hair_B_2) in the order it meets them, and
/// that order can differ between two dumps. SkinForger.Core matches the same way, for the same reason.
///
/// A skin folder that has no manifest cannot be used: without it there is no way to say what an image is
/// for. That is reported rather than guessed at.
/// Pure (no Unity).
/// </summary>
public sealed class SkinIndex
{
    private readonly Dictionary<string, string> _byIdentity = new(StringComparer.Ordinal);

    private SkinIndex(string directory) => Directory = directory;

    /// <summary>The skin folder this index describes.</summary>
    public string Directory { get; }

    /// <summary>Why the folder could not be read, or null.</summary>
    public string? Problem { get; private init; }

    /// <summary>How many parts this skin replaces.</summary>
    public int Count => _byIdentity.Count;

    /// <summary>Every part this skin replaces.</summary>
    public IEnumerable<string> Identities => _byIdentity.Keys;

    /// <summary>The image for a part, or null when this skin leaves it alone.</summary>
    public string? FileFor(string identity) => _byIdentity.TryGetValue(identity, out string? file) ? file : null;

    /// <summary>
    /// Reads a skin folder. <paramref name="exists"/> says whether a file is there (the game passes a real
    /// check; a test can pass its own), so an image a manifest names but that was never written — a
    /// censored part, for instance — is left out instead of failing later.
    /// </summary>
    public static SkinIndex Read(string directory, Func<string, bool> exists, Func<string, RegionManifest> readManifest)
    {
        string manifestPath = Path.Combine(directory, RegionManifest.FileName);
        if (!exists(manifestPath))
            return new SkinIndex(directory) { Problem = $"{RegionManifest.FileName} is missing" };

        RegionManifest manifest;
        try { manifest = readManifest(manifestPath); }
        catch (Exception exception) { return new SkinIndex(directory) { Problem = $"{RegionManifest.FileName}: {exception.Message}" }; }

        var index = new SkinIndex(directory);
        foreach (RegionManifestEntry entry in manifest.Regions)
        {
            if (DumpNaming.IsCensoredName(entry.File) || DumpNaming.IsCensoredName(entry.AttachmentName)) continue;
            string relative = entry.File.Replace('/', '\\').TrimStart('\\');
            string path = Path.Combine(directory, relative);
            if (!exists(path)) continue;                     // listed but never written
            index._byIdentity[entry.Identity] = path;
        }
        return index;
    }

    /// <summary>
    /// The images to use for a skeleton's parts, with the LAST skin that has a part winning. The order is
    /// the loading order SkinForger already uses: the player's own work on top, then the skins they turned
    /// on, newest first, then the game's own art for anything no skin touches.
    /// </summary>
    public static Dictionary<string, string> Layer(IEnumerable<SkinIndex> lowestFirst)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (SkinIndex skin in lowestFirst)
            foreach (string identity in skin.Identities)
            {
                string? file = skin.FileFor(identity);
                if (file != null) result[identity] = file;
            }
        return result;
    }
}
