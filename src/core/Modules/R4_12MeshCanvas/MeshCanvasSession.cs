using Il2CppSpine;
using Il2CppSpine.Unity;
using SkinForger.R47.Core;
using UnityEngine;

namespace SkinForger.R412.Runtime;

/// <summary>
/// Canvas expansion for every MeshAttachment of one SkeletonRenderer whose current region is an expanded PNG.
/// No attachment is written. Per frame, inside Spine's transient MeshGenerator buffers (mechanism proven in-game
/// by R4-6.2 and R4-7): reserve in GenerateMeshOverride, then in the MeshGenerator.FillVertexData prefix
///  1. remap each target's native UVs into the original-image rectangle of its expanded PNG,
///  2. append band vertices covering "expanded canvas minus mesh", placed from Spine's evaluated vertices,
///  3. insert band triangles right after the target's own triangles in the same submesh.
/// A target is active only while Spine maps its region at the expanded PNG's texel size, so Original mode,
/// unexpanded PNGs and foreign atlases are left untouched.
/// </summary>
internal sealed class MeshCanvasSession : IDisposable
{
    private const long HashPrime = 1099511628211L;
    private static readonly object RegistryGate = new();
    private static readonly HashSet<long> RegisteredRenderers = new();
    private static readonly Dictionary<long, Shape> Shapes = new();
    private static readonly Dictionary<string, RingTopology> Topologies = new();
    private static readonly HashSet<string> LoggedDecisions = new();

    private readonly SkeletonRenderer? _renderer;
    private readonly SkeletonGraphic? _graphic;
    private readonly Action<string, string> _log;
    private readonly long _rendererIdentity;
    private readonly bool _oldDisableRenderingOnOverride;
    private readonly SkeletonRenderer.InstructionDelegate _instructionCallback;
    private readonly SkeletonRenderer.SkeletonRendererDelegate _rebuildCallback;
    private readonly Dictionary<long, Layout> _layouts = new();
    private int _mostTargets;
    private bool _lostLogged;
    private HashSet<string> _candidates;
    private int _candidateVersion;
    private PendingFrame? _pending;
    private bool _disposed;
    private bool _instructionRegistered;
    private bool _rebuildRegistered;
    private bool _fillHookMissing;
    private int _committed, _rejected, _generateCalls, _fillCalls, _framesWithTargets;

    internal string AtlasFolder { get; }
    internal long NativePointer => _rendererIdentity;
    internal bool CleanupComplete => !_instructionRegistered && !_rebuildRegistered;

    /// <summary>The renderer was destroyed (its address now belongs to a new object): forget it without native calls.</summary>
    internal void Abandon()
    {
        _disposed = true;
        _pending = null;
        _instructionRegistered = _rebuildRegistered = false;
        lock (RegistryGate) RegisteredRenderers.Remove(_rendererIdentity);
    }

    internal MeshCanvasSession(SkeletonRenderer renderer, string atlasFolder, Action<string, string> log)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        AtlasFolder = atlasFolder;
        _rendererIdentity = renderer.Pointer.ToInt64();
        if (_rendererIdentity == 0) throw new InvalidOperationException("renderer-native-identity-unavailable");
        if (renderer.singleSubmesh) throw new NotSupportedException("single-submesh-generate-override-unavailable-in-spine-3.8");
        _candidateVersion = SpecResolver.Version;
        _candidates = SpecResolver.CandidateNames(atlasFolder);

