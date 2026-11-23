using Il2CppInterop.Runtime;
using MelonLoader;
using SkinForger.Main.Pure;
using UnityEngine;

namespace SkinForger.Main.Unity;

/// <summary>
/// SkinForger's window: the Image page, where each character and elfin is set to the game's own art, to the
/// creator's edited images, or to a skin, and where the Dump is started.
///
/// Written against the game and <see cref="WindowModel"/>, not transcribed from the old Main or from
/// upstream SkinChanger. Two things are deliberately unchanged, because players and SkinForger.Core depend
/// on them: the stored positions in Saved.json keep their meaning, and SkinForger.Core draws its own tabs
/// (Text, Sound, Export, Settings) by patching DrawWindow, so DrawWindow stays the single entry point.
///
/// The one rename: upstream's "Root" button is labelled "Edited" — it shows the images in
/// SkinCreator\Images\&lt;Costume&gt;, which is what the creator has been editing.
/// </summary>
public static class MainWindow
{
    private const string Prefix = "[SkinForge][Main][Window]";
    private const int WindowId = 0x5F00;

    private static readonly Color Chosen = new(0.45f, 0.95f, 0.5f);

    private static Rect _rect;
    private static Vector2 _scroll;
    private static bool _showCharacters = true, _showElfins;
    private static string _status = string.Empty;
    private static KeyCode _menuKey = KeyCode.Insert;

    /// <summary>
    /// The key that opens this window. Public because Core owns SkinForger's hotkeys as a set (one place
    /// decides what every key does); Core reads the key Menukey.txt asked for and then sets this to None so
    /// that two handlers cannot toggle the window in the same frame. None simply never matches a key press.
    /// </summary>
    public static KeyCode MenuKey { get => _menuKey; set => _menuKey = value; }
    private static bool _ready;

    // Supplied by the melon at start-up. The window never reaches for the melon type itself: the drawing
    // function is handed in because Core patches SkinForgerMod.DrawWindow by name, so the window must be
    // drawn THROUGH that method for Core's pages and its Chinese interface to appear.
    private static MainLayout _layout = new(string.Empty);
    private static SelectionStore? _selections;
    private static Action<int>? _draw;
    private static GUI.WindowFunction? _drawFunction;

    /// <summary>Whether the window is on screen. The menu key toggles it.</summary>
    public static bool Shown { get; set; }

    /// <summary>
    /// Where the window is. SkinForger.Core reads it to place its own panels (the Skin Library) beside the
    /// window and to decide whether a click landed on SkinForger rather than on the game.
    /// </summary>
    public static Rect Rect => _rect;

    /// <summary>
    /// The Image page's scroll position. SkinForger.Core's wheel handling moves it: IMGUI scroll views in
    /// this game never receive wheel events, so the wheel is read from Input and applied here.
    /// </summary>
    public static Vector2 Scroll { get => _scroll; set => _scroll = value; }

    /// <summary>Set up from the files once the game is running.</summary>
    public static void Initialize(MainLayout layout, SelectionStore selections, Action<int> draw)
    {
        _layout = layout;
        _selections = selections;
        _draw = draw;
        _menuKey = ParseKey(MenuKeySettings.Load(layout.MenuKeyPath, w => MelonLogger.Warning($"{Prefix} {w}")));
        ResetWindow();
        _ready = true;
        MelonLogger.Msg($"{Prefix} ready: press {_menuKey} to open SkinForger");
    }

