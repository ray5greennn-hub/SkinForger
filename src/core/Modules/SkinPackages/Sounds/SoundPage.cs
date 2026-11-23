using System.Diagnostics;
using UnityEngine;

namespace SkinForger.SkinPackages;

/// <summary>
/// "Sound" page of the creator window (2026-11-17, user design, after Stitchwork's audio list but simpler):
/// left the characters, right the chosen character's voice lines and BGM by category. Click a name to copy it (and
/// SkinCreator\Sounds\&lt;Character&gt;\ is created); put &lt;Name&gt;.wav / .ogg / .mp3 there. Each row: listen to the
/// original, listen to your file (when there is one), mute (muted.txt). Clicks run at the next Layout pass.
/// </summary>
internal static class SoundPage
{
    private static int _selected = -1;
    private const int RecentView = -2;   // "Recently played" (sounds the game played this session)
    private static Vector2 _listScroll, _soundScroll;
    private static string _status = string.Empty;
    private static Action? _pending;
    private static GUIStyle? _row, _rowSelected, _muted, _title, _small, _nameButton;
    private const float ListWidth = 230f;

    internal static void Scroll(float delta) => _soundScroll = new Vector2(_soundScroll.x, Math.Max(0f, _soundScroll.y + delta));

    internal static void Draw(float windowHeight)
    {
        EnsureStyles();
        SoundCatalog.Poll();
        Event e = Event.current;
        if (e != null && e.type == EventType.Layout && _pending != null) { Action action = _pending; _pending = null; action(); }

        GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
        GUILayout.Label("Click a name to copy it, then put  <Name>.wav / .ogg / .mp3  into  SkinCreator/Sounds/<Character>/  and click Reload.", _small!, Array.Empty<GUILayoutOption>());
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Stop", GUILayout.Width(70f))) _pending = () => { SoundRuntime.StopPreview(); _status = string.Empty; };
        if (GUILayout.Button("Open Sounds Folder", GUILayout.Width(160f))) _pending = () => OpenFolder(SoundRuntime.Root);
        GUILayout.EndHorizontal();
        Color content = GUI.contentColor;
        GUI.contentColor = new Color(0.55f, 0.9f, 0.6f);
        string time = SoundRuntime.PreviewTime();
        GUILayout.Label((string.IsNullOrEmpty(_status) ? " " : _status) + (time.Length > 0 ? "     " + time : string.Empty), _small!, Array.Empty<GUILayoutOption>());
        GUI.contentColor = content;

        if (!SoundCatalog.Ready)
        {
            GUILayout.Label("Reading the game's sound list...", Array.Empty<GUILayoutOption>());
            return;
        }
        IReadOnlyList<SoundCharacter> characters = SoundCatalog.Characters;
        float height = Math.Max(200f, windowHeight - 190f);

        GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
        // ---- characters
        _listScroll = GUILayout.BeginScrollView(_listScroll, GUILayout.Width(ListWidth), GUILayout.Height(height));
        if (GUILayout.Button("Recently Played", _selected == RecentView ? _rowSelected! : _row!, GUILayout.Height(26f)))
            _pending = () => { _selected = RecentView; _soundScroll = Vector2.zero; };
        GUILayout.Space(4f);
        for (int i = 0; i < characters.Count; i++)
        {
            SoundCharacter c = characters[i];
            int changed = SoundRuntime.ReplacedCount(c.Entries);
            string label = c.Costume + (changed > 0 ? $"   ({changed})" : string.Empty);
            int index = i;
            if (GUILayout.Button(label, i == _selected ? _rowSelected! : _row!, GUILayout.Height(26f)))
                _pending = () => { _selected = index; _soundScroll = Vector2.zero; };
        }
        GUILayout.EndScrollView();

        // ---- sounds of the selected character
        GUILayout.BeginVertical(Array.Empty<GUILayoutOption>());
        if (_selected == RecentView)
        {
            DrawRecent(height, content);
        }
        else if (_selected < 0 || _selected >= characters.Count)
        {
            GUILayout.Label("Choose a character on the left.", _muted!, Array.Empty<GUILayoutOption>());
        }
        else
        {
            SoundCharacter c = characters[_selected];
            GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
            GUILayout.Label(c.Costume, _title!, Array.Empty<GUILayoutOption>());
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Mute All", GUILayout.Width(90f))) _pending = () => Mute(c, c.Entries.Where(x => x.Category != "BGM").Select(x => x.Name), true);
            if (GUILayout.Button("Unmute All", GUILayout.Width(100f))) _pending = () => Mute(c, c.Entries.Select(x => x.Name), false);
            if (GUILayout.Button("Open Folder", GUILayout.Width(110f))) _pending = () => { EnsureFolder(c); OpenFolder(Path.Combine(SoundRuntime.Root, c.Costume)); };
            GUILayout.EndHorizontal();

            _soundScroll = GUILayout.BeginScrollView(_soundScroll, GUILayout.Height(height - 34f));
            string category = string.Empty;
            foreach (SoundEntry entry in c.Entries)
            {
                if (entry.Category != category)
                {
                    category = entry.Category;
                    GUILayout.Space(4f);
                    GUILayout.Label(CategoryTitle(category), _muted!, Array.Empty<GUILayoutOption>());
                }
                bool replaced = SoundRuntime.IsReplaced(entry.Name), muted = SoundRuntime.IsMuted(entry.Name);
                GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
                GUI.contentColor = muted ? new Color(0.6f, 0.6f, 0.6f) : replaced ? Color.green : content;
                if (GUILayout.Button(entry.Name, _nameButton!, GUILayout.Width(260f), GUILayout.Height(24f)))
                    _pending = () => Copy(c, entry);
                GUI.contentColor = content;
                PlayButton(entry.Name, false, "Original", true, content);
                PlayButton(entry.Name, true, "Yours", replaced && !muted, content);
                bool active = SoundRuntime.IsCurrent(entry.Name, false) || SoundRuntime.IsCurrent(entry.Name, true);
                if (active)
                {
                    bool yours = SoundRuntime.IsCurrent(entry.Name, true);
                    if (GUILayout.Button("↺", GUILayout.Width(30f), GUILayout.Height(24f)))
                        _pending = () => _status = SoundRuntime.PlayPreview(entry.Name, yours);
                }
                else GUILayout.Space(34f);
                GUI.enabled = true;
                bool now = GUILayout.Toggle(muted, " Mute", GUILayout.Width(70f), GUILayout.Height(24f));
                if (now != muted) _pending = () => Mute(c, new[] { entry.Name }, now);
                GUI.contentColor = muted ? new Color(0.75f, 0.75f, 0.75f) : replaced ? Color.green : new Color(0.6f, 0.6f, 0.6f);
                GUILayout.Label(muted ? "Muted" : replaced ? (SoundRuntime.ReplacementOrigin(entry.Name) == "package" ? "Skin Library" : "Replaced") : "Original", _small!, Array.Empty<GUILayoutOption>());
                GUI.contentColor = content;
                GUILayout.EndHorizontal();
                // linked dialogue line (2026-11-17)
                if (TextHooks.TextOfVoice(entry.Name) is { } line)
                {
                    GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
                    GUILayout.Space(18f);
                    GUI.contentColor = line.Edited ? new Color(0.55f, 0.9f, 0.6f) : new Color(0.7f, 0.7f, 0.75f);
                    string said = OneLine(line.Text);
                    GUILayout.Label("└ " + (line.Edited ? "(edited) " : string.Empty) + (said.Length > 60 ? said[..60] + "…" : said) +
                                    (muted ? $"     [muted: bubble {SoundRuntime.MutedSeconds(entry.Name):0.#} s]" : string.Empty), _small!, GUILayout.Width(470f));
                    GUI.contentColor = content;
                    if (GUILayout.Button("Edit in Text page", GUILayout.Width(130f)))
                    {
                        TextEntry target = line;
                        _pending = () => { TextPage.Focus(target); SkinPackagesMod.OpenTextTab(); };
                    }
                    GUILayout.EndHorizontal();
                }
            }
            GUILayout.EndScrollView();
        }
        GUILayout.EndVertical();
        GUILayout.EndHorizontal();
    }

