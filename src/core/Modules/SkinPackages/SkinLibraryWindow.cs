using System.Diagnostics;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;

namespace SkinForger.SkinPackages;

/// <summary>
/// Skin Library: the window for players who only want to use skins (key 0, right side of the screen; the creator
/// window opened with key 1 stays on the left). Lists every .sfskin in SkinForger/Skins with an On / Off switch,
/// a Details panel, and a Skin Conflict prompt when a skin changes a character that another enabled skin also changes.
/// Plain player words only: Skin, Turn On / Off, Skin Conflict, Loading order.
/// </summary>
internal static class SkinLibraryWindow
{
    private const string Prefix = "[SkinForge][SkinLibrary]";
    private const int WindowId = 90311;   // 90304 = creator window, 90305 = Preview (PreviewManager)
    private const float Width = 440f;

    private static bool _visible;
    private static Rect _rect;
    private static GUI.WindowFunction? _function;
    private static Vector2 _scroll;
    private static readonly HashSet<string> Expanded = new(StringComparer.OrdinalIgnoreCase);
    private static LibraryPackage? _confirm;   // Skin Conflict prompt for this package

    // ---- the creator's notes (2026-11-21). A package may carry a readme.txt; it opens by itself the first
    // time the player turns that skin on, and the README button on the card opens it again whenever they want.
    private const int ReadmeWindowId = 90312;
    private static LibraryPackage? _readme;
    private static Vector2 _readmeScroll;
    private static Rect _readmeRect;
    private static GUI.WindowFunction? _readmeFunction;
    private static string _status = string.Empty;
    private static bool _failureLogged;

    // Clicks only queue an action; it runs at the next Layout pass so Layout and Repaint draw the same controls.
    private static Action? _pending;

    private static readonly Dictionary<string, Texture2D?> Previews = new(StringComparer.OrdinalIgnoreCase);

    // ---- cover pictures (2026-11-22). A package may carry a cover.png: it is drawn behind that skin's card,
    // cropped to fill it whatever its shape, and darkened from the left so the name on top stays readable.
    // IMGUI draws from top to bottom, so a background has to be drawn before the card's contents -- but the
    // card's size is only known after they are laid out. The rect of the SAME card in the PREVIOUS frame is
    // remembered here and used for this frame's background: the list does not move between frames, so it
    // lands exactly on the card. A card drawn for the very first time simply has no background for one frame.
    private static readonly Dictionary<string, Texture2D?> CoverTextures = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Rect> CardRects = new(StringComparer.OrdinalIgnoreCase);
    private static Texture2D? _shade;
    private static Texture2D? _background;
    private static GUISkin? _themeUsed;
    private static GUIStyle? _window, _cardStyle, _title, _name, _cardName, _body, _small, _muted, _badge, _wrap, _section;

    private static readonly Color Green = new(0.40f, 0.85f, 0.50f);
    private static readonly Color Orange = new(1.00f, 0.66f, 0.32f);
    private static readonly Color Red = new(1.00f, 0.45f, 0.40f);
    private static readonly Color Gray = new(0.62f, 0.62f, 0.66f);
    private static readonly Color Gold = new(0.95f, 0.80f, 0.45f);

    /// <summary>Mouse wheel over the window scrolls the skin list (IMGUI gets no wheel events in this game).</summary>
    /// <summary>True while the window is open and the point (IMGUI coordinates) is on it.</summary>
    internal static bool Covers(Vector2 point) =>
        (_visible && _rect.Contains(point)) || (_readme != null && _readmeRect.Contains(point));

    internal static bool Visible => _visible;

    internal static bool Scroll(Vector2 mouse, float delta)
    {
        // The notes sit on top of the window, so the wheel belongs to them while they are open.
        if (_readme != null && _readmeRect.Contains(mouse))
        {
            _readmeScroll = new Vector2(_readmeScroll.x, Math.Max(0f, _readmeScroll.y + delta));
            return true;
        }
        if (!_visible || !_rect.Contains(mouse)) return false;
        _scroll = new Vector2(_scroll.x, Math.Max(0f, _scroll.y + delta));
        return true;
    }

    // ---- packages in SkinForger/ExportSkin that are not installed in SkinForger/Skins (the creator's own exports)
    private static volatile bool _exportedDirty = true;
    private static List<string> _exported = new();
    private static float _nextExportedCheck;

    /// <summary>Any thread: re-check ExportSkin at the next frame.</summary>
    internal static void RefreshExported() => _exportedDirty = true;

