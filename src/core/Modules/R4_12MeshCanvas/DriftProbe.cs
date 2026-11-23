using Il2CppSpine;
using MelonLoader;

namespace SkinForger.R412.Runtime;

/// <summary>
/// Read-only. Watches ONE part across the builds of a session and prints every number a canvas is made of,
/// so the drift can be attributed instead of guessed (2026-11-18).
///
/// What is known already: within one session the same part is placed with a canvas that grows —
/// headhoop 171x173 -> 180x180 -> 185x184 -> 187x186 while the artist's PNG stays 171x173 — and the growth
/// fits w(n+1) = 94.5 + w(n)/2 exactly, i.e. each build anchors on the middle of the previous canvas.
///
/// What is NOT known, and what this answers: the canvas is derived from two numbers, AnchorX and
/// DefaultAnchorX, and the logs so far only show their difference. If DefaultAnchorX drifts, the shared
/// attachment / region is being mutated and the fix belongs there. If AnchorX drifts while DefaultAnchorX
/// stands still, the anchor is coming from somewhere that remembers the last build, and the fix belongs
/// there instead. Those are different repairs, so the next change waits for this line.
///
/// Prints only when a number changes, and only for parts whose canvas is bigger than their base, so a
/// session costs a handful of lines.
/// </summary>
internal static class DriftProbe
{
    private const string Prefix = "[SkinForger][Drift]";

    /// <summary>Last line printed per part, so only changes are logged.</summary>
    private static readonly Dictionary<string, string> Last = new(StringComparer.Ordinal);

    internal static void Watch(string name, object placement, Attachment attachment, string regionPath)
    {
        Type type = placement.GetType();
        int Int(string member) => Convert.ToInt32(type.GetProperty(member)?.GetValue(placement) ?? 0);
        double Flt(string member) => Convert.ToDouble(type.GetProperty(member)?.GetValue(placement) ?? 0.0);

        int baseW = Int("BaseNormalWidth"), baseH = Int("BaseNormalHeight");
        int w = Int("NormalWidth"), h = Int("NormalHeight");
        if (baseW <= 0 || w <= 0 || (w <= baseW && h <= baseH)) return;   // not an enlarged canvas

        double ax = Flt("AnchorX"), ay = Flt("AnchorY");
        double dx = Flt("DefaultAnchorX"), dy = Flt("DefaultAnchorY");
        double ox = Flt("OffsetX"), oy = Flt("OffsetY");

        string folder = Path.GetFileName(Path.GetDirectoryName(regionPath ?? string.Empty)) ?? "?";
        string key = folder + "/" + name;

        string region = "region=?";
        try
        {
            if (attachment.TryCast<IHasRendererObject>()?.RendererObject is { } rendererObject &&
                new Il2CppSystem.Object(rendererObject.Pointer).TryCast<AtlasRegion>() is { } found)
                region = $"region={found.width}x{found.height} original={found.originalWidth}x{found.originalHeight} " +
                         $"offset={found.offsetX},{found.offsetY} degrees={found.degrees} " +
                         $"uv={found.u:0.#####},{found.v:0.#####}..{found.u2:0.#####},{found.v2:0.#####}";
        }
        catch (Exception) { /* the region is a bonus, not the point */ }

        string line = $"base={baseW}x{baseH} canvas={w}x{h} anchor=({ax:0.##},{ay:0.##}) " +
                      $"default=({dx:0.##},{dy:0.##}) offset=({ox:0.##},{oy:0.##}) {region}";

        lock (Last)
        {
            if (Last.TryGetValue(key, out string? previous) && previous == line) return;
            Last[key] = line;
            MelonLogger.Msg($"{Prefix} {key} {line}");
        }
    }
}
