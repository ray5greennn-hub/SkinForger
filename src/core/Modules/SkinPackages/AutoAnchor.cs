using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace SkinForger.SkinPackages;

/// <summary>
/// "Enlarging the canvas never moves the anchor, unless the player changed it in the JSON" (user request 2026-11-16).
///
/// The JSON anchor is a pixel position from the image's top-left corner, written by the Dump for the ORIGINAL size.
/// After the canvas is enlarged the original drawing sits somewhere inside the bigger PNG, so the old pixel position
/// pointed at the wrong place (the part looked shifted). Postfix on Main's GetRegionAnchor (every consumer — region
/// attachments, R4-12 mesh canvas, ElfinMeshCanvas — takes its anchor from there):
///  - only when the PNG is bigger than the original and the anchor was NOT changed by the player (it equals Main's
///    default anchor, or the value SkinForger set itself last time: "autoAnchorX/Y" in the JSON);
///  - the original dumped image (replaced\&lt;name&gt;.png, the first backup) is located inside the new canvas
///    (CanvasMatch); the anchor becomes default + that offset. No original copy or no confident match: anchor left as before;
///  - the result is written into the JSON once (anchorX/Y + autoAnchorX/Y + autoFor), so R4-12's disk reader and skin
///    packages get the same anchor; recomputed only when the PNG changes.
/// Calculated ONCE (user rule 2026-11-16): only while the anchor is still the Dump's default, i.e. the first time a
/// part is enlarged; then saved to the JSON and fixed for good. Order for that one calculation: "canvasAnchor" in the
/// JSON (nine canvas positions, e.g. "bottom-right" / 右下) &gt; the original found in the new canvas &gt; centred.
/// To have it calculated again, remove anchorX / anchorY from the JSON (or put the Dump's values back).
/// Canvas resized later (2026-11-16, Maid headhoop 171x173 -> 314x327): the pin is a pixel position, so a canvas grown
/// on the top / left would shift the drawing. "autoContent" in the JSON remembers the drawing (its box and a hash of
/// its pixels). When the PNG comes back with a different size but the very same drawing, moved exactly as a canvas
/// dialog moves it (one of the nine positions), the pin moves with it: the part stays where it was. A drawing that was
/// edited, or moved without a canvas size change, keeps the pin (moving the drawing inside the canvas moves the part).
/// Round 2 (same day): any shift counts, not only the nine dialog positions (the creator's app moves it freely).
/// Package (in-memory) files are never rewritten; their JSON already carries the creator's result.
/// </summary>
internal static class AutoAnchor
{
    private const string Prefix = "[SkinForge][AutoAnchor]";
    private static PropertyInfo? _anchorX, _anchorY, _defaultX, _defaultY, _fromSidecar;
    private static MethodInfo? _writeText, _getMaterial, _makeReadable, _cropRegion;
    [ThreadStatic] private static Il2CppSpine.Attachment? _attachment;
    [ThreadStatic] private static Il2CppSpine.AtlasRegion? _region;
    /// <summary>png path -> (png stamp, anchor offset applied) for this session.</summary>
    private static readonly Dictionary<string, (string Stamp, int Left, int Top, bool Known)> Done = new(StringComparer.OrdinalIgnoreCase);
    private const float Tolerance = 0.05f;
    private static readonly Dictionary<string, (float X, float Y)> Written = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>png path -> last write time already checked by <see cref="Follow"/> this session.</summary>
    private static readonly Dictionary<string, long> Checked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Offset of the original box for a canvas-dialog position; null for "auto" / empty / unknown.</summary>
    internal static (int Left, int Top)? CanvasAnchorOffset(string value, int extraW, int extraH)
    {
        string v = (value ?? string.Empty).Trim().ToLowerInvariant().Replace("_", "-").Replace(" ", "-");
        (int fx, int fy)? f = v switch
        {
            "top-left" or "left-top" or "左上" => (0, 0),
            "top" or "top-center" or "top-centre" or "上" or "上中" => (1, 0),
            "top-right" or "right-top" or "右上" => (2, 0),
            "left" or "center-left" or "centre-left" or "middle-left" or "左" or "左中" => (0, 1),
            "center" or "centre" or "middle" or "中" or "居中" or "中间" => (1, 1),
            "right" or "center-right" or "centre-right" or "middle-right" or "右" or "右中" => (2, 1),
            "bottom-left" or "left-bottom" or "左下" => (0, 2),
            "bottom" or "bottom-center" or "bottom-centre" or "下" or "下中" => (1, 2),
            "bottom-right" or "right-bottom" or "右下" => (2, 2),
            _ => null,
        };
        if (f == null) return null;
        return (f.Value.fx == 0 ? 0 : f.Value.fx == 1 ? extraW / 2 : extraW,
                f.Value.fy == 0 ? 0 : f.Value.fy == 1 ? extraH / 2 : extraH);
    }

