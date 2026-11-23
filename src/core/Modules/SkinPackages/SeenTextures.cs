using Il2CppSpine.Unity;
using MelonLoader;
using SkinForger.HexieBlock;
using UnityEngine;
using UnityEngine.UI;

namespace SkinForger.SkinPackages;

/// <summary>
/// A history of the pictures the game has actually shown, the way the Sound page lists "Recently Played"
/// (2026-11-18, user design: "不如改成历史显示的图片像sounds那样").
///
/// The first attempt at this — "Dump Other Pictures" — walked every loaded texture and offered 150 of them,
/// almost all pieces of characters. It told the creator nothing about WHERE a picture is on screen, which is
/// the only question being asked: "that character at the right of the stage, which file is it?"
///
/// So this watches the live scene instead of the asset list. Once a second it reads the renderers and UI
/// graphics that exist right now and writes down every texture they draw, with who draws it:
///
///   Spine  — a Spine skeleton: every character, elfin, boss and background character is one of these;
///   Sprite — a plain SpriteRenderer: stage art, effects;
///   UI     — a Canvas picture: the HUD, and the art that sits beside the HP bar.
///
/// The list is newest-first and keeps the owner's object name, so a picture can be matched to what was on
/// screen. "Dump" writes one picture to SkinCreator\Images\Other and, like every Dump, never overwrites.
/// </summary>
internal static class SeenTextures
{
    private const string Prefix = "[SkinForge][Seen]";

    internal enum Kind { Spine, Sprite, UI }

    internal sealed class Seen
    {
        internal string Name = string.Empty;
        internal int W, H;
        internal Kind Kind;
        internal string Owner = string.Empty;
        internal Texture2D? Texture;

        /// <summary>The sweep that last saw this picture being drawn. Older than the current one = gone from
        /// the screen, which is not the same as unloaded: it may still be dumpable.</summary>
        internal int LastSweep;

        /// <summary>Set when a Dump found the picture was not loaded any more. Cleared when one succeeds.</summary>
        internal bool Gone;
    }

    /// <summary>Counts the sweeps, so a row can say whether it was on screen in the latest one.</summary>
    internal static int Sweep { get; private set; }

    /// <summary>
    /// One sweep a second to start with. FontHooks learned the hard way that a periodic FindObjectsOfType
    /// over a big scene is a visible hitch (2026-11-16: 3000 Text objects every 3 s), so the interval here
    /// is not fixed: a sweep that costs more buys itself proportionally more time, up to ten seconds. A
    /// cheap scene keeps the one-second list; an expensive one slows down instead of stuttering.
    /// </summary>
    private const float Interval = 1f, MaxInterval = 10f;

    /// <summary>How many sweeps' worth of time one sweep may cost: 0.2% of the time between sweeps.</summary>
    private const float Budget = 500f;

    /// <summary>Enough to cover a whole song without growing without end.</summary>
    private const int Limit = 400;

