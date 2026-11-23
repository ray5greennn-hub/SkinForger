using System.Globalization;
using System.Text.Json;
using SkinForger.R47.Core;

namespace SkinForger.R412.Runtime;

internal sealed record TargetSpec(string AtlasFolder, string AttachmentName, CanvasSpec Canvas, string Source, string Png)
{
    internal string Describe() =>
        $"{AtlasFolder}/{AttachmentName} original={Canvas.OriginalWidth}x{Canvas.OriginalHeight} png={Canvas.ExpandedWidth}x{Canvas.ExpandedHeight} " +
        $"left={Canvas.Left:0.###} top={Canvas.Top:0.###} right={Canvas.Right:0.###} bottom={Canvas.Bottom:0.###} source={Source}";
}

/// <summary>
/// Finds expanded PNGs for every character. Two sources, same contract (Main's DesiredWorld):
///  - Main registry: the ReplacementPlacement that SkinForger Main itself computed for this attachment
///    (BaseNormal*, Normal*, Anchor*, DefaultAnchor*, Offset*); authoritative in gameplay;
///  - disk: SkinForger\Skins\*\manifest.json (originalWidth/Height) + PNG header size + JSON anchor/offset
///    (default anchor = original centre); used for renderers Main does not drive (e.g. Preview).
/// The original image's top-left lies at (anchorX - defaultAnchorX - offsetX, anchorY - defaultAnchorY - offsetY).
/// </summary>
internal static class SpecResolver
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Dictionary<string, List<TargetSpec>>> Disk = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, TargetSpec> Registry = new(StringComparer.OrdinalIgnoreCase);
    private static int _version;
    internal static Action<string, string>? Log;
    internal static int Version => Volatile.Read(ref _version);

    private static string Key(string folder, string name) => folder + "|" + name;

    internal static void Invalidate(string reason)
    {
        lock (Gate) { Disk.Clear(); _version++; }
        Log?.Invoke("SPEC_CACHE_INVALIDATED", reason);
    }

    /// <summary>Records Main's own placement for one attachment (gameplay).</summary>
    internal static void Register(string regionPath, string attachmentName, object placement)
    {
        string? folder = Path.GetFileName(Path.GetDirectoryName(regionPath ?? string.Empty));
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(attachmentName)) return;
        int baseW = ReadInt(placement, "BaseNormalWidth"), baseH = ReadInt(placement, "BaseNormalHeight");
        int w = ReadInt(placement, "NormalWidth"), h = ReadInt(placement, "NormalHeight");
        // 2026-11-16: offsetX/Y of a mesh part = rigid move of the whole part (PartTransforms), not a canvas shift.
        double left = ReadFloat(placement, "AnchorX") - ReadFloat(placement, "DefaultAnchorX");
        double top = ReadFloat(placement, "AnchorY") - ReadFloat(placement, "DefaultAnchorY");
        var spec = new CanvasSpec(baseW, baseH, w, h, left, top);
        string key = Key(folder, attachmentName);
        bool valid = spec.Problem() == null;
        lock (Gate)
        {
            Registry.TryGetValue(key, out TargetSpec? old);
            if (!valid)
            {
                if (old != null) { Registry.Remove(key); _version++; }
                return;
            }
            var entry = new TargetSpec(folder, attachmentName, spec, "main-placement", regionPath ?? string.Empty);
            if (old != null && old.Canvas == spec) return;
            Registry[key] = entry;
            _version++;
        }
        if (spec.IsExpanded)
            Log?.Invoke("SPEC_FROM_MAIN", new TargetSpec(folder, attachmentName, spec, "main-placement", regionPath ?? "").Describe());
    }

    // ------------------------------------------------------------ gameplay UV truth (used by Preview)

    internal sealed record UvTruth(int TextureId, int TextureWidth, int TextureHeight, MeshCanvasSession.Affine Map);
    private static readonly Dictionary<string, UvTruth> Truths = new(StringComparer.OrdinalIgnoreCase);
    private static int _truthVersion;
    internal static int TruthVersion => Volatile.Read(ref _truthVersion);

    /// <summary>Records the r -> atlas UV mapping of a gameplay attachment (rendered correctly in game).</summary>
    internal static void RecordTruth(string folder, string name, UvTruth truth)
    {
        lock (Gate)
        {
            string key = Key(folder, name);
            if (Truths.TryGetValue(key, out UvTruth? old) && old.TextureId == truth.TextureId && old.Map.Same(truth.Map)) return;
            Truths[key] = truth;
            _truthVersion++;
        }
    }

    internal static UvTruth? Truth(string folder, string name)
    {
        lock (Gate) return Truths.TryGetValue(Key(folder, name), out UvTruth? t) ? t : null;
    }

    /// <summary>Names of attachments with an expanded PNG for this atlas folder (Main registry + disk).</summary>
    internal static HashSet<string> CandidateNames(string folder)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        lock (Gate)
        {
            foreach (TargetSpec spec in Registry.Values)
                if (string.Equals(spec.AtlasFolder, folder, StringComparison.OrdinalIgnoreCase)) names.Add(spec.AttachmentName);
            foreach (string name in DiskFor(folder).Keys) names.Add(name);
        }
        return names;
    }

    /// <summary>Spec whose expanded size matches the texel size Spine currently maps the region to.</summary>
    internal static TargetSpec? Match(string folder, string name, double measuredWidth, double measuredHeight, out string detail)
    {
        const double tol = 1.6;
        bool Fits(TargetSpec s) => Math.Abs(s.Canvas.ExpandedWidth - measuredWidth) <= tol && Math.Abs(s.Canvas.ExpandedHeight - measuredHeight) <= tol;
        lock (Gate)
        {
            if (Registry.TryGetValue(Key(folder, name), out TargetSpec? main) && Fits(main)) { detail = "main"; return main; }
            if (DiskFor(folder).TryGetValue(name, out List<TargetSpec>? list))
                foreach (TargetSpec s in list)
                    if (Fits(s)) { detail = "disk"; return s; }
            detail = main != null ? $"main-size-mismatch png={main.Canvas.ExpandedWidth}x{main.Canvas.ExpandedHeight}" : "no-matching-png";
            return null;
        }
    }

    private static Dictionary<string, List<TargetSpec>> DiskFor(string folder)
    {
        if (Disk.TryGetValue(folder, out var cached)) return cached;
        var result = new Dictionary<string, List<TargetSpec>>(StringComparer.Ordinal);
        Disk[folder] = result;
        try
        {
            string skins = global::SkinForger.SkinPackages.SkinForgerPaths.Creator; // 2026-11-14: creator workspace
            IEnumerable<string> skinDirs = (Directory.Exists(skins) ? Directory.GetDirectories(skins) : Array.Empty<string>())
                .Concat(global::SkinForger.SkinPackages.PackageMounts.Skins.Select(s => s.VirtualDirectory));   // + .sfskin packages (in memory)
            foreach (string skinDir in skinDirs)
            {
                string manifestPath = Path.Combine(skinDir, "manifest.json");
                if (!global::SkinForger.SkinPackages.VirtualFiles.FileExists(manifestPath)) continue;
                using JsonDocument manifest = JsonDocument.Parse(global::SkinForger.SkinPackages.VirtualFiles.ReadAllText(manifestPath));
                if (!manifest.RootElement.TryGetProperty("regions", out JsonElement regions)) continue;
                foreach (JsonElement region in regions.EnumerateArray())
                {
                    string file = Str(region, "file");
                    string atlas = Str(region, "atlas");
                    string name = Str(region, "attachmentName");
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(file)) continue;
                    if (global::SkinForger.HexieBlock.HexiePolicy.IsHexieName(file) || global::SkinForger.HexieBlock.HexiePolicy.IsHexieName(name) ||
                        global::SkinForger.HexieBlock.HexiePolicy.IsHexieName(atlas)) continue; // Steam-only: hexie never read
                    string fileFolder = Path.GetFileName(Path.GetDirectoryName(file.Replace('\\', Path.DirectorySeparatorChar)) ?? "");
                    if (!string.Equals(atlas, folder, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(fileFolder, folder, StringComparison.OrdinalIgnoreCase)) continue;
                    string png = Path.Combine(skinDir, file);
                    if (!global::SkinForger.SkinPackages.VirtualFiles.FileExists(png) || !TryPngSize(png, out int w, out int h)) continue;
                    int ow = Int(region, "originalWidth"), oh = Int(region, "originalHeight");
                    if (ow <= 0 || oh <= 0) continue;
                    double left = (w - ow) / 2.0, top = (h - oh) / 2.0;   // no JSON: assume centred
                    string source = "disk-centred";
                    string json = Path.ChangeExtension(png, ".json");
                    // Same-size PNG: the original fills the canvas. Dumped JSON anchors are not image centres, so they
                    // cannot be used without Main's default anchor (R4-9: 303 false rejections).
                    if (w == ow && h == oh) { left = 0; top = 0; source = "disk-same-size"; }
                    else if (global::SkinForger.SkinPackages.VirtualFiles.FileExists(json))
                    {
                        using JsonDocument j = JsonDocument.Parse(global::SkinForger.SkinPackages.VirtualFiles.ReadAllText(json));
                        JsonElement root = j.RootElement;
                        string origin = Str(root, "origin");
                        if (root.TryGetProperty("anchorX", out _) && root.TryGetProperty("anchorY", out _) &&
                            (string.IsNullOrEmpty(origin) || origin.Equals("top-left", StringComparison.OrdinalIgnoreCase)))
                        {
                            left = Dbl(root, "anchorX") - ow / 2.0;   // offset = rigid move (PartTransforms)
                            top = Dbl(root, "anchorY") - oh / 2.0;
                            source = "disk-json";
                        }
                        else if (!string.IsNullOrEmpty(origin)) source = "disk-centred(origin=" + origin + ")";
                    }
                    var spec = new CanvasSpec(ow, oh, w, h, left, top);
                    var entry = new TargetSpec(folder, name, spec, source, png);
                    string? problem = spec.Problem();
                    if (problem != null && source.StartsWith("disk-json", StringComparison.Ordinal))
                    {
                        // The sidecar's anchor does not place the original inside the author's canvas, so
                        // it cannot be read as "anchorX - ow/2" (see the note above: a dumped anchor is not
                        // an image centre, and without Main's default anchor there is nothing to subtract).
                        // Falling back to centred is what this resolver already does when there is NO
                        // sidecar at all, and it is the difference between the part being expanded sanely
                        // and not being expanded at all.
                        //
                        // Measured on the player's elfin (2026-11-20): five of its six expanded parts were
                        // rejected this way -- wing_l's anchor is (0,130) in a 130x90 image -- so the
                        // biggest pieces of the elfin never expanded.
                        var centred = new CanvasSpec(ow, oh, w, h, (w - ow) / 2.0, (h - oh) / 2.0);
                        if (centred.Problem() == null)
                        {
                            Log?.Invoke("SPEC_RECENTRED", entry.Describe() + " problem=" + problem +
                                        " -> centred left=" + centred.Left + " top=" + centred.Top);
                            spec = centred;
                            source = "disk-centred(anchor-unusable)";
                            entry = new TargetSpec(folder, name, spec, source, png);
                            problem = null;
                        }
                    }
                    if (problem != null) { Log?.Invoke("SPEC_REJECTED", entry.Describe() + " problem=" + problem); continue; }
                    if (!result.TryGetValue(name, out var list)) result[name] = list = new List<TargetSpec>();
                    list.Add(entry);
                    if (spec.IsExpanded) Log?.Invoke("SPEC_FROM_DISK", entry.Describe());
                }
            }
        }
        catch (Exception exception)
        {
            Log?.Invoke("SPEC_DISK_ERROR", folder + " " + exception.GetBaseException().Message);
        }
        return result;
    }

    private static bool TryPngSize(string path, out int width, out int height)
    {
        width = height = 0;
        Span<byte> header = stackalloc byte[24];
        using Stream stream = global::SkinForger.SkinPackages.VirtualFiles.OpenRead(path);   // package PNGs live in memory
        if (stream.Read(header) != 24 || header[0] != 0x89 || header[1] != (byte)'P' || header[12] != (byte)'I' || header[13] != (byte)'H') return false;
        width = header[16] << 24 | header[17] << 16 | header[18] << 8 | header[19];
        height = header[20] << 24 | header[21] << 16 | header[22] << 8 | header[23];
        return width > 0 && height > 0;
    }

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static int Int(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
    private static double Dbl(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) ? v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String => double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 0,
            _ => 0
        } : 0;

    private static int ReadInt(object instance, string name) => Convert.ToInt32(Read(instance, name), CultureInfo.InvariantCulture);
    private static double ReadFloat(object instance, string name) => Convert.ToDouble(Read(instance, name), CultureInfo.InvariantCulture);
    private static object Read(object instance, string name)
    {
        var property = instance.GetType().GetProperty(name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        if (property == null) throw new MissingMemberException(instance.GetType().FullName, name);
        return property.GetValue(instance) ?? throw new InvalidOperationException("placement " + name + " is null");
    }
}
