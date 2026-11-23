using Il2CppSpine;
using Il2CppSpine.Unity;
using MelonLoader;
using SkinForger.Main.Pure;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SkinForger.Main.Unity;

/// <summary>What SkinForger knows about one Spine object in the scene.</summary>
/// <param name="InstanceId">Unity's instance id. Addresses get reused, so renderers are identified by this
/// and never by their pointer (SF日志, R4-10: a Preview renderer was mistaken for another one that way).</param>
public sealed record SpineTarget(int InstanceId, string ObjectName, PrefabOwner? Owner, bool Graphic)
{
    public bool Known => Owner != null;
}

/// <summary>
/// Where SkinForger meets the game: the four places a Spine object appears or is rebuilt.
///
///   SkeletonRenderer.Awake        a character or an elfin in a 3D scene (the main menu, battle)
///   SkeletonRenderer.Initialize   the same renderer rebuilding its skeleton
///   SkeletonGraphic.Awake         a Spine object in the UI (elfins, the victory / fail panels)
///   CharacterApply.OnEnable       the character page applying a costume
///
/// These are the same four the old Main uses, and they are facts about the game rather than anything
/// inherited from it. SkinForger.Core patches all four by name, so they stay static methods of
/// SkinForgerMod with these signatures; the work itself lives here.
///
/// What they do today: work out which costume the object belongs to and remember it. Replacing its images
/// is the next stage of the port (group I) and will read this registry — an object has to be recognised
/// before anything can be swapped into it, and getting the recognition right is most of that job.
/// </summary>
public static class SkeletonHooks
{
    private const string Prefix = "[SkinForge][Main][Hooks]";

    private static readonly Dictionary<int, SpineTarget> Targets = new();
    private static readonly HashSet<string> Logged = new(StringComparer.Ordinal);

    /// <summary>Every Spine object seen so far, newest last.</summary>
    public static IReadOnlyCollection<SpineTarget> Seen => Targets.Values;

    /// <summary>What is known about a renderer, or null when it has not been seen.</summary>
    public static SpineTarget? Of(int instanceId) => Targets.TryGetValue(instanceId, out SpineTarget? target) ? target : null;

    /// <summary>Forgets everything (a Reload, or a scene change that invalidated the ids).</summary>
    public static void Forget()
    {
        Targets.Clear();
        Applied.Clear();

        // A UI object keeps drawing whatever texture it was handed. RuntimeAtlas.Forget destroys the
        // composed textures, so a graphic still pointing at one would be drawing a destroyed texture
        // (black, or magenta) the moment the player goes back to the game's own art. Hand them back
        // their own picture first.
        foreach (SkeletonGraphic graphic in Bound)
            try
            {
                if (graphic != null && graphic.Pointer != IntPtr.Zero) graphic.OverrideTexture = null;
            }
            catch (Exception) { }
        Bound.Clear();

        // These verdicts were reached against the SELECTION as it was. A Reload is exactly when it
        // changed, so a name that owned nothing before may own something now: keeping the verdicts would
        // mean a newly chosen costume is never looked at again.
        Irrelevant.Clear();
        _unclaimed = 0;
        _nextSweep = 0f;
        _quietSweeps = 0;
    }

    /// <summary>UI objects whose texture SkinForger replaced, so it can be given back.</summary>
    private static readonly List<SkeletonGraphic> Bound = new();

    /// <summary>
    /// Whether a Spine object that appears is given its skin straight away.
    ///
    /// ON since 2026-11-19. It was off through the port — the idea being that a skin would be applied only
    /// when the player clicked one, with someone watching — and that is exactly why the 16:17 trial showed
    /// "nothing is replaced": characters in the menu, in a song and on the result panels never pass through
    /// the window, so they kept the game's own art. A player's selection is meant to be in effect the
    /// moment a character appears, which is what the four hooks below are for.
    /// </summary>
    public static bool AutoApply { get; set; } = true;

    /// <summary>(instance id, position) already applied, so an Awake storm does not recompose per frame.</summary>
    private static readonly HashSet<(int Instance, int Position)> Applied = new();

