using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace SkinForger.SkinPackages;

/// <summary>
/// The SkinForger look (2026-11-15), taken from the Skin Library window: dark purple-gray panels, rounded corners,
/// a black outline around each window, light text, green for "selected / on".
/// Used by the Skin Library (key 0) and the creator window (key 1): Main's OnGUI runs with <see cref="Skin"/> as
/// GUI.skin and gets the original skin back afterwards, so nothing else that draws IMGUI is affected.
/// </summary>
internal static class SfTheme
{
    internal static readonly Color Background = new(0.13f, 0.12f, 0.15f, 0.97f);
    internal static readonly Color Card = new(0.21f, 0.20f, 0.24f, 1f);
    internal static readonly Color Band = new(0.19f, 0.18f, 0.22f, 1f);
    internal static readonly Color Text = new(0.92f, 0.92f, 0.94f, 1f);
    internal static readonly Color Muted = new(0.62f, 0.62f, 0.66f, 1f);
    internal static readonly Color Line = new(0.36f, 0.35f, 0.40f, 1f);
    internal static readonly Color Green = new(0.40f, 0.85f, 0.50f, 1f);
    internal static readonly Color Orange = new(1.00f, 0.66f, 0.32f, 1f);
    internal static readonly Color Red = new(1.00f, 0.45f, 0.40f, 1f);
    internal static readonly Color Gold = new(0.95f, 0.80f, 0.45f, 1f);

    private static GUISkin? _skin;
    private static GUIStyle? _titleBar, _card, _divider;
    private static readonly List<Texture2D> Textures = new();
    private static bool _failed;

    /// <summary>The themed skin, or null when it could not be built (then the default look stays).</summary>
    internal static GUISkin? Skin
    {
        get
        {
            if (_failed) return null;
            if (_skin != null && _skin.Pointer != IntPtr.Zero && Textures.All(t => t != null && t.Pointer != IntPtr.Zero)) return _skin;
            try { Build(); }
            catch (Exception exception)
            {
                _failed = true;
                MelonLogger.Warning($"[SkinForge][Theme] theme unavailable, default look kept: {exception.GetBaseException().Message}");
                return null;
            }
            return _skin;
        }
    }

    internal static GUIStyle TitleBar { get { _ = Skin; return _titleBar ?? GUI.skin.label; } }
    internal static GUIStyle CardStyle { get { _ = Skin; return _card ?? GUI.skin.box; } }
    internal static GUIStyle Divider { get { _ = Skin; return _divider ?? GUI.skin.box; } }

    // ------------------------------------------------------------ Main's creator window

    internal static int Install(HarmonyLib.Harmony harmony)
    {
        Type host = AccessTools.TypeByName("SkinForger.SkinForgerMod") ?? throw new TypeLoadException("SkinForger.SkinForgerMod");
        var onGui = AccessTools.DeclaredMethod(host, "OnGUI");
        if (onGui == null) return 0;
        harmony.Patch(onGui,
            prefix: new HarmonyMethod(typeof(SfTheme), nameof(OnGuiPrefix)) { priority = HarmonyLib.Priority.First },
            finalizer: new HarmonyMethod(typeof(SfTheme), nameof(OnGuiFinalizer)) { priority = HarmonyLib.Priority.Last });
        return 1;
    }

    private static GUISkin? _saved;

    private static void OnGuiPrefix()
    {
        _saved = null;
        try
        {
            GUISkin? skin = Skin;
            if (skin == null) return;
            _saved = GUI.skin;
            GUI.skin = skin;
        }
        catch { _saved = null; }
    }

    private static Exception? OnGuiFinalizer(Exception? __exception)
    {
        try { if (_saved != null) GUI.skin = _saved; } catch { }
        _saved = null;
        return __exception;
    }

    /// <summary>Runs <paramref name="draw"/> with the themed skin (Skin Library).</summary>
    internal static void With(Action draw)
    {
        GUISkin? skin = Skin, saved = null;
        if (skin != null) { saved = GUI.skin; GUI.skin = skin; }
        try { draw(); }
        finally { if (saved != null) GUI.skin = saved; }
    }

    // ------------------------------------------------------------ building

