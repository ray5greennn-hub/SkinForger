using System.Reflection;
using HarmonyLib;
using MelonLoader;

// One melon for every SkinForger sidecar. Priority -10000 so the library resolver (former Bootstrap) is registered
// before SkinForger.Melon.dll initializes; the other modules are initialized right after Main's OnInitializeMelon,
// exactly where they ran as separate melons before.
[assembly: MelonInfo(typeof(SkinForger.Core.SkinForgerCoreMod), "SkinForger Core", "1.4.0", "SkinForger")]
[assembly: MelonGame(null, "MuseDash")]
[assembly: MelonPriority(-10000)]
[assembly: HarmonyDontPatchAll]

namespace SkinForger.Core;

/// <summary>Stand-in for MelonMod used by each former sidecar. Same callback names and signatures.</summary>
public abstract class SfModule
{
    public string ModuleName { get; internal set; } = string.Empty;
    public MelonLogger.Instance LoggerInstance { get; internal set; } = null!;
    public HarmonyLib.Harmony HarmonyInstance { get; internal set; } = null!;

    public virtual void OnInitializeMelon() { }
    public virtual void OnDeinitializeMelon() { }
    public virtual void OnUpdate() { }
    public virtual void OnLateUpdate() { }
    public virtual void OnGUI() { }
    public virtual void OnSceneWasLoaded(int buildIndex, string sceneName) { }
    public virtual void OnSceneWasInitialized(int buildIndex, string sceneName) { }
    public virtual void OnApplicationQuit() { }
}

public sealed class SkinForgerCoreMod : MelonMod
{
    private const string Prefix = "[SkinForge][Core]";
    private const string MainType = "SkinForger.SkinForgerMod";

    // Former melon names, in the order MelonLoader initialized them (alphabetical, after Main).
    private static readonly (string Name, Func<SfModule> Create)[] Modules =
    {
        ("SkinForger Atlas Build Accelerator", () => new SkinForger.AtlasBuildAccelerator.AtlasBuildAcceleratorMod()),
        ("SkinForger Battle Atlas Edge Isolation", () => new SkinForger.BattleAtlasEdgeIsolation.BattleAtlasEdgeIsolationMod()),
        ("SkinForger Character Polygon Packed Rect Repair", () => new SkinForger.CharacterPolygonPackedRectRepair.CharacterPolygonPackedRectRepairMod()),
        ("SkinForger Elfin Fresh Packed Rect", () => new SkinForger.ElfinFreshPackedRect.ElfinFreshPackedRectMod()),
        ("SkinForger Elfin Mesh Canvas", () => new SkinForger.ElfinMeshCanvas.ElfinMeshCanvasMod()),
        ("SkinForger Fast Reload", () => new SkinForger.FastReload.FastReloadMod()),
        ("SkinForger Gameplay Atlas Warm Reuse", () => new SkinForger.GameplayAtlasWarmReuse.GameplayAtlasWarmReuseMod()),
        ("SkinForger Original State Restore", () => new SkinForger.OriginalStateRestore.OriginalStateRestoreMod()),
        ("SkinForger Preview Enhancements", () => new SkinForger.PreviewEnhancements.PreviewEnhancementsMod()),
        ("SkinForger Preview Lifecycle Guard", () => new SkinForger.PreviewLifecycleGuard.PreviewLifecycleGuardMod()),
        ("SkinForger R4-12 Mesh Canvas", () => new SkinForger.R412.Runtime.R412MeshCanvasMod()),
        ("SkinForger RuntimeTexture Bilinear Probe", () => new SkinForger.RuntimeTextureBilinearProbe.RuntimeTextureBilinearProbeMod()),
        ("SkinForger UI Loading Prewarm", () => new SkinForger.UI.UIFirstOpenLoadingPrewarmMod()),
        ("SkinForger Window Input Shield", () => new SkinForger.WindowInputShield.WindowInputShieldMod()),
        // 1.1: never dump / read / replace censored (hexie) resources. Its prefixes use Priority.First.
        ("SkinForger Hexie Block", () => new SkinForger.HexieBlock.HexieBlockMod()),
        // 1.3: page tabs (Image | Export Skin Package) and .sfskin export from SkinForger/SkinCreator.
        ("SkinForger Skin Packages", () => new SkinForger.SkinPackages.SkinPackagesMod()),
        // 1.4 (2026-11-16): Bad Apple silhouettes, the former separate SkinForger.TouhouCompat.dll (no longer deployed).
        ("SkinForger Touhou Compat", () => new SkinForger.TouhouCompat.TouhouCompatMod()),
    };

    private static readonly List<SfModule> Active = new();
    private static readonly HashSet<string> CallbackFailures = new();
    private static bool _modulesStarted;

