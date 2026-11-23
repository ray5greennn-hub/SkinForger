using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSpine;
using MelonLoader;

namespace SkinForger.ElfinMeshCanvas;

public sealed class ElfinMeshCanvasMod : global::SkinForger.Core.SfModule
{
    private const string Prefix = "[SkinForge][ElfinMeshCanvas]";
    private static readonly Dictionary<long, MeshBaseline> Baselines = new();
    private static readonly HashSet<string> Logged = new(StringComparer.Ordinal);

    [ThreadStatic]
    private static Stack<BuildContext>? _contexts;

    /// <summary>
    /// NOT INSTALLED for the authored Main.
    ///
    /// It reaches canvas expansion by REWRITING THE ATTACHMENT — ApplyFullCanvas assigns mesh.vertices
    /// and mesh.regionUVs — which is the approach SkinForger abandoned on 2026-11-13 because rewriting a
    /// mesh's vertices is what broke native deformation (SF日志\SF角色扩框.txt; the standing rule is
    /// "no Attachment is ever rewritten").
    ///
    /// It was switched on for the authored Main on 2026-11-19 and did not help: with it installed the
    /// elfin looked the same as with it off, and it was rewriting the vertices of EVERY elfin mesh,
    /// including the fifteen parts whose canvas never changed. No session on record shows it producing a
    /// correct elfin.
    ///
    /// The authored Main now handles an expanded canvas without it: a mesh keeps its own shape and samples
    /// the piece of the author's picture that matches the game's canvas, placed by the anchor
    /// (RegionGeometry.MeshWindow). The author's extra border is not drawn — showing it needs vertices
    /// added, which is R4-12's transient-buffer approach, and widening R4-12 to elfins is the work.
    /// </summary>
    private static bool StandDown()
    {
        MelonLoader.MelonLogger.Msg(
            $"{Prefix} not installed: it rewrites mesh vertices (dropped 2026-11-13); the authored Main " +
            "samples the game-sized window instead, and real elfin expansion belongs in R4-12");
        return true;
    }

