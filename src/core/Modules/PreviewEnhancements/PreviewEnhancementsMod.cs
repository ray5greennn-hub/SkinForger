using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppSpine;
using Il2CppSpine.Unity;
using MelonLoader;
using UnityEngine;
using Event = UnityEngine.Event;

namespace SkinForger.PreviewEnhancements;

/// <summary>
/// Preview improvements on top of SkinForger Main's PreviewManager (Main is not modified):
///  1. Opens on the character selected in the game (DataHelper.selectedRoleIndex).
///  2. Spine skin: the context template's initialSkinName is applied (Bunny battle: "origin"); without one, a skin
///     named like "normal" is preferred, then the one resolving the most missing setup attachments (Racer battle).
///  3. Form/skin row under the Context selector when the preview skeleton has two or more skins (Racer: Normal / Mad).
///  4. Mouse wheel zooms around the cursor, left drag pans, right click resets (on the preview image only).
///  5. Mouse wheel scrolls the open Character / Context / Animation list.
///  6. Changing character keeps the current context (Main stays Main, Battle stays Battle).
/// </summary>
public sealed class PreviewEnhancementsMod : global::SkinForger.Core.SfModule
{
    private const string Prefix = "[SkinForge][PreviewEnhancements]";
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static Type? _preview;
    private static PropertyInfo? _selectedRole;

