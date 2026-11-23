using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace SkinForger.SkinPackages;

/// <summary>
/// JSON offsetX / offsetY (and anchors) on MESH parts (2026-11-16, Maid bow_R_3: offsetX -30 on a 38x63 PNG did nothing).
///
/// For a mesh the image is laid out by R4-12 from Main's placement: the original's top-left sits at
/// L = anchorX - defaultAnchorX - offsetX (T likewise) inside the PNG canvas. A same-size PNG moved by an offset puts
/// that box partly OUTSIDE the canvas; R4-12 rejects such a spec (original-not-inside-canvas) and the part is drawn
/// as if there were no offset. Region parts are fine (Main moves the quad itself).
///
/// Fix, in memory only (the creator's PNG and JSON are never changed): inside Main's ApplyRegionPngToAtlas, when a
/// mesh part's box would leave the canvas, the decoded PNG handed to Main is a copy with transparent margins just big
/// enough to hold the box (left/right/top/bottom), and the anchor Main reads is moved by the left/top margin. Main
/// then treats it as an enlarged canvas (its normal path), and R4-12 gets a valid spec: the drawing appears moved by
/// exactly the offset. Main destroys the copy after packing it (it is not its cached texture).
/// </summary>
internal static class OffsetCanvas
{
    private const string Prefix = "[SkinForge][OffsetCanvas]";
    private static MethodInfo? _getRegionAnchor, _applySettings;
    private static PropertyInfo? _ax, _ay, _dx, _dy, _ox, _oy;
    [ThreadStatic] private static Il2CppSpine.Attachment? _attachment;
    [ThreadStatic] private static Il2CppSpine.AtlasRegion? _region;
    [ThreadStatic] private static string? _textureName, _regionPath;
    [ThreadStatic] private static (string Path, int Left, int Top)? _pending;
    [ThreadStatic] private static bool _measuring;
    private static readonly HashSet<string> Logged = new(StringComparer.OrdinalIgnoreCase);

    internal static int Install(HarmonyLib.Harmony harmony)
    {
        Type host = AccessTools.TypeByName("SkinForger.SkinForgerMod") ?? throw new TypeLoadException("SkinForgerMod");
        MethodInfo[] all = host.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        MethodInfo? apply = all.FirstOrDefault(m => m.Name == "ApplyRegionPngToAtlas");
        MethodInfo? getTexture = all.FirstOrDefault(m => m.Name == "GetTexture" && m.GetParameters().Length == 3);
        _getRegionAnchor = all.FirstOrDefault(m => m.Name == "GetRegionAnchor");
        // The hook has a different name in each Main generation; MainBridge resolves it.
        _applySettings = global::SkinForger.MainBridge.MainHost.TextureSettingsHook;
        Type? anchor = AccessTools.Inner(host, "RegionAnchor");
        if (apply == null || getTexture == null || _getRegionAnchor == null || anchor == null)
        {
            MelonLogger.Warning($"{Prefix} Main methods not found: mesh offsets stay as before");
            return 0;
        }
        _ax = AccessTools.Property(anchor, "AnchorX"); _ay = AccessTools.Property(anchor, "AnchorY");
        _dx = AccessTools.Property(anchor, "DefaultAnchorX"); _dy = AccessTools.Property(anchor, "DefaultAnchorY");
        _ox = AccessTools.Property(anchor, "OffsetX"); _oy = AccessTools.Property(anchor, "OffsetY");
        harmony.Patch(apply, prefix: new HarmonyMethod(typeof(OffsetCanvas), nameof(ApplyPrefix)) { priority = Priority.First },
                             finalizer: new HarmonyMethod(typeof(OffsetCanvas), nameof(ApplyFinalizer)));
        harmony.Patch(getTexture, postfix: new HarmonyMethod(typeof(OffsetCanvas), nameof(GetTexturePostfix)) { priority = Priority.Low });
        // After AutoAnchor (normal priority): the margin is added to the final anchor.
        harmony.Patch(_getRegionAnchor, postfix: new HarmonyMethod(typeof(OffsetCanvas), nameof(AnchorPostfix)) { priority = Priority.VeryLow });
        return 3;
    }