    public override void OnInitializeMelon()
    {
        if (StandDown()) return;
        Type? host = AccessTools.TypeByName("SkinForger.SkinForgerMod");
        MethodInfo? build = Find(host, "TryBuildSynthesizedAtlasForSkeleton", 5);
        MethodInfo? placement = Find(host, "ApplyRegionPlacementToAttachment", 6);
        if (build == null || placement == null)
        {
            MelonLogger.Warning($"{Prefix} disabled: compatible 97E Elfin placement methods were not found.");
            return;
        }

        HarmonyInstance.Patch(build,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(ElfinMeshCanvasMod), nameof(BuildPrefix))),
            finalizer: new HarmonyMethod(AccessTools.Method(typeof(ElfinMeshCanvasMod), nameof(BuildFinalizer))));
        HarmonyInstance.Patch(placement,
            postfix: new HarmonyMethod(AccessTools.Method(typeof(ElfinMeshCanvasMod), nameof(PlacementPostfix))));

        MelonLogger.Msg($"{Prefix} ready: Elfin MeshAttachment full-canvas adapter; Character path untouched.");
    }

    private static MethodInfo? Find(Type? type, string name, int parameterCount) =>
        type?.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .SingleOrDefault(method => method.Name == name && method.GetParameters().Length == parameterCount);

    private static void BuildPrefix(Skeleton __0, bool __4)
    {
        (_contexts ??= new Stack<BuildContext>()).Push(new BuildContext(__0, __4));
    }

    private static Exception? BuildFinalizer(Exception? __exception)
    {
        if (_contexts is { Count: > 0 }) _contexts.Pop();
        return __exception;
    }

    private static void PlacementPostfix(object[] __args)
    {
        BuildContext? context = _contexts is { Count: > 0 } ? _contexts.Peek() : null;
        if (context == null || !context.Elfin || __args.Length < 6) return;

        try
        {
            Attachment? attachment = __args[0] as Attachment;
            MeshAttachment? mesh = attachment?.TryCast<MeshAttachment>();
            if (mesh == null || __args[2] == null) return;

            ApplyFullCanvas(context.Skeleton, mesh, __args[2], __args[4] as string ?? string.Empty);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"{Prefix} mesh adaptation failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ApplyFullCanvas(Skeleton skeleton, MeshAttachment mesh, object placement, string regionPath)
    {
        int worldLength = mesh.worldVerticesLength;
        if (worldLength < 8 || (worldLength & 1) != 0) return;

        long key = mesh.Pointer.ToInt64();
        if (!Baselines.TryGetValue(key, out MeshBaseline? baseline) || !baseline.IsCompatible(mesh))
        {
            baseline = MeshBaseline.Capture(mesh);
            Baselines[key] = baseline;
        }

        int vertexCount = worldLength / 2;
        int hullVertices = Math.Clamp(baseline.HullLength / 2, 0, vertexCount);
        if (hullVertices < 4 || baseline.RegionUvs.Length < worldLength)
        {
            LogOnce($"skip|{key:X}", $"{Prefix} skipped attachment={mesh.Name}: hull has fewer than four vertices.");
            return;
        }

        int slotIndex = FindSlotIndex(skeleton, mesh);
        if (slotIndex < 0 || skeleton.Slots == null || slotIndex >= skeleton.Slots.Count) return;
        Slot? slot = skeleton.Slots.Items[slotIndex];
        if (slot?.bone == null) return;

        int baseWidth = Math.Max(1, ReadInt(placement, "BaseNormalWidth"));
        int baseHeight = Math.Max(1, ReadInt(placement, "BaseNormalHeight"));
        int width = Math.Max(1, ReadInt(placement, "NormalWidth"));
        int height = Math.Max(1, ReadInt(placement, "NormalHeight"));
        float anchorX = ReadFloat(placement, "AnchorX");
        float anchorY = ReadFloat(placement, "AnchorY");
        float defaultAnchorX = ReadFloat(placement, "DefaultAnchorX");
        float defaultAnchorY = ReadFloat(placement, "DefaultAnchorY");
        float offsetX = ReadFloat(placement, "OffsetX");
        float offsetY = ReadFloat(placement, "OffsetY");

        float[] originalWorld = ComputeWorldVertices(skeleton, slot.bone, baseline);
        double[] plane = FitImagePlane(baseline.RegionUvs, originalWorld, worldLength);
        float[] canvasUvs = BuildCanvasUvs(baseline.RegionUvs, vertexCount, hullVertices);
        float[] vertices = BuildVertices(skeleton, slot.bone, baseline, originalWorld, canvasUvs, plane,
            baseWidth, baseHeight, width, height, anchorX, anchorY, defaultAnchorX, defaultAnchorY, offsetX, offsetY);

        mesh.vertices = Floats(vertices);
        mesh.regionUVs = Floats(canvasUvs);
        mesh.regionOffsetX = 0f;
        mesh.regionOffsetY = 0f;
        mesh.regionWidth = width;
        mesh.regionHeight = height;
        mesh.regionOriginalWidth = width;
        mesh.regionOriginalHeight = height;
        mesh.UpdateUVs();

        string logKey = $"{key:X}|{width}x{height}|{regionPath}";
        LogOnce(logKey,
            $"{Prefix} applied attachment={mesh.Name}, slot={slotIndex}:{slot.data?.name}, base={baseWidth}x{baseHeight}, replacement={width}x{height}, vertices={vertexCount}, weighted={baseline.Bones != null}, hull={hullVertices}, fullCanvasUV=True");
    }

    private static float[] ComputeWorldVertices(Skeleton skeleton, Bone slotBone, MeshBaseline baseline)
    {
        int vertexCount = baseline.WorldVerticesLength / 2;
        var world = new float[baseline.WorldVerticesLength];
        if (baseline.Bones == null)
        {
            for (int vertex = 0; vertex < vertexCount; vertex++)
            {
                float x = baseline.Vertices[vertex * 2];
                float y = baseline.Vertices[vertex * 2 + 1];
                world[vertex * 2] = slotBone.a * x + slotBone.b * y + slotBone.worldX;
                world[vertex * 2 + 1] = slotBone.c * x + slotBone.d * y + slotBone.worldY;
            }
            return world;
        }

        int boneCursor = 0;
        int vertexCursor = 0;
        for (int vertex = 0; vertex < vertexCount; vertex++)
        {
            int influenceCount = baseline.Bones[boneCursor++];
            double x = 0;
            double y = 0;
            for (int influence = 0; influence < influenceCount; influence++)
            {
                int boneIndex = baseline.Bones[boneCursor++];
                Bone bone = skeleton.Bones.Items[boneIndex];
                float localX = baseline.Vertices[vertexCursor++];
                float localY = baseline.Vertices[vertexCursor++];
                float weight = baseline.Vertices[vertexCursor++];
                x += (bone.a * localX + bone.b * localY + bone.worldX) * weight;
                y += (bone.c * localX + bone.d * localY + bone.worldY) * weight;
            }
            world[vertex * 2] = (float)x;
            world[vertex * 2 + 1] = (float)y;
        }
        return world;
    }

    private static float[] BuildVertices(Skeleton skeleton, Bone slotBone, MeshBaseline baseline,
        float[] originalWorld, float[] canvasUvs, double[] plane,
        int baseWidth, int baseHeight, int width, int height,
        float anchorX, float anchorY, float defaultAnchorX, float defaultAnchorY, float offsetX, float offsetY)
    {
        float[] result = (float[])baseline.Vertices.Clone();
        int vertexCount = baseline.WorldVerticesLength / 2;
        if (baseline.Bones == null)
        {
            for (int vertex = 0; vertex < vertexCount; vertex++)
            {
                (double desiredX, double desiredY) = DesiredWorld(canvasUvs, vertex, plane, baseWidth, baseHeight,
                    width, height, anchorX, anchorY, defaultAnchorX, defaultAnchorY, offsetX, offsetY);
                (double localDx, double localDy) = WorldDeltaToLocal(slotBone,
                    desiredX - originalWorld[vertex * 2], desiredY - originalWorld[vertex * 2 + 1]);
                result[vertex * 2] += (float)localDx;
                result[vertex * 2 + 1] += (float)localDy;
            }
            return result;
        }

        int boneCursor = 0;
        int vertexCursor = 0;
        for (int vertex = 0; vertex < vertexCount; vertex++)
        {
            (double desiredX, double desiredY) = DesiredWorld(canvasUvs, vertex, plane, baseWidth, baseHeight,
                width, height, anchorX, anchorY, defaultAnchorX, defaultAnchorY, offsetX, offsetY);
            double dx = desiredX - originalWorld[vertex * 2];
            double dy = desiredY - originalWorld[vertex * 2 + 1];
            int influenceCount = baseline.Bones[boneCursor++];
            for (int influence = 0; influence < influenceCount; influence++)
            {
                int boneIndex = baseline.Bones[boneCursor++];
                Bone bone = skeleton.Bones.Items[boneIndex];
                (double localDx, double localDy) = WorldDeltaToLocal(bone, dx, dy);
                result[vertexCursor] = baseline.Vertices[vertexCursor] + (float)localDx;
                result[vertexCursor + 1] = baseline.Vertices[vertexCursor + 1] + (float)localDy;
                result[vertexCursor + 2] = baseline.Vertices[vertexCursor + 2];
                vertexCursor += 3;
            }
        }
        return result;
    }

    private static (double X, double Y) DesiredWorld(float[] canvasUvs, int vertex, double[] plane,
        int baseWidth, int baseHeight, int width, int height,
        float anchorX, float anchorY, float defaultAnchorX, float defaultAnchorY, float offsetX, float offsetY)
    {
        double u = (defaultAnchorX - anchorX + offsetX + canvasUvs[vertex * 2] * width) / baseWidth;
        double v = (defaultAnchorY - anchorY + offsetY + canvasUvs[vertex * 2 + 1] * height) / baseHeight;
        return (plane[0] * u + plane[1] * v + plane[2],
                plane[3] * u + plane[4] * v + plane[5]);
    }

    private static (double X, double Y) WorldDeltaToLocal(Bone bone, double dx, double dy)
    {
        double determinant = bone.a * bone.d - bone.b * bone.c;
        if (Math.Abs(determinant) < 1e-10) throw new InvalidOperationException("A required bone transform is singular.");
        return ((dx * bone.d - dy * bone.b) / determinant,
                (dy * bone.a - dx * bone.c) / determinant);
    }

    private static double[] FitImagePlane(float[] uvs, float[] world, int length)
    {
        double sUU = 0, sUV = 0, sU = 0, sVV = 0, sV = 0, n = 0;
        double sUX = 0, sVX = 0, sX = 0, sUY = 0, sVY = 0, sY = 0;
        for (int i = 0; i + 1 < length; i += 2)
        {
            double u = uvs[i];
            double v = uvs[i + 1];
            double x = world[i];
            double y = world[i + 1];
            if (!Finite(u) || !Finite(v) || !Finite(x) || !Finite(y)) continue;
            sUU += u * u; sUV += u * v; sU += u;
            sVV += v * v; sV += v; n++;
            sUX += u * x; sVX += v * x; sX += x;
            sUY += u * y; sVY += v * y; sY += y;
        }
        if (n < 3) throw new InvalidOperationException("Mesh has fewer than three finite geometry samples.");
        double[,] matrix = { { sUU, sUV, sU }, { sUV, sVV, sV }, { sU, sV, n } };
        double[] xCoefficients = Solve3(matrix, new[] { sUX, sVX, sX });
        double[] yCoefficients = Solve3(matrix, new[] { sUY, sVY, sY });
        return new[] { xCoefficients[0], xCoefficients[1], xCoefficients[2], yCoefficients[0], yCoefficients[1], yCoefficients[2] };
    }

    private static double[] Solve3(double[,] source, double[] right)
    {
        double[,] matrix = (double[,])source.Clone();
        double[] result = (double[])right.Clone();
        for (int pivot = 0; pivot < 3; pivot++)
        {
            int best = pivot;
            for (int row = pivot + 1; row < 3; row++)
                if (Math.Abs(matrix[row, pivot]) > Math.Abs(matrix[best, pivot])) best = row;
            if (Math.Abs(matrix[best, pivot]) < 1e-12) throw new InvalidOperationException("Mesh image plane is degenerate.");
            if (best != pivot)
            {
                for (int column = pivot; column < 3; column++)
                    (matrix[pivot, column], matrix[best, column]) = (matrix[best, column], matrix[pivot, column]);
                (result[pivot], result[best]) = (result[best], result[pivot]);
            }
            double divisor = matrix[pivot, pivot];
            for (int column = pivot; column < 3; column++) matrix[pivot, column] /= divisor;
            result[pivot] /= divisor;
            for (int row = 0; row < 3; row++)
            {
                if (row == pivot) continue;
                double factor = matrix[row, pivot];
                for (int column = pivot; column < 3; column++) matrix[row, column] -= factor * matrix[pivot, column];
                result[row] -= factor * result[pivot];
            }
        }
        return result;
    }

    private static float[] BuildCanvasUvs(float[] original, int vertexCount, int hullVertices)
    {
        float[] result = (float[])original.Clone();
        int start = 0;
        double startDistance = double.PositiveInfinity;
        for (int vertex = 0; vertex < hullVertices; vertex++)
        {
            double u = original[vertex * 2];
            double v = original[vertex * 2 + 1];
            double distance = u * u + v * v;
            if (distance < startDistance) { startDistance = distance; start = vertex; }
        }

        var order = new int[hullVertices];
        var cumulative = new double[hullVertices + 1];
        for (int position = 0; position < hullVertices; position++)
            order[position] = (start + position) % hullVertices;
        for (int position = 1; position <= hullVertices; position++)
        {
            int previous = order[position - 1];
            int current = order[position % hullVertices];
            double du = original[current * 2] - original[previous * 2];
            double dv = original[current * 2 + 1] - original[previous * 2 + 1];
            cumulative[position] = cumulative[position - 1] + Math.Sqrt(du * du + dv * dv);
        }

        double total = cumulative[hullVertices];
        if (total < 1e-8) throw new InvalidOperationException("Mesh hull perimeter is degenerate.");
        int[] cornerPositions = { 0, 0, 0, 0, hullVertices };
        int previousCorner = 0;
        for (int corner = 1; corner < 4; corner++)
        {
            int minimum = previousCorner + 1;
            int maximum = hullVertices - (4 - corner);
            int best = minimum;
            double target = total * corner / 4.0;
            double bestDistance = double.PositiveInfinity;
            for (int position = minimum; position <= maximum; position++)
            {
                double distance = Math.Abs(cumulative[position] - target);
                if (distance < bestDistance) { bestDistance = distance; best = position; }
            }
            cornerPositions[corner] = best;
            previousCorner = best;
        }

        bool clockwise = SignedArea(original, hullVertices) < 0;
        (float U, float V)[] corners = clockwise
            ? new[] { (0f, 0f), (0f, 1f), (1f, 1f), (1f, 0f), (0f, 0f) }
            : new[] { (0f, 0f), (1f, 0f), (1f, 1f), (0f, 1f), (0f, 0f) };
        for (int edge = 0; edge < 4; edge++)
        {
            int first = cornerPositions[edge];
            int last = cornerPositions[edge + 1];
            double edgeLength = cumulative[last] - cumulative[first];
            for (int position = first; position < last; position++)
            {
                double alpha = edgeLength > 1e-8 ? (cumulative[position] - cumulative[first]) / edgeLength : 0;
                int vertex = order[position];
                result[vertex * 2] = (float)(corners[edge].U + (corners[edge + 1].U - corners[edge].U) * alpha);
                result[vertex * 2 + 1] = (float)(corners[edge].V + (corners[edge + 1].V - corners[edge].V) * alpha);
            }
        }

        for (int vertex = hullVertices; vertex < vertexCount; vertex++)
        {
            result[vertex * 2] = Math.Clamp(original[vertex * 2], 0f, 1f);
            result[vertex * 2 + 1] = Math.Clamp(original[vertex * 2 + 1], 0f, 1f);
        }
        return result;
    }

    private static double SignedArea(float[] vertices, int count)
    {
        double area = 0;
        for (int vertex = 0; vertex < count; vertex++)
        {
            int next = (vertex + 1) % count;
            area += vertices[vertex * 2] * vertices[next * 2 + 1]
                - vertices[next * 2] * vertices[vertex * 2 + 1];
        }
        return area * 0.5;
    }

    private static int FindSlotIndex(Skeleton skeleton, Attachment target)
    {
        if (skeleton.Slots != null)
            for (int index = 0; index < skeleton.Slots.Count; index++)
                if (skeleton.Slots.Items[index]?.Attachment?.Pointer == target.Pointer) return index;

        if (skeleton.Data?.Slots == null) return -1;
        int activeSkinResult = FindInSkin(skeleton, skeleton.skin, target);
        if (activeSkinResult >= 0) return activeSkinResult;
        var skins = skeleton.Data.Skins;
        if (skins != null)
            for (int skinIndex = 0; skinIndex < skins.Count; skinIndex++)
            {
                int result = FindInSkin(skeleton, skins.Items[skinIndex], target);
                if (result >= 0) return result;
            }
        return FindInSkin(skeleton, skeleton.Data.DefaultSkin, target);
    }

    private static int FindInSkin(Skeleton skeleton, Skin? skin, Attachment target)
    {
        if (skin == null || skeleton.Data?.Slots == null) return -1;
        var entries = new Il2CppSystem.Collections.Generic.List<Skin.SkinEntry>();
        for (int slotIndex = 0; slotIndex < skeleton.Data.Slots.Count; slotIndex++)
        {
            entries.Clear();
            skin.GetAttachments(slotIndex, entries);
            for (int entry = 0; entry < entries.Count; entry++)
                if (entries[entry]?.Attachment?.Pointer == target.Pointer) return slotIndex;
        }
        return -1;
    }

    private static int ReadInt(object instance, string name) => Convert.ToInt32(Read(instance, name));
    private static float ReadFloat(object instance, string name) => Convert.ToSingle(Read(instance, name));
    private static object Read(object instance, string name)
    {
        PropertyInfo? property = instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property == null) throw new MissingMemberException(instance.GetType().FullName, name);
        return property.GetValue(instance) ?? throw new InvalidOperationException($"Placement value {name} is null.");
    }

    private static Il2CppStructArray<float> Floats(float[] values)
    {
        var result = new Il2CppStructArray<float>(values.Length);
        for (int index = 0; index < values.Length; index++) result[index] = values[index];
        return result;
    }

    private static float[] Copy(Il2CppStructArray<float>? source)
    {
        if (source == null) return Array.Empty<float>();
        var result = new float[source.Length];
        for (int index = 0; index < result.Length; index++) result[index] = source[index];
        return result;
    }

    private static int[]? Copy(Il2CppStructArray<int>? source)
    {
        if (source == null) return null;
        var result = new int[source.Length];
        for (int index = 0; index < result.Length; index++) result[index] = source[index];
        return result;
    }

    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static void LogOnce(string key, string message)
    {
        if (Logged.Add(key)) MelonLogger.Msg(message);
    }

    private sealed record BuildContext(Skeleton Skeleton, bool Elfin);

    private sealed class MeshBaseline
    {
        public int[]? Bones { get; init; }
        public float[] Vertices { get; init; } = Array.Empty<float>();
        public float[] RegionUvs { get; init; } = Array.Empty<float>();
        public int WorldVerticesLength { get; init; }
        public int HullLength { get; init; }

        public static MeshBaseline Capture(MeshAttachment mesh) => new()
        {
            Bones = Copy(mesh.bones),
            Vertices = Copy(mesh.vertices),
            RegionUvs = Copy(mesh.regionUVs),
            WorldVerticesLength = mesh.worldVerticesLength,
            HullLength = mesh.hulllength
        };

        public bool IsCompatible(MeshAttachment mesh) =>
            WorldVerticesLength == mesh.worldVerticesLength &&
            RegionUvs.Length == mesh.worldVerticesLength &&
            (Bones == null) == (mesh.bones == null);
    }
}
