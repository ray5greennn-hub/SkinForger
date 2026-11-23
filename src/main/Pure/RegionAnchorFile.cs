using System.Text.Json;
using System.Text.Json.Serialization;

namespace SkinForger.Main.Pure;

/// <summary>
/// The per-image sidecar next to a dumped PNG (<c>bady.png</c> → <c>bady.json</c>), format version 2.
///
/// The anchor is a pixel position measured from the image's TOP-LEFT corner, written by the Dump for the
/// ORIGINAL size of the part. It is what keeps a part in place; the whole canvas-expansion rule depends on
/// it (SF日志\SF角色扩框.txt part 6, and Core's AutoAnchor: "enlarging the canvas never moves the anchor,
/// unless the player changed it in the JSON").
///
/// The format is FROZEN and its mixed casing is deliberate: the v2 core fields are camelCase, the later
/// per-image controls are PascalCase, exactly as they appear in players' existing files. Unknown members are
/// preserved. Pure (no Unity).
/// </summary>
public sealed class RegionAnchorFile
{
    public const int CurrentVersion = 2;
    public const string TopLeftOrigin = "top-left";

    [JsonPropertyName("version")] public int Version { get; set; } = CurrentVersion;
    /// <summary>Always "top-left" for v2. An older file without it is not top-left and must not be trusted.</summary>
    [JsonPropertyName("origin")] public string Origin { get; set; } = TopLeftOrigin;

    [JsonPropertyName("anchorX")] public float AnchorX { get; set; }
    [JsonPropertyName("anchorY")] public float AnchorY { get; set; }
    [JsonPropertyName("rotation")] public float Rotation { get; set; }
    [JsonPropertyName("offsetX")] public float OffsetX { get; set; }
    [JsonPropertyName("offsetY")] public float OffsetY { get; set; }

    /// <summary>Per-image display scale (1 = unchanged).</summary>
    [JsonPropertyName("Scale")] public float Scale { get; set; } = 1f;
    /// <summary>Per-image animation movement amplitude (0 = unchanged).</summary>
    [JsonPropertyName("MovementAmplitude")] public float MovementAmplitude { get; set; }
    /// <summary>Per-image position offset in game units.</summary>
    [JsonPropertyName("X")] public float X { get; set; }
    [JsonPropertyName("Y")] public float Y { get; set; }
    /// <summary>Per-image extra rotation in degrees.</summary>
    [JsonPropertyName("Rotate")] public float RotateDegrees { get; set; }

    /// <summary>
    /// Which of the nine canvas positions the author enlarged the canvas FROM — that is, where the
    /// original drawing stayed. "top-left" / 左上 means the room was added on the right and the bottom.
    ///
    /// It is the author's own word for something only they know, and it is the one thing that settles a
    /// placement no measurement can: when the author REDREW a part rather than enlarging it, there is no
    /// original left in the picture to find, and the anchor alone may not place the canvas. SkinForger's
    /// own vocabulary, taken from SkinForger.Core's AutoAnchor, which has always read this field.
    /// "auto" or empty means "work it out", which is the default.
    /// </summary>
    [JsonPropertyName("canvasAnchor")] public string? CanvasAnchor { get; set; }

    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore] public bool IsTopLeft => string.Equals(Origin, TopLeftOrigin, StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = null };

    /// <summary>The sidecar path of an image ("...\bady.png" → "...\bady.json").</summary>
    public static string PathOf(string imagePath) => Path.ChangeExtension(imagePath, ".json");

    /// <summary>
    /// The anchor written down for an image: its own &lt;part&gt;.json when it has one, otherwise its entry in
    /// the folder's one <see cref="FolderAnchorFile"/> (2026-11-20: a frame-by-frame skin keeps all its
    /// anchors in a single anchors.json). A per-image file wins, so fiddling with one picture on its own
    /// still works.
    /// </summary>
    public static RegionAnchorFile? ReadIfExists(string imagePath)
    {
        string path = PathOf(imagePath);
        // SkinFiles, not File: a skin may be mounted from a .sfskin package and live only in memory.
        if (!SkinFiles.Exists(path)) return FolderAnchorFile.ReadEntryAsSidecar(imagePath);
        try { return JsonSerializer.Deserialize<RegionAnchorFile>(SkinFiles.ReadAllBytes(path), Options); }
        catch (JsonException) { return null; }   // a hand-edited broken sidecar must not break the load
    }

    public void Write(string imagePath)
    {
        string path = PathOf(imagePath);
        string temporary = path + ".partial";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(this, Options));
        if (File.Exists(path)) File.Delete(path);
        File.Move(temporary, path);
    }

    /// <summary>
    /// The sidecar the Dump writes for a part: the anchor is the centre of the part as the game places it,
    /// in top-left pixel coordinates of the ORIGINAL image.
    /// </summary>
    public static RegionAnchorFile ForDump(float anchorX, float anchorY, float rotation) => new()
    {
        Version = CurrentVersion,
        Origin = TopLeftOrigin,
        AnchorX = anchorX,
        AnchorY = anchorY,
        Rotation = rotation,
        Scale = 1f
    };

    /// <summary>
    /// True when the player has not touched the placement of this image: every control is still at its
    /// default. Core's AutoAnchor only re-bases the anchor of an enlarged canvas while this holds.
    /// </summary>
    public bool IsUntouched(float dumpedAnchorX, float dumpedAnchorY) =>
        OffsetX == 0f && OffsetY == 0f && X == 0f && Y == 0f && RotateDegrees == 0f &&
        (Scale == 1f || Scale == 0f) &&
        Nearly(AnchorX, dumpedAnchorX) && Nearly(AnchorY, dumpedAnchorY);

    private static bool Nearly(float a, float b) => Math.Abs(a - b) <= 0.01f;
}