        _instructionCallback = (Action<SkeletonRendererInstruction>)OnGenerateInstructions;
        _rebuildCallback = (Action<SkeletonRenderer>)OnRendererRebuilt;
        lock (RegistryGate)
            if (!RegisteredRenderers.Add(_rendererIdentity))
                throw new InvalidOperationException("renderer-already-owned-by-r4-12");
        _oldDisableRenderingOnOverride = renderer.disableRenderingOnOverride;
        try
        {
            renderer.disableRenderingOnOverride = false;
            renderer.add_GenerateMeshOverride(_instructionCallback); _instructionRegistered = true;
            renderer.add_OnRebuild(_rebuildCallback); _rebuildRegistered = true;
        }
        catch
        {
            TryCleanup();
            lock (RegistryGate) RegisteredRenderers.Remove(_rendererIdentity);
            throw;
        }
    }

    /// <summary>
    /// R4-12 0.13: SkeletonGraphic (UI Spine, e.g. the Victory / Fail result characters). It has no GenerateMeshOverride;
    /// the mod calls Reserve() from a MeshGenerator.BuildMeshWithArrays prefix while this graphic's UpdateMesh runs, and
    /// the FillVertexData prefix applies the frame exactly as for a SkeletonRenderer. Nothing is registered on the graphic.
    /// </summary>
    internal MeshCanvasSession(SkeletonGraphic graphic, string atlasFolder, Action<string, string> log)
    {
        _graphic = graphic ?? throw new ArgumentNullException(nameof(graphic));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        AtlasFolder = atlasFolder;
        _rendererIdentity = graphic.Pointer.ToInt64();
        if (_rendererIdentity == 0) throw new InvalidOperationException("graphic-native-identity-unavailable");
        _candidateVersion = SpecResolver.Version;
        _candidates = SpecResolver.CandidateNames(atlasFolder);
        _instructionCallback = (Action<SkeletonRendererInstruction>)OnGenerateInstructions;
        _rebuildCallback = (Action<SkeletonRenderer>)OnRendererRebuilt;
        lock (RegistryGate)
            if (!RegisteredRenderers.Add(_rendererIdentity))
                throw new InvalidOperationException("graphic-already-owned-by-r4-12");
    }

    internal bool IsGraphic => _graphic != null;

    /// <summary>Graphic path: reserve band space in the instruction BuildMeshWithArrays is about to consume.</summary>
    internal void Reserve(SkeletonRendererInstruction instruction) => OnGenerateInstructions(instruction);

    private MeshGenerator? Generator => _renderer != null ? _renderer.meshGenerator : _graphic?.MeshGenerator;

    /// <summary>Generator of the frame whose reservation is pending (managed comparison only, no interop call).</summary>
    private IntPtr _generatorPointer;

    internal bool Owns(MeshGenerator generator) =>
        !_disposed && (_pending != null || _pendingClip != null || _pendingTransforms != null) && _generatorPointer != IntPtr.Zero && _generatorPointer == generator.Pointer;

    /// <summary>Mesh parts with a JSON offset / rotation in this frame: (first native vertex, part, transform).</summary>
    private List<(int First, Shape Shape, PartTransforms.Entry Entry)>? _pendingTransforms;

    /// <summary>Frame with active Spine clipping: no reservation; bands are appended after generation (ApplyClipped).</summary>
    private SkeletonRendererInstruction? _pendingClip;

    internal bool GeneratorIs(MeshGenerator generator) => !_disposed && Generator?.Pointer == generator.Pointer;

    // ---------------------------------------------------------------- reservation

    private long _generateTicks, _applyTicks;

    private void OnGenerateInstructions(SkeletonRendererInstruction instruction)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        try { GenerateCore(instruction); }
        finally { _generateTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start; }
    }

    private void GenerateCore(SkeletonRendererInstruction instruction)
    {
        _pending = null;
        _pendingClip = null;
        _pendingTransforms = null;
        if (_disposed || _fillHookMissing) return;
        _generateCalls++;
        if (PartTransforms.Any && !instruction.hasActiveClipping)
        {
            try
            {
                _pendingTransforms = CollectTransforms(instruction);
                if (_pendingTransforms != null) _generatorPointer = Generator?.Pointer ?? IntPtr.Zero;
            }
            catch (Exception exception) { _pendingTransforms = null; _log("PART_TRANSFORM_SKIPPED", $"atlas={AtlasFolder} " + exception.GetBaseException().Message); }
        }
        if (_framesWithTargets >= 120 && _fillCalls == 0)
        {
            _fillHookMissing = true;
            _log("FILL_HOOK_NOT_OBSERVED", $"renderer={_rendererIdentity} atlas={AtlasFolder} reservation=disabled");
            return;
        }
        try
        {
            if (SpecResolver.Version != _candidateVersion)
            {
                _candidateVersion = SpecResolver.Version;
                _candidates = SpecResolver.CandidateNames(AtlasFolder);
                _layouts.Clear();
            }
            if (_candidates.Count == 0) return;
            if (_renderer != null)
            {
                if (_renderer.addNormals || _renderer.calculateTangents || _renderer.tintBlack)
                    throw new NotSupportedException("optional-vertex-stream-fail-closed");
            }
            else
            {
                MeshGenerator.Settings settings = Generator!.settings;
                if (settings.addNormals || settings.calculateTangents || settings.tintBlack)
                    throw new NotSupportedException("optional-vertex-stream-fail-closed");
            }
            if (instruction.hasActiveClipping)
            {
                _generatorPointer = Generator?.Pointer ?? IntPtr.Zero;
                _pendingClip = instruction;
                return;
            }

            Layout? layout = ResolveLayout(instruction);
            if (layout == null) return;
            var items = instruction.submeshInstructions.Items;
            var pending = new PendingFrame(instruction, layout);
            foreach ((int sm, SubmeshExtra extra) in layout.Extras)
            {
                SubmeshInstruction sub = items[sm];
                pending.Remember(sm, sub.rawVertexCount, sub.rawTriangleCount);
                sub.rawVertexCount = checked(sub.rawVertexCount + extra.Vertices);
                sub.rawTriangleCount = checked(sub.rawTriangleCount + extra.Indices);
                items[sm] = sub;   // boxed struct copy: store back
                if (items[sm].rawTriangleCount != sub.rawTriangleCount)
                {
                    pending.RollbackInstruction();
                    throw new InvalidOperationException("submesh-instruction-writeback-not-effective");
                }
            }
            instruction.rawVertexCount = checked(layout.NativeVertexCount + layout.ExtraVertices);
            _generatorPointer = Generator?.Pointer ?? IntPtr.Zero;
            _pending = pending;
            if (_framesWithTargets++ == 0)
            {
                _log("FIRST_RESERVATION", $"renderer={_rendererIdentity} atlas={AtlasFolder} targets={layout.Targets.Count} bandVertices={layout.ExtraVertices}");
                // Which spec each target is built from. Two renderers of the same atlas reserving different
                // band sizes (882 / 896 / 898 / 902 for the Maid on 2026-11-18) means they resolved different
                // specs for the same parts, and a band built from the wrong canvas draws as a stray sliver.
                foreach (Target target in layout.Targets)
                    _log("TARGET_SPEC", $"renderer={_rendererIdentity} {target.Spec.Describe()}");
            }
            // A renderer that loses targets it once had is drawing some parts with un-expanded geometry while
            // they sample expanded art: that is exactly what the fragmented Maid looked like. Say so, once.
            if (layout.Targets.Count > _mostTargets) _mostTargets = layout.Targets.Count;
            else if (layout.Targets.Count < _mostTargets && !_lostLogged)
            {
                _lostLogged = true;
                MelonLoader.MelonLogger.Warning($"[SkinForger][R4-12] TARGETS_LOST renderer={_rendererIdentity} atlas={AtlasFolder} " +
                    $"{_mostTargets} -> {layout.Targets.Count}; still expanded: {string.Join(",", layout.Targets.Select(t => t.Shape.Name))}");
            }
        }
        catch (Exception exception)
        {
            _rejected++;
            _log("FRAME_REJECTED_GENERATE", $"atlas={AtlasFolder} " + exception.GetBaseException().Message);
        }
    }

    // ---------------------------------------------------------------- insertion

    internal void OnBeforeFillVertexData(MeshGenerator generator)
    {
        SkeletonRendererInstruction? clip = _pendingClip;
        _pendingClip = null;
        if (clip != null && !_disposed) { RunClipped(generator, clip); return; }
        PendingFrame? pending = _pending;
        _pending = null;
        var transforms = _pendingTransforms;
        _pendingTransforms = null;
        if (_disposed) return;
        _fillCalls++;
        if (pending == null) { ApplyTransforms(generator, transforms, null); return; }
        try
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            string detail = Apply(generator, pending.Layout, _committed);
            ApplyTransforms(generator, transforms, pending.Layout);
            _applyTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
            _committed++;
            if (_committed <= 2 || _committed % 600 == 0)
            {
                double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                _log("FRAME_COMMITTED", $"renderer={_rendererIdentity} atlas={AtlasFolder} frames={_committed} rejected={_rejected} " +
                    $"avgGenerateMs={_generateTicks * f / Math.Max(1, _generateCalls):0.###} avgApplyMs={_applyTicks * f / _committed:0.###} " +
                    $"synthesizedTriangles={pending.Layout.Synthesized} {detail}");
            }
        }
        catch (Exception exception)
        {
            _rejected++;
            try
            {
                pending.RollbackInstruction();
                RestoreNativeBuffers(generator, pending.Layout);
            }
            catch (Exception restore) { _log("FRAME_RESTORE_FAILED", restore.GetBaseException().Message); }
            _log("FRAME_ROLLED_BACK", $"atlas={AtlasFolder} " + exception.GetBaseException().Message);
            ApplyTransforms(generator, transforms, null);
        }
    }

    private static string Apply(MeshGenerator generator, Layout layout, int committed)
    {
        int native = layout.NativeVertexCount;
        int final = checked(native + layout.ExtraVertices);
        var vertexList = generator.vertexBuffer;
        var uvList = generator.uvBuffer;
        var colorList = generator.colorBuffer;
        if (vertexList.Items.Length < final || uvList.Items.Length < final || colorList.Items.Length < final)
            generator.EnsureVertexCapacity(final, false, false, false);
        // R4-12: direct spans over the native buffers (Il2CppStructArray.AsSpan) instead of per-element interop calls.
        Span<Vector3> vertices = AsSpan(vertexList.Items);
        Span<Vector2> uvs = AsSpan(uvList.Items);
        Span<Color32> colors = AsSpan(colorList.Items);
        if (vertices.Length < final || uvs.Length < final || colors.Length < final)
            throw new InvalidOperationException("reserved-vertex-capacity-unavailable");

        foreach ((int sm, SubmeshExtra _) in layout.Extras)
            if (!layout.FinalTriangles.ContainsKey(sm)) layout.BuildTriangles(sm, generator.submeshes.Items[sm]);

        Vector2 min = generator.meshBoundsMin, max = generator.meshBoundsMax;
        double worstFold = double.PositiveInfinity;
        foreach (Target target in layout.Targets)
        {
            Shape shape = target.Shape;
            if ((committed < 3 || committed % 600 == 0) &&
                !string.Equals(Shape.Fingerprint(shape.Mesh), shape.FingerprintAtStart, StringComparison.Ordinal))
                throw new InvalidOperationException("source-mesh-attachment-changed " + shape.Name);

            int first = target.FirstVertex, n = shape.VertexCount;
            V2[] position = shape.PositionScratch;
            float z = vertices[first].z;
            for (int i = 0; i < n; i++)
            {
                Vector3 p = vertices[first + i];
                if (!float.IsFinite(p.x) || !float.IsFinite(p.y)) throw new InvalidOperationException("non-finite-native-vertex " + shape.Name);
                position[i] = new(p.x, p.y);
            }

            RingTopology topo = target.Topology;
            // UVs are constant for a target (its decision is keyed by the attachment's UVs and texture): compute once.
            if (target.NativeUv == null)
            {
                Affine atlas;
                if (target.AtlasOverride is Affine fromGameplay) atlas = fromGameplay;
                else
                {
                    var atlasUv = new V2[n];
                    for (int i = 0; i < n; i++) atlasUv[i] = new(uvs[first + i].x, uvs[first + i].y);
                    atlas = Affine.Fit(shape.RegionUv, atlasUv, out double residual);
                    if (residual > 2e-4) throw new InvalidOperationException($"atlas-uv-not-affine {shape.Name} residual={residual:G3}");
                }
                CanvasSpec spec = target.Spec.Canvas;
                var nativeUv = new Vector2[n];
                for (int i = 0; i < n; i++) { V2 u = atlas.Map(spec.ToCanvas(shape.RegionUv[i])); nativeUv[i].x = (float)u.X; nativeUv[i].y = (float)u.Y; }
                var outerUv = new Vector2[topo.Outer.Length];
                for (int q = 0; q < outerUv.Length; q++) { V2 u = atlas.Map(spec.ToCanvasClamped(topo.Outer[q])); outerUv[q].x = (float)u.X; outerUv[q].y = (float)u.Y; }
                target.OuterUv = outerUv;
                target.NativeUv = nativeUv;
            }
            target.NativeUv.AsSpan().CopyTo(uvs.Slice(first, n));

            V2[] outer = target.OuterScratch ??= new V2[topo.Outer.Length];
            V2[] residualScratch = target.ResidualScratch ??= new V2[topo.Boundary.Length];
            topo.OuterPositionsFast(shape.RegionUv, position, outer, residualScratch);
            Color32 color = colors[first];
            int at = native + target.OuterOffset;
            Vector2[] outerUvs = target.OuterUv!;
            for (int q = 0; q < outer.Length; q++)
            {
                float x = (float)outer[q].X, y = (float)outer[q].Y;
                if (!float.IsFinite(x) || !float.IsFinite(y)) throw new InvalidOperationException("non-finite-band-vertex " + shape.Name);
                ref Vector3 pos = ref vertices[at + q];
                pos.x = x; pos.y = y; pos.z = z;
                uvs[at + q] = outerUvs[q];
                colors[at + q] = color;
                if (x < min.x) min.x = x; if (y < min.y) min.y = y;
                if (x > max.x) max.x = x; if (y > max.y) max.y = y;
            }
            if (committed < 2 || committed % 600 == 0)
                worstFold = Math.Min(worstFold, topo.MinSignedAreaRatio(shape.RegionUv, v => position[v], outer));
        }
        vertexList.Count = final; uvList.Count = final; colorList.Count = final;
        foreach ((int sm, int[] triangles) in layout.FinalTriangles) WriteTriangles(generator.submeshes.Items[sm], triangles);
        generator.meshBoundsMin = min;
        generator.meshBoundsMax = max;

        return $"targets={layout.Targets.Count}[{string.Join(",", layout.Targets.Select(t => t.Shape.Name))}] nativeVertices={native} " +
            $"finalVertices={final} bandVertices={layout.ExtraVertices} worstBandAreaRatio={(double.IsInfinity(worstFold) ? "n/a" : worstFold.ToString("F3"))} " +
            "attachmentsUnchanged=True";
    }

    // ---------------------------------------------------------------- rigid part transforms (JSON offset / rotation)

    private List<(int First, Shape Shape, PartTransforms.Entry Entry)>? CollectTransforms(SkeletonRendererInstruction instruction)
    {
        Skeleton skeleton = (_renderer != null ? _renderer.skeleton : _graphic!.Skeleton) ?? throw new InvalidOperationException("skeleton-unavailable");
        var drawOrder = skeleton.drawOrder.Items;
        var submeshes = instruction.submeshInstructions;
        var items = submeshes.Items;
        List<(int, Shape, PartTransforms.Entry)>? list = null;
        int global = 0;
        for (int sm = 0; sm < submeshes.Count; sm++)
        {
            SubmeshInstruction sub = items[sm];
            bool runtime = false, checkedTexture = false;
            for (int draw = sub.startSlot; draw < sub.endSlot; draw++)
            {
                Slot slot = drawOrder[draw];
                if (!slot.bone.active) continue;
                Attachment? attachment = slot.attachment;
                if (attachment == null) continue;
                if (attachment.TryCast<RegionAttachment>() != null) { global += 4; continue; }
                MeshAttachment? mesh = attachment.TryCast<MeshAttachment>();
                if (mesh == null) continue;
                int count = mesh.worldVerticesLength / 2;
                if (PartTransforms.TryGet(mesh.Pointer.ToInt64(), out PartTransforms.Entry entry))
                {
                    if (!checkedTexture)
                    {
                        checkedTexture = true;
                        Texture? texture = _graphic != null ? (_graphic.mainTexture ?? sub.material?.mainTexture) : sub.material?.mainTexture;
                        Texture2D? t2d = texture?.TryCast<Texture2D>();
                        runtime = t2d != null && IsRuntimeAtlasFormat((int)t2d.format);   // Original mode: game atlas, untouched
                    }
                    if (runtime)
                    {
                        Shape shape = GetShape(mesh, attachment.Name);
                        if (shape.VertexCount == count) (list ??= new()).Add((global, shape, entry));
                    }
                }
                global += count;
            }
        }
        if (global != instruction.rawVertexCount) return null;   // layout not understood: leave the frame alone
        return list;
    }

    private void ApplyTransforms(MeshGenerator generator, List<(int First, Shape Shape, PartTransforms.Entry Entry)>? transforms, Layout? layout)
    {
        if (transforms == null || transforms.Count == 0) return;
        try
        {
            Span<Vector3> vertices = AsSpan(generator.vertexBuffer.Items);
            int count = generator.vertexBuffer.Count;
            Vector2 min = generator.meshBoundsMin, max = generator.meshBoundsMax;
            foreach ((int first, Shape shape, PartTransforms.Entry entry) in transforms)
            {
                int n = shape.VertexCount;
                if (first + n > count) continue;
                Span<Vector3> band = Span<Vector3>.Empty;
                if (layout != null)
                    foreach (Target target in layout.Targets)
                        if (target.FirstVertex == first && ReferenceEquals(target.Shape, shape))
                        {
                            int at = layout.NativeVertexCount + target.OuterOffset, len = target.Topology.Outer.Length;
                            if (at + len <= count) band = vertices.Slice(at, len);
                            break;
                        }
                PartTransforms.Apply(entry, shape.RegionUv, vertices.Slice(first, n), band, ref min, ref max);
            }
            generator.meshBoundsMin = min;
            generator.meshBoundsMax = max;
        }
        catch (Exception exception) { _log("PART_TRANSFORM_FAILED", $"atlas={AtlasFolder} " + exception.GetBaseException().Message); }
    }

    private static Span<T> AsSpan<T>(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<T> items) where T : unmanaged =>
        items is Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<T> structArray
            ? structArray.AsSpan()
            : throw new InvalidOperationException("buffer-is-not-a-struct-array " + typeof(T).Name);

    private static void WriteTriangles(ExposedList<int> submesh, int[] triangles)
    {
        if (submesh.Items.Length < triangles.Length) submesh.Resize(triangles.Length);
        Span<int> items = AsSpan(submesh.Items);
        triangles.AsSpan().CopyTo(items);
        items.Slice(triangles.Length).Clear();
        submesh.Count = triangles.Length;
    }

    private static void RestoreNativeBuffers(MeshGenerator generator, Layout layout)
    {
        int native = layout.NativeVertexCount;
        generator.vertexBuffer.Count = native;
        generator.uvBuffer.Count = native;
        generator.colorBuffer.Count = native;
        foreach ((int sm, int[] triangles) in layout.NativeTriangles) WriteTriangles(generator.submeshes.Items[sm], triangles);
    }

    // ---------------------------------------------------------------- clipping frames (0.13)

    /// <summary>SkeletonGraphic whose UpdateMesh took the clipping path (AddSubmesh, no BuildMeshWithArrays).</summary>
    internal void OnGraphicFill(MeshGenerator generator)
    {
        if (_disposed || _graphic == null) return;
        SkeletonRendererInstruction? instruction = _graphic.currentInstructions;
        if (instruction == null || !instruction.hasActiveClipping) return;
        _generatorPointer = generator.Pointer;
        RunClipped(generator, instruction);
    }

    private readonly Dictionary<(long Mesh, long UvKey), Target> _clipTargets = new();
    private int _clipCommitted;

    private void RunClipped(MeshGenerator generator, SkeletonRendererInstruction instruction)
    {
        _fillCalls++;
        try
        {
            if (SpecResolver.Version != _candidateVersion)
            {
                _candidateVersion = SpecResolver.Version;
                _candidates = SpecResolver.CandidateNames(AtlasFolder);
                _layouts.Clear();
                _clipTargets.Clear();
            }
            if (_candidates.Count == 0) return;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            string detail = ApplyClipped(generator, instruction);
            _applyTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
            if (detail.Length == 0) return;
            _committed++; _clipCommitted++; _framesWithTargets++;
            if (_clipCommitted <= 2 || _clipCommitted % 600 == 0)
                _log("FRAME_COMMITTED", $"renderer={_rendererIdentity} atlas={AtlasFolder} frames={_committed} clipFrames={_clipCommitted} rejected={_rejected} mode=clipping {detail}");
        }
        catch (Exception exception)
        {
            _rejected++;
            _log("FRAME_REJECTED_CLIP", $"atlas={AtlasFolder} " + exception.GetBaseException().Message);
        }
    }

    /// <summary>
    /// Clipping frames: Spine already wrote this frame's vertices/triangles (clipped attachments get new vertices, so
    /// offsets cannot be reserved). Targets outside any clip range are found by their exact attachment UVs (Spine copies
    /// mesh.uvs verbatim for unclipped attachments), their triangle range is validated in the fresh triangle buffer,
    /// and the band is appended after the last vertex with its triangles right after the target's own triangles.
    /// Targets inside a clip range are left native. Nothing is written until every accepted target validated.
    /// </summary>
    private string ApplyClipped(MeshGenerator generator, SkeletonRendererInstruction instruction)
    {
        Skeleton skeleton = (_renderer != null ? _renderer.skeleton : _graphic!.Skeleton) ?? throw new InvalidOperationException("skeleton-unavailable");
        var drawOrder = skeleton.drawOrder.Items;
        var submeshItems = instruction.submeshInstructions.Items;
        int submeshCount = instruction.submeshInstructions.Count;
        var wanted = new List<Target>();
        int clippedTargets = 0;
        for (int sm = 0; sm < submeshCount; sm++)
        {
            SubmeshInstruction sub = submeshItems[sm];
            bool clipping = false;
            IntPtr clipEnd = IntPtr.Zero;
            for (int draw = sub.startSlot; draw < sub.endSlot; draw++)
            {
                Slot slot = drawOrder[draw];
                Attachment? attachment = slot.bone.active ? slot.attachment : null;
                ClippingAttachment? clip = attachment?.TryCast<ClippingAttachment>();
                if (clip != null) { clipping = true; clipEnd = clip.endSlot?.Pointer ?? IntPtr.Zero; continue; }
                MeshAttachment? mesh = attachment?.TryCast<MeshAttachment>();
                if (mesh != null && _candidates.Contains(attachment!.Name))
                {
                    if (clipping) clippedTargets++;
                    else
                    {
                        Target? fresh = TryTarget(mesh, attachment.Name, sub.material, sm, -1);
                        if (fresh != null)
                        {
                            var key = (mesh.Pointer.ToInt64(), fresh.UvKey);
                            if (!_clipTargets.TryGetValue(key, out Target? cached) || !ReferenceEquals(cached.Topology, fresh.Topology))
                                _clipTargets[key] = cached = fresh;
                            wanted.Add(cached);
                        }
                    }
                }
                if (clipping && slot.data.Pointer == clipEnd) clipping = false;
            }
        }
        if (wanted.Count == 0) return string.Empty;

        int native = generator.vertexBuffer.Count;
        Span<Vector2> nativeUvs = AsSpan(generator.uvBuffer.Items);
        // locate + validate
        var accepted = new List<(Target T, int First, int TriangleAt, int TriangleCount)>();
        int cursor = 0, missing = 0;
        foreach (Target t in wanted)
        {
            float[] uv = t.Shape.Mesh.uvs.ToArray();
            int n = t.Shape.VertexCount;
            if (uv.Length != n * 2) { missing++; continue; }
            int found = -1;
            for (int i = cursor; i + n <= native; i++)
            {
                if (nativeUvs[i].x != uv[0] || nativeUvs[i].y != uv[1]) continue;
                if (nativeUvs[i + n - 1].x != uv[2 * n - 2] || nativeUvs[i + n - 1].y != uv[2 * n - 1]) continue;
                int mid = n / 2;
                if (nativeUvs[i + mid].x != uv[2 * mid] || nativeUvs[i + mid].y != uv[2 * mid + 1]) continue;
                found = i;
                break;
            }
            if (found < 0) { missing++; continue; }
            cursor = found + n;
            ExposedList<int> triangles = generator.submeshes.Items[t.Submesh];
            Span<int> tri = AsSpan(triangles.Items);
            int count = Math.Min(triangles.Count, tri.Length), firstTriangle = -1, touching = 0;
            bool bad = false;
            for (int i = 0; i + 2 < count; i += 3)
            {
                bool a = tri[i] >= found && tri[i] < found + n, b = tri[i + 1] >= found && tri[i + 1] < found + n, c = tri[i + 2] >= found && tri[i + 2] < found + n;
                if (!(a || b || c)) continue;
                if (!(a && b && c)) { bad = true; break; }
                if (touching == 0) firstTriangle = i;
                else if (i != firstTriangle + 3 * touching) { bad = true; break; }
                touching++;
            }
            if (bad || touching * 3 != t.Shape.Triangles.Length) { missing++; continue; }
            accepted.Add((t, found, firstTriangle + 3 * touching, count));
        }
        if (accepted.Count == 0) return string.Empty;

        int extra = accepted.Sum(a => a.T.Topology.Outer.Length);
        int final = checked(native + extra);
        var vertexList = generator.vertexBuffer; var uvList = generator.uvBuffer; var colorList = generator.colorBuffer;
        if (vertexList.Items.Length < final || uvList.Items.Length < final || colorList.Items.Length < final)
            generator.EnsureVertexCapacity(final, false, false, false);
        Span<Vector3> vertices = AsSpan(vertexList.Items);
        Span<Vector2> uvs = AsSpan(uvList.Items);
        Span<Color32> colors = AsSpan(colorList.Items);
        if (vertices.Length < final || uvs.Length < final || colors.Length < final) throw new InvalidOperationException("clip-vertex-capacity-unavailable");

        Vector2 min = generator.meshBoundsMin, max = generator.meshBoundsMax;
        var insertions = new Dictionary<int, List<(int At, int[] Band)>>();
        int bandAt = native;
        foreach ((Target target, int first, int triangleAt, int _) in accepted)
        {
            Shape shape = target.Shape;
            RingTopology topo = target.Topology;
            int n = shape.VertexCount;
            V2[] position = shape.PositionScratch;
            float z = vertices[first].z;
            for (int i = 0; i < n; i++)
            {
                Vector3 p = vertices[first + i];
                if (!float.IsFinite(p.x) || !float.IsFinite(p.y)) throw new InvalidOperationException("non-finite-native-vertex " + shape.Name);
                position[i] = new(p.x, p.y);
            }
            if (target.NativeUv == null)
            {
                Affine atlas;
                if (target.AtlasOverride is Affine fromGameplay) atlas = fromGameplay;
                else
                {
                    var atlasUv = new V2[n];
                    for (int i = 0; i < n; i++) atlasUv[i] = new(uvs[first + i].x, uvs[first + i].y);
                    atlas = Affine.Fit(shape.RegionUv, atlasUv, out double residual);
                    if (residual > 2e-4) throw new InvalidOperationException($"atlas-uv-not-affine {shape.Name} residual={residual:G3}");
                }
                CanvasSpec spec = target.Spec.Canvas;
                var nativeUv = new Vector2[n];
                for (int i = 0; i < n; i++) { V2 u = atlas.Map(spec.ToCanvas(shape.RegionUv[i])); nativeUv[i].x = (float)u.X; nativeUv[i].y = (float)u.Y; }
                var outerUv = new Vector2[topo.Outer.Length];
                for (int q = 0; q < outerUv.Length; q++) { V2 u = atlas.Map(spec.ToCanvasClamped(topo.Outer[q])); outerUv[q].x = (float)u.X; outerUv[q].y = (float)u.Y; }
                target.OuterUv = outerUv;
                target.NativeUv = nativeUv;
            }
            target.NativeUv.AsSpan().CopyTo(uvs.Slice(first, n));
            V2[] outer = target.OuterScratch ??= new V2[topo.Outer.Length];
            V2[] residualScratch = target.ResidualScratch ??= new V2[topo.Boundary.Length];
            topo.OuterPositionsFast(shape.RegionUv, position, outer, residualScratch);
            Color32 color = colors[first];
            Vector2[] outerUvs = target.OuterUv!;
            for (int q = 0; q < outer.Length; q++)
            {
                float x = (float)outer[q].X, y = (float)outer[q].Y;
                if (!float.IsFinite(x) || !float.IsFinite(y)) throw new InvalidOperationException("non-finite-band-vertex " + shape.Name);
                ref Vector3 pos = ref vertices[bandAt + q];
                pos.x = x; pos.y = y; pos.z = z;
                uvs[bandAt + q] = outerUvs[q];
                colors[bandAt + q] = color;
                if (x < min.x) min.x = x; if (y < min.y) min.y = y;
                if (x > max.x) max.x = x; if (y > max.y) max.y = y;
            }
            int baseIndex = bandAt;
            int[] band = topo.Triangles.Select(v => v < n ? first + v : baseIndex + (v - n)).ToArray();
            if (!insertions.TryGetValue(target.Submesh, out var list)) insertions[target.Submesh] = list = new();
            list.Add((triangleAt, band));
            bandAt += outer.Length;
        }
        vertexList.Count = final; uvList.Count = final; colorList.Count = final;
        foreach ((int sm, var list) in insertions)
        {
            ExposedList<int> triangles = generator.submeshes.Items[sm];
            int count = triangles.Count;
            int[] current = new int[count];
            AsSpan(triangles.Items).Slice(0, count).CopyTo(current);
            var merged = new List<int>(count + list.Sum(x => x.Band.Length));
            int c = 0;
            foreach ((int at, int[] band) in list.OrderBy(x => x.At))
            {
                for (; c < at; c++) merged.Add(current[c]);
                merged.AddRange(band);
            }
            for (; c < count; c++) merged.Add(current[c]);
            WriteTriangles(triangles, merged.ToArray());
        }
        generator.meshBoundsMin = min;
        generator.meshBoundsMax = max;
        return $"targets={accepted.Count}[{string.Join(",", accepted.Select(a => a.T.Shape.Name))}] insideClip={clippedTargets} notLocated={missing} " +
               $"nativeVertices={native} finalVertices={final} attachmentsUnchanged=True";
    }

    // ---------------------------------------------------------------- layout

    private Layout? ResolveLayout(SkeletonRendererInstruction instruction)
    {
        Skeleton skeleton = (_renderer != null ? _renderer.skeleton : _graphic!.Skeleton) ?? throw new InvalidOperationException("skeleton-unavailable");
        var drawOrder = skeleton.drawOrder.Items;
        var submeshes = instruction.submeshInstructions;
        int submeshCount = submeshes.Count;
        var submeshItems = submeshes.Items;

        long signature = 1469598103934665603L;
        int globalVertex = 0;
        var targets = new List<Target>();
        var triangleCounts = new Dictionary<int, int>();
        var drawn = new List<(int Submesh, int First, MeshAttachment? Mesh)>();
        unchecked
        {
            for (int sm = 0; sm < submeshCount; sm++)
            {
                SubmeshInstruction sub = submeshItems[sm];
                int start = sub.startSlot, end = sub.endSlot, rawTriangles = sub.rawTriangleCount, counted = 0;
                triangleCounts[sm] = rawTriangles;
                signature = (signature ^ start) * HashPrime;
                signature = (signature ^ end) * HashPrime;
                signature = (signature ^ rawTriangles) * HashPrime;
                Material? material = null;
                for (int draw = start; draw < end; draw++)
                {
                    Slot slot = drawOrder[draw];
                    if (!slot.bone.active) continue;
                    Attachment? attachment = slot.attachment;
                    if (attachment == null) continue;
                    int vertexCount, triangleCount;
                    MeshAttachment? mesh = null;
                    if (attachment.TryCast<RegionAttachment>() != null) { vertexCount = 4; triangleCount = 6; }
                    else
                    {
                        mesh = attachment.TryCast<MeshAttachment>();
                        if (mesh == null) continue;
                        vertexCount = mesh.worldVerticesLength / 2;
                        triangleCount = mesh.triangles.Length;
                    }
                    signature = (signature ^ attachment.Pointer.ToInt64()) * HashPrime;
                    if (mesh != null)
                    {
                        TopologySurvey.Observe(mesh, _log);
                        string name = attachment.Name;
                        if (_candidates.Contains(name))
                        {
                            material ??= sub.material;
                            Target? target = TryTarget(mesh, name, material, sm, globalVertex);
                            if (target != null)
                            {
                                targets.Add(target);
                                signature = (signature ^ target.Spec.GetHashCode()) * HashPrime;
                                signature = (signature ^ 0x5A5A) * HashPrime;
                            }
                        }
                    }
                    drawn.Add((sm, globalVertex, mesh));
                    globalVertex += vertexCount;
                    counted += triangleCount;
                }
                if (counted != rawTriangles)
                    throw new InvalidOperationException($"native-triangle-layout-mismatch submesh={sm} counted={counted} raw={rawTriangles}");
            }
            signature = (signature ^ globalVertex) * HashPrime;
        }
        if (globalVertex != instruction.rawVertexCount)
            throw new InvalidOperationException($"native-vertex-layout-mismatch counted={globalVertex} raw={instruction.rawVertexCount}");
        if (targets.Count == 0) return null;

        if (_layouts.TryGetValue(signature, out Layout? cached) && cached.Matches(globalVertex, targets)) return cached;
        if (_layouts.Count >= 64) _layouts.Clear();
        var layout = new Layout(globalVertex, targets, triangleCounts) { Drawn = drawn };
        _layouts[signature] = layout;
        return layout;
    }

    /// <summary>Attachments SkinForger Main applied a PNG to in gameplay (not Preview); their UVs are verified correct.</summary>
    internal static readonly HashSet<long> GameplayAttachments = new();

    /// <summary>
    /// How long a "this part is not an expansion target" answer is believed, in frames (2026-11-18).
    ///
    /// A YES is self-verifying: the measured region matched a spec. A NO is not — it only means the region
    /// did not match AT THAT MOMENT. While the atlas is being rebuilt the regions pass through intermediate
    /// states, so a NO taken mid-rebuild is about a half-built atlas. The cache was then only re-checked when
    /// the texture id or the UV fingerprint changed again, which it does not once the rebuild settles: the
    /// part stayed un-expanded for the life of the renderer while drawing from the expanded art, which is
    /// what the Maid's character-select picture looked like (SF日志, Maid 2026-11-18: a renderer that lived
    /// through a rebuild kept 16 of its 25 targets).
    ///
    /// So a NO is re-asked twice a second. A YES is kept as before.
    /// </summary>
    private const int NegativeRetryFrames = 30;

    private sealed record Decision(long UvPointer, float Uv0, float Uv1, float UvLast, int TextureId, int TruthVersion, int SpecVersion,
        TargetSpec? Spec, RingTopology? Topology, Affine? Override)
    {
        /// <summary>Frame at which a negative answer stops being believed. See NegativeRetryFrames.</summary>
        internal int AskAgainFrame { get; init; }
    }
    private readonly Dictionary<long, Decision> _decisions = new();

    /// <summary>
    /// Active target when the mesh is drawn from a SkinForger runtime atlas (uncompressed; the game's own atlases are
    /// compressed, so Original mode is never touched) and Spine maps its region at the size of one of its PNGs
    /// (expanded canvas, or the original size = full canvas). Preview attachments reuse the gameplay mapping.
    /// </summary>
    private Target? TryTarget(MeshAttachment mesh, string name, Material? material, int submesh, int firstVertex)
    {
        long pointer = mesh.Pointer.ToInt64();
        // SkeletonGraphic: Main puts the replacement atlas on overrideTexture; the material keeps the game atlas.
        Texture? texture = _graphic != null ? (_graphic.mainTexture ?? material?.mainTexture) : material?.mainTexture;
        if (texture == null) return null;
        var uvArray = mesh.uvs;
        if (uvArray == null || uvArray.Length < 2) return null;
        int textureId = texture.GetInstanceID();
        long uvPointer = uvArray.Pointer.ToInt64();
        float uv0 = uvArray[0], uv1 = uvArray[1], uvLast = uvArray[uvArray.Length - 1];

        bool usable = _decisions.TryGetValue(pointer, out Decision? d) && d.UvPointer == uvPointer && d.Uv0 == uv0 && d.Uv1 == uv1 &&
                      d.UvLast == uvLast && d.TextureId == textureId && d.TruthVersion == SpecResolver.TruthVersion &&
                      d.SpecVersion == SpecResolver.Version;
        bool negative = usable && (d!.Spec == null || d.Topology == null);
        if (usable && negative && UnityEngine.Time.frameCount >= d!.AskAgainFrame) usable = false;   // see NegativeRetryFrames
        if (usable)
            return d!.Spec == null || d.Topology == null ? null : new Target(GetShape(mesh, name), d.Spec, d.Topology, submesh, firstVertex)
                { AtlasOverride = d.Override, UvKey = UvKeyOf(uvPointer, uv0, uv1, uvLast, textureId) };

        Decision decision = Evaluate(mesh, name, texture, textureId, uvPointer, uv0, uv1, uvLast);
        if (decision.Spec == null || decision.Topology == null)
            decision = decision with { AskAgainFrame = UnityEngine.Time.frameCount + NegativeRetryFrames };
        else if (negative)
            _log("TARGET_RECOVERED", $"renderer={_rendererIdentity} atlas={AtlasFolder} attachment={name} " +
                                     "(it was not a target while the atlas was being rebuilt)");
        _decisions[pointer] = decision;
        return decision.Spec == null || decision.Topology == null ? null
            : new Target(GetShape(mesh, name), decision.Spec, decision.Topology, submesh, firstVertex)
                { AtlasOverride = decision.Override, UvKey = UvKeyOf(uvPointer, uv0, uv1, uvLast, textureId) };
    }

    /// <summary>
    /// Shapes are keyed by attachment + array addresses: Preview clones are destroyed and recreated and a new
    /// attachment may reuse an old address, so the address alone could return stale regionUVs/triangles.
    /// </summary>
    private static Shape GetShape(MeshAttachment mesh, string name)
    {
        long pointer = mesh.Pointer.ToInt64();
        long uvs = mesh.regionUVs?.Pointer.ToInt64() ?? 0, tris = mesh.triangles?.Pointer.ToInt64() ?? 0;
        lock (Shapes)
        {
            if (!Shapes.TryGetValue(pointer, out Shape? existing) || existing.RegionUvArray != uvs || existing.TriangleArray != tris ||
                !string.Equals(existing.Name, name, StringComparison.Ordinal))
            {
                existing = Shape.Create(mesh, name);
                existing.RegionUvArray = uvs;
                existing.TriangleArray = tris;
                Shapes[pointer] = existing;
            }
            return existing;
        }
    }

    private Decision Evaluate(MeshAttachment mesh, string name, Texture texture, int textureId, long uvPointer, float uv0, float uv1, float uvLast)
    {
        int truthVersion = SpecResolver.TruthVersion, specVersion = SpecResolver.Version;
        Decision None(Shape s, string why, double w = 0, double h = 0)
        {
            Decide(s, null, why, w, h);
            return new Decision(uvPointer, uv0, uv1, uvLast, textureId, truthVersion, specVersion, null, null, null);
        }

        Shape shape = GetShape(mesh, name);
        Texture2D? t2d = texture.TryCast<Texture2D>();
        int format = t2d == null ? -1 : (int)t2d.format;
        if (!IsRuntimeAtlasFormat(format)) return None(shape, "game-atlas format=" + format);

        float[] uv = mesh.uvs.ToArray();
        if (uv.Length != shape.RegionUv.Length * 2) return None(shape, "uv-length-mismatch");
        var atlasUv = new V2[shape.VertexCount];
        for (int i = 0; i < atlasUv.Length; i++) atlasUv[i] = new(uv[2 * i], uv[2 * i + 1]);
        Affine own = Affine.Fit(shape.RegionUv, atlasUv, out _);

        long pointer = mesh.Pointer.ToInt64();
        bool gameplay;
        lock (GameplayAttachments) gameplay = GameplayAttachments.Contains(pointer);
        Affine map = own;
        Affine? overrideMap = null;
        string source = gameplay ? "gameplay" : "own-uv";
        if (!gameplay)
        {
            SpecResolver.UvTruth? truth = SpecResolver.Truth(AtlasFolder, name);
            if (truth != null && truth.TextureWidth == texture.width && truth.TextureHeight == texture.height)
            {
                map = truth.Map;
                overrideMap = truth.Map;
                source = truth.TextureId == textureId ? "gameplay-truth(same-texture)" : "gameplay-truth(same-size-texture)";
            }
            else if (PlacementCalibration.TryMap(pointer, texture.width, texture.height, out Affine placed, out string how))
            {
                // Preview atlas built by its own pass: Main's placement rect in THIS atlas, with the orientation
                // convention calibrated on gameplay meshes whose own UVs are verified (0.13.1).
                map = placed;
                overrideMap = placed;
                source = "placement-calibrated(" + how + ")";
            }
        }
        else PlacementCalibration.Calibrate(pointer, own, texture.width, texture.height, _log);
        (double w, double h) = map.TexelExtent(texture.width, texture.height);
        TargetSpec? spec = SpecResolver.Match(AtlasFolder, name, w, h, out string why);
        if (spec == null) return None(shape, why + " uv=" + source, w, h);
        if (gameplay) SpecResolver.RecordTruth(AtlasFolder, name, new SpecResolver.UvTruth(textureId, texture.width, texture.height, own));

        string topoKey = shape.Key + "|" + spec.Canvas;
        RingTopology? topology;
        lock (Topologies)
        {
            if (!Topologies.TryGetValue(topoKey, out topology))
            {
                try { topology = RingTopology.Build(shape.RawRegionUv, shape.Triangles, spec.Canvas); }
                catch (Exception exception) { Decide(shape, spec, "topology-failed " + exception.Message, w, h); topology = null; }
                if (topology != null) Topologies[topoKey] = topology;
            }
        }
        if (topology == null) return new Decision(uvPointer, uv0, uv1, uvLast, textureId, truthVersion, specVersion, null, null, null);
        Decide(shape, spec, $"active uv={source} band={topology.Outer.Length}v/{topology.Triangles.Length / 3}t " +
            $"islands={topology.Islands} holes={topology.FilledHoles}", w, h);
        return new Decision(uvPointer, uv0, uv1, uvLast, textureId, truthVersion, specVersion, spec, topology, overrideMap);
    }

    /// <summary>SkinForger builds its atlases on the CPU (uncompressed); the game's imported atlases are compressed.</summary>
    private static bool IsRuntimeAtlasFormat(int format) => format is 3 or 4 or 5 or 7 or 13 or 14 or 17 or 20;

    private Target? Decide(Shape shape, TargetSpec? spec, string decision, double w, double h)
    {
        string key = $"{AtlasFolder}|{shape.Name}|{shape.Mesh.Pointer.ToInt64()}|{Math.Round(w)}x{Math.Round(h)}|{decision}";
        bool first;
        lock (LoggedDecisions) first = LoggedDecisions.Add(key);
        if (first)
            _log("TARGET_DECISION", $"renderer={_rendererIdentity} atlas={AtlasFolder} attachment={shape.Name} measuredTexels={w:0.#}x{h:0.#} " +
                $"decision={decision}{(spec != null ? " spec=" + spec.Describe() : "")}");
        return null;
    }

    private void OnRendererRebuilt(SkeletonRenderer renderer)
    {
        try { _pending?.RollbackInstruction(); } catch { /* instruction may be gone after rebuild */ }
        _pending = null;
        _layouts.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _pending?.RollbackInstruction(); } catch { /* best effort */ }
        _pending = null;
        bool complete = false;
        for (int attempt = 1; attempt <= 3 && !complete; attempt++) complete = TryCleanup();
        if (complete) lock (RegistryGate) RegisteredRenderers.Remove(_rendererIdentity);
        _log(complete ? "SESSION_DISPOSED" : "SESSION_CLEANUP_INCOMPLETE",
            $"renderer={_rendererIdentity} atlas={AtlasFolder} committed={_committed} rejected={_rejected} framesWithTargets={_framesWithTargets} " +
            $"generateCalls={_generateCalls} fillCalls={_fillCalls}");
    }

    private bool TryCleanup()
    {
        if (_renderer == null) return true;   // graphic sessions register nothing on the graphic
        if (_instructionRegistered)
        {
            try { _renderer.remove_GenerateMeshOverride(_instructionCallback); _instructionRegistered = false; }
            catch (Exception exception) { _log("CLEANUP_RETRY", "GenerateMeshOverride:" + exception.GetBaseException().Message); }
        }
        if (_rebuildRegistered)
        {
            try { _renderer.remove_OnRebuild(_rebuildCallback); _rebuildRegistered = false; }
            catch (Exception exception) { _log("CLEANUP_RETRY", "OnRebuild:" + exception.GetBaseException().Message); }
        }
        bool complete = CleanupComplete;
        if (complete)
        {
            try { _renderer.disableRenderingOnOverride = _oldDisableRenderingOnOverride; }
            catch (Exception exception) { _log("CLEANUP_RETRY", "disableRenderingOnOverride:" + exception.GetBaseException().Message); return false; }
        }
        return complete;
    }

    // ---------------------------------------------------------------- helpers

    internal readonly struct Affine
    {
        private readonly double _m00, _m01, _m10, _m11, _tx, _ty;
        private Affine(double m00, double m01, double m10, double m11, double tx, double ty)
        { _m00 = m00; _m01 = m01; _m10 = m10; _m11 = m11; _tx = tx; _ty = ty; }
        internal V2 Map(V2 r) => new(_m00 * r.X + _m01 * r.Y + _tx, _m10 * r.X + _m11 * r.Y + _ty);

        internal bool Same(Affine o) =>
            Math.Abs(_m00 - o._m00) < 1e-9 && Math.Abs(_m01 - o._m01) < 1e-9 && Math.Abs(_m10 - o._m10) < 1e-9 &&
            Math.Abs(_m11 - o._m11) < 1e-9 && Math.Abs(_tx - o._tx) < 1e-9 && Math.Abs(_ty - o._ty) < 1e-9;

        /// <summary>Texel size of the r unit square (= size of the image Spine maps this region to).</summary>
        internal (double Width, double Height) TexelExtent(int textureWidth, int textureHeight) =>
            (Math.Sqrt(Math.Pow(_m00 * textureWidth, 2) + Math.Pow(_m10 * textureHeight, 2)),
             Math.Sqrt(Math.Pow(_m01 * textureWidth, 2) + Math.Pow(_m11 * textureHeight, 2)));

        internal static Affine Fit(V2[] from, V2[] to, out double maxResidual)
        {
            int n = from.Length;
            double fx = 0, fy = 0, tx = 0, ty = 0;
            for (int i = 0; i < n; i++) { fx += from[i].X; fy += from[i].Y; tx += to[i].X; ty += to[i].Y; }
            fx /= n; fy /= n; tx /= n; ty /= n;
            double sxx = 0, sxy = 0, syy = 0, a = 0, b = 0, c = 0, d = 0;
            for (int i = 0; i < n; i++)
            {
                double rx = from[i].X - fx, ry = from[i].Y - fy, ux = to[i].X - tx, uy = to[i].Y - ty;
                sxx += rx * rx; sxy += rx * ry; syy += ry * ry;
                a += ux * rx; b += ux * ry; c += uy * rx; d += uy * ry;
            }
            double det = sxx * syy - sxy * sxy;
            if (!(Math.Abs(det) > 1e-18)) throw new InvalidOperationException("atlas-affine-degenerate");
            double i00 = syy / det, i01 = -sxy / det, i11 = sxx / det;
            double m00 = a * i00 + b * i01, m01 = a * i01 + b * i11, m10 = c * i00 + d * i01, m11 = c * i01 + d * i11;
            var result = new Affine(m00, m01, m10, m11, tx - m00 * fx - m01 * fy, ty - m10 * fx - m11 * fy);
            maxResidual = 0;
            for (int i = 0; i < n; i++)
            {
                V2 e = result.Map(from[i]) - to[i];
                maxResidual = Math.Max(maxResidual, Math.Max(Math.Abs(e.X), Math.Abs(e.Y)));
            }
            return result;
        }
    }

    /// <summary>Static data of one MeshAttachment (read once, never written).</summary>
    internal sealed class Shape
    {
        internal MeshAttachment Mesh { get; }
        internal string Name { get; }
        internal int VertexCount { get; }
        internal float[] RawRegionUv { get; }
        internal V2[] RegionUv { get; }
        internal int[] Triangles { get; }
        internal string FingerprintAtStart { get; }
        internal long RegionUvArray { get; set; }
        internal long TriangleArray { get; set; }
        internal string Key => $"{Mesh.Pointer.ToInt64()}|{RegionUvArray}|{TriangleArray}";
        /// <summary>Per-frame scratch for current vertex positions (main thread only).</summary>
        internal V2[] PositionScratch => _positionScratch ??= new V2[VertexCount];
        private V2[]? _positionScratch;

        private Shape(MeshAttachment mesh, string name, float[] raw, int[] triangles)
        {
            Mesh = mesh; Name = name; RawRegionUv = raw; Triangles = triangles; VertexCount = raw.Length / 2;
            RegionUv = new V2[VertexCount];
            for (int i = 0; i < VertexCount; i++) RegionUv[i] = new(raw[2 * i], raw[2 * i + 1]);
            FingerprintAtStart = Fingerprint(mesh);
        }

        internal static Shape Create(MeshAttachment mesh, string name)
        {
            float[] raw = mesh.regionUVs.ToArray();
            int[] triangles = mesh.triangles.ToArray().Select(t => (int)t).ToArray();
            if (raw.Length != mesh.worldVerticesLength) throw new InvalidOperationException($"regionuv-length-mismatch {name}");
            return new Shape(mesh, name, raw, triangles);
        }

        internal static string Fingerprint(MeshAttachment mesh)
        {
            unchecked
            {
                long h = 1469598103934665603L;
                foreach (float f in mesh.regionUVs.ToArray()) h = (h ^ BitConverter.SingleToInt32Bits(f)) * HashPrime;
                foreach (float f in mesh.vertices.ToArray()) h = (h ^ BitConverter.SingleToInt32Bits(f)) * HashPrime;
                foreach (int t in mesh.triangles.ToArray()) h = (h ^ t) * HashPrime;
                var bones = mesh.bones;
                if (bones != null) foreach (int b in bones.ToArray()) h = (h ^ b) * HashPrime;
                return h.ToString("X16") + "|" + mesh.Pointer.ToInt64();
            }
        }
    }

    private sealed record Target(Shape Shape, TargetSpec Spec, RingTopology Topology, int Submesh, int FirstVertex)
    {
        internal int OuterOffset { get; set; }
        /// <summary>Preview: gameplay-verified r -> atlas UV mapping used instead of this attachment's own UVs.</summary>
        internal Affine? AtlasOverride { get; init; }
        /// <summary>Identity of the attachment UVs + texture this target was decided for (layout cache key).</summary>
        internal long UvKey { get; init; }
        internal Vector2[]? NativeUv { get; set; }
        internal Vector2[]? OuterUv { get; set; }
        internal V2[]? OuterScratch { get; set; }
        internal V2[]? ResidualScratch { get; set; }
    }

    private static long UvKeyOf(long uvPointer, float uv0, float uv1, float uvLast, int textureId)
    {
        unchecked
        {
            long h = 1469598103934665603L;
            h = (h ^ uvPointer) * HashPrime;
            h = (h ^ BitConverter.SingleToInt32Bits(uv0)) * HashPrime;
            h = (h ^ BitConverter.SingleToInt32Bits(uv1)) * HashPrime;
            h = (h ^ BitConverter.SingleToInt32Bits(uvLast)) * HashPrime;
            return (h ^ textureId) * HashPrime;
        }
    }

    private readonly record struct SubmeshExtra(int Vertices, int Indices);

    private sealed class Layout
    {
        internal int NativeVertexCount { get; }
        internal List<Target> Targets { get; }
        internal int ExtraVertices { get; }
        internal Dictionary<int, SubmeshExtra> Extras { get; } = new();
        internal Dictionary<int, int[]> NativeTriangles { get; } = new();
        internal Dictionary<int, int[]> FinalTriangles { get; } = new();
        private readonly Dictionary<int, int> _nativeTriangleCounts;

        internal Layout(int nativeVertexCount, List<Target> targets, Dictionary<int, int> nativeTriangleCounts)
        {
            NativeVertexCount = nativeVertexCount;
            Targets = targets;
            _nativeTriangleCounts = nativeTriangleCounts;
            int offset = 0;
            foreach (Target t in targets)
            {
                t.OuterOffset = offset;
                offset += t.Topology.Outer.Length;
                Extras.TryGetValue(t.Submesh, out SubmeshExtra e);
                Extras[t.Submesh] = new SubmeshExtra(e.Vertices + t.Topology.Outer.Length, e.Indices + t.Topology.Triangles.Length);
            }
            ExtraVertices = offset;
        }

        internal bool Matches(int nativeVertexCount, List<Target> targets) =>
            nativeVertexCount == NativeVertexCount && targets.Count == Targets.Count &&
            targets.Zip(Targets).All(p => ReferenceEquals(p.First.Topology, p.Second.Topology) && p.First.FirstVertex == p.Second.FirstVertex &&
                p.First.Submesh == p.Second.Submesh && p.First.Shape.Mesh.Pointer == p.Second.Shape.Mesh.Pointer &&
                p.First.UvKey == p.Second.UvKey &&
                p.First.AtlasOverride.HasValue == p.Second.AtlasOverride.HasValue &&
                (!p.First.AtlasOverride.HasValue || p.First.AtlasOverride.Value.Same(p.Second.AtlasOverride!.Value)));

        /// <summary>Attachments drawn by this layout in draw order (submesh, first vertex, mesh or null for a region quad).</summary>
        internal List<(int Submesh, int First, MeshAttachment? Mesh)> Drawn { get; init; } = new();

        /// <summary>
        /// Native triangles of a submesh: read from Spine's buffer when it was refilled this frame; otherwise (Spine skips
        /// the triangle rebuild when it considers the topology unchanged â€” R4-10 saw 2 rolled-back frames) rebuilt with
        /// Spine 3.8's own order: region quad (0,2,1)(2,3,1), mesh triangles + first vertex, in draw order.
        /// </summary>
        private int[]? ReadNative(int sm, ExposedList<int> submesh, int count)
        {
            var items = submesh.Items;
            if (items.Length < count) return null;
            var native = new int[count];
            for (int i = 0; i < count; i++)
            {
                int index = items[i];
                if (index < 0 || index >= NativeVertexCount) return null;
                native[i] = index;
            }
            return native;
        }

        private int[] SynthesizeNative(int sm, int count)
        {
            var result = new List<int>(count);
            foreach ((int s, int first, MeshAttachment? mesh) in Drawn)
            {
                if (s != sm) continue;
                if (mesh == null) result.AddRange(new[] { first, first + 2, first + 1, first + 2, first + 3, first + 1 });
                else foreach (int t in mesh.triangles.ToArray()) result.Add(first + t);
            }
            if (result.Count != count) throw new InvalidOperationException($"synthesized-native-triangles-mismatch submesh={sm} {result.Count}/{count}");
            return result.ToArray();
        }

        internal bool Synthesized { get; private set; }

        /// <summary>Native triangles of the submesh plus each target's band right after the target's own triangles.</summary>
        internal void BuildTriangles(int sm, ExposedList<int> submesh)
        {
            int count = _nativeTriangleCounts[sm];
            if (count % 3 != 0) throw new InvalidOperationException($"native-triangle-count-invalid submesh={sm}");
            int[]? fromBuffer = ReadNative(sm, submesh, count);
            int[] native = fromBuffer ?? SynthesizeNative(sm, count);
            if (fromBuffer == null) Synthesized = true;
            var insertions = new List<(int At, int[] Band)>();
            foreach (Target t in Targets.Where(t => t.Submesh == sm))
            {
                int lo = t.FirstVertex, hi = t.FirstVertex + t.Shape.VertexCount, firstTriangle = -1, touching = 0;
                for (int i = 0; i < native.Length; i += 3)
                {
                    bool a = native[i] >= lo && native[i] < hi, b = native[i + 1] >= lo && native[i + 1] < hi, c = native[i + 2] >= lo && native[i + 2] < hi;
                    if (!(a || b || c)) continue;
                    if (!(a && b && c)) throw new InvalidOperationException("target-triangle-shares-foreign-vertex " + t.Shape.Name);
                    if (touching == 0) firstTriangle = i;
                    else if (i != firstTriangle + 3 * touching) throw new InvalidOperationException("target-triangles-not-contiguous " + t.Shape.Name);
                    touching++;
                }
                if (touching * 3 != t.Shape.Triangles.Length)
                    throw new InvalidOperationException($"target-triangle-count {t.Shape.Name} {touching}/{t.Shape.Triangles.Length / 3}");
                int n = t.Shape.VertexCount;
                int[] band = t.Topology.Triangles.Select(v => v < n ? t.FirstVertex + v : NativeVertexCount + t.OuterOffset + (v - n)).ToArray();
                insertions.Add((firstTriangle + 3 * touching, band));
            }
            var final = new List<int>(native.Length + insertions.Sum(x => x.Band.Length));
            int cursor = 0;
            foreach ((int at, int[] band) in insertions.OrderBy(x => x.At))
            {
                for (; cursor < at; cursor++) final.Add(native[cursor]);
                final.AddRange(band);
            }
            for (; cursor < native.Length; cursor++) final.Add(native[cursor]);
            NativeTriangles[sm] = native;
            FinalTriangles[sm] = final.ToArray();
        }
    }

    private sealed class PendingFrame
    {
        private readonly SkeletonRendererInstruction _instruction;
        private readonly int _oldVertexCount;
        private readonly Dictionary<int, (int Vertices, int Triangles)> _old = new();
        internal Layout Layout { get; }

        internal PendingFrame(SkeletonRendererInstruction instruction, Layout layout)
        {
            _instruction = instruction; Layout = layout; _oldVertexCount = instruction.rawVertexCount;
        }

        internal void Remember(int sm, int vertices, int triangles) => _old[sm] = (vertices, triangles);

        internal void RollbackInstruction()
        {
            _instruction.rawVertexCount = _oldVertexCount;
            var items = _instruction.submeshInstructions.Items;
            foreach ((int sm, (int vertices, int triangles)) in _old)
            {
                SubmeshInstruction sub = items[sm];
                sub.rawVertexCount = vertices;
                sub.rawTriangleCount = triangles;
                items[sm] = sub;
            }
        }
    }
}