    private static float _next, _interval = Interval;
    private static readonly List<Seen> List = new();
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase);

    internal static int Count { get { lock (List) return List.Count; } }

    internal static List<Seen> Snapshot() { lock (List) return List.ToList(); }

    internal static void Clear()
    {
        lock (List) { List.Clear(); Known.Clear(); }
    }

    internal static void Poll(float now)
    {
        if (now < _next) return;
        Sweep++;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            foreach (Renderer renderer in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (renderer == null || renderer.Pointer == IntPtr.Zero) continue;
                GameObject owner = renderer.gameObject;
                bool spine = owner.GetComponent<SkeletonRenderer>() != null ||
                             owner.GetComponent<SkeletonMecanim>() != null;
                Material material = renderer.sharedMaterial;
                if (material == null || material.Pointer == IntPtr.Zero) continue;
                Record(material.mainTexture, spine ? Kind.Spine : Kind.Sprite, owner);
            }

            foreach (Graphic graphic in UnityEngine.Object.FindObjectsOfType<Graphic>())
            {
                if (graphic == null || graphic.Pointer == IntPtr.Zero) continue;
                GameObject owner = graphic.gameObject;
                bool spine = owner.GetComponent<SkeletonGraphic>() != null;
                Record(graphic.mainTexture, spine ? Kind.Spine : Kind.UI, owner);
            }
        }
        catch (Exception exception)
        {
            // A scene mid-load can hand back a destroyed object; the next sweep is a second away.
            MelonLogger.Warning($"{Prefix} sweep failed: {exception.GetBaseException().Message}");
        }
        finally
        {
            float cost = (float)watch.Elapsed.TotalSeconds;
            float wanted = Math.Min(MaxInterval, Math.Max(Interval, cost * Budget));
            if (wanted > _interval * 1.5f || wanted < _interval * 0.5f)
            {
                if (wanted > _interval)
                    MelonLogger.Msg($"{Prefix} a sweep costs {cost * 1000f:0.0} ms here — looking every {wanted:0.#} s instead of {_interval:0.#} s");
                _interval = wanted;
            }
            _next = Time.realtimeSinceStartup + _interval;
        }
    }

    private static void Record(Texture? texture, Kind kind, GameObject owner)
    {
        if (texture == null || texture.Pointer == IntPtr.Zero) return;
        Texture2D? flat = texture.TryCast<Texture2D>();
        if (flat == null) return;
        string name = flat.name ?? string.Empty;
        if (name.Length == 0 || HexiePolicy.IsHexieName(name)) return;
        lock (List)
        {
            if (!Known.Add(name))
            {
                // Seen before: keep its place in the list, but record that it is on screen right now.
                foreach (Seen existing in List)
                    if (string.Equals(existing.Name, name, StringComparison.Ordinal))
                    {
                        existing.LastSweep = Sweep;
                        existing.Texture = flat;
                        existing.Gone = false;
                        break;
                    }
                return;
            }
            List.Insert(0, new Seen
            {
                LastSweep = Sweep,
                Name = name,
                W = flat.width,
                H = flat.height,
                Kind = kind,
                Owner = OwnerPath(owner),
                Texture = flat
            });
            if (List.Count > Limit)
            {
                Known.Remove(List[List.Count - 1].Name);
                List.RemoveAt(List.Count - 1);
            }
        }
    }

    /// <summary>"parent/object" — one level up is usually what names the thing on screen.</summary>
    private static string OwnerPath(GameObject owner)
    {
        try
        {
            Transform? parent = owner.transform.parent;
            return parent != null ? parent.name + "/" + owner.name : owner.name;
        }
        catch (Exception) { return owner.name; }
    }

    /// <summary>
    /// Writes one picture to SkinCreator\Images\Other, with the sidecar JSON every other dumped image has
    /// (2026-11-18, user: "万一是spine人偶动画的组件就麻烦了").
    ///
    /// Two protections, because this list can show anything the game draws:
    ///
    ///  1. A picture that is already a costume's atlas is NOT copied here. SkinForger dumps a Spine atlas as
    ///     a folder of its parts, each with its own anchor; a flat copy of the whole sheet next to it would
    ///     be a second, broken way to edit the same art. The dump says where the real folder is instead.
    ///  2. Everything written here gets &lt;name&gt;.json beside it, in the same shape as a part's sidecar:
    ///     the anchor is pinned at the middle of the picture as it was dumped. Enlarging the canvas later
    ///     therefore has a recorded origin to keep the drawing on — without the sidecar an enlarged picture
    ///     has nothing saying where its old middle was.
    ///
    /// Neither the PNG nor the JSON is ever overwritten: a file already on disk is the creator's.
    /// </summary>
    internal static string Dump(Seen seen)
    {
        // Every press says what happened, in the log as well as on the window. A press that quietly did
        // nothing is indistinguishable from a broken button (2026-11-18: two UI pictures looked like they
        // dumped nothing; the picture was simply no longer on screen, and only a line of grey text said so).
        string outcome = DumpCore(seen);
        seen.Gone = outcome.StartsWith("This picture is not loaded", StringComparison.Ordinal);
        MelonLogger.Msg($"{Prefix} {seen.Name} ({seen.W}x{seen.H}, {seen.Kind}, {seen.Owner}): {outcome}");
        return outcome;
    }

    private static string DumpCore(Seen seen)
    {
        try
        {
            // The object that drew it may be gone (a popup that closed), but the picture itself is usually
            // still loaded: find it again by name and size (2026-11-19, user: the two Warning pictures
            // could never be dumped, because by the time the list was read the popup had closed).
            Texture2D? texture = Alive(seen.Texture) ? seen.Texture : Refind(seen);
            if (texture == null)
                return "This picture is not loaded any more, so there is nothing to write. Open the screen that shows it and press Dump again.";
            seen.Texture = texture;

            string? home = CostumeFolderOf(seen.Name);
            if (home != null)
                return $"{seen.Name} is already dumped as a costume atlas — edit it in Images\\{home}\\{seen.Name}\\, not here.";

            string folder = Path.Combine(SkinForgerPaths.Creator, OtherFolder);
            Directory.CreateDirectory(folder);
            string stem = Safe(seen.Name);
            string file = Path.Combine(folder, stem + ".png");
            bool had = File.Exists(file);
            if (!had && !CursorDump.WriteTexture(texture, file))
                return $"{seen.Name} could not be read back.";

            string sidecar = WriteSidecar(folder, stem, seen);
            Manifest.Note(folder, stem + ".png", seen);

            if (had) return $"Kept Images\\{OtherFolder}\\{stem}.png (not overwritten){sidecar}";
            MelonLogger.Msg($"{Prefix} wrote {stem}.png ({seen.W}x{seen.H}) from {seen.Owner}");
            return $"Wrote Images\\{OtherFolder}\\{stem}.png{sidecar}";
        }
        catch (Exception exception)
        {
            return "Dump failed: " + exception.GetBaseException().Message;
        }
    }

    private static bool Alive(Texture2D? texture)
    {
        try { return texture != null && texture.Pointer != IntPtr.Zero && texture.width > 0; }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// The same picture among everything the game still has loaded. A texture outlives the object that
    /// drew it, so this finds it by name and size even after the screen that used it has gone.
    /// </summary>
    private static Texture2D? Refind(Seen seen)
    {
        try
        {
            foreach (Texture2D candidate in Resources.FindObjectsOfTypeAll<Texture2D>())
            {
                if (!Alive(candidate)) continue;
                if (!string.Equals(candidate.name, seen.Name, StringComparison.Ordinal)) continue;
                if (candidate.width != seen.W || candidate.height != seen.H) continue;
                return candidate;
            }
        }
        catch (Exception) { /* mid-load: the next press can try again */ }
        return null;
    }

    /// <summary>The sidecar JSON, shared with the cursor dump so both folders read the same way.</summary>
    internal static string WriteSidecarFor(string png, int width, int height, string source, string kind)
    {
        var seen = new Seen { Name = Path.GetFileNameWithoutExtension(png), W = width, H = height, Owner = source };
        return WriteSidecar(Path.GetDirectoryName(png)!, Path.GetFileNameWithoutExtension(png), seen);
    }

    /// <summary>
    /// The costume whose dump already owns this atlas, or null. A folder named after the texture under
    /// Images\&lt;Costume&gt;\ is SkinForger's own dump of that Spine sheet, split into parts with anchors.
    /// </summary>
    private static string? CostumeFolderOf(string textureName)
    {
        try
        {
            string root = SkinForgerPaths.Creator;
            if (!Directory.Exists(root)) return null;
            foreach (string costume in Directory.GetDirectories(root))
            {
                string leaf = Path.GetFileName(costume);
                if (string.Equals(leaf, OtherFolder, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(leaf, CursorDump.FolderName, StringComparison.OrdinalIgnoreCase)) continue;
                if (Directory.Exists(Path.Combine(costume, textureName))) return leaf;
            }
        }
        catch (Exception) { /* a missing folder means nothing is known */ }
        return null;
    }

    /// <summary>
    /// &lt;name&gt;.json beside the picture, the same shape a part's sidecar has, with the anchor at the middle
    /// of the picture as dumped. Written only when it is not there: the creator may have moved the anchor.
    /// </summary>
    private static string WriteSidecar(string folder, string stem, Seen seen)
    {
        string path = Path.Combine(folder, stem + ".json");
        try
        {
            if (File.Exists(path)) return "  (kept your " + stem + ".json)";
            string json = string.Join(Environment.NewLine, new[]
            {
                "{",
                "  \"version\": 2,",
                "  \"origin\": \"top-left\",",
                $"  \"anchorX\": {(seen.W / 2.0).ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture)},",
                $"  \"anchorY\": {(seen.H / 2.0).ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture)},",
                "  \"canvasAnchor\": \"\",",
                "  \"rotation\": 0.0,",
                "  \"offsetX\": 0.0,",
                "  \"offsetY\": 0.0,",
                "  \"Scale\": 1.0,",
                "  \"MovementAmplitude\": 0.0,",
                "  \"X\": 0.0,",
                "  \"Y\": 0.0,",
                "  \"Rotate\": 0.0,",
                $"  \"dumpedWidth\": {seen.W},",
                $"  \"dumpedHeight\": {seen.H},",
                $"  \"source\": \"{Escape(seen.Owner)}\",",
                $"  \"kind\": \"{seen.Kind}\"",
                "}"
            });
            File.WriteAllText(path, json);
            return "  + " + stem + ".json";
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"{Prefix} {stem}.json: {exception.GetBaseException().Message}");
            return "  (json failed)";
        }
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>
    /// manifest.json of the Other folder: one line per picture saying where it came from. A costume's dump
    /// has one too, so the folder reads the same way; this one is merged, never rebuilt, so a picture dumped
    /// in an earlier session is not dropped when a new one is added.
    /// </summary>
    private static class Manifest
    {
        internal static void Note(string folder, string file, Seen seen)
        {
            try
            {
                string path = Path.Combine(folder, "manifest.json");
                var lines = new List<string>();
                if (File.Exists(path))
                    foreach (string line in File.ReadAllLines(path))
                    {
                        string trimmed = line.Trim();
                        if (trimmed.StartsWith("{ \"file\"", StringComparison.Ordinal))
                            lines.Add(trimmed.TrimEnd(','));
                    }

                string entry = "{ \"file\": \"" + Escape(file) + "\", \"width\": " + seen.W +
                               ", \"height\": " + seen.H + ", \"kind\": \"" + seen.Kind +
                               "\", \"source\": \"" + Escape(seen.Owner) + "\" }";
                lines.RemoveAll(l => l.StartsWith("{ \"file\": \"" + Escape(file) + "\"", StringComparison.Ordinal));
                lines.Add(entry);
                lines.Sort(StringComparer.OrdinalIgnoreCase);

                var text = new List<string> { "{", "  \"version\": 1,", "  \"pictures\": [" };
                for (int i = 0; i < lines.Count; i++)
                    text.Add("    " + lines[i] + (i < lines.Count - 1 ? "," : string.Empty));
                text.Add("  ]");
                text.Add("}");
                File.WriteAllLines(path, text);
            }
            catch (Exception exception)
            {
                MelonLogger.Warning($"{Prefix} manifest.json: {exception.GetBaseException().Message}");
            }
        }
    }

    internal const string OtherFolder = "Other";

    private static string Safe(string name)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return name.Trim().Length == 0 ? "picture" : name.Trim();
    }
}
