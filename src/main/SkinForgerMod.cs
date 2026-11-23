using System.Reflection;
using Il2CppSpine;
using Il2CppSpine.Unity;
using MelonLoader;
using SkinForger.Main.Pure;
using SkinForger.Main.Unity;
using UnityEngine;

namespace SkinForger;

/// <summary>
/// SkinForger's main melon, written as source (2026-11-17). Replaces the Cecil-patched SkinForger.Melon.dll.
///
/// The type name and namespace are fixed: SkinForger.Core resolves this type by name
/// (AccessTools.TypeByName("SkinForger.SkinForgerMod"), 48 places) and patches the methods listed in
/// CONTRACT.md. Keeping that surface is what lets the old Main be swapped out without touching Core.
///
/// Port status (see SF日志\SF切割SkinChanger计划.md):
///   done    - formats and layout, geometry, the Dump's naming / plan / comparison, the anchor formula,
///             the frame-rate cap, the game's character and elfin lists, this window;
///   next    - applying replacement images, the runtime atlas, and the Harmony hooks that start all of it.
/// Until the port is finished this assembly is not deployed: the Main doing the work is still the old one.
///
/// Clean-room: nothing here is transcribed from the decompiled old Main or from the upstream
/// MuseDashSkinChanger. The sources are the game's API, Core's documented contracts and SF日志.
/// </summary>
public sealed class SkinForgerMod : MelonMod
{
    internal const string Prefix = "[SkinForge][Main]";

    /// <summary>Folders and file names (frozen layout).</summary>
    public static MainLayout Layout { get; private set; } = new(MelonLoader.Utils.MelonEnvironment.GameRootDirectory);

    /// <summary>Which skin each character / elfin shows (Saved.json / SavedElfin.json).</summary>
    public static SelectionStore Selections { get; private set; } =
        new(new MainLayout(MelonLoader.Utils.MelonEnvironment.GameRootDirectory), SelectedCharacters, SelectedElfins);

    /// <summary>
    /// The live selections, character index to chosen position. SkinForger.Core binds these two by name
    /// as FIELDS and changes them in place: DumpIsolation puts everything back to Original while a Dump
    /// runs and restores it after, CreatorWindowHooks turns a Root click into the Skin Library position,
    /// PackageLoaderHooks corrects positions after a package is mounted. They are the storage, not a view
    /// of it, so a change Core makes is a change Main sees.
    /// </summary>
    public static readonly Dictionary<int, int> SelectedCharacters = new();

    /// <summary>The elfin half of <see cref="SelectedCharacters"/>.</summary>
    public static readonly Dictionary<int, int> SelectedElfins = new();

    // Where each half is saved. Core reads these two by name (they keep the old Main's names, including
    // the underscore) and hands them straight back to SaveSelection.
    private static string _savedCharactersPath = string.Empty;
    private static string _savedElfinsPath = string.Empty;

    /// <summary>Whether the Main has finished starting. Core checks this before it mounts packages.</summary>
    private static bool _initialized;

    /// <summary>
    /// Costume name -> its skin folders, lowest position first. The TYPE and the NAME are contract:
    /// SkinForger.Core's PackageLoaderHooks reads this field as Dictionary&lt;string, List&lt;string&gt;&gt;
    /// and refuses to start without it ("Main skin list contract"), which takes the Skin Library, the Text
    /// and Sound pages, the export page and the Chinese interface down with it. Learned the hard way on
    /// 2026-11-17, during the first trial of the authored Main.
    /// </summary>
    public static readonly Dictionary<string, List<string>> Skins = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The frame-rate cap from SkinForgeFpsLimiter.json; 0 means the game is left alone.</summary>
    public static int FpsLimit { get; private set; }

    private static float _nextFpsCheck;

    public override void OnInitializeMelon()
    {
        Layout = new MainLayout(MelonLoader.Utils.MelonEnvironment.GameRootDirectory);
        Selections = new SelectionStore(Layout, SelectedCharacters, SelectedElfins);
        _savedCharactersPath = Selections.FileOf(false);
        _savedElfinsPath = Selections.FileOf(true);

        Layout.EnsureFolders();
        Selections.Load();
        RebuildSkinList();
        ForgetSkinIndexes();
        FpsLimit = FpsLimiterSettings.Load(Layout.FpsLimiterPath, warning => MelonLogger.Warning($"{Prefix} {warning}"));
        MainWindow.Initialize(Layout, Selections, DrawWindow);

        // EARLY, not with the other bundled runtimes. The pictures in Images\Other include the ones the
        // game shows while it is still starting -- the headset tip is on screen before OnLateInitializeMelon
        // runs at all, so a feature started there can only ever catch it a second late (player, 2026-11-20).
        // This one hooks nothing: it reads a folder and looks at UI components, so it is safe this early.
        Start("OtherTextures", () => OtherTextures.Initialize(Layout));

        // The censored-resource contract, and the binding Core checks for. Calling it is what makes this
        // assembly reference SkinForger.Core; without that reference Core starts none of its modules.
        try { SkinForger.HexieBlock.MainGuard.Verify(); }
        catch (Exception exception) { MelonLogger.Warning($"{Prefix} MainGuard: {exception.GetBaseException().Message}"); }

        MelonLogger.Msg($"{Prefix} authored Main 0.1.0: " +
                        $"{Selections.Count(false)} character selections, {Selections.Count(true)} elfin selections, " +
                        $"frame cap {(FpsLimit == 0 ? "off" : FpsLimit.ToString())}");
        _initialized = true;
        // 2026-11-21: this line used to end "this build has never run in the game — treat the first session
        // as a trial and read the log before trusting it". It was true on 2026-11-19 and has been false and
        // alarming ever since, which is its own kind of wrong log. What is worth saying every session is
        // where to look when something does not change on screen.
        MelonLogger.Msg($"{Prefix} replacement, dump and window are all wired. " +
                        "An edited picture shows after Reload; a part in the wrong place is an anchor " +
                        "question and says so in this log.");
    }

    public override void OnLateInitializeMelon()
    {
        TryInstallPatches();
        StartBundledFeatures();
    }

    /// <summary>
    /// Starts the authored runtimes that are compiled into this assembly but hook the game themselves.
    ///
    /// In the old Main nothing called these from source either: each feature's own Cecil build step
    /// (CustomLoadingPic\Build, SelectionThumbnail\Build, ...) INJECTED the call into the start-up method
    /// of the finished binary. The authored Main is compiled, not post-processed, so the calls have to be
    /// here — and when they are missing the feature is simply absent, with nothing in the log to say so.
    /// That is how the 16:17 trial lost the custom loading pictures, which had never had a bug before.
    ///
    /// Late, not in OnInitializeMelon: each one patches a game type, and a Spine or UI hook installed
    /// during start-up has frozen the game before (SF日志, H0). Each is guarded on its own so one failure
    /// does not take the rest down.
    /// </summary>
    private static void StartBundledFeatures()
    {
        Start("CustomLoadingPic", () => CustomLoadingPicManager.Initialize());
        Start("SelectionThumbnail", () => SkinForger.SelectionThumbnail.SelectionThumbnailManager.Initialize());
        Start("Preview", () => SkinForger.Preview.PreviewManager.Initialize());
        // ReplacedBackup follows the skins folder, so it is (re)started wherever the skin list is rebuilt.
        // Its own log sink is set first: without it the class is silent (its Console.WriteLine calls were
        // rebound to MelonLogger by the old build's Cecil step), and a baseline that fails to start then
        // blocks every backup-dependent write with nothing in the log to explain it.
        ReplacedBackup.Logger = message => MelonLogger.Msg($"{Prefix} {message}");
        Start("ReplacedBackup", () => ReplacedBackup.Initialize(Layout.Images));
    }

    private static void Start(string feature, Action start)
    {
        try { start(); }
        catch (Exception exception)
        {
            MelonLogger.Warning($"{Prefix} {feature} did not start: {exception.GetBaseException().Message}");
        }
    }

    private static bool _characterShowsFromConfig;
    private static float _nextShowsRetry;

