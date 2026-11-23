using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Il2CppSpine;
using MelonLoader;
using UnityEngine;

namespace SkinForger.CharacterPolygonPackedRectRepair;

/// <summary>
/// Character-only adapter for the existing RuntimeTexture allocator.
/// Every textured Character attachment replacement receives a fresh complete
/// rotated logical canvas. The host's existing placement, anchor, RegionAttachment
/// quad, MeshAttachment geometry and Spine UV commit paths remain authoritative.
/// </summary>
public sealed class CharacterPolygonPackedRectRepairMod : global::SkinForger.Core.SfModule
{
    private const string Prefix = "[SkinForge][CharacterPolygonPackedRectRepair]";
    private static readonly bool DetailedDiagnosticsEnabled =
        string.Equals(Environment.GetEnvironmentVariable("SKINFORGER_CHARACTER_DIAGNOSTICS"), "1", StringComparison.Ordinal);

    [ThreadStatic]
    private static Stack<BuildContext>? _buildContexts;

    private static readonly Dictionary<int, List<RectInfo>> FreshRects = new();
    private static readonly HashSet<PlacementKey> CharacterMeshPlacements = new();
    private static readonly Dictionary<string, (int AtlasId, int AppliedCount)> WalkMemo = new(StringComparer.Ordinal);
    // 1.5.3: the same contract keyed by atlas size + rect. A WarmReuse snapshot restore creates a new Texture2D with
    // an identical layout (same size, same rects), which the instance-keyed set above never recognises, so the
    // MeshAttachment UV contract would be skipped on restored atlases.
    private static readonly HashSet<(int Width, int Height, RectInfo Rect)> CharacterMeshPlacementsByLayout = new();
    private static readonly HashSet<string> EntryLogKeys = new(StringComparer.Ordinal);

    private static Type? _host;
    private static MethodInfo? _enumerateAttachmentContexts;
    private static MethodInfo? _getMaterial;
    private static MethodInfo? _getExistingRegionPath;
    private static MethodInfo? _getOrCreateSynthesizedAtlas;
    private static MethodInfo? _getSynthesizedAtlasKey;
    private static MethodInfo? _getSynthesizedRegionKey;
    private static MethodInfo? _getSynthesizedAtlasState;
    private static MethodInfo? _ensureAtlasCapacity;
    private static MethodInfo? _resolveReplacementResourceScope;
    private static MethodInfo? _scanCharacter;
    private static MethodInfo? _scanByKind;
    private static MethodInfo? _getSelected;
    private static MethodInfo? _getSkinPath;
    private static MethodInfo? _getCostumeName;
    private static MethodInfo? _tryFindSelectionByTexture;