    internal static int Install(HarmonyLib.Harmony harmony)
    {
        Type host = AccessTools.TypeByName("SkinForger.SkinForgerMod") ?? throw new TypeLoadException("SkinForgerMod");
        MethodInfo? target = AccessTools.DeclaredMethod(host, "GetRegionAnchor");
        Type? anchor = AccessTools.Inner(host, "RegionAnchor");
        if (target == null || anchor == null) { MelonLogger.Warning($"{Prefix} GetRegionAnchor not found: anchors are not adjusted"); return 0; }
        _anchorX = AccessTools.Property(anchor, "AnchorX");
        _anchorY = AccessTools.Property(anchor, "AnchorY");
        _defaultX = AccessTools.Property(anchor, "DefaultAnchorX");
        _defaultY = AccessTools.Property(anchor, "DefaultAnchorY");
        _fromSidecar = AccessTools.Property(anchor, "FromSidecar");
        _writeText = AccessTools.TypeByName("SkinForger.ReplacedBackup") is { } backup
            ? AccessTools.DeclaredMethod(backup, "WriteResourceText", new[] { typeof(string), typeof(string) }) : null;
        // The Dump's own way to cut a part out of its atlas (2026-11-16: rebuilding it from manifest.json failed for the
        // rotated Maid hair_front).
        _getMaterial = AccessTools.DeclaredMethod(host, "GetMaterial", new[] { typeof(Il2CppSpine.Attachment) });
        _makeReadable = AccessTools.DeclaredMethod(host, "MakeReadable", new[] { typeof(Texture2D) });
        _cropRegion = AccessTools.DeclaredMethod(host, "CreateEditableRegionTexture", new[] { typeof(Texture2D), typeof(Il2CppSpine.Attachment), typeof(Il2CppSpine.AtlasRegion), typeof(string) });
        harmony.Patch(target, postfix: new HarmonyMethod(typeof(AutoAnchor), nameof(Postfix)));
        return 1;
    }

    /// <summary>GetRegionAnchor(regionPath, attachment, region, textureName, originalWidth, originalHeight).</summary>
    // ------------------------------------------------------------ one file per folder (2026-11-20)
    //
    // The author's rule: "I do not want one JSON per image. I want every image in the folder to share one
    // JSON -- the cat skin needs nothing clever, just a fixed anchor." So everything this module reads and
    // writes goes through the methods below, and a folder that keeps its anchors together (anchors.json,
    // Main's FolderAnchorFile) is written to in ONE file. A per-image <part>.json still wins where it
    // exists, exactly as Main's RegionAnchorFile.ReadIfExists resolves it.

    internal const string FolderFileName = "anchors.json";

    private static string FolderFileOf(string png) =>
        Path.Combine(Path.GetDirectoryName(png) ?? string.Empty, FolderFileName);

    /// <summary>Whether this image's anchor belongs in the folder's one file rather than in its own.</summary>
    private static bool InFolderFile(string png) =>
        !File.Exists(Path.ChangeExtension(png, ".json")) &&
        (File.Exists(FolderFileOf(png)) || RoleSkins.KeepsAnchorsInOneFile(png));

    /// <summary>Where this image's anchor is written down, for logs.</summary>
    private static string AnchorFileOf(string png) =>
        InFolderFile(png) ? FolderFileOf(png) : Path.ChangeExtension(png, ".json");

    /// <summary>This image's anchor record: its own JSON when it has one, else its entry in the folder file.</summary>
    private static JsonObject? ReadNode(string png)
    {
        try
        {
            string own = Path.ChangeExtension(png, ".json");
            if (File.Exists(own)) return JsonNode.Parse(File.ReadAllText(own)) as JsonObject;
            string folder = FolderFileOf(png);
            if (!File.Exists(folder)) return null;
            return JsonNode.Parse(File.ReadAllText(folder)) is JsonObject root && root["parts"] is JsonObject parts
                ? parts[Path.GetFileNameWithoutExtension(png)] as JsonObject
                : null;
        }
        catch (Exception) { return null; }   // a hand-edited broken file must never break a build
    }

