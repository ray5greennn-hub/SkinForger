using HarmonyLib;
using Il2CppTMPro;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;

namespace SkinForger.SkinPackages;

/// <summary>
/// Font replacement (2026-11-15): the first .ttf / .otf in SkinCreator\Text\Fonts\CN (or EN) replaces the font of
/// EVERY text in the game while the game language is Simplified Chinese (or English), so edited and unedited texts
/// look the same.
///  - UGUI Text: Text.OnEnable and MDLocalizationFont.OnEnable / Start postfixes (the game sets its per-language font
///    there), plus one sweep over all loaded texts when the font or the language changes.
///  - TextMeshPro: TextMeshProUGUI.OnEnable postfix with a TMP font asset made from the same font.
/// The game's own font of each component is remembered and put back when the font file is removed or the language
/// changes. The font is loaded with Font.Internal_CreateFontFromPath (Unity's file-font loader); nothing is installed.
/// </summary>
internal static class FontHooks
{
    private const string Prefix = "[SkinForge][Font]";
    private static readonly Dictionary<string, (string File, Font? Font, TMP_FontAsset? Tmp)> Loaded = new(StringComparer.Ordinal);
    private static readonly Dictionary<IntPtr, Font> OriginalFonts = new();
    private static readonly Dictionary<IntPtr, TMP_FontAsset> OriginalTmp = new();
    private static string? _activeLanguage;
    private static Font? _active;
    private static TMP_FontAsset? _activeTmp;
    private static bool _dirty = true;
    private static float _nextLanguageCheck;

    internal static string Status(string language) =>
        Loaded.TryGetValue(language, out var entry) && entry.Font != null ? Path.GetFileName(entry.File.Split('|')[0]) : string.Empty;

    /// <summary>The font file of this language when its font is not installed in Windows yet (Unity can only use installed fonts).</summary>
    internal static string? NeedsInstall(string language) => NotInstalled.TryGetValue(language, out var item) ? item.File : null;

    /// <summary>True when the font is installed, but only for this Windows account (Unity cannot see it).</summary>
    internal static bool InstalledForMeOnly(string language) => NotInstalled.TryGetValue(language, out var item) && item.UserOnly;

    private static readonly Dictionary<string, (string File, string Family, bool UserOnly)> NotInstalled = new(StringComparer.Ordinal);

