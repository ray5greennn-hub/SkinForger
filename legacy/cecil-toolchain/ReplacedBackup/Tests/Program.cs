using System;
using System.IO;
using System.Reflection;
using SkinForger;

class Program
{
    static void Main(string[] args)
    {
        string root = Path.Combine(Path.GetFullPath(args[0]), "incremental-" + Guid.NewGuid().ToString("N"));
        string skins = Path.Combine(root, "Skins");
        string a = Path.Combine(skins, "Lilith", "elfin_devil");
        string b = Path.Combine(skins, "Marija", "sample");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        byte[] original = File.ReadAllBytes(args[1]);
        byte[] edited = (byte[])original.Clone();
        edited[edited.Length / 2] ^= 1; // Same-length test data; these copies are never rendered.
        string wing = Path.Combine(a, "wing_l.png");
        string json = Path.Combine(a, "wing_l.json");
        string history = Path.Combine(a, "replaced");
        File.WriteAllBytes(wing, original);
        File.WriteAllText(json, "{\"version\":2,\"offsetY\":100}");
        for (int i = 1; i < 50; i++) File.WriteAllBytes(Path.Combine(a, "unchanged_" + i + ".png"), original);
        File.WriteAllBytes(Path.Combine(b, "body.png"), original);
        ReplacedBackup.Initialize(skins);
        Check(!Directory.Exists(history), "initialization enrolls 50 PNGs without making replaced");
        ReplacedBackup.BackupChangedAll(skins);
        Check(!Directory.Exists(history), "first unchanged Reload makes no backup");
        var timestamp = File.GetLastWriteTimeUtc(wing);
        File.WriteAllBytes(wing, edited);
        File.SetLastWriteTimeUtc(wing, timestamp);
        ReplacedBackup.BackupChangedAll(skins);
        Check(Directory.GetFiles(history).Length == 1 && File.Exists(Path.Combine(history, "wing_l.png")), "Test 1: among 50 PNGs only wing_l is backed up");
        Check(Equal(edited, File.ReadAllBytes(Path.Combine(history, "wing_l.png"))), "same size and timestamp still detects changed content");
        Check(!File.Exists(Path.Combine(history, "wing_l.json")), "untouched JSON is not copied");
        ReplacedBackup.BackupChangedAll(skins);
        Check(Directory.GetFiles(history).Length == 1, "Test 4: repeat Reload creates nothing");
        File.WriteAllBytes(wing, original);
        File.WriteAllText(json, "{\"version\":2,\"offsetY\":-100}");
        ReplacedBackup.BackupChangedAll(skins);
        Check(Directory.GetFiles(history).Length == 3 && File.Exists(Path.Combine(history, "wing_l_1.png")) && File.Exists(Path.Combine(history, "wing_l.json")), "Test 2: PNG and JSON changes produce exactly two backups");
        Check(Equal(edited, File.ReadAllBytes(Path.Combine(history, "wing_l.png"))), "Test 6: first backup preserved");
        File.SetLastWriteTimeUtc(wing, DateTime.UtcNow.AddHours(1));
        ReplacedBackup.BackupChangedAll(skins);
        Check(Directory.GetFiles(history).Length == 3, "Test 3: timestamp-only change creates nothing");
        File.WriteAllText(json, "{\"version\":2,\"offsetY\":50}");
        ReplacedBackup.BackupChangedAll(skins);
        Check(Directory.GetFiles(history).Length == 4 && File.Exists(Path.Combine(history, "wing_l_1.json")), "JSON-only change backs up only JSON");
        ResetMemory();
        File.WriteAllBytes(wing, edited);
        ReplacedBackup.Initialize(skins);
        Check(Directory.GetFiles(history).Length == 4, "restart itself copies nothing");
        ReplacedBackup.BackupChangedAll(skins);
        Check(File.Exists(Path.Combine(history, "wing_l_2.png")) && Directory.GetFiles(history).Length == 5, "offline edit detected across restart");
        File.WriteAllBytes(Path.Combine(b, "body.png"), edited);
        ReplacedBackup.BackupChangedAll(skins);
        Check(Directory.GetFiles(history).Length == 5 && File.Exists(Path.Combine(b, "replaced", "body.png")), "second skin has independent change tracking");
        File.WriteAllText(Path.Combine(history, "poison.json"), "never load or back up");
        ReplacedBackup.BackupChangedAll(skins);
        Check(Directory.GetFiles(history).Length == 6 && !Directory.Exists(Path.Combine(history, "replaced")), "replaced never participates in scan");
        Check(ReplacedBackup.ResourceDirectories(a).Length == 0, "replaced not offered as skin");
        Check(ReplacedBackup.IsExcludedPath(Path.Combine(a, "RePlAcEd", "nested", "wing_l.png")), "case insensitive nested exclusion");
        ExpectFailure(() => ReplacedBackup.ReadResourceText(Path.Combine(history, "wing_l.json")), "JSON reader rejects replaced");
        ExpectFailure(() => ReplacedBackup.WriteResourceText(Path.Combine(history, "bad.json"), "bad"), "Dump destination rejects replaced");
        File.WriteAllBytes(wing, original);
        ReplacedBackup.WriteResourceBytes(wing, edited);
        Check(Equal(original, File.ReadAllBytes(Path.Combine(history, "wing_l_3.png"))), "Test 5: Dump write first preserves modified user version");
        int before = Directory.GetFiles(history).Length;
        ReplacedBackup.BackupChangedAll(skins);
        ReplacedBackup.WriteResourceBytes(wing, edited);
        Check(Directory.GetFiles(history).Length == before, "SF output and identical Dump cause no backup");
        string blocked = Path.Combine(b, "blocked");
        Directory.CreateDirectory(blocked);
        string work = Path.Combine(blocked, "work.json");
        File.WriteAllText(work, "old");
        ReplacedBackup.BackupChangedAll(skins);
        Check(!Directory.Exists(Path.Combine(blocked, "replaced")), "unknown file enrolled without snapshot");
        File.WriteAllText(work, "user work");
        File.WriteAllText(Path.Combine(blocked, "replaced"), "blocks creation");
        ExpectFailure(() => ReplacedBackup.WriteResourceText(work, "overwrite"), "backup failure cancels overwrite");
        Check(File.ReadAllText(work) == "user work", "failed backup preserves user data");
        File.Delete(Path.Combine(blocked, "replaced"));
        ReplacedBackup.BackupChangedAll(skins);
        Check(File.ReadAllText(Path.Combine(blocked, "replaced", "work.json")) == "user work", "failed backup did not advance baseline");
        using (var locked = new FileStream(wing, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            ExpectFailure(() => ReplacedBackup.BackupChangedAll(skins), "locked file fails visibly");
        using (new FileStream(wing, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        Check(true, "file handles released");
        string state = Path.Combine(root, "UserData", "SkinForger", "replaced-state-v1.json");
        File.WriteAllText(state, "corrupt");
        ResetMemory();
        ReplacedBackup.Initialize(skins);
        ExpectFailure(() => ReplacedBackup.WriteResourceBytes(wing, original), "corrupt state blocks destructive write");
        Check(Equal(edited, File.ReadAllBytes(wing)), "corrupt state leaves PNG untouched");
        Console.WriteLine("TEST_ROOT=" + root);
        Console.WriteLine("Runtime=" + Environment.Version + "; all incremental tests passed. In-game tests pending.");
    }
    static void ResetMemory()
    {
        foreach (string name in new[] { "_skinsRoot", "_statePath", "_known" })
            typeof(ReplacedBackup).GetField(name, BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, null);
        typeof(ReplacedBackup).GetField("_ready", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, false);
    }
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
    static bool Equal(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
    static void ExpectFailure(Action action, string name)
    {
        try { action(); } catch (IOException) { Check(true, name); return; }
        throw new Exception("FAIL: " + name);
    }
}