    /// <summary>
    /// Applies the current selection to every Spine object of one costume that is on screen. Returns how
    /// many atlases were rebuilt. This is what the window calls after a click.
    /// </summary>
    public static int ApplyCostume(int index, bool elfin, MainLayout layout, SelectionStore selections)
    {
        string costume = Roster.NameAt(GameRoster.Of(elfin), index);
        if (costume.Length == 0) return 0;

        int position = selections.Get(index, elfin);
        if (position == SelectionStore.Original)
        {
            // Going back to the game's own art: the composed textures are dropped, and the game rebuilds
            // its own skeletons. Undoing the region changes in place is the job of OriginalStateRestore,
            // which the authored Main will compile in (CONTRACT.md).
            //
            // Forget() FIRST, and the whole of it. RuntimeAtlas.Forget destroys every composed texture,
            // including the ones belonging to the OTHER costumes that are still on a skin, so:
            //   * a UI object still holding one would draw a destroyed texture, and
            //   * the "already applied" memo would stop those other costumes from ever being rebuilt.
            // Clearing both means the next sweep puts the others back within a couple of seconds.
            Forget();
            RuntimeAtlas.Forget();
            MelonLogger.Msg($"{Prefix} {costume}: back to the game's own art");
            return 0;
        }

        // Which folder the stored position names. SkinForgerMod owns this, because the list it indexes
        // into (Skins) is the one SkinForger.Core adds the Skin Library's mounted packages to.
        string directory = SkinForgerMod.SkinDirectoryFor(index, position, elfin);
        if (directory.Length == 0)
        {
            MelonLogger.Warning($"{Prefix} {costume}: position {position} names no skin");
            return 0;
        }

        int applied = 0;
        foreach (SpineTarget target in Targets.Values.Where(t => t.Owner != null && t.Owner.Elfin == elfin && t.Owner.Index == index).ToList())
            applied += ApplyTo(target, index, position, elfin);

        MelonLogger.Msg($"{Prefix} {costume}: {WindowModel.LabelOf(directory)}, {applied} atlas(es) rebuilt on " +
                        $"{(applied == 0 ? "no" : string.Empty)} object(s) on screen");
        return applied;
    }