    private static void CheckExported()
    {
        if (!_exportedDirty && Time.realtimeSinceStartup < _nextExportedCheck) return;
        _exportedDirty = false;
        _nextExportedCheck = Time.realtimeSinceStartup + 3f;
        try
        {
            var list = new List<string>();
            if (Directory.Exists(SkinForgerPaths.Export))
                foreach (string file in Directory.GetFiles(SkinForgerPaths.Export, "*" + SfSkinFormat.Extension).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    var exported = new FileInfo(file);
                    var installed = new FileInfo(Path.Combine(SkinForgerPaths.Player, exported.Name));
                    if (installed.Exists && installed.Length == exported.Length && installed.LastWriteTimeUtc == exported.LastWriteTimeUtc) continue;
                    list.Add(file);
                }
            _exported = list;
        }
        catch { _exported = new List<string>(); }
    }

    private static void Install(string exported)
    {
        try
        {
            Directory.CreateDirectory(SkinForgerPaths.Player);
            string target = Path.Combine(SkinForgerPaths.Player, Path.GetFileName(exported));
            File.Copy(exported, target, overwrite: true);   // File.Copy keeps the write time: shows as installed afterwards
            _status = $"{Path.GetFileNameWithoutExtension(exported)} installed.";
            MelonLogger.Msg($"{Prefix} installed {Path.GetFileName(exported)} from ExportSkin");
            _exportedDirty = true;
            Reload("installed " + Path.GetFileName(exported));
        }
        catch (Exception exception)
        {
            _status = "Install failed: " + exception.GetBaseException().Message;
        }
    }

    internal static void Update()
    {
        if (_visible) CheckExported();
        // The key itself belongs to Hotkeys (2026-11-18): one place decides what every key does.
    }

    /// <summary>Opens or closes the Skin Library. Called by <see cref="Hotkeys"/> (key 0).</summary>
    internal static void Toggle(string reason)
    {
        _visible = !_visible;
        if (_visible) { _confirm = null; _settings = false; }
        MelonLogger.Msg($"{Prefix} window {(_visible ? "open" : "closed")} ({reason})");
    }

    /// <summary>True while the Settings page replaces the skin list in this window.</summary>
    private static bool _settings;

    internal static void OnGUI()
    {
        if (!_visible) return;
        try
        {
            EnsureStyles();
            float height = Math.Min(720f, Math.Max(360f, Screen.height - 96f));
            if (_rect.width <= 0f || _rect.x > Screen.width - 80f || _rect.y > Screen.height - 80f)
                _rect = new Rect(Screen.width - Width - 24f, 48f, Width, height);
            _rect.height = height;
            _function ??= DelegateSupport.ConvertDelegate<GUI.WindowFunction>(new Action<int>(Draw));
            // Decided on Layout only: a window that appears between Layout and Repaint is drawn without ever
            // having been laid out, and IMGUI then throws for the rest of the frame.
            if (Event.current.type == EventType.Layout && _readme == null) _readme = ReadmeToShow();
            Loc.Enter();
            try { SfTheme.With(() => _rect = GUI.Window(WindowId, _rect, _function, string.Empty, _window!)); }
            finally { Loc.Exit(); }
            if (_readme != null) DrawReadmeWindow();
        }
        catch (Exception exception)
        {
            LogOnce(exception);
        }
    }

    // ------------------------------------------------------------------ window content

    /// <summary>Logged once, the first time this window draws: what the translation layer sees in here.</summary>
    private static bool _scopeLogged;

    private static void Draw(int id)
    {
        // The scope is entered HERE as well as around GUI.Window. GUI.Window calls this back through an
        // Il2Cpp delegate, and the translation scope is thread-static: whatever that crossing does to it,
        // the window's own contents are inside the scope if the scope starts inside the window (2026-11-18,
        // user: the Skin Library stayed English with the language already set to Chinese).
        Loc.Enter();
        try
        {
            DrawInner(id);
        }
        finally { Loc.Exit(); }
    }

