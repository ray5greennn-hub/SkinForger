using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace SkinForger.SkinPackages;

/// <summary>Folder layout (2026-11-14; Images / Text / CustomLodingPic inside SkinCreator since 2026-11-15, Main v4).</summary>
public static class SkinForgerPaths
{
    public static string Root => Path.Combine(MelonLoader.Utils.MelonEnvironment.GameRootDirectory, "SkinForger");
    /// <summary>Creator workspace root: settings (Saved.json, Menukey.txt ...), preview.png, Images, Text, CustomLodingPic.</summary>
    public static string CreatorRoot => Path.Combine(Root, "SkinCreator");
    /// <summary>Skin image folders (one per character / elfin): Dump output, image editing, Root skins. Main's skins root.</summary>
    public static string Creator => Path.Combine(CreatorRoot, "Images");
    /// <summary>Replacement texts (CN / EN ...) and fonts.</summary>
    public static string Text => Path.Combine(CreatorRoot, "Text");
    /// <summary>Custom loading pictures (Main's CustomLoadingPicManager).</summary>
    public static string LoadingPictures => Path.Combine(CreatorRoot, "CustomLodingPic");
    /// <summary>Player folder: .sfskin packages from other creators.</summary>
    public static string Player => Path.Combine(Root, "Skins");
    /// <summary>Where Export Skin Package writes .sfskin files.</summary>
    public static string Export => Path.Combine(Root, "ExportSkin");
}

/// <summary>
/// Page tabs under the SkinForger title bar ("Image" = the existing page, then "Export Skin Package"; new pages are
/// appended at the end) and the Export Skin Package page.
/// </summary>
public sealed class SkinPackagesMod : global::SkinForger.Core.SfModule
{
    private const string Prefix = "[SkinForge][SkinPackages]";
    private const string Generator = "SkinForger Core 1.3.0";

    // Page order: append new pages at the end.
    private static readonly string[] Tabs = { "Image", "Text", "Sound", "Export Skin Package" };   // user order (2026-11-15; Sound 2026-11-17)
    private const int ImageTab = 0;
    private const int TextTab = 1;
    private const int SoundTab = 2;
    private const int ExportTab = 3;
    private const int SettingsTab = 4;   // drawn apart, at the right end of the tab row (user, 2026-11-16)
    private static int _tab = ImageTab;

    // Export page state
    private static volatile List<SkinFolderStatus>? _folders;
    private static volatile bool _scanning;
    private static readonly HashSet<string> Selected = new(StringComparer.OrdinalIgnoreCase);
    private static string _packageName = string.Empty;
    private static string _author = string.Empty;
    private static string _description = string.Empty;
    /// <summary>Creator's sharing choice. Off (the default, every time the page opens) = official SkinForger on this
    /// platform only; the creator has to tick it themselves for anything else to be allowed to load the package.</summary>
    private static bool _allowUnofficial;
    /// <summary>
    /// Pack every image of the chosen folders, not only the ones the export believes were edited.
    ///
    /// Why it has to exist (2026-11-21): "what did the author change?" is a GUESS. It is a good guess --
    /// replaced\ history, the recorded content hash, a size that no longer matches the atlas, a customized
    /// sidecar, an all-transparent image -- but every one of those can be defeated by ordinary use (a
    /// cleaned replaced\ folder leaves nothing behind, and a Reload makes the current file the baseline).
    /// When the guess goes wrong the author does not find out: the package simply ships fewer parts, and
    /// the game's own art comes back underneath on a stranger's machine. This tick removes the guess.
    ///
    /// ON by default since 2026-11-21. It started as an escape hatch; one afternoon with a real skin
    /// showed the default was the wrong way round. Lilith's folder holds 21 parts, the author redrew or
    /// erased every one of them, and the detector could account for 20: the recorded content hash is
    /// written from the CURRENT file at every Reload, so for that folder rule 2 can never fire, and a part
    /// redrawn at its original size with no sidecar value is invisible to the other rules too. Packing it
    /// all cost about 100 KB. Being wrong cost a stranger seeing the game's own art.
    /// </summary>
    private static bool _packEverything = true;
    private static bool _framePreview;
    private static float _nextPreviewCheck;
    // The creator's notes (2026-11-21). Like preview.png: a file in SkinCreator, found by polling, never typed
    // into this window -- a readme is long, and an IMGUI text box is a bad place to write anything long.
    private static bool _frameReadme;
    private static int _frameReadmeChars;
    private static bool _includeReadme = true;
    // The card background (2026-11-22). Like preview.png and readme.txt: a file in SkinCreator, found by
    // polling. preview.png is the small square thumbnail; cover.png is the wide picture behind the whole card.
    private static bool _frameCover;
    private static bool _includeCover = true;
    private static string PreviewFile => Path.Combine(SkinForgerPaths.CreatorRoot, SfSkinFormat.PreviewEntry);
    private static string ReadmeFile => Path.Combine(SkinForgerPaths.CreatorRoot, SfSkinFormat.ReadmeEntry);
    private static string CoverFile => Path.Combine(SkinForgerPaths.CreatorRoot, SfSkinFormat.CoverEntry);
    private static Vector2 _pageScroll;
    private static volatile bool _exporting;
    private static int _progressDone, _progressTotal;
    private static volatile string _resultText = string.Empty;
    private static volatile bool _resultOk;
    private static GUIStyle? _bold, _title, _small;
    private static bool _failureLogged;

    // IMGUI draws every frame in several passes (Layout, then the input / Repaint pass) and both must see the same
    // controls. Background work and tab switches therefore take effect only at the next Layout pass (snapshot below).
    private static int _pendingTab = -1;
    private static bool _closeRequested;
    internal static void OpenTextTab() => _pendingTab = TextTab;

    /// <summary>The page on screen (or the one a key just asked for). Used by <see cref="Hotkeys"/>.</summary>
    internal static int CurrentTab => _pendingTab >= 0 ? _pendingTab : _tab;

    /// <summary>Asks for a page; it is switched at the next Layout pass, like a click on its tab.</summary>
    internal static void RequestTab(int tab) => _pendingTab = tab;
    private static List<SkinFolderStatus> _frameFolders = new();
    private static bool _frameScanning, _frameExporting, _frameResultOk;
    private static string _frameResult = string.Empty;
    private static int _frameDone, _frameTotal;

