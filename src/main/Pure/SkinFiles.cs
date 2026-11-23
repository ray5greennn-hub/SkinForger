using System.Reflection;

namespace SkinForger.Main.Pure;

/// <summary>
/// Every READ of a skin file goes through here.
///
/// Why it exists: a .sfskin package is mounted into an IN-MEMORY file tree
/// (SkinForger.Core's SkinPackages\VirtualFiles — "Package contents are never written to disk"), and its
/// own documentation says who is supposed to read through it:
///
///     Main, the Preview runtime and Core read skin files through these methods
///     (Main / Runtime calls are redirected here by the Cecil tool; Core modules call them directly).
///
/// That redirect was a CECIL-ONLY CONTRACT: the old Main's own source called System.IO, and the build
/// step rewrote those calls. The authored Main is compiled and not post-processed, so it saw the real
/// file system only — a path inside a package simply did not exist for it, every part of a packaged skin
/// was skipped, and nothing was logged. This class is that redirect written out, which is the same thing
/// StartBundledFeatures() did for the injected Initialize() calls.
///
/// It finds Core by type name rather than by reference, because Main is the host: Core patches Main, not
/// the other way round, and Main must keep working with no Core at all. Until Core is found — and forever
/// if it never is — every call is the plain System.IO one, so nothing changes for a skin in a folder.
///
/// READS ONLY. A package is read-only and Main's writes (the Dump, the sidecars it fills in, the
/// selection files) all target the real creator workspace; routing them would mean pretending to write
/// into memory. Those stay on System.IO deliberately.
/// Pure (no Unity, no MelonLoader).
/// </summary>
public static class SkinFiles
{
    /// <summary>The Core type this forwards to, when it is loaded.</summary>
    public const string VirtualFilesType = "SkinForger.SkinPackages.VirtualFiles";

    /// <summary>The Core type that says where the enabled packages' Images\Other pictures were mounted.</summary>
    public const string PackageMountsType = "SkinForger.SkinPackages.PackageMounts";

    private static Func<string?, bool>? _fileExists;
    private static Func<string?, bool>? _directoryExists;
    private static Func<string, byte[]>? _readAllBytes;
    private static Func<string, string>? _readAllText;
    private static Func<string, string[]>? _getDirectories;
    private static Func<string, string, SearchOption, string[]>? _getFiles;
    private static Func<string?, bool>? _isVirtual;

    private static int _assembliesSeen = -1;
    private static bool _bound;
    private static Func<string?>? _packageOther;

    /// <summary>Whether the package reader was found. Only for reporting — every call works either way.</summary>
    public static bool Routed { get { Bind(); return _bound; } }

    /// <summary>
    /// Looks for Core's reader, and looks again whenever another assembly has been loaded.
    ///
    /// Main runs first and Core is a separate MelonLoader mod, so the first few calls happen before Core
    /// exists. Binding once and giving up would leave packages unreadable for the rest of the session for
    /// no better reason than start-up order.
    /// </summary>
    private static void Bind()
    {
        Assembly[] assemblies;
        try { assemblies = AppDomain.CurrentDomain.GetAssemblies(); }
        catch (Exception) { return; }
        if (_bound || assemblies.Length == _assembliesSeen) return;
        _assembliesSeen = assemblies.Length;

        foreach (Assembly assembly in assemblies)
        {
            Type? type;
            try { type = assembly.GetType(VirtualFilesType, throwOnError: false); }
            catch (Exception) { continue; }
            if (type == null) continue;

            try
            {
                var fileExists = Make<Func<string?, bool>>(type, "FileExists", typeof(string));
                var readAllBytes = Make<Func<string, byte[]>>(type, "ReadAllBytes", typeof(string));
                if (fileExists == null || readAllBytes == null) continue;   // not the type we mean

                _fileExists = fileExists;
                _readAllBytes = readAllBytes;
                _directoryExists = Make<Func<string?, bool>>(type, "DirectoryExists", typeof(string));
                _readAllText = Make<Func<string, string>>(type, "ReadAllText", typeof(string));
                _getDirectories = Make<Func<string, string[]>>(type, "GetDirectories", typeof(string));
                _getFiles = Make<Func<string, string, SearchOption, string[]>>(
                    type, "GetFiles", typeof(string), typeof(string), typeof(SearchOption));
                _isVirtual = Make<Func<string?, bool>>(type, "IsVirtual", typeof(string));
                _packageOther = MakeGetter(assembly);
                _bound = true;
                return;
            }
            catch (Exception) { /* keep looking: a wrong type of the same name must not disable packages */ }
        }
    }