    public override void OnInitializeMelon()
    {
        if (StandDownForAuthoredMain()) return;
        _host = AccessTools.TypeByName("SkinForger.SkinForgerMod");
        MethodInfo? build = Find(_host, "TryBuildSynthesizedAtlasForSkeleton", 5);
        MethodInfo? apply = Find(_host, "ApplyRegionPngToAtlas", 8);
        MethodInfo? applyPlacement = Find(_host, "ApplyRegionPlacementToRegion", 4);
        MethodInfo? applyAttachment = Find(_host, "ApplyRegionPlacementToAttachment", 6);
        MethodInfo? allocator = Find(_host, "AllocateExpandedPackedRect", 4);
        MethodInfo? getTexture = Find(_host, "GetTexture", 3);
        if (build == null || apply == null || applyPlacement == null || applyAttachment == null ||
            allocator == null || getTexture == null)
        {
            MelonLogger.Warning($"{Prefix} disabled: compatible 97E allocation methods were not found.");
            return;
        }

        _enumerateAttachmentContexts = Find(_host, "EnumerateAttachmentContexts", 1);
        _getMaterial = Find(_host, "GetMaterial", 1);
        _getExistingRegionPath = Find(_host, "GetExistingRegionPath", 4);
        _getOrCreateSynthesizedAtlas = Find(_host, "GetOrCreateSynthesizedAtlas", 4);
        _getSynthesizedAtlasKey = Find(_host, "GetSynthesizedAtlasKey", 3);
        _getSynthesizedRegionKey = _host?.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .SingleOrDefault(method => method.Name == "GetSynthesizedRegionKey" &&
                                       method.GetParameters().Length == 3 &&
                                       method.GetParameters()[2].ParameterType == typeof(string));
        _getSynthesizedAtlasState = Find(_host, "GetSynthesizedAtlasState", 1);
        _ensureAtlasCapacity = Find(_host, "EnsureAtlasCapacity", 3);
        _resolveReplacementResourceScope = Find(_host, "ResolveReplacementResourceScope", 0);
        _scanCharacter = _host?.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .SingleOrDefault(method => method.Name == "Scan" && method.GetParameters().Length == 1);
        _scanByKind = _host?.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .SingleOrDefault(method => method.Name == "Scan" && method.GetParameters().Length == 2);
        _getSelected = Find(_host, "GetSelected", 2);
        _getSkinPath = Find(_host, "GetSkinPath", 3);
        _getCostumeName = Find(_host, "GetCostumeName", 2);
        _tryFindSelectionByTexture = Find(_host, "TryFindSelectionByTexture", 4);

        if (_enumerateAttachmentContexts == null || _getMaterial == null || _getExistingRegionPath == null ||
            _getOrCreateSynthesizedAtlas == null || _getSynthesizedAtlasKey == null || _getSynthesizedRegionKey == null ||
            _getSynthesizedAtlasState == null || _ensureAtlasCapacity == null ||
            _resolveReplacementResourceScope == null)
        {
            MelonLogger.Warning($"{Prefix} disabled: Character atlas planning helpers were not found.");
            return;
        }

        HarmonyInstance.Patch(build,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(CharacterPolygonPackedRectRepairMod), nameof(BuildPrefix))),
            finalizer: new HarmonyMethod(AccessTools.Method(typeof(CharacterPolygonPackedRectRepairMod), nameof(BuildFinalizer))));
        HarmonyInstance.Patch(apply,
            transpiler: new HarmonyMethod(AccessTools.Method(typeof(CharacterPolygonPackedRectRepairMod), nameof(ApplyTranspiler))));
        HarmonyInstance.Patch(applyPlacement,
            postfix: new HarmonyMethod(AccessTools.Method(typeof(CharacterPolygonPackedRectRepairMod), nameof(ApplyPlacementPostfix))));
        HarmonyInstance.Patch(applyAttachment,
            postfix: new HarmonyMethod(AccessTools.Method(typeof(CharacterPolygonPackedRectRepairMod), nameof(ApplyAttachmentPostfix))));
        HarmonyInstance.Patch(getTexture,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(CharacterPolygonPackedRectRepairMod), nameof(GetTexturePrefix))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(CharacterPolygonPackedRectRepairMod), nameof(GetTexturePostfix))));

        if (DetailedDiagnosticsEnabled)
        {
            PatchEntryDiagnostic("CharacterApplyOnEnable", nameof(CharacterEntryPostfix));
            PatchEntryDiagnostic("SkeletonGraphicAwake", nameof(SkeletonEntryPostfix));
            PatchEntryDiagnostic("SkeletonRendererAwake", nameof(SkeletonEntryPostfix));
        }

        Type? geometry = AccessTools.TypeByName("SkinForger.CharacterMeshGeometry.CharacterMeshGeometry");
        MethodInfo? geometryApply = geometry?.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .SingleOrDefault(method => method.Name == "Apply");
        if (geometryApply != null)
        {
            HarmonyInstance.Patch(geometryApply,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(CharacterPolygonPackedRectRepairMod), nameof(GeometryPrefix))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(CharacterPolygonPackedRectRepairMod), nameof(GeometryPostfix))));
        }

        MelonLogger.Msg($"{Prefix} ready: Character full-canvas contract plus single-capacity/single-upload Character and Preview cold build optimization; detailedDiagnostics={DetailedDiagnosticsEnabled}.");
    }

    private static MethodInfo? Find(Type? type, string name, int parameterCount) =>
        type?.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .SingleOrDefault(method => method.Name == name && method.GetParameters().Length == parameterCount);

    private void PatchEntryDiagnostic(string hostMethodName, string patchMethodName)
    {
        MethodInfo? original = Find(_host, hostMethodName, 1);
        MethodInfo? postfix = AccessTools.Method(typeof(CharacterPolygonPackedRectRepairMod), patchMethodName);
        if (original != null && postfix != null)
            HarmonyInstance.Patch(original, postfix: new HarmonyMethod(postfix));
    }

    private static void BuildPrefix(Skeleton __0, Texture2D __1, string __2, string __3, bool __4)
    {
        var context = new BuildContext(__2, __3, __4, IsPreviewExecution());
        (_buildContexts ??= new Stack<BuildContext>()).Push(context);
        if (ShouldBatchAtlasBuild(context))
            PreallocateCharacterAtlas(__0, __1, context);
        context.PrefixMs = context.Total.Elapsed.TotalMilliseconds;
    }

    private static Exception? BuildFinalizer(Exception? __exception)
    {
        BuildContext? context = CurrentBuild;
        if (_buildContexts is { Count: > 0 }) _buildContexts.Pop();
        if (context != null)
        {
            FlushDeferredAtlasUploads(context);
            context.Total.Stop();
            MelonLogger.Msg($"{Prefix} BUILD_SUMMARY kind={(context.Elfin ? "Elfin" : context.Preview ? "Preview" : "Character")} " +
                $"skin={DisplayPath(context.Skin)} atlas={context.TextureName} totalMs={context.Total.Elapsed.TotalMilliseconds:0.###} " +
                $"attachmentContexts={context.AttachmentContexts} targets={context.Targets.Count} uncachedTargets={context.UncachedTargets} placements={context.PlacementKeys.Count} " +
                $"pngReads={context.PngReads} pngDecodes={context.PngDecodes} textureCacheHits={context.TextureCacheHits} " +
                $"persistentTexturesCreated={context.PersistentTexturesCreated} atlasBuilds={context.AtlasBuilds} " +
                $"atlasResizes={context.AtlasResizes} copiedPixels={context.CopiedPixels} " +
                $"deferredApplyCalls={context.DeferredApplyCalls} finalApplyCalls={context.FinalApplyCalls} " +
                $"meshGeometryCalls={context.MeshGeometryCalls} meshGeometryMs={context.MeshGeometryMs:0.###} " +
                $"preallocation={(context.PreallocationSucceeded ? "applied" : context.PreallocationStatus)} " +
                $"prefixMs={context.PrefixMs:0.###} previewChecks={context.PreviewChecks} previewCheckMs={context.PreviewCheckMs:0.###}");
        }
        return __exception;
    }

    private static BuildContext? CurrentBuild => _buildContexts is { Count: > 0 } ? _buildContexts.Peek() : null;

    private static bool ShouldBatchAtlasBuild(BuildContext context) => !context.Elfin;

    private static bool ShouldUseCharacterFullCanvas(
        Attachment? attachment,
        AtlasRegion? region,
        Texture2D? replacementTexture)
    {
        if (!IsCharacterTexturedAttachment(attachment) || region == null || replacementTexture == null)
            return false;

        int degrees = NormalizeDegrees(region.degrees);
        if (degrees != 0 && degrees != 90 && degrees != 180 && degrees != 270)
            return false;

        if (replacementTexture.width <= 0 || replacementTexture.height <= 0)
            return false;

        if (!HasValidRegionMetadata(region))
            return false;

        // Preview: RegionAttachments keep the frozen baseline path. 1.5.5: every Preview MeshAttachment uses the same
        // full-canvas contract as Gameplay (any PNG size). R4-12 skips Main's mesh geometry for all characters, so an
        // expanded Mesh PNG in Preview previously got neither path and was packed into the polygon rect
        // (Bassist Battle hair_1: measured 188x170 for a 182x176 PNG -> main-size-mismatch, no canvas).
        if (IsPreviewExecution())
            return attachment!.TryCast<MeshAttachment>() != null;

        return true;
    }

    private static bool IsCharacterTexturedAttachment(Attachment? attachment)
    {
        BuildContext? context = CurrentBuild;
        if (context == null || context.Elfin || attachment == null)
            return false;

        return attachment.TryCast<MeshAttachment>() != null ||
               attachment.TryCast<RegionAttachment>() != null;
    }

    private static bool IsCharacterMeshAttachment(Attachment? attachment) =>
        CurrentBuild is { Elfin: false } &&
        attachment?.TryCast<MeshAttachment>() != null;

    // 1.5.2: the context type and its properties are resolved once. 1.5.1 called AccessTools.TypeByName on every
    // region write; for a type in another assembly that scans all loaded assemblies (148 Il2Cpp proxies).
    private static bool _contextResolved;
    private static PropertyInfo? _contextCurrent;
    private static PropertyInfo? _contextPreview;

    private static bool IsPreviewExecution()
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            if (!_contextResolved)
            {
                _contextResolved = true;
                Type? contextType = AccessTools.TypeByName("SkinForger.CharacterReplacement.ReplacementExecutionContext");
                _contextCurrent = contextType?.GetProperty("Current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            }
            object? current = _contextCurrent?.GetValue(null);
            if (current == null) return false;
            if (_contextPreview == null || _contextPreview.DeclaringType != current.GetType())
                _contextPreview = current.GetType().GetProperty("Preview", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return Convert.ToBoolean(_contextPreview?.GetValue(current));
        }
        finally
        {
            BuildContext? build = CurrentBuild;
            if (build != null) { build.PreviewChecks++; build.PreviewCheckMs += (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency; }
        }
    }

    private static void PreallocateCharacterAtlas(Skeleton skeleton, Texture2D sourceTexture, BuildContext context)
    {
        if (skeleton == null || sourceTexture == null || string.IsNullOrEmpty(context.Skin) ||
            string.IsNullOrEmpty(context.TextureName))
        {
            context.PreallocationStatus = "invalid-build-input";
            return;
        }

        try
        {
            if (_enumerateAttachmentContexts!.Invoke(null, new object?[] { skeleton }) is not IEnumerable contexts)
            {
                context.PreallocationStatus = "attachment-enumeration-unavailable";
                return;
            }

            Texture2D? atlas = null;
            object? atlasState = null;
            object? resourceScope = _resolveReplacementResourceScope!.Invoke(null, null);
            object? appliedRegionKeys = ReadOptionalMember(resourceScope, "AppliedRegionKeys");
            object? atlasCache = ReadOptionalMember(resourceScope, "AtlasCache");

            // 1.5.4: a walk that found every target already applied has no effect (capacity is only ensured for
            // uncached targets). Skip it while the same cached atlas instance and the same applied-key count persist.
            string memoKey = Convert.ToString(_getSynthesizedAtlasKey!.Invoke(
                null, new object?[] { context.Skin, context.TextureName, sourceTexture })) ?? string.Empty;
            int appliedCount = ReadOptionalInt(appliedRegionKeys, "Count", -1);
            int cachedAtlasId = 0;
            if (memoKey.Length != 0 && appliedCount >= 0 && ContainsKey(atlasCache, memoKey))
            {
                Texture2D? cachedAtlas = _getOrCreateSynthesizedAtlas!.Invoke(
                    null, new object?[] { context.Skin, context.TextureName, sourceTexture, null }) as Texture2D;
                cachedAtlasId = cachedAtlas == null ? 0 : cachedAtlas.GetInstanceID();
                lock (WalkMemo)
                    if (cachedAtlasId != 0 && WalkMemo.TryGetValue(memoKey, out var memo) && memo.AtlasId == cachedAtlasId && memo.AppliedCount == appliedCount)
                    {
                        context.PreallocationStatus = "skipped-unchanged-all-cached";
                        return;
                    }
            }
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int nextY = 0;
            int padding = 0;
            int requiredWidth = sourceTexture.width;
            int requiredHeight = sourceTexture.height;

            foreach (object? item in contexts)
            {
                if (item == null) continue;
                context.AttachmentContexts++;
                Attachment? attachment = ReadOptionalMember(item, "Attachment") as Attachment;
                AtlasRegion? region = ReadOptionalMember(item, "Region") as AtlasRegion;
                if (attachment == null || region == null || !IsCharacterTexturedAttachment(attachment)) continue;

                Material? material = _getMaterial!.Invoke(null, new object?[] { attachment }) as Material;
                string? materialTextureName = material?.mainTexture?.name;
                if (!string.IsNullOrEmpty(materialTextureName) &&
                    !string.Equals(materialTextureName, context.TextureName, StringComparison.OrdinalIgnoreCase))
                    continue;

                string regionPath = Convert.ToString(_getExistingRegionPath!.Invoke(
                    null, new object?[] { context.Skin, attachment, region, context.TextureName })) ?? string.Empty;
                if (string.IsNullOrEmpty(regionPath) || !seenPaths.Add(Path.GetFullPath(regionPath))) continue;
                if (!TryReadPngDimensions(regionPath, out int pngWidth, out int pngHeight))
                {
                    context.PreallocationStatus = "png-header-invalid";
                    continue;
                }
                if (!ShouldUseCharacterFullCanvasDimensions(attachment, region, pngWidth, pngHeight)) continue;
                context.Targets[AttachmentKey(attachment)] = new PlannedTarget(regionPath, pngWidth, pngHeight);

                if (atlas == null)
                {
                    string atlasKey = Convert.ToString(_getSynthesizedAtlasKey!.Invoke(
                        null, new object?[] { context.Skin, context.TextureName, sourceTexture })) ?? string.Empty;
                    bool atlasWasCached = ContainsKey(atlasCache, atlasKey);
                    atlas = _getOrCreateSynthesizedAtlas!.Invoke(
                        null, new object?[] { context.Skin, context.TextureName, sourceTexture, null }) as Texture2D;
                    context.AtlasLookupCompleted = true;
                    context.AtlasBuilds = atlasWasCached ? 0 : 1;
                }
                if (atlas == null)
                {
                    context.PreallocationStatus = "atlas-create-failed";
                    return;
                }

                string regionKey = Convert.ToString(_getSynthesizedRegionKey!.Invoke(
                    null, new object?[] { context.Skin, context.TextureName, regionPath })) ?? string.Empty;
                regionKey += "|rt=" + atlas.GetInstanceID();
                if (ContainsValue(appliedRegionKeys, regionKey)) continue;
                context.UncachedTargets++;

                atlasState ??= _getSynthesizedAtlasState!.Invoke(null, new object?[] { atlas });
                if (atlasState == null)
                {
                    context.PreallocationStatus = "atlas-state-unavailable";
                    return;
                }
                if (nextY == 0)
                {
                    nextY = ReadOptionalInt(atlasState, "NextY", atlas.height);
                    padding = ReadOptionalInt(atlasState, "Padding", 0);
                    requiredWidth = atlas.width;
                    requiredHeight = atlas.height;
                }

                int degrees = NormalizeDegrees(region.degrees);
                int storageWidth = degrees is 90 or 270 ? pngHeight : pngWidth;
                int storageHeight = degrees is 90 or 270 ? pngWidth : pngHeight;
                requiredWidth = Math.Max(requiredWidth, storageWidth + padding * 2);
                requiredHeight = Math.Max(requiredHeight, nextY + storageHeight + padding);
                nextY += storageHeight + padding;
            }

            if (atlas == null || context.UncachedTargets == 0)
            {
                context.PreallocationStatus = "no-uncached-full-canvas-targets";
                if (atlas != null && memoKey.Length != 0 && appliedCount >= 0)
                    lock (WalkMemo) WalkMemo[memoKey] = (atlas.GetInstanceID(), appliedCount);
                return;
            }

            int oldWidth = atlas.width;
            int oldHeight = atlas.height;
            _ensureAtlasCapacity!.Invoke(null, new object?[] { atlas, requiredWidth, requiredHeight });
            if (atlas.width != oldWidth || atlas.height != oldHeight)
            {
                context.AtlasResizes++;
                context.CopiedPixels += (long)oldWidth * oldHeight;
                context.PreallocationSucceeded = atlas.width == requiredWidth && atlas.height == requiredHeight;
                context.PreallocationStatus = context.PreallocationSucceeded
                    ? $"{oldWidth}x{oldHeight}->{atlas.width}x{atlas.height}"
                    : $"capacity-mismatch:{atlas.width}x{atlas.height}!={requiredWidth}x{requiredHeight}";
            }
            else
            {
                context.PreallocationSucceeded = true;
                context.PreallocationStatus = "capacity-already-sufficient";
            }
        }
        catch (Exception ex)
        {
            context.PreallocationStatus = "failed:" + ex.GetBaseException().GetType().Name;
            MelonLogger.Warning($"{Prefix} Character atlas preallocation skipped safely: {ex.GetBaseException().Message}");
        }
    }

    private static bool ShouldUseCharacterFullCanvasDimensions(
        Attachment attachment,
        AtlasRegion region,
        int replacementWidth,
        int replacementHeight)
    {
        if (!IsCharacterTexturedAttachment(attachment) || replacementWidth <= 0 || replacementHeight <= 0)
            return false;
        int degrees = NormalizeDegrees(region.degrees);
        return degrees is 0 or 90 or 180 or 270 && HasValidRegionMetadata(region);
    }

    private static bool TryReadPngDimensions(string path, out int width, out int height) =>
        global::SkinForger.SkinPackages.LoadCaches.TryGetPngSize(path, p => (ReadPngDimensions(p, out int w, out int h), w, h), out width, out height);

    private static bool ReadPngDimensions(string path, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (global::SkinForger.HexieBlock.HexiePolicy.IsHexieName(path)) return false; // Steam-only: never read hexie PNGs
        try
        {
            using var stream = global::SkinForger.SkinPackages.VirtualFiles.OpenRead(path);   // package PNGs live in memory
            Span<byte> header = stackalloc byte[24];
            if (stream.Read(header) != header.Length ||
                header[0] != 0x89 || header[1] != 0x50 || header[2] != 0x4E || header[3] != 0x47 ||
                header[12] != 0x49 || header[13] != 0x48 || header[14] != 0x44 || header[15] != 0x52)
                return false;
            width = ReadBigEndianInt32(header[16..20]);
            height = ReadBigEndianInt32(header[20..24]);
            return width > 0 && height > 0;
        }
        catch
        {
            return false;
        }
    }

    private static int ReadBigEndianInt32(ReadOnlySpan<byte> bytes) =>
        (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];

    private static bool ContainsValue(object? collection, string value)
    {
        if (collection == null) return false;
        MethodInfo? contains = collection.GetType().GetMethod("Contains", new[] { typeof(string) });
        return contains != null && Convert.ToBoolean(contains.Invoke(collection, new object?[] { value }));
    }

    private static long AttachmentKey(Attachment attachment) => attachment.Pointer.ToInt64();

    [HarmonyPriority(HarmonyLib.Priority.Last)]
    private static IEnumerable<CodeInstruction> ApplyTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        Type host = AccessTools.TypeByName("SkinForger.SkinForgerMod")
            ?? throw new TypeLoadException("SkinForger.SkinForgerMod");
        MethodInfo allocator = AccessTools.Method(host, "AllocateExpandedPackedRect")
            ?? throw new MissingMethodException(host.FullName, "AllocateExpandedPackedRect");
        int allocatorCall = code.FindIndex(instruction => instruction.Calls(allocator));
        if (allocatorCall < 0) throw new InvalidOperationException("AllocateExpandedPackedRect call was not found.");

        int branch = -1;
        for (int index = allocatorCall - 1; index >= Math.Max(0, allocatorCall - 20); index--)
        {
            if (code[index].opcode == OpCodes.Brtrue || code[index].opcode == OpCodes.Brtrue_S)
            {
                branch = index;
                break;
            }
        }
        if (branch < 1 || !IsLoadLocal(code[branch - 1]))
            throw new InvalidOperationException("Existing expanded-condition branch was not found.");

        CodeInstruction expandedLoad = CloneLocalLoad(code[branch - 1]);
        CodeInstruction originalRectLoad = CloneLocalLoad(code[branch + 1]);
        MethodInfo forceHelper = AccessTools.Method(typeof(CharacterPolygonPackedRectRepairMod), nameof(ShouldUseCharacterFullCanvas))
            ?? throw new MissingMethodException(nameof(ShouldUseCharacterFullCanvas));
        int widthGetter = FindTextureDimensionGetter(code, branch, allocatorCall, "get_width");
        if (widthGetter < 1 || !IsLoadLocal(code[widthGetter - 1]))
            throw new InvalidOperationException("Replacement Texture2D local was not found.");
        CodeInstruction replacementTextureLoad = CloneLocalLoad(code[widthGetter - 1]);

        // Promote the host's expanded local to the shared full-canvas contract.
        // This keeps allocation, Region metadata, RegionAttachment quad sizing,
        // CharacterMeshGeometry and final SetRegion commits on one decision.
        if (branch < 1 || !IsLoadLocal(code[branch - 1]))
            throw new InvalidOperationException("Allocator selection branch was not found.");
        code.InsertRange(branch, new[]
        {
            new CodeInstruction(OpCodes.Ldarg_1),
            new CodeInstruction(OpCodes.Ldarg_2),
            replacementTextureLoad,
            new CodeInstruction(OpCodes.Call, forceHelper),
            new CodeInstruction(OpCodes.Or),
            new CodeInstruction(OpCodes.Dup),
            StoreForLoad(expandedLoad)
        });

        // Re-locate the allocator after the insertion, then find the common
        // PixelRect store at the end of the existing ternary.
        allocatorCall = code.FindIndex(instruction => instruction.Calls(allocator));
        int rectStore = -1;
        for (int index = allocatorCall + 1; index < Math.Min(code.Count, allocatorCall + 12); index++)
        {
            if (IsStoreLocal(code[index]))
            {
                rectStore = index;
                break;
            }
        }
        if (rectStore < 0)
            throw new InvalidOperationException("Replacement PixelRect local assignment was not found.");

        if (!IsLoadLocal(originalRectLoad))
            throw new InvalidOperationException("Original PixelRect local load was not found.");

        Type rectType = GetLocalType(code[rectStore])
            ?? throw new InvalidOperationException("PixelRect local type was not available.");
        MethodInfo logHelper = AccessTools.Method(typeof(CharacterPolygonPackedRectRepairMod), nameof(LogPlacement))
            ?? throw new MissingMethodException(nameof(LogPlacement));

        code.InsertRange(rectStore + 1, new[]
        {
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Ldarg_1),
            new CodeInstruction(OpCodes.Ldarg_2),
            CloneLocalLoad(originalRectLoad),
            new CodeInstruction(OpCodes.Box, rectType),
            LoadForStore(code[rectStore]),
            new CodeInstruction(OpCodes.Box, rectType),
            CloneLocalLoad(expandedLoad),
            new CodeInstruction(OpCodes.Call, logHelper)
        });

        MethodInfo textureApply = AccessTools.Method(typeof(Texture2D), nameof(Texture2D.Apply), Type.EmptyTypes)
            ?? throw new MissingMethodException(typeof(Texture2D).FullName, nameof(Texture2D.Apply));
        MethodInfo uploadHelper = AccessTools.Method(typeof(CharacterPolygonPackedRectRepairMod), nameof(CommitOrDeferAtlasUpload))
            ?? throw new MissingMethodException(nameof(CommitOrDeferAtlasUpload));
        int uploadCalls = 0;
        foreach (CodeInstruction instruction in code)
        {
            if (!instruction.Calls(textureApply)) continue;
            instruction.opcode = OpCodes.Call;
            instruction.operand = uploadHelper;
            uploadCalls++;
        }
        if (uploadCalls != 1)
            throw new InvalidOperationException($"Expected one target Atlas Apply call, found {uploadCalls}.");

        return code;
    }

    private static void CommitOrDeferAtlasUpload(Texture2D atlas)
    {
        BuildContext? context = CurrentBuild;
        if (atlas == null || context == null || !ShouldBatchAtlasBuild(context))
        {
            atlas?.Apply();
            return;
        }

        context.PendingAtlases[atlas.GetInstanceID()] = atlas;
        context.DeferredApplyCalls++;
    }

    private static void FlushDeferredAtlasUploads(BuildContext context)
    {
        foreach (Texture2D atlas in context.PendingAtlases.Values)
        {
            if (atlas == null) continue;
            try
            {
                atlas.Apply();
                context.FinalApplyCalls++;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"{Prefix} final atlas upload failed: {ex.Message}");
            }
        }
        context.PendingAtlases.Clear();
    }

    private static void ApplyPlacementPostfix(AtlasRegion __0, Texture2D __1, object __2)
    {
        if (__0 == null || __1 == null || __2 == null) return;

        object packedValue = ReadMember(__2, "PackedRect");
        object originalValue = ReadMember(__2, "OriginalPackedRect");
        RectInfo packed = ReadRect(packedValue);
        RectInfo original = ReadRect(originalValue);
        if (packed == original) return;

        var key = new PlacementKey(__1.GetInstanceID(), packed);
        lock (FreshRects)
        {
            if (!CharacterMeshPlacements.Contains(key) && !CharacterMeshPlacementsByLayout.Contains((__1.width, __1.height, packed))) return;
        }

        if (!TryBuildRegionContract(__0, __1, packed, out RegionContract contract))
        {
            MelonLogger.Warning($"{Prefix} skipped incompatible Character MeshAttachment placement: rect={packed}, degrees={__0.degrees}.");
            return;
        }

        // Only AtlasRegion's location and UV bounds change. Region dimensions,
        // original dimensions, offsets, degrees and the MeshAttachment's own
        // metadata stay untouched for Spine's existing UpdateUVs implementation.
        __0.x = contract.AtlasX;
        __0.y = contract.AtlasY;
        __0.u = contract.U;
        __0.u2 = contract.U2;
        __0.v = contract.V;
        __0.v2 = contract.V2;
    }

    private static void ApplyAttachmentPostfix(
        Attachment __0,
        AtlasRegion __1,
        object __2,
        string __3,
        string __4,
        string __5)
    {
        BuildContext? context = CurrentBuild;
        if (context == null || context.Elfin || context.Preview || __0 == null || __1 == null || __2 == null)
            return;

        RectInfo packed = ReadRect(ReadMember(__2, "PackedRect"));
        MeshAttachment? mesh = __0.TryCast<MeshAttachment>();
        RegionAttachment? quad = __0.TryCast<RegionAttachment>();
        if (mesh == null && quad == null) return;

        context.PlacementKeys.Add($"{AttachmentKey(__0):X}|{packed}");
        if (!DetailedDiagnosticsEnabled) return;

        bool expanded = ReadOptionalBool(__2, "Expanded");
        RectInfo original = ReadRect(ReadMember(__2, "OriginalPackedRect"));
        int normalWidth = ReadOptionalInt(__2, "NormalWidth", 0);
        int normalHeight = ReadOptionalInt(__2, "NormalHeight", 0);
        string type;
        string geometry;
        if (mesh != null)
        {
            type = "MeshAttachment";
            geometry = $"worldVertices={mesh.worldVerticesLength} vertices={mesh.vertices?.Length ?? 0} " +
                       $"bones={mesh.bones?.Length ?? 0} triangles={mesh.triangles?.Length ?? 0}";
        }
        else
        {
            type = "RegionAttachment";
            geometry = $"quad={quad!.width:0.###}x{quad.height:0.###} xy={quad.x:0.###},{quad.y:0.###} rotation={quad.rotation:0.###}";
        }

        string pngPath = string.IsNullOrEmpty(__4) ? context.Targets.GetValueOrDefault(AttachmentKey(__0)).Path : __4;
        MelonLogger.Msg($"{Prefix} FULL_CANVAS_COMMIT attachment={__0.Name ?? "<null>"} type={type} " +
            $"png={DisplayPath(pngPath)} originalRect={original} finalRect={packed} " +
            $"normal={normalWidth}x{normalHeight} expanded={expanded} oldCropExecuted={!expanded} " +
            $"region={__1.width:0.###}x{__1.height:0.###} original={__1.originalWidth:0.###}x{__1.originalHeight:0.###} " +
            $"offset={__1.offsetX:0.###},{__1.offsetY:0.###} degrees={__1.degrees} " +
            $"uv={__1.u:0.######},{__1.v:0.######},{__1.u2:0.######},{__1.v2:0.######} {geometry}");
    }

    private static void GetTexturePrefix(string __0, out TextureLoadState __state)
    {
        BuildContext? context = CurrentBuild;
        if (context == null || context.Preview)
        {
            __state = default;
            return;
        }

        bool cached = false;
        try
        {
            object? scope = _resolveReplacementResourceScope?.Invoke(null, null);
            object? cache = ReadOptionalMember(scope, "DecodedTextures");
            cached = ContainsKey(cache, __0);
        }
        catch
        {
            cached = false;
        }
        __state = new TextureLoadState(true, !cached, Stopwatch.StartNew());
    }

    private static void GetTexturePostfix(Texture2D __result, TextureLoadState __state)
    {
        if (!__state.Active) return;
        __state.Timer?.Stop();
        BuildContext? context = CurrentBuild;
        if (context == null) return;
        if (__state.CacheMiss)
        {
            context.PngReads++;
            context.PngDecodes++;
            if (__result != null) context.PersistentTexturesCreated++;
        }
        else
        {
            context.TextureCacheHits++;
        }
    }

    private static void GeometryPrefix(out Stopwatch? __state)
    {
        __state = CurrentBuild is { Elfin: false, Preview: false } ? Stopwatch.StartNew() : null;
    }

    private static void GeometryPostfix(Stopwatch? __state)
    {
        if (__state == null) return;
        __state.Stop();
        BuildContext? context = CurrentBuild;
        if (context == null) return;
        context.MeshGeometryCalls++;
        context.MeshGeometryMs += __state.Elapsed.TotalMilliseconds;
    }

    private static void CharacterEntryPostfix(object __0)
    {
        try
        {
            int index = ReadOptionalInt(__0, "m_Index", -1);
            GameObject? gameObject = (__0 as Component)?.gameObject;
            if (index < 0 && gameObject != null && _scanCharacter != null)
                index = Convert.ToInt32(_scanCharacter.Invoke(null, new object?[] { gameObject }));
            LogEntry("Character", "CharacterApply", gameObject, null, index, false);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"{Prefix} Character entry diagnostic failed: {ex.GetBaseException().Message}");
        }
    }

    private static void SkeletonEntryPostfix(object __0)
    {
        try
        {
            GameObject? gameObject = (__0 as Component)?.gameObject;
            int index = -1;
            bool elfin = false;
            if (gameObject != null && _scanCharacter != null)
                index = Convert.ToInt32(_scanCharacter.Invoke(null, new object?[] { gameObject }));
            if (index < 0 && gameObject != null && _scanByKind != null)
            {
                index = Convert.ToInt32(_scanByKind.Invoke(null, new object?[] { gameObject, true }));
                elfin = index >= 0;
            }

            Texture? texture = GetEntryTexture(__0);
            if (index < 0 && texture != null && _tryFindSelectionByTexture != null)
            {
                object?[] args = { texture, -1, false, string.Empty };
                if (Convert.ToBoolean(_tryFindSelectionByTexture.Invoke(null, args)))
                {
                    index = Convert.ToInt32(args[1]);
                    elfin = Convert.ToBoolean(args[2]);
                }
            }

            LogEntry(elfin ? "Elfin" : "UnresolvedSkeleton", __0.GetType().Name, gameObject, texture, index, elfin);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"{Prefix} Skeleton entry diagnostic failed: {ex.GetBaseException().Message}");
        }
    }

    private static void LogEntry(string kind, string hook, GameObject? gameObject, Texture? texture, int index, bool elfin)
    {
        if (!DetailedDiagnosticsEnabled) return;

        int selected = index >= 0 && _getSelected != null
            ? Convert.ToInt32(_getSelected.Invoke(null, new object?[] { index, elfin }))
            : -1;
        string skin = index >= 0 && _getSkinPath != null
            ? Convert.ToString(_getSkinPath.Invoke(null, new object?[] { index, selected, elfin })) ?? string.Empty
            : string.Empty;
        string costume = index >= 0 && _getCostumeName != null
            ? Convert.ToString(_getCostumeName.Invoke(null, new object?[] { index, elfin })) ?? string.Empty
            : string.Empty;
        string decision = index < 0 ? "identity-unresolved" :
            selected < 0 ? "no-skin-selected" :
            string.IsNullOrEmpty(skin) ? "skin-path-unresolved" :
            !global::SkinForger.SkinPackages.VirtualFiles.DirectoryExists(skin) ? "skin-directory-missing" :
            !global::SkinForger.SkinPackages.VirtualFiles.FileExists(Path.Combine(skin, "manifest.json")) ? "manifest-missing" :
            "replacement-eligible";
        string objectName = gameObject?.name ?? "<null>";
        string textureName = texture?.name ?? "<null>";
        string key = $"{kind}|{hook}|{objectName}|{textureName}|{index}|{selected}|{skin}|{decision}";
        lock (EntryLogKeys)
        {
            if (!EntryLogKeys.Add(key)) return;
        }
        MelonLogger.Msg($"{Prefix} ENTRY kind={kind} hook={hook} object={objectName} texture={textureName} " +
            $"index={index} costume={costume} selected={selected} rootSkin={DisplayPath(skin)} decision={decision}");
    }

    private static Texture? GetEntryTexture(object instance)
    {
        object? overrideTexture = ReadOptionalMember(instance, "OverrideTexture");
        if (overrideTexture is Texture texture) return texture;
        object? meshRenderer = ReadOptionalMember(instance, "meshRenderer");
        object? material = ReadOptionalMember(meshRenderer, "sharedMaterial") ?? ReadOptionalMember(instance, "material");
        return ReadOptionalMember(material, "mainTexture") as Texture ?? ReadOptionalMember(instance, "mainTexture") as Texture;
    }

    private static int FindTextureDimensionGetter(
        IReadOnlyList<CodeInstruction> code,
        int branch,
        int allocatorCall,
        string getterName)
    {
        for (int index = branch + 1; index < allocatorCall; index++)
        {
            if ((code[index].opcode == OpCodes.Call || code[index].opcode == OpCodes.Callvirt) &&
                code[index].operand is MethodInfo method && method.Name == getterName &&
                method.DeclaringType != null && typeof(Texture).IsAssignableFrom(method.DeclaringType))
                return index;
        }
        return -1;
    }

    private static bool HasValidRegionMetadata(AtlasRegion region)
    {
        float width = region.width;
        float height = region.height;
        float originalWidth = region.originalWidth;
        float originalHeight = region.originalHeight;
        float offsetX = region.offsetX;
        float offsetY = region.offsetY;
        const float epsilon = 0.01f;
        return float.IsFinite(width) && float.IsFinite(height) &&
               float.IsFinite(originalWidth) && float.IsFinite(originalHeight) &&
               float.IsFinite(offsetX) && float.IsFinite(offsetY) &&
               width > 0 && height > 0 && originalWidth > 0 && originalHeight > 0 &&
               offsetX >= -epsilon && offsetY >= -epsilon &&
               offsetX + width <= originalWidth + epsilon &&
               offsetY + height <= originalHeight + epsilon;
    }

    private static bool TryBuildRegionContract(
        AtlasRegion region,
        Texture2D runtimeTexture,
        RectInfo packed,
        out RegionContract contract)
    {
        contract = default;
        if (!HasValidRegionMetadata(region) || runtimeTexture.width <= 0 || runtimeTexture.height <= 0)
            return false;

        int degrees = NormalizeDegrees(region.degrees);
        if (degrees != 0 && degrees != 90 && degrees != 180 && degrees != 270)
            return false;

        float width = region.width;
        float height = region.height;
        float originalWidth = region.originalWidth;
        float originalHeight = region.originalHeight;
        float offsetX = region.offsetX;
        float offsetY = region.offsetY;
        int expectedStorageWidth = RoundToInt(degrees == 90 || degrees == 270 ? originalHeight : originalWidth);
        int expectedStorageHeight = RoundToInt(degrees == 90 || degrees == 270 ? originalWidth : originalHeight);
        if (packed.Width != expectedStorageWidth || packed.Height != expectedStorageHeight)
            return false;

        float uvLeft;
        float uvBottom;
        float uvWidth;
        float uvHeight;
        switch (degrees)
        {
            case 0:
                uvLeft = offsetX;
                uvBottom = offsetY;
                uvWidth = width;
                uvHeight = height;
                break;
            case 90:
                uvLeft = originalHeight - offsetY - height;
                uvBottom = offsetX;
                uvWidth = height;
                uvHeight = width;
                break;
            case 180:
                uvLeft = originalWidth - offsetX - width;
                uvBottom = originalHeight - offsetY - height;
                uvWidth = width;
                uvHeight = height;
                break;
            default: // 270: Spine's UpdateUVs uses unrotated width/height spans.
                uvLeft = offsetY;
                uvBottom = originalWidth - offsetX - height;
                uvWidth = width;
                uvHeight = height;
                break;
        }

        float left = packed.X + uvLeft;
        float bottom = packed.Y + uvBottom;
        float top = bottom + uvHeight;
        contract = new RegionContract(
            RoundToInt(left),
            runtimeTexture.height - RoundToInt(top),
            left / runtimeTexture.width,
            top / runtimeTexture.height,
            (left + uvWidth) / runtimeTexture.width,
            bottom / runtimeTexture.height);
        return true;
    }

    private static int NormalizeDegrees(int degrees)
    {
        int normalized = degrees % 360;
        return normalized < 0 ? normalized + 360 : normalized;
    }

    private static int RoundToInt(float value) =>
        (int)Math.Round(value, MidpointRounding.AwayFromZero);

    private static void LogPlacement(
        Texture2D runtimeTexture,
        Attachment attachment,
        AtlasRegion region,
        object originalRectValue,
        object replacementRectValue,
        bool expanded)
    {
        RectInfo original = ReadRect(originalRectValue);
        RectInfo replacement = ReadRect(replacementRectValue);
        int textureId = runtimeTexture.GetInstanceID();
        bool repairPlacement = IsCharacterMeshAttachment(attachment) && (expanded || replacement != original);
        bool allocatorSelected = expanded || repairPlacement;
        BuildContext? context = CurrentBuild;
        if (context != null && allocatorSelected)
            context.PlacementKeys.Add($"{AttachmentKey(attachment):X}|{replacement}");

        lock (FreshRects)
        {
            if (!FreshRects.TryGetValue(textureId, out List<RectInfo>? allocated))
            {
                allocated = new List<RectInfo>();
                FreshRects[textureId] = allocated;
            }

            bool overlapsFresh = allocatorSelected && allocated.Any(existing => existing.Overlaps(replacement));
            if (allocatorSelected) allocated.Add(replacement);
            if (repairPlacement) CharacterMeshPlacements.Add(new PlacementKey(textureId, replacement));
            if (repairPlacement) CharacterMeshPlacementsByLayout.Add((runtimeTexture.width, runtimeTexture.height, replacement));

            if (!repairPlacement) return;

            RegionContract contract = default;
            bool hasImmediateContract = !expanded &&
                TryBuildRegionContract(region, runtimeTexture, replacement, out contract);
            if (!expanded && !hasImmediateContract)
            {
                MelonLogger.Warning($"{Prefix} contract validation failed: name={attachment.Name}, rect={replacement}, degrees={region?.degrees ?? 0}.");
                return;
            }

            if (overlapsFresh)
                MelonLogger.Warning($"{Prefix} fresh packed rect overlap: attachment={attachment.Name}, rect={replacement}.");
        }
    }

    private static RectInfo ReadRect(object value)
    {
        Type type = value.GetType();
        return new RectInfo(
            ReadInt(type, value, "X"),
            ReadInt(type, value, "Y"),
            ReadInt(type, value, "Width"),
            ReadInt(type, value, "Height"));
    }

    private static int ReadInt(Type type, object value, string name) =>
        Convert.ToInt32(type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(value));

    private static object ReadMember(object value, string name) =>
        value.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(value)
        ?? throw new MissingMemberException(value.GetType().FullName, name);

    private static bool ReadBool(object value, string name) => Convert.ToBoolean(ReadMember(value, name));

    private static object? ReadOptionalMember(object? value, string name)
    {
        if (value == null) return null;
        Type type = value.GetType();
        PropertyInfo? property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property != null) return property.GetValue(value);
        FieldInfo? field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return field?.GetValue(value);
    }

    private static int ReadOptionalInt(object? value, string name, int fallback)
    {
        object? result = ReadOptionalMember(value, name);
        return result == null ? fallback : Convert.ToInt32(result);
    }

    private static bool ReadOptionalBool(object? value, string name)
    {
        object? result = ReadOptionalMember(value, name);
        return result != null && Convert.ToBoolean(result);
    }

    private static bool ContainsKey(object? dictionary, string key)
    {
        if (dictionary == null) return false;
        MethodInfo? contains = dictionary.GetType().GetMethod("ContainsKey", new[] { typeof(string) });
        return contains != null && Convert.ToBoolean(contains.Invoke(dictionary, new object?[] { key }));
    }

    private static string DisplayPath(string? path) => string.IsNullOrEmpty(path) ? "<none>" : path;


    private static bool IsLoadLocal(CodeInstruction instruction) =>
        instruction.opcode == OpCodes.Ldloc || instruction.opcode == OpCodes.Ldloc_S ||
        instruction.opcode == OpCodes.Ldloc_0 || instruction.opcode == OpCodes.Ldloc_1 ||
        instruction.opcode == OpCodes.Ldloc_2 || instruction.opcode == OpCodes.Ldloc_3;

    private static bool IsStoreLocal(CodeInstruction instruction) =>
        instruction.opcode == OpCodes.Stloc || instruction.opcode == OpCodes.Stloc_S ||
        instruction.opcode == OpCodes.Stloc_0 || instruction.opcode == OpCodes.Stloc_1 ||
        instruction.opcode == OpCodes.Stloc_2 || instruction.opcode == OpCodes.Stloc_3;

    private static bool IsMatchingStore(CodeInstruction store, CodeInstruction load, object? local)
    {
        if (load.opcode == OpCodes.Ldloc || load.opcode == OpCodes.Ldloc_S)
            return (store.opcode == OpCodes.Stloc || store.opcode == OpCodes.Stloc_S) && Equals(store.operand, local);
        return (load.opcode == OpCodes.Ldloc_0 && store.opcode == OpCodes.Stloc_0) ||
               (load.opcode == OpCodes.Ldloc_1 && store.opcode == OpCodes.Stloc_1) ||
               (load.opcode == OpCodes.Ldloc_2 && store.opcode == OpCodes.Stloc_2) ||
               (load.opcode == OpCodes.Ldloc_3 && store.opcode == OpCodes.Stloc_3);
    }

    private static Type? GetLocalType(CodeInstruction store) => store.operand switch
    {
        LocalBuilder local => local.LocalType,
        _ => null
    };

    private static CodeInstruction LoadForStore(CodeInstruction store) => store.opcode switch
    {
        var opcode when opcode == OpCodes.Stloc => new CodeInstruction(OpCodes.Ldloc, store.operand),
        var opcode when opcode == OpCodes.Stloc_S => new CodeInstruction(OpCodes.Ldloc_S, store.operand),
        var opcode when opcode == OpCodes.Stloc_0 => new CodeInstruction(OpCodes.Ldloc_0),
        var opcode when opcode == OpCodes.Stloc_1 => new CodeInstruction(OpCodes.Ldloc_1),
        var opcode when opcode == OpCodes.Stloc_2 => new CodeInstruction(OpCodes.Ldloc_2),
        var opcode when opcode == OpCodes.Stloc_3 => new CodeInstruction(OpCodes.Ldloc_3),
        _ => throw new InvalidOperationException("Unsupported PixelRect local store.")
    };

    private static CodeInstruction StoreForLoad(CodeInstruction load) => load.opcode switch
    {
        var opcode when opcode == OpCodes.Ldloc => new CodeInstruction(OpCodes.Stloc, load.operand),
        var opcode when opcode == OpCodes.Ldloc_S => new CodeInstruction(OpCodes.Stloc_S, load.operand),
        var opcode when opcode == OpCodes.Ldloc_0 => new CodeInstruction(OpCodes.Stloc_0),
        var opcode when opcode == OpCodes.Ldloc_1 => new CodeInstruction(OpCodes.Stloc_1),
        var opcode when opcode == OpCodes.Ldloc_2 => new CodeInstruction(OpCodes.Stloc_2),
        var opcode when opcode == OpCodes.Ldloc_3 => new CodeInstruction(OpCodes.Stloc_3),
        _ => throw new InvalidOperationException("Unsupported expanded local load.")
    };

    private static CodeInstruction CloneLocalLoad(CodeInstruction source) => new(source.opcode, source.operand);
    private static CodeInstruction CloneLocalStore(CodeInstruction source) => new(source.opcode, source.operand);

    private readonly record struct RectInfo(int X, int Y, int Width, int Height)
    {
        public bool Overlaps(RectInfo other) =>
            X < other.X + other.Width && X + Width > other.X &&
            Y < other.Y + other.Height && Y + Height > other.Y;

        public override string ToString() => $"{X},{Y},{Width}x{Height}";
    }

    private readonly record struct PlacementKey(int TextureId, RectInfo Rect);

    private readonly record struct PlannedTarget(string Path, int Width, int Height);

    private readonly record struct TextureLoadState(bool Active, bool CacheMiss, Stopwatch? Timer);

    private sealed class BuildContext
    {
        public BuildContext(string skin, string textureName, bool elfin, bool preview)
        {
            Skin = skin ?? string.Empty;
            TextureName = textureName ?? string.Empty;
            Elfin = elfin;
            Preview = preview;
            Total = Stopwatch.StartNew();
        }

        public string Skin { get; }
        public string TextureName { get; }
        public bool Elfin { get; }
        public bool Preview { get; }
        public Stopwatch Total { get; }
        public Dictionary<long, PlannedTarget> Targets { get; } = new();
        public Dictionary<int, Texture2D> PendingAtlases { get; } = new();
        public HashSet<string> PlacementKeys { get; } = new(StringComparer.Ordinal);
        public int AttachmentContexts { get; set; }
        public int UncachedTargets { get; set; }
        public int PngReads { get; set; }
        public int PngDecodes { get; set; }
        public int TextureCacheHits { get; set; }
        public int PersistentTexturesCreated { get; set; }
        public int AtlasBuilds { get; set; }
        public int AtlasResizes { get; set; }
        public long CopiedPixels { get; set; }
        public int DeferredApplyCalls { get; set; }
        public int FinalApplyCalls { get; set; }
        public int MeshGeometryCalls { get; set; }
        public double MeshGeometryMs { get; set; }
        public bool AtlasLookupCompleted { get; set; }
        public bool PreallocationSucceeded { get; set; }
        public string PreallocationStatus { get; set; } = "not-attempted";
        public double PrefixMs { get; set; }
        public int PreviewChecks { get; set; }
        public double PreviewCheckMs { get; set; }
    }

    private readonly record struct RegionContract(
        int AtlasX,
        int AtlasY,
        float U,
        float V,
        float U2,
        float V2);

    /// <summary>
    /// This module patches the OLD Main, which writes its atlas one region at a time. The authored Main
    /// composes the whole atlas in one pass instead, where every replacement already gets a rectangle of its own (AtlasComposer rule 1), which is what this module exists to force.
    ///
    /// Patching it anyway would be worse than useless: the transpilers here look for IL the authored Main
    /// does not contain, and teaching it to write region by region would bring back the very design the cut
    /// removes — with it the 2026-10-01 endlessly growing atlas and the 2026-10-04 overlapping rectangles.
    /// So when the authored Main is the one loaded, this module stands down and says so.
    /// </summary>
    private static bool StandDownForAuthoredMain()
    {
        if (global::SkinForger.MainBridge.MainHost.Generation != global::SkinForger.MainBridge.MainGeneration.Authored)
            return false;
        MelonLoader.MelonLogger.Msg($"{Prefix} not needed: the authored Main composes the atlas itself");
        return true;
    }

}