    /// <summary>Is a font of this family in the per-user font folder (Windows "Install" = for this account only)?</summary>
    private static bool InstalledForUserOnly(string family)
    {
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");
            if (!Directory.Exists(folder)) return false;
            foreach (string file in Directory.GetFiles(folder))
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is not (".ttf" or ".otf" or ".ttc")) continue;
                if (string.Equals(FontFile.FamilyName(File.ReadAllBytes(file)), family, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { }
        return false;
    }
    private static float _nextInstallCheck;
    private static readonly HashSet<string> Retried = new(StringComparer.OrdinalIgnoreCase);   // one reload per newly listed font

    internal static string? Problem(string language) => _problems.TryGetValue(language, out string? p) ? p : null;
    private static readonly Dictionary<string, string> _problems = new(StringComparer.Ordinal);

    internal static int Install(HarmonyLib.Harmony harmony)
    {
        int patched = 0;
        harmony.Patch(AccessTools.Method(typeof(Text), "OnEnable"), postfix: new HarmonyMethod(typeof(FontHooks), nameof(TextPostfix)));
        patched++;
        Type? localizationFont = AccessTools.TypeByName("Il2Cpp.MDLocalizationFont");
        foreach (string name in new[] { "OnEnable", "Start" })
            if (localizationFont != null && AccessTools.Method(localizationFont, name) is { } method)
            {
                harmony.Patch(method, postfix: new HarmonyMethod(typeof(FontHooks), nameof(LocalizationFontPostfix)));
                patched++;
            }
        harmony.Patch(AccessTools.Method(typeof(TextMeshProUGUI), "OnEnable"), postfix: new HarmonyMethod(typeof(FontHooks), nameof(TmpPostfix)));
        patched++;
        // The game also sets fonts later (e.g. when a panel shows another character): catch every font assignment.
        try { harmony.Patch(AccessTools.PropertySetter(typeof(Text), nameof(Text.font)), postfix: new HarmonyMethod(typeof(FontHooks), nameof(TextFontSetPostfix))); patched++; }
        catch (Exception exception) { MelonLogger.Warning($"{Prefix} Text.font setter not patched: {exception.GetBaseException().Message}"); }
        try { harmony.Patch(AccessTools.PropertySetter(typeof(TMP_Text), nameof(TMP_Text.font)), postfix: new HarmonyMethod(typeof(FontHooks), nameof(TmpFontSetPostfix))); patched++; }
        catch (Exception exception) { MelonLogger.Warning($"{Prefix} TMP_Text.font setter not patched: {exception.GetBaseException().Message}"); }
        // TextMeshPro reads glyphs with FontEngine.LoadFontFace(Font, ...). An installed-Windows font has no font data
        // inside the game, so that fails (CreateFontAsset returned null, 2026-11-15 log). For SkinForger's fonts the
        // face is loaded from the font FILE instead (FontEngine.LoadFontFace(path, size)).
        Type engine = typeof(UnityEngine.TextCore.LowLevel.FontEngine);
        foreach ((Type[] args, string prefix) in new[]
                 {
                     (new[] { typeof(Font) }, nameof(FacePrefix1)),
                     (new[] { typeof(Font), typeof(int) }, nameof(FacePrefix2)),
                     (new[] { typeof(Font), typeof(int), typeof(int) }, nameof(FacePrefix3)),
                 })
        {
            try
            {
                if (AccessTools.Method(engine, "LoadFontFace", args) is { } method)
                {
                    harmony.Patch(method, prefix: new HarmonyMethod(typeof(FontHooks), prefix) { priority = HarmonyLib.Priority.First });
                    patched++;
                }
            }
            catch (Exception exception) { MelonLogger.Warning($"{Prefix} FontEngine.LoadFontFace({args.Length}) not patched: {exception.GetBaseException().Message}"); }
        }
        return patched;
    }

    // ------------------------------------------------------------ TextMeshPro: font face from the file

    private static readonly Dictionary<IntPtr, string> FontFiles = new();

    private static bool Redirect(Font? font, int size, ref UnityEngine.TextCore.LowLevel.FontEngineError result)
    {
        if (font == null || font.Pointer == IntPtr.Zero || !FontFiles.TryGetValue(font.Pointer, out string? file)) return true;
        result = size > 0 ? UnityEngine.TextCore.LowLevel.FontEngine.LoadFontFace(file, size) : UnityEngine.TextCore.LowLevel.FontEngine.LoadFontFace(file);
        return false;
    }

    private static bool FacePrefix1(Font font, ref UnityEngine.TextCore.LowLevel.FontEngineError __result) => Redirect(font, 0, ref __result);
    private static bool FacePrefix2(Font font, int pointSize, ref UnityEngine.TextCore.LowLevel.FontEngineError __result) => Redirect(font, pointSize, ref __result);
    private static bool FacePrefix3(Font font, int pointSize, ref UnityEngine.TextCore.LowLevel.FontEngineError __result) => Redirect(font, pointSize, ref __result);

    /// <summary>Font files may have changed: reload at the next frame.</summary>
    // LabelFixes.Forget(): the author edits SkinCreator\Text\LabelFixes.json and presses Reload -- the file is read
    // again here, and every label it touched is adjusted afresh. That loop is the whole point of the file.
    internal static void Refresh() { _dirty = true; LabelFixes.Forget(); SoonRecheck(); }

    /// <summary>Every frame from the module; work only when files or the language changed.</summary>
    internal static void Update(float time)
    {
        if (time >= _nextLanguageCheck)
        {
            _nextLanguageCheck = time + 2f;
            if (TextHooks.CurrentFolder != _activeLanguage) _dirty = true;
        }
        if (NotInstalled.Count > 0 && time >= _nextInstallCheck)
        {
            // The player may install the font while the game runs: use it as soon as Windows lists it.
            _nextInstallCheck = time + 3f;
            try
            {
                var installed = new HashSet<string>(Font.GetOSInstalledFontNames() ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                foreach (var pair in NotInstalled.ToList())
                    if (installed.Contains(pair.Value.Family) && Retried.Add(pair.Value.File + "|" + pair.Value.Family)) { Loaded.Remove(pair.Key); _dirty = true; }
            }
            catch { }
        }
        // 2026-11-16: FindObjectsOfType over ~3000 texts every 3 s was a periodic hitch. The font setters catch most
        // changes; the scan now runs 0.5 / 2 / 5 s after a scene load or font change, else every 20 s.
        if (_scheduleFromNow) { _scheduleFromNow = false; _nextRecheck = time + 0.5f; }
        // The sweep used to run only while a replacement font was loaded. It carries two things now that have
        // nothing to do with the font -- the author's LabelFixes and the name labels the game masks -- and the
        // player asked for the second one to hold "无论什么字体", a font of their own or none (2026-11-21).
        if (time >= _nextRecheck)
        {
            _nextRecheck = time + (_burst.Count > 0 ? Math.Max(0.1f, _burst.Dequeue()) : 20f);
            try { Recheck(); } catch (Exception exception) { if (_recheckLogs++ < 3) MelonLogger.Warning($"{Prefix} re-check failed: {exception.GetBaseException().Message}"); }
        }
        if (!_dirty) return;
        _dirty = false;
        try
        {
            LoadFonts();
            _activeLanguage = TextHooks.CurrentFolder;
            (Font? font, TMP_FontAsset? tmp) = _activeLanguage != null && Loaded.TryGetValue(_activeLanguage, out var entry) ? (entry.Font, entry.Tmp) : (null, null);
            if (font == _active && tmp == _activeTmp) return;
            _active = font;
            _activeTmp = tmp;
            Sweep();
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"{Prefix} font update failed: {exception.GetBaseException().Message}");
        }
    }

    private static void LoadFonts()
    {
        foreach (string language in TextStore.Languages)
        {
            string directory = TextStore.FontsDir(SkinForgerPaths.Text, language);
            string? file = Directory.Exists(directory)
                ? Directory.GetFiles(directory).Where(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
                : null;
            string stamp = file == null ? string.Empty : file + "|" + new FileInfo(file).LastWriteTimeUtc.Ticks;
            if (Loaded.TryGetValue(language, out var old) && old.File == stamp) continue;
            _problems.Remove(language);
            NotInstalled.Remove(language);
            if (file == null) { Loaded.Remove(language); continue; }
            Font? font = null;
            TMP_FontAsset? tmp = null;
            try
            {
                char sample = language == "CN" ? '你' : 'A';
                // Unity 2019.4: Internal_CreateFontFromPath binds a NEW native font to a managed Font that has none
                // yet. "new Font()" already created a native font (Internal_CreateFont), so 1.9.0 loaded nothing.
                // Way 1: raw Il2Cpp Font object (constructor not run) + path load. Way 2 (old): new Font() + path.
                string way = "raw object";
                font = LoadFromPath(file, raw: true);
                if (!CanDraw(font, sample)) { way = "new Font()"; font = LoadFromPath(file, raw: false); }
                if (!CanDraw(font, sample))
                {
                    // Unity 2019.4 players cannot load a font FILE (1.9.1 log: fontNames=[], dynamic=False either way).
                    // They can use a font installed in Windows by its family name, read from the file.
                    // 2026-11-15 [实测]: a font installed "for me only" (HKCU, %LOCALAPPDATA%\Microsoft\Windows\Fonts) is
                    // NOT seen by Unity 2019.4, even after a restart; only fonts installed for all users are.
                    string? family = FontFile.FamilyName(File.ReadAllBytes(file));
                    if (family == null) throw new InvalidDataException("not a font file Unity can read");
                    string[] osFonts = Font.GetOSInstalledFontNames() ?? Array.Empty<string>();
                    bool listed = osFonts.Any(n => string.Equals(n, family, StringComparison.OrdinalIgnoreCase));
                    way = "installed Windows font \"" + family + "\"";
                    font = Font.CreateDynamicFontFromOSFont(family, 32);   // also tried when not listed: costs nothing
                    if (!CanDraw(font, sample))
                    {
                        bool userOnly = InstalledForUserOnly(family);
                        NotInstalled[language] = (file, family, userOnly);
                        MelonLogger.Msg($"{Prefix} {language}: \"{family}\" listed by Unity={listed}, installed for this Windows user only={userOnly}, Unity sees {osFonts.Length} fonts");
                        throw new InvalidDataException(userOnly
                            ? $"\"{family}\" is installed only for your Windows account; Unity only sees fonts installed for all users"
                            : $"\"{family}\" is not installed in Windows");
                    }
                }
                font!.name = Path.GetFileNameWithoutExtension(file);
                font.hideFlags = HideFlags.HideAndDontSave;
                MelonLogger.Msg($"{Prefix} {language}: font loaded via {way}, fontNames=[{string.Join(", ", font.fontNames ?? Array.Empty<string>())}]");
            }
            catch (Exception exception)
            {
                _problems[language] = exception.GetBaseException().Message;
                MelonLogger.Warning($"{Prefix} {language}: {Path.GetFileName(file)} not usable: {exception.GetBaseException().Message}");
                font = null;
            }
            if (font != null)
            {
                FontFiles[font.Pointer] = file;
                try
                {
                    tmp = TMP_FontAsset.CreateFontAsset(font, 64, 6, UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA, 2048, 2048, AtlasPopulationMode.Dynamic, true);
                    if (tmp != null) { tmp.hideFlags = HideFlags.HideAndDontSave; tmp.name = font.name + " (SkinForger TMP)"; }
                    else MelonLogger.Warning($"{Prefix} {language}: TextMeshPro font not created; TextMeshPro texts keep the game font");
                }
                catch (Exception exception) { MelonLogger.Warning($"{Prefix} {language}: TextMeshPro font not created ({exception.GetBaseException().Message}); TextMeshPro texts keep the game font"); }
                MelonLogger.Msg($"{Prefix} {language}: {Path.GetFileName(file)} loaded{(tmp != null ? " (+ TextMeshPro)" : "")}");
            }
            Loaded[language] = (stamp, font, tmp);
        }
    }

    private static Font? LoadFromPath(string file, bool raw)
    {
        try
        {
            Font font;
            if (raw)
            {
                IntPtr instance = Il2CppInterop.Runtime.IL2CPP.il2cpp_object_new(Il2CppInterop.Runtime.Il2CppClassPointerStore<Font>.NativeClassPtr);
                font = new Font(instance);
            }
            else font = new Font();
            Font.Internal_CreateFontFromPath(font, file);
            return font;
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"{Prefix} font load ({(raw ? "raw object" : "new Font()")}) failed: {exception.GetBaseException().Message}");
            return null;
        }
    }

    /// <summary>A dynamic font reports no characters until they are requested, so request one and read it back.</summary>
    private static bool CanDraw(Font? font, char c)
    {
        try
        {
            if (font == null || font.Pointer == IntPtr.Zero) return false;
            font.RequestCharactersInTexture(c.ToString(), 32, FontStyle.Normal);
            return font.GetCharacterInfo(c, out CharacterInfo glyph, 32, FontStyle.Normal) && glyph.advance > 0;
        }
        catch { return false; }
    }

    /// <summary>Applies the active font (or the game's own fonts) to every text loaded right now.</summary>
    private static void Sweep()
    {
        int texts = 0, tmps = 0;
        foreach (Text text in Resources.FindObjectsOfTypeAll<Text>()) { Apply(text); texts++; }
        foreach (TextMeshProUGUI text in Resources.FindObjectsOfTypeAll<TextMeshProUGUI>()) { ApplyTmp(text); tmps++; }
        MelonLogger.Msg($"{Prefix} language={_activeLanguage ?? "other"} font={(_active != null ? _active.name : "game default")}: {texts} texts, {tmps} TextMeshPro texts updated" +
                        $" (TextMeshPro font {(_activeTmp != null ? "ready" : "none")}; {_shrunk} labels made smaller to keep the width and the height the game gave them; " +
                        "kept the game font for outline shaders: " +
                        (SkippedShaders.Count == 0 ? "none" : string.Join(", ", SkippedShaders.Select(p => $"{p.Key}={p.Value}"))) + ")");
    }

    private static readonly Dictionary<IntPtr, int> OriginalSizes = new();
    private static readonly Dictionary<string, int> SkippedShaders = new(StringComparer.Ordinal);
    private static int _shrunk;

    private static void Apply(Text? text)
    {
        if (text == null || text.Pointer == IntPtr.Zero) return;
        Unmask(text);   // a name label is drawn in full from the moment it appears, not at the next sweep
        bool was = _applying;
        _applying = true;
        try { ApplyCore(text); }
        finally { _applying = was; }
    }

    private static void ApplyCore(Text text)
    {
        if (_active != null)
        {
            Font current = text.font;
            if (current != null && current.Pointer == _active.Pointer) { Refit(text); return; }
            if (OutlineShader(text) is { } shader)
            {
                // Outline drawn by the text's shader inside each glyph quad: with a new font the outline is cut at the
                // top and bottom (2026-11-15, result screen score). Such texts keep the game font.
                SkippedShaders[shader] = SkippedShaders.TryGetValue(shader, out int n) ? n + 1 : 1;
                return;
            }
            if (current != null && !OriginalFonts.ContainsKey(text.Pointer)) OriginalFonts[text.Pointer] = current;
            // Switching from one SkinForger font to another (language change): measure from the game font and size.
            if (OriginalFonts.TryGetValue(text.Pointer, out Font? gameFont) && gameFont != null && gameFont.Pointer != IntPtr.Zero &&
                current != null && current.Pointer != gameFont.Pointer)
            {
                text.font = gameFont;
                if (OriginalSizes.Remove(text.Pointer, out int gameSize)) text.fontSize = gameSize;
            }
            // Single-line labels keep the width they had with the game font (a wider font pushed "ENTER!" under its
            // icon, 2026-11-15): the font size is lowered by the width ratio.
            // A box of width 0 does not wrap either: "ENTER! " on the home screen is centred in a 0-wide box (2026-11-15).
            bool oneLine = text.horizontalOverflow == HorizontalWrapMode.Overflow || text.rectTransform.rect.width < 1f ||
                           WidthFollowsText(text.rectTransform);
            bool fit = oneLine && !text.resizeTextForBestFit && !string.IsNullOrEmpty(text.text);
            float before = fit ? text.preferredWidth : 0f;
            // And the same for HEIGHT (2026-11-21, the character select screen). See HeightRoom.
            float room = text.resizeTextForBestFit || string.IsNullOrEmpty(text.text) ? 0f
                : HeightRoom(text.rectTransform, text.verticalOverflow == VerticalWrapMode.Truncate);
            float beforeHeight = room > 0f ? text.preferredHeight : 0f;

            text.font = _active;

            float ratio = 1f;
            if (before > 1f)
            {
                float after = text.preferredWidth;
                if (after > before * 1.02f) ratio = Math.Min(ratio, before / after);
            }
            if (room > 0f && beforeHeight > 0f && beforeHeight <= room + 0.5f)
            {
                float afterHeight = text.preferredHeight;
                if (afterHeight > room)
                {
                    ratio = Math.Min(ratio, room / afterHeight);
                    ReportTooTall(text.transform, text.text, beforeHeight, afterHeight, room);
                }
            }
            if (ratio < 0.999f)
            {
                if (!OriginalSizes.ContainsKey(text.Pointer)) OriginalSizes[text.Pointer] = text.fontSize;
                text.fontSize = Math.Max(1, (int)Math.Floor(text.fontSize * ratio));
                _shrunk++;
            }
        }
        else
        {
            if (OriginalFonts.Remove(text.Pointer, out Font? original) && original != null && original.Pointer != IntPtr.Zero) text.font = original;
            if (OriginalSizes.Remove(text.Pointer, out int size)) text.fontSize = size;
        }
    }

    /// <summary>What a label was last measured with: its string and the height of its box.</summary>
    private static readonly Dictionary<IntPtr, (string Text, float Height, float Width)> Fitted = new();

    /// <summary>
    /// Measures a label that ALREADY has our font again, because its content or its box has changed since.
    ///
    /// Why (2026-11-21): the width and height rules only ran the moment a label was switched to our font -- and a
    /// label is switched once, usually while it is still empty or still being laid out. The character select card
    /// is the worst case of that: one label shows a different name for every character the player scrolls to, and
    /// after the first one it is already "our font", so the fit was never re-checked. That is why the skin name
    /// kept its cut bottom while the log said "0 labels made smaller".
    ///
    /// The baseline is always the GAME's font at the GAME's size, remembered when we first took the label over,
    /// so repeated passes cannot shrink it twice.
    /// </summary>
    private static void Refit(Text text)
    {
        try
        {
            if (text.resizeTextForBestFit || string.IsNullOrEmpty(text.text)) return;
            if (!OriginalFonts.TryGetValue(text.Pointer, out Font? gameFont) || gameFont == null || gameFont.Pointer == IntPtr.Zero) return;

            RectTransform rect = text.rectTransform;
            var now = (text.text, rect.rect.height, rect.rect.width);
            if (Fitted.TryGetValue(text.Pointer, out var last) && last.Text == now.Item1 &&
                Math.Abs(last.Height - now.Item2) < 0.5f && Math.Abs(last.Width - now.Item3) < 0.5f) return;
            Fitted[text.Pointer] = now;

            int baseSize = OriginalSizes.TryGetValue(text.Pointer, out int remembered) ? remembered : text.fontSize;
            float room = HeightRoom(rect, text.verticalOverflow == VerticalWrapMode.Truncate);
            bool oneLine = text.horizontalOverflow == HorizontalWrapMode.Overflow || rect.rect.width < 1f || WidthFollowsText(rect);

            text.font = gameFont;
            text.fontSize = baseSize;
            float beforeWidth = oneLine ? text.preferredWidth : 0f;
            float beforeHeight = room > 0f ? text.preferredHeight : 0f;

            text.font = _active;
            text.fontSize = baseSize;
            float ratio = 1f;
            if (beforeWidth > 1f)
            {
                float after = text.preferredWidth;
                if (after > beforeWidth * 1.02f) ratio = Math.Min(ratio, beforeWidth / after);
            }
            if (room > 0f && beforeHeight > 0f && beforeHeight <= room + 0.5f)
            {
                float afterHeight = text.preferredHeight;
                if (afterHeight > room)
                {
                    ratio = Math.Min(ratio, room / afterHeight);
                    ReportTooTall(text.transform, text.text, beforeHeight, afterHeight, room);
                }
            }
            if (ratio < 0.999f)
            {
                if (!OriginalSizes.ContainsKey(text.Pointer)) OriginalSizes[text.Pointer] = baseSize;
                text.fontSize = Math.Max(1, (int)Math.Floor(baseSize * ratio));
                _shrunk++;
            }
            else if (OriginalSizes.TryGetValue(text.Pointer, out int size)) text.fontSize = size;
        }
        catch (Exception) { }
    }

    /// <summary>The same for a TextMeshPro label; see <see cref="Refit(Text)"/>.</summary>
    private static void RefitTmp(TMP_Text text)
    {
        try
        {
            if (text.enableAutoSizing || string.IsNullOrEmpty(text.text)) return;
            if (!OriginalTmp.TryGetValue(text.Pointer, out TMP_FontAsset? gameFont) || gameFont == null || gameFont.Pointer == IntPtr.Zero) return;

            RectTransform rect = text.rectTransform;
            var now = (text.text, rect.rect.height, rect.rect.width);
            if (Fitted.TryGetValue(text.Pointer, out var last) && last.Text == now.Item1 &&
                Math.Abs(last.Height - now.Item2) < 0.5f && Math.Abs(last.Width - now.Item3) < 0.5f) return;
            Fitted[text.Pointer] = now;

            float baseSize = OriginalTmpSizes.TryGetValue(text.Pointer, out float remembered) ? remembered : text.fontSize;
            float room = HeightRoom(rect, text.overflowMode != TextOverflowModes.Overflow);
            bool oneLine = !text.enableWordWrapping || rect.rect.width < 1f || WidthFollowsText(rect);

            text.font = gameFont;
            text.fontSize = baseSize;
            float beforeWidth = oneLine ? text.GetPreferredValues(text.text).x : 0f;
            float beforeHeight = room > 0f ? text.GetPreferredValues(text.text).y : 0f;

            text.font = _activeTmp;
            text.fontSize = baseSize;
            float ratio = 1f;
            if (beforeWidth > 1f)
            {
                float after = text.GetPreferredValues(text.text).x;
                if (after > beforeWidth * 1.02f) ratio = Math.Min(ratio, beforeWidth / after);
            }
            if (room > 0f && beforeHeight > 0f && beforeHeight <= room + 0.5f)
            {
                float afterHeight = text.GetPreferredValues(text.text).y;
                if (afterHeight > room)
                {
                    ratio = Math.Min(ratio, room / afterHeight);
                    ReportTooTall(text.transform, text.text, beforeHeight, afterHeight, room);
                }
            }
            if (ratio < 0.999f)
            {
                if (!OriginalTmpSizes.ContainsKey(text.Pointer)) OriginalTmpSizes[text.Pointer] = baseSize;
                text.fontSize = Math.Max(1f, baseSize * ratio);
                _shrunk++;
            }
            else if (OriginalTmpSizes.TryGetValue(text.Pointer, out float size)) text.fontSize = size;
        }
        catch (Exception) { }
    }

    private static readonly HashSet<string> Unmasked = new(StringComparer.Ordinal);

    /// <summary>
    /// A character / elfin NAME is always drawn in full, whatever font it is in.
    ///
    /// The rule, decided 2026-11-21: EVERY character / elfin name ignores its name mask -- all of them, not
    /// just one; whether or not that label's font was replaced; and whatever font anyone replaces it with later.
    ///
    /// Muse Dash puts the name of a character / elfin inside a mask of its own -- on the elfin card the label's
    /// direct parent is literally called ImgRoleNameMask. With the game's own font the glyphs happen to end
    /// inside the masked shape; a different font draws them a little taller and the bottom is simply never
    /// drawn. Nothing is wrong with the text, its box or its size, which is why three builds that measured and
    /// moved the text changed nothing at all.
    ///
    /// uGUI has one switch for this: a Graphic with maskable = false is not registered with any mask above it
    /// and draws in full. So SkinForger turns it off for name labels -- as a rule of the mod, not as something
    /// each player has to discover and configure.
    ///
    /// Only a mask that is ABOUT the name is undone. The nearest mask above the label decides: if it is a
    /// scroll viewport or anything else, the label is left exactly as the game made it, because a text escaping
    /// a scrolling list is a worse bug than a cut name.
    /// </summary>
    private static void Unmask(MaskableGraphic? graphic)
    {
        try
        {
            if (graphic == null || graphic.Pointer == IntPtr.Zero || !graphic.maskable) return;
            Transform? mask = NameMaskAbove(graphic.transform);
            if (mask == null) return;
            graphic.maskable = false;
            string path = PathOf(graphic.transform);
            if (Unmasked.Add(path) && Unmasked.Count <= 20)
                MelonLogger.Msg($"{Prefix} name drawn in full (the game masks it with \"{mask.name}\"): {path}");
        }
        catch (Exception) { }
    }

    /// <summary>The nearest mask above a label, but only when that mask is the one the game puts around a NAME.</summary>
    private static Transform? NameMaskAbove(Transform label)
    {
        Transform? parent = label.parent;
        for (int depth = 0; depth < 6 && parent != null && parent.Pointer != IntPtr.Zero; depth++, parent = parent.parent)
        {
            if (parent.GetComponent<Mask>() == null && parent.GetComponent<RectMask2D>() == null) continue;
            string maskName = parent.name ?? string.Empty;
            // "ImgRoleNameMask", "ElfinNameMask", ... -- a mask whose whole purpose is that one label.
            if (maskName.IndexOf("name", StringComparison.OrdinalIgnoreCase) >= 0) return parent;
            // ... or a label that says it is a name, sitting directly inside a mask of its own.
            if (depth == 0 && (label.name ?? string.Empty).IndexOf("name", StringComparison.OrdinalIgnoreCase) >= 0) return parent;
            return null;   // the nearest mask is a scroll viewport or similar: that one has to keep clipping
        }
        return null;
    }

    private static readonly HashSet<string> TooTallSaid = new(StringComparer.Ordinal);

    /// <summary>Says, once per label, that the replacement font drew it taller than its box and was scaled back.</summary>
    private static void ReportTooTall(Transform? where, string? content, float before, float after, float room)
    {
        try
        {
            string path = PathOf(where);
            if (!TooTallSaid.Add(path) || TooTallSaid.Count > 24) return;
            MelonLogger.Msg($"{Prefix} \"{content}\" was taller than its box after the font change " +
                            $"({before:0.#} -> {after:0.#} in {room:0.#}), so it is drawn smaller instead of cut: {path}");
        }
        catch (Exception) { }
    }

    /// <summary>
    /// How much vertical room a label really has, in its own local units; 0 means "nothing is cutting it off".
    ///
    /// Why this exists (2026-11-21, the player's screenshot of the character select screen): the skin name under
    /// the character's picture had its bottom third sliced off after the font was replaced. The player read it as
    /// "something of Muse Dash's own is covering it". It is simpler than that -- the label has a FIXED box, and a
    /// replacement font draws the same string taller than the game's own font does, so the part that no longer fits
    /// is cut. A line is cut either by the label's own box (verticalOverflow = Truncate) or by a mask above it: this
    /// game puts long names inside a "DisplayArea" with a mask so they can scroll, and a mask cuts pixels wherever
    /// its rectangle ends, which is exactly what half a row of glyphs looks like.
    ///
    /// The width rule above already keeps a one-line label the width it had with the game font. This is that rule
    /// for the other axis, and it is the same promise: a replaced font may look different, it may not take more
    /// room than the game gave the text.
    /// </summary>
    /// <summary>
    /// True when the label's own box FOLLOWS its text on the horizontal axis (a ContentSizeFitter set to
    /// anything but Unconstrained), so the box can never force a wrap -- it just grows with the text.
    ///
    /// 2026-11-21, BUG-072 came back: the width rule only ran for a box that overflows or is 0 wide, which is
    /// what "ENTER! " looks like BEFORE its ContentSizeFitter has run. Once the fitter HAS run, the box is
    /// exactly as wide as the new, wider font needs (149 instead of 0), the rule was skipped, and the centred
    /// label grew left out of its Tag and under the key icon. Whether the rule runs must not depend on when we
    /// happen to measure: a fitted box is a one-line box at any width. That is also why the player saw the bug
    /// at start-up and not after a song -- a freshly built panel is measured before its fitter, an old one after.
    /// </summary>
    private static bool WidthFollowsText(RectTransform rect)
    {
        try
        {
            ContentSizeFitter? fitter = rect.GetComponent<ContentSizeFitter>();
            return fitter != null && fitter.Pointer != IntPtr.Zero &&
                   fitter.horizontalFit != ContentSizeFitter.FitMode.Unconstrained;
        }
        catch (Exception) { return false; }   // a label whose components cannot be read is left exactly as it was
    }

    private static float HeightRoom(RectTransform rect, bool ownBoxCuts)
    {
        try
        {
            float scale = Math.Abs(rect.lossyScale.y);
            if (scale < 0.0001f) scale = 1f;
            float room = ownBoxCuts && rect.rect.height > 0.5f ? rect.rect.height : float.MaxValue;

            Transform? parent = rect.parent;
            for (int depth = 0; depth < 8 && parent != null && parent.Pointer != IntPtr.Zero; depth++, parent = parent.parent)
            {
                if (parent.GetComponent<RectMask2D>() == null && parent.GetComponent<Mask>() == null) continue;
                RectTransform? box = parent.TryCast<RectTransform>();
                if (box == null) continue;
                float own = Math.Abs(box.lossyScale.y);
                float height = box.rect.height * (own < 0.0001f ? 1f : own) / scale;
                if (height > 0.5f && height < room) room = height;
            }
            return room == float.MaxValue ? 0f : room;
        }
        catch (Exception)
        {
            return 0f;   // a label whose parents cannot be read is left exactly as it was
        }
    }

    private static readonly string[] OutlineProperties = { "_OutlineColor", "_OutlineWidth", "_OutlineSize", "_Outline", "_OutlineThickness", "_StrokeColor", "_StrokeWidth" };

    /// <summary>Name of the text's own shader when it draws an outline (a material property named like one), else null.</summary>
    private static string? OutlineShader(Text text)
    {
        try
        {
            Material? material = text.material;
            if (material == null || material.Pointer == IntPtr.Zero || material.shader == null) return null;
            if (text.defaultMaterial != null && material.Pointer == text.defaultMaterial.Pointer) return null;
            foreach (string property in OutlineProperties)
                if (material.HasProperty(property)) return material.shader.name;
        }
        catch { }
        return null;
    }

    private static readonly Dictionary<IntPtr, float> OriginalTmpSizes = new();

    private static void ApplyTmp(TMP_Text? text)
    {
        if (text == null || text.Pointer == IntPtr.Zero) return;
        Unmask(text);
        bool was = _applying;
        _applying = true;
        try
        {
            if (_activeTmp != null)
            {
                TMP_FontAsset current = text.font;
                if (current != null && current.Pointer == _activeTmp.Pointer) { RefitTmp(text); return; }
                if (current != null && !OriginalTmp.ContainsKey(text.Pointer)) OriginalTmp[text.Pointer] = current;
                if (OriginalTmp.TryGetValue(text.Pointer, out TMP_FontAsset? gameFont) && gameFont != null && gameFont.Pointer != IntPtr.Zero &&
                    current != null && current.Pointer != gameFont.Pointer)
                {
                    text.font = gameFont;
                    if (OriginalTmpSizes.Remove(text.Pointer, out float gameSize)) text.fontSize = gameSize;
                }
                // Same width rule as UGUI labels (TextMeshPro labels did not get it in 8673EE1E).
                bool fit = (!text.enableWordWrapping || text.rectTransform.rect.width < 1f || WidthFollowsText(text.rectTransform)) &&
                           !text.enableAutoSizing && !string.IsNullOrEmpty(text.text);
                float before = fit ? text.GetPreferredValues(text.text).x : 0f;
                // And the same for HEIGHT (2026-11-21, the character select screen). See HeightRoom.
                float room = text.enableAutoSizing || string.IsNullOrEmpty(text.text) ? 0f
                    : HeightRoom(text.rectTransform, text.overflowMode != TextOverflowModes.Overflow);
                float beforeHeight = room > 0f ? text.GetPreferredValues(text.text).y : 0f;

                text.font = _activeTmp;

                float ratio = 1f;
                if (before > 1f)
                {
                    float after = text.GetPreferredValues(text.text).x;
                    if (after > before * 1.02f) ratio = Math.Min(ratio, before / after);
                }
                if (room > 0f && beforeHeight > 0f && beforeHeight <= room + 0.5f)
                {
                    float afterHeight = text.GetPreferredValues(text.text).y;
                    if (afterHeight > room)
                    {
                        ratio = Math.Min(ratio, room / afterHeight);
                        ReportTooTall(text.transform, text.text, beforeHeight, afterHeight, room);
                    }
                }
                if (ratio < 0.999f)
                {
                    if (!OriginalTmpSizes.ContainsKey(text.Pointer)) OriginalTmpSizes[text.Pointer] = text.fontSize;
                    text.fontSize = Math.Max(1f, text.fontSize * ratio);
                    _shrunk++;
                }
            }
            else
            {
                if (OriginalTmp.Remove(text.Pointer, out TMP_FontAsset? original) && original != null && original.Pointer != IntPtr.Zero) text.font = original;
                if (OriginalTmpSizes.Remove(text.Pointer, out float size)) text.fontSize = size;
            }
        }
        finally { _applying = was; }
    }

    // ------------------------------------------------------------ later font changes by the game

    private static bool _applying;
    private static float _nextRecheck;
    private static readonly Queue<float> _burst = new();

    /// <summary>A scene was loaded or the font changed: scan a few times soon.</summary>
    internal static void SoonRecheck()
    {
        _burst.Clear();
        foreach (float gap in new[] { 1.5f, 3f, 15f }) _burst.Enqueue(gap);
        _nextRecheck = 0f;
        _scheduleFromNow = true;
    }
    private static bool _scheduleFromNow;
    private static int _recheckLogs, _setLogs;
    private static readonly HashSet<string> Diagnosed = new(StringComparer.Ordinal);

    private static void TextFontSetPostfix(Text __instance, Font value)
    {
        if (_applying || _active == null || value == null || value.Pointer == _active.Pointer) return;
        try
        {
            // The game chose a new font for this text: that is its own font from now on.
            OriginalFonts[__instance.Pointer] = value;
            if (OriginalSizes.Remove(__instance.Pointer, out int size)) { _applying = true; try { __instance.fontSize = size; } finally { _applying = false; } }
            if (_setLogs++ < 10) MelonLogger.Msg($"{Prefix} game set font \"{value.name}\" on {PathOf(__instance.transform)}: replaced");
            Apply(__instance);
        }
        catch { }
    }

    private static void TmpFontSetPostfix(TMP_Text __instance, TMP_FontAsset value)
    {
        if (_applying || _activeTmp == null || value == null || value.Pointer == _activeTmp.Pointer) return;
        try
        {
            OriginalTmp[__instance.Pointer] = value;
            if (OriginalTmpSizes.Remove(__instance.Pointer, out float size)) { _applying = true; try { __instance.fontSize = size; } finally { _applying = false; } }
            if (_setLogs++ < 10) MelonLogger.Msg($"{Prefix} game set TextMeshPro font \"{value.name}\" on {PathOf(__instance.transform)}: replaced");
            ApplyTmp(__instance);
        }
        catch { }
    }

    /// <summary>
    /// Every 3 s: texts on screen that still use another font get ours (covers paths no hook sees), and the first
    /// ones are logged with their place in the scene. Texts whose text contains "ENTER" are described once (layout).
    /// </summary>
    private static void Recheck()
    {
        int fixedTexts = 0;
        foreach (Text text in UnityEngine.Object.FindObjectsOfType<Text>())
        {
            if (text == null || text.Pointer == IntPtr.Zero) continue;
            // The author's own adjustments (SkinCreator\Text\LabelFixes.json). Here rather than inside Apply,
            // because a label may need adjusting whether or not SkinForger replaced its font -- and because this
            // loop sees every label on screen, which is the only place that is true (2026-11-21).
            LabelFixes.Apply(text);
            Unmask(text);
            Describe(text, text.text, $"Text font={text.font?.name} size={text.fontSize} align={text.alignment} " +
                                      $"hOverflow={text.horizontalOverflow} vOverflow={text.verticalOverflow} bestFit={text.resizeTextForBestFit}" +
                                      $"{(text.resizeTextForBestFit ? $"[{text.resizeTextMinSize}..{text.resizeTextMaxSize}]" : string.Empty)} " +
                                      $"lineSpacing={text.lineSpacing} preferredWidth={text.preferredWidth:0.#} preferredHeight={text.preferredHeight:0.#} " +
                                      $"room={HeightRoom(text.rectTransform, text.verticalOverflow == VerticalWrapMode.Truncate):0.#}");
            if (_active == null || text.font == null || OutlineShader(text) != null) continue;
            // Already ours: Apply only re-measures it (Refit), which is the point -- this screen's labels change
            // their string as the player scrolls, and the fit has to follow the string (2026-11-21).
            if (text.font.Pointer == _active.Pointer) { Apply(text); continue; }
            string font = text.font.name;
            Apply(text);
            if (text.font != null && text.font.Pointer == _active.Pointer && fixedTexts++ < 3 && _recheckLogs++ < 20)
                MelonLogger.Msg($"{Prefix} re-check: {PathOf(text.transform)} (Text) had \"{font}\": replaced");
        }
        foreach (TMP_Text text in UnityEngine.Object.FindObjectsOfType<TMP_Text>())
        {
            if (text == null || text.Pointer == IntPtr.Zero) continue;
            LabelFixes.Apply(text);
            Unmask(text);
            Describe(text, text.text, $"{text.GetIl2CppType().Name} font={text.font?.name} size={text.fontSize} align={text.alignment} " +
                                      $"wrap={text.enableWordWrapping} autoSize={text.enableAutoSizing} overflow={text.overflowMode} " +
                                      $"margin={text.margin} preferredWidth={text.preferredWidth:0.#} preferredHeight={text.preferredHeight:0.#} " +
                                      $"room={HeightRoom(text.rectTransform, text.overflowMode != TextOverflowModes.Overflow):0.#}");
            if (_activeTmp == null || text.font == null) continue;
            if (text.font.Pointer == _activeTmp.Pointer) { ApplyTmp(text); continue; }   // already ours: re-measure only
            string font = text.font.name;
            ApplyTmp(text);
            if (text.font != null && text.font.Pointer == _activeTmp.Pointer && fixedTexts++ < 6 && _recheckLogs++ < 20)
                MelonLogger.Msg($"{Prefix} re-check: {PathOf(text.transform)} ({text.GetIl2CppType().Name}) had \"{font}\": replaced");
        }
    }

    /// <summary>
    /// One-time layout description of a label worth watching.
    ///
    /// 2026-11-15: the "ENTER!" label, which a wider font pushed under its icon.
    /// 2026-11-21: every label of the character / elfin select screens (PnlRole, PnlElfin). The player's skin name
    /// there loses its bottom third after the font is replaced, and the first guess -- "a fixed box or a mask is
    /// cutting it, so draw it smaller" -- changed nothing: the height rule did not fire once, so whatever cuts that
    /// label is NOT the label's own box and NOT a mask within eight parents of it. Guessing again would cost
    /// another launch; this prints what the label and every one of its parents actually are, which settles it.
    /// The ancestors carry their components, so a mask, a layout group, a fitter or an Image drawn after the text
    /// (an overlay) all show up by name, with the numbers next to them.
    /// </summary>
    private static void Describe(Component text, string? content, string details)
    {
        if (string.IsNullOrEmpty(content)) return;   // 2026-11-21: an empty label said nothing useful and used up its one turn
        string path = PathOf(text.transform);
        bool watched = content.IndexOf("ENTER", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       path.IndexOf("PnlRole", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       path.IndexOf("PnlElfin", StringComparison.OrdinalIgnoreCase) >= 0;
        if (!watched) return;
        if (!Diagnosed.Add(path + "|" + content + "|" + _activeLanguage) || Diagnosed.Count > 24) return;
        MelonLogger.Msg($"{Prefix} how \"{content.Replace("\n", " ")}\" is drawn: {Material(text)}");
        MelonLogger.Msg($"{Prefix} ancestors of \"{content.Replace("\n", " ")}\": {Ancestors(text.transform)}");
        RectTransform? rect = text.transform.TryCast<RectTransform>();
        string siblings = string.Empty;
        Transform? parent = text.transform.parent;
        if (parent != null)
        {
            var parts = new List<string>();
            for (int i = 0; i < parent.childCount && i < 8; i++)
            {
                Transform child = parent.GetChild(i);
                RectTransform? r = child.TryCast<RectTransform>();
                parts.Add($"{child.name}[{(r != null ? $"pos={r.anchoredPosition} size={r.rect.size} pivot={r.pivot}" : "")}]");
            }
            var layouts = new List<string>();
            foreach (Component c in parent.GetComponents<Component>()) if (c != null) layouts.Add(c.GetIl2CppType().Name);
            siblings = $" parent components=[{string.Join(", ", layouts)}] children={string.Join(" ", parts)}";
        }
        MelonLogger.Msg($"{Prefix} layout of \"{content.Replace("\n", " ")}\" ({_activeLanguage}): {path} {details} rect={(rect != null ? $"pos={rect.anchoredPosition} size={rect.rect.size} pivot={rect.pivot}" : "?")}{siblings}");
    }

    /// <summary>
    /// How a label is actually drawn: its material, the shader behind it, and the shader's own properties.
    ///
    /// Why (2026-11-21, third attempt at the character / elfin select screen): two rules that place the text
    /// inside its box changed nothing, which rules out the box and the masks. The remaining way to lose the
    /// bottom of a glyph is the glyph QUAD itself -- a text drawn by a special shader (an outline baked into each
    /// glyph's quad) has exactly this symptom, and FontHooks already keeps the game font for such labels. It
    /// recognises them by a list of property names (_OutlineColor, _OutlineWidth ...), and a shader whose
    /// property is called something else slips through. So: print the names, instead of guessing them.
    /// </summary>
    private static string Material(Component component)
    {
        try
        {
            Graphic? graphic = component.TryCast<Graphic>();
            if (graphic == null) return "not a Graphic";
            Material? material = graphic.material;
            Material? standard = graphic.defaultMaterial;
            if (material == null || material.Pointer == IntPtr.Zero) return "no material";
            bool isDefault = standard != null && material.Pointer == standard.Pointer;
            Shader? shader = material.shader;
            var found = new List<string>();
            if (shader != null && shader.Pointer != IntPtr.Zero)
            {
                int count = shader.GetPropertyCount();
                for (int i = 0; i < count && i < 40; i++) found.Add(shader.GetPropertyName(i));
            }
            return $"material=\"{material.name}\" default={isDefault} shader=\"{shader?.name}\" " +
                   $"properties=[{string.Join(",", found)}] recognisedAsOutline={(OutlineShaderName(graphic) ?? "no")}";
        }
        catch (Exception exception) { return "(" + exception.GetBaseException().Message + ")"; }
    }

    /// <summary>What <see cref="OutlineShader"/> would answer for this graphic, for the diagnostic above.</summary>
    private static string? OutlineShaderName(Graphic graphic)
    {
        Text? text = graphic.TryCast<Text>();
        return text != null ? OutlineShader(text) : null;
    }

    /// <summary>
    /// The label's parents, innermost first: each one's name, its box, its sibling index among its own parent's
    /// children (an Image with a HIGHER index is drawn ON TOP of this one), and the components it carries.
    /// A mask, a layout group, a size fitter or an overlay image is then a name in this line rather than a guess.
    /// </summary>
    private static string Ancestors(Transform? t)
    {
        var steps = new List<string>();
        try
        {
            for (int depth = 0; t != null && t.Pointer != IntPtr.Zero && depth < 10; depth++, t = t.parent)
            {
                RectTransform? rect = t.TryCast<RectTransform>();
                var parts = new List<string>();
                foreach (Component component in t.GetComponents<Component>())
                    if (component != null && component.Pointer != IntPtr.Zero) parts.Add(component.GetIl2CppType().Name);
                string size = rect != null ? $"size={rect.rect.size} pos={rect.anchoredPosition} pivot={rect.pivot} scale={rect.localScale.y:0.##}" : "no rect";
                steps.Add($"{t.name}[{size} order={t.GetSiblingIndex()} active={t.gameObject.activeInHierarchy} ({string.Join(",", parts)})" +
                          $"{OnTopOf(t)}]");
            }
        }
        catch (Exception exception) { steps.Add("(" + exception.GetBaseException().Message + ")"); }
        return string.Join(" <- ", steps);
    }

    /// <summary>
    /// The siblings of <paramref name="t"/> that are drawn AFTER it and have something to draw.
    ///
    /// uGUI draws siblings in order, so a later Image sits on top of everything in this branch. If the bottom of
    /// a label is missing and nothing in its parents can clip it, the only remaining explanation is one of these
    /// -- which is what the player said in the first place ("被盖住").
    /// </summary>
    private static string OnTopOf(Transform t)
    {
        try
        {
            Transform? parent = t.parent;
            if (parent == null || parent.Pointer == IntPtr.Zero) return string.Empty;
            int mine = t.GetSiblingIndex();
            var over = new List<string>();
            for (int i = mine + 1; i < parent.childCount && over.Count < 6; i++)
            {
                Transform other = parent.GetChild(i);
                if (other == null || other.Pointer == IntPtr.Zero || !other.gameObject.activeInHierarchy) continue;
                Graphic? graphic = other.GetComponent<Graphic>();
                if (graphic == null || graphic.Pointer == IntPtr.Zero) continue;
                RectTransform? r = other.TryCast<RectTransform>();
                over.Add($"{other.name}{(r != null ? $"@{r.anchoredPosition}{r.rect.size}" : string.Empty)}");
            }
            return over.Count == 0 ? string.Empty : " ON TOP: " + string.Join(" ", over);
        }
        catch (Exception) { return string.Empty; }
    }

    private static string PathOf(Transform? t)
    {
        var names = new List<string>();
        for (int i = 0; t != null && i < 8; i++, t = t.parent) names.Add(t.name);
        names.Reverse();
        return string.Join("/", names);
    }

    private static void TextPostfix(Text __instance)
    {
        if (_active == null && OriginalFonts.Count == 0) return;
        try { Apply(__instance); } catch { }
    }

    private static void LocalizationFontPostfix(Il2Cpp.MDLocalizationFont __instance)
    {
        if (_active == null && OriginalFonts.Count == 0) return;
        try
        {
            Text? text = __instance.m_TextComponent;
            if (text == null) return;
            // The game just set its language font: that is the component's own font from now on.
            if (_active != null && text.font != null && text.font.Pointer != _active.Pointer) OriginalFonts[text.Pointer] = text.font;
            Apply(text);
        }
        catch { }
    }

    private static void TmpPostfix(TextMeshProUGUI __instance)
    {
        if (_activeTmp == null && OriginalTmp.Count == 0) return;
        try { ApplyTmp(__instance); } catch { }
    }
}