/// <summary>One-time, read-only check that the band topology can be built for every mesh seen (nominal canvas).</summary>
internal static class TopologySurvey
{
    private const int Limit = 4000;
    private static readonly HashSet<long> Seen = new();
    private static readonly Dictionary<string, int> Results = new();
    private static int _ok;

    internal static void Observe(MeshAttachment mesh, Action<string, string> log)
    {
        long key = mesh.Pointer.ToInt64();
        lock (Seen)
        {
            if (Seen.Count >= Limit || !Seen.Add(key)) return;
        }
        string result;
        try
        {
            float[] raw = mesh.regionUVs.ToArray();
            int[] triangles = mesh.triangles.ToArray().Select(t => (int)t).ToArray();
            RingTopology.Build(raw, triangles, new CanvasSpec(100, 100, 16));
            result = "ok";
        }
        catch (Exception exception)
        {
            string message = exception.GetBaseException().Message;
            int space = message.IndexOf(' ');
            result = space > 0 ? message[..space] : message;
            log("SURVEY_FAILED", $"attachment={mesh.Name} reason={exception.GetBaseException().Message}");
        }
        lock (Seen)
        {
            Results[result] = Results.GetValueOrDefault(result) + 1;
            if (result == "ok") _ok++;
        }
    }

    internal static string Summary()
    {
        lock (Seen) return $"meshes={Seen.Count} ok={_ok} " + string.Join(" ", Results.Where(p => p.Key != "ok").Select(p => $"{p.Key}={p.Value}"));
    }
}

