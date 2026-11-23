using System.Diagnostics;
using UnityEngine;

namespace SkinForger.SkinPackages;

/// <summary>
/// "Text" page of the creator window (between Image and Export Skin Package), modelled on Stitchwork's text page:
/// left a searchable list, right the original and the editable text. Only the texts SkinForger replaces are listed:
/// character names, costume names, descriptions, main-menu dialogues and loading tips. Language CN / EN.
/// Like the other pages, clicks only queue an action that runs at the next Layout pass.
/// </summary>
internal static class TextPage
{
    private static string _language = "CN";
    private static int _filter;
    private static readonly string[] Filters = { "All", "Names", "Descriptions", "Skills", "CV", "Dialogues", "Festival", "Loading Tips", "Elfins" };
    private static bool _editedOnly;
    private static string _search = string.Empty;
    private static string _selectedKey = string.Empty;
    private static string _draft = string.Empty;
    private static string _draftKey = string.Empty;
    private static Vector2 _listScroll, _editorScroll;
    private static string _status = string.Empty;
    private static Action? _pending;
    private static GUIStyle? _row, _rowSelected, _wrap, _muted, _title, _small, _area;

    private const float RowHeight = 40f;
    private const float ListWidth = 300f;

    // filtered list, rebuilt at Layout when inputs change
    private static List<TextEntry> _visible = new();
    private static string _visibleKey = string.Empty;

    /// <summary>Shows one entry (from the Sound page): its language, all filters, selected.</summary>
    internal static void Focus(TextEntry entry)
    {
        _language = entry.Language;
        _filter = 0;
        _editedOnly = false;
        _search = string.Empty;
        _selectedKey = entry.Key;
        _draftKey = string.Empty;
        _visibleKey = string.Empty;
        _scrollToSelected = true;
    }

    private static bool _scrollToSelected;

    internal static void Scroll(float delta) => _listScroll = new Vector2(_listScroll.x, Math.Max(0f, _listScroll.y + delta));