    private static void DrawInner(int id)
    {
        try
        {
            if (!_scopeLogged)
            {
                _scopeLogged = true;
                MelonLogger.Msg($"{Prefix} drawing: chinese={SfSettings.Chinese} scope={Loc.Drawing} sample=\"{Loc.T("Installed Skins")}\"");
            }
            if (Event.current.type == EventType.Layout && _pending != null)
            {
                Action action = _pending;
                _pending = null;
                action();
            }
            IReadOnlyList<LibraryPackage> packages = SkinLibrary.Packages;
            List<LibraryPackage> enabled = SkinLibrary.EnabledInOrder();

            // ---- header
            GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
            GUILayout.Label("SKIN LIBRARY", _title!, Array.Empty<GUILayoutOption>());
            GUILayout.FlexibleSpace();
            Badge(packages.Count == 1 ? "1 Installed" : $"{packages.Count} Installed", packages.Count > 0 ? Green : Gray);
            // A player who only uses skins never opens the creator window, so the one set of settings is
            // reachable from here too (2026-11-18, user). Same page, same file: SettingsPage.
            if (GUILayout.Button(_settings ? "Back" : "Settings", GUILayout.Width(80f)))
                _pending = () => _settings = !_settings;
            if (GUILayout.Button("X", GUILayout.Width(26f))) _pending = () => _visible = false;
            GUILayout.EndHorizontal();

            if (_settings)
            {
                GUILayout.Space(6f);
                _scroll = GUILayout.BeginScrollView(_scroll, Array.Empty<GUILayoutOption>());
                SettingsPage.Draw();
                GUILayout.EndScrollView();
                GUILayout.Label("Press 0 to close", _small!, Array.Empty<GUILayoutOption>());
                GUI.DragWindow(new Rect(0f, 0f, 10000f, 30f));
                return;
            }

            GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
            GUILayout.Label("Installed Skins", _muted!, Array.Empty<GUILayoutOption>());
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Open Skins Folder", GUILayout.Width(140f))) OpenFolder();
            if (GUILayout.Button("Refresh", GUILayout.Width(70f))) _pending = () => { _exportedDirty = true; Reload("refresh"); };
            GUILayout.EndHorizontal();
            GUILayout.Space(4f);

            // ---- body: the conflict prompt or the list
            _scroll = GUILayout.BeginScrollView(_scroll, Array.Empty<GUILayoutOption>());
            if (_confirm != null) DrawConflict(_confirm);
            else if (packages.Count == 0) DrawEmpty();
            else foreach (LibraryPackage package in packages.OrderByDescending(p => SkinLibrary.Priority(p)).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
                DrawCard(package, enabled);
            if (_confirm == null) DrawExported();
            GUILayout.EndScrollView();

            // ---- footer
            GUILayout.Box(string.Empty, _section!, GUILayout.Height(1f), GUILayout.ExpandWidth(true));
            GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
            GUILayout.Label("Active Skins", _body!, Array.Empty<GUILayoutOption>());
            GUILayout.FlexibleSpace();
            GUILayout.Label($"{enabled.Count} / {packages.Count(p => p.Problem == null)}", _name!, Array.Empty<GUILayoutOption>());
            GUILayout.EndHorizontal();
            GUILayout.Label("Your own work in SkinCreator always stays on top.", _small!, Array.Empty<GUILayoutOption>());
            GUILayout.Label("Loading order:  SkinCreator  >  newest skin turned on  >  older skins  >  Muse Dash", _small!, Array.Empty<GUILayoutOption>());
            GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
            string status = PackageLoaderHooks.ReloadPending ? "Applying..." : _status;
            GUI.contentColor = Green;
            GUILayout.Label(status, _small!, Array.Empty<GUILayoutOption>());
            GUI.contentColor = Color.white;
            GUILayout.FlexibleSpace();
            GUI.enabled = enabled.Count > 0 && _confirm == null;
            if (GUILayout.Button("Turn All Off", GUILayout.Width(100f)))
                _pending = () =>
                {
                    SkinLibrary.DisableAll();
                    _status = "All skins turned off.";
                    Reload("all off");
                };
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.Label("Press 0 to close", _small!, Array.Empty<GUILayoutOption>());

            GUI.DragWindow(new Rect(0f, 0f, 10000f, 30f));
        }
        catch (Exception exception)
        {
            LogOnce(exception);
        }
    }

    private static void DrawEmpty()
    {
        GUILayout.Space(20f);
        GUILayout.Label("No skins installed yet.", _name!, Array.Empty<GUILayoutOption>());
        GUILayout.Space(4f);
        GUILayout.Label("Put .sfskin files into the SkinForger/Skins folder (\"Open Skins Folder\" above). They show up here automatically.", _wrap!, Array.Empty<GUILayoutOption>());
        GUILayout.Label("Skins you export yourself are listed below with an Install button.", _small!, Array.Empty<GUILayoutOption>());
    }

    /// <summary>Skins the player exported (SkinForger/ExportSkin) but has not installed: one click copies them to Skins.</summary>
    private static void DrawExported()
    {
        List<string> exported = _exported;
        if (exported.Count == 0) return;
        GUILayout.Space(6f);
        GUILayout.Label("Your Exported Skins (not installed)", _muted!, Array.Empty<GUILayoutOption>());
        foreach (string file in exported)
        {
            GUILayout.BeginHorizontal(_cardStyle!, Array.Empty<GUILayoutOption>());
            bool update = File.Exists(Path.Combine(SkinForgerPaths.Player, Path.GetFileName(file)));
            GUILayout.Label(Path.GetFileNameWithoutExtension(file) + (update ? (SfSettings.Chinese ? "   （比已安装的更新）" : "   (newer than the installed one)") : string.Empty), _body!, Array.Empty<GUILayoutOption>());
            GUILayout.FlexibleSpace();
            GUI.enabled = !PackageLoaderHooks.ReloadPending;
            if (GUILayout.Button(update ? "Update" : "Install", GUILayout.Width(80f))) { string path = file; _pending = () => Install(path); }
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.Space(4f);
        }
        GUILayout.Label("Install copies the file from SkinForger/ExportSkin to SkinForger/Skins.", _small!, Array.Empty<GUILayoutOption>());
    }

    private static void DrawCard(LibraryPackage package, List<LibraryPackage> enabled)
    {
        bool on = enabled.Any(p => p.FileName.Equals(package.FileName, StringComparison.OrdinalIgnoreCase));
        int priority = SkinLibrary.Priority(package);
        GUILayout.BeginVertical(_cardStyle!, Array.Empty<GUILayoutOption>());
        DrawCover(package);

        GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
        Texture2D? preview = PreviewOf(package);
        if (preview != null) GUILayout.Box(preview, GUIStyle.none, GUILayout.Width(64f), GUILayout.Height(64f));
        GUILayout.BeginVertical(Array.Empty<GUILayoutOption>());
        GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
        GUILayout.Label(package.Name, _cardName!, Array.Empty<GUILayoutOption>());
        GUILayout.FlexibleSpace();
        if (package.Problem != null) Badge("Can't Load", Red);
        else if (on) Badge(enabled.Count > 1 ? $"Enabled  #{priority}" : "Enabled", Green);
        else if (SkinLibrary.NewlyAdded.Contains(package.FileName)) Badge("New", Gold);
        else Badge("Off", Gray);
        GUILayout.EndHorizontal();
        if (package.Author.Length > 0) GUILayout.Label("by " + package.Author, _muted!, Array.Empty<GUILayoutOption>());
        GUILayout.Label(package.Problem != null ? "This file is damaged or not a SkinForger skin." : Summary(package), _muted!, Array.Empty<GUILayoutOption>());
        if (package.Problem == null && package.Costumes.Count > 0)
            GUILayout.Label(string.Join(", ", package.Costumes.Keys), _small!, Array.Empty<GUILayoutOption>());
        GUILayout.EndVertical();
        GUILayout.EndHorizontal();

        GUILayout.Box(string.Empty, _section!, GUILayout.Height(1f), GUILayout.ExpandWidth(true));
        GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
        bool open = Expanded.Contains(package.FileName);
        if (GUILayout.Button(open ? "Hide Details" : "Details", GUILayout.Width(100f)))
            _pending = () => { if (!Expanded.Remove(package.FileName)) Expanded.Add(package.FileName); };
        if (package.Problem == null && package.Readme.Length > 0)
        {
            Color readmeBackground = GUI.backgroundColor;
            GUI.backgroundColor = Gold;
            if (GUILayout.Button("README", GUILayout.Width(90f))) { LibraryPackage shown = package; _pending = () => OpenReadme(shown); }
            GUI.backgroundColor = readmeBackground;
        }
        GUILayout.FlexibleSpace();
        if (package.Problem == null) Switch(package, on);
        GUILayout.EndHorizontal();

        if (open) DrawDetails(package, on);
        GUILayout.EndVertical();
        if (Event.current.type == EventType.Repaint) CardRects[package.FileName] = GUILayoutUtility.GetLastRect();
        GUILayout.Space(6f);
    }

    /// <summary>
    /// The card's background: the package's cover.png, cropped to fill the card (any shape fits), with a
    /// black-to-clear wash from the left edge so the name and the lines under it stay readable on top of it.
    /// </summary>
    private static void DrawCover(LibraryPackage package)
    {
        if (Event.current.type != EventType.Repaint) return;
        Texture2D? cover = CoverOf(package);
        if (cover == null) return;
        if (!CardRects.TryGetValue(package.FileName, out Rect rect) || rect.width <= 1f || rect.height <= 1f) return;
        Color tint = GUI.color;
        // ScaleAndCrop fills the whole rect and clips what sticks out: a square cover fills a wide card.
        GUI.color = new Color(1f, 1f, 1f, 0.85f);
        GUI.DrawTexture(rect, cover, ScaleMode.ScaleAndCrop, false);
        GUI.color = Color.white;
        GUI.DrawTexture(rect, Shade(), ScaleMode.StretchToFill, true);
        GUI.color = tint;
    }

    /// <summary>Left-to-right black wash drawn over a cover. Built once; the gradient is in its pixels.</summary>
    private static Texture2D Shade()
    {
        if (_shade != null && _shade.Pointer != IntPtr.Zero) return _shade;
        const int width = 64;
        var texture = new Texture2D(width, 1, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        for (int x = 0; x < width; x++)
        {
            float t = x / (float)(width - 1);
            // Nearly solid at the left edge, still a little dark at the right: the picture shows, the text wins.
            float alpha = 0.90f * (1f - t) * (1f - t) + 0.28f;
            texture.SetPixel(x, 0, new Color(0f, 0f, 0f, Math.Min(0.95f, alpha)));
        }
        texture.Apply();
        _shade = texture;
        return texture;
    }

    private static Texture2D? CoverOf(LibraryPackage package)
    {
        if (package.Cover == null) return null;
        string key = package.FileName + "|" + package.WriteUtc.Ticks;
        if (CoverTextures.TryGetValue(key, out Texture2D? cached) && (cached == null || cached.Pointer != IntPtr.Zero)) return cached;
        Texture2D? texture = null;
        try
        {
            texture = new Texture2D(2, 2) { hideFlags = HideFlags.HideAndDontSave };
            if (!ImageConversion.LoadImage(texture, package.Cover)) texture = null;
        }
        catch { texture = null; }
        CoverTextures[key] = texture;
        return texture;
    }

    private static string Summary(LibraryPackage package)
    {
        var parts = new List<string> { "Skin Package", package.Images == 1 ? "1 Image" : $"{package.Images} Images" };
        if (package.Audio > 0) parts.Add($"{package.Audio} Audio");
        if (package.Text > 0) parts.Add($"{package.Text} Text");
        if (package.LoadingPictures > 0) parts.Add($"{package.LoadingPictures} Loading Pictures");
        if (package.OtherPictures > 0) parts.Add(package.OtherPictures == 1 ? "1 Other Picture" : $"{package.OtherPictures} Other Pictures");
        if (package.Fonts > 0) parts.Add(package.Fonts == 1 ? "1 Font" : $"{package.Fonts} Fonts");
        return string.Join("  ·  ", parts);
    }

    /// <summary>On / Off switch. Turning a skin on that overlaps another enabled skin opens the Skin Conflict prompt.</summary>
    private static void Switch(LibraryPackage package, bool on)
    {
        Color background = GUI.backgroundColor;
        GUI.backgroundColor = on ? Green : new Color(0.45f, 0.45f, 0.5f);
        GUI.enabled = !PackageLoaderHooks.ReloadPending;
        if (GUILayout.Button(on ? "ON" : "OFF", GUILayout.Width(80f), GUILayout.Height(24f)))
        {
            if (on)
                _pending = () =>
                {
                    SkinLibrary.SetEnabled(package, false);
                    _status = $"{package.Name} turned off.";
                    Reload("turned off " + package.FileName);
                };
            else
                _pending = () =>
                {
                    if (SkinLibrary.Conflicts(package).Count > 0) { _confirm = package; _scroll = Vector2.zero; return; }
                    TurnOn(package);
                };
        }
        GUI.enabled = true;
        GUI.backgroundColor = background;
    }

    private static void TurnOn(LibraryPackage package)
    {
        SkinLibrary.SetEnabled(package, true);
        _status = $"{package.Name} turned on.";
        Reload("turned on " + package.FileName);
        if (package.Readme.Length > 0 && !SkinLibrary.ReadmeSeen(package)) OpenReadme(package);
    }

    /// <summary>Shows a package's notes, and remembers that they have been shown.</summary>
    private static void OpenReadme(LibraryPackage package)
    {
        _readme = package;
        _readmeScroll = Vector2.zero;
        SkinLibrary.MarkReadmeSeen(package);
        MelonLogger.Msg($"{Prefix} readme opened: {package.FileName} ({package.Readme.Length} characters)");
    }

    /// <summary>
    /// The package whose notes should open by themselves, or null.
    ///
    /// A package can become enabled without anyone pressing ON: a newly installed skin that overlaps nothing is
    /// turned on for the player by SkinLibrary.Update. Asking what is ENABLED, rather than hooking the button
    /// alone, catches that too. It is only ever asked while this window is open, so a readme can never appear
    /// over a song.
    /// </summary>
    private static LibraryPackage? ReadmeToShow()
    {
        if (_confirm != null || _settings) return null;
        foreach (LibraryPackage package in SkinLibrary.EnabledInOrder())
        {
            if (package.Readme.Length == 0 || SkinLibrary.ReadmeSeen(package)) continue;
            SkinLibrary.MarkReadmeSeen(package);
            _readmeScroll = Vector2.zero;
            MelonLogger.Msg($"{Prefix} readme shown on first use: {package.FileName} ({package.Readme.Length} characters)");
            return package;
        }
        return null;
    }

    /// <summary>The notes themselves: centred, dimmed behind, wheel to scroll, closed with Close or Esc.</summary>
    private static void DrawReadmeWindow()
    {
        float width = Math.Min(760f, Math.Max(420f, Screen.width - 160f));
        float height = Math.Min(620f, Math.Max(300f, Screen.height - 200f));
        _readmeRect = new Rect((Screen.width - width) * 0.5f, (Screen.height - height) * 0.5f, width, height);
        // Everything behind the notes is dimmed, so they read as one thing on top of the game rather than as
        // another panel among the skin cards.
        Color tint = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.55f);
        GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = tint;
        _readmeFunction ??= DelegateSupport.ConvertDelegate<GUI.WindowFunction>(new Action<int>(DrawReadme));
        Loc.Enter();
        try { SfTheme.With(() => GUI.ModalWindow(ReadmeWindowId, _readmeRect, _readmeFunction, string.Empty, _window!)); }
        finally { Loc.Exit(); }
    }

    private static void DrawReadme(int id)
    {
        LibraryPackage? package = _readme;
        if (package == null) return;
        try
        {
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
            {
                _readme = null;
                Event.current.Use();
                return;
            }
            GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
            GUILayout.Label(package.Name, _title!, Array.Empty<GUILayoutOption>());
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("X", GUILayout.Width(26f))) _readme = null;
            GUILayout.EndHorizontal();
            GUILayout.Label(package.Author.Length > 0 ? "by " + package.Author : "Not credited", _muted!, Array.Empty<GUILayoutOption>());
            GUILayout.Box(string.Empty, _section!, GUILayout.Height(1f), GUILayout.ExpandWidth(true));
            GUILayout.Space(4f);

            _readmeScroll = GUILayout.BeginScrollView(_readmeScroll, Array.Empty<GUILayoutOption>());
            // The creator's own words: never translated, never turned into markup, shown exactly as written.
            GUILayout.Label(package.Readme, _wrap!, Array.Empty<GUILayoutOption>());
            GUILayout.EndScrollView();

            GUILayout.Space(4f);
            GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
            GUILayout.Label("Written by the skin's creator.  Mouse wheel to scroll.", _small!, Array.Empty<GUILayoutOption>());
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Close", GUILayout.Width(110f), GUILayout.Height(26f))) _readme = null;
            GUILayout.EndHorizontal();
        }
        catch (Exception exception)
        {
            _readme = null;
            LogOnce(exception);
        }
    }