    /// <summary>
    /// PackageMounts.OtherPictures: the virtual folder holding the enabled packages' Images\Other
    /// pictures. A PROPERTY, so it has to be read every time -- the mount changes whenever the player
    /// turns a package on or off, and it is null while none of them carry any such picture.
    /// </summary>
    private static Func<string?>? MakeGetter(Assembly assembly)
    {
        try
        {
            Type? mounts = assembly.GetType(PackageMountsType, throwOnError: false);
            MethodInfo? getter = mounts?.GetProperty("OtherPictures",
                BindingFlags.Public | BindingFlags.Static)?.GetGetMethod();
            if (getter == null) return null;
            return (Func<string?>)Delegate.CreateDelegate(typeof(Func<string?>), getter);
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Where the enabled packages' loose pictures are mounted, or null when there are none.
    ///
    /// Deliberately NOT merged into the creator's own Images\Other: that folder is a real one on disk,
    /// and a virtual directory answers for its whole path, so mounting over it would hide every file the
    /// player put there. The caller reads both and lets the player's own file win.
    /// </summary>
    public static string? PackageOtherPictures
    {
        get
        {
            Bind();
            try { return _packageOther?.Invoke(); }
            catch (Exception) { return null; }
        }
    }

    private static TDelegate? Make<TDelegate>(Type type, string name, params Type[] parameters)
        where TDelegate : Delegate
    {
        MethodInfo? method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Static,
            binder: null, types: parameters, modifiers: null);
        if (method == null) return null;
        try { return (TDelegate)Delegate.CreateDelegate(typeof(TDelegate), method); }
        catch (Exception) { return null; }
    }

    /// <summary>Whether this path lives inside a mounted package rather than on disk.</summary>
    public static bool IsInPackage(string? path)
    {
        Bind();
        try { return _isVirtual != null && _isVirtual(path); }
        catch (Exception) { return false; }
    }

    public static bool Exists(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        Bind();
        try { if (_fileExists != null) return _fileExists(path); }
        catch (Exception) { }
        return File.Exists(path);
    }

    public static bool DirectoryExists(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        Bind();
        try { if (_directoryExists != null) return _directoryExists(path); }
        catch (Exception) { }
        return Directory.Exists(path);
    }

    public static byte[] ReadAllBytes(string path)
    {
        Bind();
        if (_readAllBytes != null) return _readAllBytes(path);
        return File.ReadAllBytes(path);
    }

    public static string ReadAllText(string path)
    {
        Bind();
        if (_readAllText != null) return _readAllText(path);
        return File.ReadAllText(path);
    }

    public static string[] GetDirectories(string path)
    {
        Bind();
        try { if (_getDirectories != null) return _getDirectories(path); }
        catch (Exception) { }
        return Directory.GetDirectories(path);
    }

    public static string[] GetFiles(string path) =>
        GetFiles(path, "*", SearchOption.TopDirectoryOnly);

    public static string[] GetFiles(string path, string searchPattern, SearchOption searchOption)
    {
        Bind();
        try { if (_getFiles != null) return _getFiles(path, searchPattern, searchOption); }
        catch (Exception) { }
        return Directory.GetFiles(path, searchPattern, searchOption);
    }
}