    // Register the SkinForger.* resolver as early as possible (the former Bootstrap also did this statically).
    // 1.2: Core only serves the Steam-only Main build, which references SkinForger.Core (HexieBlock.MainGuard).
    // With any other Main (e.g. the old 11C58361 that still contains the censored-resource features) Core does not
    // register the library resolver and starts nothing, so that Main cannot initialize either.
    private static readonly bool MainBound = IsMainBound(out MainBindingDetail);
    private static readonly string MainBindingDetail;

    static SkinForgerCoreMod()
    {
        if (MainBound)
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(SkinForger.Bootstrap.BootstrapMod).TypeHandle);
    }

    private static bool IsMainBound(out string detail)
    {
        Assembly? main = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, "SkinForger.Melon", StringComparison.OrdinalIgnoreCase));
        if (main == null) { detail = "SkinForger.Melon not loaded"; return false; }
        bool bound = main.GetReferencedAssemblies().Any(r => string.Equals(r.Name, "SkinForger.Core", StringComparison.OrdinalIgnoreCase));
        detail = bound ? "bound" : "SkinForger.Melon does not reference SkinForger.Core (not the Steam-only build)";
        return bound;
    }

    public override void OnInitializeMelon()
    {
        if (!MainBound)
        {
            LoggerInstance.Error($"{Prefix} SkinForger disabled: {MainBindingDetail}. Use the official SkinForger package for the Steam release of Muse Dash.");
            return;
        }
        // 2026-11-17: before Main's OnInitializeMelon (it builds SkinCreator at start-up).
        try { SkinForger.SkinPackages.LazyCreatorFolder.InstallEarly(HarmonyInstance); }
        catch (Exception exception) { LoggerInstance.Warning($"{Prefix} LazyCreatorFolder not installed: {exception.GetBaseException().Message}"); }
        Start(new SkinForger.Bootstrap.BootstrapMod(), "SkinForger Bootstrap");

        MethodInfo? mainInit = AccessTools.TypeByName(MainType)?.GetMethod(nameof(OnInitializeMelon),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
        if (mainInit == null)
        {
            LoggerInstance.Warning($"{Prefix} {MainType}.OnInitializeMelon not found; starting modules now.");
            StartModules();
            return;
        }

        // A finalizer runs even if Main's initialization throws, like the separate melons did.
        HarmonyInstance.Patch(mainInit, finalizer: new HarmonyMethod(typeof(SkinForgerCoreMod), nameof(MainInitializedFinalizer)));
        LoggerInstance.Msg($"{Prefix} waiting for SkinForger Main initialization ({Modules.Length} modules).");
    }

    private static void MainInitializedFinalizer() => StartModules();

    private static void StartModules()
    {
        if (_modulesStarted) return;
        _modulesStarted = true;
        foreach ((string name, Func<SfModule> create) in Modules)
        {
            SfModule module;
            try { module = create(); }
            catch (Exception exception) { MelonLogger.Error($"{Prefix} {name} could not be created: {exception}"); continue; }
            Start(module, name);
        }
        MelonLogger.Msg($"{Prefix} ready version=1.3.0 modules={Active.Count}/{Modules.Length + 1}");
    }

    private static void Start(SfModule module, string name)
    {
        module.ModuleName = name;
        module.LoggerInstance = new MelonLogger.Instance(name);
        module.HarmonyInstance = new HarmonyLib.Harmony("SkinForger.Core." + name.Replace(' ', '_'));
        try
        {
            module.OnInitializeMelon();
            Active.Add(module);
        }
        catch (Exception exception)
        {
            MelonLogger.Error($"{Prefix} {name} failed to initialize: {exception}");
        }
    }

    private static void ForEach(string callback, Action<SfModule> action)
    {
        for (int i = 0; i < Active.Count; i++)
        {
            SfModule module = Active[i];
            try { action(module); }
            catch (Exception exception)
            {
                if (CallbackFailures.Add(module.ModuleName + "|" + callback))
                    MelonLogger.Error($"{Prefix} {module.ModuleName}.{callback} threw (logged once): {exception}");
            }
        }
    }

    public override void OnUpdate() => ForEach(nameof(OnUpdate), m => m.OnUpdate());
    public override void OnLateUpdate() => ForEach(nameof(OnLateUpdate), m => m.OnLateUpdate());
    public override void OnGUI() => ForEach(nameof(OnGUI), m => m.OnGUI());
    public override void OnSceneWasLoaded(int buildIndex, string sceneName) =>
        ForEach(nameof(OnSceneWasLoaded), m => m.OnSceneWasLoaded(buildIndex, sceneName));
    public override void OnSceneWasInitialized(int buildIndex, string sceneName) =>
        ForEach(nameof(OnSceneWasInitialized), m => m.OnSceneWasInitialized(buildIndex, sceneName));
    public override void OnApplicationQuit() => ForEach(nameof(OnApplicationQuit), m => m.OnApplicationQuit());
    public override void OnDeinitializeMelon() => ForEach(nameof(OnDeinitializeMelon), m => m.OnDeinitializeMelon());
}