/// <summary>
/// Placement rects Main wrote for each MeshAttachment (any scope) and the orientation convention that maps a placement
/// rect to Spine UVs, learned from gameplay meshes (whose own UVs are verified correct). Used for Preview atlases whose
/// mesh UVs disagree with the placed PNG (Bassist Battle hair_1 / Fail: same area, different aspect).
/// </summary>
internal static class PlacementCalibration
{
    internal readonly record struct Placed(int X, int Y, int W, int H, int Degrees);
    private static readonly Dictionary<long, Placed> Placements = new();
    private static readonly Dictionary<int, int> Variant = new();      // degrees -> variant (0..15)
    private static readonly HashSet<string> Logged = new();
    private static readonly V2[] Corners = { new(0, 0), new(1, 0), new(0, 1), new(1, 1), new(0.5, 0.25) };

    internal static void Record(long attachment, Placed placed) { lock (Placements) Placements[attachment] = placed; }

    private static V2 Apply(int variant, V2 c, Placed p, int width, int height)
    {
        double u = c.X, v = c.Y;
        int d = variant & 7;
        if ((d & 4) != 0) (u, v) = (v, u);
        if ((d & 1) != 0) u = 1 - u;
        if ((d & 2) != 0) v = 1 - v;
        double x = (p.X + p.W * u) / width, y = (p.Y + p.H * v) / height;
        if ((variant & 8) != 0) y = 1 - y;
        return new V2(x, y);
    }