    private static void DrawDetails(LibraryPackage package, bool on)
    {
        GUILayout.Space(4f);
        Row("Author", package.Author.Length > 0 ? package.Author : "Not credited");
        if (package.Description.Length > 0)
        {
            GUILayout.Label("Description", _muted!, Array.Empty<GUILayoutOption>());
            GUILayout.Label(package.Description, _wrap!, Array.Empty<GUILayoutOption>());
        }
        Row("Sharing", package.SharingAllowed
            ? "Creator allows any build"
            : "Official SkinForger on " + (package.MadeFor.Length > 0 ? package.MadeFor : SfSkinFormat.OfficialGame) + " only");
        Row("Images", package.Images.ToString());
        Row("Audio", package.Audio.ToString());
        Row("Text", package.Text.ToString());
        if (package.Created.Length > 0) Row("Made on", package.Created);
        Row("File", $"{package.FileName}  ({SkinFolderScanner.FormatSize(package.Bytes)})");
        if (package.Problem != null)
        {
            GUI.contentColor = Red;
            GUILayout.Label(package.Problem, _wrap!, Array.Empty<GUILayoutOption>());
            GUI.contentColor = Color.white;
            return;
        }

        GUILayout.Space(4f);
        GUILayout.Label("Changes", _muted!, Array.Empty<GUILayoutOption>());
        foreach ((string costume, int images) in package.Costumes)
            Row("  " + costume, images == 1 ? "1 image" : $"{images} images");

        GUILayout.Space(4f);
        GUILayout.Label("Conflicts", _muted!, Array.Empty<GUILayoutOption>());
        List<SkinConflict> conflicts = SkinLibrary.Conflicts(package);
        if (conflicts.Count == 0)
        {
            GUI.contentColor = Green;
            GUILayout.Label("No conflict with your other enabled skins.", _body!, Array.Empty<GUILayoutOption>());
            GUI.contentColor = Color.white;
            return;
        }
        int mine = SkinLibrary.Priority(package);
        foreach (SkinConflict conflict in conflicts)
        {
            LibraryPackage? other = SkinLibrary.Packages.FirstOrDefault(p => p.Name == conflict.OtherName && SkinLibrary.IsEnabled(p));
            bool onTop = on && other != null && mine > 0 && mine < SkinLibrary.Priority(other);   // #1 is the top
            // Built at runtime, so Loc's dictionary cannot reach it: these say both languages themselves.
            bool zh = SfSettings.Chinese;
            string shared = conflict.SharedImages == 0 ? string.Empty
                : zh ? $"（{conflict.SharedImages} 张图重叠）" : $" ({conflict.SharedImages} same images)";
            string winner = onTop
                ? (zh ? "显示的是这个皮肤。" : "This skin is shown on top.")
                : (zh ? $"显示的是 {conflict.OtherName}。" : conflict.OtherName + " is shown on top.");
            GUI.contentColor = Orange;
            GUILayout.Label(on
                ? (zh ? $"{conflict.Costume}：{conflict.OtherName} 也改了这个角色{shared}。{winner}"
                      : $"{conflict.Costume}: also changed by {conflict.OtherName}{shared}. {winner}")
                : (zh ? $"{conflict.Costume}：{conflict.OtherName} 也改了这个角色{shared}。开启这个皮肤后，显示的就是它。"
                      : $"{conflict.Costume}: also changed by {conflict.OtherName}{shared}. Turning this skin on puts it on top."),
                _wrap!, Array.Empty<GUILayoutOption>());
            GUI.contentColor = Color.white;
        }
    }

