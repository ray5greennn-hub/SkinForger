using System.Reflection;
using HarmonyLib;
using Il2CppSpine;
using Il2CppSpine.Unity;
using MelonLoader;

namespace SkinForger.R412.Runtime;

/// <summary>
/// R4-8: Mesh canvas expansion for all characters, with native Mesh deformation.
///
///  - SkinForger Main CharacterMeshGeometry.Apply is skipped for every character (non-elfin) mesh, so all
///    character meshes keep their authored vertices / regionUVs / weights / Deform. Main's placement for the
///    attachment is recorded first (it is the authoritative canvas contract).
///  - Every SkeletonRenderer whose atlas has an expanded PNG (gameplay, results, Preview) gets a session that
///    draws the expanded canvas with transient band geometry (see MeshCanvasSession).
///  - Elfins (SkeletonGraphic) and RegionAttachments keep their existing, verified paths.
/// </summary>
public sealed class R412MeshCanvasMod : global::SkinForger.Core.SfModule
{
    private const string MainGeometryType = "SkinForger.CharacterMeshGeometry.CharacterMeshGeometry";
    private const string MainHostType = "SkinForger.SkinForgerMod";
    private static R412MeshCanvasMod? _current;
    private readonly object _gate = new();
    private readonly Dictionary<long, (MeshCanvasSession? Session, int Version)> _renderers = new();
    private readonly Dictionary<string, int> _throttle = new();
    private readonly Dictionary<string, int> _skipped = new();
    private MeshCanvasSession[] _activeSessions = Array.Empty<MeshCanvasSession>();
    private readonly List<MethodBase> _patched = new();
    private BoundedLog? _log;
    private bool _enabled;
    private int _mainSkips;