    /// <summary>
    /// Saves this image's anchor record where it belongs. In folder mode the rest of the file -- every other
    /// part's entry, and anything the author put there -- is read back and written out untouched.
    /// </summary>
    private static void WriteNode(string png, JsonObject node)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        string path;
        string text;
        if (InFolderFile(png))
        {
            path = FolderFileOf(png);
            JsonObject root;
            try { root = (File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null) ?? NewFolderRoot(); }
            catch (Exception) { root = NewFolderRoot(); }
            if (root["parts"] is not JsonObject parts) { parts = new JsonObject(); root["parts"] = parts; }
            // The entry carries no version / origin of its own: the folder file states the convention once.
            JsonObject entry = JsonNode.Parse(node.ToJsonString())!.AsObject();
            entry.Remove("version");
            entry.Remove("origin");
            parts[Path.GetFileNameWithoutExtension(png)] = entry;
            text = root.ToJsonString(options);
        }
        else
        {
            path = Path.ChangeExtension(png, ".json");
            text = node.ToJsonString(options);
        }
        // ReplacedBackup first (its baseline: no "changed" backup on the next Reload), but an anchor file is
        // SkinForger's own bookkeeping, not the author's art, and it lives in SkinCreator -- which the backup
        // does not track at all (it tracks SkinForger\Skins). 2026-11-20, log 21-27: every single anchor write
        // of a cat folder came back "SkinForger backup state is unavailable; operation cancelled" and NOTHING
        // was saved, so the anchors were worked out again on the next launch. The write is what matters here.
        bool written = false;
        if (_writeText != null)
        {
            try { _writeText.Invoke(null, new object[] { path, text }); written = true; }
            catch (Exception) { written = false; }
        }
        if (!written) File.WriteAllText(path, text);
        global::SkinForger.FastReload.FastReloadMod.NoteWrittenBySkinForger(path);
    }

    private static JsonObject NewFolderRoot() => new()
    {
        ["version"] = 2,
        ["origin"] = "top-left",
        // What a frame-by-frame author states for the whole folder, and may override per part:
        //  - extraLeft / extraTop: how many pixels of room were added on the left and on the top. The exact
        //    answer for "I enlarge each side as I need to", and it wins over everything else.
        //  - canvasAnchor: one of the nine canvas-dialog positions, when the room was added from a side
        //    rather than by a measured amount. "center" is the default a canvas dialog uses.
        ["canvasAnchor"] = "center",
        ["_howToPinAPart"] = "An anchor is a pixel position from the image's TOP-LEFT corner. " +
            "To say where the game's original part sits in an enlarged canvas, put \"extraLeft\" and \"extraTop\" " +
            "(the pixels of room you added on those two sides) in that part's entry below, or here for the whole " +
            "folder; they are read on every load, so changing them moves the part. Without them, \"canvasAnchor\" " +
            "is used, and without that the original is looked for in the picture.",
    };

    /// <summary>
    /// How much room the author added on the LEFT and on the TOP of this image, in pixels -- "extraLeft" /
    /// "extraTop" in the anchor record (the folder file may state defaults for every part).
    ///
    /// Settled 2026-11-20: the canvas is NOT enlarged evenly -- each side gets as much room as that drawing
    /// needs. The nine canvas positions cannot say "200 more on the left and 50 on the top",
    /// and measuring cannot find the original when the part has been REPAINTED bigger (eye_L: a 76x66 part
    /// drawn at 158x96 in a 167x118 canvas -- the old picture is simply not in there any more). The two
    /// numbers the author has in front of them while they do it are exactly the two numbers needed, so they
    /// are what the file asks for. They WIN over everything else, every load: an author who changes the
    /// number expects the part to move, not to be told the pin was fixed once and for all.
    /// </summary>
    private static (int Left, int Top)? AuthorExtra(string png, JsonObject? node, int extraW, int extraH)
    {
        float? left = Float(node, "extraLeft"), top = Float(node, "extraTop");
        if (left == null && top == null && InFolderFile(png))
            try
            {
                string folder = FolderFileOf(png);
                if (File.Exists(folder) && JsonNode.Parse(File.ReadAllText(folder)) is JsonObject root)
                {
                    left = Float(root, "extraLeft");
                    top = Float(root, "extraTop");
                }
            }
            catch (Exception) { /* a broken file must never break a build */ }
        if (left == null && top == null) return null;
        // A side the author did not mention keeps the middle, which is where a canvas dialog puts it.
        int l = left == null ? extraW / 2 : (int)Math.Round(left.Value);
        int t = top == null ? extraH / 2 : (int)Math.Round(top.Value);
        return (Math.Clamp(l, 0, extraW), Math.Clamp(t, 0, extraH));
    }

    /// <summary>
    /// The canvas position to use for an image: its own entry's canvasAnchor, else the one stated for the
    /// whole folder, else "center" in folder mode (a folder file means a frame-by-frame skin, where every
    /// frame is enlarged the same way) and "" -- measure it -- for a single image with its own sidecar.
    /// </summary>
    private static string CanvasAnchorFor(string png, JsonObject? node)
    {
        if (node?["canvasAnchor"] is JsonValue own && own.TryGetValue(out string? mine) && !string.IsNullOrWhiteSpace(mine))
            return mine.Trim();
        if (InFolderFile(png))
        {
            try
            {
                string folder = FolderFileOf(png);
                if (File.Exists(folder) && JsonNode.Parse(File.ReadAllText(folder)) is JsonObject root &&
                    root["canvasAnchor"] is JsonValue shared && shared.TryGetValue(out string? all) && !string.IsNullOrWhiteSpace(all))
                    return all.Trim();
            }
            catch (Exception) { /* a broken file must never break a build */ }
        }
        // Nothing stated: say so, and let the picture be MEASURED.
        //
        // 2026-11-21. This used to answer "center" for a folder-file skin, which quietly cancelled the
        // measurement -- the one step that actually keeps a part still. The author enlarged body_4 at the
        // TOP only; centred put the old drawing half way down the new canvas, so the frame sank by half
        // the added height. Pinning an anchor is supposed to mean "however you enlarge the canvas, the
        // drawing does not move", and the only way to deliver that without asking is to find where the
        // old drawing sits in the new picture. An explicit canvasAnchor still wins over the measurement,
        // and extraLeft / extraTop still win over both.
        return string.Empty;
    }

    /// <summary>
    /// The pin for this image, or null when it has none yet: what SkinForger wrote this session, else the
    /// anchor saved in the sidecar (only when the player has not moved it since — anchorX/Y still equal the
    /// autoAnchorX/Y SkinForger wrote). A player's own anchorX/Y is a pin too and is returned as it stands.
    /// </summary>
    private static (float X, float Y)? PinFor(string png, object result)
    {
        if (Written.TryGetValue(png, out var written)) return written;
        try
        {
            if (ReadNode(png) is not { } node) return null;
            if (node["anchorX"]?.GetValue<double>() is not { } ax || node["anchorY"]?.GetValue<double>() is not { } ay) return null;
            // Only a saved pin counts. A sidecar that carries no autoAnchor has never been through the
            // first-enlargement path, so the code below must still run for it.
            if (node["autoAnchorX"] == null || node["autoAnchorY"] == null) return null;
            return ((float)ax, (float)ay);
        }
        catch (Exception) { return null; }
    }

    private static void Postfix(string __0, Il2CppSpine.Attachment __1, Il2CppSpine.AtlasRegion __2, int __4, int __5, object __result)
    {
        _attachment = __1;
        _region = __2;
        if (__result == null || string.IsNullOrEmpty(__0) || __4 <= 0 || __5 <= 0 || DumpIsolation.Active) return;
        try
        {
            string png = __0;
            if (VirtualFiles.IsVirtual(png) || !File.Exists(png)) return;   // packages: the JSON already carries the anchor

            // 2026-11-18: an experiment that handed a saved pin back before the size tests lived here.
            // It changed nothing (the canvas drift does not come through this path) so it was taken out
            // again rather than left in the field unverified. See SF日志 BUG_LOG, same day.

            if (!LoadCaches.TryGetPngSize(png, ReadSize, out int w, out int h)) return;

            // An image at its original size is LEFT ALONE -- nothing here, nothing written.
            //
            // 2026-11-20, and this was a mistake worth writing down: for a few hours this module wrote an
            // anchor record for every image that had none, "so that a later enlargement cannot move it".
            // It moved them instead. An anchor record changes how Main places a part: RuntimeAtlas keeps
            // the game's own quad placement while a part has NO anchor of its own ("Only a REAL anchor goes
            // in"), and recomputes x / y from the anchor as soon as one exists -- and the record also
            // carried rotation = the region's degrees, which Main adds to the quad's rotation (turns), so
            // every part packed at 90 / 270 in the atlas was spun. 52 parts of one show atlas were pinned
            // on the first build and the next Reload drew them wrong (user: "the main show is misplaced
            // after I click Reload").
            //
            // It was never needed either: Main measures the default anchor from the attachment on every
            // load, so an unenlarged part is already placed correctly, and what an ENLARGED part needs is
            // only where the original sits inside the bigger canvas -- which the author now states with
            // extraLeft / extraTop. Nothing to save for a part nobody enlarged.
            if (w <= __4 && h <= __5) return;
            if (w < __4 || h < __5) return;   // smaller than the original in one direction: not an enlarged canvas

            float ax = (float)_anchorX!.GetValue(__result)!, ay = (float)_anchorY!.GetValue(__result)!;
            float dx = (float)_defaultX!.GetValue(__result)!, dy = (float)_defaultY!.GetValue(__result)!;
            bool sidecar = _fromSidecar!.GetValue(__result) is true;
            JsonObject? node = sidecar ? ReadNode(png) : null;

            // The author's own numbers come first, before any pin, any saved anchor and any measurement:
            // they are a statement about this very image, and they are re-read on every load so that
            // editing them moves the part. Nothing is written back -- the file already says it.
            if (AuthorExtra(png, node ?? ReadNode(png), w - __4, h - __5) is { } said)
            {
                float sx = dx + said.Left, sy = dy + said.Top;
                if (!Near(sx, ax) || !Near(sy, ay))
                {
                    _anchorX.SetValue(__result, sx);
                    _anchorY.SetValue(__result, sy);
                }
                if (!Written.TryGetValue(png, out var before) || !Near(before.X, sx) || !Near(before.Y, sy))
                    MelonLogger.Msg($"{Prefix} {Path.GetFileName(Path.GetDirectoryName(png))}/{Path.GetFileName(png)} " +
                                    $"{__4}x{__5} -> {w}x{h}: the author added {said.Left} on the left and {said.Top} on the top " +
                                    $"(extraLeft / extraTop), anchor ({dx:0.##},{dy:0.##}) -> ({sx:0.##},{sy:0.##})");
                Written[png] = (sx, sy);
                return;
            }
            // 2026-11-20 (user: pin the anchor for cat skins). Cat skins are frame-by-frame and get NO dump sidecar
            // (RoleSkins keeps their atlases out of Main's WriteAnchorMetadataIfMissing), so node was null here: the
            // anchor worked out below was applied in memory and never saved. Every launch worked it out again, and a
            // launch where the match is not confident falls back to "centred" -- the part jumps. A part has to be
            // pinned to a file to stay put, so a sidecar is STARTED here, at the moment the canvas is first enlarged:
            // the clean dump folder the workflow asks for is kept (no JSON per frame), only the frames the author
            // actually enlarged get one. Only the pin is written; rotation / offsets / Scale are left out so the
            // placement stays exactly what it is today for everything else.
            bool started = false;
            if (node == null && ReadNode(png) == null)   // an existing non-top-left (v1) record is never overwritten
            {
                node = new JsonObject { ["version"] = 2, ["origin"] = "top-left" };
                started = true;
            }
            // 2026-11-16 (user rule): an anchor is calculated ONCE, when a freshly dumped part is first enlarged. After
            // that it is a fixed pin, whatever the canvas does later (adding space on top must push the drawing down).
            // Log 15-49: SF recalculated it on every new canvas size (248x220 -> 241 -> 276 -> 320) and moved it along.
            bool isDefault = Near(ax, dx) && Near(ay, dy);
            if (Written.TryGetValue(png, out var pinned) || (sidecar && !isDefault))
            {
                // A pin (set by SkinForger or the player). Main caches the JSON until Reload: hand out the value SF
                // saved this session. Only a pure canvas resize moves it (with the drawing).
                if (!Written.ContainsKey(png)) pinned = (ax, ay);
                (float fx, float fy) = Follow(png, node, pinned.X, pinned.Y);
                if (!Near(fx, ax) || !Near(fy, ay) || Written.ContainsKey(png))
                {
                    Written[png] = (fx, fy);
                    _anchorX.SetValue(__result, fx);
                    _anchorY.SetValue(__result, fy);
                }
                return;
            }

            // First enlargement. canvasAnchor (optional): which of the nine canvas-dialog positions the canvas was
            // enlarged from ("bottom-right", "center", ... or 右下 / 居中 ...). "auto" / empty = find the original.
            string canvasAnchor = CanvasAnchorFor(png, node);
            (int Left, int Top, string How)? found;
            if (CanvasAnchorOffset(canvasAnchor, w - __4, h - __5) is { } fixedOffset)
                found = (fixedOffset.Left, fixedOffset.Top, $"canvasAnchor \"{canvasAnchor}\"");
            else
            {
                found = Locate(png, __4, __5, w, h, out string why);
                // Not found (e.g. the part was redrawn): a drawing of about the original's size is taken as the
                // redrawn part in its place (2026-11-16, headhoop); otherwise enlarged from the centre (user choice).
                found ??= OnDrawing(png, __4, __5, w, h, why);
                found ??= ((w - __4) / 2, (h - __5) / 2,
                    $"centred as a last resort: the old drawing could not be found in the new canvas ({why}). " +
                    "Put extraLeft / extraTop (the pixels you added on the left and on the top) in this part's " +
                    "entry to say it exactly, or canvasAnchor (\"top\", \"bottom\", ...) for the side you grew from");
            }
            (int left, int top, string how) = found.Value;
            float newX = dx + left, newY = dy + top;
            MelonLogger.Msg($"{Prefix} {Path.GetFileName(Path.GetDirectoryName(png))}/{Path.GetFileName(png)} {__4}x{__5} -> {w}x{h}: " +
                            $"original at ({left},{top}) [{how}], anchor ({dx:0.##},{dy:0.##}) -> ({newX:0.##},{newY:0.##}) - saved, fixed from now on" +
                            (started ? " (sidecar started: this frame had none)" : string.Empty));
            string stamp = $"{w}x{h}|{File.GetLastWriteTimeUtc(png).Ticks}";
            // A sidecar started here is a pin for good, so it is only started on a placement that was actually
            // found. "centred" is the last-resort guess: pinning that would freeze a guess forever, which is worse
            // than working it out again next launch (and the author can still write canvasAnchor by hand).
            if (started && how.StartsWith("centred", StringComparison.Ordinal)) node = null;
            if (node != null)
            {
                WriteJson(png, node, newX, newY, stamp, how);
                Follow(png, node, newX, newY);   // remembers the drawing for later canvas resizes
            }
            Written[png] = (newX, newY);
            _anchorX.SetValue(__result, newX);
            _anchorY.SetValue(__result, newY);
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"{Prefix} {Path.GetFileName(__0)}: {exception.GetBaseException().Message}");
        }
    }

    /// <summary>
    /// Where the original drawing sits in the enlarged canvas (top-left offset). Null = not known for sure: the anchor
    /// is then left exactly as before (never worse than without this feature).
    /// Originals, best first: the part cut out of the GAME's atlas (manifest.json says where; 2026-11-16: the copy in
    /// replaced\ can be the creator's own edit when the dumped file was never registered by the backup system), then
    /// replaced\&lt;name&gt;.png if it has the original size.
    /// </summary>
    private static (int Left, int Top, string How)? Locate(string png, int ow, int oh, int w, int h, out string why)
    {
        byte[]? canvas = Pixels(png, out int cw, out int ch);
        if (canvas == null) { why = "unreadable PNG"; return null; }
        var candidates = new List<(byte[] Pixels, string Source)>();
        try { if (FromDumpCrop(ow, oh) is { } dumped) candidates.Add((dumped, "dump crop")); }
        catch (Exception exception) { MelonLogger.Warning($"{Prefix} dump crop failed: {exception.GetBaseException().Message}"); }
        try { candidates.AddRange(FromGameAtlas(png, ow, oh)); } catch (Exception exception) { MelonLogger.Warning($"{Prefix} game atlas read failed: {exception.GetBaseException().Message}"); }
        string copy = Path.Combine(Path.GetDirectoryName(png)!, "replaced", Path.GetFileName(png));
        if (File.Exists(copy) && Pixels(copy, out int sw, out int sh) is { } saved && sw == ow && sh == oh) candidates.Add((saved, "replaced copy"));
        if (candidates.Count == 0) { why = "no original found (game atlas / replaced copy)"; return null; }

        CanvasMatch.Result? best = null;
        string bestSource = string.Empty;
        foreach ((byte[] original, string source) in candidates)
        {
            CanvasMatch.Result result = CanvasMatch.Find(original, ow, oh, canvas, cw, ch);
            if (best == null || (result.Confident && !best.Value.Confident) || (result.Confident == best.Value.Confident && result.Score < best.Value.Score))
            {
                best = result;
                bestSource = source;
            }
        }
        why = $"{bestSource}, {best!.Value.Method}, score={best.Value.Score:0.###} centred={best.Value.CentredScore:0.###}";
        if (!best.Value.Confident)
        {
            why = "not confident: " + why;
            SaveDebug(png, ow, oh, candidates);
            return null;
        }
        return (best.Value.Left, best.Value.Top, "matched " + why);
    }

    /// <summary>The part exactly as the Dump would write it (Main's GetMaterial + MakeReadable + CreateEditableRegionTexture).</summary>
    private static byte[]? FromDumpCrop(int ow, int oh)
    {
        if (_attachment == null || _region == null || _getMaterial == null || _makeReadable == null || _cropRegion == null) return null;
        if (_getMaterial.Invoke(null, new object[] { _attachment }) is not Material material) return null;
        Texture2D? atlas = material.mainTexture?.TryCast<Texture2D>();
        if (atlas == null || global::SkinForger.HexieBlock.HexiePolicy.IsHexieName(atlas.name)) return null;
        Texture2D? readable = null, part = null;
        try
        {
            readable = _makeReadable.Invoke(null, new object[] { atlas }) as Texture2D;
            if (readable == null) return null;
            part = _cropRegion.Invoke(null, new object[] { readable, _attachment, _region, atlas.name }) as Texture2D;
            if (part == null || part.width != ow || part.height != oh) return null;
            Color32[] colours = part.GetPixels32();
            var bytes = new byte[colours.Length * 4];
            for (int i = 0; i < colours.Length; i++) { Color32 c = colours[i]; bytes[i * 4] = c.r; bytes[i * 4 + 1] = c.g; bytes[i * 4 + 2] = c.b; bytes[i * 4 + 3] = c.a; }
            return bytes;
        }
        finally
        {
            if (part != null) UnityEngine.Object.Destroy(part);
            if (readable != null && readable.Pointer != atlas.Pointer) UnityEngine.Object.Destroy(readable);
        }
    }

    /// <summary>When nothing matches: the candidate originals as PNG in UserData\SkinForger\anchor-debug (to look at).</summary>
    private static void SaveDebug(string png, int ow, int oh, List<(byte[] Pixels, string Source)> candidates)
    {
        try
        {
            string folder = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "SkinForger", "anchor-debug");
            Directory.CreateDirectory(folder);
            string stem = Path.GetFileName(Path.GetDirectoryName(png)) + "_" + Path.GetFileNameWithoutExtension(png);
            int n = 0;
            foreach ((byte[] pixels, string source) in candidates)
            {
                var texture = new Texture2D(ow, oh, TextureFormat.RGBA32, false);
                var colours = new Color32[ow * oh];
                for (int i = 0; i < colours.Length; i++) colours[i] = new Color32(pixels[i * 4], pixels[i * 4 + 1], pixels[i * 4 + 2], pixels[i * 4 + 3]);
                texture.SetPixels32(colours);
                texture.Apply();
                File.WriteAllBytes(Path.Combine(folder, $"{stem}_{n++}_{string.Concat(source.Where(char.IsLetterOrDigit))}.png"), ImageConversion.EncodeToPNG(texture));
                UnityEngine.Object.Destroy(texture);
            }
            MelonLogger.Msg($"{Prefix} {stem}: {n} candidate original(s) saved to {folder} for checking");
        }
        catch { }
    }

    private static readonly Dictionary<int, Color32[]> AtlasPixels = new();
    private static readonly Dictionary<string, (DateTime Time, Dictionary<string, JsonObject> Regions)> Manifests = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The original image of this part from the game's atlas (one or two candidates: rotated parts are tried
    /// both ways), as ow x oh RGBA rows bottom-up, placed with the manifest's trim offsets.</summary>
    private static IEnumerable<(byte[] Pixels, string Source)> FromGameAtlas(string png, int ow, int oh)
    {
        string atlasFolder = Path.GetDirectoryName(png)!;
        string skinRoot = Path.GetDirectoryName(atlasFolder)!;
        string manifest = Path.Combine(skinRoot, "manifest.json");
        if (!File.Exists(manifest)) yield break;
        DateTime time = File.GetLastWriteTimeUtc(manifest);
        if (!Manifests.TryGetValue(manifest, out var cached) || cached.Time != time)
        {
            var regions = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
            if (JsonNode.Parse(File.ReadAllText(manifest))?["regions"] is JsonArray array)
                foreach (JsonNode? item in array)
                    if (item is JsonObject region && region["file"]?.GetValue<string>() is { } file)
                        regions[file.Replace('/', '\\')] = region;
            Manifests[manifest] = cached = (time, regions);
        }
        string relative = Path.GetRelativePath(skinRoot, png).Replace('/', '\\');
        if (!cached.Regions.TryGetValue(relative, out JsonObject? r)) yield break;
        string atlas = r["atlas"]?.GetValue<string>() ?? Path.GetFileName(atlasFolder);
        if (global::SkinForger.HexieBlock.HexiePolicy.IsHexieName(atlas) || global::SkinForger.HexieBlock.HexiePolicy.IsHexieName(png)) yield break;
        int x = Int(r, "x"), y = Int(r, "y"), bw = Int(r, "width"), bh = Int(r, "height");
        int offX = (int)Math.Round(Dbl(r, "offsetX")), offY = (int)Math.Round(Dbl(r, "offsetY"));
        bool rotated = r["rotate"]?.GetValue<bool>() == true || Int(r, "degrees") is 90 or 270;
        int aw = rotated ? bh : bw, ah = rotated ? bw : bh;   // size of the block in the atlas
        if (bw <= 0 || bh <= 0) yield break;

        // The game's atlas texture: same name, the block fits, smallest (SkinForger's runtime copies are larger).
        Texture2D? texture = Resources.FindObjectsOfTypeAll<Texture2D>()
            .Where(t => t != null && t.name == atlas && x + aw <= t.width && y + ah <= t.height)
            .OrderBy(t => t.width * t.height).FirstOrDefault();
        if (texture == null) yield break;
        if (!AtlasPixels.TryGetValue(texture.GetInstanceID(), out Color32[]? pixels))
            AtlasPixels[texture.GetInstanceID()] = pixels = ReadBack(texture);
        int tw = texture.width, th = texture.height;

        // Block rows bottom-up. The manifest's y is taken as from the top first, then from the bottom (both are
        // tried; only a confident match is used).
        foreach ((int baseRow, string yFrom) in new[] { (th - y - ah, "y from top"), (y, "y from bottom") })
        // Rotated parts: every way the block can be turned / mirrored back (degrees 90 / 270 conventions differ).
        foreach (int turn in rotated ? new[] { 1, 2, 3, 4 } : new[] { 0 })
        {
            var image = new byte[ow * oh * 4];
            for (int by = 0; by < bh; by++)
                for (int bx = 0; bx < bw; bx++)
                {
                    // (bx, by): unrotated block pixel, by from the bottom. Where it is in the atlas block:
                    int sx, sy;
                    if (turn == 0) { sx = bx; sy = by; }
                    else if (turn == 1) { sx = by; sy = bw - 1 - bx; }            // turned one way
                    else if (turn == 2) { sx = bh - 1 - by; sy = bx; }            // turned the other way
                    else if (turn == 3) { sx = by; sy = bx; }                     // transposed
                    else { sx = bh - 1 - by; sy = bw - 1 - bx; }                  // anti-transposed
                    int ax = x + sx, ay = baseRow + sy;
                    if (ax < 0 || ay < 0 || ax >= tw || ay >= th) continue;
                    int ix = bx + offX, iy = by + offY;
                    if (ix < 0 || iy < 0 || ix >= ow || iy >= oh) continue;
                    Color32 c = pixels[ay * tw + ax];
                    int i = (iy * ow + ix) * 4;
                    image[i] = c.r; image[i + 1] = c.g; image[i + 2] = c.b; image[i + 3] = c.a;
                }
            yield return (image, (turn == 0 ? "game atlas" : $"game atlas turn {turn}") + ", " + yFrom);
        }
    }

    private static Color32[] ReadBack(Texture2D source)
    {
        RenderTexture previous = RenderTexture.active;
        RenderTexture temporary = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        try
        {
            temporary.filterMode = FilterMode.Point;
            Graphics.Blit(source, temporary);
            RenderTexture.active = temporary;
            var copy = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
            copy.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            copy.Apply();
            Color32[] result = copy.GetPixels32();
            UnityEngine.Object.Destroy(copy);
            return result;
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(temporary);
        }
    }

    private static int Int(JsonObject o, string name) { try { return o[name] is JsonValue v ? (int)Math.Round(v.GetValue<double>()) : 0; } catch { return 0; } }
    private static double Dbl(JsonObject o, string name) { try { return o[name] is JsonValue v ? v.GetValue<double>() : 0; } catch { return 0; } }

    private static byte[]? Pixels(string path, out int width, out int height)
    {
        width = height = 0;
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        try
        {
            if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(path))) return null;
            width = texture.width;
            height = texture.height;
            Color32[] colours = texture.GetPixels32();
            var bytes = new byte[colours.Length * 4];
            for (int i = 0; i < colours.Length; i++)
            {
                Color32 c = colours[i];
                bytes[i * 4] = c.r; bytes[i * 4 + 1] = c.g; bytes[i * 4 + 2] = c.b; bytes[i * 4 + 3] = c.a;
            }
            return bytes;
        }
        finally { UnityEngine.Object.Destroy(texture); }
    }

    // ------------------------------------------------------------ canvas resize after the pin (2026-11-16)

    private readonly record struct Content(int W, int H, int L, int T, int R, int B, ulong Hash, long Ticks)
    {
        public override string ToString() => $"{W}x{H}|{L},{T},{R},{B}|{Hash:x16}|{Ticks}";
        public static Content? Parse(string? text)
        {
            try
            {
                string[] p = (text ?? string.Empty).Split('|');
                if (p.Length != 4) return null;
                string[] size = p[0].Split('x'), box = p[1].Split(',');
                return new Content(int.Parse(size[0]), int.Parse(size[1]), int.Parse(box[0]), int.Parse(box[1]), int.Parse(box[2]), int.Parse(box[3]),
                                   ulong.Parse(p[2], System.Globalization.NumberStyles.HexNumber), long.Parse(p[3]));
            }
            catch { return null; }
        }
    }

    /// <summary>The drawing's box (top-left coordinates, inclusive) and a hash of its pixels (alpha, colour where visible).</summary>
    private static Content? Signature(byte[] px, int w, int h, long ticks)
    {
        int l = w, t = h, r = -1, b = -1;
        for (int row = 0; row < h; row++)
            for (int x = 0; x < w; x++)
                if (px[(row * w + x) * 4 + 3] > 0)
                {
                    int y = h - 1 - row;
                    if (x < l) l = x; if (x > r) r = x; if (y < t) t = y; if (y > b) b = y;
                }
        if (r < 0) return null;
        ulong hash = 14695981039346656037UL;
        void Mix(int v) { hash ^= (byte)v; hash *= 1099511628211UL; }
        Mix(r - l); Mix((r - l) >> 8); Mix(b - t); Mix((b - t) >> 8);
        for (int y = t; y <= b; y++)
        {
            int row = h - 1 - y;
            for (int x = l; x <= r; x++)
            {
                int i = (row * w + x) * 4;
                Mix(px[i + 3]);
                if (px[i + 3] > 0) { Mix(px[i]); Mix(px[i + 1]); Mix(px[i + 2]); }
            }
        }
        return new Content(w, h, l, t, r, b, hash, ticks);
    }

    /// <summary>shift is where a canvas dialog puts old content when the size changes by extra (left / centre / right).</summary>
    private static bool CanvasShift(int shift, int extra) =>
        Math.Abs(shift) <= 1 || Math.Abs(shift - extra / 2.0) <= 1 || Math.Abs(shift - extra) <= 1;

    /// <summary>
    /// Pinned anchor + the PNG changed since last checked: when only the canvas size changed (same drawing, moved the
    /// way a canvas dialog moves it) the pin moves with the drawing; otherwise it stays. Remembers the drawing in the
    /// JSON ("autoContent") either way.
    /// </summary>
    private static (float X, float Y) Follow(string png, JsonObject? node, float px, float py)
    {
        if (node == null) return (px, py);
        long ticks = File.GetLastWriteTimeUtc(png).Ticks;
        if (Checked.TryGetValue(png, out long seen) && seen == ticks) return (px, py);
        Checked[png] = ticks;
        Content? old = Content.Parse(node["autoContent"] is JsonValue v && v.TryGetValue(out string? s) ? s : null);
        if (old is { } known && known.Ticks == ticks) return (px, py);
        byte[]? pixels = Pixels(png, out int w, out int h);
        if (pixels == null || Signature(pixels, w, h, ticks) is not { } now) return (px, py);
        float nx = px, ny = py;
        string name = Path.GetFileName(Path.GetDirectoryName(png)) + "/" + Path.GetFileName(png);
        if (old is { } before && before.Hash == now.Hash && (before.W != w || before.H != h))
        {
            int sx = now.L - before.L, sy = now.T - before.T;
            // 2026-11-16 round 2 (log 20-28: 314x327 -> 171x173 moved the drawing by (-34,-133)): the creator's
            // painting app places the old canvas anywhere, not on the nine dialog positions. A new canvas size with
            // the very same drawing is a canvas resize, whatever the shift.
            if (true)
            {
                nx = px + sx;
                ny = py + sy;
                MelonLogger.Msg($"{Prefix} {name}: canvas {before.W}x{before.H} -> {w}x{h}, same drawing moved by ({sx},{sy}): anchor follows it ({px:0.##},{py:0.##}) -> ({nx:0.##},{ny:0.##}), the part stays in place");
            }
            else
                MelonLogger.Msg($"{Prefix} {name}: canvas {before.W}x{before.H} -> {w}x{h}, drawing moved by ({sx},{sy}) (not a canvas-dialog position): anchor kept, the part moves with the drawing");
        }
        node["autoContent"] = now.ToString();
        if (nx != px || ny != py)
        {
            node["anchorX"] = Math.Round(nx, 4);
            node["anchorY"] = Math.Round(ny, 4);
            if (node["autoAnchorX"] != null) { node["autoAnchorX"] = Math.Round(nx, 4); node["autoAnchorY"] = Math.Round(ny, 4); }
        }
        try { WriteNode(png, node); }
        catch (Exception exception) { MelonLogger.Warning($"{Prefix} {name}: anchor record not updated: {exception.GetBaseException().Message}"); }
        return (nx, ny);
    }

    /// <summary>Not found by matching: if the drawing is about the original's size, the original's box centred on it.</summary>
    private static (int Left, int Top, string How)? OnDrawing(string png, int ow, int oh, int w, int h, string why)
    {
        byte[]? pixels = Pixels(png, out int cw, out int ch);
        if (pixels == null || cw != w || ch != h || Signature(pixels, w, h, 0) is not { } box) return null;
        int bw = box.R - box.L + 1, bh = box.B - box.T + 1;
        if (bw < ow * 0.8 || bw > ow * 1.3 || bh < oh * 0.8 || bh > oh * 1.3) return null;
        int left = Math.Clamp((int)Math.Round((box.L + box.R + 1) / 2.0 - ow / 2.0), 0, w - ow);
        int top = Math.Clamp((int)Math.Round((box.T + box.B + 1) / 2.0 - oh / 2.0), 0, h - oh);
        return (left, top, $"on the drawing ({bw}x{bh} at {box.L},{box.T}, about the original's size): original not found by matching ({why}); set canvasAnchor in the JSON if it is wrong");
    }

    private static void WriteJson(string png, JsonObject node, float x, float y, string stamp, string how)
    {
        node["anchorX"] = Math.Round(x, 4);
        node["anchorY"] = Math.Round(y, 4);
        node["autoAnchorX"] = Math.Round(x, 4);
        node["autoAnchorY"] = Math.Round(y, 4);
        node["autoFor"] = stamp;
        node["autoAnchor"] = "set once by SkinForger when the canvas was first enlarged (" + how + "); it stays fixed from now on. Change anchorX / anchorY to move it, or delete them to have it found again";
        // Make the option visible: the nine canvas-dialog positions, or "auto" (find the original drawing).
        if (node["canvasAnchor"] == null) node["canvasAnchor"] = "auto";
        WriteNode(png, node);
    }

    private static (bool Ok, int W, int H) ReadSize(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[24];
            if (stream.Read(header) != 24 || header[0] != 0x89 || header[1] != (byte)'P') return (false, 0, 0);
            int w = header[16] << 24 | header[17] << 16 | header[18] << 8 | header[19];
            int h = header[20] << 24 | header[21] << 16 | header[22] << 8 | header[23];
            return (w > 0 && h > 0, w, h);
        }
        catch { return (false, 0, 0); }
    }

    private static float? Float(JsonObject? node, string name)
    {
        try { return node?[name] is JsonValue v ? (float)v.GetValue<double>() : null; } catch { return null; }
    }

    private static bool Near(float a, float b) => Math.Abs(a - b) <= Tolerance;
}
