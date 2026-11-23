using System.Text.Json;
using System.Text.Json.Serialization;
using Il2CppTMPro;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;

namespace SkinForger.SkinPackages;

/// <summary>
/// SkinCreator\Text\LabelFixes.json -- the author's own adjustments to single labels of the game's interface.
///
/// Why this exists (2026-11-21). A replaced font made the skin name on the elfin / character select card lose its
/// bottom edge. Three builds tried to work it out from the inside and all three were wrong:
///
///   1. "its box or a mask is cutting it"  -- the height rule never fired once (the fit was only ever measured at
///      the moment a label was handed our font, and that label changes its string per character);
///   2. "then it is the re-measuring"      -- added, and the picture did not change;
///   3. "then it is an outline shader"     -- the diagnostic came back "Default UI Material" on every label of
///      that screen, and the nine parents above it hold no mask at all.
///
/// Each round cost the player a launch and told them "try again". That is the wrong shape of help. The thing they
/// can see and I cannot is the screen itself -- so the adjustment belongs in their hands, as numbers they can
/// change and see the result of after one Reload, with no build and no waiting for me.
///
/// A fix finds its label by the TEXT IT SHOWS (what the player can read on screen) or by the object's path, and
/// then may: stop the label being truncated, make its box taller, move it, or scale its font size. Every applied
/// fix says so in the log with the label's full path and its numbers, which is also how the object finally gets
/// identified for good.
/// </summary>
internal sealed class LabelFix
{
    /// <summary>Part of the text as it appears on screen, e.g. "红头罩". Empty = match by <see cref="Path"/> only.</summary>
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;

    /// <summary>Part of the object's path, e.g. "TxtElfinName". Empty = match by <see cref="Text"/> only.</summary>
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;

    /// <summary>Pixels to add to the label's box height (its middle stays put: half goes up, half down).</summary>
    [JsonPropertyName("extraHeight")] public float ExtraHeight { get; set; }

    /// <summary>Pixels to move the label by. Positive = up.</summary>
    [JsonPropertyName("moveUp")] public float MoveUp { get; set; }

    /// <summary>Multiplies the font size. 0.9 = nine tenths of the size the game uses. 1 = leave it.</summary>
    [JsonPropertyName("sizeScale")] public float SizeScale { get; set; } = 1f;

    /// <summary>Let the text draw outside its box instead of being cut off at it.</summary>
    [JsonPropertyName("neverCut")] public bool NeverCut { get; set; } = true;

    /// <summary>
    /// Draw the whole label, even the part a mask would cut away.
    ///
    /// This is the one that answers the player's question (2026-11-21). The name on the elfin select card lives
    /// inside a parent called ImgRoleNameMask -- Muse Dash masks that label on purpose, and with the game's own
    /// font the glyphs happen to fit inside the mask's shape. A replacement font draws them a little taller and
    /// the bottom lands outside, so it is simply not drawn. Nothing about the text's own box or size is wrong,
    /// which is why two builds that moved and shrank the text changed nothing.
    ///
    /// uGUI has a switch for exactly this: a Graphic with maskable = false is not registered with any mask or
    /// rect-clipper above it, so it draws in full. No layout is touched and nothing has to be nudged by hand.
    /// </summary>
    [JsonPropertyName("ignoreMask")] public bool IgnoreMask { get; set; } = true;

    /// <summary>Free text; SkinForger never reads it.</summary>
    [JsonPropertyName("note")] public string Note { get; set; } = string.Empty;
}

internal static class LabelFixes
{
    private const string Prefix = "[SkinForge][Font]";
    public const string FileName = "LabelFixes.json";

    private static readonly List<LabelFix> Fixes = new();
    private static readonly HashSet<IntPtr> Done = new();
    private static readonly HashSet<string> Said = new(StringComparer.Ordinal);
    private static bool _loaded;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The file, written with one worked example the first time SkinForger runs.</summary>
    private static string PathOfFile => System.IO.Path.Combine(SkinForgerPaths.Text, FileName);

