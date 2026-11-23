using System.Text.Json;
using System.Text.Json.Nodes;

namespace SkinForger.Main.Pure;

/// <summary>
/// ONE anchor file for a whole folder: <c>&lt;atlas folder&gt;\anchors.json</c>.
///
/// The rule, decided 2026-11-20: every image in a folder shares ONE JSON. A frame-by-frame skin needs
/// nothing clever, just a fixed anchor, and hundreds of sidecars are noise. A frame-by-frame skin is
/// hundreds of pictures, and hundreds of sidecars next to them is noise the author has to look at every
/// day. The anchor is the one thing that must be written down, so it is written down once, in a file whose
/// shape says so:
///
/// <code>
/// {
///   "version": 2,
///   "origin": "top-left",
///   "parts": {
///     "body_1": { "anchorX": 200.98, "anchorY": 610.47, "rotation": 0 },
///     "eye_L":  { "anchorX": 83.5,   "anchorY": 59,     "rotation": 0 }
///   }
/// }
/// </code>
///
/// A key is the image's name without ".png". An entry holds exactly the fields of
/// <see cref="RegionAnchorFile"/>, so everything downstream (the pin, the author's nudge, canvasAnchor,
/// Scale) works in a folder file as it does in a sidecar, and SkinForger.Core's AutoAnchor writes its
/// result here too.
///
/// A per-image <c>&lt;part&gt;.json</c> still WINS where one exists: an author who wants to fiddle with
/// one picture on its own keeps that option, and no existing skin changes meaning. Pure (no Unity).
/// </summary>
public static class FolderAnchorFile
{
    public const string FileName = "anchors.json";

    /// <summary>The folder file that covers an image.</summary>
    public static string PathOf(string imagePath) =>
        Path.Combine(Path.GetDirectoryName(imagePath) ?? string.Empty, FileName);

    /// <summary>The key an image has inside the folder file ("...\body_1.png" → "body_1").</summary>
    public static string KeyOf(string imagePath) => Path.GetFileNameWithoutExtension(imagePath);

    /// <summary>True when this folder keeps its anchors in one file.</summary>
    public static bool Exists(string imagePath) => SkinFiles.Exists(PathOf(imagePath));

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = null };

    /// <summary>The whole folder file as JSON, or null when there is none / it is broken.</summary>
    public static JsonObject? Read(string imagePath)
    {
        string path = PathOf(imagePath);
        if (!SkinFiles.Exists(path)) return null;
        try { return JsonNode.Parse(SkinFiles.ReadAllBytes(path)) as JsonObject; }
        catch (JsonException) { return null; }   // a hand-edited broken file must not break the load
    }

    /// <summary>This image's entry inside the folder file, or null when the folder or the entry is absent.</summary>
    public static JsonObject? ReadEntry(string imagePath) =>
        Read(imagePath) is { } root && root["parts"] is JsonObject parts
            ? parts[KeyOf(imagePath)] as JsonObject
            : null;

    /// <summary>This image's entry read as an anchor sidecar, or null when there is none.</summary>
    public static RegionAnchorFile? ReadEntryAsSidecar(string imagePath)
    {
        JsonObject? entry = ReadEntry(imagePath);
        if (entry == null) return null;
        try
        {
            RegionAnchorFile? file = JsonSerializer.Deserialize<RegionAnchorFile>(entry.ToJsonString(), Options);
            if (file == null) return null;
            // The convention is stated once, at the top of the folder file; an entry inherits it.
            if (string.IsNullOrEmpty(file.Origin) || entry["origin"] == null)
                file.Origin = Read(imagePath)?["origin"]?.GetValue<string>() ?? RegionAnchorFile.TopLeftOrigin;
            return file;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Puts an entry into the folder file, creating the file when it is not there yet. Existing entries and
    /// unknown members are left exactly as they are: this is the author's file.
    /// </summary>
    public static void WriteEntry(string imagePath, JsonObject entry, Action<string, string>? write = null)
    {
        string path = PathOf(imagePath);
        JsonObject root = Read(imagePath) ?? new JsonObject
        {
            ["version"] = RegionAnchorFile.CurrentVersion,
            ["origin"] = RegionAnchorFile.TopLeftOrigin,
        };
        if (root["parts"] is not JsonObject parts)
        {
            parts = new JsonObject();
            root["parts"] = parts;
        }
        parts[KeyOf(imagePath)] = entry;
        string text = root.ToJsonString(Options);
        if (write != null) { write(path, text); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".partial";
        File.WriteAllText(temporary, text);
        if (File.Exists(path)) File.Delete(path);
        File.Move(temporary, path);
    }

    /// <summary>
    /// Puts many entries in at once: the file is read and written ONCE, not once per image. A Dump writes
    /// hundreds of parts in a row, and re-reading the whole folder file for each of them was both slow and
    /// a lot of rewriting of the same text (2026-11-20).
    ///
    /// Each entry is MERGED over whatever is there: the Dump's own fields (anchorX / anchorY / rotation)
    /// are refreshed, and everything the author added -- extraLeft / extraTop, a nudge, canvasAnchor -- is
    /// left exactly as it is. A per-image sidecar, when one exists, still wins over all of this, so an
    /// image someone chose to keep on its own file is not touched here either.
    /// </summary>
    public static int WriteEntries(string folder, IEnumerable<(string Key, JsonObject Entry)> entries,
        Func<string, bool>? hasOwnSidecar = null, Action<string, string>? write = null)
    {
        string path = Path.Combine(folder, FileName);
        JsonObject root;
        try
        {
            root = (SkinFiles.Exists(path) ? JsonNode.Parse(SkinFiles.ReadAllBytes(path)) as JsonObject : null)
                   ?? new JsonObject { ["version"] = RegionAnchorFile.CurrentVersion, ["origin"] = RegionAnchorFile.TopLeftOrigin };
        }
        catch (JsonException)
        {
            return 0;   // a hand-edited broken file is the author's; never overwrite it with a fresh one
        }
        if (root["parts"] is not JsonObject parts)
        {
            parts = new JsonObject();
            root["parts"] = parts;
        }

        int written = 0;
        foreach ((string key, JsonObject entry) in entries)
        {
            if (hasOwnSidecar != null && hasOwnSidecar(key)) continue;
            if (parts[key] is JsonObject existing)
            {
                foreach (KeyValuePair<string, JsonNode?> field in entry)
                    existing[field.Key] = field.Value == null ? null : JsonNode.Parse(field.Value.ToJsonString());
            }
            else parts[key] = entry;
            written++;
        }
        if (written == 0) return 0;

        string text = root.ToJsonString(Options);
        if (write != null) { write(path, text); return written; }
        Directory.CreateDirectory(folder);
        string temporary = path + ".partial";
        File.WriteAllText(temporary, text);
        if (File.Exists(path)) File.Delete(path);
        File.Move(temporary, path);
        return written;
    }

    /// <summary>The anchor an image needs, as the Dump writes it, as a folder-file entry.</summary>
    public static JsonObject EntryForDump(float anchorX, float anchorY, float rotation) => new()
    {
        ["anchorX"] = Math.Round(anchorX, 4),
        ["anchorY"] = Math.Round(anchorY, 4),
        ["rotation"] = Math.Round(rotation, 4),
    };
}