    public override void OnUpdate()
    {
        // The game's own loop is running, so asking for a config table is safe now. See
        // GameConfig.GameLoopRunning for why nothing may ask before this point.
        GameConfig.GameLoopRunning = true;
        RetryCharacterShows();
        MainWindow.OnUpdate();
        UpdateFpsLimiterGuard();
        // The four creation hooks miss every Spine object the game reuses (the elfin page's elfin is the
        // one that showed it). Watching the scene is the only thing that catches those.
        SkeletonHooks.Sweep();
        // The pictures that belong to no costume (warning screens, UI panels, a font atlas). They are
        // found by the texture's own NAME, so the only way to reach them is to watch the scene too.
        OtherTextures.Sweep();
    }

    /// <summary>
    /// The four places SkinForger meets the game. They are patched once, late, so the game's assemblies are
    /// loaded: a Spine hook installed during start-up has frozen the game before (SF日志, H0).
    /// Failing to patch one of them is logged and does not stop the others.
    /// </summary>
    public static int TryInstallPatches()
    {
        if (_patched) return _patchCount;
        _patched = true;
        var harmony = new HarmonyLib.Harmony("SkinForger.Main");

        _patchCount += Patch(harmony, typeof(SkeletonRenderer), "Awake", nameof(SkeletonRendererAwake));
        _patchCount += Patch(harmony, typeof(SkeletonRenderer), "Initialize", nameof(SkeletonRendererInitialize), new[] { typeof(bool) });
        _patchCount += Patch(harmony, typeof(SkeletonGraphic), "Awake", nameof(SkeletonGraphicAwake));

        Type? characterApply = HarmonyLib.AccessTools.TypeByName("CharacterApply") ??
                               HarmonyLib.AccessTools.TypeByName("Il2CppCharacterApply");
        if (characterApply != null) _patchCount += Patch(harmony, characterApply, "OnEnable", nameof(CharacterApplyOnEnable));
        else MelonLogger.Warning($"{Prefix} CharacterApply was not found: the character page hook is not installed");

        MelonLogger.Msg($"{Prefix} {_patchCount} of 4 game hooks installed");
        return _patchCount;
    }

    private static bool _patched;
    private static int _patchCount;