    internal static void Draw(float windowHeight)
    {
        EnsureStyles();
        Event e = Event.current;
        if (e != null && e.type == EventType.Layout)
        {
            if (_pending != null) { Action action = _pending; _pending = null; action(); }
            RebuildVisible();
            if (_scrollToSelected)
            {
                _scrollToSelected = false;
                int index = _visible.FindIndex(v => v.Key == _selectedKey);
                if (index >= 0) _listScroll = new Vector2(0f, Math.Max(0f, (index - 2) * RowHeight));
            }
        }

        // ---- top bar
        GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
        GUILayout.Label("Language", _muted!, GUILayout.Width(66f));
        foreach (string language in TextStore.Languages)
        {
            Color content = GUI.contentColor;
            if (language == _language) GUI.contentColor = Color.green;
            if (GUILayout.Button(language == "CN" ? "CN  简体中文" : "EN  English", GUILayout.Width(110f)) && language != _language)
                _pending = () => { _language = language; _listScroll = Vector2.zero; };
            GUI.contentColor = content;
        }
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Dump Text", GUILayout.Width(100f))) _pending = () => _status = TextHooks.Dump();
        if (GUILayout.Button("Open Text Folder", GUILayout.Width(140f))) OpenFolder(SkinForgerPaths.Text);
        GUILayout.EndHorizontal();

        // Search on its own row, the filters on the next one: one row was wider than the window (2026-11-15).
        GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
        GUILayout.Label("Search", _muted!, GUILayout.Width(66f));
        _search = GUILayout.TextField(_search ?? string.Empty, 60, GUILayout.Width(260f)) ?? string.Empty;
        GUILayout.Space(10f);
        _editedOnly = GUILayout.Toggle(_editedOnly, "Edited only", GUILayout.Width(100f));
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
        GUILayout.Label("Show", _muted!, GUILayout.Width(66f));
        for (int i = 0; i < Filters.Length; i++)
        {
            Color content = GUI.contentColor;
            if (i == _filter) GUI.contentColor = Color.green;
            int index = i;
            if (GUILayout.Button(Filters[i], GUILayout.ExpandWidth(false))) _pending = () => { _filter = index; _listScroll = Vector2.zero; };
            GUI.contentColor = content;
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(4f);

        IReadOnlyList<TextEntry> all = TextRuntime.Of(_language);
        float bodyHeight = Math.Max(200f, windowHeight - 300f);
        GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());

        // ---- list (only the visible rows are drawn)
        GUILayout.BeginVertical(GUILayout.Width(ListWidth));
        GUILayout.Label($"{_visible.Count} texts · {all.Count(x => x.Edited)} edited", _small!, Array.Empty<GUILayoutOption>());
        _listScroll = GUILayout.BeginScrollView(_listScroll, GUILayout.Width(ListWidth), GUILayout.Height(bodyHeight));
        if (all.Count == 0)
        {
            GUILayout.Label("No texts yet.\n\nClick \"Dump Text\" to export the game's character and elfin names, descriptions, skills, CV, dialogues and loading tips " +
                            "(Chinese and English) into SkinForger/SkinCreator/Text.", _wrap!, Array.Empty<GUILayoutOption>());
        }
        else
        {
            int first = Math.Max(0, (int)(_listScroll.y / RowHeight) - 2);
            int count = (int)(bodyHeight / RowHeight) + 5;
            int last = Math.Min(_visible.Count, first + count);
            GUILayout.Space(first * RowHeight);
            for (int i = first; i < last; i++)
            {
                TextEntry entry = _visible[i];
                bool selected = entry.Key == _selectedKey;
                string preview = OneLine(entry.Text);
                string label = (entry.Edited ? "● " : "") + entry.Title + "\n" + (preview.Length > 40 ? preview[..40] + "…" : preview);
                if (GUILayout.Button(label, selected ? _rowSelected! : _row!, GUILayout.Height(RowHeight - 2f), GUILayout.Width(ListWidth - 22f)))
                {
                    string key = entry.Key;
                    _pending = () => { _selectedKey = key; _draftKey = string.Empty; };
                }
            }
            GUILayout.Space(Math.Max(0, _visible.Count - last) * RowHeight);
        }
        GUILayout.EndScrollView();
        GUILayout.EndVertical();

        // ---- editor
        GUILayout.Space(10f);
        GUILayout.BeginVertical(SfTheme.CardStyle, GUILayout.Height(bodyHeight + 20f));
        TextEntry? current = all.FirstOrDefault(x => x.Key == _selectedKey);
        if (current == null)
        {
            GUILayout.Label("Select a text on the left to edit it.", _muted!, Array.Empty<GUILayoutOption>());
            GUILayout.Label("Edited texts are used by the game the next time it shows them (open the character screen again, " +
                            "or wait for the next loading screen).", _wrap!, Array.Empty<GUILayoutOption>());
        }
        else
        {
            if (_draftKey != current.Key) { _draft = current.Text; _draftKey = current.Key; _editorScroll = Vector2.zero; }
            GUILayout.Label(current.Title, _title!, Array.Empty<GUILayoutOption>());
            GUILayout.Label($"{(current.Language == "CN" ? "简体中文" : "English")} · used when the game language is {(current.Language == "CN" ? "Simplified Chinese" : "English")}", _small!, Array.Empty<GUILayoutOption>());
            GUILayout.Space(6f);
            GUILayout.Label("Original", _muted!, Array.Empty<GUILayoutOption>());
            _editorScroll = GUILayout.BeginScrollView(_editorScroll, GUILayout.Height(Math.Min(110f, bodyHeight * 0.3f)));
            GUILayout.Label(current.Original, _wrap!, Array.Empty<GUILayoutOption>());
            GUILayout.EndScrollView();
            GUILayout.Space(6f);
            // Voice of this dialogue line (2026-11-17): listen while editing; muted voices last by the text length.
            if (TextHooks.VoiceOfText(current) is { } voice)
            {
                GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
                GUILayout.Label("Voice:  " + voice, _small!, GUILayout.Width(250f));
                if (GUILayout.Button(SoundRuntime.IsPlaying(voice, false) ? "|| Original" : "▶ Original", GUILayout.Width(95f)))
                    _pending = () => _status = SoundRuntime.TogglePreview(voice, false);
                GUI.enabled = SoundRuntime.IsReplaced(voice) && !SoundRuntime.IsMuted(voice);
                if (GUILayout.Button(SoundRuntime.IsPlaying(voice, true) ? "|| Yours" : "▶ Yours", GUILayout.Width(80f)))
                    _pending = () => _status = SoundRuntime.TogglePreview(voice, true);
                GUI.enabled = true;
                GUILayout.Label(SoundRuntime.IsMuted(voice) ? $"Muted: the bubble stays {SoundRuntime.MutedSeconds(voice):0.#} s"
                    : SoundRuntime.IsReplaced(voice) ? "Replaced" : "Original voice", _small!, Array.Empty<GUILayoutOption>());
                GUILayout.EndHorizontal();
            }
            GUILayout.Label("Your text", _muted!, Array.Empty<GUILayoutOption>());
            _draft = GUILayout.TextArea(_draft ?? string.Empty, 2000, _area!, GUILayout.Height(Math.Max(80f, bodyHeight - 230f))) ?? string.Empty;
            GUILayout.Space(6f);
            GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
            bool changed = _draft != current.Text;
            GUI.enabled = changed;
            if (GUILayout.Button("Save", GUILayout.Width(90f), GUILayout.Height(28f)))
            {
                TextEntry target = current;
                string text = _draft;
                _pending = () => { target.Text = text; TextRuntime.Save(target); _status = $"Saved: {target.Title}"; };
            }
            if (GUILayout.Button("Revert", GUILayout.Width(90f), GUILayout.Height(28f))) _pending = () => _draftKey = string.Empty;
            GUI.enabled = current.Edited;
            if (GUILayout.Button("Use Original", GUILayout.Width(110f), GUILayout.Height(28f)))
            {
                TextEntry target = current;
                _pending = () => { target.Text = target.Original; TextRuntime.Save(target); _draftKey = string.Empty; _status = $"Back to the original: {target.Title}"; };
            }
            GUI.enabled = true;
            GUILayout.FlexibleSpace();
            GUILayout.Label(changed ? "Not saved" : current.Edited ? "● Edited" : "Original", _small!, Array.Empty<GUILayoutOption>());
            GUILayout.EndHorizontal();
        }
        GUILayout.EndVertical();
        GUILayout.EndHorizontal();

        // ---- fonts + status
        GUILayout.Space(6f);
        GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
        GUILayout.Label("Fonts", _muted!, GUILayout.Width(50f));
        GUILayout.Label("A .ttf / .otf in Text/Fonts/CN or Text/Fonts/EN replaces the font of the whole game in that language.", _small!, Array.Empty<GUILayoutOption>());
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Open Fonts Folder", GUILayout.Width(140f)))
        {
            foreach (string language in TextStore.Languages) Directory.CreateDirectory(TextStore.FontsDir(SkinForgerPaths.Text, language));
            OpenFolder(Path.Combine(SkinForgerPaths.Text, "Fonts"));
        }
        GUILayout.EndHorizontal();
        foreach (string language in TextStore.Languages)
        {
            string name = FontHooks.Status(language);
            string? problem = FontHooks.Problem(language);
            string? install = FontHooks.NeedsInstall(language);
            GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
            GUILayout.Space(54f);
            Color content = GUI.contentColor;
            if (name.Length > 0) GUI.contentColor = Color.green;
            else if (problem != null) GUI.contentColor = new Color(1f, 0.75f, 0.3f);
            string text = name.Length > 0 ? $"{language}: using {name}"
                : install != null && FontHooks.InstalledForMeOnly(language)
                    ? $"{language}: \"{Path.GetFileName(install)}\" is installed only for your Windows account, and the game can only use fonts installed for ALL users. Click Show Font File, right-click the file > \"Install for all users\", then restart the game."
                : install != null ? $"{language}: \"{Path.GetFileName(install)}\" must be installed in Windows for ALL users (the game cannot load font files). Click Show Font File, right-click the file > \"Install for all users\", then restart the game."
                : problem != null ? $"{language}: font not usable: {problem}"
                : $"{language}: game font (no font file)";
            GUILayout.Label(text, _small!, Array.Empty<GUILayoutOption>());
            GUI.contentColor = content;
            if (install != null)
            {
                string file = install;
                if (GUILayout.Button("Show Font File", GUILayout.Width(120f))) ShowFile(file);
            }
            GUILayout.EndHorizontal();
        }
        if (_status.Length > 0)
        {
            Color content = GUI.contentColor;
            GUI.contentColor = Color.green;
            GUILayout.Label(_status, _wrap!, Array.Empty<GUILayoutOption>());
            GUI.contentColor = content;
        }
    }

