using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using Newtonsoft.Json;

namespace SkinForger
{
    // Embedded in the main Mod by the build tool; never deployed as another DLL.
    public static class ReplacedBackup
    {
        private static string _skinsRoot;
        private static string _statePath;
        private static bool _ready;
        private static Dictionary<string, string> _known;

        public static void Initialize(string skinsRoot)
        {
            if (string.IsNullOrEmpty(skinsRoot)) return;
            string root = Path.GetFullPath(skinsRoot).TrimEnd(Path.DirectorySeparatorChar);
            if (_ready && string.Equals(root, _skinsRoot, StringComparison.OrdinalIgnoreCase)) return;
            _ready = false;
            try
            {
                RequireResourcePath(root);
                _skinsRoot = root;
                _statePath = Path.Combine(Path.GetDirectoryName(root), "UserData", "SkinForger", "replaced-state-v1.json");
                RequireResourcePath(_statePath);
                _known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                bool existing = File.Exists(_statePath);
                if (existing)
                {
                    var saved = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(_statePath));
                    if (saved == null) throw new IOException("Invalid backup state file");
                    // An entry that does not belong (a file that moved out of the tracked tree, a hand-edited
                    // line) is DROPPED, not fatal. 2026-11-20: one unrecognised entry used to invalidate the
                    // whole state, which switched off every backup-dependent write for the session -- and the
                    // only sign of it was a line nobody could see, because this class logged to a console.
                    int skipped = 0;
                    foreach (var pair in saved)
                    {
                        if (!IsTrackedResource(pair.Key) || !IsHash(pair.Value)) { skipped++; continue; }
                        string full = Path.GetFullPath(pair.Key);
                        if (!_known.ContainsKey(full)) _known.Add(full, pair.Value);
                    }
                    if (skipped > 0)
                        Log("[ReplacedBackup] " + skipped.ToString(CultureInfo.InvariantCulture) +
                            " state entry/entries did not belong to " + _skinsRoot + " and were dropped");
                }
                int enrolled = 0;
                if (Directory.Exists(root))
                    foreach (string costume in ResourceDirectories(root)) enrolled += EnrollTree(costume);
                if (enrolled > 0 || !existing) SaveState();
                _ready = true;
                Log("[ReplacedBackup] Baseline ready: new=" + enrolled.ToString(CultureInfo.InvariantCulture) +
                    ", tracked=" + _known.Count.ToString(CultureInfo.InvariantCulture) + "; no files copied");
            }
            catch (Exception ex)
            {
                Log("[ReplacedBackup] State unavailable; backup-dependent writes will be blocked: " + ex.Message);
            }
        }

        private static bool IsHash(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char c in value)
                if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        private static bool IsTrackedResource(string path)
        {
            string full = Path.GetFullPath(path);
            string parent = Path.GetDirectoryName(full);
            if (!full.StartsWith(_skinsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(parent, _skinsRoot, StringComparison.OrdinalIgnoreCase) || IsExcludedPath(full)) return false;
            string extension = Path.GetExtension(full);
            return extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
                (extension.Equals(".png", StringComparison.OrdinalIgnoreCase) &&
                 !(Contains(Path.GetFileName(full), "atlas") && Contains(Path.GetFileName(full), "debug")));
        }

        private static string HashStream(Stream input)
        {
            using (SHA256 sha = SHA256.Create()) return Convert.ToHexString(sha.ComputeHash(input));
        }

        private static string HashBytes(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create()) return Convert.ToHexString(sha.ComputeHash(bytes));
        }

