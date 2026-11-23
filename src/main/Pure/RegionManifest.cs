using System.Text.Json;
using System.Text.Json.Serialization;

namespace SkinForger.Main.Pure;

/// <summary>
/// <c>SkinCreator\Images\&lt;Costume&gt;\manifest.json</c>: one entry per dumped part, telling the runtime which
/// PNG belongs to which atlas region / attachment and where that region sits in the game's atlas.
///
/// The format is FROZEN: players' existing dump folders and every published .sfskin must keep working, and
/// SkinForger.Core reads it (PackageMounts.PartsByFile matches images by the triple atlas|region|attachment).
/// Keys are camelCase, "file" uses backslashes, and unknown members are preserved on load so a newer field
/// written by another version is not silently dropped.
/// Pure (no Unity), checked by Tests\ against a real manifest.
/// </summary>
public sealed class RegionManifest
{
    public const int CurrentVersion = 1;
    public const string FileName = "manifest.json";

    [JsonPropertyName("version")] public int Version { get; set; } = CurrentVersion;
    [JsonPropertyName("regions")] public List<RegionManifestEntry> Regions { get; set; } = new();

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = null
    };

    /// <summary>Through SkinFiles, so a manifest inside a mounted .sfskin package is read too.</summary>
    public static RegionManifest Read(string path) => Parse(SkinFiles.ReadAllBytes(path));

    public static RegionManifest Parse(byte[] json)
    {
        RegionManifest? manifest = JsonSerializer.Deserialize<RegionManifest>(json, Options)
                                   ?? throw new InvalidDataException("manifest.json is empty.");
        manifest.Regions ??= new List<RegionManifestEntry>();
        return manifest;
    }

    /// <summary>Writes the manifest, complete file at once (a half-written manifest would break the next load).</summary>
    public void Write(string path)
    {
        string temporary = path + ".partial";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(this, Options));
        if (File.Exists(path)) File.Delete(path);
        File.Move(temporary, path);
    }

    /// <summary>The entry of an image, by its path relative to the costume folder (either separator).</summary>
    public RegionManifestEntry? ByFile(string relativePath)
    {
        string wanted = Normalize(relativePath);
        return Regions.FirstOrDefault(r => Normalize(r.File) == wanted);
    }

    /// <summary>The entry of a part, by the identity Core matches on: atlas + region + attachment.</summary>
    public RegionManifestEntry? ByPart(string atlas, string regionName, string attachmentName) =>
        Regions.FirstOrDefault(r =>
            string.Equals(r.Atlas, atlas, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.RegionName, regionName, StringComparison.Ordinal) &&
            string.Equals(r.AttachmentName, attachmentName, StringComparison.Ordinal));

    /// <summary>Adds the entry, or replaces the one with the same file (a re-dump of one part).</summary>
    public void Put(RegionManifestEntry entry)
    {
        string wanted = Normalize(entry.File);
        int index = Regions.FindIndex(r => Normalize(r.File) == wanted);
        if (index < 0) Regions.Add(entry); else Regions[index] = entry;
    }

    /// <summary>"folder\name.png", lower case: the comparison key for a manifest file path.</summary>
    public static string Normalize(string? path) =>
        (path ?? string.Empty).Replace('/', '\\').TrimStart('\\').ToLowerInvariant();

    /// <summary>The part identity Core uses, "atlas|region|attachment".</summary>
    public static string PartIdentity(string atlas, string regionName, string attachmentName) =>
        BarePageName(atlas) + "|" + regionName + "|" + attachmentName;

    /// <summary>
    /// An atlas page reduced to its bare name: no folder, no extension. The SAME page reaches SkinForger
    /// under two spellings — the Spine page's own name ("1_rock_main_show.png") and the texture name the
    /// loading path uses ("1_rock_main_show") — and an identity is a plain string, so without this they
    /// produce two different identities for one part.
    ///
    /// That cost the 2026-11-19 16:31 trial: the replacement images were read and composed (the atlas grew
    /// 512x2048 -> 512x3728) and then NOT ONE region was pointed at them, because the lookup was keyed by
    /// the page spelling while the parts were keyed by the texture spelling. The log said
    /// "0 replaced, 0 rebased" with no error: every piece worked and the two halves could not find each
    /// other. Canonicalising here, in the one place an identity is ever built, is what keeps them equal.
    /// </summary>
    public static string BarePageName(string? atlas)
    {
        if (string.IsNullOrEmpty(atlas)) return string.Empty;
        string name = atlas!.Replace('\\', '/');
        int slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];
        int dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    /// <summary>The atlas a part identity belongs to (its first field).</summary>
    public static string AtlasOf(string identity)
    {
        int bar = identity.IndexOf('|');
        return bar < 0 ? identity : identity[..bar];
    }
}

/// <summary>One dumped part. Field order follows the file on disk, so a rewritten manifest stays readable in a diff.</summary>
public sealed class RegionManifestEntry
{
    /// <summary>Path of the PNG relative to the costume folder, with backslashes: "1_rock_main_show\hand_left.png".</summary>
    [JsonPropertyName("file")] public string File { get; set; } = string.Empty;
    /// <summary>Atlas (Spine skin / prefab) the part belongs to: the subfolder name.</summary>
    [JsonPropertyName("atlas")] public string Atlas { get; set; } = string.Empty;
    [JsonPropertyName("attachmentName")] public string AttachmentName { get; set; } = string.Empty;
    [JsonPropertyName("regionName")] public string RegionName { get; set; } = string.Empty;
    /// <summary>What the Image page shows; the Dump numbers a repeated name (hair_B, hair_B_2).</summary>
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = string.Empty;

    /// <summary>The region's rectangle in the game's atlas, top-left origin.</summary>
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }

    /// <summary>True when the atlas stores the part turned (then <see cref="Degrees"/> says by how much).</summary>
    [JsonPropertyName("rotate")] public bool Rotate { get; set; }
    [JsonPropertyName("degrees")] public int Degrees { get; set; }

    /// <summary>Size of the part as it is drawn (before the atlas rotation).</summary>
    [JsonPropertyName("originalWidth")] public int OriginalWidth { get; set; }
    [JsonPropertyName("originalHeight")] public int OriginalHeight { get; set; }

    /// <summary>Spine's own trim offsets of the region.</summary>
    [JsonPropertyName("offsetX")] public float OffsetX { get; set; }
    [JsonPropertyName("offsetY")] public float OffsetY { get; set; }

    /// <summary>The region's UV rectangle in the atlas, as Spine mapped it.</summary>
    [JsonPropertyName("u")] public float U { get; set; }
    [JsonPropertyName("v")] public float V { get; set; }
    [JsonPropertyName("u2")] public float U2 { get; set; }
    [JsonPropertyName("v2")] public float V2 { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore] public PixelRect PackedRect => new(X, Y, Width, Height);
    [JsonIgnore] public string Identity => RegionManifest.PartIdentity(Atlas, RegionName, AttachmentName);
}