    public override void OnInitializeMelon()
    {
        try
        {
            if (_current != null) throw new InvalidOperationException("duplicate-r4-12-instance");
            // 2026-11-17: UserData\SkinForger\Logs\R4-12 (players must not get a SkinForger_Source folder); last 5 kept.
            string root = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "SkinForger", "Logs", "R4-12");
            try
            {
                if (Directory.Exists(root))
                    foreach (string old in Directory.GetFiles(root, "r4-12-*.log").OrderByDescending(f => f, StringComparer.Ordinal).Skip(4))
                        try { File.Delete(old); } catch { }
            }
            catch { }
            _log = new(root, 8 * 1024 * 1024, 30000);
            SpecResolver.Log = Log;
            PartTransforms.Log = Log;

            Type geometry = FindType(MainGeometryType);
            Type host = FindType(MainHostType);
            Patch(Single(geometry, "Apply", 6, m => m.GetParameters()[0].ParameterType == typeof(Skeleton) &&
                m.GetParameters()[3].ParameterType == typeof(Attachment)), nameof(MainGeometryPrefix));
            Patch(Single(host, "ReloadAssets", 0, null), nameof(ReloadPrefix));
            // Original/Root buttons call SaveSelection + ReloadAssets without PreviewManager.BeforeHostReload /
            // AfterHostReload (only the Reload button does). With atlas reuse the old atlas texture the Preview borrowed
            // is released, so an open Preview went blank. Wrap every ReloadAssets with Main's own pair.
            Type previewManager = FindType("SkinForger.Preview.PreviewManager");
            _previewBefore = Single(previewManager, "BeforeHostReload", 0, null);
            _previewAfter = Single(previewManager, "AfterHostReload", 0, null);
            MethodInfo reloadAssets = Single(host, "ReloadAssets", 0, null);
            HarmonyInstance.Patch(reloadAssets, prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(R412MeshCanvasMod), nameof(PreviewBeforeReload))),
                postfix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(R412MeshCanvasMod), nameof(PreviewAfterReload))));
            Patch(Single(host, "ReloadSkins", 0, null), nameof(ReloadPrefix));
            Patch(Single(typeof(SkeletonRenderer), "LateUpdate", 0, m => m.ReturnType == typeof(void)), nameof(LateUpdatePrefix));
            Patch(Single(typeof(MeshGenerator), "FillVertexData", 1, m => m.GetParameters()[0].ParameterType == typeof(UnityEngine.Mesh)),
                nameof(FillVertexDataPrefix));
            // 0.13: SkeletonGraphic (Victory / Fail result characters are UI Spine, not SkeletonRenderer).
            foreach (MethodInfo update in AccessTools.GetDeclaredMethods(typeof(SkeletonGraphic)).Where(m => m.Name == "UpdateMesh"))
            {
                HarmonyInstance.Patch(update, prefix: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(R412MeshCanvasMod), nameof(GraphicUpdatePrefix))),
                    finalizer: new HarmonyMethod(AccessTools.DeclaredMethod(typeof(R412MeshCanvasMod), nameof(GraphicUpdateFinalizer))));
                _patched.Add(update);
            }
            Patch(Single(typeof(MeshGenerator), "BuildMeshWithArrays", 2, m => m.GetParameters()[0].ParameterType == typeof(SkeletonRendererInstruction)),
                nameof(BuildMeshWithArraysPrefix));

            _current = this;
            _enabled = true;
            Log("READY", "version=0.13.1 scope=all-character-meshes graphics=character-skeletongraphic canvas=png-or-full fullCanvasForSameSize=true previewUv=gameplay-truth gate=runtime-atlas-format mainGeometry=skipped-for-characters " +
                "writes=transient-meshgenerator-buffers-only elfins=unchanged regionAttachments=unchanged");
        }
        catch (Exception exception)
        {
            MelonLogger.Error("[SkinForger][R4-12] DISABLED " + exception.GetBaseException().Message);
            Shutdown("initialize-failure");
        }
    }

    public override void OnDeinitializeMelon() => Shutdown("melon-deinitialize");

    private void Patch(MethodBase target, string prefixName)
    {
        MethodInfo prefix = AccessTools.DeclaredMethod(typeof(R412MeshCanvasMod), prefixName)!;
        HarmonyInstance.Patch(target, prefix: new HarmonyMethod(prefix));
        _patched.Add(target);
        if (!OwnerPresent(target)) throw new InvalidOperationException("harmony-owner-not-confirmed:" + target.Name);
    }

    // ------------------------------------------------------------ Main hooks

    private static bool MainGeometryPrefix(Skeleton skeleton, bool elfin, Attachment attachment, object placement, string regionPath)
    {
        R412MeshCanvasMod? current = _current;
        if (current?._enabled != true) return true;

        // Elfins go through here too since 2026-11-20. They were excluded because they had their own
        // path (ElfinMeshCanvas), which rewrites mesh vertices -- the approach dropped on 2026-11-13 --
        // and which never produced a correct elfin. Everything below is the supported mechanism: band
        // geometry appended to the mesh generator's transient buffers, attachments untouched.
        //
        // The exclusion also kept the elfin out of the SkeletonGraphic path, because that path requires
        // the atlas folder to be in _skipped, and only this method puts it there.
        _ = elfin;
        try
        {
            string name = attachment?.Name ?? "?";
            if (attachment != null && attachment.TryCast<MeshAttachment>() != null && !IsPreviewExecution())
                lock (MeshCanvasSession.GameplayAttachments) MeshCanvasSession.GameplayAttachments.Add(attachment.Pointer.ToInt64());
            if (placement != null && attachment != null && attachment.TryCast<MeshAttachment>() != null)
            {
                try
                {
                    Type pt = placement.GetType();
                    object? rect = pt.GetProperty("PackedRect")?.GetValue(placement);
                    if (rect != null)
                    {
                        Type rt = rect.GetType();
                        int Read(string n) => Convert.ToInt32(rt.GetProperty(n)?.GetValue(rect) ?? 0);
                        int degrees = Convert.ToInt32(pt.GetProperty("Degrees")?.GetValue(placement) ?? 0);
                        PlacementCalibration.Record(attachment.Pointer.ToInt64(),
                            new PlacementCalibration.Placed(Read("X"), Read("Y"), Read("Width"), Read("Height"), ((degrees % 360) + 360) % 360));
                    }
                }
                catch { /* calibration data only */ }
            }
            if (placement != null && attachment?.TryCast<MeshAttachment>() is { } transformed)
            {
                try { PartTransforms.Register(transformed, name, placement, regionPath); }
                catch (Exception exception) { current.Log("PART_TRANSFORM_ERROR", $"{name} {exception.GetBaseException().Message}"); }
            }
            if (placement != null && !string.IsNullOrEmpty(regionPath) && attachment?.TryCast<MeshAttachment>() != null)
            {
                try { DriftProbe.Watch(name, placement, attachment, regionPath); }
                catch (Exception) { /* a probe must never affect a build */ }
                try { SpecResolver.Register(regionPath, name, placement); }
                catch (Exception exception) { current.Log("SPEC_FROM_MAIN_ERROR", $"{name} {exception.GetBaseException().Message}"); }
            }
            current._mainSkips++;
            string folder = Path.GetFileName(Path.GetDirectoryName(regionPath ?? string.Empty)) ?? "?";
            int count;
            lock (current._skipped) { current._skipped.TryGetValue(folder, out count); current._skipped[folder] = ++count; }
            if (count == 1) current.Log("MAIN_GEOMETRY_SKIPPED", $"atlas={folder} first={name} (authored mesh geometry kept for all its meshes)");
            return false;
        }
        catch (Exception exception)
        {
            current.Log("MAIN_GEOMETRY_SKIP_ERROR", exception.GetBaseException().Message);
            return true;
        }
    }

    private static PropertyInfo? _contextCurrent;
    private static bool _contextResolved;

    /// <summary>Main's ReplacementExecutionContext.Current.Preview (same probe OriginalStateRestore uses).</summary>
    private static bool IsPreviewExecution()
    {
        try
        {
            if (!_contextResolved)
            {
                _contextResolved = true;
                Type? type = AccessTools.TypeByName("SkinForger.CharacterReplacement.ReplacementExecutionContext");
                _contextCurrent = type?.GetProperty("Current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            }
            object? current = _contextCurrent?.GetValue(null);
            object? preview = current?.GetType().GetProperty("Preview", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(current);
            return preview != null && Convert.ToBoolean(preview);
        }
        catch { return false; }
    }

    private static MethodInfo? _previewBefore, _previewAfter;

    private static void PreviewBeforeReload()
    {
        R412MeshCanvasMod? current = _current;
        if (current?._enabled != true || _previewBefore == null) return;
        try { _previewBefore.Invoke(null, null); }
        catch (Exception exception) { current.Log("PREVIEW_RELOAD_HOOK_ERROR", "before " + exception.GetBaseException().Message); }
    }

    private static void PreviewAfterReload()
    {
        R412MeshCanvasMod? current = _current;
        if (current?._enabled != true || _previewAfter == null) return;
        try { _previewAfter.Invoke(null, null); }
        catch (Exception exception) { current.Log("PREVIEW_RELOAD_HOOK_ERROR", "after " + exception.GetBaseException().Message); }
    }

    private static void ReloadPrefix()
    {
        R412MeshCanvasMod? current = _current;
        if (current?._enabled != true) return;
        SpecResolver.Invalidate("skinforger-reload");
        PartTransforms.Clear();   // Main applies every placement again (Original mode: none)
    }

    // ------------------------------------------------------------ sessions

    private static void LateUpdatePrefix(SkeletonRenderer __instance)
    {
        R412MeshCanvasMod? current = _current;
        if (current?._enabled != true) return;
        current.Observe(__instance);
    }

    private static void FillVertexDataPrefix(MeshGenerator __instance)
    {
        R412MeshCanvasMod? current = _current;
        if (current?._enabled != true) return;
        // SkeletonGraphic clipping path (AddSubmesh, no BuildMeshWithArrays): its UpdateMesh is still running.
        MeshCanvasSession? graphic = _graphicSession;
        if (graphic != null && graphic.GeneratorIs(__instance))
        {
            _graphicSession = null;
            try { graphic.OnGraphicFill(__instance); }
            catch (Exception exception) { current.Log("GRAPHIC_FILL_REJECTED", exception.GetBaseException().Message); }
            return;
        }
        MeshCanvasSession[] sessions = current._activeSessions;
        if (sessions.Length == 0) return;
        try
        {
            foreach (MeshCanvasSession session in sessions)
            {
                if (!session.Owns(__instance)) continue;
                session.OnBeforeFillVertexData(__instance);
                return;
            }
        }
        catch (Exception exception)
        {
            current.Log("FILL_PREFIX_REJECTED", exception.GetBaseException().Message);
        }
    }

    // ------------------------------------------------------------ SkeletonGraphic (0.13)

    [ThreadStatic] private static MeshCanvasSession? _graphicSession;
    private readonly Dictionary<long, (MeshCanvasSession? Session, int Version)> _graphics = new();

    private static void GraphicUpdatePrefix(SkeletonGraphic __instance)
    {
        _graphicSession = null;
        R412MeshCanvasMod? current = _current;
        if (current?._enabled != true || __instance == null) return;
        try { _graphicSession = current.ObserveGraphic(__instance); }
        catch (Exception exception) { current.Log("GRAPHIC_OBSERVE_REJECTED", exception.GetBaseException().Message); }
    }

    private static Exception? GraphicUpdateFinalizer(Exception? __exception)
    {
        _graphicSession = null;
        return __exception;
    }

    private static void BuildMeshWithArraysPrefix(MeshGenerator __instance, SkeletonRendererInstruction instruction)
    {
        MeshCanvasSession? session = _graphicSession;
        if (session == null) return;
        _graphicSession = null;   // one reservation per UpdateMesh
        try { session.Reserve(instruction); }
        catch (Exception exception) { _current?.Log("GRAPHIC_RESERVE_REJECTED", exception.GetBaseException().Message); }
    }

    /// <summary>Character SkeletonGraphics only: the atlas folder must be one whose Main geometry was skipped (non-elfin).</summary>
    private MeshCanvasSession? ObserveGraphic(SkeletonGraphic graphic)
    {
        long pointer = graphic.Pointer.ToInt64();
        if (pointer == 0) return null;
        long id = graphic.GetInstanceID();
        int version = SpecResolver.Version;
        lock (_gate)
            if (_graphics.TryGetValue(id, out var known) && (known.Session != null || known.Version == version)) return known.Session;
        string folder = "?";
        try
        {
            folder = AtlasFolderOf(graphic.skeletonDataAsset);
            bool character;
            lock (_skipped) character = _skipped.Keys.Any(k => string.Equals(Normalize(k), folder, StringComparison.Ordinal));
            MeshCanvasSession? session = null;
            if (folder.Length != 0 && character && SpecResolver.CandidateNames(folder).Count != 0)
            {
                session = new MeshCanvasSession(graphic, folder, Log);
                Log("SESSION_REGISTERED", $"graphic={pointer} instance={id} atlas={folder} candidates={SpecResolver.CandidateNames(folder).Count}");
            }
            lock (_gate)
            {
                _graphics[id] = (session, version);
                if (session != null) _activeSessions = _activeSessions.Append(session).ToArray();
            }
            return session;
        }
        catch (Exception exception)
        {
            lock (_gate) _graphics[id] = (null, int.MaxValue);
            Log("SESSION_REJECTED", $"graphic={id} atlas={folder} {exception.GetBaseException().Message}");
            return null;
        }
    }

    /// <summary>
    /// Renderers are identified by Unity instance ID, not by native address: Preview destroys and recreates its
    /// SkeletonAnimation and the new object often reuses the old address (R4-9: Preview renderers were taken for
    /// already-known ones and never got a session).
    /// </summary>
    private void Observe(SkeletonRenderer renderer)
    {
        long pointer = renderer.Pointer.ToInt64();
        if (pointer == 0) return;
        long id = renderer.GetInstanceID();
        int version = SpecResolver.Version;
        lock (_gate)
            if (_renderers.TryGetValue(id, out var known) && (known.Session != null || known.Version == version)) return;
        string folder = "?";
        try
        {
            folder = AtlasFolderOf(renderer);
            MeshCanvasSession? session = null;
            if (folder.Length != 0 && SpecResolver.CandidateNames(folder).Count != 0)
            {
                // A session still bound to a destroyed renderer at the same address is abandoned (no native calls).
                lock (_gate)
                    foreach (long staleKey in _renderers.Where(p => p.Value.Session?.NativePointer == pointer).Select(p => p.Key).ToArray())
                    {
                        _renderers[staleKey].Session!.Abandon();
                        _renderers.Remove(staleKey);
                        Log("SESSION_ABANDONED", $"instance={staleKey} pointer={pointer} reason=address-reused-by-new-renderer");
                    }
                session = new MeshCanvasSession(renderer, folder, Log);
                Log("SESSION_REGISTERED", $"renderer={pointer} instance={id} atlas={folder} candidates={SpecResolver.CandidateNames(folder).Count}");
            }
            lock (_gate)
            {
                _renderers[id] = (session, version);
                _activeSessions = _renderers.Values.Concat(_graphics.Values).Where(v => v.Session != null).Select(v => v.Session!).ToArray();
            }
        }
        catch (Exception exception)
        {
            lock (_gate) _renderers[id] = (null, int.MaxValue);   // do not retry this renderer
            Log("SESSION_REJECTED", $"renderer={id} atlas={folder} {exception.GetBaseException().Message}");
        }
    }

    /// <summary>
    /// Atlas folder of a renderer. Preview clones its SkeletonDataAsset with Object.Instantiate, so the asset is named
    /// "..._SkeletonData(Clone)" (R4-10: Preview never matched a skin folder and silently got no session). The
    /// skeletonJSON TextAsset is shared with the original and keeps its name, so it is preferred.
    /// </summary>
    private static string AtlasFolderOf(SkeletonRenderer renderer)
    {
        if (renderer.skeleton == null) return string.Empty;
        return AtlasFolderOf(renderer.skeletonDataAsset);
    }

    private static string AtlasFolderOf(SkeletonDataAsset? asset)
    {
        if (asset == null || asset.Pointer == IntPtr.Zero) return string.Empty;
        string fromJson = string.Empty;
        try { var json = asset.skeletonJSON; if (json != null && json.Pointer != IntPtr.Zero) fromJson = Normalize(json.name); }
        catch { /* fall back to the asset name */ }
        string fromAsset = Normalize(asset.name);
        if (fromJson.Length != 0 && SpecResolver.CandidateNames(fromJson).Count != 0) return fromJson;
        return fromAsset.Length != 0 ? fromAsset : fromJson;
    }

    internal static string Normalize(string? value)
    {
        string text = (value ?? string.Empty).Trim();
        while (text.EndsWith("(Clone)", StringComparison.OrdinalIgnoreCase)) text = text[..^7].TrimEnd();
        if (text.EndsWith("_SkeletonData", StringComparison.OrdinalIgnoreCase)) text = text[..^13];
        if (text.EndsWith(".skel", StringComparison.OrdinalIgnoreCase)) text = text[..^5];
        if (text.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) text = text[..^5];
        return text.ToLowerInvariant();
    }

    // ------------------------------------------------------------ lifecycle

    private void Shutdown(string reason)
    {
        lock (_gate)
        {
            _enabled = false;
            if (ReferenceEquals(_current, this)) _current = null;
            int incomplete = 0;
            foreach (var entry in _renderers.Values.Concat(_graphics.Values))
            {
                if (entry.Session == null) continue;
                entry.Session.Dispose();
                if (!entry.Session.CleanupComplete) incomplete++;
            }
            _renderers.Clear();
            _graphics.Clear();
            _activeSessions = Array.Empty<MeshCanvasSession>();
            bool ownerAbsent = true;
            foreach (MethodBase target in _patched)
            {
                try { HarmonyInstance.Unpatch(target, HarmonyPatchType.All, HarmonyInstance.Id); }
                catch (Exception exception) { Log("UNPATCH_ERROR", exception.GetBaseException().Message); }
                ownerAbsent &= !OwnerPresent(target);
            }
            string skipped;
            lock (_skipped) skipped = string.Join(",", _skipped.Select(p => $"{p.Key}:{p.Value}"));
            Log("TOPOLOGY_SURVEY", TopologySurvey.Summary());
            Log("SHUTDOWN", $"reason={reason} sessionsCleanupIncomplete={incomplete} harmonyOwnerAbsent={ownerAbsent} " +
                $"mainGeometrySkips={_mainSkips} perAtlas=[{skipped}]");
            _log?.Dispose();
            _log = null;
        }
    }

    private void Log(string kind, string detail)
    {
        if (kind.Contains("REJECTED", StringComparison.Ordinal) || kind.Contains("ROLLED_BACK", StringComparison.Ordinal) ||
            kind.Contains("FAILED", StringComparison.Ordinal))
        {
            int count;
            lock (_throttle)
            {
                string key = kind + "|" + detail;
                if (_throttle.Count > 512) _throttle.Clear();
                _throttle.TryGetValue(key, out count);
                _throttle[key] = ++count;
            }
            if (count > 3 && count % 600 != 0) return;
            detail += " repeat=" + count;
        }
        _log?.Write(kind, detail);
        if (kind is not ("SURVEY_FAILED" or "SPEC_FROM_DISK" or "TARGET_DECISION"))
            MelonLogger.Msg($"[SkinForger][R4-12] {kind} {detail}");
    }

    private bool OwnerPresent(MethodBase target)
    {
        Patches? info = global::HarmonyLib.Harmony.GetPatchInfo(target);
        return info != null && info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers)
            .Any(patch => patch.owner == HarmonyInstance.Id);
    }

    private static Type FindType(string fullName) =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => { try { return a.GetType(fullName, false); } catch { return null; } })
            .FirstOrDefault(t => t != null) ?? throw new TypeLoadException("type-not-found:" + fullName);

    private static MethodInfo Single(Type type, string name, int parameters, Func<MethodInfo, bool>? filter)
    {
        MethodInfo[] matches = AccessTools.GetDeclaredMethods(type)
            .Where(m => m.Name == name && m.GetParameters().Length == parameters && (filter == null || filter(m)))
            .ToArray();
        if (matches.Length != 1) throw new MissingMethodException($"{type.Name}.{name}-signature-count:{matches.Length}");
        return matches[0];
    }
}

internal sealed class BoundedLog : IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private readonly long _maxBytes;
    private readonly int _maxRecords;
    private int _records;
    private bool _disposed;

    internal BoundedLog(string directory, long maxBytes, int maxRecords)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "r4-12-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".log");
        _writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
            new System.Text.UTF8Encoding(false));
        _maxBytes = maxBytes;
        _maxRecords = maxRecords;
    }

    internal void Write(string kind, string detail)
    {
        lock (_gate)
        {
            if (_disposed || _records >= _maxRecords) return;
            string safe = detail.Replace('\r', ' ').Replace('\n', ' ');
            string line = $"{DateTimeOffset.UtcNow:O}\t{kind}\t{safe}";
            long encodedBytes = System.Text.Encoding.UTF8.GetByteCount(line + Environment.NewLine);
            if (_writer.BaseStream.Position + encodedBytes > _maxBytes) return;
            _writer.WriteLine(line);
            _writer.Flush();
            _records++;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _writer.Dispose();
        }
    }
}