    public override void OnInitializeMelon()
    {
        try
        {
            _preview = AccessTools.TypeByName("SkinForger.Preview.PreviewManager") ?? throw new TypeLoadException("PreviewManager");
            Type dataHelper = AccessTools.TypeByName("Il2CppAssets.Scripts.Database.DataHelper") ?? throw new TypeLoadException("DataHelper");
            _selectedRole = dataHelper.GetProperty("selectedRoleIndex", Flags) ?? throw new MissingMemberException("DataHelper.selectedRoleIndex");

            PatchPostfix("BuildCharacters", nameof(BuildCharactersPostfix));
            PatchPostfix("BuildCurrentPreviewAttempt", nameof(BuildAttemptPostfix));
            PatchPostfix("FitCameraToAnimation", nameof(FitCameraPostfix));
            PatchPrefix("LoadContextsAndPreview", nameof(LoadContextsPrefix));
            PatchPostfix("LoadContextsAndPreview", nameof(LoadContextsPostfix));
            Type host = AccessTools.TypeByName("SkinForger.SkinForgerMod") ?? throw new TypeLoadException("SkinForgerMod");
            _configString = host.GetMethods(Flags).Single(m => m.Name == "ConfigString" && m.GetParameters().Length == 3);
            _characterShows = host.GetField("CharacterShows", Flags);
            _displayName = Method("ContextDisplayName");
            _buildCurrentPreview = Method("BuildCurrentPreview");
            PatchPrefix("BuildCurrentPreview", nameof(BuildCurrentPreviewPrefix));
            PatchPrefix("DrawCharacterSelector", nameof(CharacterListPrefix));
            PatchPrefix("DrawContextSelector", nameof(ContextSelectorReplacement));
            PatchPrefix("DrawAnimationSelector", nameof(AnimationListPrefix));
            PatchPostfix("DrawCharacterSelector", nameof(CharacterListPostfix));
            PatchPostfix("DrawAnimationSelector", nameof(AnimationListPostfix));
            HarmonyInstance.Patch(Method("DrawAnimationSelector"),
                transpiler: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(PreviewEnhancementsMod), nameof(AnimationArrowsTranspiler))));
            _playSelected = Method("PlaySelected");
            _fitCamera = Method("FitCameraToAnimation");
            HarmonyInstance.Patch(Method("DrawPreviewWindow"),
                prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(PreviewEnhancementsMod), nameof(WindowPrefix))),
                finalizer: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(PreviewEnhancementsMod), nameof(WindowFinalizer))),
                transpiler: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(PreviewEnhancementsMod), nameof(PreviewRectTranspiler))));
            MelonLogger.Msg($"{Prefix} ready version=0.7.0 animationArrows=on defaultCharacter=game-selected skin=template-first formRow=on zoomPan=image-only " +
                "listScroll=per-list keepCharacterListOpen=on keepAnimationListOpen=on keepContext=on windowScroll=on resultContexts=Results[victory,fail]");
        }
        catch (Exception exception)
        {
            MelonLogger.Error($"{Prefix} DISABLED {exception.GetBaseException().Message}");
        }
    }

    // ------------------------------------------------------------ reflection helpers

    private static MethodInfo Method(string name) =>
        _preview!.GetMethods(Flags).Single(m => m.Name == name);

    private void PatchPrefix(string name, string prefix) =>
        HarmonyInstance.Patch(Method(name), prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(PreviewEnhancementsMod), prefix)));

    private void PatchPostfix(string name, string postfix) =>
        HarmonyInstance.Patch(Method(name), postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(PreviewEnhancementsMod), postfix)));

    private static object? Get(string field) => _preview!.GetField(field, Flags)?.GetValue(null);
    private static void Set(string field, object value) => _preview!.GetField(field, Flags)?.SetValue(null, value);
    private static List<string> Names(string field) => Get(field) as List<string> ?? new List<string>();
    private static int Int(string field) => Get(field) is int value ? value : -1;
    private static bool Bool(string field) => Get(field) is true;

    // ------------------------------------------------------------ which renderer the Preview is showing
    //
    // 2026-11-20. These features were written against the old Preview, which kept a SkeletonAnimation in
    // "_skeletonAnimation". The authored Preview clones the GAMEPLAY prefab, which is SkeletonMecanim
    // driven by a Unity Animator, and keeps it in "_skeletonRenderer" -- so every `Get("_skeletonAnimation")
    // is not SkeletonAnimation` test failed and the Form row, the skin choice and the result-material fix
    // all silently did nothing. Everything they actually need (the skeleton, its data, the component) is on
    // SkeletonRenderer, which both types are. Only Spine's AnimationState is specific to SkeletonAnimation,
    // and the two places that need it say so.

    /// <summary>The Preview's renderer, whichever kind it is, or null when nothing is being shown.</summary>
    private static SkeletonRenderer? Renderer()
    {
        if (Get("_skeletonAnimation") is SkeletonAnimation animation && animation.Pointer != IntPtr.Zero) return animation;
        if (Get("_skeletonRenderer") is SkeletonRenderer renderer && renderer.Pointer != IntPtr.Zero) return renderer;
        return null;
    }

    /// <summary>The Preview's SkeletonAnimation, when it is one (only Spine's AnimationState needs this).</summary>
    private static SkeletonAnimation? Animation()
    {
        SkeletonRenderer? renderer = Renderer();
        return renderer == null ? null : ((Il2CppObjectBase)renderer).TryCast<SkeletonAnimation>();
    }

    /// <summary>The skeleton on show, with its data; false when there is nothing to work on.</summary>
    private static bool Showing(out SkeletonRenderer renderer, out Skeleton skeleton, out SkeletonData data)
    {
        renderer = null!;
        skeleton = null!;
        data = null!;
        SkeletonRenderer? found = Renderer();
        Skeleton? bones = found?.Skeleton;
        SkeletonData? theData = bones?.data;
        if (found == null || bones == null || theData == null) return false;
        renderer = found;
        skeleton = bones;
        data = theData;
        return true;
    }

    // ------------------------------------------------------------ 1. default character

    private static void BuildCharactersPostfix()
    {
        try
        {
            int role = Convert.ToInt32(_selectedRole!.GetValue(null));
            if (Get("CharacterIds") is not List<int> ids) return;
            int index = ids.IndexOf(role);
            if (index < 0) { MelonLogger.Msg($"{Prefix} DEFAULT_CHARACTER role={role} not-in-preview-list"); return; }
            Set("_characterIndex", index);
            var names = Names("CharacterNames");
            MelonLogger.Msg($"{Prefix} DEFAULT_CHARACTER role={role} index={index} character={(index < names.Count ? names[index] : "?")}");
        }
        catch (Exception exception) { MelonLogger.Warning($"{Prefix} DEFAULT_CHARACTER skipped: {exception.GetBaseException().Message}"); }
    }

    // ------------------------------------------------------------ 6. keep context across characters

    private static string? _pendingContext;

    private static string? _lastWantedContext;

    private static void LoadContextsPrefix()
    {
        var names = Names("ContextNames");
        int index = Int("_contextIndex");
        _pendingContext = index >= 0 && index < names.Count ? names[index] : null;
        _lastWantedContext = _pendingContext;
    }

    // ------------------------------------------------------------ 8. Victory / Fail contexts

    private static MethodInfo? _configString, _displayName, _buildCurrentPreview;
    private static FieldInfo? _characterShows;

    /// <summary>
    /// Main already iterates CharacterShows (mainShow, battleShow, victoryShow, failShow), but keeps only prefabs with a
    /// SkeletonRenderer. The result characters are UI Spine (SkeletonGraphic), so Victory and Fail were silently dropped.
    /// Their SkeletonDataAsset is the same kind Preview already plays with its own SkeletonAnimation, so they are added
    /// here (template = null; Main's CopyRendererSettings and the skin rule handle a null template).
    /// </summary>
    private static void LoadContextsPostfix()
    {
        try
        {
            if (Get("ContextFields") is not List<string> fields || Get("ContextNames") is not List<string> names ||
                Get("ContextResources") is not List<string> resources || Get("ContextAssets") is not List<SkeletonDataAsset> assets ||
                Get("ContextTemplates") is not List<SkeletonRenderer> templates || _characterShows?.GetValue(null) is not string[] shows ||
                Get("CharacterIds") is not List<int> ids) return;
            int characterIndex = Int("_characterIndex");
            if (characterIndex < 0 || characterIndex >= ids.Count) return;
            int id = ids[characterIndex];
            var manager = Il2CppPeroTools2.Resources.ResourcesManager.instance;
            if (manager == null) return;
            int added = 0;
            for (int order = 0; order < shows.Length; order++)
            {
                string field = shows[order];
                if (fields.Contains(field)) continue;
                // 2026-11-16: follow the skin chosen in the Skin row (other character skins).
                string resource = global::SkinForger.SkinPackages.RoleSkins.PreviewShow(id, field)
                                  ?? _configString?.Invoke(null, new object[] { "character", id, field }) as string ?? string.Empty;
                if (resource.Length == 0) continue;
                GameObject? prefab = manager.LoadFromName<GameObject>(resource);
                if (prefab == null) continue;
                SkeletonGraphic? best = null;
                foreach (SkeletonGraphic graphic in prefab.GetComponentsInChildren<SkeletonGraphic>(true))
                {
                    string objectName = graphic.gameObject.name ?? string.Empty;
                    if (objectName.Contains("ghost", StringComparison.OrdinalIgnoreCase) || objectName.Contains("shadow", StringComparison.OrdinalIgnoreCase) ||
                        objectName.Contains("cat", StringComparison.OrdinalIgnoreCase)) continue;
                    if (graphic.skeletonDataAsset == null) continue;
                    best = graphic;
                    break;
                }
                // A cat skin's own objects are named "..._cat": take the first usable one when the filter left none.
                if (best == null)
                    foreach (SkeletonGraphic graphic in prefab.GetComponentsInChildren<SkeletonGraphic>(true))
                        if (graphic.skeletonDataAsset != null && !(graphic.gameObject.name ?? string.Empty).Contains("ghost", StringComparison.OrdinalIgnoreCase)) { best = graphic; break; }
                SkeletonDataAsset? asset = best?.skeletonDataAsset;
                if (best != null && !string.IsNullOrEmpty(best.initialSkinName)) ExtraSkins[resource] = best.initialSkinName;
                if (asset == null || asset.GetSkeletonData(true) == null)
                {
                    MelonLogger.Msg($"{Prefix} CONTEXT_EXTRA_SKIPPED field={field} resource={resource} reason=no-skeleton-graphic");
                    continue;
                }
                int at = fields.Count(f => Array.IndexOf(shows, f) is int o && o >= 0 && o < order);   // keep CharacterShows order
                at = Math.Min(at, fields.Count);
                string display = _displayName?.Invoke(null, new object[] { field }) as string ?? field;
                fields.Insert(at, field);
                names.Insert(at, display);
                resources.Insert(at, resource);
                assets.Insert(at, asset);
                templates.Insert(at, null!);
                int current = Int("_contextIndex");
                if (current >= at) Set("_contextIndex", current + 1);
                added++;
                MelonLogger.Msg($"{Prefix} CONTEXT_ADDED name={display} field={field} resource={resource} asset={asset.name} skin={(ExtraSkins.TryGetValue(resource, out string? extraSkin) ? extraSkin : "<default>")} (SkeletonGraphic)");
            }
            if (added == 0 || _lastWantedContext == null) return;
            int wanted = names.FindIndex(n => string.Equals(n, _lastWantedContext, StringComparison.OrdinalIgnoreCase));
            if (wanted >= 0 && wanted != Int("_contextIndex"))
            {
                Set("_contextIndex", wanted);
                MelonLogger.Msg($"{Prefix} KEEP_CONTEXT {_lastWantedContext}");
                _buildCurrentPreview?.Invoke(null, null);
            }
        }
        catch (Exception exception) { MelonLogger.Warning($"{Prefix} CONTEXT_EXTRA skipped: {exception.GetBaseException().Message}"); }
        finally { _lastWantedContext = null; }
    }

    private static void BuildCurrentPreviewPrefix()
    {
        string? wanted = _pendingContext;
        _pendingContext = null;
        if (wanted == null) return;
        var names = Names("ContextNames");
        int index = names.FindIndex(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index != Int("_contextIndex"))
        {
            Set("_contextIndex", index);
            MelonLogger.Msg($"{Prefix} KEEP_CONTEXT {wanted}");
        }
    }

    // ------------------------------------------------------------ 2. skin selection + 3. form row

    private static List<Skin> Skins(SkeletonData data)
    {
        var list = new List<Skin>();
        var skins = data.skins;
        for (int i = 0; i < skins.Count; i++) { Skin s = skins.Items[i]; if (s != null && s.Pointer != data.defaultSkin?.Pointer) list.Add(s); }
        // Steam release only: censored (hexie) Spine skins are never offered or shown.
        list.RemoveAll(s => global::SkinForger.HexieBlock.HexiePolicy.IsHexieName(s.name));
        return list;
    }

    private static string TemplateSkin()
    {
        int index = Int("_contextIndex");
        if (index >= 0 && Get("ContextTemplates") is IList templates && index < templates.Count &&
            templates[index] is SkeletonRenderer template && template.Pointer != IntPtr.Zero)
            return template.initialSkinName ?? string.Empty;
        // Extra (SkeletonGraphic) contexts have no template: the skin their own object sets (2026-11-16: the cat
        // Victory shares the main-show skeleton and showed big white eyes with the default skin).
        if (index >= 0 && Get("ContextResources") is IList resources && index < resources.Count &&
            resources[index] is string resource && ExtraSkins.TryGetValue(resource, out string? skin))
            return skin;
        return string.Empty;
    }

    private static readonly Dictionary<string, string> ExtraSkins = new(StringComparer.OrdinalIgnoreCase);

    private static int Missing(SkeletonData data, Skin? skin, out int total)
    {
        int missing = 0;
        total = 0;
        Skin? defaultSkin = data.defaultSkin;
        var slots = data.slots;
        for (int i = 0; i < slots.Count; i++)
        {
            string? name = slots.Items[i]?.attachmentName;
            if (string.IsNullOrEmpty(name)) continue;
            total++;
            if ((skin?.GetAttachment(i, name) ?? defaultSkin?.GetAttachment(i, name)) == null) missing++;
        }
        return missing;
    }

    /// <summary>Display order of skins: template skin, then "normal", then the rest by name.</summary>
    private static List<Skin> Ordered(SkeletonData data, string templateSkin) =>
        Skins(data).OrderBy(s => string.Equals(s.name, templateSkin, StringComparison.Ordinal) ? 0 :
            s.name.Contains("normal", StringComparison.OrdinalIgnoreCase) ? 1 : 2).ThenBy(s => s.name, StringComparer.OrdinalIgnoreCase).ToList();

    private static void ApplySkin(SkeletonRenderer animation, Skin skin)
    {
        Skeleton? skeleton = animation.Skeleton;
        if (skeleton == null) return;
        skeleton.SetSkin(skin);
        skeleton.SetSlotsToSetupPose();
        // A SkeletonAnimation can be re-posed right away; a Mecanim one is posed by its Animator on the
        // next frame, which is soon enough and is what the gameplay object does anyway.
        ((Il2CppObjectBase)animation).TryCast<SkeletonAnimation>()?.AnimationState?.Apply(skeleton);
    }

    private static void BuildAttemptPostfix()
    {
        ResetView();
        FixResultPreviewMaterial();
        try
        {
            if (!Showing(out SkeletonRenderer animation, out Skeleton skeleton, out SkeletonData data)) return;
            string context = Int("_contextIndex") is int ci && ci >= 0 && ci < Names("ContextNames").Count ? Names("ContextNames")[ci] : "?";
            string templateSkin = TemplateSkin();
            List<Skin> skins = Ordered(data, templateSkin);
            string skinNames = string.Join(",", skins.Select(s => s.name));
            int missingNow = Missing(data, skeleton.skin, out int total);

            Skin? chosen = null;
            string reason;
            Skin? template = skins.FirstOrDefault(s => string.Equals(s.name, templateSkin, StringComparison.Ordinal));
            if (template != null) { chosen = template; reason = "template"; }
            else if (missingNow > 0)
            {
                // First by display order that improves; else the best overall.
                chosen = skins.FirstOrDefault(s => Missing(data, s, out _) < missingNow);
                reason = "fills-missing";
            }
            else reason = "default-complete";
            if (chosen == null && global::SkinForger.HexieBlock.HexiePolicy.IsHexieName(skeleton.skin?.name))
            {
                // The prefab itself started on a censored skin: move to the first normal one.
                chosen = skins.FirstOrDefault();
                reason = "leave-censored-skin";
                if (chosen == null)
                {
                    animation.gameObject.SetActive(false);
                    MelonLogger.Msg($"{Prefix} SKIN_HIDDEN context={context}: only censored skins are available");
                    return;
                }
            }

            if (chosen == null || chosen.Pointer == skeleton.skin?.Pointer)
            {
                MelonLogger.Msg($"{Prefix} SKIN_OK context={context} current={skeleton.skin?.name ?? "<default>"} reason={reason} template={Show(templateSkin)} " +
                    $"missing={missingNow}/{total} skins=[{skinNames}]");
                return;
            }
            ApplySkin(animation, chosen);
            MelonLogger.Msg($"{Prefix} SKIN_APPLIED context={context} skin={chosen.name} reason={reason} missing={missingNow}->{Missing(data, chosen, out _)} of {total} " +
                $"template={Show(templateSkin)} skins=[{skinNames}]");
        }
        catch (Exception exception) { MelonLogger.Warning($"{Prefix} SKIN skipped: {exception.GetBaseException().Message}"); }
    }

    /// <summary>Form/skin buttons under the Context selector (inside Main's Preview window function).</summary>
    private static void DrawFormRow()
    {
        try
        {
            if (!Showing(out SkeletonRenderer animation, out Skeleton skeleton, out SkeletonData data)) return;
            List<Skin> skins = Ordered(data, TemplateSkin());
            if (skins.Count < 2) return;
            GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
            GUILayout.Label("Form", GUILayout.Width(90));
            Color previous = GUI.contentColor;
            for (int i = 0; i < skins.Count; i++)
            {
                Skin skin = skins[i];
                bool current = skeleton.skin != null && skeleton.skin.Pointer == skin.Pointer;
                GUI.contentColor = current ? Color.green : previous;
                if (GUILayout.Button($"{i + 1}: {skin.name}", Array.Empty<GUILayoutOption>()) && !current)
                {
                    ApplySkin(animation, skin);
                    MelonLogger.Msg($"{Prefix} FORM_SELECTED skin={skin.name}");
                }
            }
            GUI.contentColor = previous;
            GUILayout.EndHorizontal();
        }
        catch (Exception exception) { MelonLogger.Warning($"{Prefix} FORM_ROW skipped: {exception.GetBaseException().Message}"); }
    }

    // ------------------------------------------------------------ 5. list wheel scrolling

    /// <summary>
    /// Main's three selector lists share one static scroll position (_selectorScroll). When two lists are open, the short
    /// one (e.g. Context with 3 rows) clamps it back to 0 every frame, so the long one (Character) could not scroll.
    /// Each list now keeps its own position (loaded into _selectorScroll before Main draws it, read back after), and the
    /// mouse wheel scrolls only the list under the cursor (its rect is captured right after Main's EndScrollView).
    /// </summary>
    private sealed class ListState
    {
        internal ListState(string expanded, string names) { Expanded = expanded; Names = names; }
        internal string Expanded { get; }
        internal string Names { get; }
        internal Vector2 Scroll;
        internal Rect View;
        internal bool HasView;
        internal int IndexBefore;
        internal bool WasExpanded;
    }

    private static readonly ListState CharacterList = new("_characterExpanded", "CharacterNames");
    private static readonly ListState ContextList = new("_contextExpanded", "ContextNames");
    private static readonly ListState AnimationList = new("_animationExpanded", "Animations");

    private static void CharacterListPrefix() => ListPrefix(CharacterList, "_characterIndex");
    private static void ContextListPrefix() => ListPrefix(ContextList, "_contextIndex");
    private static void AnimationListPrefix() => ListPrefix(AnimationList, "_animationIndex");

    private static void CharacterListPostfix()
    {
        bool reselected = Int("_characterIndex") != CharacterList.IndexBefore;
        ListPostfix(CharacterList);
        // Main collapses the character list after a pick; keep it open so another character can be picked directly.
        if (reselected && CharacterList.WasExpanded && !Bool("_characterExpanded")) Set("_characterExpanded", true);
    }

    private static void ContextListPostfix()
    {
        ListPostfix(ContextList);
        DrawFormRow();
    }

    private static void AnimationListPostfix()
    {
        bool reselected = Int("_animationIndex") != AnimationList.IndexBefore;
        ListPostfix(AnimationList);
        // Same as the character list: Main collapses the animation list after a pick (BUG-054); keep it open.
        if (reselected && AnimationList.WasExpanded && !Bool("_animationExpanded")) Set("_animationExpanded", true);
    }

    // ------------------------------------------------------------ 6b. previous / next animation (2026-11-15)

    /// <summary>
    /// "&lt;" and "&gt;" buttons at the end of the Animation row: play the previous / next animation in list order
    /// (wraps around), so every animation can be checked without opening the list. Inserted by a transpiler right
    /// before the row's first GUILayout.EndHorizontal in Main's DrawAnimationSelector; Main is not modified.
    /// </summary>
    private static MethodInfo? _playSelected, _fitCamera;

    private static IEnumerable<CodeInstruction> AnimationArrowsTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo arrows = AccessTools.DeclaredMethod(typeof(PreviewEnhancementsMod), nameof(AnimationArrows));
        bool inserted = false;
        foreach (CodeInstruction instruction in instructions)
        {
            if (!inserted && instruction.operand is MethodInfo method && method.Name == "EndHorizontal" && method.DeclaringType == typeof(GUILayout))
            {
                var call = new CodeInstruction(System.Reflection.Emit.OpCodes.Call, arrows);
                call.labels.AddRange(instruction.labels);   // keep jumps that targeted EndHorizontal
                instruction.labels.Clear();
                yield return call;
                inserted = true;
            }
            yield return instruction;
        }
        if (!inserted) MelonLogger.Warning($"{Prefix} animation arrows not added (row end not found)");
    }

    private static void AnimationArrows()
    {
        try
        {
            int count = Get("Animations") is System.Collections.ICollection list ? list.Count : 0;
            GUI.enabled = count > 1;
            int step = 0;
            if (GUILayout.Button("<", GUILayout.Width(34f))) step = -1;
            if (GUILayout.Button(">", GUILayout.Width(34f))) step = 1;
            GUI.enabled = true;
            if (step == 0 || count <= 1) return;
            int index = Int("_animationIndex");
            index = ((index < 0 ? 0 : index) + step + count) % count;
            Set("_animationIndex", index);
            _playSelected?.Invoke(null, new object[] { true });
            _fitCamera?.Invoke(null, null);
        }
        catch (Exception exception)
        {
            GUI.enabled = true;
            MelonLogger.Warning($"{Prefix} animation arrows failed: {exception.GetBaseException().Message}");
        }
    }

    // ------------------------------------------------------------ 7. scrollable preview window

    /// <summary>
    /// Main's Preview is a fixed 760x720 GUI.Window; open lists push Play/Pause/Speed/Close below its bottom edge where
    /// they are clipped. The whole window body is wrapped in a vertical scroll view (scrollbar + mouse wheel). Wheel
    /// events already used by a list or the image (zoom) do not scroll the window.
    /// </summary>
    private static Vector2 _windowScroll;
    private static bool _windowScrollActive, _hasViewport;
    private static Rect _viewport;

    private static void WindowPrefix()
    {
        _windowScrollActive = false;
        try
        {
            Rect window = Get("_windowRect") is Rect r ? r : new Rect(0, 0, 760, 720);
            float height = Math.Max(120f, window.height - 30f);
            _windowScroll = GUILayout.BeginScrollView(_windowScroll, GUILayout.Height(height));
            _windowScrollActive = true;
        }
        catch { /* never break Main's window */ }
    }

    private static Exception? WindowFinalizer(Exception? __exception)
    {
        if (!_windowScrollActive) return __exception;
        _windowScrollActive = false;
        try
        {
            Event e = Event.current;
            bool wheel = e != null && e.type == EventType.ScrollWheel;
            Vector2 delta = wheel ? e!.delta : default;
            GUILayout.EndScrollView();
            if (e != null && e.type != EventType.Layout && e.type != EventType.Used)
            {
                _viewport = GUILayoutUtility.GetLastRect();
                _hasViewport = true;
            }
            // Unity's EndScrollView may already have consumed the wheel; only scroll when nobody used it.
            if (wheel && e!.type == EventType.ScrollWheel && _hasViewport && _viewport.Contains(e.mousePosition))
            {
                _windowScroll.y = Math.Max(0f, _windowScroll.y + delta.y * 20f);   // BeginScrollView clamps the max
                e.Use();
            }
            Rect window = Get("_windowRect") is Rect r ? r : new Rect(0, 0, 760, 720);
            GUI.DragWindow(new Rect(0, 0, window.width, 24));   // title bar (Main's own DragWindow is inside the scroll view now)
        }
        catch { /* never break Main's window */ }
        return __exception;
    }

    private static void ListPrefix(ListState list, string indexField)
    {
        try
        {
            list.IndexBefore = Int(indexField);
            list.WasExpanded = Bool(list.Expanded);
            Set("_selectorScroll", list.Scroll);
            if (!list.WasExpanded || !list.HasView) return;
            Event e = Event.current;
            if (e == null || e.type != EventType.ScrollWheel) return;
            Vector2 mouse = e.mousePosition;
            Rect view = list.View;
            if (mouse.x < view.x || mouse.x > view.x + view.width || mouse.y < view.y || mouse.y > view.y + view.height) return;
            float max = Math.Max(0f, Names(list.Names).Count * 30f);
            list.Scroll.y = Math.Clamp(list.Scroll.y + e.delta.y * 20f, 0f, max);   // Main's BeginScrollView clamps to content
            Set("_selectorScroll", list.Scroll);
            e.Use();
        }
        catch { /* never break Main's window */ }
    }

    private static void ListPostfix(ListState list)
    {
        try
        {
            if (!Bool(list.Expanded) || !list.WasExpanded) { list.HasView = false; return; }
            if (Get("_selectorScroll") is Vector2 scroll) list.Scroll = scroll;
            Event e = Event.current;
            if (e != null && e.type != EventType.Layout)
            {
                list.View = GUILayoutUtility.GetLastRect();   // the scroll view Main just closed
                list.HasView = true;
            }
        }
        catch { /* never break Main's window */ }
    }

    // ------------------------------------------------------------ 9. Context selector with a "Results" group

    private static readonly string[] ResultFields = { "victoryShow", "failShow" };
    private static bool _resultsOpen;
    private static Vector2 _contextScroll;
    private static Rect _contextView;
    private static bool _contextViewValid;

    private static bool IsResult(int index) =>
        Get("ContextFields") is List<string> fields && index >= 0 && index < fields.Count && ResultFields.Contains(fields[index]);

    /// <summary>
    /// Replaces Main's DrawContextSelector (same header, same pick behaviour: collapse, select, animation 0,
    /// BuildCurrentPreview) so Victory and Fail sit in one expandable "Results" entry under Main / Battle.
    /// </summary>
    private static bool ContextSelectorReplacement()
    {
        try
        {
            List<string> names = Names("ContextNames");
            int index = Int("_contextIndex");
            GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
            GUILayout.Label("Context", GUILayout.Width(90));
            string header = names.Count == 0 ? "Unavailable" : index >= 0 && index < names.Count ? names[index] : "Unavailable";
            if (GUILayout.Button(header + "  v", Array.Empty<GUILayoutOption>()))
            {
                bool open = !Bool("_contextExpanded");
                Set("_contextExpanded", open);
                if (open && IsResult(index)) _resultsOpen = true;
            }
            GUILayout.EndHorizontal();

            if (Bool("_contextExpanded") && names.Count > 0)
            {
                var primary = Enumerable.Range(0, names.Count).Where(i => !IsResult(i)).ToList();
                var results = Enumerable.Range(0, names.Count).Where(IsResult).ToList();
                int rows = primary.Count + (results.Count > 0 ? 1 : 0) + (_resultsOpen ? results.Count : 0);
                Event e = Event.current;
                if (e != null && e.type == EventType.ScrollWheel && _contextViewValid && _contextView.Contains(e.mousePosition))
                {
                    _contextScroll.y = Math.Max(0f, _contextScroll.y + e.delta.y * 20f);
                    e.Use();
                }
                _contextScroll = GUILayout.BeginScrollView(_contextScroll, GUILayout.Height(Math.Min(170f, 28f * Math.Max(1, rows))));
                int picked = -1;
                foreach (int i in primary)
                    if (GUILayout.Button(names[i], Array.Empty<GUILayoutOption>())) picked = i;
                if (results.Count > 0)
                {
                    if (GUILayout.Button(_resultsOpen ? "Results  ^" : "Results  v", Array.Empty<GUILayoutOption>())) _resultsOpen = !_resultsOpen;
                    if (_resultsOpen)
                        foreach (int i in results)
                        {
                            GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
                            GUILayout.Space(24f);
                            if (GUILayout.Button(names[i], Array.Empty<GUILayoutOption>())) picked = i;
                            GUILayout.EndHorizontal();
                        }
                }
                GUILayout.EndScrollView();
                if (e != null && e.type != EventType.Layout) { _contextView = GUILayoutUtility.GetLastRect(); _contextViewValid = true; }
                if (picked >= 0)
                {
                    Set("_contextExpanded", false);
                    Set("_contextIndex", picked);
                    Set("_animationIndex", 0);
                    _buildCurrentPreview?.Invoke(null, null);
                }
            }
            else _contextViewValid = false;
            DrawFormRow();
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"{Prefix} CONTEXT_SELECTOR fallback to Main: {exception.GetBaseException().Message}");
            return true;
        }
        return false;
    }

    // ------------------------------------------------------------ 10. Result contexts: material / visibility

    /// <summary>
    /// Result skeletons come from SkeletonGraphic prefabs; their atlas materials may use a UI shader that draws nothing
    /// on Preview's MeshRenderer + camera. Preview-owned materials using a SkeletonGraphic/UI shader get the regular
    /// Spine/Skeleton shader (straight-alpha flag carried over). Everything is logged once per build for diagnosis.
    /// </summary>
    private static void FixResultPreviewMaterial()
    {
        try
        {
            if (!IsResult(Int("_contextIndex"))) return;
            SkeletonRenderer? animation = Renderer();
            if (animation == null) return;
            MeshRenderer? renderer = animation.GetComponent<MeshRenderer>();
            var materials = renderer?.sharedMaterials;
            var report = new List<string>();
            Shader? spine = Shader.Find("Spine/Skeleton");
            if (materials != null)
                foreach (Material material in materials)
                {
                    if (material == null) { report.Add("null"); continue; }
                    string shader = material.shader?.name ?? "<none>";
                    string action = "kept";
                    if (spine != null && (shader.Contains("SkeletonGraphic", StringComparison.OrdinalIgnoreCase) || shader.StartsWith("UI/", StringComparison.OrdinalIgnoreCase)))
                    {
                        float straight = material.HasProperty("_StraightAlphaInput") ? material.GetFloat("_StraightAlphaInput") : 0f;
                        material.shader = spine;
                        if (material.HasProperty("_StraightAlphaInput")) material.SetFloat("_StraightAlphaInput", straight);
                        action = "->Spine/Skeleton";
                    }
                    report.Add($"{material.name}:{shader}:{action}:tex={(material.mainTexture != null ? material.mainTexture.name + "@" + material.mainTexture.width + "x" + material.mainTexture.height : "null")}");
                }
            Bounds bounds = renderer != null ? renderer.bounds : default;
            Skeleton? skeleton = animation.Skeleton;
            MelonLogger.Msg($"{Prefix} RESULT_PREVIEW context={Names("ContextNames")[Int("_contextIndex")]} renderer={(renderer != null)} enabled={renderer?.enabled} " +
                $"materials=[{string.Join(" | ", report)}] bounds={bounds.center}/{bounds.size} skeletonScale={skeleton?.ScaleX}x{skeleton?.ScaleY} " +
                $"slots={skeleton?.slots?.Count} skin={skeleton?.skin?.name ?? "<default>"}");
        }
        catch (Exception exception) { MelonLogger.Warning($"{Prefix} RESULT_PREVIEW diagnostics failed: {exception.GetBaseException().Message}"); }
    }

    /// <summary>
    /// Main fits the camera to the renderer bounds of frame 0. Result animations enter from off-screen (Bassist Fail
    /// "in": frame-0 bounds center x=-9.1, size 2.9x3.4), so the character left the view. Samples the current animation
    /// over its duration and fits to the union (same 4:3 aspect and 1.18 margin as Main).
    /// </summary>
    private static void FitResultAnimation(Camera camera)
    {
        try
        {
            // Sampling a whole animation to fit the camera needs Spine's own AnimationState: a Mecanim
            // preview is driven by a Unity Animator and cannot be wound forward like this. The fit then
            // stays Main's frame-0 fit, which is what it was before this module existed.
            SkeletonAnimation? animation = Animation();
            if (animation == null || animation.Pointer == IntPtr.Zero) return;
            MeshRenderer? renderer = animation.meshRenderer;
            TrackEntry? entry = animation.AnimationState?.GetCurrent(0);
            if (renderer == null || entry == null || entry.Animation == null) return;
            float duration = Math.Max(0.0001f, entry.Animation.Duration);
            float original = entry.TrackTime;
            bool any = false;
            Bounds union = default;
            const int samples = 24;
            for (int i = 0; i <= samples; i++)
            {
                entry.TrackTime = duration * i / samples;
                animation.Update(0f);
                animation.LateUpdate();
                Bounds b = renderer.bounds;
                if (b.size.x < 0.0001f && b.size.y < 0.0001f) continue;
                if (!any) { union = b; any = true; } else union.Encapsulate(b);
            }
            entry.TrackTime = original;
            animation.Update(0f);
            animation.LateUpdate();
            if (!any) return;
            float aspect = 1.333333f;
            float size = Math.Max(union.extents.y, union.extents.x / aspect) * 1.18f;
            camera.orthographicSize = Math.Max(0.25f, size);
            Vector3 center = union.center;
            camera.transform.position = new Vector3(center.x, center.y, -10f);
            MelonLogger.Msg($"{Prefix} RESULT_CAMERA_FIT samples={samples + 1} center={center} size={union.size} ortho={camera.orthographicSize:0.###}");
        }
        catch (Exception exception) { MelonLogger.Warning($"{Prefix} RESULT_CAMERA_FIT skipped: {exception.GetBaseException().Message}"); }
    }

    // ------------------------------------------------------------ 4. zoom / pan

    private static float _zoom = 1f;
    private static Vector3 _pan;
    private static float _baseOrtho;
    private static Vector3 _basePosition;
    private static bool _hasBase, _dragging;
    private static Vector2 _lastMouse;

    private static void ResetView() { _zoom = 1f; _pan = default; _dragging = false; ApplyView(); }

    private static void FitCameraPostfix()
    {
        if (Get("_camera") is not Camera camera || camera.Pointer == IntPtr.Zero) return;
        if (IsResult(Int("_contextIndex"))) FitResultAnimation(camera);
        _baseOrtho = camera.orthographicSize;
        _basePosition = camera.transform.position;
        _hasBase = true;
        ApplyView();
    }

    private static void ApplyView()
    {
        if (!_hasBase || Get("_camera") is not Camera camera || camera.Pointer == IntPtr.Zero) return;
        camera.orthographicSize = _baseOrtho * _zoom;
        Vector3 p = _basePosition;
        p.x += _pan.x; p.y += _pan.y;
        camera.transform.position = p;
    }

    /// <summary>Inserts "dup; call OnPreviewRect(Rect)" right after DrawPreviewWindow's GUILayoutUtility.GetRect.</summary>
    private static IEnumerable<CodeInstruction> PreviewRectTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo hook = AccessTools.Method(typeof(PreviewEnhancementsMod), nameof(OnPreviewRect));
        bool done = false;
        foreach (CodeInstruction instruction in instructions)
        {
            yield return instruction;
            if (!done && instruction.operand is MethodInfo m && m.DeclaringType == typeof(GUILayoutUtility) && m.Name == "GetRect" && m.ReturnType == typeof(Rect))
            {
                yield return new CodeInstruction(OpCodes.Dup);
                yield return new CodeInstruction(OpCodes.Call, hook);
                done = true;
            }
        }
        if (!done) MelonLogger.Warning($"{Prefix} preview image rect hook not found; zoom/pan unavailable");
    }

    public static void OnPreviewRect(Rect rect)
    {
        try
        {
            Event e = Event.current;
            if (e == null || !_hasBase || rect.width < 4 || rect.height < 4) return;
            EventType type = e.type;
            if (type is EventType.Layout or EventType.Repaint) return;
            // Open lists are laid out above the image (they push it down), so the image rect never overlaps them:
            // zoom / pan / reset work in every list state.
            Vector2 mouse = e.mousePosition;
            // Main draws the RenderTexture with ScaleMode.ScaleToFit: the black image is the aspect-fit rect centred in
            // the layout rect, not the whole rect. Only that area reacts (BUG-055).
            Rect shown = rect;
            if (Get("_renderTexture") is RenderTexture rt && rt.Pointer != IntPtr.Zero && rt.width > 0 && rt.height > 0)
            {
                float aspect = rt.width / (float)rt.height;
                float w = Math.Min(rect.width, rect.height * aspect);
                float h = Math.Min(rect.height, rect.width / aspect);
                shown = new Rect(rect.x + (rect.width - w) / 2f, rect.y + (rect.height - h) / 2f, w, h);
            }
            bool inside = mouse.x >= shown.x && mouse.x <= shown.xMax && mouse.y >= shown.y && mouse.y <= shown.yMax;
            // With the window scroll view the image can be partly scrolled out of sight; the hidden part must not react.
            if (_windowScrollActive && _hasViewport &&
                (mouse.y < _windowScroll.y || mouse.y > _windowScroll.y + _viewport.height)) inside = false;
            float shownHeight = Math.Max(1f, shown.height);
            float unitsPerPixel = 2f * _baseOrtho * _zoom / shownHeight;

            if (type == EventType.ScrollWheel && inside)
            {
                float factor = e.delta.y > 0 ? 1.12f : 1f / 1.12f;
                float newZoom = Math.Clamp(_zoom * factor, 0.05f, 20f);
                float newUnits = 2f * _baseOrtho * newZoom / shownHeight;
                float dx = mouse.x - shown.center.x, dy = mouse.y - shown.center.y;
                // Keep the world point under the cursor fixed.
                _pan.x += dx * (unitsPerPixel - newUnits);
                _pan.y -= dy * (unitsPerPixel - newUnits);
                _zoom = newZoom;
                ApplyView();
                e.Use();
            }
            else if (type == EventType.MouseDown && inside && e.button == 0)
            {
                _dragging = true;
                _lastMouse = mouse;
                e.Use();
            }
            else if (type == EventType.MouseDrag && _dragging)
            {
                Vector2 delta = mouse - _lastMouse;
                _lastMouse = mouse;
                _pan.x -= delta.x * unitsPerPixel;
                _pan.y += delta.y * unitsPerPixel;
                ApplyView();
                e.Use();
            }
            else if (type == EventType.MouseUp && _dragging)
            {
                _dragging = false;
                e.Use();
            }
            else if (type == EventType.MouseDown && inside && e.button == 1)
            {
                ResetView();
                e.Use();
            }
        }
        catch { /* never break Main's window */ }
    }

    private static string Show(string value) => string.IsNullOrEmpty(value) ? "<none>" : value;
}
