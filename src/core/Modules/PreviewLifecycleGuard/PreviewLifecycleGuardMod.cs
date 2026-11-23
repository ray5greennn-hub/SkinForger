using System.Reflection;
using MelonLoader;

namespace SkinForger.PreviewLifecycleGuard;

public sealed class PreviewLifecycleGuardMod : global::SkinForger.Core.SfModule
{
    private const string Prefix = "[SkinForge][PreviewLifecycleGuard]";
    private const int MaxFailureLogs = 8;

    private static FieldInfo? _previewOpen;
    private static MethodInfo? _closePreview;
    private static bool _resolved;
    private static int _failureLogs;

    public override void OnInitializeMelon()
    {
        ResolveContracts();
        if (_resolved)
            MelonLogger.Msg($"{Prefix} ready version=0.1.0 policy=close-on-menu-hidden-or-scene-load");
    }

    public override void OnUpdate()
    {
        if (!ResolveContracts()) return;

        try
        {
            global::SkinForger.MainBridge.MainHost.TryWindowShown(out bool menuVisible);
            bool previewOpen = (bool)(_previewOpen!.GetValue(null) ?? false);
            if (PreviewLifecyclePolicy.ShouldCloseForMenuState(menuVisible, previewOpen))
                CloseOwnedPreview("menu-hidden", menuVisible);
        }
        catch (Exception exception)
        {
            LogFailure("menu-state", exception);
        }
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        if (!ResolveContracts()) return;

        try
        {
            bool previewOpen = (bool)(_previewOpen!.GetValue(null) ?? false);
            if (PreviewLifecyclePolicy.ShouldCloseForSceneLoad(previewOpen))
                CloseOwnedPreview($"scene-loaded:{buildIndex}:{sceneName}", menuVisible: null);
        }
        catch (Exception exception)
        {
            LogFailure("scene-load", exception);
        }
    }

    public override void OnDeinitializeMelon()
    {
        if (!ResolveContracts()) return;

        try
        {
            bool previewOpen = (bool)(_previewOpen!.GetValue(null) ?? false);
            if (previewOpen) CloseOwnedPreview("mod-deinitialize", menuVisible: null);
        }
        catch (Exception exception)
        {
            LogFailure("deinitialize", exception);
        }
    }

    private static bool ResolveContracts()
    {
        if (_resolved) return true;

        Type? host = FindType("SkinForger.SkinForgerMod");
        Type? preview = FindType("SkinForger.Preview.PreviewManager");
        _previewOpen = preview?.GetField("_open", BindingFlags.Static | BindingFlags.NonPublic);
        _closePreview = preview?.GetMethod("Close", BindingFlags.Static | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
        _resolved = global::SkinForger.MainBridge.MainHost.WindowStateAvailable &&
                    _previewOpen?.FieldType == typeof(bool) &&
                    _closePreview?.ReturnType == typeof(void);

        if (!_resolved && _failureLogs++ < MaxFailureLogs)
            MelonLogger.Warning($"{Prefix} contract unavailable host={host?.Assembly.FullName ?? "missing"} preview={preview?.Assembly.FullName ?? "missing"}");

        return _resolved;
    }

    private static Type? FindType(string fullName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type = assembly.GetType(fullName, throwOnError: false, ignoreCase: false);
            if (type != null) return type;
        }
        return null;
    }

    private static void CloseOwnedPreview(string reason, bool? menuVisible)
    {
        _closePreview!.Invoke(null, null);
        bool stillOpen = (bool)(_previewOpen!.GetValue(null) ?? false);
        MelonLogger.Msg($"{Prefix} cleanup reason={reason} menuVisible={menuVisible?.ToString() ?? "unknown"} previewOpenAfter={stillOpen} status={(stillOpen ? "failed" : "closed")}");
    }

    private static void LogFailure(string stage, Exception exception)
    {
        if (_failureLogs++ >= MaxFailureLogs) return;
        Exception root = exception is TargetInvocationException { InnerException: not null }
            ? exception.InnerException
            : exception.GetBaseException();
        MelonLogger.Warning($"{Prefix} failure stage={stage} type={root.GetType().Name} message={root.Message}");
    }
}
