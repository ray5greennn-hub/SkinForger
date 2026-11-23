using System.IO.Enumeration;
using System.Text;

namespace SkinForger.SkinPackages;

/// <summary>
/// In-memory file tree for mounted .sfskin packages. Main, the Preview runtime and Core read skin files through
/// these methods (Main / Runtime calls are redirected here by the Cecil tool; Core modules call them directly).
/// A path inside a mounted package is answered from memory; every other path goes to the real file system
/// unchanged. Package contents are never written to disk.
/// Pure .NET (no Unity / MelonLoader) so it can be tested outside the game. Must work before any module starts.
/// </summary>
public static class VirtualFiles
{
    public sealed class Tree
    {
        public static readonly Tree Empty = new(new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase));

        public readonly Dictionary<string, byte[]> Files;
        public readonly Dictionary<string, DateTime> FileTimes;
        public readonly HashSet<string> Directories = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, List<string>> ChildDirectories = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, List<string>> ChildFiles = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Roots = new();

        /// <param name="files">normalized absolute path -> content</param>
        /// <param name="times">normalized absolute path -> package write time (UTC)</param>
        public Tree(Dictionary<string, byte[]> files, Dictionary<string, DateTime> times)
        {
            Files = files;
            FileTimes = times;
        }

        /// <summary>Registers the directories of every file up to (and including) <paramref name="root"/>.</summary>
        public void Index(string root)
        {
            root = Normalize(root);
            Roots.Add(root);
            Directories.Add(root);
            static void Link(Dictionary<string, List<string>> map, string parent, string child)
            {
                if (!map.TryGetValue(parent, out List<string>? list)) map[parent] = list = new List<string>();
                if (!list.Contains(child, StringComparer.OrdinalIgnoreCase)) list.Add(child);
            }
            foreach (string file in Files.Keys.Where(f => f.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            {
                string directory = Path.GetDirectoryName(file)!;
                Link(ChildFiles, directory, file);
                // Walk up to the root, registering each directory under its parent.
                while (!string.Equals(directory, root, StringComparison.OrdinalIgnoreCase))
                {
                    string parent = Path.GetDirectoryName(directory)!;
                    Link(ChildDirectories, parent, directory);
                    if (!Directories.Add(directory)) break;   // already registered with all its ancestors
                    directory = parent;
                }
            }
        }
    }

    private static volatile Tree _tree = Tree.Empty;

    public static Tree Current => _tree;

    public static void Replace(Tree tree) => _tree = tree ?? Tree.Empty;

    public static string Normalize(string path)
    {
        string full = Path.GetFullPath(path);
        return full.Length > 3 ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }

    /// <summary>Null for paths that cannot be inside a package (also: nothing mounted).</summary>
    private static string? Key(string? path)
    {
        Tree tree = _tree;
        if (tree.Roots.Count == 0 || string.IsNullOrEmpty(path)) return null;
        try
        {
            string key = Normalize(path);
            foreach (string root in tree.Roots)
                if (key.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                    (key.Length == root.Length || key[root.Length] == Path.DirectorySeparatorChar))
                    return key;
            return null;
        }
        catch
        {
            return null;
        }
    }

    public static bool IsVirtual(string? path) => Key(path) != null;

    // ------------------------------------------------------------ System.IO replacements (same signatures)

    public static bool FileExists(string? path)
    {
        string? key = Key(path);
        return key != null ? _tree.Files.ContainsKey(key) : File.Exists(path) || LazyCreatorFolder.TryPending(path, out _);
    }

    public static bool DirectoryExists(string? path)
    {
        string? key = Key(path);
        return key != null ? _tree.Directories.Contains(key) : Directory.Exists(path);
    }

    public static byte[] ReadAllBytes(string path)
    {
        string? key = Key(path);
        if (key == null) return File.ReadAllBytes(path);
        return _tree.Files.TryGetValue(key, out byte[]? data) ? (byte[])data.Clone() : throw new FileNotFoundException("Not in skin package", path);
    }

    public static string ReadAllText(string path)
    {
        string? key = Key(path);
        if (key == null)
            return !File.Exists(path) && LazyCreatorFolder.TryPending(path, out string pending) ? pending : File.ReadAllText(path);
        if (!_tree.Files.TryGetValue(key, out byte[]? data)) throw new FileNotFoundException("Not in skin package", path);
        using var reader = new StreamReader(new MemoryStream(data, false), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    public static string[] GetDirectories(string path)
    {
        string? key = Key(path);
        if (key == null) return LazyCreatorFolder.ListAsEmpty(path) ? Array.Empty<string>() : Directory.GetDirectories(path);
        if (!_tree.Directories.Contains(key)) throw new DirectoryNotFoundException(path);
        return _tree.ChildDirectories.TryGetValue(key, out List<string>? list) ? list.ToArray() : Array.Empty<string>();
    }

    public static string[] GetFiles(string path) => GetFiles(path, "*", SearchOption.TopDirectoryOnly);

    public static string[] GetFiles(string path, string searchPattern, SearchOption searchOption)
    {
        string? key = Key(path);
        if (key == null) return LazyCreatorFolder.ListAsEmpty(path) ? Array.Empty<string>() : Directory.GetFiles(path, searchPattern, searchOption);
        if (!_tree.Directories.Contains(key)) throw new DirectoryNotFoundException(path);
        IEnumerable<string> files = searchOption == SearchOption.AllDirectories
            ? _tree.Files.Keys.Where(f => f.StartsWith(key + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            : _tree.ChildFiles.TryGetValue(key, out List<string>? list) ? list : Enumerable.Empty<string>();
        return files.Where(f => FileSystemName.MatchesSimpleExpression(searchPattern, Path.GetFileName(f))).ToArray();
    }

    public static FileAttributes GetAttributes(string path)
    {
        string? key = Key(path);
        if (key == null) return File.GetAttributes(path);
        if (_tree.Files.ContainsKey(key)) return FileAttributes.ReadOnly;
        if (_tree.Directories.Contains(key)) return FileAttributes.Directory | FileAttributes.ReadOnly;
        throw new FileNotFoundException("Not in skin package", path);
    }

    /// <summary>Replaces <c>callvirt FileInfo::get_Length</c>.</summary>
    public static long FileLength(FileInfo info)
    {
        string? key = Key(info.FullName);
        if (key == null) return info.Length;
        return _tree.Files.TryGetValue(key, out byte[]? data) ? data.LongLength : throw new FileNotFoundException("Not in skin package", info.FullName);
    }

    /// <summary>Replaces <c>callvirt FileSystemInfo::get_LastWriteTimeUtc</c>.</summary>
    public static DateTime LastWriteTimeUtc(FileSystemInfo info)
    {
        string? key = Key(info.FullName);
        if (key == null) return info.LastWriteTimeUtc;
        return _tree.FileTimes.TryGetValue(key, out DateTime time) ? time : DateTime.FromFileTimeUtc(0);
    }

    /// <summary>Replaces <c>callvirt FileSystemInfo::get_Exists</c>.</summary>
    public static bool InfoExists(FileSystemInfo info)
    {
        string? key = Key(info.FullName);
        if (key == null) return info.Exists;
        return info is DirectoryInfo ? _tree.Directories.Contains(key) : _tree.Files.ContainsKey(key);
    }

    /// <summary>Read-only stream (Core modules that read PNG headers).</summary>
    public static Stream OpenRead(string path)
    {
        string? key = Key(path);
        if (key == null) return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return _tree.Files.TryGetValue(key, out byte[]? data) ? new MemoryStream(data, false) : throw new FileNotFoundException("Not in skin package", path);
    }
}
