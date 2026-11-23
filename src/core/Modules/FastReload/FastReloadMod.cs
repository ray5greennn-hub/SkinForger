using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using Event = UnityEngine.Event;

namespace SkinForger.FastReload;

/// <summary>
/// Incremental "Reload" for SkinForger Main (Main is not modified). Main's Reload button runs
/// BackupChangedAll -> ReloadSkins -> ReloadSelections -> ReloadAssets, which destroys and rebuilds every runtime atlas of
/// every selected skin (5-7 s measured 2026-11-13) and SHA-hashes every PNG for the "replaced" backup (~1 s).
///
/// Fast path (all must hold, otherwise Main's full Reload runs exactly as before):
///  - no PNG/JSON file was added or removed under the skins folder and no JSON changed since the last reload;
///  - every changed PNG keeps its pixel size;
///  - every live gameplay atlas of the affected skin was built through ApplyRegionPngToAtlas while this mod was loaded
///    (so the atlas / attachment / region of each placement is known).
/// Each changed PNG is then re-applied by Main's own ApplyRegionPngToAtlas into the SAME packed rect of the SAME live
/// atlas (the allocator returns the previous rect), so region UVs, attachments and mesh-canvas bands stay valid. If the
/// re-applied placement differs in any value other than alpha bounds, the full Reload runs.
/// Shift+Reload always runs the full Reload.
///
/// Backup: ReplacedBackup.BackupFile hashes a file only when its size or write time changed since it was last hashed
/// and Main's known hash still equals that hash (same decision Main would reach, without reading the file).
/// </summary>
public sealed class FastReloadMod : global::SkinForger.Core.SfModule
{
    private const string Prefix = "[SkinForge][FastReload]";
    private const BindingFlags All = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static Type _host = null!;
    private static MethodInfo _backupChangedAll = null!, _applyRegion = null!, _regionKey = null!, _resolveScope = null!;
    private static FieldInfo _known = null!, _gameplayScope = null!, _skins = null!;
    private static Type? _preview;
    private static string _cachePath = string.Empty;

    /// <summary>
    /// This module patches the OLD Main. On the authored Main it stands down, because incremental reload rewrites the IL of the old Main's per-region reload path; the authored Main reloads by composing its atlas once, so there is no per-region path to make incremental.
    ///
    /// A patch that cannot find the IL it rewrites fails loudly and takes other modules with it, so saying
    /// so once and doing nothing is the honest behaviour while both Mains exist (SF切割SkinChanger计划).
    /// </summary>
    private static bool StandDownForAuthoredMain()
    {
        if (global::SkinForger.MainBridge.MainHost.Generation != global::SkinForger.MainBridge.MainGeneration.Authored)
            return false;
        MelonLoader.MelonLogger.Msg($"{Prefix} not installed: the authored Main does not need it");
        return true;
    }