        private static int EnrollTree(string directory)
        {
            int count = 0;
            foreach (string file in Directory.GetFiles(directory))
            {
                string full = Path.GetFullPath(file);
                if (!IsTrackedResource(full) || _known.ContainsKey(full)) continue;
                using (var input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read))
                    _known.Add(full, HashStream(input));
                count++;
            }
            foreach (string child in ResourceDirectories(directory)) count += EnrollTree(child);
            return count;
        }

        private static void EnsureReady()
        {
            if (!_ready) throw new IOException("SkinForger backup state is unavailable; operation cancelled");
        }

        private static void SaveState()
        {
            RequireResourcePath(_statePath);
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath));
            string temp = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                byte[] data = System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(_known, Formatting.Indented));
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    output.Write(data, 0, data.Length);
                    output.Flush(true);
                }
                File.Move(temp, _statePath, true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        private static void Remember(string path, string hash)
        {
            string previous;
            bool existed = _known.TryGetValue(path, out previous);
            _known[path] = hash;
            try { SaveState(); }
            catch
            {
                if (existed) _known[path] = previous;
                else _known.Remove(path);
                throw;
            }
        }
        public static bool IsExcludedPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return true;
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full);
            string current = root;
            string[] parts = full.Substring(root.Length).Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                if (part.Equals("replaced", StringComparison.OrdinalIgnoreCase) ||
                    part.Equals("_Atlas", StringComparison.OrdinalIgnoreCase) ||
                    part.Equals("_Debug", StringComparison.OrdinalIgnoreCase) ||
                    (part.StartsWith("_", StringComparison.Ordinal) &&
                     (Contains(part, "atlas") || Contains(part, "debug") || Contains(part, "cache") || Contains(part, "backup"))))
                    return true;
                current = Path.Combine(current, part);
                if ((Directory.Exists(current) || File.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    return true;
            }
            return false;
        }

        private static bool Contains(string value, string needle)
        {
            return value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static string[] ResourceDirectories(string path)
        {
            if (IsExcludedPath(path)) return new string[0];
            List<string> result = new List<string>();
            foreach (string directory in Directory.GetDirectories(path))
                if (!IsExcludedPath(directory)) result.Add(directory);
            return result.ToArray();
        }

        public static string ReadResourceText(string path)
        {
            RequireResourcePath(path);
            return File.ReadAllText(path);
        }

        public static void RequireResourcePath(string path)
        {
            if (IsExcludedPath(path))
                throw new IOException("SkinForger excluded backup/debug resource path: " + path);
        }

        // Hash comparison runs only on Reload. Enumeration alone never triggers a copy.
        public static void BackupChangedAll(string skinsRoot)
        {
            Initialize(skinsRoot);
            EnsureReady();
            int count = 0;
            if (!string.IsNullOrEmpty(skinsRoot) && Directory.Exists(skinsRoot))
                foreach (string costume in ResourceDirectories(skinsRoot)) count += BackupChangedTree(costume);
            Log("[ReplacedBackup] Incremental Reload completed: backedUp=" + count.ToString(CultureInfo.InvariantCulture));
        }

        public static int BackupChangedTree(string directory)
        {
            EnsureReady();
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory) || IsExcludedPath(directory)) return 0;
            int count = 0;
            foreach (string file in Directory.GetFiles(directory))
            {
                if (IsTrackedResource(file) && BackupFile(file, false) != null) count++;
            }
            foreach (string child in ResourceDirectories(directory)) count += BackupChangedTree(child);
            return count;
        }

        private static string BackupFile(string source, bool protectUnknown)
        {
            EnsureReady();
            RequireResourcePath(source);
            source = Path.GetFullPath(source);
            if (!IsTrackedResource(source)) throw new IOException("Outside tracked skin resources: " + source);
            if (!File.Exists(source)) return null;
            using (FileStream input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                string hash = HashStream(input);
                string knownHash;
                bool known = _known.TryGetValue(source, out knownHash);
                if (known && string.Equals(hash, knownHash, StringComparison.OrdinalIgnoreCase)) return null;
                if (!known && !protectUnknown)
                {
                    // Unknown existing files have no provable earlier version. Register, never snapshot all.
                    Remember(source, hash);
                    return null;
                }
                input.Position = 0;
                string backupDirectory = Path.Combine(Path.GetDirectoryName(source), "replaced");
                if ((Directory.Exists(backupDirectory) || File.Exists(backupDirectory)) &&
                    (File.GetAttributes(backupDirectory) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Backup directory is a reparse point: " + backupDirectory);
                Directory.CreateDirectory(backupDirectory);
                string stem = Path.GetFileNameWithoutExtension(source);
                string extension = Path.GetExtension(source);
                for (int version = 0; version < int.MaxValue; version++)
                {
                    string name = version == 0 ? Path.GetFileName(source) :
                        stem + "_" + version.ToString(CultureInfo.InvariantCulture) + extension;
                    string destination = Path.Combine(backupDirectory, name);
                    FileStream output;
                    try
                    {
                        output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    }
                    catch (IOException)
                    {
                        if (File.Exists(destination) || Directory.Exists(destination)) continue;
                        throw;
                    }
                    try
                    {
                        using (output)
                        {
                            input.CopyTo(output);
                            output.Flush(true);
                        }
                    }
                    catch
                    {
                        // Only remove the incomplete file created by this invocation.
                        try { File.Delete(destination); } catch { }
                        throw;
                    }
                    // Record only after the durable copy succeeds, preventing duplicate unchanged Reloads.
                    Remember(source, hash);
                    Log("[ReplacedBackup] Changed file saved: " + source + " -> " + destination);
                    return destination;
                }
            }
            throw new IOException("Backup version space exhausted: " + source);
        }

        public static void WriteResourceBytes(string path, byte[] bytes)
        {
            EnsureReady();
            RequireResourcePath(path);
            path = Path.GetFullPath(path);
            BackupFile(path, true); // Unknown file being overwritten is protected conservatively.
            File.WriteAllBytes(path, bytes);
            Remember(path, HashBytes(bytes)); // SF's own output is the next known baseline.
        }

        public static void WriteResourceText(string path, string text)
        {
            WriteResourceBytes(path, new System.Text.UTF8Encoding(false).GetBytes(text));
        }

        /// <summary>
        /// Where this class says what it did. The old build had its Console.WriteLine calls REBOUND to
        /// MelonLogger by the Cecil step -- a contract that existed only in the processed assembly, so in
        /// the authored build every line went to a console nobody reads. 2026-11-20: the baseline silently
        /// failed to start and every backup-dependent write was refused, with nothing in the log to say so.
        /// The host sets this once; until it does, the lines still go to the console.
        /// </summary>
        public static Action<string> Logger;

        public static void Log(string message)
        {
            var sink = Logger;
            if (sink != null) { sink(message); return; }
            Console.WriteLine(message);
        }
    }
}
