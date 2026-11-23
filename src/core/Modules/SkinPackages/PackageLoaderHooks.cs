using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using MelonLoader;

namespace SkinForger.SkinPackages;

/// <summary>
/// Makes Main use the .sfskin packages in SkinForger/Skins:
///  - ReloadSkins prefix: (re)mount packages into <see cref="VirtualFiles"/> (decoded in memory only);
///  - ReloadSkins postfix: add the Skin Library skin of each character / elfin to Main's skin list, after Root and the
///    creator's own skins; characters the player just turned a skin on for switch to it (SkinLibrary.PendingSelect);
///  - GetSkinLabel postfix: the button shows the package file name;
///  - polling (every 2 s, cheap): packages added / removed / replaced while the game runs trigger Main's full reload.
/// </summary>
internal static class PackageLoaderHooks
{
    private const string Prefix = "[SkinForge][SkinPackages]";
    private static Type? _host;
    private static FieldInfo? _skins, _initialized, _selectedCharacters, _selectedElfins, _savedCharactersPath, _savedElfinsPath;
    private static MethodInfo? _reloadSkins, _reloadSelections, _reloadAssets, _getCount, _getCostumeName, _saveSelection;
    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase);
    private static float _nextPoll;
    private static string? _reloadReason;

    /// <summary>The Skin Library changed (skin turned on / off): Main reloads at the next frame.</summary>
    internal static void RequestReload(string reason) => _reloadReason = reason;

    internal static bool ReloadPending => _reloadReason != null;
    private static string _pendingSignature = string.Empty;

    internal static int Install(HarmonyLib.Harmony harmony)
    {
        _host = AccessTools.TypeByName("SkinForger.SkinForgerMod") ?? throw new TypeLoadException("SkinForger.SkinForgerMod");
        const BindingFlags all = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        _skins = _host.GetField("Skins", all);
        _initialized = _host.GetField("_initialized", all);
        _selectedCharacters = _host.GetField("SelectedCharacters", all);
        _selectedElfins = _host.GetField("SelectedElfins", all);
        _savedCharactersPath = _host.GetField("_savedCharactersPath", all);
        _savedElfinsPath = _host.GetField("_savedElfinsPath", all);
        _reloadSkins = AccessTools.DeclaredMethod(_host, "ReloadSkins", Type.EmptyTypes);
        _reloadSelections = AccessTools.DeclaredMethod(_host, "ReloadSelections", Type.EmptyTypes);
        _reloadAssets = AccessTools.DeclaredMethod(_host, "ReloadAssets", Type.EmptyTypes);
        _getCount = AccessTools.DeclaredMethod(_host, "GetCount", new[] { typeof(string) });
        _getCostumeName = AccessTools.DeclaredMethod(_host, "GetCostumeName", new[] { typeof(int), typeof(bool) });
        _saveSelection = _host.GetMethods(all).FirstOrDefault(m => m.Name == "SaveSelection" && m.GetParameters().Length == 2);
        MethodInfo? label = AccessTools.DeclaredMethod(_host, "GetSkinLabel");
        if (_skins == null || _reloadSkins == null || label == null) throw new MissingMemberException("Main skin list contract");

        SkinLibrary.Initialize(Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "SkinForger", "skin-library.json"));
        _selectedPathsFile = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "SkinForger", "selected-skin-paths.json");
        if (_saveSelection != null)
            harmony.Patch(_saveSelection, postfix: new HarmonyMethod(typeof(PackageLoaderHooks), nameof(SaveSelectionPostfix)) { priority = HarmonyLib.Priority.Last });
        harmony.Patch(_reloadSkins,
            prefix: new HarmonyMethod(typeof(PackageLoaderHooks), nameof(ReloadSkinsPrefix)) { priority = HarmonyLib.Priority.First },
            postfix: new HarmonyMethod(typeof(PackageLoaderHooks), nameof(ReloadSkinsPostfix)) { priority = HarmonyLib.Priority.Last });
        harmony.Patch(label, postfix: new HarmonyMethod(typeof(PackageLoaderHooks), nameof(LabelPostfix)));
        return 2;
    }

    // Main stores selections as list positions. Adding a creator folder or a package moves the positions, so the
    // selected skin is also remembered by path (in-session and in UserData) and the position is corrected after
    // every ReloadSkins. Example: Bassist on package [pkg]=0, then Dump creates Root -> [Root, pkg]: stays on pkg.
    private static readonly Dictionary<string, string> SelectedPaths = new(StringComparer.Ordinal);
    private static string _selectedPathsFile = string.Empty;
    private static bool _selectedPathsLoaded;

    private static string SelectionKey(bool elfin, int index) => (elfin ? "E:" : "C:") + index;

    /// <summary>Records the selected skin path of every costume from Main's current list and selections.</summary>
    private static void CaptureSelectedPaths()
    {
        if (DumpIsolation.Active) return;   // selections are temporarily Original while dumping
        if (_skins!.GetValue(null) is not Dictionary<string, List<string>> skins || skins.Count == 0 || _getCostumeName == null) return;
        foreach (bool elfin in new[] { false, true })
        {
            if ((elfin ? _selectedElfins : _selectedCharacters)?.GetValue(null) is not Dictionary<int, int> selection) continue;
            foreach ((int index, int position) in selection)
            {
                string costume = _getCostumeName.Invoke(null, new object[] { index, elfin }) as string ?? string.Empty;
                if (position >= 0 && skins.TryGetValue(costume, out List<string>? list) && position < list.Count)
                    SelectedPaths[SelectionKey(elfin, index)] = VirtualFiles.Normalize(list[position]);
                else
                    SelectedPaths.Remove(SelectionKey(elfin, index));
            }
        }
    }

    /// <summary>Moves each selection to wherever its skin now sits in the list.</summary>
    private static void RestoreSelectedPaths(Dictionary<string, List<string>> skins)
    {
        if (_getCostumeName == null || _saveSelection == null) return;
        foreach (bool elfin in new[] { false, true })
        {
            if ((elfin ? _selectedElfins : _selectedCharacters)?.GetValue(null) is not Dictionary<int, int> selection) continue;
            bool changed = false;
            foreach (int index in selection.Keys.ToList())
            {
                int current = selection[index];
                if (current < 0) continue;   // Original stays Original
                string costume = _getCostumeName.Invoke(null, new object[] { index, elfin }) as string ?? string.Empty;
                if (!skins.TryGetValue(costume, out List<string>? list) || list.Count == 0) continue;   // no skins at all: leave it

                int position = -1;
                if (SelectedPaths.TryGetValue(SelectionKey(elfin, index), out string? path))
                    position = list.FindIndex(p => string.Equals(VirtualFiles.Normalize(p), path, StringComparison.OrdinalIgnoreCase));
                if (position >= 0)
                {
                    if (position == current) continue;
                    MelonLogger.Msg($"{Prefix} kept {costume} on {Labels.GetValueOrDefault(path!, Path.GetFileName(path!))} (list position {current} -> {position})");
                }
                else if (path == null && current < list.Count)
                {
                    continue;   // no remembered skin and the position is still valid: leave Main's choice alone
                }
                else
                {
                    // The selected skin is gone (skin turned off / removed, creator folder deleted) or the position
                    // points past the list. Root is used only where the player chose it (2026-11-15):
                    //  - was on "skins + my work": my work again, else Root, else the skins (creator folder deleted);
                    //  - was on "skins only" (or an old package path): the skins, else Original. Never Root by itself;
                    //  - was on Root / a creator skin: Root, else the skins, else the first remaining skin.
                    string root = VirtualFiles.Normalize(Path.Combine(SkinForgerPaths.Creator, costume));
                    string players = VirtualFiles.Normalize(SkinForgerPaths.Player) + Path.DirectorySeparatorChar;
                    string mineRoot = VirtualFiles.Normalize(Path.Combine(SkinForgerPaths.Player, PackageMounts.MyWorkFolder)) + Path.DirectorySeparatorChar;
                    int At(string? directory) => directory == null ? -1 :
                        list.FindIndex(p => string.Equals(VirtualFiles.Normalize(p), directory, StringComparison.OrdinalIgnoreCase));
                    string? plain = PackageMounts.Find(costume, false)?.VirtualDirectory, mine = PackageMounts.Find(costume, true)?.VirtualDirectory;
                    bool wasMine = path != null && path.StartsWith(mineRoot, StringComparison.OrdinalIgnoreCase);
                    bool wasSkins = path != null && !wasMine && path.StartsWith(players, StringComparison.OrdinalIgnoreCase);
                    if (wasMine)
                    {
                        position = At(mine);
                        if (position < 0) position = At(root);
                        if (position < 0) position = At(plain);
                    }
                    else if (wasSkins)
                    {
                        position = At(plain);   // -1 = Original
                    }
                    else
                    {
                        position = At(root);
                        if (position < 0) position = At(plain);
                        if (position < 0) position = 0;
                    }

                    // 2026-11-21. NEVER land on Original while the player's own work is sitting in the list.
                    //
                    // What happened: the player installed their own exported package at
                    // 15:23:45 and turned it off eight seconds later. The install auto-selected it, so the
                    // remembered path was now under SkinForger\Skins -- "was on skins only" -- and that branch
                    // has no fallback but Original. Lilith went to Original and STAYED there: their own
                    // SkinCreator\Images\Lilith, which was being drawn in every session up to 15:07, was
                    // switched off by toggling a package, and the only sign of it was this one log line among
                    // eight hundred. They spent the next half hour wondering why an edited SelectionThumbnail
                    // "was not being read at all" -- a costume on Original reads nothing, by design.
                    //
                    // The 2026-11-15 rule "never Root by itself" is about turning a skin ON: picking a package
                    // must not quietly switch the player's own work on top of it. It was never meant to apply
                    // when something DISAPPEARS. Original means "no SkinForger at all", which is a bigger jump
                    // than "your own folder" and it throws away work; falling back to the player's own art is
                    // the least surprising thing that can happen here, and the log now says it in their words.
                    if (position < 0)
                    {
                        int own = At(root);
                        if (own >= 0)
                        {
                            MelonLogger.Msg($"{Prefix} {costume}: the skin it was showing is gone, so it is back on " +
                                            "your own work in SkinCreator (not Original -- your drawings are still there)");
                            position = own;
                        }
                    }

                    string chosen = position < 0 ? "Original"
                        : string.Equals(VirtualFiles.Normalize(list[position]), root, StringComparison.OrdinalIgnoreCase) ? "Root"
                        : Labels.GetValueOrDefault(VirtualFiles.Normalize(list[position]), Path.GetFileName(list[position]));
                    MelonLogger.Msg($"{Prefix} {costume}: selected skin {(path != null ? Labels.GetValueOrDefault(path, Path.GetFileName(path)) : "#" + current)} is no longer available -> {chosen}");
                }
                selection[index] = position;
                changed = true;
            }
            if (changed) _saveSelection.Invoke(null, new object?[] { selection, (elfin ? _savedElfinsPath : _savedCharactersPath)?.GetValue(null) });
        }
    }

    private static readonly HashSet<string> UnknownCostumeSaid = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A package for a costume this copy of the game does not have.
    ///
    /// Every Steam copy of Muse Dash installs the same files -- the "Just as planned" DLC is an entitlement, not
    /// a download (one depot, checked 2026-11-21), so owning it or not does not change the roster SkinForger
    /// sees. A package can still name a costume this game has no row for: a Muse Dash of another version, or a
    /// skin made on another platform. It then merges into the skin list, matches nothing, and shows nothing,
    /// with not a word said -- the exact shape of report that is hardest to answer ("I installed it and nothing
    /// happened"). So it is said here, once per costume.
    /// </summary>
    private static void WarnAboutCostumesTheGameDoesNotHave()
    {
        if (_getCount == null || _getCostumeName == null || PackageMounts.Skins.Count == 0) return;
        try
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (bool elfin in new[] { false, true })
            {
                int count = Convert.ToInt32(_getCount.Invoke(null, new object[] { elfin ? "elfin" : "character" }));
                for (int i = 0; i < count; i++)
                    if (_getCostumeName.Invoke(null, new object[] { i, elfin }) is string name && name.Length > 0)
                        known.Add(name);
            }
            if (known.Count == 0) return;   // the tables are not loaded yet: saying "none of them exist" would be a lie
            foreach (MountedSkin skin in PackageMounts.Skins)
                if (!known.Contains(skin.Costume) && UnknownCostumeSaid.Add(skin.Costume))
                    MelonLogger.Warning($"{Prefix} {skin.ButtonLabel}: this skin is for \"{skin.Costume}\", and this " +
                                        "copy of Muse Dash has no such character or elfin (a different version or " +
                                        "platform). It is installed, and it will never show anything.");
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"{Prefix} costume check: {exception.GetBaseException().Message}");
        }
    }

    private static void LoadSelectedPaths()
    {
        if (_selectedPathsLoaded) return;
        _selectedPathsLoaded = true;
        try
        {
            if (File.Exists(_selectedPathsFile) &&
                JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_selectedPathsFile)) is { } stored)
                foreach ((string key, string path) in stored) SelectedPaths.TryAdd(key, path);
        }
        catch { }
    }

    private static void SaveSelectedPaths()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_selectedPathsFile)!);
            File.WriteAllText(_selectedPathsFile, JsonSerializer.Serialize(SelectedPaths.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value)));
        }
        catch { }
    }

    /// <summary>Main saved a selection (button click, Original, Deselect All ...): remember the chosen skin by path.</summary>
    private static void SaveSelectionPostfix()
    {
        if (DumpIsolation.Active) return;
        try
        {
            CaptureSelectedPaths();
            SaveSelectedPaths();
        }
        catch { }
    }

    private static void ReloadSkinsPrefix()
    {
        try
        {
            LoadSelectedPaths();
            CaptureSelectedPaths();   // in-session: positions in the list that is about to be rebuilt
        }
        catch { }
        try
        {
            PackageMounts.Mount(SkinForgerPaths.Player, SkinForgerPaths.Creator);   // + creator edits drawn over packages
            foreach (MountProblem problem in PackageMounts.Problems)
                MelonLogger.Warning($"{Prefix} package skipped: {Path.GetFileName(problem.PackageFile)} ({problem.Reason})");
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"{Prefix} mount failed: {exception.GetBaseException().Message}");
        }
    }

    private static void ReloadSkinsPostfix()
    {
        try
        {
            if (_skins!.GetValue(null) is not Dictionary<string, List<string>> skins) return;
            if (_getCount != null && _getCostumeName != null)
                CreatorWindowHooks.RefreshCostumeIndexes(
                    (i, elfin) => _getCostumeName.Invoke(null, new object[] { i, elfin }) as string ?? string.Empty,
                    Convert.ToInt32(_getCount.Invoke(null, new object[] { "character" })),
                    Convert.ToInt32(_getCount.Invoke(null, new object[] { "elfin" })));
            Labels.Clear();
            foreach (MountedSkin skin in PackageMounts.Skins)
            {
                if (!skins.TryGetValue(skin.Costume, out List<string>? list)) skins[skin.Costume] = list = new List<string>();
                if (!list.Any(p => string.Equals(p, skin.VirtualDirectory, StringComparison.OrdinalIgnoreCase))) list.Add(skin.VirtualDirectory);
                Labels[skin.VirtualDirectory] = skin.ButtonLabel;
            }
            WarnAboutCostumesTheGameDoesNotHave();
            if (SkinLibrary.Packages.Count > 0 || PackageMounts.Problems.Count > 0)
                MelonLogger.Msg($"{Prefix} skin library: {SkinLibrary.EnabledInOrder().Count}/{SkinLibrary.Packages.Count} enabled; " +
                    (PackageMounts.Skins.Count == 0 ? "no character changed" :
                    string.Join(", ", PackageMounts.Skins.Where(s => !s.WithCreator).Select(s => $"{s.Costume}=[{string.Join(" > ", s.Layers)}]" +
                        (PackageMounts.Find(s.Costume, true) is { } mine ? $"(+ Root variant, {mine.Overlaid} creator edits)" : "")))) +
                    $" (decoded in memory, {VirtualFiles.Current.Files.Count} files)");
            if (!DumpIsolation.Active)   // a dump keeps its costumes on Original until it finishes
            {
                RestoreSelectedPaths(skins);
                ApplyPendingSelections(skins);
                CaptureSelectedPaths();
                SaveSelectedPaths();
            }
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"{Prefix} skin list merge failed: {exception.GetBaseException().Message}");
        }
    }

    private static void LabelPostfix(string skinPath, ref string __result)
    {
        if (!string.IsNullOrEmpty(skinPath) && Labels.TryGetValue(VirtualFiles.Normalize(skinPath), out string? label))
            __result = label;
    }

    /// <summary>Costumes the player just enabled a skin for (or a new package arrived for) switch to their Skin
    /// Library skin. Every other costume keeps what Main has (Original, Root ...).</summary>
    private static void ApplyPendingSelections(Dictionary<string, List<string>> skins)
    {
        if (SkinLibrary.PendingSelect.Count == 0) return;
        if (_getCount == null || _getCostumeName == null || _saveSelection == null) return;
        foreach (bool elfin in new[] { false, true })
        {
            if ((elfin ? _selectedElfins : _selectedCharacters)?.GetValue(null) is not Dictionary<int, int> selection) continue;
            int count = Convert.ToInt32(_getCount.Invoke(null, new object[] { elfin ? "elfin" : "character" }));
            bool changed = false;
            for (int i = 0; i < count; i++)
            {
                string costume = _getCostumeName.Invoke(null, new object[] { i, elfin }) as string ?? string.Empty;
                if (!SkinLibrary.PendingSelect.Contains(costume)) continue;
                if (!skins.TryGetValue(costume, out List<string>? list)) continue;
                // On Root (or already on "skins + my work"): keep the player's work on top. Anything else
                // (Original, skins only, nothing): skins only. Root is never switched on by turning a skin on.
                int current = selection.TryGetValue(i, out int selected) ? selected : -1;
                string currentPath = current >= 0 && current < list.Count ? VirtualFiles.Normalize(list[current]) : string.Empty;
                bool onMyWork = currentPath.Length > 0 &&
                    (string.Equals(currentPath, VirtualFiles.Normalize(Path.Combine(SkinForgerPaths.Creator, costume)), StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(currentPath, PackageMounts.Find(costume, true)?.VirtualDirectory, StringComparison.OrdinalIgnoreCase));
                MountedSkin? skin = (onMyWork ? PackageMounts.Find(costume, true) : null) ?? PackageMounts.Find(costume, false);
                if (skin == null) continue;
                int position = list.FindIndex(p => string.Equals(VirtualFiles.Normalize(p), skin.VirtualDirectory, StringComparison.OrdinalIgnoreCase));
                if (position < 0 || current == position) continue;
                selection[i] = position;
                changed = true;
                MelonLogger.Msg($"{Prefix} skin library: {costume} now uses [{string.Join(" > ", skin.Layers)}]{(skin.WithCreator ? " + your Root work on top" : " (skins only)")}");
            }
            if (changed) _saveSelection.Invoke(null, new object?[] { selection, (elfin ? _savedElfinsPath : _savedCharactersPath)?.GetValue(null) });
        }
        SkinLibrary.PendingSelect.Clear();
    }

    /// <summary>Called every frame; checks the folder every 2 s. Reloads once the change has settled (copy finished).</summary>
    internal static void Poll(float time)
    {
        if (_reloadReason != null && !DumpIsolation.Active)
        {
            string reason = _reloadReason;
            _reloadReason = null;
            try
            {
                if (_initialized?.GetValue(null) is true)
                {
                    MelonLogger.Msg($"{Prefix} skin library changed ({reason}): full reload");
                    _reloadSkins!.Invoke(null, null);
                    _reloadSelections?.Invoke(null, null);
                    _reloadAssets?.Invoke(null, null);
                }
            }
            catch (Exception exception)
            {
                MelonLogger.Warning($"{Prefix} skin library reload failed: {exception.GetBaseException().Message}");
            }
            return;
        }
        if (time < _nextPoll) return;
        _nextPoll = time + 2f;
        try
        {
            if (_initialized?.GetValue(null) is not true) return;
            // .sfskin packages in Skins, plus the character folders in SkinCreator (a deleted creator folder must
            // reveal the package underneath without pressing Reload).
            string signature = PackageMounts.Signature(SkinForgerPaths.Player) + "#" + PackageMounts.CreatorSignature(SkinForgerPaths.Creator);
            string mounted = PackageMounts.MountedSignature + "#" + PackageMounts.MountedCreatorSignature;
            if (string.Equals(signature, mounted, StringComparison.Ordinal)) { _pendingSignature = string.Empty; return; }
            if (!string.Equals(signature, _pendingSignature, StringComparison.Ordinal)) { _pendingSignature = signature; return; }   // wait one more poll
            _pendingSignature = string.Empty;
            MelonLogger.Msg($"{Prefix} skin packages in SkinForger/Skins or skin folders in SkinForger/SkinCreator/Images changed: full reload");
            _reloadSkins!.Invoke(null, null);
            _reloadSelections?.Invoke(null, null);
            _reloadAssets?.Invoke(null, null);
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"{Prefix} package poll failed: {exception.GetBaseException().Message}");
        }
    }
}