    /// <summary>
    /// Every sound the game played this session, newest first (in-song hits, weapon sounds, UI ...). A name that
    /// belongs to a character goes to that character's folder; any other to Sounds\Common\. Same replace / mute rules.
    /// </summary>
    private static void DrawRecent(float height, Color content)
    {
        GUILayout.Label("Recently Played", _title!, Array.Empty<GUILayoutOption>());
        GUILayout.Label("Every sound the game played since it started, newest first (play a song to see hit / weapon sounds).", _small!, Array.Empty<GUILayoutOption>());
        _soundScroll = GUILayout.BeginScrollView(_soundScroll, GUILayout.Height(height - 50f));
        IReadOnlyList<string> recent = SoundRuntime.Recent;
        if (recent.Count == 0) GUILayout.Label("Nothing played yet.", _muted!, Array.Empty<GUILayoutOption>());
        foreach (string name in recent)
        {
            var known = SoundCatalog.Find(name);
            string folder = known?.Character.Costume ?? "Common";
            bool replaced = SoundRuntime.IsReplaced(name), muted = SoundRuntime.IsMuted(name);
            GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
            GUI.contentColor = muted ? new Color(0.6f, 0.6f, 0.6f) : replaced ? Color.green : content;
            if (GUILayout.Button(name, _nameButton!, GUILayout.Width(260f), GUILayout.Height(24f)))
                _pending = () => CopyName(folder, name);
            GUI.contentColor = content;
            PlayButton(name, false, "Original", true, content);
            PlayButton(name, true, "Yours", replaced && !muted, content);
            bool now = GUILayout.Toggle(muted, " Mute", GUILayout.Width(70f), GUILayout.Height(24f));
            if (now != muted) _pending = () => MuteIn(folder, name, now);
            GUI.contentColor = new Color(0.7f, 0.7f, 0.7f);
            GUILayout.Label(folder, _small!, Array.Empty<GUILayoutOption>());
            GUI.contentColor = content;
            GUILayout.EndHorizontal();
        }
        GUILayout.EndScrollView();
    }