    /// <summary>A Unity key name, or Insert when the file holds something Unity does not know.</summary>
    public static KeyCode ParseKey(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name) && Enum.TryParse(name, ignoreCase: true, out KeyCode key)) return key;
        MelonLogger.Warning($"{Prefix} \"{name}\" is not a key Unity knows; using {MenuKeySettings.DefaultKey}");
        return KeyCode.Insert;
    }

    /// <summary>
    /// The window back at its starting size and place: the top left corner (2026-11-22, user).
    ///
    /// It used to start a third of the way across and a quarter of the way down, which on a tall window put
    /// the creator page low on the left. Core s Skin Library opens on the RIGHT, so the left edge is where
    /// this one belongs, and starting at the top leaves the whole height for the page.
    /// </summary>
    public static void ResetWindow() =>
        _rect = new Rect(24f, 48f, Screen.width / 2.2f, Screen.height / 1.8f);

    public static void OnUpdate()
    {
        if (_ready && Input.GetKeyDown(_menuKey)) Shown = !Shown;
    }

    public static void OnGUI()
    {
        if (!_ready || !Shown) return;
        if (_draw == null) return;
        // Il2Cpp cannot take a managed method group as a Unity delegate: it has to be converted once and
        // kept alive, the way Core's Skin Library window and the Preview window already do it.
        _drawFunction ??= DelegateSupport.ConvertDelegate<GUI.WindowFunction>(new Action<int>(_draw));
        _rect = GUI.Window(WindowId, _rect, _drawFunction, "SkinForger");
    }

    /// <summary>
    /// The window's contents. SkinForger.Core patches this method: it enters its own drawing scope here
    /// (the Chinese interface) and appends its pages after the Image page, so everything the window shows
    /// goes through this one call.
    /// </summary>
    public static void Draw(int windowId)
    {
        // The Preview button, on its own line ABOVE the Reload row -- where the old build's Cecil step put
        // it, by inserting this call before the first instruction of DrawWindow (user, 2026-11-20: "the
        // button should be above Reload").
        SkinForger.Preview.PreviewManager.DrawLauncher();

        GUILayout.BeginHorizontal();
        // These three carried SkinChanger's wording until 2026-11-19. The behaviour was always this Main's
        // own; only the words were inherited, and inherited words are the one kind of upstream expression a
        // rewrite cannot remove by rewriting. They say what they do now, which reads better anyway.
        if (GUILayout.Button("Reload", GUILayout.Width(110f))) Reload();
        if (GUILayout.Button("Dump All", GUILayout.Width(100f))) DumpAll();
        if (GUILayout.Button("Clear Selection", GUILayout.Width(120f))) DeselectAll();
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Reset Position", GUILayout.Width(120f))) ResetWindow();
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        _showCharacters = GUILayout.Toggle(_showCharacters, " Characters", GUILayout.Width(130f));
        _showElfins = GUILayout.Toggle(_showElfins, " Elfins", GUILayout.Width(110f));
        GUILayout.FlexibleSpace();
        if (_status.Length > 0) GUILayout.Label(_status);
        GUILayout.EndHorizontal();

        _scroll = GUILayout.BeginScrollView(_scroll);
        if (_showCharacters) DrawList(false);
        if (_showElfins) DrawList(true);
        if (!_showCharacters && !_showElfins)
            GUILayout.Label("Tick Characters or Elfins to see the list.");
        GUILayout.EndScrollView();

        GUI.DragWindow(new Rect(0f, 0f, Screen.width, 24f));
    }

    /// <summary>
    /// One list, characters or elfins. The rows themselves are drawn by
    /// SkinForgerMod.DrawSelectionList, because SkinForger.Core patches THAT method: it hides the Skin
    /// Library's own skins from this window and reapplies a skin whose files changed. Drawing the rows
    /// here would walk straight past both.
    /// </summary>
    private static void DrawList(bool elfin)
    {
        // "Sprites" / "Selection Thumbnail": the two pages of this list. SelectionThumbnailManager draws
        // the chooser and, when the thumbnail page is picked, the thumbnail list itself -- it returns false
        // and this list is not drawn. (The old build injected exactly this at the top of DrawSelectionList;
        // the authored Main had the manager initialised but nothing ever called it, so the chooser and the
        // whole thumbnail page were missing from the window. 2026-11-20.)
        if (!SkinForger.SelectionThumbnail.SelectionThumbnailManager.DrawCategory(elfin)) return;

        if (RowsOf(elfin).Count == 0)
        {
            GUILayout.Label(elfin ? "The game's elfin list is not loaded yet." : "The game's character list is not loaded yet.");
            return;
        }
        SkinForgerMod.DrawSelectionList(elfin);
    }

    // ------------------------------------------------------------------ actions

    internal static void Choose(CostumeRow row, SkinChoice choice)
    {
        int position = WindowModel.PositionAfterClick(row, choice);
        // Through SkinForgerMod, not the store: SetSelection ends in SaveSelection, which is where
        // SkinForger.Core rewrites a click on "Edited" into the Skin Library position and records it.
        SkinForgerMod.SetSelection(row.Index, position, row.Elfin);
        // Applying happens here, with the player watching, rather than when a Spine object appears
        // (SkeletonHooks.AutoApply): a wrong assumption then shows up as a broken character on screen.
        int atlases = _selections == null ? 0 : SkeletonHooks.ApplyCostume(row.Index, row.Elfin, _layout, _selections);
        _status = position == SelectionStore.Original
            ? $"{row.Name}: the game's own art"
            : $"{row.Name}: {choice.Label}" + (atlases > 0 ? $" ({atlases} atlas(es) rebuilt)" : " (nothing on screen to change)");
        MelonLogger.Msg($"{Prefix} {row.Name} -> {(position == SelectionStore.Original ? WindowModel.OriginalLabel : choice.Label)}");
    }

    /// <summary>
    /// The Reload button: re-read everything from disk and let the next sweep rebuild from it.
    ///
    /// 2026-11-20: this used to forget the roster and re-read Saved.json only, which is why an edited PNG
    /// did not show -- the composed atlases stayed as they were, and SkinForger.Core's load caches
    /// (LoadCaches: region paths and PNG header sizes, R4-12's spec cache) hang off ReloadSkins /
    /// ReloadAssets and so were never cleared. A stale PNG size is the worst of them: an enlarged image
    /// keeps its old size for the rest of the session, so the new canvas is never even noticed.
    /// Both are called here, in this order: the lists and selections first, then the art.
    /// </summary>
    private static void Reload()
    {
        // The Preview holds its own clone and its own textures; it is torn down before the reload and
        // rebuilt after it, or it would be left pointing at atlases that no longer exist. (Another pair
        // the old build injected around this very sequence.)
        SkinForger.Preview.PreviewManager.BeforeHostReload();
        // Keep a copy of every image the author has changed since the last Reload, before anything reads
        // them again. The old build injected this call here too; without it SkinForger was quietly keeping
        // no backups at all of the creator's own work (2026-11-20).
        try { global::SkinForger.ReplacedBackup.BackupChangedAll(_layout.Images); }
        catch (Exception exception) { MelonLogger.Warning($"{Prefix} backup of changed images: {exception.GetBaseException().Message}"); }
        global::SkinForger.SkinForgerMod.ReloadSkins();    // selections, lists, backup (Core clears its caches on this)
        // The selection screen's cover pictures (SelectionThumbnail.png) are Unity Sprites this runtime
        // built once and cached; dropping the atlases does not touch them. The old build injected this
        // call right here -- InsertBefore(the first ReloadAssets call in DrawWindow) -- and without it an
        // edited thumbnail only appeared after restarting the game, with nothing in the log to say so
        // (found by Analysis/injection-parity.py on 2026-11-21, after the tool was taught to require a
        // QUALIFIED call: the bare name "Reload" matched this very method and read as "called").
        SkinForger.SelectionThumbnail.SelectionThumbnailManager.Reload();
        global::SkinForger.SkinForgerMod.ReloadAssets();   // drop the composed atlases: the next sweep reads the edited PNGs
        SkinForger.Preview.PreviewManager.AfterHostReload();
        _status = "Reloaded.";
    }

    private static void DeselectAll()
    {
        _selections?.Clear();
        _status = "Everything is showing the game's own art.";
    }

    /// <summary>
    /// The Dump button: writes every image of this costume into SkinCreator\Images\&lt;Costume&gt;.
    ///
    /// 2026-11-20: the trigger was the one piece of the Dump this Main never got (the parts that do the
    /// work -- PartCollector, DumpPlan, DumpSession, GamePrefabs.Spawn -- were written and tested, and
    /// SkinForgerMod.DumpCostume has been calling them all along for SkinForger.Core). The button said
    /// "not wired up in this build yet", which is exactly what an author trying to get their art back
    /// does not need to read. It is wired now, and the Reload that follows it is part of the same click:
    /// a Dump writes images and a manifest, and everything that reads them has to look again.
    /// </summary>
    internal static void Dump(CostumeRow row)
    {
        try
        {
            _status = $"Dumping {row.Name}...";
            int written = SkinForgerMod.DumpCostume(row.Index, row.Elfin);
            SkinForgerMod.ReloadSkins();
            SkinForgerMod.ReloadAssets();
            _status = written > 0
                ? $"{row.Name}: {written} image(s) written to SkinCreator\\Images\\{row.Name}"
                : $"{row.Name}: nothing was written - see the log";
            MelonLogger.Msg($"{Prefix} dump of {row.Name}: {written} image(s)");
        }
        catch (Exception exception)
        {
            _status = $"{row.Name}: the dump failed - {exception.GetBaseException().Message}";
            MelonLogger.Warning($"{Prefix} dump of {row.Name}: {exception.GetBaseException()}");
        }
    }

    /// <summary>The Dump All button: every character and every elfin the game's lists name.</summary>
    private static void DumpAll()
    {
        try
        {
            _status = "Dumping everything...";
            int written = SkinForgerMod.ExtractAll();
            SkinForgerMod.ReloadSkins();
            SkinForgerMod.ReloadAssets();
            _status = $"{written} image(s) written to SkinCreator\\Images";
        }
        catch (Exception exception)
        {
            _status = $"the dump failed - {exception.GetBaseException().Message}";
            MelonLogger.Warning($"{Prefix} dump all: {exception.GetBaseException()}");
        }
    }

    // ------------------------------------------------------------------ model

    internal static List<CostumeRow> RowsOf(bool elfin) =>
        WindowModel.Rows(GameRoster.Of(elfin), elfin, _layout.Images,
            SkinFoldersOf, index => _selections?.Get(index, elfin) ?? SelectionStore.Original);

    /// <summary>
    /// The skins a costume can be set to, lowest position first: SkinForgerMod.Skins, which holds the
    /// creator's own folder and every skin SkinForger.Core mounted from the Skin Library. Reading the
    /// folder directly instead would leave the player's packages out of the window.
    /// </summary>
    private static IReadOnlyList<string> SkinFoldersOf(string costume)
    {
        try
        {
            return SkinForgerMod.Skins.TryGetValue(costume, out List<string>? list)
                ? list
                : Array.Empty<string>();
        }
        catch (Exception) { return Array.Empty<string>(); }
    }
}