    public override void OnInitializeMelon()
    {
        if (StandDownForAuthoredMain()) return;
        try
        {
            _host = AccessTools.TypeByName("SkinForger.SkinForgerMod") ?? throw new TypeLoadException("SkinForgerMod");
            Type backup = AccessTools.TypeByName("SkinForger.ReplacedBackup") ?? throw new TypeLoadException("ReplacedBackup");
            _preview = AccessTools.TypeByName("SkinForger.Preview.PreviewManager");
            _backupChangedAll = Method(backup, "BackupChangedAll", 1);
            MethodInfo backupFile = Method(backup, "BackupFile", 2);
            _known = backup.GetField("_known", All) ?? throw new MissingFieldException("ReplacedBackup._known");
            _applyRegion = Method(_host, "ApplyRegionPngToAtlas", 8);
            MethodInfo getTexture = Method(_host, "GetTexture", 3);
            MethodInfo allocate = Method(_host, "AllocateExpandedPackedRect", 4);
            _regionKey = _host.GetMethod("GetSynthesizedRegionKey", All, null, new[] { typeof(string), typeof(string), typeof(string) }, null)
                ?? throw new MissingMethodException("GetSynthesizedRegionKey(string,string,string)");
            _resolveScope = Method(_host, "ResolveReplacementResourceScope", 0);
            _gameplayScope = _host.GetField("GameplayScope", All) ?? throw new MissingFieldException("GameplayScope");
            _skins = _host.GetField("Skins", All) ?? throw new MissingFieldException("Skins");
            MethodInfo drawWindow = Method(_host, "DrawWindow", -1);
            _initializeBackup = Method(backup, "Initialize", 1);
            _isTracked = Method(backup, "IsTrackedResource", 1);
            _backupFile = backupFile;
            _getDumpDirectory = Method(_host, "GetDumpSkinDirectory", 2);
            _getCostumeName = Method(_host, "GetCostumeName", 2);
            _saveSelection = Method(_host, "SaveSelection", 2);
            _selectedCharacters = _host.GetField("SelectedCharacters", All);
            _selectedElfins = _host.GetField("SelectedElfins", All);
            _savedCharactersPath = _host.GetField("_savedCharactersPath", All);
            _savedElfinsPath = _host.GetField("_savedElfinsPath", All);
            HarmonyInstance.Patch(Method(_host, "DumpCostume", 2), postfix: Hook(nameof(DumpCostumePostfix), HarmonyLib.Priority.Last));
            _getSkinPath = Method(_host, "GetSkinPath", 3);
            _reloadAssets = Method(_host, "ReloadAssets", 0);
            HarmonyInstance.Patch(_reloadAssets, postfix: Hook(nameof(ReloadAssetsPostfix), HarmonyLib.Priority.Last));
            HarmonyInstance.Patch(Method(_host, "EnsureInitialized", 0), postfix: Hook(nameof(EnsureInitializedPostfix), HarmonyLib.Priority.Last));
            HarmonyInstance.Patch(Method(_host, "DrawSelectionList", 1), transpiler: Hook(nameof(SelectionListTranspiler)));
            HarmonyInstance.Patch(Method(_host, "ApplyRegionPlacementToRegion", 4), prefix: Hook(nameof(RegionPlacementPrefix), HarmonyLib.Priority.First));

            HarmonyInstance.Patch(drawWindow, transpiler: Hook(nameof(DrawWindowTranspiler)));
            HarmonyInstance.Patch(allocate, transpiler: Hook(nameof(AllocatorTranspiler)));
            HarmonyInstance.Patch(_applyRegion, postfix: Hook(nameof(ApplyRegionPostfix), HarmonyLib.Priority.Last));
            HarmonyInstance.Patch(getTexture, postfix: Hook(nameof(GetTexturePostfix), HarmonyLib.Priority.Last));
            HarmonyInstance.Patch(backupFile, prefix: Hook(nameof(BackupFilePrefix), HarmonyLib.Priority.First), postfix: Hook(nameof(BackupFilePostfix), HarmonyLib.Priority.Last));
            HarmonyInstance.Patch(_backupChangedAll, postfix: Hook(nameof(BackupChangedAllPostfix), HarmonyLib.Priority.Last));
            Type? dump = AccessTools.TypeByName("SkinForger.DumpSystem.DumpSessionManager");
            MethodInfo? dumpEnd = dump?.GetMethod("End", All, null, Type.EmptyTypes, null);
            if (dumpEnd != null) HarmonyInstance.Patch(dumpEnd, postfix: Hook(nameof(DumpEndPostfix), HarmonyLib.Priority.Last));

            _cachePath = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "SkinForger.FastReload.backup-cache.tsv");
            LoadBackupCache();
            _baselineRoot = DefaultSkinsRoot();
            _baseline = Scan(_baselineRoot);
            MelonLogger.Msg($"{Prefix} ready version=0.2.2 baselineFiles={_baseline?.Count ?? 0} backupCache={BackupCache.Count} (Shift+Reload = full reload)");
        }
        catch (Exception exception)
        {
            MelonLogger.Error($"{Prefix} DISABLED {exception.GetBaseException().Message}");
        }
    }

    private static MethodInfo Method(Type type, string name, int parameters) =>
        type.GetMethods(All).SingleOrDefault(m => m.Name == name && (parameters < 0 || m.GetParameters().Length == parameters))
        ?? throw new MissingMethodException(type.FullName, name);

    private static HarmonyMethod Hook(string name, int priority = HarmonyLib.Priority.Normal) =>
        new(AccessTools.DeclaredMethod(typeof(FastReloadMod), name)) { priority = priority };

    private static string DefaultSkinsRoot() =>
        global::SkinForger.SkinPackages.SkinForgerPaths.Creator; // 2026-11-14: creator workspace

    private static string Norm(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
        catch { return path; }
    }

    private static object? Read(object? instance, string name)
    {
        if (instance == null) return null;
        Type type = instance.GetType();
        return type.GetProperty(name, All)?.GetValue(instance) ?? type.GetField(name, All)?.GetValue(instance);
    }

    private static double Ms(long start) => (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;

    // ================================================================ Reload button

    /// <summary>Replaces "call ReplacedBackup.BackupChangedAll" in DrawWindow with "call OnReloadClicked; brtrue past
    /// the rest of Main's Reload block (â€¦ReloadAssets; PreviewManager.AfterHostReload)".</summary>
    private static IEnumerable<CodeInstruction> DrawWindowTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        var code = instructions.ToList();
        MethodInfo? afterHostReload = _preview?.GetMethod("AfterHostReload", All, null, Type.EmptyTypes, null);
        int backupCall = code.FindIndex(c => c.Calls(_backupChangedAll));
        int afterCall = afterHostReload == null || backupCall < 0 ? -1 : code.FindIndex(backupCall, c => c.Calls(afterHostReload));
        if (backupCall < 0 || afterCall < 0 || afterCall + 1 >= code.Count || afterCall - backupCall > 20)
        {
            MelonLogger.Warning($"{Prefix} Reload button hook not found (backup={backupCall}, after={afterCall}); fast reload off");
            return code;
        }
        Label skip = generator.DefineLabel();
        code[afterCall + 1].labels.Add(skip);
        code[backupCall].operand = AccessTools.Method(typeof(FastReloadMod), nameof(OnReloadClicked));
        code.Insert(backupCall + 1, new CodeInstruction(OpCodes.Brtrue, skip));
        MelonLogger.Msg($"{Prefix} Reload button hooked");
        return code;
    }

    /// <summary>Returns true when the reload was completed incrementally (Main's full reload block is skipped).</summary>
    public static bool OnReloadClicked(string skinsDirectory)
    {
        long start = Stopwatch.GetTimestamp();
        bool force = false;
        try { force = Event.current != null && Event.current.shift; } catch { }

        _backupHashed = _backupSkipped = 0;
        string root = Norm(skinsDirectory);
        long scanStart = Stopwatch.GetTimestamp();
        Dictionary<string, FileStamp>? current = Scan(root);
        double scanMs = Ms(scanStart);

        long backupStart = Stopwatch.GetTimestamp();
        try
        {
            if (current == null || !FastBackup(skinsDirectory, current))
                _backupChangedAll.Invoke(null, new object[] { skinsDirectory });   // Main's own walk (BackupFile prefix still skips unchanged files)
        }
        catch (TargetInvocationException exception) when (exception.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw(); }
        double backupMs = Ms(backupStart);

        var stats = new HotStats();
        string reason;
        bool hot = false;
        string? selected = force ? null : ApplyPendingDumpSelections();
        List<string> stale = force || selected != null ? new List<string>() : StaleCostumes();
        if (force) reason = "shift-held";
        else if (selected != null) reason = "dump-auto-select:" + selected;
        else if (stale.Count > 0) reason = "selection-not-applied:[" + string.Join(",", stale) + "]";
        // 2026-11-14: a creator edit in SkinCreator\<Costume> is drawn over that costume's package skins: remount.
        else if (current != null && _baseline != null && global::SkinForger.SkinPackages.PackageMounts.TouchesOverlay(ChangedPaths(current, _baseline))) reason = "package-overlay-changed";
        // 2026-11-14: .sfskin packages added / removed / replaced in SkinForger/Skins need Main's ReloadSkins (remount).
        else if (global::SkinForger.SkinPackages.PackageMounts.Changed(global::SkinForger.SkinPackages.SkinForgerPaths.Player)) reason = "skin-packages-changed";
        else
        {
            try { hot = TryHotReload(root, current, stats, out reason); }
            catch (Exception exception) { reason = "exception:" + exception.GetBaseException().GetType().Name + ":" + exception.GetBaseException().Message; }
        }
        _baselineRoot = root;
        _baseline = current;   // the full reload (if any) reads the files as they are now

        string common = $"backupMs={backupMs:0.#} (hashed={_backupHashed} skipped={_backupSkipped}) scanMs={scanMs:0.#} " +
                        $"changedPng={stats.ChangedPng} unusedSkinFiles={stats.Unused} patched={stats.Patched} decodeEvicted={stats.Evicted} " +
                        $"previewMs={stats.PreviewMs:0.#}";
        if (hot)
        {
            MelonLogger.Msg($"{Prefix} RELOAD mode=incremental totalMs={Ms(start):0.#} {common} files=[{string.Join(",", stats.Files)}]");
            // 2026-11-15: an incremental Reload never calls Main's ReloadAssets, so skin switch icons edited in
            // Icon\ were not refreshed until the character page was opened again.
            global::SkinForger.SkinPackages.RoleSkins.AfterReload();
        }
        else
            MelonLogger.Msg($"{Prefix} RELOAD mode=full reason={reason} preMs={Ms(start):0.#} {common} (Main full reload follows)");
        return hot;
    }

    // ================================================================ backup without Main's full tree walk

    private static MethodInfo? _initializeBackup, _isTracked, _backupFile;

    /// <summary>
    /// Equivalent of ReplacedBackup.BackupChangedAll: Initialize, then BackupFile(path, false) for every tracked file —
    /// but only for files whose size/write time differs from when they were last hashed (or whose known hash moved).
    /// Unchanged files would be hashed by Main and return null, so skipping them changes nothing.
    /// </summary>
    private static bool FastBackup(string skinsDirectory, Dictionary<string, FileStamp> files)
    {
        if (_initializeBackup == null || _isTracked == null || _backupFile == null) return false;
        _initializeBackup.Invoke(null, new object[] { skinsDirectory });
        if (_known.GetValue(null) is not Dictionary<string, string> known) return false;
        int backedUp = 0;
        foreach ((string path, FileStamp stamp) in files)
        {
            if (BackupCache.TryGetValue(path, out BackupEntry entry) && entry.Length == stamp.Length && entry.Ticks == stamp.Ticks &&
                known.TryGetValue(path, out string? hash) && string.Equals(hash, entry.Hash, StringComparison.OrdinalIgnoreCase))
            { _backupSkipped++; continue; }
            if (_isTracked.Invoke(null, new object[] { path }) is not true) continue;
            if (_backupFile.Invoke(null, new object[] { path, false }) != null) backedUp++;
        }
        BackupChangedAllPostfix();
        MelonLogger.Msg($"[ReplacedBackup] Incremental Reload completed: backedUp={backedUp} (FastReload: hashed={_backupHashed}, unchanged skipped={_backupSkipped})");
        return true;
    }

    // ================================================================ applied-selection tracking (BUG-062)

    /// <summary>
    /// Resolved skin path per costume ("c:9" -> ...\Skins\Maid) as of the last real apply (first EnsureInitialized,
    /// every ReloadAssets). Main only re-applies when a selection index changes, but the same index can resolve to a
    /// different folder later (dump creates the folder the saved index points at, folders added/removed). Such a
    /// costume is "stale": Reload and a click on its already-selected button then run the real apply.
    /// </summary>
    private static Dictionary<string, string>? _applied;
    private static MethodInfo? _getSkinPath, _reloadAssets;

    private static Dictionary<string, string> ResolveSelections()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((FieldInfo? field, bool elfin) in new[] { (_selectedCharacters, false), (_selectedElfins, true) })
        {
            if (field?.GetValue(null) is not Dictionary<int, int> selection) continue;
            foreach ((int index, int selected) in selection)
            {
                if (selected < 0) continue;
                string path = string.Empty;
                try { path = Norm(_getSkinPath?.Invoke(null, new object[] { index, selected, elfin }) as string); } catch { }
                if (path.Length > 0) result[(elfin ? "e:" : "c:") + index] = path;
            }
        }
        return result;
    }

    private static List<string> StaleCostumes()
    {
        if (_applied == null) return new List<string>();
        Dictionary<string, string> now = ResolveSelections();
        return now.Keys.Union(_applied.Keys)
            .Where(k => !string.Equals(now.GetValueOrDefault(k), _applied.GetValueOrDefault(k), StringComparison.OrdinalIgnoreCase))
            .Select(k => k + "=" + (now.TryGetValue(k, out string? p) ? Path.GetFileName(p) : "original"))
            .ToList();
    }

    private static void MarkApplied() { try { _applied = ResolveSelections(); } catch { } }

    private static void EnsureInitializedPostfix() { if (_applied == null) MarkApplied(); }

    private static void ReloadAssetsPostfix() => MarkApplied();

    /// <summary>Called when an Original / skin button is clicked although it is already the selection.</summary>
    public static void ReapplyIfStale()
    {
        try
        {
            List<string> stale = StaleCostumes();
            if (stale.Count == 0) return;
            MelonLogger.Msg($"{Prefix} SELECTION_REAPPLY stale=[{string.Join(",", stale)}] (button was already selected; running ReloadAssets)");
            _reloadAssets?.Invoke(null, null);
        }
        catch (Exception exception) { MelonLogger.Warning($"{Prefix} reapply failed: {exception.GetBaseException().Message}"); }
    }

    /// <summary>In DrawSelectionList, "if (selection[i] == thisButton) skip" gets a ReapplyIfStale() call on the skip path.</summary>
    private static IEnumerable<CodeInstruction> SelectionListTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        MethodInfo getItem = AccessTools.PropertyGetter(typeof(Dictionary<int, int>), "Item");
        MethodInfo hook = AccessTools.Method(typeof(FastReloadMod), nameof(ReapplyIfStale));
        int inserted = 0;
        for (int i = code.Count - 1; i >= 3; i--)
        {
            if ((code[i].opcode == OpCodes.Br || code[i].opcode == OpCodes.Br_S) &&
                (code[i - 1].opcode == OpCodes.Bne_Un || code[i - 1].opcode == OpCodes.Bne_Un_S) && code[i - 3].Calls(getItem))
            {
                var call = new CodeInstruction(OpCodes.Call, hook);
                call.MoveLabelsFrom(code[i]);
                code.Insert(i, call);
                inserted++;
            }
        }
        MelonLogger.Msg($"{Prefix} already-selected button hooks={inserted} (expected 2: Original, skin)");
        return code;
    }

    // ================================================================ Dump -> select the dumped skin on the next Reload

    private static readonly List<(int Index, bool Elfin, string Directory)> PendingDumpSelections = new();
    private static MethodInfo? _getDumpDirectory, _getCostumeName, _saveSelection;
    private static FieldInfo? _selectedCharacters, _selectedElfins, _savedCharactersPath, _savedElfinsPath;

    private static void DumpCostumePostfix(int __0, bool __1)
    {
        try
        {
            string directory = Norm(_getDumpDirectory?.Invoke(null, new object[] { __0, __1 }) as string);
            if (directory.Length == 0 || !Directory.Exists(directory)) return;
            PendingDumpSelections.RemoveAll(p => p.Index == __0 && p.Elfin == __1);
            PendingDumpSelections.Add((__0, __1, directory));
            MelonLogger.Msg($"{Prefix} DUMP_PENDING_SELECT index={__0} elfin={__1} dir={directory} (applied on next Reload)");
        }
        catch (Exception exception) { MelonLogger.Warning($"{Prefix} dump auto-select skipped: {exception.GetBaseException().Message}"); }
    }

    /// <summary>Selects each freshly dumped skin (exactly what its Root/skin button does: set index, SaveSelection).
    /// Returns the costume names changed, or null when nothing changed.</summary>
    private static string? ApplyPendingDumpSelections()
    {
        if (PendingDumpSelections.Count == 0) return null;
        var changed = new List<string>();
        try
        {
            foreach ((int index, bool elfin, string directory) in PendingDumpSelections)
            {
                string name = _getCostumeName?.Invoke(null, new object[] { index, elfin }) as string ?? string.Empty;
                if (name.Length == 0 || _skins.GetValue(null) is not Dictionary<string, List<string>> skins ||
                    !skins.TryGetValue(name, out List<string>? list)) continue;
                int position = list.FindIndex(p => string.Equals(Norm(p), directory, StringComparison.OrdinalIgnoreCase));
                if (position < 0) { MelonLogger.Warning($"{Prefix} dump auto-select: {name} skin list has no {directory}"); continue; }
                if ((elfin ? _selectedElfins : _selectedCharacters)?.GetValue(null) is not Dictionary<int, int> selection) continue;
                int previous = selection.TryGetValue(index, out int now) ? now : -1;
                // 2026-11-14: a skin package is selected: keep it. The creator's edits are drawn over it (package overlay).
                if (previous >= 0 && previous < list.Count && global::SkinForger.SkinPackages.VirtualFiles.IsVirtual(list[previous]))
                {
                    MelonLogger.Msg($"{Prefix} DUMP_KEEP_PACKAGE costume={name}: package stays selected; edited images in {directory} are drawn over it");
                    continue;
                }
                if (previous == position) continue;
                selection[index] = position;
                _saveSelection?.Invoke(null, new object?[] { selection, (elfin ? _savedElfinsPath : _savedCharactersPath)?.GetValue(null) });
                changed.Add(name);
                MelonLogger.Msg($"{Prefix} DUMP_AUTO_SELECT costume={name} elfin={elfin} skin={list[position]} (was {previous})");
            }
        }
        finally { PendingDumpSelections.Clear(); }
        return changed.Count == 0 ? null : string.Join(",", changed);
    }

    private sealed class HotStats
    {
        internal int ChangedPng, Unused, Patched, Evicted;
        internal double PreviewMs;
        internal readonly List<string> Files = new();
    }

    private static bool TryHotReload(string root, Dictionary<string, FileStamp>? current, HotStats stats, out string reason)
    {
        if (current == null) { reason = "skins-folder-missing"; return false; }
        if (_baseline == null || !string.Equals(_baselineRoot, root, StringComparison.OrdinalIgnoreCase)) { reason = "no-baseline"; return false; }

        int added = current.Keys.Count(k => !_baseline.ContainsKey(k));
        int removed = _baseline.Keys.Count(k => !current.ContainsKey(k));
        if (added > 0 || removed > 0) { reason = $"files-added={added},removed={removed}"; return false; }
        // Selection files (Saved*.json written by the Original/Root buttons) live in the skins folder; Main applies
        // selection changes itself, so they are not skin content.
        var selectionFiles = new[] { _savedCharactersPath?.GetValue(null) as string, _savedElfinsPath?.GetValue(null) as string }
            .Where(p => !string.IsNullOrEmpty(p)).Select(p => Norm(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = current.Where(p => !_baseline[p.Key].Equals(p.Value) && !selectionFiles.Contains(p.Key)).Select(p => p.Key).ToList();
        string? json = changed.FirstOrDefault(p => !p.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
        if (json != null) { reason = "non-png-changed:" + Path.GetFileName(json); return false; }
        stats.ChangedPng = changed.Count;
        if (changed.Count == 0) { reason = "no-change"; return true; }

        object scope = _gameplayScope.GetValue(null) ?? throw new InvalidOperationException("GameplayScope");
        IDictionary placements = Read(scope, "RegionPlacements") as IDictionary ?? throw new InvalidOperationException("RegionPlacements");
        object appliedObject = Read(scope, "AppliedRegionKeys") ?? throw new InvalidOperationException("AppliedRegionKeys");
        var applied = new HashSet<string>(((IEnumerable)appliedObject).OfType<string>(), StringComparer.Ordinal);
        List<string> skinRoots = SkinRoots();

        // ---- plan (no mutation except decode-cache eviction, which a full reload clears anyway)
        var plan = new List<Record>();
        foreach (string png in changed)
        {
            stats.Files.Add(Path.GetFileName(Path.GetDirectoryName(png)) + "/" + Path.GetFileName(png));
            stats.Evicted += EvictDecoded(png, scope);
            string? skin = skinRoots.Where(s => png.StartsWith(s + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.Length).FirstOrDefault();
            if (skin == null) { reason = "unknown-skin:" + Path.GetFileName(png); return false; }
            string skinPrefix = skin + "|";
            var liveKeys = applied.Where(k => k.StartsWith(skinPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
            if (liveKeys.Count == 0) { stats.Unused++; continue; }   // skin not on any live gameplay atlas
            string? unknown = liveKeys.FirstOrDefault(k => !Records.ContainsKey(k));
            if (unknown != null) { reason = "live-atlas-not-recorded"; return false; }
            var records = liveKeys.Select(k => Records[k]).Where(r => string.Equals(r.RegionPath, png, StringComparison.OrdinalIgnoreCase)).ToList();
            if (records.Count == 0) continue;   // region not on a live atlas (e.g. battle-only); decoded cache already evicted
            if (records.Any(r => r.Atlas == null || r.Atlas.Pointer == IntPtr.Zero)) { reason = "atlas-destroyed"; return false; }
            if (!Dims.TryGetValue(png, out var before)) { reason = "png-size-unknown:" + Path.GetFileName(png); return false; }
            if ( !TryReadPngSize(png, out int width, out int height) ||
                before.W != width || before.H != height)
            { reason = "png-size-changed:" + Path.GetFileName(png); return false; }
            plan.AddRange(records);
        }

        // ---- apply in place
        MethodInfo remove = appliedObject.GetType().GetMethod("Remove", new[] { typeof(string) }) ?? throw new MissingMethodException("AppliedRegionKeys.Remove");
        foreach (Record record in plan)
        {
            object? old = placements[record.Key];
            if (old == null) { reason = "placement-missing"; return false; }
            Dictionary<string, object?> before = Values(old);
            object? rect = Read(old, "PackedRect");
            _hotRect = (Convert.ToInt32(Read(rect, "X")), Convert.ToInt32(Read(rect, "Y")),
                        Convert.ToInt32(Read(rect, "Width")), Convert.ToInt32(Read(rect, "Height")));
            _hotActive = true;
            _hotMismatch = false;
            bool ok;
            List<(PropertyInfo Property, object? Value)>? live = null;
            try
            {
                // Main rewrote this AtlasRegion to the runtime placement after the first build; Main computes the
                // packed rect from the region, so it is re-applied against the region as it was before that rewrite.
                live = SwapRegion(record.Region);
                remove.Invoke(appliedObject, new object[] { record.Key });
                object?[] args = { record.Atlas, record.Attachment, record.Region, record.TextureName, record.RawRegionPath, record.Skin, null, null };
                ok = _applyRegion.Invoke(null, args) is true;
            }
            finally
            {
                _hotActive = false;
                if (live != null) foreach ((PropertyInfo property, object? value) in live) try { property.SetValue(record.Region, value); } catch { }
            }
            if (_hotMismatch) { reason = "allocator-size-mismatch:" + Path.GetFileName(record.RegionPath); return false; }
            if (!ok) { reason = "apply-failed:" + Path.GetFileName(record.RegionPath); return false; }
            object? now = placements[record.Key];
            string? diff = now == null ? "placement-removed" : Diff(before, Values(now));
            if (diff != null && now != null) diff += $"[{Describe(before.GetValueOrDefault(diff))}->{Describe(Values(now).GetValueOrDefault(diff))},regionOriginal={(live != null)}]";
            if (diff != null) { reason = "placement-changed:" + diff + ":" + Path.GetFileName(record.RegionPath); return false; }
            stats.Patched++;
        }

        // ---- preview (own atlas): rebuild only when open
        if (_preview != null && _preview.GetField("_open", All)?.GetValue(null) is true)
        {
            long previewStart = Stopwatch.GetTimestamp();
            foreach (object other in Scopes.ToList()) if (!ReferenceEquals(other, scope)) foreach (string png in changed) EvictDecoded(png, other);
            _preview.GetMethod("BeforeHostReload", All, null, Type.EmptyTypes, null)?.Invoke(null, null);
            _preview.GetMethod("AfterHostReload", All, null, Type.EmptyTypes, null)?.Invoke(null, null);
            stats.PreviewMs = Ms(previewStart);
        }
        reason = "ok";
        return true;
    }

    private static string Describe(object? value)
    {
        if (value == null) return "null";
        Type type = value.GetType();
        PropertyInfo[] props = type.GetProperties(BindingFlags.Instance | BindingFlags.Public);
        return type.IsPrimitive || props.Length == 0 || value is string ? Convert.ToString(value) ?? "" :
            string.Join(";", props.Where(p => p.GetIndexParameters().Length == 0).Select(p => p.Name + "=" + p.GetValue(value)));
    }

    // ---- original AtlasRegion values (captured before Main's first ApplyRegionPlacementToRegion on that region)
    private static readonly Dictionary<IntPtr, List<(PropertyInfo Property, object? Value)>> RegionOriginals = new();
    private static PropertyInfo[]? _regionProperties;

    private static PropertyInfo[] RegionProperties(Type type) => _regionProperties ??= type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && p.DeclaringType?.Namespace == "Il2CppSpine").ToArray();

    private static List<(PropertyInfo, object?)> ReadRegion(object region) =>
        RegionProperties(region.GetType()).Select(p => (p, p.GetValue(region))).ToList();

    private static void RegionPlacementPrefix(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase __0)
    {
        try
        {
            if (__0 == null || __0.Pointer == IntPtr.Zero || RegionOriginals.ContainsKey(__0.Pointer)) return;
            RegionOriginals[__0.Pointer] = ReadRegion(__0);
        }
        catch { }
    }

    /// <summary>Sets the region to its captured original values; returns the live values to put back (null = no capture).</summary>
    private static List<(PropertyInfo, object?)>? SwapRegion(object? region)
    {
        if (region is not Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase native || native.Pointer == IntPtr.Zero ||
            !RegionOriginals.TryGetValue(native.Pointer, out var original)) return null;
        List<(PropertyInfo, object?)> live = ReadRegion(region);
        foreach ((PropertyInfo property, object? value) in original) property.SetValue(region, value);
        return live;
    }

    private static List<string> SkinRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_skins.GetValue(null) is IDictionary skins)
            foreach (DictionaryEntry entry in skins)
                if (entry.Value is IEnumerable list)
                    foreach (object? item in list) if (item is string path && path.Length > 0) roots.Add(Norm(path));
        foreach (Record record in Records.Values) roots.Add(record.SkinNorm);
        return roots.ToList();
    }

    private static int EvictDecoded(string png, object scope)
    {
        if (Read(scope, "DecodedTextures") is not IDictionary decoded) return 0;
        var keys = decoded.Keys.OfType<string>().Where(k => string.Equals(Norm(k), png, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (string key in keys) decoded.Remove(key);
        return keys.Count;
    }

    private static readonly string[] IgnoredPlacementValues = { "OriginalBounds", "ModifiedBounds" };

    private static Dictionary<string, object?> Values(object placement)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (PropertyInfo property in placement.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!property.CanRead || property.GetIndexParameters().Length != 0 || IgnoredPlacementValues.Contains(property.Name)) continue;
            values[property.Name] = property.GetValue(placement);
        }
        return values;
    }

    private static string? Diff(Dictionary<string, object?> before, Dictionary<string, object?> after)
    {
        foreach ((string name, object? value) in before)
            if (!after.TryGetValue(name, out object? other) || !Equals(value, other)) return name;
        return null;
    }

    private static bool TryReadPngSize(string path, out int width, out int height)
    {
        width = height = 0;
        if (global::SkinForger.HexieBlock.HexiePolicy.IsHexieName(path)) return false; // Steam-only: never read hexie PNGs
        try
        {
            using var stream = global::SkinForger.SkinPackages.VirtualFiles.OpenRead(path);   // package PNGs live in memory
            Span<byte> header = stackalloc byte[24];
            if (stream.Read(header) != 24 || header[0] != 0x89 || header[1] != 0x50 || header[12] != (byte)'I' || header[15] != (byte)'R') return false;
            width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
            height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
            return width > 0 && height > 0;
        }
        catch { return false; }
    }

    // ================================================================ file baseline

    private readonly record struct FileStamp(long Length, long Ticks);

    private static IEnumerable<string> ChangedPaths(Dictionary<string, FileStamp> current, Dictionary<string, FileStamp> baseline) =>
        current.Where(kv => !baseline.TryGetValue(kv.Key, out FileStamp old) || old != kv.Value).Select(kv => kv.Key)
            .Concat(baseline.Keys.Where(k => !current.ContainsKey(k)));
    private static Dictionary<string, FileStamp>? _baseline;
    private static string _baselineRoot = string.Empty;

    /// <summary>
    /// SkinForger wrote this file itself (e.g. AutoAnchor saving an anchor into a JSON): take its new stamp into the
    /// baseline, so the next Reload does not see a "changed" JSON and fall back to a full reload (2026-11-16, log 20-08:
    /// "non-png-changed:braid_L.json" right after AutoAnchor saved braid_L.json).
    /// </summary>
    public static void NoteWrittenBySkinForger(string path)
    {
        try
        {
            if (_baseline == null || string.IsNullOrEmpty(path)) return;
            var file = new FileInfo(path);
            string full = file.FullName;
            if (file.Exists && _baseline.ContainsKey(full)) _baseline[full] = new FileStamp(file.Length, file.LastWriteTimeUtc.Ticks);
        }
        catch { }
    }

    private static Dictionary<string, FileStamp>? Scan(string root)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;
        var files = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (FileInfo file in new DirectoryInfo(root).EnumerateFiles("*", options))
        {
            string ext = file.Extension;
            if (!ext.Equals(".png", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".json", StringComparison.OrdinalIgnoreCase)) continue;
            string full = file.FullName;
            if (full.Contains(Path.DirectorySeparatorChar + "replaced" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            files[full] = new FileStamp(file.Length, file.LastWriteTimeUtc.Ticks);
        }
        return files;
    }

    private static void DumpEndPostfix()
    {
        // A dump writes a new skin folder that Main only indexes; it is not applied, so it joins the baseline as-is.
        try { _baseline = Scan(_baselineRoot.Length > 0 ? _baselineRoot : DefaultSkinsRoot()); } catch { }
    }

    // ================================================================ placement records

    private sealed class Record
    {
        internal string Key = string.Empty, TextureName = string.Empty, RegionPath = string.Empty, RawRegionPath = string.Empty, Skin = string.Empty, SkinNorm = string.Empty;
        internal Texture2D? Atlas;
        internal object? Attachment, Region;
    }

    private static readonly Dictionary<string, Record> Records = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, (int W, int H)> Dims = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<object> Scopes = new();

    private static bool IsPreviewScope(object? scope) => Read(scope, "Preview") is true;

    private static void ApplyRegionPostfix(bool __result, Texture2D __0, object __1, object __2, string __3, string __4, string __5)
    {
        try
        {
            if (!__result || __0 == null) return;
            object? scope = _resolveScope.Invoke(null, null);
            if (scope == null || IsPreviewScope(scope)) return;
            string key = (_regionKey.Invoke(null, new object[] { __5, __3, __4 }) as string ?? string.Empty) + "|rt=" + __0.GetInstanceID();
            Records[key] = new Record
            {
                Key = key, Atlas = __0, Attachment = __1, Region = __2, TextureName = __3,
                RawRegionPath = __4, RegionPath = Norm(__4), Skin = __5, SkinNorm = Norm(__5)
            };
            if (Records.Count > 20000) PruneRecords();
        }
        catch { /* bookkeeping only */ }
    }

    private static void PruneRecords()
    {
        foreach (string key in Records.Where(p => p.Value.Atlas == null || p.Value.Atlas.Pointer == IntPtr.Zero).Select(p => p.Key).ToList())
            Records.Remove(key);
    }

    private static void GetTexturePostfix(string __0, Texture2D __result)
    {
        try
        {
            if (__result == null || string.IsNullOrEmpty(__0)) return;
            Dims[Norm(__0)] = (__result.width, __result.height);
            object? scope = _resolveScope.Invoke(null, null);
            if (scope != null && !Scopes.Any(s => ReferenceEquals(s, scope)))
            {
                Scopes.Add(scope);
                if (Scopes.Count > 16) Scopes.RemoveAt(0);
            }
        }
        catch { }
    }

    // ================================================================ allocator: keep the previous rect during a hot apply

    private static bool _hotActive, _hotMismatch;
    private static (int X, int Y, int W, int H) _hotRect;

    public static bool TryHotRect(int normalWidth, int normalHeight, int degrees)
    {
        if (!_hotActive) return false;
        bool swap = degrees is 90 or 270;
        int w = swap ? normalHeight : normalWidth, h = swap ? normalWidth : normalHeight;
        if (w == _hotRect.W && h == _hotRect.H) return true;
        _hotMismatch = true;   // never fall through to a real allocation (it would grow the live atlas)
        return true;
    }

    public static int HotX() => _hotRect.X;
    public static int HotY() => _hotRect.Y;
    public static int HotW() => _hotRect.W;
    public static int HotH() => _hotRect.H;

    /// <summary>Prepends: if (TryHotRect(w, h, degrees)) return new PixelRect(HotX, HotY, HotW, HotH);</summary>
    private static IEnumerable<CodeInstruction> AllocatorTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase original)
    {
        Type rectType = ((MethodInfo)original).ReturnType;
        ConstructorInfo ctor = rectType.GetConstructor(All, null, new[] { typeof(int), typeof(int), typeof(int), typeof(int) }, null)
            ?? throw new MissingMethodException(rectType.FullName, ".ctor(int,int,int,int)");
        var code = instructions.ToList();
        Label original0 = generator.DefineLabel();
        code[0].labels.Add(original0);
        code.InsertRange(0, new[]
        {
            new CodeInstruction(OpCodes.Ldarg_1),
            new CodeInstruction(OpCodes.Ldarg_2),
            new CodeInstruction(OpCodes.Ldarg_3),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(FastReloadMod), nameof(TryHotRect))),
            new CodeInstruction(OpCodes.Brfalse, original0),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(FastReloadMod), nameof(HotX))),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(FastReloadMod), nameof(HotY))),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(FastReloadMod), nameof(HotW))),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(FastReloadMod), nameof(HotH))),
            new CodeInstruction(OpCodes.Newobj, ctor),
            new CodeInstruction(OpCodes.Ret),
        });
        return code;
    }

    // ================================================================ backup hashing cache

    private readonly record struct BackupEntry(long Length, long Ticks, string Hash);
    private static readonly Dictionary<string, BackupEntry> BackupCache = new(StringComparer.OrdinalIgnoreCase);
    private static int _backupHashed, _backupSkipped;

    private static bool BackupFilePrefix(string __0, ref string? __result)
    {
        try
        {
            string full = Path.GetFullPath(__0);
            var info = new FileInfo(full);
            if (!info.Exists || !BackupCache.TryGetValue(full, out BackupEntry entry)) return true;
            if (entry.Length != info.Length || entry.Ticks != info.LastWriteTimeUtc.Ticks) return true;
            if (_known.GetValue(null) is not Dictionary<string, string> known || !known.TryGetValue(full, out string? hash) ||
                !string.Equals(hash, entry.Hash, StringComparison.OrdinalIgnoreCase)) return true;
            __result = null;   // unchanged: Main would hash it, find the known hash and return null
            _backupSkipped++;
            return false;
        }
        catch { return true; }
    }

    private static void BackupFilePostfix(string __0, bool __runOriginal)
    {
        if (!__runOriginal) return;
        try
        {
            _backupHashed++;
            string full = Path.GetFullPath(__0);
            var info = new FileInfo(full);
            if (info.Exists && _known.GetValue(null) is Dictionary<string, string> known && known.TryGetValue(full, out string? hash) && !string.IsNullOrEmpty(hash))
                BackupCache[full] = new BackupEntry(info.Length, info.LastWriteTimeUtc.Ticks, hash);
        }
        catch { }
    }

    private static void BackupChangedAllPostfix()
    {
        try
        {
            using var writer = new StreamWriter(_cachePath + ".tmp", false);
            foreach ((string path, BackupEntry entry) in BackupCache)
                writer.WriteLine($"{path}\t{entry.Length}\t{entry.Ticks}\t{entry.Hash}");
            writer.Close();
            File.Move(_cachePath + ".tmp", _cachePath, true);
        }
        catch (Exception exception) { MelonLogger.Warning($"{Prefix} backup cache not saved: {exception.Message}"); }
    }

    private static void LoadBackupCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) return;
            foreach (string line in File.ReadLines(_cachePath))
            {
                string[] parts = line.Split('\t');
                if (parts.Length == 4 && long.TryParse(parts[1], out long length) && long.TryParse(parts[2], out long ticks))
                    BackupCache[parts[0]] = new BackupEntry(length, ticks, parts[3]);
            }
        }
        catch { BackupCache.Clear(); }
    }
}