    private static void DrawConflict(LibraryPackage package)
    {
        List<SkinConflict> conflicts = SkinLibrary.Conflicts(package);
        GUILayout.BeginVertical(_cardStyle!, Array.Empty<GUILayoutOption>());
        GUI.contentColor = Orange;
        GUILayout.Label("Skin Conflict Detected", _name!, Array.Empty<GUILayoutOption>());
        GUI.contentColor = Color.white;
        GUILayout.Label(SfSettings.Chinese
            ? $"你正在开启 {package.Name}。在它和其他皮肤改了同一个角色的地方，显示的是 {package.Name}。它没有的图，仍然用另一个皮肤的。"
            : $"You are turning on {package.Name}. Where it changes the same character as another skin, " +
              $"{package.Name} is shown on top. Images it does not have still come from the other skin.",
            _wrap!, Array.Empty<GUILayoutOption>());
        GUILayout.Space(6f);
        GUILayout.Label("Characters", _muted!, Array.Empty<GUILayoutOption>());
        foreach (string costume in package.Costumes.Keys)
        {
            List<SkinConflict> hits = conflicts.Where(c => c.Costume.Equals(costume, StringComparison.OrdinalIgnoreCase)).ToList();
            GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
            GUILayout.Label(costume, _body!, GUILayout.Width(110f));
            GUILayout.FlexibleSpace();
            if (hits.Count == 0)
            {
                GUI.contentColor = Green;
                GUILayout.Label("No conflict", _body!, Array.Empty<GUILayoutOption>());
            }
            else
            {
                GUI.contentColor = Orange;
                int shared = hits.Sum(h => h.SharedImages);
                GUILayout.Label($"{string.Join(", ", hits.Select(h => h.OtherName))}  →  {package.Name}" +
                                (shared > 0 ? $"  ({shared} images)" : string.Empty), _body!, Array.Empty<GUILayoutOption>());
            }
            GUI.contentColor = Color.white;
            GUILayout.EndHorizontal();
        }
        GUILayout.Space(6f);
        GUILayout.Label("Your SkinCreator work stays unchanged and on top.", _small!, Array.Empty<GUILayoutOption>());
        GUILayout.Space(8f);
        GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
        if (GUILayout.Button("Cancel", GUILayout.Height(30f))) _pending = () => _confirm = null;
        Color background = GUI.backgroundColor;
        GUI.backgroundColor = Green;
        if (GUILayout.Button("Turn On Anyway", GUILayout.Height(30f)))
            _pending = () =>
            {
                _confirm = null;
                TurnOn(package);
            };
        GUI.backgroundColor = background;
        GUILayout.EndHorizontal();
        GUILayout.EndVertical();
    }

