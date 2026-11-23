using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppPeroTools2.Resources;
using Il2CppSpine;
using Il2CppSpine.Unity;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;
using WindowFunction = UnityEngine.GUI.WindowFunction;

namespace SkinForger.Preview;

public static class PreviewManager
{
    private const string Prefix = "[SkinForge][Preview]";
    private const int PreviewLayer = 31;
    private const int WindowId = 90305;
    private const int SpeedCount = 4;
    private const int ProfileOpenLimit = 2;
    private static readonly List<int> CharacterIds = new();
    private static readonly List<string> CharacterNames = new();
    private static readonly List<string> ContextFields = new();
    private static readonly List<string> ContextNames = new();
    private static readonly List<string> ContextResources = new();
    private static readonly List<GameObject> ContextPrefabs = new();
    private static readonly List<SkeletonDataAsset> ContextAssets = new();
    private static readonly List<SkeletonRenderer> ContextTemplates = new();
    private static readonly List<string> Animations = new();
    private static readonly List<Material> OwnedMaterials = new();
    private static Type? _host;
    private static MethodInfo? _getCount;
    private static MethodInfo? _getCostumeName;
    private static MethodInfo? _configString;
    private static MethodInfo? _getSelected;
    private static MethodInfo? _getSkinPath;
    private static MethodInfo? _buildAtlas;
    private static MethodInfo? _beginAmplitude;
    private static string[] _contextFields = Array.Empty<string>();
    /// <summary>Main's CharacterShows. Read again every time contexts are built: the host fills it in once
    /// the game's config is loaded, which is after this manager is initialised (2026-11-20).</summary>
    private static FieldInfo? _showFields;
    private static WindowFunction? _windowDelegate;
    private static Rect _windowRect = new(180f, 80f, 760f, 720f);
    private static Vector2 _selectorScroll;
    private static bool _initialized;
    private static bool _open;
    private static bool _rebuildAfterReload;
    private static bool _loop = true;
    private static bool _paused;
    private static bool _characterExpanded;
    private static bool _contextExpanded;
    private static bool _animationExpanded;
    private static int _characterIndex;
    private static int _contextIndex;
    private static int _animationIndex;
    private static int _speedIndex = 2;
    private static GameObject? _previewRoot;
    private static GameObject? _cameraRoot;
    private static SkeletonRenderer? _skeletonRenderer;
    private static SkeletonMecanim? _skeletonMecanim;
    private static Animator? _animator;
    private static Camera? _camera;
    private static RenderTexture? _renderTexture;
    private static int _profileOpenCount;
    private static PreviewProfile? _activeProfile;