    private static int Patch(HarmonyLib.Harmony harmony, Type type, string method, string postfix, Type[]? arguments = null)
    {
        try
        {
            MethodBase? target = HarmonyLib.AccessTools.Method(type, method, arguments);
            if (target == null)
            {
                MelonLogger.Warning($"{Prefix} {type.Name}.{method} was not found");
                return 0;
            }
            harmony.Patch(target, postfix: new HarmonyLib.HarmonyMethod(typeof(SkinForgerMod), postfix));
            return 1;
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"{Prefix} {type.Name}.{method}: {exception.GetBaseException().Message}");
            return 0;
        }
    }

    // ---------------------------------------------------------------- the game hooks (Core patches these)

    // The Preview spawns its own copy of a character and poses it itself. While it is doing that, these
    // four hooks must not run: Main would see the clone appear and apply a skin to it, which is both wasted
    // work and the wrong picture. PreviewManager.IsHostApplySuppressed() says when to stand back.
    //
    // 2026-11-20: in the old build this guard was INJECTED into each of these methods by the Cecil step
    // (PreviewMvp\Build\Program.cs), together with the Preview button itself, the Draw call and the two
    // reload callbacks. The authored Main is compiled, not post-processed, so all of it was simply absent
    // -- which is why the Preview button disappeared from the window. Written out here, once.
    public static void SkeletonRendererAwake(SkeletonRenderer __instance)
    {
        if (SkinForger.Preview.PreviewManager.IsHostApplySuppressed()) return;
        SkeletonHooks.OnSkeletonRenderer(__instance);
    }

    public static void SkeletonRendererInitialize(SkeletonRenderer __instance)
    {
        if (SkinForger.Preview.PreviewManager.IsHostApplySuppressed()) return;
        SkeletonHooks.OnSkeletonRendererInitialize(__instance);
    }

    public static void SkeletonGraphicAwake(SkeletonGraphic __instance)
    {
        if (SkinForger.Preview.PreviewManager.IsHostApplySuppressed()) return;
        SkeletonHooks.OnSkeletonGraphic(__instance);
    }

    public static void CharacterApplyOnEnable(Component __instance)
    {
        if (SkinForger.Preview.PreviewManager.IsHostApplySuppressed()) return;
        SkeletonHooks.OnCharacterApply(__instance?.gameObject);
    }

    // ---------------------------------------------------------------- the rest of the contract

    /// <summary>
    /// The costume a Spine object belongs to: its index, or -1 when no costume claims it.
    ///
    /// The parameter is called <c>obj</c>, not something more readable, because Harmony binds a patch's
    /// parameters to this one BY NAME and throws when the name is not there — which takes down the whole
    /// module, not just that patch. SkinPackages' prefix, postfix and record all declare <c>obj</c>, so a
    /// rename here is a contract break. 2026-11-19 cost a trial exactly this way; Analysis/param-name-parity.py
    /// now catches it offline. Do not rename.
    /// </summary>
    public static int Scan(GameObject? obj, bool elfin)
    {
        PrefabOwner? owner = GamePrefabs.OwnerOf(obj?.name);
        // Also apply, for whoever asked: Core's RoleSkins calls this while it works through a costume's
        // other skins. It is NOT a scene sweep -- Core's own sweep looks for textures and never calls this
        // -- so the objects the creation hooks miss are caught by SkeletonHooks.Sweep from OnUpdate.
        if (owner != null) SkeletonHooks.Notice(obj);
        return owner != null && owner.Elfin == elfin ? owner.Index : -1;
    }

    /// <summary>Reads the selections and the lists again (the window's Reload button, and Core after mounting).</summary>
    public static void ReloadSkins()
    {
        RefreshCharacterShows();
        GameRoster.Forget();
        GamePrefabs.Forget();
        Selections.Load();
        RebuildSkinList();
        // The backup follows the CREATOR's folder (SkinCreator\Images), so it is re-pointed whenever the
        // list is rebuilt — the same place the old Main did it from.
        //
        // 2026-11-20: this Main handed it Layout.Player (SkinForger\Skins) instead. Every one of the 11291
        // entries in the player's replaced-state-v1.json names a file under SkinCreator\Images, so the
        // first one failed the "is this a tracked resource" test, the whole state was declared invalid, and
        // EVERY backup-dependent write was refused for the rest of the session. The old Main pointed it at
        // the folder Core redirects its skins root to, which is Images; that is what the state file says
        // and what the authored Main's own MainLayout calls "Main's skins root".
        Start("ReplacedBackup", () => ReplacedBackup.Initialize(Layout.Images));
        MelonLogger.Msg($"{Prefix} reloaded: {Selections.Count(false)} character and {Selections.Count(true)} elfin selections");
    }

    /// <summary>
    /// Puts every Spine object back to the game's own art. Replacing images is not part of this build yet,
    /// so there is nothing to undo; the registry is cleared so the next pass looks at everything afresh.
    /// </summary>
    public static void ReloadAssets()
    {
        SkeletonHooks.Forget();
        RuntimeAtlas.Forget();
        OtherTextures.Forget();   // puts the Images\Other pictures back and re-reads the folder
        ForgetSkinIndexes();
    }

    /// <summary>
    /// Brings a show prefab into the scene for a moment. Used by the Dump.
    ///
    /// The parameter is called <c>resourceName</c> because HexieBlock's prefix declares that name and
    /// Harmony binds by name: a rename here stops censored prefabs from being refused and takes the whole
    /// module down with it. Checked by Analysis/param-name-parity.py. Do not rename.
    /// </summary>
    public static bool TrySpawnForExtract(string resourceName, Action<GameObject> use) =>
        GamePrefabs.Spawn(resourceName, use);

    /// <summary>
    /// Dumps one costume into SkinCreator\Images\&lt;Costume&gt;, where the author edits. Returns how many
    /// images were written.
    ///
    /// 2026-11-20: this used to dump into the SHADOW folder (SkinCreator\ImagesAuthored), which is where
    /// the port compared the authored Dump with the old one part by part. That was right while nothing
    /// called it but the parity probe -- and wrong the moment it became the player's Dump button, which
    /// has to put the pictures where the author opens them. The probe asks SkeletonHooks.DumpCostume for a
    /// shadow dump itself, so the comparison is unaffected.
    /// </summary>
    public static int DumpCostume(int index, bool elfin)
    {
        ForgetSkinIndexes();   // a Dump writes images and a manifest: every index read before it is stale
        return SkeletonHooks.DumpCostume(index, elfin, Layout, shadow: false).Sum(result => result.Written);
    }

    /// <summary>
    /// Says whether an atlas folder keeps every anchor in ONE anchors.json instead of one JSON per image.
    ///
    /// Only SkinForger.Core knows which atlases are frame-by-frame (cat) skins -- it learns them from the
    /// game's own prefabs -- and RoleSkins sets this at start-up. Main asks through it rather than guessing.
    ///
    /// 2026-11-21: before this, the Dump decided by looking for an existing anchors.json, so a cat folder
    /// that had just been dumped fresh (no file yet) got 52 per-image JSONs -- the very thing the author
    /// asked not to have. A JSON per image is for puppet parts the author nudges one by one; a frame-by-
    /// frame skin wants one file that pins the lot.
    /// </summary>
    public static Func<string,bool>? FolderKeepsAnchorsInOneFile;   // no space in the type: Analysis/field-name-parity.py matches declarations by shape

    /// <summary>Dumps every character and elfin that the game's lists name.</summary>
    public static int ExtractAll()
    {
        int written = 0;
        foreach (RosterEntry entry in GameRoster.Characters.ToList()) written += DumpCostume(entry.Index, false);
        foreach (RosterEntry entry in GameRoster.Elfins.ToList()) written += DumpCostume(entry.Index, true);
        MelonLogger.Msg($"{Prefix} dumped everything: {written} images");
        return written;
    }

    // ------------------------------------------------------------------ names Core looks up
    //
    // Core finds these BY NAME with reflection, and several of them were named by the old Main rather than
    // chosen. Keeping the names is what makes the authored Main a drop-in: a rename here is a silently
    // disabled feature over there. They are plain views onto this Main's own state, nothing is duplicated.

    /// <summary>
    /// The config fields that name a character's show prefabs, in the game's own order. PreviewEnhancements
    /// walks them to offer every context (main show, victory, fail ...) in the Preview window.
    /// </summary>
    public static string[] CharacterShows = ShowFieldsFromConfig();

    /// <summary>
    /// Reads the show fields again now that the game's config is loaded.
    ///
    /// CharacterShows is a static field, so its initializer runs when this class is first touched -- long
    /// before the game has any config -- and it was stuck on the four fallback names for the whole session
    /// (2026-11-20). Core and the Preview read the field, so the field is what gets refreshed, in place.
    /// </summary>
    public static void RefreshCharacterShows()
    {
        try
        {
            string[] shows = ShowFieldsFromConfig();
            if (shows.Length > 0 && !shows.SequenceEqual(CharacterShows))
            {
                CharacterShows = shows;
                MelonLogger.Msg($"{Prefix} character show fields: {string.Join(", ", shows)}");
            }
        }
        catch (Exception exception) { MelonLogger.Warning($"{Prefix} show fields: {exception.GetBaseException().Message}"); }
    }

    /// <summary>
    /// Reads the show fields once the game has its config.
    ///
    /// ReloadSkins runs while the mods are still starting, when the config cannot be asked for, so the
    /// first read always falls back. This keeps asking -- once a second, and only until the config
    /// answers -- so the session ends up with the real fields instead of the four fallback names.
    /// </summary>
    private static void RetryCharacterShows()
    {
        if (_characterShowsFromConfig) return;
        if (UnityEngine.Time.realtimeSinceStartup < _nextShowsRetry) return;
        _nextShowsRetry = UnityEngine.Time.realtimeSinceStartup + 1f;
        if (string.IsNullOrEmpty(GameConfig.Text(GamePrefabs.CharacterTable))) return;
        _characterShowsFromConfig = true;
        // ReloadSkins re-reads the show fields and every list that was built without a config.
        ReloadSkins();
    }

    /// <summary>
    /// The show fields the character config actually carries, in the order the config lists them. Read from
    /// the config rather than written down, so a game update that adds a context does not need a code change
    /// (PrefabIndex.IsPrefabField defines what counts as one).
    /// </summary>
    private static string[] ShowFieldsFromConfig()
    {
        try
        {
            string? json = GameConfig.Text(GamePrefabs.CharacterTable);   // "character" -- "characters" is not a table
            if (string.IsNullOrEmpty(json)) return new[] { "mainShow", "battleShow", "victoryShow", "failShow" };
            var names = new List<string>();
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(json!);
            foreach (System.Text.Json.JsonElement row in document.RootElement.EnumerateArray())
                foreach (System.Text.Json.JsonProperty property in row.EnumerateObject())
                    if (property.Value.ValueKind == System.Text.Json.JsonValueKind.String &&
                        PrefabIndex.IsPrefabField(property.Name) && !names.Contains(property.Name))
                        names.Add(property.Name);
            return names.Count > 0 ? names.ToArray() : new[] { "mainShow", "battleShow", "victoryShow", "failShow" };
        }
        catch (Exception)
        {
            return new[] { "mainShow", "battleShow", "victoryShow", "failShow" };
        }
    }

    /// <summary>The game's config tables, by name. Core reads character and elfin rows through it.</summary>
    public static GameConfigView Config { get; } = new();

    /// <summary>A read-only view of one config table; only what Core asks for is exposed.</summary>
    public sealed class GameConfigView
    {
        public string String(string table, int index, string field) => GameConfig.String(table, index, field);
        public string? Text(string table) => GameConfig.Text(table);
    }

    // ------------------------------------------------------------------ Dump seams
    //
    // The Dump is one flow, but Core hangs five things off named steps inside it, and a step that is not
    // there means a feature silently stops working rather than failing loudly:
    //
    //   ExtractSkeleton               HexieBlock refuses a censored skeleton; DumpIsolation adds the
    //                                 attachments the slots are not currently showing (129 -> 197 parts
    //                                 for the Bassist);
    //   ExtractAttachment             HexieBlock refuses a censored part, one at a time;
    //   DumpCostumeResources          RoleSkins dumps the OTHER skins of the same character here;
    //   WriteAnchorMetadataIfMissing  RoleSkins keeps cat atlases out of it; AutoAnchor pins the anchor;
    //   CreateEditableRegionTexture   AutoAnchor cuts the original part out of the atlas to find where a
    //                                 painted-over drawing used to sit.
    //
    // They are written as real steps, not as empty hooks: each one does its own share of the work, so the
    // Dump still behaves the same when nothing is hooked onto it.

    /// <summary>
    /// Exports every part of one skeleton that belongs to <paramref name="atlasTexture"/>'s page. Returns
    /// how many images were written. Both of Core's Dump hooks sit on this.
    /// </summary>
    public static int ExtractSkeleton(Skeleton skeleton, int index, bool elfin)
    {
        if (skeleton == null || skeleton.Pointer == IntPtr.Zero) return 0;
        string costume = Roster.NameAt(GameRoster.Of(elfin), index);
        if (costume.Length == 0) return 0;

        int written = 0;
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string atlas, Texture2D texture) in SkeletonHooks.AtlasesOf(skeleton))
        {
            if (!done.Add(atlas)) continue;
            written += DumpSession.Run(skeleton, texture, costume, atlas, Layout, shadow: false).Written;
        }
        return written;
    }

    /// <summary>
    /// Exports one attachment, on its own. Used for the parts an animation swaps in, which no slot is
    /// showing while the Dump runs. Returns 1 when an image was written.
    /// </summary>
    public static int ExtractAttachment(Attachment attachment, int index, bool elfin)
    {
        if (attachment == null || attachment.Pointer == IntPtr.Zero) return 0;
        string costume = Roster.NameAt(GameRoster.Of(elfin), index);
        if (costume.Length == 0) return 0;
        try
        {
            AtlasRegion? region = PartCollector.RegionOf(attachment);
            if (region == null || !PartCollector.UsableMetadata(region)) return 0;
            PartSpec? part = PartCollector.Describe(attachment, string.Empty);
            if (part == null || part.Atlas.Length == 0) return 0;

            Texture2D? page = region.page?.rendererObject?.TryCast<Material>()?.mainTexture?.TryCast<Texture2D>();
            byte[]? atlas = TextureBridge.ToRgba(page, out int atlasWidth, out int atlasHeight);
            if (atlas == null) return 0;

            byte[] image = RegionGeometry.BuildPartImage(atlas, atlasWidth, atlasHeight, part.Stored, part.Degrees,
                part.Rotate, part.OffsetX, part.OffsetY, part.OriginalWidth, part.OriginalHeight,
                out int width, out int height);

            string folder = Path.Combine(Layout.Images, costume, part.Atlas);
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, DumpNaming.DisplayNameOf(part.AttachmentName) + ".png");
            if (File.Exists(file)) return 0;                       // a Dump fills in; it never overwrites
            if (!TextureBridge.SavePng(file, image, width, height)) return 0;
            WriteAnchorMetadataIfMissing(file, attachment, region, part.Atlas);
            return 1;
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"{Prefix} one part of {costume}: {exception.GetBaseException().Message}");
            return 0;
        }
    }

    /// <summary>
    /// A costume's resources beyond the skeletons it shows by default — the place Core adds a character's
    /// other skins. Main itself has nothing extra to write here, so it reports what the Dump wrote.
    /// </summary>
    public static int DumpCostumeResources(GameObject prefab, int index, bool elfin)
    {
        if (prefab == null) return 0;
        string costume = Roster.NameAt(GameRoster.Of(elfin), index);
        if (costume.Length == 0) return 0;

        int written = 0;
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Skeleton skeleton in SkeletonHooks.SkeletonsIn(prefab))
            foreach ((string atlas, Texture2D texture) in SkeletonHooks.AtlasesOf(skeleton))
            {
                if (!done.Add(atlas)) continue;
                written += DumpSession.Run(skeleton, texture, costume, atlas, Layout, shadow: false).Written;
            }
        return written;
    }

    /// <summary>
    /// Writes &lt;part&gt;.json next to a dumped image when it has none. Never overwrites: the anchor in an
    /// existing sidecar is the creator's, and moving it would move their part.
    /// </summary>
    public static void WriteAnchorMetadataIfMissing(string imagePath, Attachment attachment, AtlasRegion region, string textureName)
    {
        try
        {
            if (string.IsNullOrEmpty(imagePath) || region == null) return;
            string json = RegionAnchorFile.PathOf(imagePath);
            if (File.Exists(json)) return;
            // 2026-11-20: a folder that keeps its anchors in one anchors.json (a frame-by-frame skin -- the
            // author asked for one file, not one per frame) gets the entry written there. Never overwrites
            // an entry: the anchor in it is the author's, and moving it would move their part.
            bool folderFile = FolderAnchorFile.Exists(imagePath);
            if (folderFile && FolderAnchorFile.ReadEntry(imagePath) != null) return;
            PartSpec? part = attachment == null ? null : PartCollector.Describe(attachment, textureName ?? string.Empty);
            if (part == null) return;
            (float x, float y) = DumpPlan.CanvasCentre(part, part.OriginalWidth, part.OriginalHeight);
            if (folderFile) FolderAnchorFile.WriteEntry(imagePath, FolderAnchorFile.EntryForDump(x, y, region.degrees));
            else RegionAnchorFile.ForDump(x, y, region.degrees).Write(json);
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"{Prefix} anchor sidecar for {Path.GetFileName(imagePath)}: {exception.GetBaseException().Message}");
        }
    }

    /// <summary>
    /// The part's own image, cut out of a readable copy of the atlas page — the picture the Dump writes.
    /// Core uses it to find where a drawing sat before the canvas was enlarged.
    /// </summary>
    public static Texture2D? CreateEditableRegionTexture(Texture2D atlas, Attachment attachment, AtlasRegion region, string textureName)
    {
        try
        {
            if (atlas == null || attachment == null || region == null) return null;
            PartSpec? part = PartCollector.Describe(attachment, textureName ?? string.Empty);
            if (part == null) return null;
            byte[]? page = TextureBridge.ToRgba(atlas, out int pageWidth, out int pageHeight);
            if (page == null) return null;
            byte[] image = RegionGeometry.BuildPartImage(page, pageWidth, pageHeight, part.Stored, part.Degrees,
                part.Rotate, part.OffsetX, part.OffsetY, part.OriginalWidth, part.OriginalHeight,
                out int width, out int height);
            return TextureBridge.FromRgba(image, width, height, linear: true, name: part.AttachmentName);
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"{Prefix} region cut-out: {exception.GetBaseException().Message}");
            return null;
        }
    }

    /// <summary>Reads the selection files again, without touching the lists (Core calls this by name).</summary>
    public static void ReloadSelections() => Selections.Load();

    /// <summary>
    /// How many rows one of the GAME's config tables has: GetCount("character"), GetCount("elfin"),
    /// GetCount("character_English").
    ///
    /// 2026-11-20, and this one hid in plain sight. The authored Main gave this name a different meaning --
    /// "how many skins does this costume have" -- with the same signature, so every name check passed while
    /// three callers quietly got 0 back: the Preview (GetCount("character") built its character list, so
    /// the window opened EMPTY and showed nothing at all), SkinPackages' costume indexes, and the Sound
    /// tab's catalogue. A name that keeps its shape but changes its meaning is the one kind of break no
    /// parity tool can see; the meaning is restored here and the skin count has its own name below.
    /// </summary>
    public static int GetCount(string table) => GameConfig.RowCount(table);

    /// <summary>How many skins a costume has (its folder in SkinCreator\Images, plus mounted packages).</summary>
    public static int SkinCount(string costume) =>
        !string.IsNullOrEmpty(costume) && Skins.TryGetValue(costume, out List<string>? list) ? list.Count : 0;

    /// <summary>
    /// Fills <see cref="Skins"/>: every costume folder under SkinCreator\Images is that costume's own
    /// skin. Skins mounted from .sfskin packages are added by Core through the same field.
    /// </summary>
    public static void RebuildSkinList()
    {
        Skins.Clear();
        try
        {
            if (!SkinFiles.DirectoryExists(Layout.Images)) return;
            foreach (string folder in SkinFiles.GetDirectories(Layout.Images))
            {
                string costume = Path.GetFileName(folder);
                if (MainLayout.IsReservedFolderName(costume) || DumpNaming.IsCensoredName(costume)) continue;
                Skins[costume] = new List<string> { folder };
            }
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"{Prefix} skin list: {exception.GetBaseException().Message}");
        }
    }

    /// <summary>One field of the game's config, as text; empty when there is none.</summary>
    public static string ConfigString(string table, int index, string field) => GameConfig.String(table, index, field);

    /// <summary>
    /// The anchor of one image: where its reference point sits, in top-left pixels of its canvas.
    ///
    /// The SHAPE of this type is contract. SkinForger.Core's AutoAnchor finds it with
    /// AccessTools.Inner(host, "RegionAnchor") and reads AnchorX, AnchorY, DefaultAnchorX, DefaultAnchorY
    /// and FromSidecar by name — that is how the canvas-expansion rule ("enlarging the canvas never moves
    /// the anchor, unless the player changed it") tells a player's own value from the Dump's default.
    /// None of these names may change.
    /// </summary>
    public sealed class RegionAnchor
    {
        /// <summary>The anchor in use: the player's value when the sidecar has one, else the default.</summary>
        public float AnchorX { get; internal set; }
        public float AnchorY { get; internal set; }
        /// <summary>What the Dump would write for this image, from the attachment's own placement.</summary>
        public float DefaultAnchorX { get; internal set; }
        public float DefaultAnchorY { get; internal set; }
        /// <summary>True when the values came from the .json beside the image.</summary>
        public bool FromSidecar { get; internal set; }

        /// <summary>The replacement image's own size: the canvas the author actually drew on.</summary>
        public int NormalWidth { get; internal set; }
        public int NormalHeight { get; internal set; }

        /// <summary>The canvas the game shipped, before the author made it bigger.</summary>
        public int BaseNormalWidth { get; internal set; }
        public int BaseNormalHeight { get; internal set; }

        /// <summary>
        /// Extra displacement on top of the canvas change. Zero for SkinForger's own path; it exists
        /// because SkinForger.Core's OffsetCanvas adds a margin here when an anchor falls outside.
        /// </summary>
        public float OffsetX { get; internal set; }
        public float OffsetY { get; internal set; }

        /// <summary>
        /// The author's extra turn in degrees, positive = clockwise on screen: the sidecar's "rotation"
        /// and "Rotate" added together, which is how SkinForger.Core's PartTransforms reads them for a
        /// mesh. Main applies it to a quad itself.
        /// </summary>
        public float Rotation { get; internal set; }

        /// <summary>
        /// The author's per-image scale ("Scale" in the sidecar, 1 = unchanged). It multiplies the
        /// attachment's own scaleX / scaleY, exactly as the player's ReplacementDispatcher does.
        /// </summary>
        public float Scale { get; internal set; } = 1f;

        /// <summary>Always "top-left" for the sidecar format in use.</summary>
        public string Origin { get; internal set; } = RegionAnchorFile.TopLeftOrigin;

        /// <summary>
        /// Where the replacement ended up in the composed atlas, and how it is stored. SkinForger.Core's
        /// mesh canvas reads these to calibrate its band geometry (PlacementCalibration).
        /// </summary>
        public PackedRectangle? PackedRect { get; internal set; }
        public int Degrees { get; internal set; }

        /// <summary>A rectangle in the composed atlas, read by name from SkinForger.Core.</summary>
        public sealed class PackedRectangle
        {
            public int X { get; internal set; }
            public int Y { get; internal set; }
            public int Width { get; internal set; }
            public int Height { get; internal set; }
        }

        public override string ToString() =>
            $"({AnchorX:0.###}, {AnchorY:0.###}) default ({DefaultAnchorX:0.###}, {DefaultAnchorY:0.###})" +
            (FromSidecar ? " from sidecar" : string.Empty);
    }

    /// <summary>
    /// The anchor for an image, read from its sidecar when there is one and computed from the attachment
    /// otherwise. Core's AutoAnchor postfixes this method, so the parameter order is contract:
    /// (regionPath, attachment, region, textureName, originalWidth, originalHeight).
    /// </summary>
    public static RegionAnchor GetRegionAnchor(string regionPath, Attachment attachment, AtlasRegion region,
        string textureName, int originalWidth, int originalHeight)
    {
        RegionAttachment? quad = attachment?.TryCast<RegionAttachment>();
        // A quad's default anchor is computed FROM its x / y / rotation, and Main moves those by the
        // author's nudge. Reading them back off a moved attachment would answer a default that is wrong
        // by exactly the nudge, and the canvas placement -- anchor minus default -- would shift again on
        // every apply (the elfin alone is applied four times in one session). RuntimeAtlas remembers the
        // placement the GAME gave each quad before anything moved it; that is what this measures from.
        (float quadX, float quadY, float quadRotation) = quad == null
            ? (0f, 0f, 0f)
            : RuntimeAtlas.TryPristineQuad(attachment, out (float X, float Y, float Rotation) pristine)
                ? pristine
                : (quad.x, quad.y, quad.rotation);

        (float defaultX, float defaultY) = quad != null
            ? RegionGeometry.QuadAnchor(quadX, quadY, quad.width, quad.height, quadRotation, originalWidth, originalHeight)
            : RegionGeometry.DefaultAnchor(originalWidth, originalHeight);

        var anchor = new RegionAnchor
        {
            AnchorX = defaultX,
            AnchorY = defaultY,
            DefaultAnchorX = defaultX,
            DefaultAnchorY = defaultY
        };

        RegionAnchorFile? sidecar = string.IsNullOrEmpty(regionPath) ? null : RegionAnchorFile.ReadIfExists(regionPath);
        if (sidecar is { IsTopLeft: true })
        {
            anchor.AnchorX = sidecar.AnchorX;
            anchor.AnchorY = sidecar.AnchorY;
            anchor.FromSidecar = true;
            anchor.Origin = sidecar.Origin;
        }

        // The author's own nudge, from the same sidecar. SkinForger.Core reads OffsetX / OffsetY off this
        // object (OffsetCanvas, PartTransforms, DriftProbe) and moves the whole part by them, so leaving
        // them at zero quietly undoes every adjustment the author made by hand — which is what the
        // authored Main did until 2026-11-20. It is read even when the anchor is not in the top-left
        // convention, because a nudge is a nudge whatever the anchor says.
        if (sidecar != null)
        {
            anchor.OffsetX = sidecar.OffsetX;
            anchor.OffsetY = sidecar.OffsetY;
            // The Dump writes both names and the author may have edited either, so both count --
            // SkinForger.Core's PartTransforms adds them together for a mesh, and this is the same sum.
            anchor.Rotation = sidecar.Rotation + sidecar.RotateDegrees;
            if (sidecar.Scale > 0f) anchor.Scale = sidecar.Scale;
        }
        return anchor;
    }

    /// <summary>
    /// Whether the atlas this material draws is premultiplied. The game's Spine atlases are, so a
    /// replacement has to be premultiplied on the way in and a dumped image un-premultiplied on the way
    /// out; a material whose shader says "straight alpha" is left alone.
    /// </summary>
    public static bool ShouldUsePremultipliedAlpha(Material? material)
    {
        if (material == null || material.Pointer == IntPtr.Zero) return true;
        try
        {
            string shader = material.shader?.name ?? string.Empty;
            if (shader.IndexOf("Straight", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return !material.HasProperty("_StraightAlphaInput") || material.GetInt("_StraightAlphaInput") == 0;
        }
        catch (Exception) { return true; }
    }

    /// <summary>Premultiplies a readable texture in place (the atlas convention).</summary>
    public static void PremultiplyAlphaInPlace(Texture2D? texture)
    {
        if (texture == null || texture.Pointer == IntPtr.Zero) return;
        byte[]? pixels = TextureBridge.ToRgba(texture, out int width, out int height);
        if (pixels == null) return;
        PixelMath.PremultiplyInPlace(pixels, width, height);
        texture.SetPixels32(new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Color32>(
            TextureBridge.FlipFromTopLeft(pixels, width, height)));
        texture.Apply(false, false);
    }

    public override void OnGUI()
    {
        MainWindow.OnGUI();
        SkinForger.Preview.PreviewManager.Draw();   // the Preview window, drawn after this one
    }

    // ------------------------------------------------- window state SkinForger.Core reads (MainBridge)

    /// <summary>
    /// Whether SkinForger's window is on screen. The old Main had this as a private static field
    /// (<c>_showMenu</c>); Core read it by reflection. It is a property here because Core should not depend
    /// on a field's name or accessibility — its MainBridge asks for this member.
    /// </summary>
    public static bool WindowShown { get => MainWindow.Shown; set => MainWindow.Shown = value; }

    /// <summary>The key that opens the window; Core's one hotkey handler reads and clears it. See MainWindow.</summary>
    public static KeyCode MenuKey { get => MainWindow.MenuKey; set => MainWindow.MenuKey = value; }

    /// <summary>
    /// Where the window is, in screen pixels. Replaces the old Main's private <c>_windowRect</c>.
    /// </summary>
    public static Rect WindowRect => MainWindow.Rect;

    /// <summary>
    /// The Image page's scroll position. Replaces the old Main's private <c>_scroll</c>, which Core's wheel
    /// handling wrote by reflection.
    /// </summary>
    public static Vector2 WindowScroll { get => MainWindow.Scroll; set => MainWindow.Scroll = value; }

    /// <summary>
    /// Called on every texture SkinForger creates at runtime, after its sampling settings have been taken
    /// from the atlas it replaces. It deliberately does nothing: it is an extension point, so that
    /// SkinForger.Core has one named place to adjust runtime sampling (RuntimeTextureBilinearProbe patches
    /// it). The old Main's ApplySkinForgerTextureSettings served this purpose while also doing the work;
    /// here the work stays in RuntimeAtlas and only the hook is public.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static void ApplyTextureSettings(Texture2D texture)
    {
        _ = texture;
    }

    // ------------------------------------------- the loading seams SkinForger.Core patches (B2, 2026-11-18)
    //
    // These four are real steps of the authored Main's own loading path, not wrappers kept for Core's sake.
    // They are named, public and called exactly once each per part / per skeleton, because that is what lets
    // Core do its work without reaching into anything private:
    //
    //   EnumerateAttachmentContexts  HexieBlock removes censored parts from the list before any PNG is read
    //   GetExistingRegionPath        RoleSkins redirects to package files, LoadCaches remembers the answer
    //   GetTexture                   OffsetCanvas pads the loaded image so an off-canvas anchor still works
    //   ApplySkeleton                TouhouCompat syncs custom materials, HexieBlock learns censored skins
    //
    // Parameter NAMES are part of the contract: Harmony binds Core's patches by name (attachment, region,
    // textureName, path, skeleton, selected, elfin).

    /// <summary>
    /// Every textured attachment of a skeleton, with the region it draws from. The returned list is the one
    /// the replacement path and the Dump both walk, and it is mutable on purpose: SkinForger.Core's
    /// HexieBlock removes censored entries from it in a postfix, so nothing downstream can read them.
    /// </summary>
    public static List<AttachmentContext> EnumerateAttachmentContexts(Skeleton skeleton) =>
        PartCollector.Walk(skeleton);

    /// <summary>
    /// The image file a skin has for one part, or null when it leaves that part to the game.
    ///
    /// The answer comes from the skin folder's own manifest.json (matched by part identity, never by file
    /// name — two parts can share a name). SkinForger.Core postfixes this: the Skin Library's mounted
    /// packages answer for parts the folder itself has no file for, and an untouched dumped image of
    /// another skin is turned back into "absent" so the game's own art is kept.
    /// </summary>
    public static string? GetExistingRegionPath(string skin, Attachment attachment, AtlasRegion region, string textureName)
    {
        if (string.IsNullOrEmpty(skin) || attachment == null || region == null) return null;
        PartSpec? part = PartCollector.Describe(attachment, textureName);
        if (part == null) return null;
        return SkinIndexFor(skin).FileFor(part.Identity);
    }

    /// <summary>
    /// A replacement PNG as a texture. <paramref name="textureName"/> is the atlas it belongs to, which is
    /// what the censorship rules are keyed on; <paramref name="linear"/> matches the runtime atlas.
    ///
    /// The composition works on plain pixel buffers, so this detour through a Texture2D exists for one
    /// reason: it is the point where SkinForger.Core's OffsetCanvas replaces the image with a padded copy
    /// when an anchor puts the original outside the canvas. The caller destroys the texture.
    /// </summary>
    /// <summary>
    /// Reads the PNG that replaces one region and adds it to the composition. Returns whether it did.
    ///
    /// This exists under this name, with these first five parameters in this order, because SkinForger.Core
    /// binds to it two different ways and both have to work:
    ///
    ///   * HexieBlock's prefix binds BY NAME (<c>attachment, region, textureName, regionPath</c>) and
    ///     returns false with <c>__result</c> false to refuse a censored region outright, so the return
    ///     type must stay <c>bool</c>.
    ///   * OffsetCanvas' prefix binds BY POSITION (<c>__1 __2 __3 __4</c>) to learn which part the
    ///     GetTexture call inside this method belongs to; that context is the ONLY thing it needs from the
    ///     old Main, and without it the whole module stands down and anchors that fall outside the canvas
    ///     silently stop working (seen in the 2026-11-19 16:17 trial).
    ///
    /// In the old Main this method wrote pixels into the atlas one region at a time. Here it only reads the
    /// image and hands it to AtlasComposer, which places every replacement in one pass — the design that
    /// keeps the atlas from growing without end and parts from overlapping. The name is a contract; the
    /// per-region writing is not, and is deliberately not reproduced.
    /// </summary>
    public static bool ApplyRegionPngToAtlas(Texture2D targetAtlas, Attachment attachment, AtlasRegion region,
        string textureName, string regionPath, string identity, List<Replacement> into)
    {
        _ = targetAtlas;
        _ = attachment;
        _ = region;
        if (string.IsNullOrEmpty(regionPath) || into == null) return false;

        Texture2D? image = GetTexture(regionPath, textureName, linear: true);
        if (image == null) return false;
        byte[]? pixels = TextureBridge.ToRgba(image, out int width, out int height);
        UnityEngine.Object.Destroy(image);
        if (pixels == null) return false;

        into.Add(new Replacement(identity, pixels, width, height));
        return true;
    }

    /// <summary>
    /// Announces that one region's replacement has been placed, so a mesh can be grown to the author's
    /// bigger canvas. Returns nothing; SkinForger itself needs no work here.
    ///
    /// It exists for SkinForger.Core's ElfinMeshCanvas, which does the part SkinForger cannot express
    /// with an atlas alone: a mesh's shape is its VERTICES, so a canvas that grew needs vertices added,
    /// not a bigger rectangle. That module binds by position -- argument 0 the attachment, argument 2 the
    /// placement, argument 4 the region's file -- and reads ten values off the placement, which is why
    /// RegionAnchor carries the canvas sizes as well as the anchors.
    ///
    /// Six parameters because that is the arity Core looks for. Do not change the order.
    /// </summary>
    public static void ApplyRegionPlacementToAttachment(Attachment attachment, AtlasRegion region,
        RegionAnchor placement, string textureName, string regionPath, string skin)
    {
        _ = attachment;
        _ = region;
        _ = placement;
        _ = textureName;
        _ = regionPath;
        _ = skin;
    }

    public static Texture2D? GetTexture(string path, string textureName, bool linear)
    {
        _ = textureName;
        if (string.IsNullOrEmpty(path)) return null;
        byte[]? pixels = TextureBridge.LoadPng(path, out int width, out int height);
        return pixels == null ? null : TextureBridge.FromRgba(pixels, width, height, linear, Path.GetFileName(path));
    }

    /// <summary>
    /// Puts the selection on one skeleton: every atlas it draws from is rebuilt from the skin at
    /// <paramref name="selected"/>. Returns how many atlases were rebuilt.
    ///
    /// This is the single entry point for applying a skin to a skeleton, so Core sees each one exactly
    /// once — before (HexieBlock learns the skeleton's censored attachments) and after (TouhouCompat
    /// re-syncs custom materials, OriginalStateRestore notes what to undo).
    /// </summary>
    /// <summary>
    /// Rebuilds whichever of a skeleton's atlas pages belong to a skin the player has selected, working
    /// from the PAGE NAME instead of from the object's name. Returns how many pages were rebuilt.
    ///
    /// Needed because an object's name only says which costume it is on the character page. In a song the
    /// character's object is called "CharacterShow" and the elfin's says nothing at all, so the name-based
    /// path reaches neither. The page name is unambiguous: TryFindSelectedSkinByAtlas answers which
    /// selected skin folder owns it, and HexieBlock refuses censored pages inside that same call.
    /// </summary>
    public static int ApplySkeletonByAtlas(Skeleton skeleton, int instanceId)
    {
        if (skeleton == null || skeleton.Pointer == IntPtr.Zero) return 0;

        int applied = 0;
        ApplyingInstanceId = instanceId;
        try
        {
            foreach ((string atlas, Texture2D texture) in SkeletonHooks.AtlasesOf(skeleton))
            {
                if (!TryFindSelectedSkin(atlas, out string skin, out bool elfin) || skin.Length == 0) continue;
                if (TryBuildSynthesizedAtlasForSkeleton(skeleton, texture, atlas, skin, elfin) != null) applied++;
            }
        }
        finally { ApplyingInstanceId = 0; }
        return applied;
    }

    public static int ApplySkeleton(Skeleton skeleton, int index, int selected, bool elfin)
    {
        if (skeleton == null || skeleton.Pointer == IntPtr.Zero || selected < 0) return 0;

        string directory = SkinDirectoryFor(index, selected, elfin);
        if (directory.Length == 0) return 0;

        // Per-image "MovementAmplitude": how far a part is allowed to move in the game's animations. The
        // control has always been in the sidecar (RegionAnchorFile.MovementAmplitude) and the code that
        // honours it has always existed (AnimationAmplitudePatch) -- but the old build INJECTED the three
        // calls into this Main, and the authored Main did not even compile the runtime in, so the field
        // did nothing at all (2026-11-20). The three calls are here now, in the same places.
        SkinForger.AnimationAmplitude.AnimationAmplitudeManager.BeginSkeleton(skeleton);

        int applied = 0;
        foreach ((string atlas, Texture2D texture) in SkeletonHooks.AtlasesOf(skeleton))
        {
            // Through the build seam, never straight into RuntimeAtlas: eight SkinForger.Core modules
            // patch TryBuildSynthesizedAtlasForSkeleton, and skipping it would skip all of them.
            if (TryBuildSynthesizedAtlasForSkeleton(skeleton, texture, atlas, directory, elfin) != null) applied++;
        }
        if (applied > 0)
        {
            RegisterAmplitudeParts(skeleton, directory);
            SkinForger.AnimationAmplitude.AnimationAmplitudeManager.ApplyToSkeleton(skeleton, directory);
        }
        return applied;
    }

    /// <summary>
    /// Tells the amplitude manager which slot each replaced image belongs to. Only images whose sidecar
    /// actually asks for a different amplitude are registered, so a skin that never touches the control
    /// costs one pass over the slots and nothing else.
    /// </summary>
    private static void RegisterAmplitudeParts(Skeleton skeleton, string directory)
    {
        try
        {
            if (skeleton.slots == null) return;
            for (int index = 0; index < skeleton.slots.Count; index++)
            {
                Slot? slot = skeleton.slots.Items[index];
                Attachment? attachment = slot?.Attachment;
                if (attachment == null) continue;
                AtlasRegion? region = PartCollector.RegionOf(attachment);
                if (region == null) continue;
                string? file = GetExistingRegionPath(directory, attachment, region, region.page?.name ?? string.Empty);
                if (string.IsNullOrEmpty(file)) continue;
                RegionAnchorFile? sidecar = RegionAnchorFile.ReadIfExists(file!);
                if (sidecar == null || sidecar.MovementAmplitude == 0f) continue;
                SkinForger.AnimationAmplitude.AnimationAmplitudeManager.RegisterReplacement(skeleton, index, attachment, file!);
            }
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"{Prefix} movement amplitude: {exception.GetBaseException().Message}");
        }
    }

    /// <summary>
    /// Which renderer the current ApplySkeleton belongs to. ApplySkeleton keeps the four-parameter shape
    /// Core patches, so the renderer's instance id — which the composed atlas is cached and bound by —
    /// travels beside it instead of in the signature. SkeletonHooks sets it around the call.
    /// </summary>
    internal static int ApplyingInstanceId { get; set; }

    /// <summary>
    /// The folder a stored position means for a costume: an index into <see cref="Skins"/>, whose entry 0
    /// is the creator's own SkinCreator\Images folder and whose later entries are the skins Core mounted.
    /// Empty when the position names no skin.
    /// </summary>
    public static string SkinDirectoryFor(int index, int selected, bool elfin)
    {
        if (selected < 0) return string.Empty;
        string costume = GetCostumeName(index, elfin);
        if (costume.Length == 0) return string.Empty;
        return Skins.TryGetValue(costume, out List<string>? list) && selected < list.Count
            ? list[selected]
            : string.Empty;
    }

    /// <summary>
    /// A skin folder's index, read once. Files only change on a Reload or a Dump, both of which clear this
    /// (the same lifetime Core's LoadCaches uses for the same answers).
    /// </summary>
    private static readonly Dictionary<string, SkinIndex> Indexes = new(StringComparer.OrdinalIgnoreCase);

    private static SkinIndex SkinIndexFor(string directory)
    {
        if (Indexes.TryGetValue(directory, out SkinIndex? index)) return index;
        // SkinFiles.Exists, not File.Exists: this is the lookup GetExistingRegionPath answers from, so a
        // packaged skin's parts are found here or nowhere.
        index = SkinIndex.Read(directory, SkinFiles.Exists, RegionManifest.Read);
        Indexes[directory] = index;
        return index;
    }

    /// <summary>Forgets every skin index (a Reload, a Dump, or a package mounted / removed).</summary>
    public static void ForgetSkinIndexes() => Indexes.Clear();

    // ------------------------------------------- the atlas and lookup seams Core patches (B3a, 2026-11-18)

    /// <summary>
    /// Builds the runtime atlas for one page of one skeleton and returns it, or null when nothing was
    /// replaced. This is the busiest hook in SkinForger.Core — eight modules patch it — and the five
    /// parameters, in this order and with these names, are what they bind to:
    /// <paramref name="skeleton"/>, the game's atlas, <paramref name="textureName"/> (the page),
    /// <paramref name="skin"/> (the folder being applied) and <paramref name="elfin"/>.
    ///
    /// What they do here: UntouchedRegionRebase captures the regions no image replaces before the atlas
    /// grows under them, OriginalStateRestore opens its undo transaction, BattleAtlasEdgeIsolation and
    /// ElfinFreshPackedRect note whether this is an elfin, HexieBlock refuses censored pages outright.
    /// </summary>
    public static Texture2D? TryBuildSynthesizedAtlasForSkeleton(Skeleton skeleton, Texture2D atlas,
        string textureName, string skin, bool elfin)
    {
        if (skeleton == null || skeleton.Pointer == IntPtr.Zero || atlas == null) return null;
        if (string.IsNullOrEmpty(skin)) return null;

        SkinIndex index = SkinIndexFor(skin);
        if (index.Problem != null)
        {
            MelonLogger.Warning($"{Prefix} {skin}: {index.Problem}");
            return null;
        }

        AtlasApplied result = RuntimeAtlas.Apply(skeleton, atlas, textureName,
            SkinIndex.Layer(new[] { index }), ApplyingInstanceId, skin, elfin);
        foreach (string problem in result.Problems) MelonLogger.Warning($"{Prefix} {textureName}: {problem}");
        return result.Replaced > 0 ? RuntimeAtlas.Composed_Of(ApplyingInstanceId, textureName) : null;
    }

    /// <summary>
    /// The material an attachment is drawn with (its atlas page's). AutoAnchor reads the original atlas
    /// through it, so it is asked for by this name with this one parameter.
    /// </summary>
    public static Material? GetMaterial(Attachment attachment)
    {
        AtlasRegion? region = PartCollector.RegionOf(attachment);
        AtlasPage? page = region?.page;
        Material? material = page?.rendererObject?.TryCast<Material>();
        return material != null && material.Pointer != IntPtr.Zero ? material : null;
    }

    /// <summary>
    /// A readable copy of a texture the game shipped compressed. The caller owns it. AutoAnchor asks for
    /// this by name so it does not have to repeat the read-back itself.
    /// </summary>
    public static Texture2D? MakeReadable(Texture2D texture) => TextureBridge.ReadableCopy(texture);

    /// <summary>
    /// The folder a stored position names, as Core asks for it: the same answer as
    /// <see cref="SkinDirectoryFor"/>, under the name four Core modules look up (RoleSkins, DumpIsolation,
    /// FastReload, the packed-rect repair).
    /// </summary>
    public static string GetSkinPath(int index, int position, bool elfin) =>
        SkinDirectoryFor(index, position, elfin);

    /// <summary>
    /// The folder a Dump of this costume writes into. FastReload reads it to know which files to watch.
    /// </summary>
    public static string GetDumpSkinDirectory(int index, bool elfin)
    {
        string costume = GetCostumeName(index, elfin);
        return costume.Length == 0 ? string.Empty : Path.Combine(Layout.Images, costume);
    }

    /// <summary>
    /// The subfolder names inside a costume's folder that belong to the creator's own skin rather than
    /// being skins of their own. RoleSkins adds the icon folders to this set in a postfix — without it,
    /// "SkinSwitchIcons" showed up as a skin button of its own (2026-11-15).
    /// </summary>
    public static HashSet<string> GetRootSkinDataDirectories(string costumeFolder)
    {
        _ = costumeFolder;
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase) { DumpNaming.ThumbnailFolder };
    }

    /// <summary>
    /// Whether some costume is currently showing a skin that owns this atlas page, and which folder that
    /// is. HexieBlock refuses the lookup outright for censored pages.
    /// </summary>
    public static bool TryFindSelectedSkinByAtlas(string atlasName, out string skin) =>
        TryFindSelectedSkin(atlasName, out skin, out _);

    /// <summary>
    /// The same lookup, also reporting whether the skin belongs to an elfin. A separate method because the
    /// two-parameter shape above is what SkinForger.Core binds to (HexieBlock), so its signature is not
    /// ours to extend.
    /// </summary>
    internal static bool TryFindSelectedSkin(string atlasName, out string skin, out bool isElfin)
    {
        skin = string.Empty;
        isElfin = false;
        if (string.IsNullOrEmpty(atlasName)) return false;
        foreach (bool elfin in new[] { false, true })
            foreach (int index in Selections.Replaced(elfin))
            {
                string directory = SkinDirectoryFor(index, Selections.Get(index, elfin), elfin);
                if (directory.Length == 0) continue;
                SkinIndex candidate = SkinIndexFor(directory);
                if (candidate.Problem != null) continue;
                foreach (string identity in candidate.Identities)
                    if (RegionManifest.AtlasOf(identity).Equals(atlasName, StringComparison.OrdinalIgnoreCase))
                    {
                        skin = directory;
                        isElfin = elfin;
                        return true;
                    }
            }
        return false;
    }

    /// <summary>
    /// Makes sure the Main has read its files. Core patches this (FastReload hangs its incremental state
    /// off it, LazyCreatorFolder makes the creator folder appear on first use), so it stays a named method
    /// even though starting up is what normally does the work.
    /// </summary>
    public static void EnsureInitialized()
    {
        if (_initialized) return;
        Selections.Load();
        RebuildSkinList();
        ForgetSkinIndexes();
        _initialized = true;
    }

    // ---------------------------------------------------------------- the contract Core patches

    /// <summary>
    /// The window's contents. Core patches this method by name: it enters its drawing scope here (the
    /// Chinese interface) and appends its own pages, so it must stay a static method of this type.
    /// </summary>
    public static void DrawWindow(int windowId) => MainWindow.Draw(windowId);

    /// <summary>The position a character / elfin is set to: -1 for the game's own art.</summary>
    public static int GetSelected(int index, bool elfin) => Selections.Get(index, elfin);

    /// <summary>
    /// Writes one set of selections to one file. THIS is the shape SkinForger.Core patches, in four
    /// places: CreatorWindowHooks rewrites the dictionary in a prefix before it is written, and
    /// PackageLoaderHooks records which skin each costume ended up on in a postfix. Every change Main
    /// makes to a selection goes through here, so Core never misses one.
    /// </summary>
    /// <remarks>
    /// The first parameter is named <c>source</c> because CreatorWindowHooks' prefix declares that name
    /// and Harmony binds by name. Checked by Analysis/param-name-parity.py. Do not rename.
    /// </remarks>
    public static void SaveSelection(Dictionary<int, int> source, string path) =>
        SelectionStore.SaveTo(source, path);

    /// <summary>
    /// Sets one costume's choice and saves it. Main's own path for a click in the window; it ends in
    /// <see cref="SaveSelection"/> so Core's patches run.
    /// </summary>
    public static void SetSelection(int index, int position, bool elfin)
    {
        Selections.Live(elfin)[index] = position;
        SaveSelection(Selections.Live(elfin), elfin ? _savedElfinsPath : _savedCharactersPath);
    }

    /// <summary>Puts every character and elfin back to the game's own art.</summary>
    public static void DeselectAll() => Selections.Clear();

    /// <summary>The English name of a character / elfin, which is how a costume is identified everywhere.</summary>
    public static string GetCostumeName(int index, bool elfin) => Roster.NameAt(GameRoster.Of(elfin), index);

    /// <summary>
    /// The name to SHOW for a costume: the game’s own display name, falling back to the resource name.
    /// SelectionThumbnail asks for it by name when the resource name is empty (2026-11-20: the authored
    /// Main never had it, so that fallback quietly produced nothing).
    /// </summary>
    public static string GetDisplayName(int index, bool elfin)
    {
        string shown = ConfigString(elfin ? "elfin" : "character", index, elfin ? "name" : "cosName");
        return string.IsNullOrEmpty(shown) ? GetCostumeName(index, elfin) : shown;
    }

    /// <summary>How many rows the game's list has, including any that are not shown.</summary>
    /// <summary>How many rows a roster list has. Named apart from GetCount(string) on purpose: an overload
    /// pair made reflection by name alone throw "Ambiguous match found", which took the Preview down.</summary>
    public static int RosterCount(bool elfin) => GameRoster.RowCount(elfin);

    /// <summary>
    /// What one skin folder is called on the costume's row: "Edited" for the creator's own folder,
    /// otherwise the folder's own name.
    ///
    /// THIS is the shape SkinForger.Core patches (two parameters, named costume and skinPath, which is
    /// how Harmony binds its postfixes): PackageLoaderHooks makes the button show the package's file
    /// name, and CreatorWindowHooks returns a marker that hides Skin Library skins from this window.
    /// Every label the window draws comes from here, so neither of them can be bypassed.
    /// </summary>
    public static string GetSkinLabel(string costume, string skinPath)
    {
        if (string.IsNullOrEmpty(skinPath)) return WindowModel.OriginalLabel;
        string edited = costume.Length > 0 ? Path.Combine(Layout.Images, costume) : string.Empty;
        return WindowModel.SamePath(skinPath, edited) ? WindowModel.EditedLabel : WindowModel.LabelOf(skinPath);
    }

    /// <summary>
    /// What a stored position is called on screen. Main's own by-position lookup; it answers through
    /// <see cref="GetSkinLabel"/> so a Core postfix applies here too.
    /// </summary>
    public static string SkinLabelAt(int index, int position, bool elfin)
    {
        if (position == SelectionStore.Original) return WindowModel.OriginalLabel;
        string costume = GetCostumeName(index, elfin);
        string directory = SkinDirectoryFor(index, position, elfin);
        return directory.Length == 0 ? WindowModel.OriginalLabel : GetSkinLabel(costume, directory);
    }

    /// <summary>
    /// Draws one list of the Image page: every character (or elfin), its Original and Dump buttons, and
    /// one button per skin it can be set to.
    ///
    /// The SHAPE of this method is contractual, not just its name, because SkinForger.Core rewrites it:
    ///
    ///   * CreatorWindowHooks finds the call to GetSkinLabel and replaces the
    ///     GUILayout.Button(string, GUILayoutOption[]) call that FOLLOWS it with its own, which draws
    ///     nothing for a Skin Library skin and draws "Edited" green while the Library skin carrying the
    ///     creator's work is in use. So a skin's label must be fetched through GetSkinLabel here, and
    ///     there must be exactly one Button call for skins in this method.
    ///   * FastReload looks for the "this position is already showing, skip it" branch and adds its
    ///     reapply check to it, which is why the current position is read through the selection
    ///     dictionary's indexer rather than a local.
    ///
    /// One parameter (which list), as the old Main had: Core binds it by that arity.
    /// </summary>
    public static void DrawSelectionList(bool elfin)
    {
        Dictionary<int, int> selection = Selections.Live(elfin);
        Color content = GUI.contentColor;

        foreach (CostumeRow row in MainWindow.RowsOf(elfin))
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(row.Name, GUILayout.Width(150f));

            // Original, then Dump: the two buttons that are not skins, in the order they always had.
            SkinChoice original = row.Choices[0];
            GUI.contentColor = row.IsOriginal ? Chosen : content;
            if (GUILayout.Button(original.Label, GUILayout.Width(110f))) MainWindow.Choose(row, original);
            GUI.contentColor = content;
            if (GUILayout.Button("Dump", GUILayout.Width(90f))) MainWindow.Dump(row);

            for (int position = 1; position < row.Choices.Count; position++)
            {
                SkinChoice choice = row.Choices[position];
                int current = selection.ContainsKey(row.Index) ? selection[row.Index] : SelectionStore.Original;
                bool showing = current == choice.Position;

                string label = GetSkinLabel(row.Name, choice.Directory);
                GUILayoutOption[] options = { GUILayout.Width(150f) };
                GUI.contentColor = showing ? Chosen : content;
                if (GUILayout.Button(label, options)) MainWindow.Choose(row, choice);
                GUI.contentColor = content;
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }
        GUI.contentColor = content;
    }

    /// <summary>The colour a button showing the current choice is drawn in.</summary>
    private static readonly Color Chosen = new(0.45f, 0.95f, 0.5f);

    /// <summary>
    /// The key that opens the window, from Menukey.txt. False when the name is not one Unity knows; the key
    /// is Insert in that case, so a mistyped file still leaves the window reachable.
    /// </summary>
    public static bool TryParseMenuKey(string? name, out KeyCode key)
    {
        string normalized = MenuKeySettings.Normalize(name);
        if (normalized.Length > 0 && Enum.TryParse(normalized, ignoreCase: true, out key)) return true;
        key = KeyCode.Insert;
        return false;
    }

    /// <summary>
    /// Keeps the frame-rate cap in place. The game raises its own target now and then, so this is checked
    /// about once a second rather than set once.
    /// </summary>
    private static void UpdateFpsLimiterGuard()
    {
        if (FpsLimit <= 0 || Time.unscaledTime < _nextFpsCheck) return;
        _nextFpsCheck = Time.unscaledTime + 1f;
        if (FpsLimiterSettings.ShouldApply(Application.targetFrameRate, FpsLimit))
            Application.targetFrameRate = FpsLimit;
    }
}
