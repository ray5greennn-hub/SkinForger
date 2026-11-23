using System.Reflection;
using MelonLoader;

namespace SkinForger.Bootstrap;

/// <summary>
/// Lets the whole SkinForger package live in one folder (Mods\SkinForger). SkinForger.Melon.dll references
/// SkinForger.PreviewOriginalContractRestore.Runtime.dll (a plain library, not a melon), which used to sit in UserLibs.
/// MelonLoader does not load non-melon DLLs from Mods, so this resolves SkinForger.* references from this folder.
/// </summary>
public sealed class BootstrapMod : global::SkinForger.Core.SfModule
{
    private static bool _registered;
    private static string _directory = string.Empty;

    static BootstrapMod() => Register();

    public override void OnInitializeMelon()
    {
        Register();
        LoggerInstance.Msg($"[SkinForge][Bootstrap] ready: SkinForger.* libraries resolve from {_directory}");
    }

    private static void Register()
    {
        if (_registered) return;
        _registered = true;
        _directory = Path.GetDirectoryName(typeof(BootstrapMod).Assembly.Location) ?? string.Empty;
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
    }

    private static Assembly? Resolve(object? sender, ResolveEventArgs args)
    {
        string? name = new AssemblyName(args.Name).Name;
        if (string.IsNullOrEmpty(name) || !name.StartsWith("SkinForger.", StringComparison.OrdinalIgnoreCase)) return null;
        Assembly? loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
        if (loaded != null) return loaded;
        string path = Path.Combine(_directory, name + ".dll");
        return File.Exists(path) ? Assembly.LoadFrom(path) : null;
    }
}