    internal static void Calibrate(long attachment, MeshCanvasSession.Affine own, int width, int height, Action<string, string> log)
    {
        Placed p;
        lock (Placements) if (!Placements.TryGetValue(attachment, out p)) return;
        lock (Variant) if (Variant.ContainsKey(p.Degrees)) return;
        for (int variant = 0; variant < 16; variant++)
        {
            double worst = 0;
            foreach (V2 c in Corners)
            {
                V2 a = own.Map(c), b = Apply(variant, c, p, width, height);
                worst = Math.Max(worst, Math.Max(Math.Abs(a.X - b.X) * width, Math.Abs(a.Y - b.Y) * height));
            }
            if (worst > 0.75) continue;
            lock (Variant) Variant[p.Degrees] = variant;
            log("PLACEMENT_CALIBRATED", $"degrees={p.Degrees} variant={variant} worstTexelError={worst:0.###} rect={p.X},{p.Y},{p.W}x{p.H} atlas={width}x{height}");
            return;
        }
        string key = "fail|" + p.Degrees;
        lock (Logged) if (Logged.Add(key)) log("PLACEMENT_CALIBRATION_MISS", $"degrees={p.Degrees} rect={p.X},{p.Y},{p.W}x{p.H} atlas={width}x{height}");
    }

    internal static bool TryMap(long attachment, int width, int height, out MeshCanvasSession.Affine map, out string how)
    {
        map = default; how = string.Empty;
        Placed p;
        lock (Placements) if (!Placements.TryGetValue(attachment, out p)) return false;
        int variant;
        lock (Variant) if (!Variant.TryGetValue(p.Degrees, out variant)) return false;
        var src = new V2[Corners.Length]; var dst = new V2[Corners.Length];
        for (int i = 0; i < Corners.Length; i++) { src[i] = Corners[i]; dst[i] = Apply(variant, Corners[i], p, width, height); }
        map = MeshCanvasSession.Affine.Fit(src, dst, out _);
        how = $"deg={p.Degrees},v={variant}";
        return true;
    }
}