    /// <summary>Read again after a Reload, so editing the file and pressing Reload is the whole loop.</summary>
    internal static void Forget()
    {
        _loaded = false;
        Done.Clear();
        Said.Clear();
    }

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        Fixes.Clear();
        try
        {
            string path = PathOfFile;
            if (!File.Exists(path))
            {
                if (!Directory.Exists(SkinForgerPaths.Text)) return;   // no Text workspace yet: nothing to explain
                File.WriteAllText(path, Template, new System.Text.UTF8Encoding(false));
                MelonLogger.Msg($"{Prefix} {FileName} written: adjust a single label of the game's interface there " +
                                "(it is read again on every Reload)");
            }
            List<LabelFix>? read = JsonSerializer.Deserialize<List<LabelFix>>(File.ReadAllText(path), Options);
            foreach (LabelFix fix in read ?? new List<LabelFix>())
                if (!string.IsNullOrWhiteSpace(fix.Text) || !string.IsNullOrWhiteSpace(fix.Path))
                    Fixes.Add(fix);
            if (Fixes.Count > 0) MelonLogger.Msg($"{Prefix} {FileName}: {Fixes.Count} label adjustment(s)");
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"{Prefix} {FileName} could not be read ({exception.GetBaseException().Message}); " +
                                "no label adjustments this session. Fix the file and press Reload.");
        }
    }

    private static LabelFix? Match(string content, string path)
    {
        foreach (LabelFix fix in Fixes)
        {
            bool byText = fix.Text.Length > 0 && content.IndexOf(fix.Text, StringComparison.Ordinal) >= 0;
            bool byPath = fix.Path.Length > 0 && path.IndexOf(fix.Path, StringComparison.OrdinalIgnoreCase) >= 0;
            bool needsBoth = fix.Text.Length > 0 && fix.Path.Length > 0;
            if (needsBoth ? byText && byPath : byText || byPath) return fix;
        }
        return null;
    }

    /// <summary>
    /// Applies the author's adjustment to a uGUI label, once per label per Reload.
    /// Returns the font size to use, or 0 to leave the size alone.
    /// </summary>
    internal static void Apply(Text text)
    {
        Load();
        if (Fixes.Count == 0 || text == null || text.Pointer == IntPtr.Zero) return;
        try
        {
            string content = text.text ?? string.Empty;
            if (content.Length == 0) return;
            string path = Where(text.transform);
            LabelFix? fix = Match(content, path);
            if (fix == null || !Done.Add(text.Pointer)) return;

            RectTransform rect = text.rectTransform;
            Vector2 sizeBefore = rect.sizeDelta, posBefore = rect.anchoredPosition;
            int sizeFontBefore = text.fontSize;

            if (fix.IgnoreMask) text.maskable = false;
            if (fix.NeverCut) text.verticalOverflow = VerticalWrapMode.Overflow;
            Grow(rect, fix.ExtraHeight);
            if (Math.Abs(fix.MoveUp) > 0.01f) rect.anchoredPosition = new Vector2(rect.anchoredPosition.x, rect.anchoredPosition.y + fix.MoveUp);
            if (fix.SizeScale > 0.01f && Math.Abs(fix.SizeScale - 1f) > 0.001f)
                text.fontSize = Math.Max(1, (int)Math.Round(text.fontSize * fix.SizeScale));

            Report(content, path, $"Text box {sizeBefore.y:0.#}->{rect.sizeDelta.y:0.#} pos {posBefore.y:0.#}->{rect.anchoredPosition.y:0.#} " +
                                  $"size {sizeFontBefore}->{text.fontSize} neverCut={fix.NeverCut} ignoreMask={fix.IgnoreMask}");
        }
        catch (Exception) { }
    }

    /// <summary>The same for a TextMeshPro label.</summary>
    internal static void Apply(TMP_Text text)
    {
        Load();
        if (Fixes.Count == 0 || text == null || text.Pointer == IntPtr.Zero) return;
        try
        {
            string content = text.text ?? string.Empty;
            if (content.Length == 0) return;
            string path = Where(text.transform);
            LabelFix? fix = Match(content, path);
            if (fix == null || !Done.Add(text.Pointer)) return;

            RectTransform rect = text.rectTransform;
            Vector2 sizeBefore = rect.sizeDelta, posBefore = rect.anchoredPosition;
            float sizeFontBefore = text.fontSize;

            if (fix.IgnoreMask) text.maskable = false;
            if (fix.NeverCut) text.overflowMode = TextOverflowModes.Overflow;
            Grow(rect, fix.ExtraHeight);
            if (Math.Abs(fix.MoveUp) > 0.01f) rect.anchoredPosition = new Vector2(rect.anchoredPosition.x, rect.anchoredPosition.y + fix.MoveUp);
            if (fix.SizeScale > 0.01f && Math.Abs(fix.SizeScale - 1f) > 0.001f) text.fontSize *= fix.SizeScale;

            Report(content, path, $"TextMeshPro box {sizeBefore.y:0.#}->{rect.sizeDelta.y:0.#} pos {posBefore.y:0.#}->{rect.anchoredPosition.y:0.#} " +
                                  $"size {sizeFontBefore:0.#}->{text.fontSize:0.#} neverCut={fix.NeverCut} ignoreMask={fix.IgnoreMask}");
        }
        catch (Exception) { }
    }

    /// <summary>
    /// Makes a label's box taller WITHOUT moving what it shows.
    ///
    /// 2026-11-21: the first version just added to sizeDelta.y, and on the elfin card -- whose label is pinned to
    /// the top of its parent -- the box grew downwards and took the text with it, so the player's name ended up
    /// under the card instead of on it. The box has to grow around the middle it already had, whatever its pivot
    /// and anchors say, or "make the box taller" is a trap rather than a knob.
    /// </summary>
    private static void Grow(RectTransform rect, float extra)
    {
        if (Math.Abs(extra) <= 0.01f) return;
        Vector2 size = rect.sizeDelta;
        Vector2 pivot = rect.pivot;
        rect.sizeDelta = new Vector2(size.x, size.y + extra);
        // Growing moves the box by (pivot.y - 0.5) * extra away from its middle; put that back.
        float drift = (pivot.y - 0.5f) * extra;
        if (Math.Abs(drift) > 0.01f)
            rect.anchoredPosition = new Vector2(rect.anchoredPosition.x, rect.anchoredPosition.y + drift);
    }

    private static void Report(string content, string path, string what)
    {
        if (!Said.Add(path) || Said.Count > 24) return;
        MelonLogger.Msg($"{Prefix} {FileName} adjusted \"{content.Replace("\n", " ")}\": {what} -- {path}");
    }

    private static string Where(Transform? t)
    {
        var names = new List<string>();
        for (int i = 0; t != null && t.Pointer != IntPtr.Zero && i < 10; i++, t = t.parent) names.Add(t.name);
        names.Reverse();
        return string.Join("/", names);
    }

    /// <summary>
    /// The file SkinForger writes the first time. The first entry is the one the player asked for; the numbers are
    /// a starting point, not an answer, and the comments say which number to turn for which symptom.
    /// </summary>
    private const string Template = @"// SkinForger -- adjustments to single labels of Muse Dash's own interface. (Empty by default; you can ignore it.)