    // ------------------------------------------------------------------ helpers

    private static void Reload(string reason)
    {
        PackageLoaderHooks.RequestReload(reason);
    }

    private static void Row(string label, string value)
    {
        GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
        GUILayout.Label(label, _muted!, GUILayout.Width(110f));
        GUILayout.Label(value, _body!, Array.Empty<GUILayoutOption>());
        GUILayout.EndHorizontal();
    }

    private static void Badge(string text, Color color)
    {
        Color content = GUI.contentColor;
        GUI.contentColor = color;
        GUILayout.Label(text, _badge!, Array.Empty<GUILayoutOption>());
        GUI.contentColor = content;
    }

    private static Texture2D? PreviewOf(LibraryPackage package)
    {
        if (package.Preview == null) return null;
        string key = package.FileName + "|" + package.WriteUtc.Ticks;
        if (Previews.TryGetValue(key, out Texture2D? cached) && (cached == null || cached.Pointer != IntPtr.Zero)) return cached;
        Texture2D? texture = null;
        try
        {
            texture = new Texture2D(2, 2) { hideFlags = HideFlags.HideAndDontSave };
            if (!ImageConversion.LoadImage(texture, package.Preview)) texture = null;
        }
        catch { texture = null; }
        Previews[key] = texture;
        return texture;
    }

