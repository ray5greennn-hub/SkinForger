using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using HarmonyLib;
using MelonLoader;

namespace SkinForger.SkinPackages;

/// <summary>
/// The expansion canvas of a part is the PNG the artist drew. Nothing may make it bigger (2026-11-18).
///
/// What went wrong: within ONE session, the same part was placed with a canvas that grew on every rebuild
/// of its atlas, while the file on disk never changed —
///
///     headhoop     171x173 -> 180x180 -> 185x184 -> 187x186   (file: 171x173)
///     hair_front   248x320 -> 277x339 -> 292x349 -> 299x354   (file: 248x320)
///
/// The growth is exactly self-feeding: each build's anchor is the middle of the PREVIOUS build's canvas
/// (171/2 = 85.5 gives the next width 180, 180/2 = 90 gives 185, and so on, matching every number above).
/// So the placement is derived from the region as the last build left it rather than from the drawing. The
/// expansion band is built for that canvas, so the surplus ring hangs outside the art and draws as thin
/// stretched slivers of edge colour — the blue threads beside the Maid's ribbons.
///
/// This is a prefix on the two methods that hand a placement to a region / attachment. When the placement's
/// canvas is LARGER than the PNG it claims to be placing, the placement is wrong by definition — a canvas
/// cannot be bigger than the image it holds — and it is corrected to the file: the PNG's size, and the
/// anchor the sidecar JSON carries. A placement that agrees with the file is never touched, so a part that
/// is placed correctly is left exactly as it is.
///
/// Deliberately a correction and not a cache: it compares against the artist's file every time, so editing
/// a PNG and reloading does the right thing immediately, and nothing stale can be pinned.
/// </summary>
internal static class PlacementCanvasClamp
{
    private const string Prefix = "[SkinForge][CanvasClamp]";

    private static PropertyInfo? _normalWidth, _normalHeight, _anchorX, _anchorY;
    private static FieldInfo? _normalWidthField, _normalHeightField, _anchorXField, _anchorYField;
    private static bool _resolved;

    /// <summary>png path -> (size, anchor) read from disk; cleared when the file's write time changes.</summary>
    private static readonly Dictionary<string, (long Ticks, int W, int H, float? AnchorX, float? AnchorY)> Files =
        new(StringComparer.OrdinalIgnoreCase);

    private static int _corrected;
    private static readonly HashSet<string> Logged = new(StringComparer.OrdinalIgnoreCase);