    private static void TakeSnapshot()
    {
        if (Event.current == null || Event.current.type != EventType.Layout) return;
        if (_pendingExtras != null)
        {
            Action apply = _pendingExtras;
            _pendingExtras = null;
            apply();
        }
        if (_closeRequested)
        {
            _closeRequested = false;
            global::SkinForger.MainBridge.MainHost.TrySetWindowShown(false);
        }
        if (_pendingTab >= 0)
        {
            _tab = _pendingTab;
            _pendingTab = -1;
            if (_tab == ExportTab) StartScan();
        }
        _frameScanning = _scanning;
        _frameExporting = _exporting;
        _frameFolders = _folders ?? new List<SkinFolderStatus>();
        _frameResult = _resultText ?? string.Empty;
        _frameResultOk = _resultOk;
        _frameDone = Volatile.Read(ref _progressDone);
        _frameTotal = Volatile.Read(ref _progressTotal);
        if (_tab == ExportTab && Time.realtimeSinceStartup >= _nextExtrasCheck)
        {
            _nextExtrasCheck = Time.realtimeSinceStartup + 2f;
            CountExtras();
        }
        if (_tab == ExportTab && Time.realtimeSinceStartup >= _nextPreviewCheck)
        {
            _nextPreviewCheck = Time.realtimeSinceStartup + 2f;
            try { _framePreview = File.Exists(PreviewFile); } catch { _framePreview = false; }
            try { _frameCover = File.Exists(CoverFile); } catch { _frameCover = false; }
            try
            {
                _frameReadme = File.Exists(ReadmeFile);
                _frameReadmeChars = _frameReadme ? SfSkinFormat.DecodeText(File.ReadAllBytes(ReadmeFile)).Trim().Length : 0;
                if (_frameReadmeChars == 0) _frameReadme = false;   // an empty file is no readme
            }
            catch { _frameReadme = false; _frameReadmeChars = 0; }
        }
    }

