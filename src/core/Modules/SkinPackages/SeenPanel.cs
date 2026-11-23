using UnityEngine;

namespace SkinForger.SkinPackages;

/// <summary>
/// The bottom of the Image page: the Cursor dump and the history of pictures the game has shown
/// (2026-11-18, user design). Dumping pictures happens at the bottom of this page and nowhere else —
/// "这个下方区域，是dump图片的地方。所有dump图片的按钮一律不要放在上面".
///
/// Every click is deferred to the next Layout pass, the way the Sound page does it: changing what is drawn
/// in the middle of a frame desynchronises Layout and Repaint, which is what made this window flicker twice
/// before.
/// </summary>
internal static class SeenPanel
{
    private static bool _open;
    private static bool _spine = true, _sprite = true, _ui = true;

    /// <summary>
    /// Hides the pictures that are not being drawn this moment (2026-11-19, user: "others 里面太多点了
    /// dump 不下来的东西"). The list is a history, so it keeps naming pictures whose screen has closed;
    /// a picture that is still loaded can be dumped anyway, which is why they are only dimmed by default
    /// rather than dropped.
    /// </summary>
    private static bool _onlyHere;
    private static Vector2 _scroll;
    private static string _search = string.Empty;

    /// <summary>What the creator typed into the by-name box.</summary>
    private static string _name2 = string.Empty;
    private static string _status = string.Empty;
    private static Action? _pending;
    private static GUIStyle? _small, _name;

    private const float Height = 190f;

    /// <summary>Not on screen this moment, and gone altogether.</summary>
    private static readonly Color Dim = new(0.62f, 0.62f, 0.62f), Faded = new(0.48f, 0.44f, 0.44f);