    /// <summary>ApplyRegionPngToAtlas(targetAtlas, attachment, region, textureName, regionPath, skin, ref placement, metrics).</summary>
    private static void ApplyPrefix(Il2CppSpine.Attachment __1, Il2CppSpine.AtlasRegion __2, string __3, string __4)
    {
        _attachment = __1; _region = __2; _textureName = __3; _regionPath = __4; _pending = null;
    }

    private static Exception? ApplyFinalizer(Exception? __exception)
    {
        _attachment = null; _region = null; _pending = null; _regionPath = null;
        return __exception;
    }

    private static void GetTexturePostfix(string __0, ref Texture2D __result)
    {
        if (__result == null || _attachment == null || _region == null || _measuring || __0 != _regionPath) return;
        try
        {
            if (_attachment.TryCast<Il2CppSpine.MeshAttachment>() == null) return;
            int ow = _region.originalWidth, oh = _region.originalHeight;
            if (ow <= 0 || oh <= 0) return;
            int w = __result.width, h = __result.height;

            // The anchor Main is about to use (with every other postfix, e.g. AutoAnchor's pin).
            object? anchor;
            _measuring = true;
            try { anchor = _getRegionAnchor!.Invoke(null, new object?[] { __0, _attachment, _region, _textureName, ow, oh }); }
            finally { _measuring = false; }
            if (anchor == null) return;
            // 2026-11-16 (same day): the offset of a mesh part is a rigid move of the whole part (R4-12 PartTransforms),
            // so only the ANCHOR can put the original outside the canvas now.
            double left = F(_ax, anchor) - F(_dx, anchor);
            double top = F(_ay, anchor) - F(_dy, anchor);

            // The default anchor must be the middle of the part's own art. When it is not, the measurement
            // is stale and padding on top of it feeds itself (2026-11-18, proved by the probe):
            //
            //   build 1  headhoop  default=(71.5,64.5) = 143x129 / 2   left=+5   nothing packed   correct
            //   build 2            default=(85.5, ...) = 171x173 / 2   left=-9   packs 9 -> 180x180
            //   build 3            default=(90,   ...) = 180x180 / 2   left=-4.5 packs 5 -> 185x184
            //   build 4            default=(92.5, ...) = 185x184 / 2   left=-2   packs 2 -> 187x186
            //
            // The drawing never moved; each default was measured against the canvas the PREVIOUS build
            // produced, so "left" came out negative and this packed more room, which became the next
            // build's canvas. The expansion band is built for that canvas, so the surplus ring hangs
            // outside the drawing and draws as thin stretched slivers — the blue threads by the ribbons.
            //
            // The part's true untrimmed size is in the costume's manifest (originalWidth / originalHeight),
            // which the Dump wrote and nothing at runtime touches. If the default is not that size halved,
            // nothing is packed. A part the artist really did move keeps a default of base/2, so the
            // feature this module exists for is unaffected.
            if (BaseSize.Of(__0) is { } baseSize)
            {
                double dxv = F(_dx, anchor), dyv = F(_dy, anchor);
                if (Math.Abs(dxv - baseSize.W / 2.0) >= 1.0 || Math.Abs(dyv - baseSize.H / 2.0) >= 1.0)
                {
                    string staleKey = __0 + "|stale";
                    if (Logged.Add(staleKey))
                        MelonLogger.Msg($"{Prefix} {Path.GetFileName(Path.GetDirectoryName(__0))}/{Path.GetFileName(__0)}: " +
                                        $"default anchor ({dxv:0.##},{dyv:0.##}) does not match the part's own " +
                                        $"{baseSize.W}x{baseSize.H} (expected {baseSize.W / 2.0:0.##},{baseSize.H / 2.0:0.##}) - " +
                                        "a stale measurement, nothing packed (the canvas would grow on every rebuild)");
                    return;
                }
            }

            int padL = Math.Max(0, (int)Math.Ceiling(-left - 0.001)), padT = Math.Max(0, (int)Math.Ceiling(-top - 0.001));
            int padR = Math.Max(0, (int)Math.Ceiling(left + ow - w - 0.001)), padB = Math.Max(0, (int)Math.Ceiling(top + oh - h - 0.001));
            if (padL + padT + padR + padB == 0) return;
            if (padL + padR > 4096 || padT + padB > 4096) return;   // nonsense values: leave Main's behaviour

            int nw = w + padL + padR, nh = h + padT + padB;
            Color32[] source = __result.GetPixels32();
            var target = new Color32[nw * nh];
            // Unity rows are bottom-up: the bottom margin comes first.
            for (int y = 0; y < h; y++)
                Array.Copy(source, y * w, target, (y + padB) * nw + padL, w);
            var copy = new Texture2D(nw, nh, TextureFormat.RGBA32, false, true) { name = __result.name };
            copy.SetPixels32(target);
            copy.Apply();
            _applySettings?.Invoke(null, new object[] { copy });
            __result = copy;
            _pending = (__0, padL, padT);
            string key = __0 + "|" + padL + "," + padT + "," + padR + "," + padB;
            if (Logged.Add(key))
                MelonLogger.Msg($"{Prefix} {Path.GetFileName(Path.GetDirectoryName(__0))}/{Path.GetFileName(__0)}: mesh part, offset/anchor puts the original " +
                                $"at ({left:0.##},{top:0.##}) outside the {w}x{h} canvas; packed with transparent margins L{padL} T{padT} R{padR} B{padB} " +
                                $"({nw}x{nh}, in memory only) so the offset takes effect");
        }
        catch (Exception exception)
        {
            _pending = null;
            MelonLogger.Warning($"{Prefix} {Path.GetFileName(__0)}: {exception.GetBaseException().Message}");
        }
    }