    public override void OnInitializeMelon()
    {
        Directory.CreateDirectory(SkinForgerPaths.Player);
        EditDetector.StateFile = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "SkinForger", "replaced-state-v1.json");
        EditDetector.IconOriginalsFile = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "SkinForger", "icon-originals.json");
        Type host = AccessTools.TypeByName("SkinForger.SkinForgerMod") ?? throw new TypeLoadException("SkinForger.SkinForgerMod");
        MethodInfo drawWindow = AccessTools.DeclaredMethod(host, "DrawWindow") ?? throw new MissingMethodException("DrawWindow");
        HarmonyInstance.Patch(drawWindow, prefix: new HarmonyMethod(typeof(SkinPackagesMod), nameof(DrawWindowPrefix)) { priority = HarmonyLib.Priority.First });
        // The Dump buttons for things that belong to no character are drawn after Main's page, at the
        // bottom of the Image page where dumping pictures happens.
        HarmonyInstance.Patch(drawWindow, postfix: new HarmonyMethod(typeof(SkinPackagesMod), nameof(DrawWindowPostfix)) { priority = HarmonyLib.Priority.Last });
        int loader = PackageLoaderHooks.Install(HarmonyInstance);
        int dumpIsolation = DumpIsolation.Install(HarmonyInstance);
        dumpIsolation += DumpKeepExisting.Install(HarmonyInstance);
        int roleSkins = RoleSkins.Install(HarmonyInstance);
        roleSkins += PreviewFrames.Install(HarmonyInstance);
        int loadCaches = LoadCaches.Install(HarmonyInstance);
        int autoAnchor = AutoAnchor.Install(HarmonyInstance);
        // 2026-11-18: INSTALLED BUT OFF. Correcting the placement here proved the diagnosis (the canvas really
        // is bigger than the drawing) but broke the picture: the atlas rect and UVs are decided elsewhere, so
        // shrinking only the placement squashed the art (the Maid's fringe). The growth has to be stopped
        // where the placement is derived, not patched after the fact.
        int canvasClamp = 0;
        int offsetCanvas = OffsetCanvas.Install(HarmonyInstance);
        int thumbnail = ThumbnailWithDump.Install(HarmonyInstance);
        int sounds = 0;
        try { sounds = SoundRuntime.Install(HarmonyInstance); }
        catch (Exception exception) { MelonLogger.Warning($"{Prefix} sound replacement not installed: {exception.GetBaseException().Message}"); }
        int editHistory = EditHistory.Install(HarmonyInstance);
        int loc = Loc.Install(HarmonyInstance);
        int reloadFilter = ReloadFilter.Install(HarmonyInstance);
        int rebase = UntouchedRegionRebase.Install(HarmonyInstance);
        int creatorWindow = CreatorWindowHooks.Install(HarmonyInstance);
        int theme = SfTheme.Install(HarmonyInstance);
        int profiler = LoadProfiler.Install(HarmonyInstance);
        int textHooks = TextHooks.Install(HarmonyInstance);
        int fontHooks = FontHooks.Install(HarmonyInstance);
        int typingGuard = global::SkinForger.SkinPackages.TypingGuard.Install(HarmonyInstance);
        int hotkeys = Hotkeys.Install();
        const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        Type? preview = AccessTools.TypeByName("SkinForger.Preview.PreviewManager");
        _previewOpen = preview?.GetField("_open", statics);
        _previewRect = preview?.GetField("_windowRect", statics);
        MelonLogger.Msg($"{Prefix} ready version=1.26.0 sounds={sounds} thumbnail={thumbnail}/2 offsetCanvas={offsetCanvas} reloadFilter={reloadFilter} roleSkins={roleSkins} loadCaches={loadCaches} autoAnchor={autoAnchor} canvasClamp={canvasClamp} editHistory={editHistory} loc={loc} textHooks={textHooks} fontHooks={fontHooks} typingGuard={typingGuard} skinLibrary=key0 hotkeys={hotkeys}/2 creatorWindowHooks={creatorWindow}/3 theme={theme} loadProfiler={profiler}/4 tabs=[{string.Join(", ", Tabs)}] loaderHooks={loader} dumpIsolation={dumpIsolation}/2 untouchedRebase={rebase} creator={SkinForgerPaths.Creator} player={SkinForgerPaths.Player} export={SkinForgerPaths.Export}");
    }

    public override void OnUpdate()
    {
        PackageLoaderHooks.Poll(Time.realtimeSinceStartup);
        SkinLibraryWindow.Update();
        float now = Time.realtimeSinceStartup;
        TextRuntime.Poll(now);
        SoundRuntime.Poll(now);
        SeenTextures.Poll(now);
        TextHooks.Update();
        FontHooks.Update(now);
        Hotkeys.Update();
        WheelScroll();
        UpdateTypingGuard();
    }

    // ------------------------------------------------------------ typing guard (2026-11-15)
    // While a SkinForger text field has keyboard focus (Text page, package name, search ...), keys must not reach the
    // game: Enter / arrows / Esc used to press buttons on the character screen. The game's Rewired keyboard is turned
    // off (and WindowInputShield skips the UI EventSystem) until focus leaves; clicking outside the SkinForger windows
    // or closing them releases the focus.

    internal static bool Typing => global::SkinForger.SkinPackages.TypingGuard.Active;

    private static bool OverAnyWindow(Vector2 mouse) =>
        SkinLibraryWindow.Covers(mouse) ||
        (CreatorWindowShown && global::SkinForger.MainBridge.MainHost.TryWindowRect(out Rect menu) && menu.Contains(mouse)) ||
        (_previewOpen?.GetValue(null) is true && _previewRect?.GetValue(null) is Rect preview && preview.Contains(mouse));

    private static void UpdateTypingGuard()
    {
        try
        {
            bool anyWindow = CreatorWindowShown || SkinLibraryWindow.Visible;
            bool focused = GUIUtility.keyboardControl != 0;
            if (focused)
            {
                var mouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
                bool clickedOutside = (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1)) && !OverAnyWindow(mouse);
                if (!anyWindow || clickedOutside) { GUIUtility.keyboardControl = 0; focused = false; }
            }
            global::SkinForger.SkinPackages.TypingGuard.Set(focused && anyWindow);
        }
        catch { }
    }

    // ------------------------------------------------------------ mouse wheel
    // IMGUI scroll views in this game do not receive wheel events (only the scrollbar works), so the wheel is read
    // from Input and applied to the scroll view of the window under the mouse.

    private static FieldInfo? _previewOpen, _previewRect;

    /// <summary>
    /// Whether Main's creator window is open. Read through MainBridge, which answers for the legacy Main
    /// (private _showMenu) and the authored one (WindowShown) alike.
    /// </summary>
    private static bool CreatorWindowShown =>
        global::SkinForger.MainBridge.MainHost.TryWindowShown(out bool shown) && shown;
    private const float WheelStep = 48f;

    private static void WheelScroll()
    {
        try
        {
            float wheel;
            global::SkinForger.WindowInputShield.WindowInputShieldMod.OwnWheelRead = true;   // the shield hides the wheel from the game only
            try { wheel = Input.mouseScrollDelta.y; }
            finally { global::SkinForger.WindowInputShield.WindowInputShieldMod.OwnWheelRead = false; }
            if (wheel == 0f) return;
            var mouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            if (SkinLibraryWindow.Scroll(mouse, -wheel * WheelStep)) return;
            // The Preview window scrolls itself (PreviewEnhancements); never scroll the page behind it.
            if (_previewOpen?.GetValue(null) is true && _previewRect?.GetValue(null) is Rect preview && preview.Contains(mouse)) return;
            if (!CreatorWindowShown || !global::SkinForger.MainBridge.MainHost.TryWindowRect(out Rect rect) || !rect.Contains(mouse)) return;
            if (_tab == ImageTab)
            {
                if (global::SkinForger.MainBridge.MainHost.TryWindowScroll(out Vector2 scroll))
                    global::SkinForger.MainBridge.MainHost.TrySetWindowScroll(new Vector2(scroll.x, Math.Max(0f, scroll.y - wheel * WheelStep)));
            }
            else if (_tab == TextTab)
            {
                TextPage.Scroll(-wheel * WheelStep);
            }
            else if (_tab == SoundTab)
            {
                SoundPage.Scroll(-wheel * WheelStep);
            }
            else
            {
                _pageScroll = new Vector2(_pageScroll.x, Math.Max(0f, _pageScroll.y - wheel * WheelStep));
            }
        }
        catch { }
    }

    /// <summary>Skin Library window (key 0, right side).</summary>
    public override void OnGUI() => SkinLibraryWindow.OnGUI();

    public override void OnSceneWasInitialized(int buildIndex, string sceneName) => FontHooks.SoonRecheck();

    // ------------------------------------------------------------------ title bar + tab band

    private static FieldInfo? _windowRect;
    private static Texture2D? _gray;

    private static Texture2D Solid(ref Texture2D? cache, Color color)
    {
        if (cache != null && cache.Pointer != IntPtr.Zero) return cache;
        cache = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
        cache.SetPixel(0, 0, color);
        cache.Apply();
        return cache;
    }

    /// <summary>Black title bar with "SkinForger", drawn over the skin's title area (window-local coordinates).</summary>
    private static void DrawTitleBar()
    {
        if (Event.current == null || Event.current.type != EventType.Repaint) return;
        float width = _windowRect?.GetValue(null) is Rect rect ? rect.width : 860f;
        float height = Math.Max(16f, GUI.skin.window.padding.top);
        var bar = new Rect(0f, 0f, width, height);
        GUI.Label(bar, "SkinForger", SfTheme.TitleBar);   // black, rounded top (window outline is black too)
    }

    // ------------------------------------------------------------------ window

    private static void DrawWindowPostfix()
    {
        Loc.Enter();
        try { if (_tab == ImageTab) CursorDump.DrawFooter(); }
        catch (Exception) { /* never take the window down */ }
        finally { Loc.Exit(); }
    }

    /// <summary>
    /// Core draws the tab row and the Text / Sound / Export pages from a PREFIX on Main's DrawWindow, and
    /// Loc opens its translation scope from a prefix on the same method. Two prefixes both asking for the
    /// first slot have no defined order, and when this one ran first every page Core draws came out in
    /// English while Main's own Image page was translated — exactly what the window looked like
    /// (2026-11-19, user: "点开 text 和 sounds 的页面还是有很多英文，export 页面也是全英文").
    ///
    /// So the scope is opened here as well. Entering it twice is harmless: it is a depth counter.
    /// </summary>
    private static bool DrawWindowPrefix()
    {
        Loc.Enter();
        try { return DrawWindowBody(); }
        finally { Loc.Exit(); }
    }

    private static bool DrawWindowBody()
    {
        try
        {
            TakeSnapshot();
            DrawTabs();
        }
        catch (Exception exception)
        {
            LogFailureOnce("tabs", exception);
        }
        if (_tab == ImageTab) return true;

        try
        {
            if (_tab == TextTab) TextPage.Draw(_windowRect?.GetValue(null) is Rect window ? window.height : 560f);
            else if (_tab == SoundTab) SoundPage.Draw(_windowRect?.GetValue(null) is Rect soundWindow ? soundWindow.height : 560f);
            else if (_tab == SettingsTab) DrawSettingsPage();
            else DrawExportPage();
        }
        catch (Exception exception)
        {
            GUILayout.Label("Export page error: " + exception.GetBaseException().Message, Array.Empty<GUILayoutOption>());
            LogFailureOnce("export-page", exception);
        }
        GUI.DragWindow(new Rect(0f, 0f, Screen.width, 24f));
        return false;
    }

    private static void DrawTabs()
    {
        EnsureStyles();
        DrawTitleBar();
        GUILayout.BeginHorizontal(_tabBand!, Array.Empty<GUILayoutOption>());
        Color background = GUI.backgroundColor, content = GUI.contentColor;
        for (int i = 0; i < Tabs.Length; i++)
        {
            bool current = i == _tab;
            GUI.backgroundColor = current ? new Color(0.55f, 0.9f, 0.6f) : background;
            GUI.contentColor = current ? Color.green : content;
            float width = Math.Max(90f, 16f + 8.5f * Tabs[i].Length);
            if (GUILayout.Button(Tabs[i], GUILayout.Width(width)) && !current)
                _pendingTab = i;
        }
        GUI.backgroundColor = background;
        GUI.contentColor = content;
        GUILayout.FlexibleSpace();
        bool settings = _tab == SettingsTab;
        GUI.backgroundColor = settings ? new Color(0.55f, 0.9f, 0.6f) : background;
        GUI.contentColor = settings ? Color.green : content;
        if (GUILayout.Button("Settings", GUILayout.Width(100f)) && !settings) _pendingTab = SettingsTab;
        GUI.backgroundColor = background;
        GUI.contentColor = content;
        // Close (2026-11-18, user): this window can be put away while Preview stays open, so Preview is not
        // hidden behind it. Closing by the Image key would be the same key that opens Preview's page.
        GUI.backgroundColor = new Color(0.85f, 0.45f, 0.45f);
        if (GUILayout.Button("Close", GUILayout.Width(70f))) _closeRequested = true;
        GUI.backgroundColor = background;
        GUILayout.EndHorizontal();
        GUILayout.Space(4f);

        // The cursor belongs to no character, so its button sits with the page controls, above the lists.
        // Drawn here because this method already runs on every pass of every frame: anything drawn
        // conditionally per FRAME (rather than per tab) desynchronises IMGUI's Layout and Repaint passes
        // and makes the window flicker (2026-11-18).
    }

    // ------------------------------------------------------------------ settings page (2026-11-16)

    private static void DrawSettingsPage() => SettingsPage.Draw();


    private static GUIStyle? _tabBand, _titleBar;

    private static void EnsureStyles()
    {
        if (_bold != null && _tabBand?.normal.background != null && _tabBand.normal.background.Pointer != IntPtr.Zero) return;
        _bold = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
        _title = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 16 };
        _small = new GUIStyle(GUI.skin.label) { fontSize = 11 };
        _titleBar = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
        _titleBar.normal.textColor = Color.white;
        // Gray band behind the page tabs: separates them from the page's own buttons (Preview, Reload ...).
        _tabBand = new GUIStyle(GUI.skin.box)
        {
            margin = new RectOffset(0, 0, 0, 6),
            padding = new RectOffset(6, 6, 4, 4)
        };
        _tabBand.normal.background = Solid(ref _gray, new Color(0.25f, 0.24f, 0.29f, 1f));
    }

    // ------------------------------------------------------------------ export page

    private static void DrawExportPage()
    {
        EnsureStyles();
        Color content = GUI.contentColor;
        _pageScroll = GUILayout.BeginScrollView(_pageScroll, Array.Empty<GUILayoutOption>());

        GUILayout.Label("Export Skin Package", _title!, Array.Empty<GUILayoutOption>());
        GUILayout.Label("Choose what to include: characters, elfins, texts and custom loading pictures.", Array.Empty<GUILayoutOption>());
        GUILayout.Label("Only the images you changed are packed. Every part you did not change shows each player's own game art.", _small!, Array.Empty<GUILayoutOption>());
        GUILayout.Space(6f);

        // ---- 2026-11-17 (user): four groups - Characters, Elfins, Text, Custom Loading Pictures. Like the characters,
        // each packs only what the creator changed; a group / item with nothing changed cannot be selected.
        List<SkinFolderStatus> folders = _frameFolders;
        List<SkinFolderStatus> exportable = folders.Where(f => f.Exportable).ToList();
        GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
        GUILayout.Label("SkinCreator/Images:  " + SkinForgerPaths.Creator, _small!, Array.Empty<GUILayoutOption>());
        GUILayout.FlexibleSpace();
        GUI.enabled = !_frameScanning && !_frameExporting;
        if (GUILayout.Button("Refresh", GUILayout.Width(90f))) StartScan();
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        if (_frameScanning)
        {
            GUILayout.BeginVertical(SfTheme.CardStyle, Array.Empty<GUILayoutOption>());
            GUILayout.Label("Checking skin folders...", Array.Empty<GUILayoutOption>());
            GUILayout.EndVertical();
        }
        else
        {
            DrawFolderGroup("Characters", folders.Where(f => !IsElfinFolder(f.Path)).ToList(), content);
            DrawFolderGroup("Elfins", folders.Where(f => IsElfinFolder(f.Path)).ToList(), content);
        }
        Selected.RemoveWhere(name => !exportable.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) && !_frameScanning);
        GUILayout.Space(2f);

        // ---- Text (edited texts only) + fonts
        GUILayout.Label("Text", _bold!, Array.Empty<GUILayoutOption>());
        GUILayout.BeginVertical(SfTheme.CardStyle, Array.Empty<GUILayoutOption>());
        ExtraRow(ref _includeText, "Edited texts", _extraTexts > 0, _extraTexts > 0 ? $"{_extraTexts} edited texts ({_extraTextDetail})" : "No edited texts yet (Text page)");
        if (_includeText) PerName(_extraTextByOwner, TextOwners, "texts");
        ExtraRow(ref _includeFonts, "Fonts", _extraFonts.Count > 0, _extraFonts.Count > 0 ? string.Join("   ", _extraFonts.Select(f => f.Language + ": " + Path.GetFileName(f.File))) : "No font files in SkinCreator/Text/Fonts");
        GUILayout.EndVertical();
        GUILayout.Space(2f);

        // ---- Sounds (replaced voice lines / BGM + mute lists), 2026-11-17
        GUILayout.Label("Sounds", _bold!, Array.Empty<GUILayoutOption>());
        GUILayout.BeginVertical(SfTheme.CardStyle, Array.Empty<GUILayoutOption>());
        ExtraRow(ref _includeSounds, "Voices & BGM", _extraSounds.Count > 0, _extraSounds.Count > 0
            ? $"{_extraSounds.Count(f => !f.File.EndsWith("muted.txt", StringComparison.OrdinalIgnoreCase))} replaced sounds, {_extraSounds.Count(f => f.File.EndsWith("muted.txt", StringComparison.OrdinalIgnoreCase))} mute lists ({string.Join(", ", _extraSounds.Select(f => f.Costume).Distinct())})"
            : "No replaced or muted sounds yet (Sound page)");
        if (_includeSounds)
            PerName(_extraSounds.GroupBy(f => f.Costume, StringComparer.OrdinalIgnoreCase)
                                .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
                                .Select(g => (g.Key, g.Count())).ToList(), SoundCostumes, "files");
        GUILayout.EndVertical();
        GUILayout.Space(2f);

        // ---- Custom loading pictures (the ones config.json uses)
        GUILayout.Label("Custom Loading Pictures", _bold!, Array.Empty<GUILayoutOption>());
        GUILayout.BeginVertical(SfTheme.CardStyle, Array.Empty<GUILayoutOption>());
        int pictures = _extraLoading.Count(IsPicture);
        ExtraRow(ref _includeLoading, "Loading pictures", pictures > 0, pictures > 0 ? $"{pictures} pictures used by SkinCreator/CustomLodingPic/config.json" : "No custom loading pictures in SkinCreator/CustomLodingPic");
        GUILayout.EndVertical();
        GUILayout.Space(2f);

        // ---- Pictures that belong to no costume: matched by the texture's own name, not by an atlas,
        // so they travel as their own section of the package (2026-11-20).
        GUILayout.Label("Other Pictures", _bold!, Array.Empty<GUILayoutOption>());
        GUILayout.BeginVertical(SfTheme.CardStyle, Array.Empty<GUILayoutOption>());
        int others = _extraOther.Count(IsPicture);
        ExtraRow(ref _includeOther, "Other pictures", others > 0, others > 0
            ? $"{others} pictures in SkinCreator/Images/Other, matched by texture name"
            : "No pictures in SkinCreator/Images/Other (dump them from the Image page, Pictures on screen)");
        GUILayout.EndVertical();
        GUILayout.Space(6f);

        // ---- Package Name
        GUILayout.Label("Package Name", _bold!, Array.Empty<GUILayoutOption>());
        GUI.enabled = !_frameExporting;
        _packageName = GUILayout.TextField(_packageName ?? string.Empty, 80, Array.Empty<GUILayoutOption>()) ?? string.Empty;
        GUI.enabled = true;
        string? nameProblem = SfSkinFormat.ValidateName(_packageName);
        GUI.contentColor = nameProblem == null ? new Color(0.75f, 0.75f, 0.75f) : new Color(1f, 0.55f, 0.45f);
        GUILayout.Label(nameProblem ?? "Will be saved as  " + _packageName.Trim() + SfSkinFormat.Extension +
                        "  (a number is added if that name already exists)", _small!, Array.Empty<GUILayoutOption>());
        GUI.contentColor = content;
        GUILayout.Space(4f);

        // ---- Author / Description (optional) + preview picture
        GUI.enabled = !_frameExporting;
        GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
        GUILayout.Label("Author", _bold!, GUILayout.Width(90f));
        GUILayout.Label("(optional, leave empty to stay anonymous)", _small!, Array.Empty<GUILayoutOption>());
        GUILayout.EndHorizontal();
        _author = GUILayout.TextField(_author ?? string.Empty, SfSkinFormat.MaxAuthorLength, Array.Empty<GUILayoutOption>()) ?? string.Empty;
        GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
        GUILayout.Label("Description", _bold!, GUILayout.Width(90f));
        GUILayout.Label("(optional, shown in players' Skin Library)", _small!, Array.Empty<GUILayoutOption>());
        GUILayout.EndHorizontal();
        _description = GUILayout.TextArea(_description ?? string.Empty, SfSkinFormat.MaxDescriptionLength, GUILayout.Height(44f)) ?? string.Empty;
        GUI.enabled = true;
        GUI.contentColor = new Color(0.75f, 0.75f, 0.75f);
        GUILayout.Label(_framePreview
            ? "Preview picture:  SkinCreator/preview.png will be included"
            : "Preview picture (optional):  put a picture named preview.png in SkinForger/SkinCreator", _small!, Array.Empty<GUILayoutOption>());
        GUI.contentColor = content;
        GUILayout.Space(6f);

        // ---- Readme (2026-11-21): the creator's own notes, written in a real text editor and picked up from
        // SkinCreator/readme.txt exactly like preview.png. Players get a README button on the library card, and
        // the notes open by themselves the first time they turn the skin on.
        GUILayout.Label("Readme", _bold!, Array.Empty<GUILayoutOption>());
        GUILayout.BeginVertical(SfTheme.CardStyle, Array.Empty<GUILayoutOption>());
        GUI.enabled = !_frameExporting && _frameReadme;
        _includeReadme = GUILayout.Toggle(_includeReadme && _frameReadme, "  Include my readme", Array.Empty<GUILayoutOption>());
        GUI.enabled = true;
        GUI.contentColor = new Color(0.75f, 0.75f, 0.75f);
        GUILayout.Label(_frameReadme
            ? $"SkinCreator/readme.txt will travel with the package ({_frameReadmeChars} characters)." +
              "\nPlayers see a README button on your skin, and it opens by itself the first time they turn the skin on."
            : "Optional:  put a text file named readme.txt in SkinForger/SkinCreator and write whatever players" +
              "\nshould know. Any editor, any encoding; at most " + SfSkinFormat.MaxReadmeLength + " characters travel.",
            _small!, Array.Empty<GUILayoutOption>());
        GUI.contentColor = content;
        GUILayout.EndVertical();
        GUILayout.Space(6f);

        // ---- Cover (2026-11-22): the wide picture players see behind this skin's card in their Skin Library.
        GUILayout.Label("Cover", _bold!, Array.Empty<GUILayoutOption>());
        GUILayout.BeginVertical(SfTheme.CardStyle, Array.Empty<GUILayoutOption>());
        GUI.enabled = !_frameExporting && _frameCover;
        _includeCover = GUILayout.Toggle(_includeCover && _frameCover, "  Include my cover", Array.Empty<GUILayoutOption>());
        GUI.enabled = true;
        GUI.contentColor = new Color(0.75f, 0.75f, 0.75f);
        GUILayout.Label(_frameCover
            ? "SkinCreator/cover.png will travel with the package." +
              "\nIt is drawn behind your skin in the player's Skin Library, cropped to fill the card, darkened from the left so the name stays readable."
            : "Optional:  put a picture named cover.png in SkinForger/SkinCreator." +
              "\nAny shape works - it is cropped to fill the card. A wide picture shows most of itself.",
            _small!, Array.Empty<GUILayoutOption>());
        GUI.contentColor = content;
        GUILayout.EndVertical();
        GUILayout.Space(6f);

        // ---- Sharing (the creator's own permission; the skin images are theirs, not SkinForger's)
        GUILayout.Label("Sharing", _bold!, Array.Empty<GUILayoutOption>());
        GUILayout.BeginVertical(SfTheme.CardStyle, Array.Empty<GUILayoutOption>());
        GUI.enabled = !_frameExporting;
        _allowUnofficial = GUILayout.Toggle(_allowUnofficial,
            "  Allow unofficial builds to load this skin package", Array.Empty<GUILayoutOption>());
        GUI.enabled = true;
        GUI.contentColor = new Color(0.75f, 0.75f, 0.75f);
        GUILayout.Label(_allowUnofficial
            ? "Allowed:  any build, including ports to other platforms, may load this package.\n" +
              "Your name travels with it, but you will not be asked again."
            : "Not ticked (default):  official SkinForger on " + SfSkinFormat.OfficialGame + " only.\n" +
              "Ports and other unofficial builds are refused. Your work is yours — tick this only if you want it shared further.",
            _small!, Array.Empty<GUILayoutOption>());
        GUI.contentColor = content;
        GUILayout.EndVertical();
        GUILayout.Space(4f);

        // ---- What goes in: the guess, or the whole folder (2026-11-21)
        GUILayout.Label("What to pack", _bold!, Array.Empty<GUILayoutOption>());
        GUILayout.BeginVertical(SfTheme.CardStyle, Array.Empty<GUILayoutOption>());
        GUI.enabled = !_frameExporting;
        _packEverything = GUILayout.Toggle(_packEverything,
            "  Pack every image, not only the ones I changed", Array.Empty<GUILayoutOption>());
        GUI.enabled = true;
        GUI.contentColor = new Color(0.75f, 0.75f, 0.75f);
        GUILayout.Label(_packEverything
            ? "Ticked (default):  every PNG and sidecar in the chosen folders travels, the selection\n" +
              "thumbnail included. Bigger, and certain — nothing can be left behind by mistake."
            : "Only the images SkinForger believes you changed. Smaller, but it is a GUESS: a part you\n" +
              "redrew at its original size can look untouched, and then it is missing for everyone else.",
            _small!, Array.Empty<GUILayoutOption>());
        GUI.contentColor = content;
        GUILayout.EndVertical();
        GUILayout.Space(4f);
        GUILayout.Label("Output location:  SkinForger/ExportSkin/", _small!, Array.Empty<GUILayoutOption>());
        GUILayout.Space(4f);

        // ---- Export
        int count = exportable.Count(f => Selected.Contains(f.Name));
        bool extras = ExtrasSelected;
        GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
        GUI.enabled = nameProblem == null && (count > 0 || extras) && !_frameExporting && !_frameScanning;
        string label = _frameExporting ? "Exporting..." : count > 0 || extras ? $"Export Skin Package  ({count} skin{(count == 1 ? "" : "s")}{(extras ? " + extras" : "")})" : "Export Skin Package";
        if (GUILayout.Button(label, GUILayout.Height(30f))) StartExport(exportable.Where(f => Selected.Contains(f.Name)).ToList());
        GUI.enabled = true;
        if (GUILayout.Button("Open ExportSkin Folder", GUILayout.Width(190f), GUILayout.Height(30f))) OpenExportFolder();
        GUILayout.EndHorizontal();

        if (_frameExporting)
        {
            GUILayout.Label($"Packing {_frameDone} / {Math.Max(1, _frameTotal)} files...", Array.Empty<GUILayoutOption>());
        }
        else if (!string.IsNullOrEmpty(_frameResult))
        {
            GUI.contentColor = _frameResultOk ? Color.green : new Color(1f, 0.55f, 0.45f);
            GUILayout.Label(_frameResult, Array.Empty<GUILayoutOption>());
            GUI.contentColor = content;
        }
        else if (count == 0 && !extras)
        {
            GUILayout.Label("Select at least one Ready character or elfin, edited texts, fonts or loading pictures.", _small!, Array.Empty<GUILayoutOption>());
        }

        GUILayout.EndScrollView();
    }

    /// <summary>One group of skin folders (Characters / Elfins): only Ready folders (with edited images) can be ticked.</summary>
    private static void DrawFolderGroup(string title, List<SkinFolderStatus> group, Color content)
    {
        List<SkinFolderStatus> ready = group.Where(f => f.Exportable).ToList();
        GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
        GUILayout.Label(title, _bold!, Array.Empty<GUILayoutOption>());
        GUILayout.FlexibleSpace();
        bool all = ready.Count > 0 && ready.All(f => Selected.Contains(f.Name));
        GUI.enabled = !_frameExporting && ready.Count > 0;
        if (GUILayout.Button(all ? "Clear All" : "Select All", GUILayout.Width(100f)))
        {
            if (all) foreach (SkinFolderStatus f in ready) Selected.Remove(f.Name);
            else foreach (SkinFolderStatus f in ready) Selected.Add(f.Name);
        }
        GUI.enabled = true;
        GUILayout.EndHorizontal();
        GUILayout.BeginVertical(SfTheme.CardStyle, Array.Empty<GUILayoutOption>());
        if (group.Count == 0)
            GUILayout.Label(title == "Elfins" ? "No elfin dumped yet (Image page, Elfins list)." : "No character dumped yet (Image page, Characters list).", _small!, Array.Empty<GUILayoutOption>());
        foreach (SkinFolderStatus folder in group)
        {
            GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
            bool selected = Selected.Contains(folder.Name);
            GUI.enabled = folder.Exportable && !_frameExporting;
            bool now = GUILayout.Toggle(selected && folder.Exportable, string.Empty, GUILayout.Width(22f));
            GUI.enabled = true;
            if (folder.Exportable && now != selected)
            {
                if (now) Selected.Add(folder.Name); else Selected.Remove(folder.Name);
            }
            GUI.contentColor = StateColor(folder.State);
            GUILayout.Label(folder.StateText, _bold!, GUILayout.Width(110f));
            GUI.contentColor = content;
            GUILayout.Label(folder.Name, GUILayout.Width(200f));
            GUI.contentColor = new Color(0.75f, 0.75f, 0.75f);
            GUILayout.Label(folder.Detail, Array.Empty<GUILayoutOption>());
            GUI.contentColor = content;
            GUILayout.EndHorizontal();
        }
        GUILayout.EndVertical();
        GUILayout.Space(2f);
    }

    private static readonly Dictionary<string, bool> ElfinFolders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>An elfin skin folder holds the elfin's atlas folder ("elfin_devil" ...), a character's holds its own.</summary>
    private static bool IsElfinFolder(string path)
    {
        if (ElfinFolders.TryGetValue(path, out bool known)) return known;
        bool elfin = false;
        try { elfin = Directory.Exists(path) && Directory.GetDirectories(path).Any(d => Path.GetFileName(d).StartsWith("elfin_", StringComparison.OrdinalIgnoreCase)); }
        catch { }
        ElfinFolders[path] = elfin;
        return elfin;
    }

    private static Color StateColor(SkinFolderState state) => state switch
    {
        SkinFolderState.Ready => Color.green,
        SkinFolderState.MissingFiles => new Color(1f, 0.45f, 0.4f),
        SkinFolderState.NoEdits => new Color(0.65f, 0.65f, 0.65f),
        _ => new Color(1f, 0.7f, 0.25f)
    };

    // ------------------------------------------------------------------ background work (no Unity calls)

    private static void StartScan()
    {
        if (_scanning) return;
        _scanning = true;
        Task.Run(() =>
        {
            try
            {
                List<SkinFolderStatus> folders = SkinFolderScanner.Scan(SkinForgerPaths.Creator);
                _folders = folders;
                MelonLogger.Msg($"{Prefix} scan: " + (folders.Count == 0 ? "no folders" :
                    string.Join(", ", folders.Select(f => $"{f.Name}={f.StateText}"))));
            }
            catch (Exception exception)
            {
                _folders = new List<SkinFolderStatus>();
                MelonLogger.Warning($"{Prefix} scan failed: {exception.GetBaseException().Message}");
            }
            finally
            {
                _scanning = false;
            }
        });
    }

    // ---- extras (counted every 2 s while the Export page is open, see TakeSnapshot)
    private static bool _includeText = true, _includeFonts, _includeLoading = true, _includeSounds = true;
    private static bool _includeOther = true;

    /// <summary>
    /// Edited texts and replaced sounds, per character (2026-11-19, user: a creator may be working on two
    /// unrelated skins at once, so packing "all texts" or "all sounds" forces the other one into the file).
    /// A name not in the set is not packed; both sets start full, so the default is what it always was.
    /// </summary>
    private static List<(string Owner, int Count)> _extraTextByOwner = new();
    private static readonly HashSet<string> TextOwners = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SoundCostumes = new(StringComparer.OrdinalIgnoreCase);
    private static bool _ownerDefaultsDone;
    private static readonly HashSet<string> KnownTextOwners = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> KnownSoundCostumes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The name shown for texts that belong to no character (loading tips).</summary>
    private const string TipsOwner = "Loading Tips";
    private static List<(string Costume, string File)> _extraSounds = new();
    private static int _extraTexts;
    private static string _extraTextDetail = string.Empty;
    private static List<(string Language, string File)> _extraFonts = new();
    private static List<string> _extraLoading = new();
    /// <summary>SkinCreator\Images\Other: the pictures that belong to no costume (2026-11-20).</summary>
    private static List<string> _extraOther = new();
    private static float _nextExtrasCheck;

    private static bool ExtrasSelected =>
        (_includeText && _extraTextByOwner.Any(item => TextOwners.Contains(item.Owner))) ||
        (_includeFonts && _extraFonts.Count > 0) || (_includeLoading && _extraLoading.Count > 0) ||
        (_includeOther && _extraOther.Any(IsPicture)) ||
        (_includeSounds && _extraSounds.Any(f => SoundCostumes.Contains(f.Costume)));

    private static void ExtraRow(ref bool include, string name, bool available, string detail)
    {
        GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
        GUI.enabled = available && !_frameExporting;
        include = GUILayout.Toggle(include, string.Empty, GUILayout.Width(22f));
        GUI.enabled = true;
        GUILayout.Label(name, _bold!, GUILayout.Width(150f));
        Color content = GUI.contentColor;
        GUI.contentColor = available ? new Color(0.75f, 0.75f, 0.75f) : new Color(0.55f, 0.55f, 0.55f);
        GUILayout.Label(detail, Array.Empty<GUILayoutOption>());
        GUI.contentColor = content;
        GUILayout.EndHorizontal();
    }

    /// <summary>
    /// One tick per character under a group, so a creator can pack this skin's texts or sounds without
    /// dragging another project's along (2026-11-19, user). Ticks are applied at the next Layout pass, like
    /// every other control in this window: changing what is drawn in the middle of a frame desynchronises
    /// IMGUI's two passes and makes the window flicker.
    /// </summary>
    private static void PerName(List<(string Name, int Count)> items, HashSet<string> selected, string unit)
    {
        if (items.Count == 0) return;
        Color content = GUI.contentColor;
        if (items.Count > 1)
        {
            GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
            GUILayout.Space(26f);
            GUI.enabled = !_frameExporting;
            bool all = items.All(item => selected.Contains(item.Name));
            if (GUILayout.Button(all ? "Clear All" : "Select All", GUILayout.Width(90f), GUILayout.Height(20f)))
            {
                List<(string Name, int Count)> snapshot = items;
                bool select = !all;
                _pendingExtras = () =>
                {
                    foreach ((string name, int _) in snapshot)
                        if (select) selected.Add(name); else selected.Remove(name);
                };
            }
            GUI.enabled = true;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }
        foreach ((string name, int count) in items)
        {
            GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
            GUILayout.Space(26f);
            GUI.enabled = !_frameExporting;
            bool on = selected.Contains(name);
            bool now = GUILayout.Toggle(on, string.Empty, GUILayout.Width(22f));
            GUI.enabled = true;
            if (now != on)
            {
                string captured = name;
                bool add = now;
                _pendingExtras = () => { if (add) selected.Add(captured); else selected.Remove(captured); };
            }
            GUILayout.Label(name, GUILayout.Width(150f));
            GUI.contentColor = new Color(0.7f, 0.7f, 0.7f);
            GUILayout.Label($"{count} {unit}", _small!, Array.Empty<GUILayoutOption>());
            GUI.contentColor = content;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }
    }

    private static Action? _pendingExtras;

    private static bool IsPicture(string file) =>
        file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);

    private static void CountExtras()
    {
        try
        {
            int cn = TextRuntime.Of("CN").Count(e => e.Edited), en = TextRuntime.Of("EN").Count(e => e.Edited);
            _extraTexts = cn + en;
            _extraTextDetail = $"CN {cn}, EN {en}";

            var byOwner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string language in TextStore.Languages)
                foreach (TextEntry entry in TextRuntime.Of(language))
                {
                    if (!entry.Edited) continue;
                    string owner = string.IsNullOrEmpty(entry.Owner) ? TipsOwner : entry.Owner;
                    byOwner[owner] = byOwner.GetValueOrDefault(owner) + 1;
                }
            _extraTextByOwner = byOwner.OrderBy(pair => pair.Key, StringComparer.CurrentCultureIgnoreCase)
                                       .Select(pair => (pair.Key, pair.Value)).ToList();
            var fonts = new List<(string, string)>();
            foreach (string language in TextStore.Languages)
            {
                string dir = TextStore.FontsDir(SkinForgerPaths.Text, language);
                if (Directory.Exists(dir))
                    fonts.AddRange(Directory.GetFiles(dir).Where(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)).Select(f => (language, f)));
            }
            _extraFonts = fonts;
            _extraLoading = Directory.Exists(SkinForgerPaths.LoadingPictures) ? UsedLoadingFiles(SkinForgerPaths.LoadingPictures) : new List<string>();
            // Images\Other: every picture the creator dumped there, with its sidecar. They belong to no
            // costume, so they travel as their own section rather than inside a skin folder.
            string otherDir = Path.Combine(SkinForgerPaths.Creator, SeenTextures.OtherFolder);
            _extraOther = Directory.Exists(otherDir)
                ? Directory.GetFiles(otherDir)
                    .Where(f => (f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                 f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) &&
                                !SfSkinFormat.IsCensoredName(Path.GetFileName(f)))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>();
            // Sounds: only files named after a game sound (+ muted.txt) are packed.
            var sounds = new List<(string, string)>();
            if (Directory.Exists(SoundRuntime.Root))
                foreach (string dir in Directory.GetDirectories(SoundRuntime.Root))
                    foreach (string file in Directory.GetFiles(dir))
                    {
                        string fileName = Path.GetFileName(file);
                        bool sound = SoundRuntime.Extensions.Contains(Path.GetExtension(fileName).ToLowerInvariant());   // any game sound name (Common: hit / weapon sounds)
                        if (sound || fileName.Equals("muted.txt", StringComparison.OrdinalIgnoreCase)) sounds.Add((Path.GetFileName(dir), file));
                    }
            _extraSounds = sounds;

            // Anything new is selected: a creator who just edited a character expects it to be in the file.
            foreach ((string owner, int _) in _extraTextByOwner)
                if (!_ownerDefaultsDone || !KnownTextOwners.Contains(owner)) TextOwners.Add(owner);
            foreach (string costume in _extraSounds.Select(f => f.Costume).Distinct())
                if (!_ownerDefaultsDone || !KnownSoundCostumes.Contains(costume)) SoundCostumes.Add(costume);
            KnownTextOwners.Clear();
            foreach ((string owner, int _) in _extraTextByOwner) KnownTextOwners.Add(owner);
            KnownSoundCostumes.Clear();
            foreach (string costume in _extraSounds.Select(f => f.Costume).Distinct()) KnownSoundCostumes.Add(costume);
            _ownerDefaultsDone = true;
        }
        catch { }
    }

    /// <summary>
    /// Loading pictures the creator actually uses: the ones listed in config.json "LoadingPictures" (names without
    /// extension) + config.json itself; pictures lying in the folder but not listed are not packed. No config.json /
    /// no list: every picture (all of them are the creator's own - the game's loading pictures are never dumped there).
    /// </summary>
    private static List<string> UsedLoadingFiles(string folder)
    {
        List<string> pictures = Directory.GetFiles(folder).Where(IsPicture).ToList();
        string config = Path.Combine(folder, "config.json");
        if (!File.Exists(config)) return pictures;
        try
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(config));
            if (doc.RootElement.TryGetProperty("LoadingPictures", out System.Text.Json.JsonElement list) && list.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var names = new HashSet<string>(list.EnumerateArray().Select(e => e.ToString()), StringComparer.OrdinalIgnoreCase);
                pictures = pictures.Where(p => names.Contains(Path.GetFileNameWithoutExtension(p)) || names.Contains(Path.GetFileName(p))).ToList();
            }
        }
        catch { }
        if (pictures.Count > 0) pictures.Add(config);
        return pictures;
    }

    /// <summary>Main thread: texts come from TextRuntime; files are read later on the export thread.</summary>
    private static (List<(string Path, string Text)> Texts, int TextCount, List<(string Path, string File)> Files, int Fonts, int Pictures, int Others) CollectExtras()
    {
        var texts = new List<(string, string)>();
        int textCount = 0;
        if (_includeText)
            foreach (string language in TextStore.Languages)
            {
                // Only the characters that are ticked (2026-11-19). An entry with no owner is a loading tip.
                List<TextEntry> chosen = TextRuntime.Of(language)
                    .Where(e => TextOwners.Contains(string.IsNullOrEmpty(e.Owner) ? TipsOwner : e.Owner)).ToList();
                textCount += chosen.Count(e => e.Edited);
                foreach ((string relative, string json) in TextStore.Build(language, chosen, editedOnly: true))
                    texts.Add(("text/" + relative.Replace(Path.DirectorySeparatorChar, '/'), json));
            }
        var files = new List<(string, string)>();
        if (_includeFonts) foreach ((string language, string file) in _extraFonts) files.Add(($"text/Fonts/{language}/{Path.GetFileName(file)}", file));
        if (_includeLoading) foreach (string file in _extraLoading) files.Add(("loading/" + Path.GetFileName(file), file));
        if (_includeOther) foreach (string file in _extraOther) files.Add(("other/" + Path.GetFileName(file), file));
        if (_includeSounds)
            foreach ((string costume, string file) in _extraSounds.Where(f => SoundCostumes.Contains(f.Costume)))
                files.Add(($"audio/{costume}/{Path.GetFileName(file)}", file));
        return (texts, textCount, files, _includeFonts ? _extraFonts.Count : 0, _includeLoading ? _extraLoading.Count(IsPicture) : 0,
                _includeOther ? _extraOther.Count(IsPicture) : 0);
    }

    private static void StartExport(List<SkinFolderStatus> folders)
    {
        if (_exporting || (folders.Count == 0 && !ExtrasSelected)) return;
        var collected = CollectExtras();
        string name = _packageName.Trim();
        string author = _author ?? string.Empty, description = _description ?? string.Empty;
        bool allowUnofficial = _allowUnofficial, everything = _packEverything;
        bool readme = _includeReadme && _frameReadme;
        bool cover = _includeCover && _frameCover;
        _exporting = true;
        _resultText = string.Empty;
        Volatile.Write(ref _progressDone, 0);
        Volatile.Write(ref _progressTotal, 0);
        Task.Run(() =>
        {
            try
            {
                // Re-check right before packing: files may have changed since the page was opened.
                List<SkinFolderStatus> fresh = folders.Select(f => SkinFolderScanner.Check(f.Path)).ToList();
                SkinFolderStatus? bad = fresh.FirstOrDefault(f => !f.Exportable);
                if (bad != null)
                {
                    _resultOk = false;
                    _resultText = $"Not exported: {bad.Name} is now {bad.StateText} ({bad.Detail}). Fix it or deselect it.";
                    _folders = SkinFolderScanner.Scan(SkinForgerPaths.Creator);
                    return;
                }
                var extras = new SfSkinFormat.PackageExtras { Texts = collected.TextCount, Fonts = collected.Fonts, LoadingPictures = collected.Pictures, OtherPictures = collected.Others };
                foreach ((string zipPath, string json) in collected.Texts) extras.Files.Add((zipPath, System.Text.Encoding.UTF8.GetBytes(json)));
                foreach ((string zipPath, string file) in collected.Files) extras.Files.Add((zipPath, File.ReadAllBytes(file)));
                string path = SfSkinFormat.Write(SkinForgerPaths.Export, name, fresh.Select(f => f.Path).ToList(), Generator,
                    (done, total) => { Volatile.Write(ref _progressDone, done); Volatile.Write(ref _progressTotal, total); },
                    everything ? null : EditDetector.ExportFiles,   // null = the whole folder; otherwise the changed images (+ manifest)
                    author, description, File.Exists(PreviewFile) ? PreviewFile : null, extras, allowUnofficial,
                    readme && File.Exists(ReadmeFile) ? ReadmeFile : null,
                    cover && File.Exists(CoverFile) ? CoverFile : null);
                long size = new FileInfo(path).Length;
                _resultOk = true;
                string extra = string.Join(", ", new[] { collected.TextCount > 0 ? $"{collected.TextCount} texts" : "", collected.Fonts > 0 ? $"{collected.Fonts} fonts" : "",
                    collected.Pictures > 0 ? $"{collected.Pictures} loading pictures" : "" }.Where(x => x.Length > 0));
                _resultText = $"Exported  {Path.GetFileName(path)}  ({fresh.Count} skin{(fresh.Count == 1 ? "" : "s")}, " +
                              (everything ? "every image" : $"{fresh.Sum(f => f.Images)} edited images") +
                              $"{(extra.Length > 0 ? ", " + extra : "")}, " +
                              $"{SkinFolderScanner.FormatSize(size)})  to SkinForger/ExportSkin/\n" +
                              "To try it yourself: press 0 (Skin Library) and click Install next to it.";
                SkinLibraryWindow.RefreshExported();
                MelonLogger.Msg($"{Prefix} exported {path} skins=[{string.Join(", ", fresh.Select(f => f.Name))}] bytes={size} " +
                                $"sharing={(allowUnofficial ? SfSkinFormat.RedistributionAny : SfSkinFormat.RedistributionOfficialOnly)}");
            }
            catch (Exception exception)
            {
                _resultOk = false;
                _resultText = "Export failed: " + exception.GetBaseException().Message;
                MelonLogger.Warning($"{Prefix} export failed: {exception}");
            }
            finally
            {
                _exporting = false;
            }
        });
    }

    private static void OpenExportFolder()
    {
        try
        {
            Directory.CreateDirectory(SkinForgerPaths.Export);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + SkinForgerPaths.Export + "\"") { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            _resultOk = false;
            _resultText = "Cannot open the folder: " + exception.GetBaseException().Message;
        }
    }

    private static void LogFailureOnce(string where, Exception exception)
    {
        if (_failureLogged) return;
        _failureLogged = true;
        MelonLogger.Warning($"{Prefix} {where} failed: {exception}");
    }
}
