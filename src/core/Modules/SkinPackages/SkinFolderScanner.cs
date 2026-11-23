using System.Text.Json;

namespace SkinForger.SkinPackages;

public enum SkinFolderState
{
    Ready,
    MissingFiles,
    BrokenFiles,
    NoEdits
}

public sealed record SkinFolderStatus(string Name, string Path, SkinFolderState State, string Detail, int Images, long Bytes)
{
    public bool Exportable => State == SkinFolderState.Ready;

    public string StateText => State switch
    {
        SkinFolderState.Ready => "Ready",
        SkinFolderState.MissingFiles => "Missing Files",
        SkinFolderState.NoEdits => "No Edits",
        _ => "Broken Files"
    };
}

/// <summary>
/// Checks every skin folder in SkinCreator before export. Pure .NET (testable outside the game).
///   Ready         manifest.json is valid and every PNG that will be packed is a real PNG.
///   Missing Files manifest.json is missing, or there is no PNG to pack.
///   Broken Files  manifest.json cannot be read, or a PNG is damaged.
/// A manifest entry without its PNG is not an error: deleting a PNG is how a creator keeps that part original.
/// </summary>
public static class SkinFolderScanner
{
    public static List<SkinFolderStatus> Scan(string creatorRoot)
    {
        var result = new List<SkinFolderStatus>();
        if (!Directory.Exists(creatorRoot)) return result;
        foreach (string folder in Directory.GetDirectories(creatorRoot).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            string name = System.IO.Path.GetFileName(folder);
            if (name.StartsWith(".") || name.StartsWith("_") || name.Equals("replaced", StringComparison.OrdinalIgnoreCase) ||
                NotACostume.Contains(name) || SfSkinFormat.IsCensoredName(name))
                continue;
            result.Add(Check(folder));
        }
        return result;
    }

    /// <summary>
    /// Folders under SkinCreator\Images that are not a costume, so a costume's verdict says nothing about
    /// them: Other (loose pictures, packed as their own section of a .sfskin) and Cursor (dump only). Their
    /// manifest.json lists "pictures", not "regions", so without this they were judged as a broken skin
    /// folder -- Other already read "manifest.json has no regions", and Cursor joined it on 2026-11-22 when
    /// the cursor dump started writing a manifest of its own.
    /// </summary>
    private static readonly HashSet<string> NotACostume = new(StringComparer.OrdinalIgnoreCase) { "Other", "Cursor" };

    public static SkinFolderStatus Check(string folder)
    {
        string name = System.IO.Path.GetFileName(folder);
        try
        {
            List<string> files = SfSkinFormat.PackableFiles(folder).ToList();
            List<string> pngs = files.Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).ToList();
            long bytes = files.Sum(f => new FileInfo(f).Length);

            string manifest = System.IO.Path.Combine(folder, "manifest.json");
            if (!File.Exists(manifest))
                return new SkinFolderStatus(name, folder, SkinFolderState.MissingFiles, "manifest.json missing", pngs.Count, bytes);
            if (pngs.Count == 0)
                return new SkinFolderStatus(name, folder, SkinFolderState.MissingFiles, "no images", 0, bytes);

            int regions;
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifest));
                if (!document.RootElement.TryGetProperty("regions", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
                    return new SkinFolderStatus(name, folder, SkinFolderState.BrokenFiles, "manifest.json has no regions", pngs.Count, bytes);
                regions = list.GetArrayLength();
            }
            catch (Exception)
            {
                return new SkinFolderStatus(name, folder, SkinFolderState.BrokenFiles, "manifest.json unreadable", pngs.Count, bytes);
            }

            List<string> broken = pngs.Where(p => !IsPng(p)).ToList();
            if (broken.Count > 0)
            {
                string first = System.IO.Path.GetRelativePath(folder, broken[0]);
                string detail = broken.Count == 1 ? $"damaged image: {first}" : $"{broken.Count} damaged images (first: {first})";
                return new SkinFolderStatus(name, folder, SkinFolderState.BrokenFiles, detail, pngs.Count, bytes);
            }

            // Packages carry only what the creator changed; untouched parts show the player's own game art.
            List<string> edited = EditDetector.EditedImages(folder);
            if (edited.Count == 0)
                return new SkinFolderStatus(name, folder, SkinFolderState.NoEdits,
                    $"no edited images yet ({pngs.Count} untouched originals)", 0, 0);
            long editedBytes = EditDetector.ExportFiles(folder).Sum(f => new FileInfo(f).Length);
            return new SkinFolderStatus(name, folder, SkinFolderState.Ready,
                $"{edited.Count} edited of {pngs.Count} images, {FormatSize(editedBytes)}", edited.Count, editedBytes);
        }
        catch (Exception exception)
        {
            return new SkinFolderStatus(name, folder, SkinFolderState.BrokenFiles, "cannot read folder: " + exception.Message, 0, 0);
        }
    }

    /// <summary>PNG signature plus a valid IHDR chunk with a non-zero size.</summary>
    public static bool IsPng(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> header = stackalloc byte[24];
            if (stream.Read(header) != 24) return false;
            ReadOnlySpan<byte> signature = stackalloc byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            if (!header[..8].SequenceEqual(signature)) return false;
            if (header[12] != (byte)'I' || header[13] != (byte)'H' || header[14] != (byte)'D' || header[15] != (byte)'R') return false;
            int width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
            int height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
            return width > 0 && height > 0;
        }
        catch
        {
            return false;
        }
    }

    public static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.0} MB" : $"{Math.Max(1, bytes / 1024)} KB";
}