    /// <summary>
    /// Applies a costume's selected skin to one Spine object. Every atlas it draws from is rebuilt.
    ///
    /// The work per skeleton goes through SkinForgerMod.ApplySkeleton, which is the method
    /// SkinForger.Core patches: HexieBlock reads the skeleton's censored skins before it runs, and
    /// TouhouCompat re-syncs custom materials after. Calling it exactly once per skeleton is the
    /// contract, so nothing here may rebuild an atlas behind its back.
    /// </summary>
    public static int ApplyTo(SpineTarget target, int index, int selected, bool elfin,
        Component? component = null)
    {
        int applied = 0;
        try
        {
            // A SkeletonRenderer is prepared before and bound after: the composed atlas belongs to THIS
            // renderer, not to the page material its character's other renderers share. A SkeletonGraphic
            // binds its texture a different way (overrideTexture) and is left to its own path.
            SkeletonRenderer? renderer = target.Graphic ? null
                : component?.TryCast<SkeletonRenderer>() ?? RendererOf(target.InstanceId);
            if (renderer != null) SkinForger.AtlasOwnershipFix.AtlasOwnershipManager.Prepare(renderer);

            SkinForgerMod.ApplyingInstanceId = target.InstanceId;
            try
            {
                foreach (Skeleton skeleton in SkeletonsOf(target, component))
                {
                    int one = SkinForgerMod.ApplySkeleton(skeleton, index, selected, elfin);
                    if (one > 0 && target.Graphic) BindGraphic(component, target.InstanceId, skeleton);
                    applied += one;
                }
            }
            finally { SkinForgerMod.ApplyingInstanceId = 0; }

            if (renderer != null && applied > 0) SkinForger.AtlasOwnershipFix.AtlasOwnershipManager.Bind(renderer);
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"{Prefix} {target.ObjectName}: {exception.GetBaseException().Message}");
        }
        return applied;
    }

    /// <summary>The renderer with this instance id, or null when it is gone or is a UI graphic.</summary>
    private static SkeletonRenderer? RendererOf(int instanceId)
    {
        foreach (SkeletonRenderer renderer in UnityEngine.Object.FindObjectsOfType<SkeletonRenderer>())
            if (renderer.GetInstanceID() == instanceId) return renderer;
        return null;
    }

    /// <summary>The skeletons of a Spine object that is already in the scene, found by its instance id.</summary>
    private static IEnumerable<Skeleton> SkeletonsOf(SpineTarget target, Component? component)
    {
        // The component the caller already has, first: no scene search, and it works for an object on an
        // inactive page, which is where the elfin lives until its page is shown.
        if (component != null && component.Pointer != IntPtr.Zero)
        {
            Skeleton? direct = Safe(() => component.TryCast<SkeletonRenderer>()?.Skeleton)
                            ?? Safe(() => component.TryCast<SkeletonGraphic>()?.Skeleton);
            if (direct != null) { yield return direct; yield break; }
        }
        foreach (Skeleton skeleton in SkeletonsByScan(target)) yield return skeleton;
    }

    private static IEnumerable<Skeleton> SkeletonsByScan(SpineTarget target)
    {
        foreach (SkeletonRenderer renderer in UnityEngine.Object.FindObjectsOfType<SkeletonRenderer>())
            if (renderer.GetInstanceID() == target.InstanceId)
            {
                Skeleton? skeleton = Safe(() => renderer.Skeleton);
                if (skeleton != null) yield return skeleton;
                yield break;
            }
        foreach (SkeletonGraphic graphic in UnityEngine.Object.FindObjectsOfType<SkeletonGraphic>())
            if (graphic.GetInstanceID() == target.InstanceId)
            {
                Skeleton? skeleton = Safe(() => graphic.Skeleton);
                if (skeleton != null) yield return skeleton;
                yield break;
            }
    }

    // ---------------------------------------------------------------- the four hooks

    public static void OnSkeletonRenderer(SkeletonRenderer? renderer) => Remember(renderer?.gameObject, renderer?.GetInstanceID(), graphic: false, renderer);

    public static void OnSkeletonRendererInitialize(SkeletonRenderer? renderer) => Remember(renderer?.gameObject, renderer?.GetInstanceID(), graphic: false, renderer);

    public static void OnSkeletonGraphic(SkeletonGraphic? graphic) => Remember(graphic?.gameObject, graphic?.GetInstanceID(), graphic: true, graphic);

    public static void OnCharacterApply(GameObject? root)
    {
        if (root == null || root.Pointer == IntPtr.Zero) return;
        // The character page builds its costume under one object; every Spine object below it is ours.
        foreach (SkeletonRenderer renderer in root.GetComponentsInChildren<SkeletonRenderer>(true))
            Remember(renderer.gameObject, renderer.GetInstanceID(), graphic: false, renderer);
        foreach (SkeletonGraphic graphic in root.GetComponentsInChildren<SkeletonGraphic>(true))
            Remember(graphic.gameObject, graphic.GetInstanceID(), graphic: true, graphic);
    }

    /// <summary>
    /// Notices a Spine object that did not arrive through one of the four hooks, and gives it its skin.
    ///
    /// The four hooks only fire when an object is CREATED. The game reuses objects: the elfin page's elfin
    /// has already woken up by the time the player opens it, so Awake never fires again and Initialize is
    /// not called either. In the 16:49 trial that is exactly what happened — the player opened the elfin
    /// page (the log has its music) and not one elfin object was ever seen, while all three characters
    /// were replaced correctly.
    ///
    /// SkinForger.Core sweeps the scene every couple of seconds and asks Main's Scan which costume each
    /// Spine object belongs to; routing that question through here is what catches everything the four
    /// creation hooks miss. The work is deduplicated per (object, position), so a sweep that finds nothing
    /// new costs a dictionary lookup.
    /// </summary>
    public static void Notice(GameObject? gameObject)
    {
        if (gameObject == null || gameObject.Pointer == IntPtr.Zero) return;
        try
        {
            foreach (SkeletonRenderer renderer in gameObject.GetComponentsInChildren<SkeletonRenderer>(true))
                Remember(renderer.gameObject, renderer.GetInstanceID(), graphic: false, renderer);
            foreach (SkeletonGraphic graphic in gameObject.GetComponentsInChildren<SkeletonGraphic>(true))
                Remember(graphic.gameObject, graphic.GetInstanceID(), graphic: true, graphic);
        }
        catch (Exception exception)
        {
            if (Logged.Add("notice|" + exception.GetBaseException().Message))
                MelonLogger.Warning($"{Prefix} {exception.GetBaseException().Message}");
        }
    }

    private static float _nextSweep;
    private static int _sweeps;

    /// <summary>How many sweeps in a row found nothing, which is what the interval backs off on.</summary>
    private static int _quietSweeps;

    /// <summary>How many distinct censuses have been logged; the line is capped so a song cannot flood it.</summary>
    private static int _censuses;

    /// <summary>
    /// Looks over the whole scene for Spine objects the four creation hooks never saw, and gives them
    /// their skin. Called from OnUpdate, at most every <paramref name="seconds"/>.
    ///
    /// Why this is needed, and why the previous attempt was wrong: the hooks only fire when an object is
    /// CREATED, and the game reuses objects — the elfin page's elfin has already woken up before the player
    /// opens it. The 2026-11-19 16:57 trial proved that no elfin object is ever created while SkinForger is
    /// watching: all three characters were replaced and not one elfin object appeared in the log at all,
    /// not even as "no costume claims this object".
    ///
    /// The fix before this one routed the same work through Main's Scan, on the belief that SkinForger.Core
    /// sweeps the scene and calls it every couple of seconds. It does not: Core's own sweep ([Seen]) looks
    /// for TEXTURES for the Dump page and never asks Scan about scene objects. So nothing called it and the
    /// elfin stayed unreplaced. Watching the scene here depends on nothing outside this assembly.
    ///
    /// Cost: two FindObjectsOfType calls per sweep, and a dictionary lookup per object found. Objects
    /// already applied are skipped by the (object, position) memo, so a quiet scene costs almost nothing.
    /// </summary>
    public static void Sweep(float seconds = 2f)
    {
        if (!AutoApply || Time.unscaledTime < _nextSweep) return;

        // A song puts thousands of Spine objects in the scene, and this walks all of them. Once nothing
        // new has turned up for a while there is nothing to be gained from looking every two seconds, so
        // the interval backs off to at most 16s. Any hook firing, or a Reload, starts it over.
        float wait = seconds * (1 << Math.Min(_quietSweeps / 4, 3));
        _nextSweep = Time.unscaledTime + wait;
        int before = Applied.Count;
        int roots = 0, renderers = 0, graphics = 0;
        try
        {
            // From the scene roots with GetComponentsInChildren(true), NOT FindObjectsOfType<T>().
            // Two reasons, both learned the hard way on 2026-11-19:
            //   * FindObjectsOfType<T> skips components on INACTIVE objects, and a page that is not the
            //     one on screen is exactly that;
            //   * under IL2CPP a generic FindObjectsOfType<T> can quietly come back empty for a type like
            //     SkeletonGraphic, and nothing in this build had ever proved it does not. The 17:08 trial
            //     ran a sweep built on it for 90 seconds with the elfin page open and found nothing at all.
            // GetComponentsInChildren(true) is the call the CharacterApply hook already uses successfully.
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                Scene scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    roots++;
                    if (root == null || root.Pointer == IntPtr.Zero) continue;
                    foreach (SkeletonRenderer renderer in root.GetComponentsInChildren<SkeletonRenderer>(true))
                    {
                        renderers++;
                        if (!AlreadyApplied(renderer.GetInstanceID()))
                            Remember(renderer.gameObject, renderer.GetInstanceID(), graphic: false, renderer);
                    }
                    foreach (SkeletonGraphic graphic in root.GetComponentsInChildren<SkeletonGraphic>(true))
                    {
                        graphics++;
                        if (!AlreadyApplied(graphic.GetInstanceID()))
                            Remember(graphic.gameObject, graphic.GetInstanceID(), graphic: true, graphic);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            if (Logged.Add("sweep|" + exception.GetBaseException().Message))
                MelonLogger.Warning($"{Prefix} sweep: {exception.GetBaseException().Message}");
        }
        _sweeps++;

        // A census whenever what the sweep can see changes. Without it, "the elfin is not replaced" and
        // "the sweep cannot see any Spine object at all" look identical in the log -- which is what cost
        // the 17:08 trial.
        // Capped: during a song the counts change on nearly every sweep, so an uncapped "log each new
        // census" would write a line every couple of seconds and grow the dedup set without end. The first
        // twenty distinct ones are what answer "can the sweep see anything at all", which is what this
        // line exists for.
        string census = $"{roots} root(s), {renderers} renderer(s), {graphics} graphic(s)";
        if (_censuses < 20 && Logged.Add("census|" + census))
        {
            _censuses++;
            MelonLogger.Msg($"{Prefix} sweep sees {census}");
        }
        if (Applied.Count != before)
        {
            _quietSweeps = 0;
            MelonLogger.Msg($"{Prefix} sweep {_sweeps}: {Applied.Count - before} object(s) the hooks had not seen");
        }
        else _quietSweeps++;
    }

    private static bool AlreadyApplied(int instanceId) =>
        Applied.Any(pair => pair.Instance == instanceId);

    /// <summary>
    /// Records a Spine object and, when its costume has a skin, applies it.
    ///
    /// <paramref name="component"/> is the SkeletonRenderer or SkeletonGraphic itself. It is passed in, not
    /// looked up again, because looking it up means FindObjectsOfType -- which skips inactive objects and,
    /// for SkeletonGraphic under IL2CPP, had never been shown to return anything at all. The caller always
    /// has the component in hand, so there is no reason to go looking.
    /// </summary>
    private static void Remember(GameObject? gameObject, int? instanceId, bool graphic,
        Component? component = null)
    {
        if (gameObject == null || gameObject.Pointer == IntPtr.Zero || instanceId == null) return;
        try
        {
            string name = gameObject.name ?? string.Empty;
            PrefabOwner? owner = GamePrefabs.OwnerOf(name);
            var target = new SpineTarget(instanceId.Value, name, owner, graphic);
            Targets[instanceId.Value] = target;

            if (owner == null)
            {
                // The object's NAME does not say which costume it is. That is the normal case outside the
                // character page: in a song the character's object is called "CharacterShow" and the elfin
                // has no recognisable name at all, which is why neither was ever replaced in a song and the
                // elfin was never replaced anywhere (2026-11-19 18:17 trial, 1346 unclaimed names and not
                // one elfin among them). So ask the atlas instead: every page a skeleton draws from is
                // looked up against the skins the player has selected.
                ApplyByAtlas(target, component);
                return;
            }

            ApplyOnAppear(target, component);
        }
        catch (Exception exception)
        {
            if (Logged.Add("error|" + exception.GetBaseException().Message))
                MelonLogger.Warning($"{Prefix} {exception.GetBaseException().Message}");
        }
    }

    /// <summary>
    /// Gives a Spine object that has just appeared the skin its costume is set to. This is what makes a
    /// selection visible in the menu, in a song and on the result panels without the player opening the
    /// window — the 16:17 trial had every piece of the composition working and still showed the game's own
    /// art everywhere, because nothing called it.
    ///
    /// Each (object, position) pair is composed once: Awake and Initialize both arrive for the same
    /// renderer, and a song can rebuild one repeatedly.
    /// </summary>
    /// <summary>
    /// Points a UI Spine object at the composed atlas.
    ///
    /// A SkeletonRenderer draws through its page material, which the ownership manager binds. A
    /// SkeletonGraphic does NOT: it draws through its own OverrideTexture, so composing an atlas and
    /// updating the regions leaves it drawing the game's original picture with UVs that now address a
    /// different, taller texture. The elfin is a SkeletonGraphic, so without this it could only ever look
    /// unchanged or scrambled.
    /// </summary>
    private static void BindGraphic(Component? component, int instanceId, Skeleton skeleton)
    {
        SkeletonGraphic? graphic = component?.TryCast<SkeletonGraphic>();
        if (graphic == null || graphic.Pointer == IntPtr.Zero) return;
        foreach ((string atlas, Texture2D _) in AtlasesOf(skeleton))
        {
            Texture2D? composed = RuntimeAtlas.Composed_Of(instanceId, atlas);
            if (composed == null) continue;
            graphic.OverrideTexture = composed;
            if (!Bound.Any(known => known != null && known.Pointer == graphic.Pointer)) Bound.Add(graphic);
            if (Logged.Add("graphic|" + atlas))
                MelonLogger.Msg($"{Prefix} {atlas}: the composed atlas is now this UI object's texture");
            return;   // one page per graphic
        }
    }

    /// <summary>Object names that were examined and own none of the selected skins. Keyed by NAME, because
    /// a song spawns thousands of note objects that share a handful of names.</summary>
    private static readonly HashSet<string> Irrelevant = new(StringComparer.Ordinal);
    private static int _unclaimed;

    /// <summary>
    /// Gives a skin to an object whose name says nothing, by asking which selected skin owns the atlas
    /// pages its skeleton draws from.
    ///
    /// This is the only way to reach two things the name-based path can never reach:
    ///   * the character in a song, whose object is called "CharacterShow" (its atlas is char_1_rock, and
    ///     Bassist's folder holds 57 parts for it -- they had simply never been applied, which is what the
    ///     wrong-looking hair in a song was);
    ///   * the elfin, anywhere, which has no show-prefab name on its object at all.
    ///
    /// Each name is examined once: a song spawns thousands of note objects, and walking a skeleton is not
    /// free, so a name that owns nothing is remembered as irrelevant and never walked again.
    /// </summary>
    private static void ApplyByAtlas(SpineTarget target, Component? component)
    {
        if (!AutoApply || target.ObjectName.Length == 0) return;
        if (Irrelevant.Contains(target.ObjectName)) return;
        if (AlreadyApplied(target.InstanceId)) return;

        bool matched = false;
        try
        {
            // The same prepare / bind as the name-based path. Without it a composed atlas is built and
            // then never reaches the renderer, which would look exactly like "still not replaced".
            SkeletonRenderer? renderer = target.Graphic ? null
                : component?.TryCast<SkeletonRenderer>() ?? RendererOf(target.InstanceId);
            if (renderer != null) SkinForger.AtlasOwnershipFix.AtlasOwnershipManager.Prepare(renderer);

            foreach (Skeleton skeleton in SkeletonsOf(target, component))
            {
                int applied = SkinForgerMod.ApplySkeletonByAtlas(skeleton, target.InstanceId);
                if (applied <= 0) continue;
                if (target.Graphic) BindGraphic(component, target.InstanceId, skeleton);
                matched = true;
                Applied.Add((target.InstanceId, AtlasMatch));
                MelonLogger.Msg($"{Prefix} {target.ObjectName}: {applied} atlas(es) matched by page name");
            }

            if (renderer != null && matched) SkinForger.AtlasOwnershipFix.AtlasOwnershipManager.Bind(renderer);
        }
        catch (Exception exception)
        {
            if (Logged.Add("byatlas|" + exception.GetBaseException().Message))
                MelonLogger.Warning($"{Prefix} {target.ObjectName}: {exception.GetBaseException().Message}");
        }

        if (matched) return;
        Irrelevant.Add(target.ObjectName);
        _unclaimed++;
        // One line per 200, not one per object: the 18:17 trial wrote 1346 of them during a song.
        if (_unclaimed % 200 == 1)
            MelonLogger.Msg($"{Prefix} {_unclaimed} object name(s) own none of the selected skins " +
                            $"(latest \"{target.ObjectName}\")");
    }

    /// <summary>The position recorded for an object whose skin was found by its atlas rather than by a
    /// stored selection, so the memo can tell the two apart.</summary>
    private const int AtlasMatch = int.MinValue;

    private static void ApplyOnAppear(SpineTarget target, Component? component)
    {
        if (!AutoApply || target.Owner == null) return;
        try
        {
            int position = SkinForgerMod.Selections.Get(target.Owner.Index, target.Owner.Elfin);
            if (position == SelectionStore.Original) return;
            if (!Applied.Add((target.InstanceId, position))) return;

            int applied = ApplyTo(target, target.Owner.Index, position, target.Owner.Elfin, component);
            if (applied > 0)
            {
                MelonLogger.Msg($"{Prefix} {target.ObjectName}: applied position {position}, {applied} atlas(es)");
                return;
            }

            Applied.Remove((target.InstanceId, position));   // nothing composed: let a later rebuild retry
            // Say so once per object. A claimed object that composes nothing used to be completely silent,
            // which is how "the elfin is not replaced" looked identical in the log to "no elfin was there".
            if (Logged.Add("empty|" + target.ObjectName + "|" + position))
                MelonLogger.Warning($"{Prefix} {target.ObjectName}: position {position} replaced nothing " +
                                    $"(costume {(target.Owner.Elfin ? "elfin" : "character")} {target.Owner.Index}, " +
                                    $"folder \"{SkinForgerMod.SkinDirectoryFor(target.Owner.Index, position, target.Owner.Elfin)}\")");
        }
        catch (Exception exception)
        {
            if (Logged.Add("apply|" + exception.GetBaseException().Message))
                MelonLogger.Warning($"{Prefix} {target.ObjectName}: {exception.GetBaseException().Message}");
        }
    }

    // ---------------------------------------------------------------- the Dump

    /// <summary>
    /// Dumps one costume: every one of its show prefabs is brought into the scene for a moment, and each
    /// atlas its skeletons draw from is written out. Returns what happened, per atlas.
    /// </summary>
    public static List<DumpResult> DumpCostume(int index, bool elfin, MainLayout layout, bool shadow = true)
    {
        var results = new List<DumpResult>();
        string costume = Roster.NameAt(GameRoster.Of(elfin), index);
        if (costume.Length == 0)
        {
            MelonLogger.Warning($"{Prefix} no {(elfin ? "elfin" : "character")} at index {index}");
            return results;
        }

        List<string> prefabs = GamePrefabs.PrefabsOf(index, elfin);
        if (prefabs.Count == 0)
        {
            MelonLogger.Warning($"{Prefix} {costume}: the config names no show prefab");
            return results;
        }

        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string prefab in prefabs)
        {
            GamePrefabs.Spawn(prefab, copy =>
            {
                foreach (Skeleton skeleton in SkeletonsIn(copy))
                    foreach ((string atlas, Texture2D texture) in AtlasesOf(skeleton))
                    {
                        if (!done.Add(atlas)) continue;
                        results.Add(DumpSession.Run(skeleton, texture, costume, atlas, layout, shadow));
                    }
            });
        }

        int written = results.Sum(r => r.Written);
        MelonLogger.Msg($"{Prefix} {costume}: {written} images from {results.Count} atlas(es) of {prefabs.Count} prefab(s)" +
                        (shadow ? " (shadow folder)" : string.Empty));
        return results;
    }

    /// <summary>The skeletons of a spawned prefab, renderers and UI graphics alike.</summary>
    public static IEnumerable<Skeleton> SkeletonsIn(GameObject root)
    {
        foreach (SkeletonRenderer renderer in root.GetComponentsInChildren<SkeletonRenderer>(true))
        {
            Skeleton? skeleton = Safe(() => renderer.Skeleton);
            if (skeleton != null) yield return skeleton;
        }
        foreach (SkeletonGraphic graphic in root.GetComponentsInChildren<SkeletonGraphic>(true))
        {
            Skeleton? skeleton = Safe(() => graphic.Skeleton);
            if (skeleton != null) yield return skeleton;
        }
    }

    /// <summary>
    /// The atlas pages a skeleton draws from, with their textures: the page carries the material it is
    /// drawn with, and the material carries the texture.
    /// </summary>
    public static IEnumerable<(string Atlas, Texture2D Texture)> AtlasesOf(Skeleton skeleton)
    {
        var seen = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        SkeletonData? data = skeleton.Data;
        int slotCount = data?.slots?.Count ?? 0;
        if (data == null) yield break;

        var entries = new Il2CppSystem.Collections.Generic.List<Skin.SkinEntry>();
        for (int s = 0; s < (data.skins?.Count ?? 0); s++)
        {
            Skin? skin = data.skins!.Items[s];
            if (skin == null || DumpNaming.IsCensoredName(skin.name)) continue;
            for (int slot = 0; slot < slotCount; slot++)
            {
                entries.Clear();
                try { skin.GetAttachments(slot, entries); } catch (Exception) { continue; }
                for (int e = 0; e < entries.Count; e++)
                {
                    AtlasRegion? region = PartCollector.RegionOf(entries[e]?.Attachment);
                    AtlasPage? page = region?.page;
                    string name = page?.name ?? string.Empty;
                    if (page == null || name.Length == 0 || DumpNaming.IsCensoredName(name)) continue;
                    if (seen.ContainsKey(name)) continue;
                    Texture2D? texture = TextureOf(page);
                    if (texture != null) seen[name] = texture;
                }
            }
        }
        foreach ((string name, Texture2D texture) in seen) yield return (BareName(name), texture);
    }

    /// <summary>The texture an atlas page is drawn with.</summary>
    private static Texture2D? TextureOf(AtlasPage page)
    {
        try
        {
            Material? material = page.rendererObject?.TryCast<Material>();
            Texture2D? texture = material?.mainTexture?.TryCast<Texture2D>();
            return texture != null && texture.Pointer != IntPtr.Zero ? texture : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>"1_rock_main_show.png" -> "1_rock_main_show": the folder name a dump writes.</summary>
    private static string BareName(string value)
    {
        int dot = value.LastIndexOf('.');
        return dot > 0 ? value[..dot] : value;
    }

    private static T? Safe<T>(Func<T?> get) where T : class
    {
        try { return get(); } catch (Exception) { return null; }
    }
}
