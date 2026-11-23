using UnityEngine;

namespace SkinForger.SkinPackages;

/// <summary>
/// The Settings page. It lives in a class of its own because two windows show it (2026-11-18, user): the
/// creator window's Settings tab, and the Skin Library's own Settings button — a player who only uses
/// skins never opens the creator window, but still needs the language switch.
///
/// There is one set of settings, not two: both windows draw THIS page, and it writes to the same
/// <see cref="SfSettings"/>. Changing the language in the Skin Library changes it everywhere.
///
/// Every change is applied at the next Layout pass, never in the middle of a frame: a setting that
/// changes what is drawn halfway through desynchronises IMGUI's two passes and makes the window flicker.
/// </summary>
internal static class SettingsPage
{
    private static Action? _pendingSetting;
    private static GUIStyle? _title, _small, _bold;

    private static void EnsureStyles()
    {
        if (_title != null) return;
        _bold = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
        _title = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 16 };
        _small = new GUIStyle(GUI.skin.label) { fontSize = 11 };
    }

    internal static void Draw()
    {
        EnsureStyles();
        GUILayout.Label(SfSettings.Chinese ? "界面语言" : "Interface language", _title!, Array.Empty<GUILayoutOption>());
        bool follow = SfSettings.UiLanguage == "auto";
        bool newFollow = GUILayout.Toggle(follow, SfSettings.Chinese ? "  跟随游戏语言（游戏是中文时显示中文，其他语言显示英文）" : "  Follow the game language (Chinese when the game is in Chinese, otherwise English)", Array.Empty<GUILayoutOption>());
        if (newFollow != follow) _pendingSetting = () => SfSettings.UiLanguage = newFollow ? "auto" : (SfSettings.Chinese ? "zh" : "en");

        // The two language buttons are ALWAYS clickable (2026-11-18). They used to be greyed out while
        // "follow the game language" was ticked, so pressing 中文 on an English game did nothing at all,
        // and unticking the box quietly chose the game's own language instead — the setting file ended up
        // saying "en" while the player believed they had chosen Chinese. Choosing a language IS the way to
        // stop following the game, so pressing one does both.
        GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
        GUILayout.Space(24f);
        Color content = GUI.contentColor;
        foreach ((string value, string label) in new[] { ("zh", "中文"), ("en", "English") })
        {
            bool on = !follow && SfSettings.UiLanguage == value;
            GUI.contentColor = on ? Color.green : content;
            if (GUILayout.Button(label, GUILayout.Width(110f)) && !on) _pendingSetting = () => SfSettings.UiLanguage = value;
        }
        GUI.contentColor = content;
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Label(SfSettings.Chinese
            ? "点一个语言就会自动取消上面的跟随游戏语言。"
            : "Choosing a language turns off \"follow the game language\" above.", _small!, Array.Empty<GUILayoutOption>());

        GUILayout.Space(16f);
        GUILayout.Label(SfSettings.Chinese ? "编辑历史" : "Edit history", _title!, Array.Empty<GUILayoutOption>());
        bool record = SfSettings.RecordEditHistory;
        bool newRecord = GUILayout.Toggle(record, SfSettings.Chinese
            ? "  每次 Reload 时，把改过的图片的上一个版本保存到 replaced 文件夹"
            : "  On every Reload, keep the previous version of each edited image in the replaced folder", Array.Empty<GUILayoutOption>());
        if (newRecord != record) _pendingSetting = () => SfSettings.RecordEditHistory = newRecord;
        GUILayout.Label(SfSettings.Chinese
            ? "关闭后 Reload 不再往 replaced 里保存任何东西（已经保存的不会删除）。JSON 文件从不保存到 replaced，那里只记录图片的编辑历史。"
            : "When off, Reload saves nothing to replaced (what is already there stays). JSON files are never saved there; it only keeps the history of images.", _small!, Array.Empty<GUILayoutOption>());
        GUILayout.Space(16f);
        GUILayout.Label(SfSettings.Chinese ? "快捷键" : "Hotkeys", _title!, Array.Empty<GUILayoutOption>());
        foreach ((string key, string zh, string en) in new[]
                 {
                     ("1", "图片页（主窗口）", "Image page (this window)"),
                     ("2", "文字页", "Text page"),
                     ("3", "声音页", "Sound page"),
                     ("4", "设置页", "Settings page"),
                     ("0", "皮肤库", "Skin Library"),
                 })
        {
            GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
            GUILayout.Space(24f);
            GUILayout.Label(key, _bold!, GUILayout.Width(24f));
            GUILayout.Label(SfSettings.Chinese ? zh : en, Array.Empty<GUILayoutOption>());
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }
        GUILayout.Label(SfSettings.Chinese
            ? "窗口关着时按数字键就开到那一页；已经在那一页再按一次就关闭。在输入框里打字时快捷键不生效。预览没有快捷键，用图片页的按钮打开。"
            : "A number key opens the window on that page; pressing the key of the page already shown closes it. Keys do nothing while you type in a field. Preview has no key: open it with its button on the Image page.", _small!, Array.Empty<GUILayoutOption>());

        if (Event.current != null && Event.current.type == EventType.Layout && _pendingSetting != null)
        {
            Action apply = _pendingSetting;
            _pendingSetting = null;
            apply();
        }
    }

}