    internal static void Draw()
    {
        EnsureStyles();
        Event e = Event.current;
        if (e != null && e.type == EventType.Layout && _pending != null)
        {
            Action action = _pending;
            _pending = null;
            action();
        }

        Color content = GUI.contentColor;

        GUILayout.Space(6f);
        GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
        if (GUILayout.Button("Dump Cursor", GUILayout.Width(120f)))
            _pending = () => _status = CursorDump.Dump();
        if (GUILayout.Button((_open ? "▼  " : "►  ") + $"Pictures on screen ({SeenTextures.Count})", GUILayout.Width(220f)))
            _pending = () => _open = !_open;
        if (_open && GUILayout.Button("Clear", GUILayout.Width(70f)))
            _pending = () => { SeenTextures.Clear(); _status = "History cleared."; };
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();

        GUI.contentColor = new Color(0.62f, 0.62f, 0.62f);
        GUILayout.Label(_status.Length > 0 ? _status : CursorDump.Warning, _small!, Array.Empty<GUILayoutOption>());
        GUI.contentColor = content;

        // Pictures the list can never offer, because their screen is gone before the window can be opened.
        GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
        GUI.contentColor = Dim;
        GUILayout.Label("By name:", _small!, GUILayout.Width(64f));
        GUI.contentColor = content;
        string typed = GUILayout.TextField(_name2 ?? string.Empty, 64, GUILayout.Width(180f));
        if (typed != _name2) _pending = () => _name2 = typed;
        if (GUILayout.Button("Load & Dump", GUILayout.Width(110f)))
        {
            string wanted = typed;
            _pending = () => { NamedDump.Dump(wanted); _status = NamedDump.Status; };
        }
        foreach ((string label, string prefab) in NamedDump.Suggestions)
            if (GUILayout.Button(label, GUILayout.Width(130f)))
            {
                string wanted = prefab;
                _pending = () => { NamedDump.Dump(wanted); _status = NamedDump.Status; _name2 = wanted; };
            }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();

        if (!_open) return;

        GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
        GUI.contentColor = new Color(0.62f, 0.62f, 0.62f);
        GUILayout.Label("Show:", _small!, GUILayout.Width(42f));
        GUI.contentColor = content;
        bool spine = GUILayout.Toggle(_spine, " Characters & monsters", GUILayout.Width(185f));
        if (spine != _spine) _pending = () => _spine = spine;
        bool sprite = GUILayout.Toggle(_sprite, " Stage art", GUILayout.Width(100f));
        if (sprite != _sprite) _pending = () => _sprite = sprite;
        bool ui = GUILayout.Toggle(_ui, " HUD", GUILayout.Width(70f));
        if (ui != _ui) _pending = () => _ui = ui;
        bool onlyHere = GUILayout.Toggle(_onlyHere, " On screen now", GUILayout.Width(130f));
        if (onlyHere != _onlyHere) _pending = () => _onlyHere = onlyHere;
        GUILayout.Space(10f);
        GUI.contentColor = new Color(0.62f, 0.62f, 0.62f);
        GUILayout.Label("Find:", _small!, GUILayout.Width(36f));
        GUI.contentColor = content;
        string search = GUILayout.TextField(_search, GUILayout.Width(150f));
        if (search != _search) _pending = () => _search = search;
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();

        List<SeenTextures.Seen> all = SeenTextures.Snapshot();
        _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(Height));
        int shown = 0;
        foreach (SeenTextures.Seen seen in all)
        {
            if (!Wanted(seen.Kind)) continue;
            bool here = seen.LastSweep == SeenTextures.Sweep;
            if (_onlyHere && !here) continue;
            if (_search.Length > 0 &&
                seen.Name.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0 &&
                seen.Owner.IndexOf(_search, StringComparison.OrdinalIgnoreCase) < 0) continue;
            shown++;

            GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
            SeenTextures.Seen row = seen;
            if (GUILayout.Button("Dump", GUILayout.Width(60f), GUILayout.Height(22f)))
                _pending = () => _status = SeenTextures.Dump(row);
            GUI.contentColor = seen.Gone ? Faded : Tint(seen.Kind);
            GUILayout.Label(Label(seen.Kind), _small!, GUILayout.Width(74f));
            GUI.contentColor = seen.Gone ? Faded : here ? content : Dim;
            GUILayout.Label(seen.Name, _name!, GUILayout.Width(230f));
            GUI.contentColor = content;
            GUI.contentColor = new Color(0.62f, 0.62f, 0.62f);
            GUILayout.Label($"{seen.W}x{seen.H}", _small!, GUILayout.Width(80f));
            GUILayout.Label(seen.Owner, _small!, Array.Empty<GUILayoutOption>());
            if (seen.Gone)
            {
                GUI.contentColor = Faded;
                GUILayout.Label("— not loaded; open that screen again", _small!, Array.Empty<GUILayoutOption>());
            }
            GUI.contentColor = content;
            GUILayout.EndHorizontal();
        }
        if (shown == 0)
        {
            GUI.contentColor = new Color(0.62f, 0.62f, 0.62f);
            GUILayout.Label(all.Count == 0
                ? "Nothing seen yet. Play a song (or open any screen) and the pictures it draws land here."
                : "Nothing matches the filter.", _small!, Array.Empty<GUILayoutOption>());
            GUI.contentColor = content;
        }
        GUILayout.EndScrollView();

        GUI.contentColor = new Color(0.62f, 0.62f, 0.62f);
        GUILayout.Label("Newest first. A dimmed name is not on screen right now; it can usually still be dumped, and only turns grey-red once a Dump has found it unloaded. Pictures land in SkinCreator\\Images\\Other and nothing you have edited is overwritten.", _small!, Array.Empty<GUILayoutOption>());
        GUI.contentColor = content;
    }

    private static bool Wanted(SeenTextures.Kind kind) => kind switch
    {
        SeenTextures.Kind.Spine => _spine,
        SeenTextures.Kind.Sprite => _sprite,
        _ => _ui
    };

    private static string Label(SeenTextures.Kind kind) => kind switch
    {
        SeenTextures.Kind.Spine => "character",
        SeenTextures.Kind.Sprite => "stage",
        _ => "HUD"
    };

    private static Color Tint(SeenTextures.Kind kind) => kind switch
    {
        SeenTextures.Kind.Spine => new Color(0.55f, 0.9f, 0.6f),
        SeenTextures.Kind.Sprite => new Color(0.65f, 0.75f, 1f),
        _ => new Color(0.9f, 0.8f, 0.5f)
    };

    private static void EnsureStyles()
    {
        if (_small != null) return;
        _small = new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = false };
        _name = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = false };
    }
}