    private static void RebuildVisible()
    {
        IReadOnlyList<TextEntry> all = TextRuntime.Of(_language);
        string key = $"{TextRuntime.Version}|{_language}|{_filter}|{_editedOnly}|{_search}|{all.Count}";
        if (key == _visibleKey) return;
        _visibleKey = key;
        string search = (_search ?? string.Empty).Trim();
        string filter = Filters[_filter];
        _visible = all.Where(e => filter == "All" || (filter == "Elfins" ? e.Table == TextEntry.Elfins : e.Category == filter))
            .Where(e => !_editedOnly || e.Edited)
            .Where(e => search.Length == 0 ||
                        e.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        e.Text.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        e.Original.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static string OneLine(string text) => (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ");

    private static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
        }
        catch (Exception exception) { _status = "Cannot open the folder: " + exception.GetBaseException().Message; }
    }

    /// <summary>Opens a font file with Windows (the font viewer has an "Install" button). The player installs it.</summary>
    /// <summary>
    /// Shows the font file selected in Explorer. The player right-clicks it > "Install for all users" (Windows asks for
    /// administrator rights). The font viewer's "Install" button installs for this account only, which Unity 2019.4
    /// does not see (2026-11-15 [实测]), so it is not used any more.
    /// </summary>
    private static void ShowFile(string file)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + file + "\"") { UseShellExecute = true });
            _status = "Right-click the selected font file > \"Install for all users\" (Windows 11: Show more options first), then restart the game.";
        }
        catch (Exception exception) { _status = "Cannot show the font file: " + exception.GetBaseException().Message; }
    }

    private static void EnsureStyles()
    {
        if (_row != null && _row.normal.background != null && _row.normal.background.Pointer != IntPtr.Zero) return;
        _row = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleLeft, wordWrap = false, fontSize = 12, padding = new RectOffset(8, 6, 3, 3) };
        _rowSelected = new GUIStyle(_row);
        _rowSelected.normal.background = _row.onNormal.background ?? _row.active.background;
        _rowSelected.normal.textColor = Color.white;
        _wrap = new GUIStyle(GUI.skin.label) { wordWrap = true };
        _muted = new GUIStyle(GUI.skin.label) { fontSize = 12 };
        _muted.normal.textColor = SfTheme.Muted;
        _small = new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = true };
        _small.normal.textColor = SfTheme.Muted;
        _title = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold };
        _area = new GUIStyle(GUI.skin.textArea) { wordWrap = true, fontSize = 14 };
    }
}