    private static void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(SkinForgerPaths.Player);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + SkinForgerPaths.Player + "\"") { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            _status = "Cannot open the folder: " + exception.GetBaseException().Message;
        }
    }

    private static Texture2D Solid(ref Texture2D? cache, Color color)
    {
        if (cache != null && cache.Pointer != IntPtr.Zero) return cache;
        cache = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
        cache.SetPixel(0, 0, color);
        cache.Apply();
        return cache;
    }

    private static void EnsureStyles()
    {
        GUISkin? theme = SfTheme.Skin;
        if (_window != null && ReferenceEquals(_themeUsed, theme) && (theme == null || theme.Pointer != IntPtr.Zero) &&
            _window.normal.background != null && _window.normal.background.Pointer != IntPtr.Zero) return;
        _themeUsed = theme;
        // Same dark panel as the theme, with the theme's 2 px black outline and rounded corners.
        _window = new GUIStyle(theme?.window ?? GUI.skin.window) { padding = new RectOffset(14, 14, 12, 12) };
        if (theme == null)
        {
            Texture2D background = Solid(ref _background, SfTheme.Background);
            _window.normal.background = background;
            _window.onNormal.background = background;
            _window.focused.background = background;
            _window.onFocused.background = background;
        }
        _cardStyle = SfTheme.CardStyle;
        _section = SfTheme.Divider;
        _title = new GUIStyle(GUI.skin.label) { fontSize = 17, fontStyle = FontStyle.Bold };
        _title.normal.textColor = Color.white;
        _name = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold };
        _name.normal.textColor = Color.white;
        // The skin's own name on its card: the biggest thing in the list (2026-11-22, user: make it easier to read).
        _cardName = new GUIStyle(GUI.skin.label) { fontSize = 21, fontStyle = FontStyle.Bold };
        _cardName.normal.textColor = Color.white;
        _body = new GUIStyle(GUI.skin.label) { fontSize = 13 };
        _body.normal.textColor = new Color(0.92f, 0.92f, 0.94f);
        _wrap = new GUIStyle(_body) { wordWrap = true };
        _muted = new GUIStyle(GUI.skin.label) { fontSize = 12 };
        _muted.normal.textColor = Gray;
        _small = new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = true };
        _small.normal.textColor = Gray;
        _badge = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight };
    }

    private static void LogOnce(Exception exception)
    {
        if (_failureLogged) return;
        _failureLogged = true;
        MelonLogger.Warning($"{Prefix} window failed: {exception}");
    }
}