    public static bool SuppressHostApply { get; private set; }

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            _host = typeof(PreviewManager).Assembly.GetType("SkinForger.SkinForgerMod", true);
            BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            // GetCount(string costume) -- how many skins a costume has. The authored Main also has
            // GetCount(bool elfin) (how many rows a list has), and asking by name alone threw
            // "Ambiguous match found", which aborted the whole of Initialize: no window delegate, so the
            // Preview button opened nothing at all and only the log said why (2026-11-20).
            _getCount = _host!.GetMethod("GetCount", flags, null, new[] { typeof(string) }, null);
            _getCostumeName = _host.GetMethod("GetCostumeName", flags, null, new[] { typeof(int), typeof(bool) }, null);
            _configString = _host.GetMethod("ConfigString", flags, null, new[] { typeof(string), typeof(int), typeof(string) }, null);
            _getSelected = _host.GetMethod("GetSelected", flags, null, new[] { typeof(int), typeof(bool) }, null);
            _getSkinPath = _host.GetMethod("GetSkinPath", flags, null, new[] { typeof(int), typeof(int), typeof(bool) }, null);
            _buildAtlas = _host.GetMethod("TryBuildSynthesizedAtlasForSkeleton", flags);
            _beginAmplitude = _host.Assembly.GetType("SkinForger.AnimationAmplitude.AnimationAmplitudeManager")?
                .GetMethod("BeginSkeleton", flags);
            _showFields = _host.GetField("CharacterShows", flags);
            _contextFields = _showFields?.GetValue(null) as string[] ?? Array.Empty<string>();
            _windowDelegate = DelegateSupport.ConvertDelegate<WindowFunction>((Delegate)new Action<int>(DrawPreviewWindow));
            string missing = string.Join(", ", new[]
            {
                _getCount == null ? "GetCount" : null,
                _getCostumeName == null ? "GetCostumeName" : null,
                _configString == null ? "ConfigString" : null,
                _getSelected == null ? "GetSelected" : null,
                _getSkinPath == null ? "GetSkinPath" : null,
                _buildAtlas == null ? "TryBuildSynthesizedAtlasForSkeleton" : null,
            }.Where(name => name != null));
            if (missing.Length > 0) Warning("the host is missing: " + missing + " -- the Preview will be limited");
            Log("initialized; independent SkeletonAnimation + Camera + RenderTexture path ready");
        }
        catch (Exception ex)
        {
            Warning("initialization failed: " + ex.Message);
        }
    }

    public static void DrawLauncher()
    {
        if (GUILayout.Button("Preview", new[] { GUILayout.Width(110f) }))
        {
            if (_open) Close(); else Open();
        }
    }

    public static void Draw()
    {
        if (!_open || _windowDelegate == null) return;
        _windowRect = GUI.Window(WindowId, _windowRect, _windowDelegate, "SkinForger Preview");
    }

    public static void BeforeHostReload()
    {
        if (!_open) return;
        _rebuildAfterReload = true;
        DestroyPreviewObjects();
    }

    public static void AfterHostReload()
    {
        if (!_open || !_rebuildAfterReload) return;
        _rebuildAfterReload = false;
        BuildCurrentPreview();
    }

    public static bool IsHostApplySuppressed() => SuppressHostApply;

    private static void Open()
    {
        BeginPreviewProfile();
        Initialize();
        ProfilePoint("initialize");
        BuildCharacters();
        ProfilePoint("build_characters");
        _open = true;
        _characterExpanded = _contextExpanded = _animationExpanded = false;
        LoadContextsAndPreview();
        ProfilePoint("load_contexts_and_preview");
        Log("window opened");
        EndPreviewProfile("open_complete");
    }

    private static void Close()
    {
        _open = false;
        _rebuildAfterReload = false;
        DestroyPreviewObjects();
        ClearContexts();
        Animations.Clear();
        Log("window closed; preview objects released");
    }

    private static void BuildCharacters()
    {
        CharacterIds.Clear();
        CharacterNames.Clear();
        int count = HostInt(_getCount, "character");
        for (int i = 0; i < count; i++)
        {
            string name = HostString(_getCostumeName, i, false);
            if (string.IsNullOrEmpty(name)) name = "Character " + i;
            CharacterIds.Add(i);
            CharacterNames.Add(name);
        }
        if (_characterIndex < 0 || _characterIndex >= CharacterIds.Count) _characterIndex = 0;
    }

    private static void LoadContextsAndPreview()
    {
        bool previousSuppress = SuppressHostApply;
        SuppressHostApply = true;
        try
        {
            long total = ProfileStart();
            DestroyPreviewObjects();
            ProfileEnd("contexts.destroy_previous_preview", total);
            total = ProfileStart();
            ClearContexts();
            Animations.Clear();
            _contextIndex = 0;
            _animationIndex = 0;
            ProfileEnd("contexts.clear_lists", total);
            if (_showFields?.GetValue(null) is string[] shows && shows.Length > 0) _contextFields = shows;
            if (CharacterIds.Count == 0 || _characterIndex >= CharacterIds.Count)
            {
                Warning("nothing to preview: the host listed " + CharacterIds.Count + " character(s)");
                return;
            }
            int characterId = CharacterIds[_characterIndex];
            total = ProfileStart();
            ResourcesManager resources = ResourcesManager.instance;
            ProfileEnd("contexts.resources_manager", total);
            if (resources == null)
            {
                Warning("ResourcesManager is not ready");
                return;
            }
            foreach (string field in _contextFields)
            {
                string resourceName = HostString(_configString, "character", characterId, field);
                if (string.IsNullOrEmpty(resourceName)) continue;
                try
                {
                    long step = ProfileStart();
                    GameObject prefab = resources.LoadFromName<GameObject>(resourceName);
                    ProfileEnd("context." + field + ".load_resource:" + resourceName, step);
                    if (prefab == null) continue;
                    step = ProfileStart();
                    SkeletonRenderer? template = SelectPrimaryRenderer(prefab);
                    ProfileEnd("context." + field + ".select_renderer", step);
                    step = ProfileStart();
                    SkeletonDataAsset? asset = template?.SkeletonDataAsset;
                    if (asset == null || asset.GetSkeletonData(true) == null) continue;
                    ProfileEnd("context." + field + ".get_skeleton_data", step);
                    ContextFields.Add(field);
                    ContextNames.Add(ContextDisplayName(field));
                    ContextResources.Add(resourceName);
                    ContextPrefabs.Add(prefab);
                    ContextAssets.Add(asset);
                    ContextTemplates.Add(template);
                }
                catch (Exception ex)
                {
                    Warning("context skipped: " + field + "/" + resourceName + ": " + ex.Message);
                }
            }
            if (ContextAssets.Count > 0) BuildCurrentPreview();
        }
        finally
        {
            SuppressHostApply = previousSuppress;
        }
    }

    private static SkeletonRenderer? SelectPrimaryRenderer(GameObject prefab)
    {
        SkeletonRenderer? best = null;
        int bestAnimations = -1;
        Il2CppArrayBase<SkeletonRenderer> renderers = prefab.GetComponentsInChildren<SkeletonRenderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            SkeletonRenderer renderer = renderers[i];
            SkeletonData data = renderer?.SkeletonDataAsset?.GetSkeletonData(true);
            int count = data?.Animations?.Count ?? -1;
            string objectName = renderer == null ? string.Empty : renderer.gameObject.name ?? string.Empty;
            if (objectName.IndexOf("ghost", StringComparison.OrdinalIgnoreCase) >= 0 ||
                objectName.IndexOf("shadow", StringComparison.OrdinalIgnoreCase) >= 0 ||
                objectName.IndexOf("cat", StringComparison.OrdinalIgnoreCase) >= 0) count -= 100000;
            if (count > bestAnimations)
            {
                best = renderer;
                bestAnimations = count;
            }
        }
        return best;
    }