    internal static int Install(HarmonyLib.Harmony harmony)
    {
        try
        {
            Type host = AccessTools.TypeByName("SkinForger.SkinForgerMod") ?? throw new TypeLoadException("SkinForgerMod");
            int patched = 0;
            foreach (MethodInfo method in AccessTools.GetDeclaredMethods(host))
            {
                if (method.Name != "ApplyRegionPlacementToAttachment" && method.Name != "ApplyRegionPlacementToRegion") continue;
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(PlacementCanvasClamp), nameof(ClampPrefix)) { priority = Priority.First });
                patched++;
            }
            if (patched == 0) MelonLogger.Warning($"{Prefix} placement methods not found: a canvas that grows is not corrected");
            else MelonLogger.Msg($"{Prefix} watching {patched} placement path(s): a canvas may never be bigger than its PNG");
            return patched;
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"{Prefix} not installed: {exception.GetBaseException().Message}");
            return 0;
        }
    }

    /// <summary>
    /// Harmony hands the arguments by position; both methods take the placement and, where there is one, the
    /// PNG path. Anything unexpected simply leaves the call alone.
    /// </summary>
    private static void ClampPrefix(object[] __args)
    {
        try
        {
            object? placement = null;
            string? png = null;
            foreach (object? argument in __args)
            {
                if (argument is string text && text.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) png ??= text;
                else if (argument != null && placement == null && LooksLikePlacement(argument)) placement = argument;
            }
            if (placement == null || string.IsNullOrEmpty(png)) return;
            Correct(placement, png!);
        }
        catch (Exception) { /* never take a placement down */ }
    }

    private static bool LooksLikePlacement(object candidate)
    {
        Type type = candidate.GetType();
        return type.GetProperty("NormalWidth") != null || type.GetField("NormalWidth") != null;
    }

    private static void Correct(object placement, string png)
    {
        Resolve(placement);
        if (!TryFile(png, out int fileW, out int fileH, out float? ax, out float? ay)) return;

        int w = ReadInt(placement, _normalWidth, _normalWidthField);
        int h = ReadInt(placement, _normalHeight, _normalHeightField);
        if (w <= 0 || h <= 0) return;
        if (w <= fileW && h <= fileH) return;   // agrees with the drawing (or smaller): leave it alone

        WriteInt(placement, _normalWidth, _normalWidthField, fileW);
        WriteInt(placement, _normalHeight, _normalHeightField, fileH);
        if (ax is { } pinX) WriteFloat(placement, _anchorX, _anchorXField, pinX);
        if (ay is { } pinY) WriteFloat(placement, _anchorY, _anchorYField, pinY);

        _corrected++;
        lock (Logged)
            if (Logged.Add(png))
                MelonLogger.Msg($"{Prefix} {Path.GetFileName(Path.GetDirectoryName(png))}/{Path.GetFileName(png)}: " +
                                $"canvas {w}x{h} is bigger than the drawing {fileW}x{fileH} - corrected to the file" +
                                (ax is { } x && ay is { } y ? $" (anchor {x:0.##},{y:0.##} from the JSON)" : string.Empty));
    }

    private static bool TryFile(string png, out int w, out int h, out float? anchorX, out float? anchorY)
    {
        w = h = 0;
        anchorX = anchorY = null;
        try
        {
            if (VirtualFiles.IsVirtual(png) || !File.Exists(png)) return false;
            long ticks = File.GetLastWriteTimeUtc(png).Ticks;
            lock (Files)
            {
                if (Files.TryGetValue(png, out var cached) && cached.Ticks == ticks)
                {
                    (_, w, h, anchorX, anchorY) = cached;
                    return w > 0 && h > 0;
                }
            }
            if (!ReadPngSize(png, out w, out h)) return false;
            string json = Path.ChangeExtension(png, ".json");
            if (File.Exists(json) && JsonNode.Parse(File.ReadAllText(json)) is JsonObject node)
            {
                if (node["anchorX"]?.GetValue<double>() is { } x) anchorX = (float)x;
                if (node["anchorY"]?.GetValue<double>() is { } y) anchorY = (float)y;
            }
            lock (Files) Files[png] = (ticks, w, h, anchorX, anchorY);
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>PNG header only: width and height are the first fields of IHDR.</summary>
    private static bool ReadPngSize(string path, out int width, out int height)
    {
        width = height = 0;
        try
        {
            using FileStream stream = File.OpenRead(path);
            Span<byte> head = stackalloc byte[24];
            if (stream.Read(head) != 24) return false;
            if (head[0] != 0x89 || head[1] != 'P' || head[2] != 'N' || head[3] != 'G') return false;
            width = (head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19];
            height = (head[20] << 24) | (head[21] << 16) | (head[22] << 8) | head[23];
            return width > 0 && height > 0;
        }
        catch (Exception) { return false; }
    }

    private static void Resolve(object placement)
    {
        if (_resolved) return;
        _resolved = true;
        Type type = placement.GetType();
        _normalWidth = type.GetProperty("NormalWidth");
        _normalHeight = type.GetProperty("NormalHeight");
        _anchorX = type.GetProperty("AnchorX");
        _anchorY = type.GetProperty("AnchorY");
        _normalWidthField = type.GetField("NormalWidth");
        _normalHeightField = type.GetField("NormalHeight");
        _anchorXField = type.GetField("AnchorX");
        _anchorYField = type.GetField("AnchorY");
    }

    private static int ReadInt(object target, PropertyInfo? property, FieldInfo? field)
    {
        object? value = property != null && property.CanRead ? property.GetValue(target) : field?.GetValue(target);
        return value is int number ? number : 0;
    }

    private static void WriteInt(object target, PropertyInfo? property, FieldInfo? field, int value)
    {
        try
        {
            if (property?.GetSetMethod(nonPublic: true) != null) property.SetValue(target, value);
            else field?.SetValue(target, value);
        }
        catch (Exception) { }
    }

    private static void WriteFloat(object target, PropertyInfo? property, FieldInfo? field, float value)
    {
        try
        {
            if (property?.GetSetMethod(nonPublic: true) != null) property.SetValue(target, value);
            else field?.SetValue(target, value);
        }
        catch (Exception) { }
    }
}