//
// NOTE: character / elfin NAMES do not belong here. SkinForger always draws them in full, past the game's own
// name mask, whatever font you use and whether or not you changed one. That is built in.
//
// This file is for when some OTHER line of the interface sits wrong. Edit it, then press Reload in SkinForger
// (key 1 opens the window). No restart.
//
// A label is found by the TEXT IT SHOWS (""text"", part of what you read on screen) or by its place in the game
// (""path""). Give both and both have to match. Then:
//
//   ""ignoreMask"":  true  -- draw the whole label even where a mask of the game would cut it away
//   ""neverCut"":    true  -- the text may be drawn outside its own box instead of being cut off at it
//   ""extraHeight"": 40    -- make the box this many pixels taller (its middle stays put)
//   ""moveUp"":      10    -- move the whole label up this many pixels (negative = down)
//   ""sizeScale"":   0.9   -- nine tenths of the font size the game uses (1 = leave it alone)
//
// Every adjustment that finds its label writes a line in the MelonLoader log with the label's full path and the
// numbers before and after. If no line appears, the ""text"" / ""path"" matched nothing.
//
// Example (delete the // to use it):
// [ { ""note"": """", ""text"": ""some words on screen"", ""ignoreMask"": true, ""neverCut"": true,
//     ""extraHeight"": 0, ""moveUp"": 0, ""sizeScale"": 1 } ]
[
]";
}