    private static void AnchorPostfix(string __0, object __result)
    {
        if (_measuring || __result == null || _pending is not { } pad || pad.Path != __0) return;
        try
        {
            _ax!.SetValue(__result, (float)(F(_ax, __result) + pad.Left));
            _ay!.SetValue(__result, (float)(F(_ay, __result) + pad.Top));
        }
        catch { }
    }

    private static double F(PropertyInfo? property, object instance) => Convert.ToDouble(property!.GetValue(instance));
}

/// <summary>
/// The untrimmed size of a dumped part, read from its costume's manifest.json (2026-11-18).
///
/// This is the one size at runtime that cannot have drifted: the Dump wrote it from the game's own atlas
/// and nothing changes it afterwards. The atlas region's own originalWidth / originalHeight cannot be used
/// for the same purpose, because those are exactly what a rebuild overwrites.
/// </summary>
internal static class BaseSize
{
    private static readonly Dictionary<string, (int W, int H)?> Cache = new(StringComparer.OrdinalIgnoreCase);

    internal static (int W, int H)? Of(string? png)
    {
        if (string.IsNullOrEmpty(png)) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(png!, out var cached)) return cached;
            (int, int)? found = Read(png!);
            Cache[png!] = found;
            return found;
        }
    }

    private static (int, int)? Read(string png)
    {
        try
        {
            string? atlasDir = Path.GetDirectoryName(png);
            string? costume = Path.GetDirectoryName(atlasDir);
            if (costume == null) return null;
            string manifest = Path.Combine(costume, "manifest.json");
            if (!VirtualFiles.FileExists(manifest)) return null;
            string wanted = Path.GetFileName(atlasDir) + "\\" + Path.GetFileName(png);
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(VirtualFiles.ReadAllText(manifest));
            if (!document.RootElement.TryGetProperty("regions", out System.Text.Json.JsonElement regions)) return null;
            foreach (System.Text.Json.JsonElement region in regions.EnumerateArray())
            {
                if (!region.TryGetProperty("file", out System.Text.Json.JsonElement file)) continue;
                if (!string.Equals(file.GetString(), wanted, StringComparison.OrdinalIgnoreCase)) continue;
                int w = region.TryGetProperty("originalWidth", out var ow) ? ow.GetInt32() : 0;
                int h = region.TryGetProperty("originalHeight", out var oh) ? oh.GetInt32() : 0;
                return w > 0 && h > 0 ? (w, h) : null;
            }
        }
        catch (Exception) { /* no manifest, or an odd one: the guard simply does not apply */ }
        return null;
    }
}