    private static void Build()
    {
        foreach (Texture2D texture in Textures) if (texture != null && texture.Pointer != IntPtr.Zero) UnityEngine.Object.Destroy(texture);
        Textures.Clear();
        GUISkin source = GUI.skin;
        GUISkin skin = UnityEngine.Object.Instantiate(source);
        skin.hideFlags = HideFlags.HideAndDontSave;

        // Window: dark panel, rounded, 2 px black outline.
        GUIStyle window = new(source.window);
        SetBackground(window, Rounded(Background, Color.black, 9, 2), 11);
        window.onNormal.background = window.normal.background;
        window.onFocused.background = window.normal.background;
        window.focused.background = window.normal.background;
        AllText(window, Text);
        skin.window = window;

        GUIStyle button = new(source.button);
        button.normal.background = Rounded(new Color(0.27f, 0.26f, 0.31f, 1f), new Color(0.40f, 0.39f, 0.46f, 1f), 5, 1);
        button.hover.background = Rounded(new Color(0.34f, 0.33f, 0.39f, 1f), new Color(0.50f, 0.49f, 0.56f, 1f), 5, 1);
        button.active.background = Rounded(new Color(0.18f, 0.17f, 0.21f, 1f), new Color(0.40f, 0.39f, 0.46f, 1f), 5, 1);
        button.focused.background = button.normal.background;
        button.onNormal.background = Rounded(new Color(0.20f, 0.33f, 0.24f, 1f), Green, 5, 1);
        button.onHover.background = button.onNormal.background;
        button.onActive.background = button.active.background;
        button.border = new RectOffset(6, 6, 6, 6);
        AllText(button, Text);
        skin.button = button;

        GUIStyle field = new(source.textField);
        SetBackground(field, Rounded(new Color(0.09f, 0.09f, 0.11f, 1f), new Color(0.33f, 0.32f, 0.38f, 1f), 4, 1), 5);
        field.hover.background = Rounded(new Color(0.09f, 0.09f, 0.11f, 1f), new Color(0.48f, 0.47f, 0.54f, 1f), 4, 1);
        field.focused.background = Rounded(new Color(0.09f, 0.09f, 0.11f, 1f), Green, 4, 1);
        field.onFocused.background = field.focused.background;
        AllText(field, Text);
        skin.textField = field;
        GUIStyle area = new(source.textArea);
        area.normal.background = field.normal.background;
        area.hover.background = field.hover.background;
        area.focused.background = field.focused.background;
        area.onFocused.background = field.focused.background;
        area.border = field.border;
        AllText(area, Text);
        skin.textArea = area;

        GUIStyle box = new(source.box);
        SetBackground(box, Rounded(Card, new Color(0, 0, 0, 0), 6, 0), 7);
        AllText(box, Text);
        skin.box = box;

        GUIStyle label = new(source.label);
        AllText(label, Text);
        skin.label = label;
        GUIStyle toggle = new(source.toggle);
        AllText(toggle, Text);
        skin.toggle = toggle;

        GUIStyle track = new(source.verticalScrollbar);
        SetBackground(track, Rounded(new Color(0.09f, 0.09f, 0.11f, 0.9f), new Color(0, 0, 0, 0), 4, 0), 5);
        skin.verticalScrollbar = track;
        GUIStyle thumb = new(source.verticalScrollbarThumb);
        SetBackground(thumb, Rounded(new Color(0.40f, 0.39f, 0.46f, 1f), new Color(0, 0, 0, 0), 4, 0), 5);
        thumb.hover.background = Rounded(new Color(0.52f, 0.51f, 0.58f, 1f), new Color(0, 0, 0, 0), 4, 0);
        thumb.active.background = thumb.hover.background;
        skin.verticalScrollbarThumb = thumb;
        GUIStyle hTrack = new(source.horizontalScrollbar);
        SetBackground(hTrack, track.normal.background, 5);
        skin.horizontalScrollbar = hTrack;
        GUIStyle hThumb = new(source.horizontalScrollbarThumb);
        SetBackground(hThumb, thumb.normal.background, 5);
        skin.horizontalScrollbarThumb = hThumb;

        _titleBar = new GUIStyle(source.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, border = new RectOffset(11, 11, 11, 2) };
        _titleBar.normal.background = RoundedTop(Color.black, 9);
        _titleBar.normal.textColor = Color.white;
        _card = new GUIStyle(source.box) { padding = new RectOffset(10, 10, 8, 8), margin = new RectOffset(0, 0, 0, 0) };
        SetBackground(_card, Rounded(Card, new Color(0, 0, 0, 0), 7, 0), 8);
        _divider = new GUIStyle(GUIStyle.none) { margin = new RectOffset(0, 0, 6, 6), fixedHeight = 1f, stretchWidth = true };
        _divider.normal.background = Solid(Line);

        _skin = skin;
        MelonLogger.Msg("[SkinForge][Theme] dark theme ready (Skin Library look, black window outline)");
    }

    private static void SetBackground(GUIStyle style, Texture2D texture, int border)
    {
        style.normal.background = texture;
        style.hover.background = texture;
        style.active.background = texture;
        style.focused.background = texture;
        style.border = new RectOffset(border, border, border, border);
    }

    private static void AllText(GUIStyle style, Color color)
    {
        style.normal.textColor = color;
        style.hover.textColor = Color.white;
        style.active.textColor = color;
        style.focused.textColor = color;
        style.onNormal.textColor = color;
        style.onHover.textColor = Color.white;
        style.onActive.textColor = color;
        style.onFocused.textColor = color;
    }

    internal static Texture2D Solid(Color color)
    {
        var texture = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
        texture.SetPixel(0, 0, color);
        texture.Apply();
        Textures.Add(texture);
        return texture;
    }

    /// <summary>Rounded rectangle for 9-slice drawing (style.border = radius + 2): fill, optional outline, smooth corners.</summary>
    private static Texture2D Rounded(Color fill, Color outline, int radius, int outlineWidth) => Shape(fill, outline, radius, outlineWidth, true);

    private static Texture2D RoundedTop(Color fill, int radius) => Shape(fill, fill, radius, 0, false);

    private static Texture2D Shape(Color fill, Color outline, int radius, int outlineWidth, bool roundBottom)
    {
        int size = radius * 2 + 4;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                bool cornerRow = roundBottom || py > size / 2f;   // texture row 0 is the bottom
                float dx = Math.Max(0f, Math.Max(radius - px, px - (size - radius)));
                float dy = cornerRow ? Math.Max(0f, Math.Max(radius - py, py - (size - radius))) : Math.Max(0f, py - (size - radius));
                float d = (float)Math.Sqrt(dx * dx + dy * dy);
                float coverage = Mathf.Clamp01(radius - d + 0.5f);
                Color color = fill;
                if (outlineWidth > 0)
                {
                    float inner = Mathf.Clamp01(radius - outlineWidth - d + 0.5f);
                    // straight edges: distance to the nearest side
                    float edge = Math.Min(Math.Min(px, size - px), Math.Min(py, size - py));
                    if (dx == 0f && dy == 0f) inner = Mathf.Clamp01(edge - outlineWidth + 0.5f);
                    color = Color.Lerp(outline, fill, inner);
                }
                color.a *= coverage;
                texture.SetPixel(x, y, color);
            }
        texture.Apply();
        Textures.Add(texture);
        return texture;
    }
}