    private static void CopyName(string folder, string name)
    {
        try
        {
            GUIUtility.systemCopyBuffer = name;
            Directory.CreateDirectory(Path.Combine(SoundRuntime.Root, folder));
            _status = $"Copied  {name}   ->  save your file as  SkinCreator/Sounds/{folder}/{name}.wav  (or .ogg / .mp3)";
        }
        catch (Exception exception) { _status = "Copy failed: " + exception.GetBaseException().Message; }
    }

    private static void MuteIn(string folder, string name, bool mute)
    {
        try
        {
            SoundRuntime.SetMuted(folder, new[] { name }, mute);
            _status = (mute ? "Muted " : "Unmuted ") + name + $"  (SkinCreator/Sounds/{folder}/muted.txt)";
        }
        catch (Exception exception) { _status = "Failed: " + exception.GetBaseException().Message; }
    }

    /// <summary>▶ Name = play from the start; while this one plays: "|| Name" (pause); paused: "▶ Name" in yellow (resume).</summary>
    private static void PlayButton(string name, bool yours, string label, bool enabled, Color content)
    {
        bool playing = SoundRuntime.IsPlaying(name, yours), paused = SoundRuntime.IsPaused(name, yours);
        GUI.enabled = enabled;
        GUI.contentColor = playing ? Color.green : paused ? new Color(1f, 0.85f, 0.3f) : content;
        if (GUILayout.Button((playing ? "|| " : "▶ ") + label, GUILayout.Width(yours ? 80f : 95f), GUILayout.Height(24f)))
            _pending = () => _status = SoundRuntime.TogglePreview(name, yours);
        GUI.contentColor = content;
        GUI.enabled = true;
    }

    private static string CategoryTitle(string category) => category switch
    {
        "Apply" => "Apply  (character chosen)",
        "Start" => "Start  (song starts)",
        "Main" => "Main  (touched on the main menu)",
        "Hidden" => "Hidden  (hidden main-menu lines)",
        "Festival" => "Festival  (holidays)",
        "Hurt" => "Hurt  (in a song)",
        "EmptyHit" => "Empty hit  (in a song)",
        "Lost" => "Lost  (song failed)",
        "Victory" => "Victory  (song cleared)",
        "Transform" => "Transform",
        "BGM" => "Character BGM",
        _ => "Other"
    };

    private static string OneLine(string text) => (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ");

    private static void EnsureFolder(SoundCharacter c) => Directory.CreateDirectory(Path.Combine(SoundRuntime.Root, c.Costume));

    private static void Copy(SoundCharacter c, SoundEntry entry)
    {
        try
        {
            GUIUtility.systemCopyBuffer = entry.Name;
            EnsureFolder(c);
            _status = $"Copied  {entry.Name}   ->  save your file as  SkinCreator/Sounds/{c.Costume}/{entry.Name}.wav  (or .ogg / .mp3)";
        }
        catch (Exception exception) { _status = "Copy failed: " + exception.GetBaseException().Message; }
    }

    private static void Mute(SoundCharacter c, IEnumerable<string> names, bool mute)
    {
        try
        {
            List<string> list = names.ToList();
            SoundRuntime.SetMuted(c.Costume, list, mute);
            _status = (mute ? "Muted " : "Unmuted ") + (list.Count == 1 ? list[0] : $"{list.Count} sounds") + $"  (SkinCreator/Sounds/{c.Costume}/muted.txt)";
        }
        catch (Exception exception) { _status = "Failed: " + exception.GetBaseException().Message; }
    }

    private static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
        }
        catch (Exception exception) { _status = "Cannot open the folder: " + exception.GetBaseException().Message; }
    }

    private static void EnsureStyles()
    {
        if (_row != null && _row.normal.background != null && _row.normal.background.Pointer != IntPtr.Zero) return;
        _row = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleLeft };
        _rowSelected = new GUIStyle(_row) { fontStyle = FontStyle.Bold };
        _rowSelected.normal.textColor = Color.green;
        _nameButton = new GUIStyle(GUI.skin.button) { alignment = TextAnchor.MiddleLeft };
        _muted = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
        _muted.normal.textColor = new Color(0.8f, 0.8f, 0.9f);
        _title = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 16 };
        _small = new GUIStyle(GUI.skin.label) { fontSize = 11 };
    }
}