#if SKINFORGER_AUTHORED_MAIN
    /// <summary>
    /// Builds the preview. Two names, because SkinForger.Core's PreviewEnhancements binds to both: a
    /// prefix on <c>BuildCurrentPreview</c> (and it invokes that name by reflection) and a postfix on
    /// <c>BuildCurrentPreviewAttempt</c>, which is where it resets the view and reads the built skeleton.
    /// A missing name takes the whole module down — the 2026-11-19 trial lost Preview this way.
    ///
    /// In the old Cecil-built Main the split carried a second meaning: an attempt through the Shared Core
    /// with a fallback that rebuilt through the private replacement. The authored Main has only one
    /// replacement path (ApplySkeleton -> TryBuildSynthesizedAtlasForSkeleton), so there is nothing to
    /// fall back to and the wrapper does not pretend otherwise.
    /// </summary>
    private static void BuildCurrentPreview() => BuildCurrentPreviewAttempt();

    private static void BuildCurrentPreviewAttempt()
    {
        bool previousSuppress = SuppressHostApply;
#else
    private static void BuildCurrentPreview()
    {
        bool previousSuppress = SuppressHostApply;
#endif
        SuppressHostApply = true;
        try
        {
            long total = ProfileStart();
            DestroyPreviewObjects();
            ProfileEnd("build.destroy_previous_preview", total);
            total = ProfileStart();
            Animations.Clear();
            if (ContextPrefabs.Count == 0 || _contextIndex < 0 || _contextIndex >= ContextPrefabs.Count) return;
            GameObject sourcePrefab = ContextPrefabs[_contextIndex];
            string contextName = ContextNames[_contextIndex];
            if (sourcePrefab == null) return;
            ProfileEnd("build.prepare_context", total);

            long step = ProfileStart();
            bool sourceWasActive = sourcePrefab.activeSelf;
            try
            {
                sourcePrefab.SetActive(false);
                _previewRoot = Object.Instantiate(sourcePrefab);
            }
            finally
            {
                sourcePrefab.SetActive(sourceWasActive);
            }
            ProfileEnd("build.instantiate_gameplay_prefab", step);
            if (_previewRoot == null) throw new InvalidOperationException("Gameplay prefab clone returned null");
            _previewRoot.name = "SkinForger Preview " + contextName;
            _previewRoot.hideFlags = HideFlags.HideAndDontSave;
            Object.DontDestroyOnLoad(_previewRoot);
            SetLayerRecursively(_previewRoot, PreviewLayer);

            step = ProfileStart();
            _skeletonRenderer = SelectPrimaryRenderer(_previewRoot);
            if (_skeletonRenderer == null) throw new InvalidOperationException("Gameplay prefab clone has no SkeletonRenderer");
            _skeletonMecanim = ((Il2CppObjectBase)_skeletonRenderer).TryCast<SkeletonMecanim>();
            if (_skeletonMecanim == null)
                throw new InvalidOperationException("Gameplay renderer type is not SkeletonMecanim: " + _skeletonRenderer.GetType().FullName);
            _animator = _previewRoot.GetComponentInChildren<Animator>(true);
            if (_animator == null) throw new InvalidOperationException("Gameplay prefab clone has no Animator");
            DisableGameplayBehaviours(_previewRoot, _skeletonRenderer);
            ProfileEnd("build.bind_gameplay_components", step);

            step = ProfileStart();
            _previewRoot.SetActive(true);
            if (_skeletonRenderer.Skeleton == null)
                throw new InvalidOperationException("Cloned SkeletonMecanim has no Skeleton after activation");
            if (_skeletonRenderer.meshRenderer == null)
                throw new InvalidOperationException("Cloned SkeletonMecanim has no MeshRenderer");
            ProfileEnd("build.activate_gameplay_prefab", step);

            // Put the player's own skin on the clone.
            //
            // 2026-11-21, and this is why the preview came out in pieces. The old build had the Cecil step
            // wire two calls into this method -- IsolatePreviewRendering and ApplyCurrentReplacement -- and
            // the authored build, being compiled rather than post-processed, called NEITHER. The clone
            // shares its SkeletonData, and so its atlas regions, with the gameplay object SkinForger has
            // already rebuilt: the regions' uvs address the COMPOSED atlas while the clone's material still
            // held the game's ORIGINAL texture, so every part sampled the wrong rectangle.
            //
            // The fix binds the composed atlas to the preview renderer's OWN material copies. The old build
            // assigned it onto the page materials shared with the game and needed an isolation pass first;
            // copies need no isolation, cannot disturb the game, and are destroyed with the preview.
            step = ProfileStart();
            ApplyCurrentReplacement(_skeletonRenderer.Skeleton, CharacterIds[_characterIndex]);
            CheckPreviewBinding(_skeletonRenderer.Skeleton);
            ProfileEnd("build.apply_replacement", step);

            step = ProfileStart();
            PopulateAnimations(_skeletonRenderer.Skeleton.Data);
            ProfileEnd("build.populate_animations", step);
            step = ProfileStart();
            CreateCamera();
            ProfileEnd("build.create_camera", step);
            step = ProfileStart();
            PlaySelected(restart: true);
            ProfileEnd("build.play_selected", step);
            step = ProfileStart();
            FitCameraToAnimation();
            ProfileEnd("build.fit_camera_to_animation", step);
            Log("created character=" + CharacterNames[_characterIndex] + ", context=" + contextName +
                ", renderer=" + _skeletonRenderer.GetType().FullName + ", animations=" + Animations.Count +
                ", materials=" + _skeletonRenderer.meshRenderer.sharedMaterials.Length);
            LogRuntimeGraph("preview", _previewRoot, _skeletonRenderer);
        }
        catch (Exception ex)
        {
            Warning("creation failed: " + ex);
            DestroyPreviewObjects();
        }
        finally
        {
            SuppressHostApply = previousSuppress;
        }
    }

    private static void BeginPreviewProfile()
    {
        if (_profileOpenCount >= ProfileOpenLimit)
        {
            _activeProfile = null;
            return;
        }
        _profileOpenCount++;
        _activeProfile = new PreviewProfile(_profileOpenCount);
        ProfilePoint("preview_click");
    }

    private static void EndPreviewProfile(string stage)
    {
        PreviewProfile? profile = _activeProfile;
        if (profile == null) return;
        ProfilePoint(stage);
        Log("profile open=" + profile.OpenIndex + " total=" + profile.ElapsedTotal().ToString("0.###") + "ms; " +
            string.Join("; ", profile.Entries));
        _activeProfile = null;
    }

    private static long ProfileStart()
    {
        return _activeProfile == null ? 0L : Stopwatch.GetTimestamp();
    }

    private static void ProfileEnd(string stage, long start)
    {
        if (_activeProfile == null || start == 0L) return;
        double elapsed = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        _activeProfile.Add(stage, elapsed);
    }

    private static void ProfilePoint(string stage)
    {
        _activeProfile?.Point(stage);
    }

    private static void IsolatePreviewRendering(SkeletonData data)
    {
        Dictionary<long, AtlasRegion> regionMap = new();
        Dictionary<long, AtlasPage> pageMap = new();
        Il2CppSystem.Collections.Generic.List<Skin.SkinEntry> entries = new();
        int slotCount = data.slots?.Count ?? 0;
        if (data.skins == null) return;
        for (int skinIndex = 0; skinIndex < data.skins.Count; skinIndex++)
        {
            Skin skin = data.skins.Items[skinIndex];
            for (int slot = 0; slot < slotCount; slot++)
            {
                entries.Clear();
                skin.GetAttachments(slot, entries);
                for (int i = 0; i < entries.Count; i++)
                {
                    Attachment attachment = entries[i].Attachment;
                    Il2CppObjectBase attachmentObject = attachment;
                    RegionAttachment regionAttachment = attachmentObject.TryCast<RegionAttachment>();
                    if (regionAttachment != null)
                    {
                        AtlasRegion source = regionAttachment.RendererObject?.TryCast<AtlasRegion>();
                        if (source != null) regionAttachment.RendererObject = CloneRegion(source, regionMap, pageMap);
                    }
                    MeshAttachment meshAttachment = attachmentObject.TryCast<MeshAttachment>();
                    if (meshAttachment != null)
                    {
                        AtlasRegion source = meshAttachment.RendererObject?.TryCast<AtlasRegion>();
                        if (source != null) meshAttachment.RendererObject = CloneRegion(source, regionMap, pageMap);
                    }
                }
            }
        }
    }

    private static SkeletonData CloneSkeletonData(SkeletonData source)
    {
        SkeletonData clone = new();
        clone.name = source.name + " [SkinForger Preview]";
        clone.bones = source.bones;
        clone.slots = source.slots;
        clone.events = source.events;
        clone.ikConstraints = source.ikConstraints;
        clone.transformConstraints = source.transformConstraints;
        clone.pathConstraints = source.pathConstraints;
        clone.x = source.x;
        clone.y = source.y;
        clone.width = source.width;
        clone.height = source.height;
        clone.version = source.version;
        clone.hash = source.hash;
        clone.fps = source.fps;
        clone.imagesPath = source.imagesPath;
        clone.audioPath = source.audioPath;
        clone.animations = CloneAnimations(source.animations);
        clone.skins = new ExposedList<Skin>(source.skins?.Count ?? 0);

        Dictionary<long, Skin> skinMap = new();
        Dictionary<long, Attachment> attachmentMap = new();
        Dictionary<long, AtlasRegion> regionMap = new();
        Dictionary<long, AtlasPage> pageMap = new();
        if (source.skins != null)
        {
            for (int i = 0; i < source.skins.Count; i++)
            {
                Skin sourceSkin = source.skins.Items[i];
                Skin copied = CloneSkin(sourceSkin, source.slots.Count, attachmentMap, regionMap, pageMap);
                clone.skins.Add(copied);
                skinMap[PointerOf(sourceSkin)] = copied;
            }
        }
        if (source.defaultSkin != null && skinMap.TryGetValue(PointerOf(source.defaultSkin), out Skin? defaultSkin))
            clone.defaultSkin = defaultSkin;
        else if (clone.skins.Count > 0)
            clone.defaultSkin = clone.skins.Items[0];
        RemapLinkedMeshes(attachmentMap);
        return clone;
    }

    private static Skin CloneSkin(Skin source, int slotCount, Dictionary<long, Attachment> attachmentMap,
        Dictionary<long, AtlasRegion> regionMap, Dictionary<long, AtlasPage> pageMap)
    {
        Skin clone = new((source?.name ?? "default") + " [Preview]");
        if (source == null) return clone;
        clone.bones = source.bones;
        clone.constraints = source.constraints;
        Il2CppSystem.Collections.Generic.List<Skin.SkinEntry> entries = new();
        for (int slot = 0; slot < slotCount; slot++)
        {
            entries.Clear();
            source.GetAttachments(slot, entries);
            for (int i = 0; i < entries.Count; i++)
            {
                Skin.SkinEntry entry = entries[i];
                Attachment copied = CloneAttachment(entry.Attachment, attachmentMap, regionMap, pageMap);
                if (copied != null) clone.SetAttachment(slot, entry.Name, copied);
            }
        }
        return clone;
    }

    private static Attachment CloneAttachment(Attachment source, Dictionary<long, Attachment> attachmentMap,
        Dictionary<long, AtlasRegion> regionMap, Dictionary<long, AtlasPage> pageMap)
    {
        if (source == null) return null!;
        long key = PointerOf(source);
        if (attachmentMap.TryGetValue(key, out Attachment? existing)) return existing;
        Attachment clone = source.Copy();
        attachmentMap[key] = clone;
        Il2CppObjectBase sourceObject = source;
        Il2CppObjectBase cloneObject = clone;
        RegionAttachment sourceRegionAttachment = sourceObject.TryCast<RegionAttachment>();
        RegionAttachment cloneRegionAttachment = cloneObject.TryCast<RegionAttachment>();
        if (sourceRegionAttachment != null && cloneRegionAttachment != null)
        {
            AtlasRegion region = sourceRegionAttachment.RendererObject?.TryCast<AtlasRegion>();
            if (region != null) cloneRegionAttachment.RendererObject = CloneRegion(region, regionMap, pageMap);
        }
        MeshAttachment sourceMesh = sourceObject.TryCast<MeshAttachment>();
        MeshAttachment cloneMesh = cloneObject.TryCast<MeshAttachment>();
        if (sourceMesh != null && cloneMesh != null)
        {
            AtlasRegion region = sourceMesh.RendererObject?.TryCast<AtlasRegion>();
            if (region != null) cloneMesh.RendererObject = CloneRegion(region, regionMap, pageMap);
        }
        return clone;
    }

    private static AtlasRegion CloneRegion(AtlasRegion source, Dictionary<long, AtlasRegion> regionMap,
        Dictionary<long, AtlasPage> pageMap)
    {
        long key = PointerOf(source);
        if (regionMap.TryGetValue(key, out AtlasRegion? existing)) return existing;
        AtlasRegion clone = source.Clone();
        regionMap[key] = clone;
        if (source.page != null)
        {
            long pageKey = PointerOf(source.page);
            if (!pageMap.TryGetValue(pageKey, out AtlasPage? page))
            {
                page = source.page.Clone();
                Material original = source.page.rendererObject?.TryCast<Material>();
                if (original != null)
                {
                    Material material = new(original) { name = original.name + " [SkinForger Preview]" };
                    OwnedMaterials.Add(material);
                    page.rendererObject = material;
                }
                pageMap[pageKey] = page;
            }
            clone.page = page;
        }
        return clone;
    }

    private static void RemapLinkedMeshes(Dictionary<long, Attachment> attachments)
    {
        foreach (KeyValuePair<long, Attachment> pair in attachments)
        {
            MeshAttachment copied = ((Il2CppObjectBase)pair.Value).TryCast<MeshAttachment>();
            if (copied == null || copied.parentMesh == null) continue;
            long parentKey = PointerOf(copied.parentMesh);
            if (attachments.TryGetValue(parentKey, out Attachment? parent))
            {
                MeshAttachment clonedParent = ((Il2CppObjectBase)parent).TryCast<MeshAttachment>();
                if (clonedParent != null) copied.ParentMesh = clonedParent;
            }
        }
    }

    private static ExposedList<Il2CppSpine.Animation> CloneAnimations(ExposedList<Il2CppSpine.Animation> source)
    {
        ExposedList<Il2CppSpine.Animation> result = new(source?.Count ?? 0);
        if (source == null) return result;
        for (int i = 0; i < source.Count; i++)
        {
            Il2CppSpine.Animation animation = source.Items[i];
            ExposedList<Timeline> timelines = new(animation.timelines?.Count ?? 0);
            if (animation.timelines != null)
            {
                for (int j = 0; j < animation.timelines.Count; j++)
                    timelines.Add(CloneMutableTimeline(animation.timelines.Items[j]));
            }
            result.Add(new Il2CppSpine.Animation(animation.Name, timelines, animation.Duration));
        }
        return result;
    }

    private static Timeline CloneMutableTimeline(Timeline source)
    {
        if (source == null) return null!;
        Il2CppObjectBase obj = source;
        if (obj.TryCast<ScaleTimeline>() != null || obj.TryCast<ShearTimeline>() != null) return source;
        TranslateTimeline translate = obj.TryCast<TranslateTimeline>();
        if (translate != null)
        {
            TranslateTimeline copy = new(translate.FrameCount) { boneIndex = translate.boneIndex };
            copy.frames = CopyFloats(translate.frames);
            copy.curves = CopyFloats(translate.curves);
            return ((Il2CppObjectBase)copy).TryCast<Timeline>();
        }
        RotateTimeline rotate = obj.TryCast<RotateTimeline>();
        if (rotate != null)
        {
            RotateTimeline copy = new(rotate.FrameCount) { boneIndex = rotate.boneIndex };
            copy.frames = CopyFloats(rotate.frames);
            copy.curves = CopyFloats(rotate.curves);
            return ((Il2CppObjectBase)copy).TryCast<Timeline>();
        }
        return source;
    }

    private static Il2CppStructArray<float> CopyFloats(Il2CppStructArray<float> source)
    {
        Il2CppStructArray<float> copy = new(source?.Length ?? 0);
        if (source != null) for (int i = 0; i < source.Length; i++) copy[i] = source[i];
        return copy;
    }

    private static void ApplyCurrentReplacement(Skeleton skeleton, int listIndex)
    {
        if (skeleton == null || _getSelected == null || _getSkinPath == null || _buildAtlas == null) return;
        int selected = HostInt(_getSelected, listIndex, false);
        if (selected < 0) return;
        string skinPath = HostString(_getSkinPath, listIndex, selected, false);
        if (string.IsNullOrEmpty(skinPath)) return;
        _beginAmplitude?.Invoke(null, new object[] { skeleton });
        Dictionary<int, Material> materials = CollectMaterials(skeleton);
        foreach (Material material in materials.Values)
        {
            Texture2D source = material.mainTexture?.TryCast<Texture2D>();
            if (source == null || string.IsNullOrEmpty(source.name)) continue;
            Texture2D atlas = _buildAtlas.Invoke(null, BuildAtlasArguments(skeleton, source, skinPath, source.name)) as Texture2D;
            if (atlas == null || atlas.Pointer == source.Pointer) continue;

            // Bind it the way the host binds one: a material override ON THIS RENDERER.
            //
            // 2026-11-21, third attempt at BUG-031's signature, and the reason the first two failed:
            //  * assigning meshRenderer.sharedMaterials does not hold. A Spine renderer rebuilds its
            //    material list from the ATLAS PAGE (AtlasRegion.page.rendererObject) every time it
            //    regenerates its mesh, so the binding was gone on the next frame and the clone went back
            //    to drawing composed-atlas uvs out of the original texture -- in pieces.
            //  * writing the texture onto the page material does hold, but the page is SHARED with the
            //    game, so the Preview would be reaching into the menu behind it.
            // CustomMaterialOverride is the seam Spine provides for exactly this, it is what the host's
            // own AtlasOwnershipManager uses for gameplay, and it belongs to this renderer alone.
            Material copy = new(material) { name = material.name + " [SkinForger Preview]", mainTexture = atlas };
            OwnedMaterials.Add(copy);
            if (_skeletonRenderer != null) _skeletonRenderer.CustomMaterialOverride[material] = copy;
            Log("preview page " + material.name + ": " + source.name + " " + source.width + "x" + source.height +
                " -> composed " + atlas.width + "x" + atlas.height + " (renderer override)");
        }
    }

    /// <summary>
    /// The arguments for the host's TryBuildSynthesizedAtlasForSkeleton, filled in BY PARAMETER NAME.
    ///
    /// The old Main took (skeleton, sourceTexture, skin, textureName, elfin); the authored Main declares
    /// (skeleton, atlas, textureName, skin, elfin) -- the two strings the other way round. Passing them by
    /// position therefore handed the skin folder in as the texture name and the atlas name in as the skin,
    /// and the build quietly produced nothing usable (2026-11-21). SkinForger.Core has always bound this
    /// method by parameter name, which is why only the Preview was hurt; this does the same, so either
    /// Main works and a future reorder cannot break it again.
    /// </summary>
    private static object[] BuildAtlasArguments(Skeleton skeleton, Texture2D source, string skinPath, string textureName)
    {
        ParameterInfo[] parameters = _buildAtlas!.GetParameters();
        object[] arguments = new object[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            string name = parameters[i].Name ?? string.Empty;
            arguments[i] = name switch
            {
                "skeleton" => skeleton,
                "sourceTexture" or "atlas" => source,
                "skin" or "skinPath" => skinPath,
                "textureName" => textureName,
                "elfin" => false,
                _ => parameters[i].ParameterType == typeof(bool) ? false
                     : parameters[i].ParameterType == typeof(string) ? string.Empty
                     : null!,
            };
        }
        return arguments;
    }

    /// <summary>
    /// Says in one line whether the preview's uvs and its texture agree.
    ///
    /// Fragmentation always has the same shape: a region whose uvs were computed for an atlas of one size,
    /// drawn out of a texture of another (BUG-031, and twice again on 2026-11-21). The page carries the
    /// size its uvs were built for, so comparing the two is the whole diagnosis -- in the log, on every
    /// open, instead of another round of guessing.
    /// </summary>
    private static void CheckPreviewBinding(Skeleton skeleton)
    {
        try
        {
            if (skeleton == null || _skeletonRenderer == null) return;
            ExposedList<Skin> skins = skeleton.Data?.Skins;
            if (skins == null || skins.Count == 0) return;
            Il2CppSystem.Collections.Generic.List<Skin.SkinEntry> entries = new();
            int slots = skeleton.Data.Slots?.Count ?? 0;
            for (int slot = 0; slot < slots; slot++)
            {
                entries.Clear();
                skins.Items[0].GetAttachments(slot, entries);
                for (int i = 0; i < entries.Count; i++)
                {
                    Attachment attachment = entries[i].Attachment;
                    AtlasRegion region = null!;
                    RegionAttachment ra = ((Il2CppObjectBase)attachment).TryCast<RegionAttachment>();
                    if (ra != null) region = ra.RendererObject?.TryCast<AtlasRegion>();
                    MeshAttachment ma = ((Il2CppObjectBase)attachment).TryCast<MeshAttachment>();
                    if (ma != null) region = ma.RendererObject?.TryCast<AtlasRegion>();
                    Material page = region?.page?.rendererObject?.TryCast<Material>();
                    if (region?.page == null || page == null) continue;

                    Material drawn = page;
                    try
                    {
                        if (_skeletonRenderer.CustomMaterialOverride != null &&
                            _skeletonRenderer.CustomMaterialOverride.TryGetValue(page, out Material? over) && over != null)
                            drawn = over;
                    }
                    catch (Exception) { }
                    Texture2D texture = drawn.mainTexture?.TryCast<Texture2D>();
                    int pageWidth = region.page.width, pageHeight = region.page.height;
                    bool agree = texture != null && texture.width == pageWidth && texture.height == pageHeight;
                    string line = "binding check: uvs are for " + pageWidth + "x" + pageHeight +
                                  ", the texture drawn is " + (texture == null ? "<none>" :
                                  texture.name + " " + texture.width + "x" + texture.height) +
                                  " via " + (drawn.Pointer == page.Pointer ? "the page material" : "a renderer override");
                    if (agree) Log(line + " - they agree");
                    else Warning(line + " - THEY DO NOT AGREE, the preview will come out in pieces");
                    return;   // one page is the whole answer
                }
            }
        }
        catch (Exception) { }
    }

    private static Dictionary<int, Material> CollectMaterials(Skeleton skeleton)
    {
        Dictionary<int, Material> result = new();
        ExposedList<Skin> skins = skeleton.Data?.Skins;
        if (skins == null) return result;
        Il2CppSystem.Collections.Generic.List<Skin.SkinEntry> entries = new();
        int slots = skeleton.Data.Slots?.Count ?? 0;
        for (int s = 0; s < skins.Count; s++)
        {
            Skin skin = skins.Items[s];
            for (int slot = 0; slot < slots; slot++)
            {
                entries.Clear();
                skin.GetAttachments(slot, entries);
                for (int i = 0; i < entries.Count; i++)
                {
                    Attachment attachment = entries[i].Attachment;
                    AtlasRegion region = null!;
                    RegionAttachment ra = ((Il2CppObjectBase)attachment).TryCast<RegionAttachment>();
                    if (ra != null) region = ra.RendererObject?.TryCast<AtlasRegion>();
                    MeshAttachment ma = ((Il2CppObjectBase)attachment).TryCast<MeshAttachment>();
                    if (ma != null) region = ma.RendererObject?.TryCast<AtlasRegion>();
                    Material material = region?.page?.rendererObject?.TryCast<Material>();
                    if (material != null) result[material.GetInstanceID()] = material;
                }
            }
        }
        return result;
    }

    private static void DisableGameplayBehaviours(GameObject root, SkeletonRenderer renderer)
    {
        Il2CppArrayBase<MonoBehaviour> behaviours = root.GetComponentsInChildren<MonoBehaviour>(true);
        for (int i = 0; i < behaviours.Length; i++)
        {
            MonoBehaviour behaviour = behaviours[i];
            if (behaviour == null || behaviour.Pointer == renderer.Pointer) continue;
            behaviour.enabled = false;
        }
    }

    private static void SetLayerRecursively(GameObject root, int layer)
    {
        root.layer = layer;
        Transform transform = root.transform;
        for (int i = 0; i < transform.childCount; i++)
            SetLayerRecursively(transform.GetChild(i).gameObject, layer);
    }

    private static void LogRuntimeGraph(string label, GameObject root, SkeletonRenderer renderer)
    {
        MeshFilter filter = renderer.gameObject.GetComponent<MeshFilter>();
        Mesh mesh = filter == null ? null! : filter.sharedMesh;
        int vertices = mesh == null ? 0 : mesh.vertexCount;
        long indices = 0;
        if (mesh != null)
            for (int i = 0; i < mesh.subMeshCount; i++) indices += mesh.GetIndexCount(i);
        Material[] materials = renderer.meshRenderer == null ? Array.Empty<Material>() : renderer.meshRenderer.sharedMaterials;
        Log(label + " root=" + root.name + ", renderer=" + renderer.GetType().FullName +
            ", skeleton=" + PointerOf(renderer.Skeleton) + ", defaultSkin=" + PointerOf(renderer.Skeleton?.Data?.defaultSkin) +
            ", mesh=" + PointerOf(mesh) + ", vertices=" + vertices + ", indices=" + indices +
            ", materials=" + materials.Length + ", layer=" + root.layer +
            ", active=" + root.activeInHierarchy);
    }

    private static void PopulateAnimations(SkeletonData data)
    {
        Animations.Clear();
        if (data?.Animations == null) return;
        for (int i = 0; i < data.Animations.Count; i++)
        {
            string name = data.Animations.Items[i]?.Name;
            if (!string.IsNullOrEmpty(name)) Animations.Add(name);
        }
        _animationIndex = Animations.Count == 0 ? -1 : Math.Min(Math.Max(_animationIndex, 0), Animations.Count - 1);
    }

    private static void CreateCamera()
    {
        _renderTexture = new RenderTexture(640, 480, 24, RenderTextureFormat.ARGB32)
        {
            name = "SkinForger Preview RenderTexture",
            hideFlags = HideFlags.HideAndDontSave
        };
        _renderTexture.Create();
        _cameraRoot = new GameObject("SkinForger Preview Camera") { hideFlags = HideFlags.HideAndDontSave };
        Object.DontDestroyOnLoad(_cameraRoot);
        _camera = _cameraRoot.AddComponent<Camera>();
        _camera.orthographic = true;
        _camera.clearFlags = CameraClearFlags.SolidColor;
        _camera.backgroundColor = new Color(0.10f, 0.11f, 0.13f, 1f);
        _camera.cullingMask = 1 << PreviewLayer;
        _camera.targetTexture = _renderTexture;
        _camera.nearClipPlane = 0.01f;
        _camera.farClipPlane = 100f;
        _cameraRoot.transform.position = new Vector3(0f, 0f, -10f);
    }

    private static void PlaySelected(bool restart)
    {
        if (_animator == null || _animationIndex < 0 || _animationIndex >= Animations.Count) return;
        if (restart)
        {
            _animator.Play(Animations[_animationIndex], 0, 0f);
            _animator.Update(0f);
        }
        _paused = false;
        _animator.speed = SpeedValue(_speedIndex);
    }

    private static void FitCameraToAnimation()
    {
        if (_skeletonRenderer == null || _camera == null) return;
        try
        {
            Bounds bounds = _skeletonRenderer.meshRenderer.bounds;
            if (bounds.size.x <= 0.0001f || bounds.size.y <= 0.0001f)
            {
                SkeletonData data = _skeletonRenderer.Skeleton?.Data;
                bounds = new Bounds(new Vector3(data?.x ?? 0f, data?.y ?? 0f, 0f),
                    new Vector3(Math.Max(data?.width ?? 1f, 1f), Math.Max(data?.height ?? 1f, 1f), 1f));
            }
            float aspect = 640f / 480f;
            float size = Math.Max(bounds.extents.y, bounds.extents.x / aspect) * 1.18f;
            _camera.orthographicSize = Math.Max(size, 0.25f);
            _camera.transform.position = new Vector3(bounds.center.x, bounds.center.y, -10f);
        }
        catch (Exception ex)
        {
            Warning("camera fit failed: " + ex.Message);
        }
    }

    private static void DrawPreviewWindow(int id)
    {
        try
        {
            DrawCharacterSelector();
            DrawContextSelector();
            DrawAnimationSelector();

            Rect previewRect = GUILayoutUtility.GetRect(700f, 430f, new[] { GUILayout.ExpandWidth(true) });
            if (_renderTexture != null) GUI.DrawTexture(previewRect, _renderTexture, ScaleMode.ScaleToFit, false);
            else GUI.Box(previewRect, "Preview unavailable");

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Play"))
            {
                _paused = false;
                if (_animator != null) _animator.speed = SpeedValue(_speedIndex);
            }
            if (GUILayout.Button("Pause"))
            {
                _paused = true;
                if (_animator != null) _animator.speed = 0f;
            }
            if (GUILayout.Button("Restart"))
            {
                PlaySelected(restart: true);
                FitCameraToAnimation();
            }
            bool loop = GUILayout.Toggle(_loop, "Loop", new[] { GUILayout.Width(80f) });
            if (loop != _loop)
            {
                _loop = loop;
                PlaySelected(restart: true);
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Speed", new[] { GUILayout.Width(60f) });
            for (int i = 0; i < SpeedCount; i++)
            {
                Color old = GUI.contentColor;
                if (_speedIndex == i) GUI.contentColor = Color.green;
                float speed = SpeedValue(i);
                if (GUILayout.Button(speed.ToString("0.##") + "x", new[] { GUILayout.Width(70f) }))
                {
                    _speedIndex = i;
                    if (_animator != null && !_paused) _animator.speed = speed;
                }
                GUI.contentColor = old;
            }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Close", new[] { GUILayout.Width(90f) })) Close();
            GUILayout.EndHorizontal();
            GUI.DragWindow(new Rect(0f, 0f, _windowRect.width, 24f));
        }
        catch (Exception ex)
        {
            GUILayout.Label(ex.Message);
            Warning("window failed: " + ex.Message);
        }
    }

    private sealed class PreviewProfile
    {
        private readonly long _start = Stopwatch.GetTimestamp();
        private long _last = Stopwatch.GetTimestamp();

        public PreviewProfile(int openIndex)
        {
            OpenIndex = openIndex;
        }

        public int OpenIndex { get; }

        public List<string> Entries { get; } = new();

        public void Add(string stage, double elapsedMs)
        {
            Entries.Add(stage + "=" + elapsedMs.ToString("0.###") + "ms");
            _last = Stopwatch.GetTimestamp();
        }

        public void Point(string stage)
        {
            long now = Stopwatch.GetTimestamp();
            double elapsed = (now - _last) * 1000.0 / Stopwatch.Frequency;
            Entries.Add(stage + "+=" + elapsed.ToString("0.###") + "ms");
            _last = now;
        }

        public double ElapsedTotal()
        {
            return (Stopwatch.GetTimestamp() - _start) * 1000.0 / Stopwatch.Frequency;
        }
    }

    private static void DrawCharacterSelector()
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label("Character", new[] { GUILayout.Width(90f) });
        string value = CharacterNames.Count == 0 ? "Unavailable" : CharacterNames[_characterIndex];
        if (GUILayout.Button(value + "  v")) _characterExpanded = !_characterExpanded;
        GUILayout.EndHorizontal();
        if (!_characterExpanded) return;
        _selectorScroll = GUILayout.BeginScrollView(_selectorScroll, new[] { GUILayout.Height(Math.Min(170f, 28f * Math.Max(CharacterNames.Count, 1))) });
        for (int i = 0; i < CharacterNames.Count; i++)
        {
            if (!GUILayout.Button(CharacterNames[i])) continue;
            _characterExpanded = false;
            _characterIndex = i;
            LoadContextsAndPreview();
            break;
        }
        GUILayout.EndScrollView();
    }

    private static void DrawContextSelector()
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label("Context", new[] { GUILayout.Width(90f) });
        string value = ContextNames.Count == 0 ? "Unavailable" : ContextNames[_contextIndex];
        if (GUILayout.Button(value + "  v")) _contextExpanded = !_contextExpanded;
        GUILayout.EndHorizontal();
        if (!_contextExpanded) return;
        _selectorScroll = GUILayout.BeginScrollView(_selectorScroll, new[] { GUILayout.Height(Math.Min(170f, 28f * Math.Max(ContextNames.Count, 1))) });
        for (int i = 0; i < ContextNames.Count; i++)
        {
            if (!GUILayout.Button(ContextNames[i])) continue;
            _contextExpanded = false;
            _contextIndex = i;
            _animationIndex = 0;
            BuildCurrentPreview();
            break;
        }
        GUILayout.EndScrollView();
    }

    private static void DrawAnimationSelector()
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label("Animation", new[] { GUILayout.Width(90f) });
        string value = _animationIndex < 0 || _animationIndex >= Animations.Count ? "Unavailable" : Animations[_animationIndex];
        if (GUILayout.Button(value + "  v")) _animationExpanded = !_animationExpanded;
        GUILayout.EndHorizontal();
        if (!_animationExpanded) return;
        _selectorScroll = GUILayout.BeginScrollView(_selectorScroll, new[] { GUILayout.Height(Math.Min(170f, 28f * Math.Max(Animations.Count, 1))) });
        for (int i = 0; i < Animations.Count; i++)
        {
            if (!GUILayout.Button(Animations[i])) continue;
            _animationExpanded = false;
            _animationIndex = i;
            PlaySelected(restart: true);
            FitCameraToAnimation();
            break;
        }
        GUILayout.EndScrollView();
    }

    private static void DestroyPreviewObjects()
    {
        _skeletonRenderer = null;
        _skeletonMecanim = null;
        _animator = null;
        _camera = null;
        if (_cameraRoot != null) Object.Destroy(_cameraRoot);
        if (_previewRoot != null) Object.Destroy(_previewRoot);
        if (_renderTexture != null)
        {
            _renderTexture.Release();
            Object.Destroy(_renderTexture);
        }
        for (int i = 0; i < OwnedMaterials.Count; i++)
            if (OwnedMaterials[i] != null) Object.Destroy(OwnedMaterials[i]);
        OwnedMaterials.Clear();
        _cameraRoot = null;
        _previewRoot = null;
        _renderTexture = null;
    }

    private static string ContextDisplayName(string field)
    {
        string value = field ?? string.Empty;
        if (value.EndsWith("Show", StringComparison.OrdinalIgnoreCase)) value = value[..^4];
        if (value.Length == 0) return "Skeleton";
        return char.ToUpperInvariant(value[0]) + value[1..];
    }

    private static float SpeedValue(int index)
    {
        return index switch
        {
            0 => 0.25f,
            1 => 0.5f,
            3 => 2f,
            _ => 1f
        };
    }

    private static void ClearContexts()
    {
        ContextFields.Clear();
        ContextNames.Clear();
        ContextResources.Clear();
        ContextPrefabs.Clear();
        ContextAssets.Clear();
        ContextTemplates.Clear();
    }

    private static int HostInt(MethodInfo? method, params object[] args)
    {
        object? value = method?.Invoke(null, args);
        return value is int number ? number : -1;
    }

    private static string HostString(MethodInfo? method, params object[] args)
    {
        return method?.Invoke(null, args) as string ?? string.Empty;
    }

    private static long PointerOf(Il2CppObjectBase value) => value == null ? 0L : value.Pointer.ToInt64();
    private static void Log(string message) => MelonLogger.Msg(Prefix + " " + message);
    private static void Warning(string message) => MelonLogger.Warning(Prefix + " " + message);
}
