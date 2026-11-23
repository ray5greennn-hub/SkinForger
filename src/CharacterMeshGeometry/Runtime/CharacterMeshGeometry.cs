using System.Reflection;
using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSpine;
using MelonLoader;
using Newtonsoft.Json.Linq;

namespace SkinForger.CharacterMeshGeometry;

internal static class CharacterMeshGeometry
{
    private const string Prefix = "[SkinForge][CharacterMeshGeometry]";
    private static readonly Dictionary<long, MeshBaseline> Baselines = new();
    private static readonly HashSet<string> Logged = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedFailures = new(StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<Skeleton, float[]> SetupPoseMatrixCache = new();
    private static readonly object SetupPoseMatrixCacheLock = new();

    internal static void Apply(Skeleton skeleton, bool elfin, int slotIndex, Attachment attachment,
        object placement, string regionPath)
    {
        if (elfin || skeleton == null || attachment == null || placement == null) return;

        MeshAttachment? mesh = attachment.TryCast<MeshAttachment>();
        if (mesh == null) return;

        try
        {
            if (!ShouldPreserveOriginalMesh(regionPath ?? string.Empty))
                throw new InvalidOperationException("Global Mesh Geometry preservation policy is unavailable.");
            ApplyCanvas(skeleton, slotIndex, mesh, placement, regionPath ?? string.Empty);
        }
        catch (Exception ex)
        {
            string failureKey = $"{attachment.Name}|{regionPath}|{ex.GetType().FullName}|{ex.Message}";
            if (LoggedFailures.Add(failureKey))
                MelonLogger.Warning($"{Prefix} FAILED attachment={attachment.Name} " +
                    $"type={ex.GetType().Name} message={ex.Message}");
        }
    }

    private static bool GlobalMeshGeometryPolicy() => true;

    private static bool ShouldPreserveOriginalMesh(string regionPath)
    {
        if (!GlobalMeshGeometryPolicy())
            throw new InvalidOperationException("Global Mesh Geometry preservation policy must not be disabled.");
        // This is deliberately independent of attachment metadata. Existing,
        // malformed, missing, and future JSON files cannot opt ordinary texture
        // replacement back into pose-dependent geometry reconstruction.
        return true;
    }

    private static void ApplyCanvas(Skeleton skeleton, int slotIndex, MeshAttachment mesh,
        object placement, string regionPath)
    {
        if (slotIndex < 0 || skeleton.Slots == null || slotIndex >= skeleton.Slots.Count)
            throw new InvalidOperationException($"Invalid slot index {slotIndex}.");
        Slot? slot = skeleton.Slots.Items[slotIndex];
        if (slot?.bone == null) throw new InvalidOperationException("Replacement slot has no bone.");

        long attachmentId = mesh.Pointer.ToInt64();
        if (!Baselines.TryGetValue(attachmentId, out MeshBaseline? baseline))
        {
            baseline = MeshBaseline.Capture(mesh);
            Baselines.Add(attachmentId, baseline);
        }
        else if (!baseline.IsCompatible(mesh))
        {
            throw new InvalidOperationException(
                "Immutable baseline identity no longer matches this MeshAttachment; refusing to recapture mutated geometry.");
        }

        int worldLength = baseline.WorldVerticesLength;
        int vertexCount = worldLength / 2;
        int hullVertices = Math.Clamp(baseline.HullLength / 2, 0, vertexCount);
        if (worldLength < 8 || (worldLength & 1) != 0)
            throw new InvalidOperationException($"Invalid worldVerticesLength={worldLength}.");
        if (baseline.RegionUvs.Length != worldLength)
            throw new InvalidOperationException(
                $"regionUVs length {baseline.RegionUvs.Length} does not match worldVerticesLength {worldLength}.");
        if (baseline.Weighted && baseline.Bones.Length == 0)
            throw new InvalidOperationException("Weighted mesh has an empty bones stream.");
        if (!baseline.Weighted && baseline.Vertices.Length != worldLength)
            throw new InvalidOperationException(
                $"Unweighted vertices length {baseline.Vertices.Length} does not match worldVerticesLength {worldLength}.");
        if (hullVertices < 4)
            throw new InvalidOperationException($"Mesh hull has only {hullVertices} vertices.");

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

        float[] originalWorld = ComputeWorldVertices(skeleton, slot.bone, baseline.Weighted, baseline.Bones,
            baseline.Vertices, baseline.WorldVerticesLength);
        Bounds beforeBounds = Bounds.From(originalWorld);
        double[] plane = FitImagePlane(baseline.RegionUvs, originalWorld, worldLength);
        float[] expandedRegionUvs = BuildCanvasUvs(
            baseline.RegionUvs, baseline.Triangles, vertexCount, hullVertices);
        float[] expandedVertices = BuildSetupPoseVertices(skeleton, slot.bone, baseline, originalWorld,
            expandedRegionUvs, plane,
            baseWidth, baseHeight, width, height, anchorX, anchorY, defaultAnchorX, defaultAnchorY, offsetX, offsetY);

        float[] postWorld = ComputeWorldVertices(skeleton, slot.bone, baseline.Weighted, baseline.Bones,
            expandedVertices, baseline.WorldVerticesLength);
        Bounds afterBounds = Bounds.From(postWorld);
        ValidateTriangleMapping("regionUVs", baseline.RegionUvs, expandedRegionUvs,
            baseline.Triangles, hullVertices, expandedRegionUvs);
        ValidateTriangleMapping("world geometry", originalWorld, postWorld,
            baseline.Triangles, hullVertices, expandedRegionUvs);
        ValidatePostState(mesh, baseline, expandedVertices, expandedRegionUvs, beforeBounds, afterBounds,
            baseWidth, baseHeight, width, height);

        mesh.vertices = Floats(expandedVertices);
        mesh.regionUVs = Floats(expandedRegionUvs);

        string key = $"{attachmentId:X}|{width}x{height}|{regionPath}";
        if (!Logged.Add(key)) return;

        string identity = $"attachment=0x{attachmentId:X} deform=0x{Ptr(mesh.deformAttachment):X} " +
            $"parent=0x{Ptr(mesh.parentMesh):X}";
        MelonLogger.Msg($"{Prefix} BASELINE slot={slotIndex}:{slot.data?.name} {identity} " +
            $"weighted={baseline.Weighted} " +
            $"vertices={baseline.Vertices.Length} worldVerticesLength={baseline.WorldVerticesLength} " +
            $"bones={baseline.Bones.Length} bonesHash={Hash(baseline.Bones):X16} " +
            $"triangles={baseline.Triangles.Length} trianglesHash={Hash(baseline.Triangles):X16} " +
            $"regionUVs={baseline.RegionUvs.Length} weightsHash={HashWeights(baseline.Bones, baseline.Vertices):X16} " +
            $"bounds={beforeBounds} png={width}x{height} base={baseWidth}x{baseHeight}");
        MelonLogger.Msg($"{Prefix} POST slot={slotIndex}:{slot.data?.name} {identity} " +
            $"weighted={baseline.Weighted} " +
            $"vertices={mesh.vertices?.Length ?? 0} worldVerticesLength={mesh.worldVerticesLength} " +
            $"bones={mesh.bones?.Length ?? 0} bonesHash={Hash(Copy(mesh.bones)):X16} " +
            $"triangles={mesh.triangles?.Length ?? 0} trianglesHash={Hash(Copy(mesh.triangles)):X16} " +
            $"regionUVs={mesh.regionUVs?.Length ?? 0} weightsHash={HashWeights(baseline.Bones, Copy(mesh.vertices)):X16} " +
            $"bounds={afterBounds} png={width}x{height} uvChanged={!Equal(baseline.RegionUvs, expandedRegionUvs)} " +
            $"topologyPreserved=true deformPreserved=true bindReference=setup-pose path={regionPath}");
    }

    private static float[] BuildSetupPoseVertices(Skeleton skeleton, Bone slotBone, MeshBaseline baseline,
        float[] originalWorld, float[] canvasUvs, double[] plane, int baseWidth, int baseHeight, int width, int height,
        float anchorX, float anchorY, float defaultAnchorX, float defaultAnchorY, float offsetX, float offsetY)
    {
        // A single-bone mesh is affine under its slot bone, so the existing inverse transform
        // produces the same local geometry in every pose. Weighted meshes require a stable
        // bind reference; using the live pose here permanently bakes an animation frame into
        // every influence-local coordinate.
        if (!baseline.Weighted)
            return BuildVertices(skeleton, slotBone, baseline, originalWorld, canvasUvs, plane,
                baseWidth, baseHeight, width, height, anchorX, anchorY,
                defaultAnchorX, defaultAnchorY, offsetX, offsetY);

        float[] setupMatrices = CaptureSetupPoseMatrices(skeleton);
        float[] setupWorld = ComputeWeightedWorld(baseline.Bones, baseline.Vertices,
            baseline.WorldVerticesLength, setupMatrices);
        double[] setupPlane = FitImagePlane(baseline.RegionUvs, setupWorld, baseline.WorldVerticesLength);
        return BuildWeightedVerticesFromReference(baseline.Bones, baseline.Vertices,
            baseline.WorldVerticesLength, setupWorld, canvasUvs, setupPlane, setupMatrices,
            baseWidth, baseHeight, width, height, anchorX, anchorY,
            defaultAnchorX, defaultAnchorY, offsetX, offsetY);
    }

    private static float[] CaptureSetupPoseMatrices(Skeleton skeleton)
    {
        lock (SetupPoseMatrixCacheLock)
        {
            if (SetupPoseMatrixCache.TryGetValue(skeleton, out float[]? cached)) return cached;

            var setup = new Skeleton(skeleton.Data);
            setup.SetToSetupPose();
            setup.UpdateWorldTransform();
            if (setup.Bones == null || skeleton.Bones == null || setup.Bones.Count != skeleton.Bones.Count)
                throw new InvalidOperationException(
                    $"Setup-pose skeleton bone count mismatch {setup.Bones?.Count ?? 0}/{skeleton.Bones?.Count ?? 0}.");

            var matrices = new float[setup.Bones.Count * 6];
            for (int index = 0; index < setup.Bones.Count; index++)
            {
                Bone reference = setup.Bones.Items[index];
                Bone live = skeleton.Bones.Items[index];
                if (reference?.Data == null || live?.Data == null || reference.Data.index != live.Data.index)
                    throw new InvalidOperationException($"Setup-pose bone identity mismatch at index {index}.");
                int offset = index * 6;
                matrices[offset] = reference.a;
                matrices[offset + 1] = reference.b;
                matrices[offset + 2] = reference.c;
                matrices[offset + 3] = reference.d;
                matrices[offset + 4] = reference.worldX;
                matrices[offset + 5] = reference.worldY;
            }
            ValidateBoneMatrices(matrices);
            SetupPoseMatrixCache.Add(skeleton, matrices);
            return matrices;
        }
    }

    private static float[] ComputeWeightedWorld(int[] bones, float[] vertices,
        int worldVerticesLength, float[] matrices)
    {
        ValidateBoneMatrices(matrices);
        int vertexCount = worldVerticesLength / 2;
        var world = new float[worldVerticesLength];
        int boneCursor = 0;
        int vertexCursor = 0;
        for (int vertex = 0; vertex < vertexCount; vertex++)
        {
            if (boneCursor >= bones.Length)
                throw new InvalidOperationException($"Weighted bones ended before vertex {vertex}.");
            int influenceCount = bones[boneCursor++];
            double x = 0;
            double y = 0;
            for (int influence = 0; influence < influenceCount; influence++)
            {
                if (boneCursor >= bones.Length || vertexCursor + 2 >= vertices.Length)
                    throw new InvalidOperationException(
                        $"Weighted stream ended at vertex {vertex}, influence {influence}.");
                int boneIndex = bones[boneCursor++];
                int matrix = MatrixOffset(matrices, boneIndex);
                float localX = vertices[vertexCursor++];
                float localY = vertices[vertexCursor++];
                float weight = vertices[vertexCursor++];
                x += (matrices[matrix] * localX + matrices[matrix + 1] * localY + matrices[matrix + 4]) * weight;
                y += (matrices[matrix + 2] * localX + matrices[matrix + 3] * localY + matrices[matrix + 5]) * weight;
            }
            world[vertex * 2] = (float)x;
            world[vertex * 2 + 1] = (float)y;
        }
        if (boneCursor != bones.Length || vertexCursor != vertices.Length)
            throw new InvalidOperationException(
                $"Weighted stream mismatch bones={boneCursor}/{bones.Length} vertices={vertexCursor}/{vertices.Length}.");
        return world;
    }

    private static float[] BuildWeightedVerticesFromReference(int[] bones, float[] vertices,
        int worldVerticesLength, float[] originalWorld, float[] canvasUvs, double[] plane, float[] matrices,
        int baseWidth, int baseHeight, int width, int height, float anchorX, float anchorY,
        float defaultAnchorX, float defaultAnchorY, float offsetX, float offsetY)
    {
        if (originalWorld.Length != worldVerticesLength || canvasUvs.Length != worldVerticesLength)
            throw new InvalidOperationException("Weighted reference geometry length mismatch.");
        ValidateBoneMatrices(matrices);
        float[] result = (float[])vertices.Clone();
        int vertexCount = worldVerticesLength / 2;
        int boneCursor = 0;
        int vertexCursor = 0;
        for (int vertex = 0; vertex < vertexCount; vertex++)
        {
            (double desiredX, double desiredY) = DesiredWorld(canvasUvs, vertex, plane,
                baseWidth, baseHeight, width, height, anchorX, anchorY,
                defaultAnchorX, defaultAnchorY, offsetX, offsetY);
            double dx = desiredX - originalWorld[vertex * 2];
            double dy = desiredY - originalWorld[vertex * 2 + 1];
            if (boneCursor >= bones.Length)
                throw new InvalidOperationException($"Weighted bones ended before vertex {vertex}.");
            int influenceCount = bones[boneCursor++];
            for (int influence = 0; influence < influenceCount; influence++)
            {
                if (boneCursor >= bones.Length || vertexCursor + 2 >= vertices.Length)
                    throw new InvalidOperationException(
                        $"Weighted stream ended at vertex {vertex}, influence {influence}.");
                int boneIndex = bones[boneCursor++];
                (double localDx, double localDy) = WorldDeltaToLocal(matrices, boneIndex, dx, dy);
                result[vertexCursor] = vertices[vertexCursor] + (float)localDx;
                result[vertexCursor + 1] = vertices[vertexCursor + 1] + (float)localDy;
                result[vertexCursor + 2] = vertices[vertexCursor + 2];
                vertexCursor += 3;
            }
        }
        if (boneCursor != bones.Length || vertexCursor != vertices.Length)
            throw new InvalidOperationException(
                $"Weighted stream mismatch bones={boneCursor}/{bones.Length} vertices={vertexCursor}/{vertices.Length}.");
        return result;
    }

    private static (double X, double Y) WorldDeltaToLocal(float[] matrices, int boneIndex,
        double dx, double dy)
    {
        int offset = MatrixOffset(matrices, boneIndex);
        double a = matrices[offset];
        double b = matrices[offset + 1];
        double c = matrices[offset + 2];
        double d = matrices[offset + 3];
        double determinant = a * d - b * c;
        if (Math.Abs(determinant) < 1e-10)
            throw new InvalidOperationException($"Setup-pose bone {boneIndex} transform is singular.");
        return ((dx * d - dy * b) / determinant,
                (dy * a - dx * c) / determinant);
    }

    private static void ValidateBoneMatrices(float[] matrices)
    {
        if (matrices.Length == 0 || matrices.Length % 6 != 0)
            throw new InvalidOperationException($"Invalid setup-pose matrix stream length {matrices.Length}.");
        for (int index = 0; index < matrices.Length; index++)
            if (!Finite(matrices[index]))
                throw new InvalidOperationException($"Non-finite setup-pose matrix value at {index}.");
    }

    private static int MatrixOffset(float[] matrices, int boneIndex)
    {
        int offset = boneIndex * 6;
        if (boneIndex < 0 || offset + 5 >= matrices.Length)
            throw new InvalidOperationException(
                $"Setup-pose bone index {boneIndex} is outside {matrices.Length / 6} matrices.");
        return offset;
    }

    private static void ValidatePostState(MeshAttachment mesh, MeshBaseline baseline,
        float[] expandedVertices, float[] expandedRegionUvs, Bounds before, Bounds after,
        int baseWidth, int baseHeight, int width, int height)
    {
        int[] postBones = Copy(mesh.bones);
        int[] postTriangles = Copy(mesh.triangles);
        if (baseline.Weighted != (mesh.bones != null))
            throw new InvalidOperationException("Mesh weighted/unweighted identity changed during expansion.");
        if (mesh.worldVerticesLength != baseline.WorldVerticesLength)
            throw new InvalidOperationException("worldVerticesLength changed during expansion.");
        if ((mesh.vertices?.Length ?? 0) != baseline.Vertices.Length)
            throw new InvalidOperationException("vertices length changed during expansion.");
        if (!Equal(postBones, baseline.Bones))
            throw new InvalidOperationException("bones content changed during expansion.");
        if (!Equal(postTriangles, baseline.Triangles))
            throw new InvalidOperationException("triangles content changed during expansion.");
        if ((mesh.regionUVs?.Length ?? 0) != baseline.RegionUvs.Length ||
            expandedRegionUvs.Length != baseline.RegionUvs.Length)
            throw new InvalidOperationException("regionUVs length changed during expansion.");
        if (Ptr(mesh.deformAttachment) != baseline.DeformAttachmentId)
            throw new InvalidOperationException("deformAttachment identity changed during expansion.");
        if (Ptr(mesh.parentMesh) != baseline.ParentMeshId)
            throw new InvalidOperationException("ParentMesh identity changed during expansion.");
        if (baseline.Weighted && !WeightsEqual(baseline.Bones, baseline.Vertices, expandedVertices))
            throw new InvalidOperationException("A weighted vertex weight changed during expansion.");
        if (width > baseWidth && after.Width <= before.Width + 0.001f)
            throw new InvalidOperationException(
                $"Expanded PNG width did not enlarge world bounds ({before.Width:0.###} -> {after.Width:0.###}).");
        if (height > baseHeight && after.Height <= before.Height + 0.001f)
            throw new InvalidOperationException(
                $"Expanded PNG height did not enlarge world bounds ({before.Height:0.###} -> {after.Height:0.###}).");
    }

    private static float[] ComputeWorldVertices(Skeleton skeleton, Bone slotBone, bool weighted,
        int[] bones, float[] vertices, int worldVerticesLength)
    {
        int vertexCount = worldVerticesLength / 2;
        float[] world = new float[worldVerticesLength];
        if (!weighted)
        {
            for (int vertex = 0; vertex < vertexCount; vertex++)
            {
                float x = vertices[vertex * 2];
                float y = vertices[vertex * 2 + 1];
                world[vertex * 2] = slotBone.a * x + slotBone.b * y + slotBone.worldX;
                world[vertex * 2 + 1] = slotBone.c * x + slotBone.d * y + slotBone.worldY;
            }
            return world;
        }

        int boneCursor = 0;
        int vertexCursor = 0;
        for (int vertex = 0; vertex < vertexCount; vertex++)
        {
            int influenceCount = bones[boneCursor++];
            double x = 0;
            double y = 0;
            for (int influence = 0; influence < influenceCount; influence++)
            {
                int boneIndex = bones[boneCursor++];
                Bone bone = skeleton.Bones.Items[boneIndex];
                float localX = vertices[vertexCursor++];
                float localY = vertices[vertexCursor++];
                float weight = vertices[vertexCursor++];
                x += (bone.a * localX + bone.b * localY + bone.worldX) * weight;
                y += (bone.c * localX + bone.d * localY + bone.worldY) * weight;
            }
            world[vertex * 2] = (float)x;
            world[vertex * 2 + 1] = (float)y;
        }
        if (boneCursor != bones.Length || vertexCursor != vertices.Length)
            throw new InvalidOperationException(
                $"Weighted mesh stream mismatch bones={boneCursor}/{bones.Length} vertices={vertexCursor}/{vertices.Length}.");
        return world;
    }

    private static float[] BuildVertices(Skeleton skeleton, Bone slotBone, MeshBaseline baseline,
        float[] originalWorld, float[] canvasUvs, double[] plane, int baseWidth, int baseHeight, int width, int height,
        float anchorX, float anchorY, float defaultAnchorX, float defaultAnchorY, float offsetX, float offsetY)
    {
        float[] result = (float[])baseline.Vertices.Clone();
        int vertexCount = baseline.WorldVerticesLength / 2;
        if (!baseline.Weighted)
        {
            for (int vertex = 0; vertex < vertexCount; vertex++)
            {
                (double desiredX, double desiredY) = DesiredWorld(canvasUvs, vertex, plane, baseWidth, baseHeight,
                    width, height, anchorX, anchorY, defaultAnchorX, defaultAnchorY, offsetX, offsetY);
                (double localDx, double localDy) = WorldDeltaToLocal(slotBone,
                    desiredX - originalWorld[vertex * 2], desiredY - originalWorld[vertex * 2 + 1]);
                result[vertex * 2] = baseline.Vertices[vertex * 2] + (float)localDx;
                result[vertex * 2 + 1] = baseline.Vertices[vertex * 2 + 1] + (float)localDy;
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
        int baseWidth, int baseHeight, int width, int height, float anchorX, float anchorY,
        float defaultAnchorX, float defaultAnchorY, float offsetX, float offsetY)
    {
        double u = (defaultAnchorX - anchorX + offsetX + canvasUvs[vertex * 2] * width) / baseWidth;
        double v = (defaultAnchorY - anchorY + offsetY + canvasUvs[vertex * 2 + 1] * height) / baseHeight;
        return (plane[0] * u + plane[1] * v + plane[2],
                plane[3] * u + plane[4] * v + plane[5]);
    }

    private static (double X, double Y) WorldDeltaToLocal(Bone bone, double dx, double dy)
    {
        double determinant = bone.a * bone.d - bone.b * bone.c;
        if (Math.Abs(determinant) < 1e-10)
            throw new InvalidOperationException("A required bone transform is singular.");
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
        return new[] { xCoefficients[0], xCoefficients[1], xCoefficients[2],
            yCoefficients[0], yCoefficients[1], yCoefficients[2] };
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
            if (Math.Abs(matrix[best, pivot]) < 1e-12)
                throw new InvalidOperationException("Mesh image plane is degenerate.");
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

    private static float[] BuildCanvasUvs(float[] original, int[] triangles, int vertexCount, int hullVertices)
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

        int[] order = new int[hullVertices];
        double[] cumulative = new double[hullVertices + 1];
        for (int position = 0; position < hullVertices; position++) order[position] = (start + position) % hullVertices;
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
        MapInteriorHarmonically(result, triangles, vertexCount, hullVertices);
        return result;
    }

    private static void MapInteriorHarmonically(float[] result, int[] triangles,
        int vertexCount, int hullVertices)
    {
        int interiorCount = vertexCount - hullVertices;
        if (interiorCount <= 0) return;
        if (triangles.Length == 0 || triangles.Length % 3 != 0)
            throw new InvalidOperationException("Mesh triangles are empty or malformed.");

        var neighbors = new HashSet<int>[vertexCount];
        for (int vertex = 0; vertex < vertexCount; vertex++) neighbors[vertex] = new HashSet<int>();
        for (int offset = 0; offset < triangles.Length; offset += 3)
        {
            int a = triangles[offset];
            int b = triangles[offset + 1];
            int c = triangles[offset + 2];
            if (a < 0 || b < 0 || c < 0 || a >= vertexCount || b >= vertexCount || c >= vertexCount)
                throw new InvalidOperationException(
                    $"Triangle {offset / 3} contains an invalid vertex index ({a},{b},{c}/{vertexCount}).");
            if (a == b || b == c || a == c)
                throw new InvalidOperationException($"Triangle {offset / 3} repeats a vertex index.");
            AddEdge(neighbors, a, b); AddEdge(neighbors, b, c); AddEdge(neighbors, c, a);
        }

        var matrix = new double[interiorCount, interiorCount];
        var rightU = new double[interiorCount];
        var rightV = new double[interiorCount];
        for (int vertex = hullVertices; vertex < vertexCount; vertex++)
        {
            int row = vertex - hullVertices;
            if (neighbors[vertex].Count == 0)
                throw new InvalidOperationException($"Interior vertex {vertex} has no triangle neighbors.");
            matrix[row, row] = neighbors[vertex].Count;
            foreach (int neighbor in neighbors[vertex])
            {
                if (neighbor < hullVertices)
                {
                    rightU[row] += result[neighbor * 2];
                    rightV[row] += result[neighbor * 2 + 1];
                }
                else
                {
                    matrix[row, neighbor - hullVertices] -= 1;
                }
            }
        }

        double[] u = SolveDense(matrix, rightU);
        double[] v = SolveDense(matrix, rightV);
        for (int vertex = hullVertices; vertex < vertexCount; vertex++)
        {
            int index = vertex - hullVertices;
            if (!Finite(u[index]) || !Finite(v[index]) ||
                u[index] < -1e-6 || u[index] > 1 + 1e-6 || v[index] < -1e-6 || v[index] > 1 + 1e-6)
                throw new InvalidOperationException(
                    $"Harmonic UV for vertex {vertex} is invalid ({u[index]},{v[index]}).");
            result[vertex * 2] = (float)Math.Clamp(u[index], 0, 1);
            result[vertex * 2 + 1] = (float)Math.Clamp(v[index], 0, 1);
        }
    }

    private static void AddEdge(HashSet<int>[] neighbors, int first, int second)
    {
        neighbors[first].Add(second);
        neighbors[second].Add(first);
    }

    private static double[] SolveDense(double[,] source, double[] right)
    {
        int count = right.Length;
        var matrix = (double[,])source.Clone();
        var result = (double[])right.Clone();
        for (int pivot = 0; pivot < count; pivot++)
        {
            int best = pivot;
            for (int row = pivot + 1; row < count; row++)
                if (Math.Abs(matrix[row, pivot]) > Math.Abs(matrix[best, pivot])) best = row;
            if (Math.Abs(matrix[best, pivot]) < 1e-12)
                throw new InvalidOperationException("Mesh interior mapping matrix is singular.");
            if (best != pivot)
            {
                for (int column = pivot; column < count; column++)
                    (matrix[pivot, column], matrix[best, column]) =
                        (matrix[best, column], matrix[pivot, column]);
                (result[pivot], result[best]) = (result[best], result[pivot]);
            }

            double divisor = matrix[pivot, pivot];
            for (int column = pivot; column < count; column++) matrix[pivot, column] /= divisor;
            result[pivot] /= divisor;
            for (int row = 0; row < count; row++)
            {
                if (row == pivot) continue;
                double factor = matrix[row, pivot];
                if (factor == 0) continue;
                for (int column = pivot; column < count; column++)
                    matrix[row, column] -= factor * matrix[pivot, column];
                result[row] -= factor * result[pivot];
            }
        }
        return result;
    }

    private static void ValidateTriangleMapping(string domain, float[] baseline, float[] current,
        int[] triangles, int hullVertices, float[] canvasUvs)
    {
        if (baseline.Length != current.Length || baseline.Length != canvasUvs.Length || (baseline.Length & 1) != 0)
            throw new InvalidOperationException($"{domain} triangle validation has mismatched coordinate arrays.");
        if (triangles.Length % 3 != 0)
            throw new InvalidOperationException($"{domain} triangle validation has malformed topology.");

        int vertexCount = baseline.Length / 2;
        double maximumArea = 0;
        for (int offset = 0; offset < triangles.Length; offset += 3)
        {
            ValidateTriangleIndices(triangles, offset, vertexCount, domain);
            maximumArea = Math.Max(maximumArea,
                Math.Max(Math.Abs(TriangleArea(baseline, triangles, offset)),
                    Math.Abs(TriangleArea(current, triangles, offset))));
        }
        double epsilon = Math.Max(1e-10, maximumArea * 1e-6);
        for (int offset = 0; offset < triangles.Length; offset += 3)
        {
            double before = TriangleArea(baseline, triangles, offset);
            double after = TriangleArea(current, triangles, offset);
            bool beforeZero = Math.Abs(before) <= epsilon;
            bool afterZero = Math.Abs(after) <= epsilon;
            bool flattenedBoundary = IsFlattenedBoundaryTriangle(
                triangles, offset, hullVertices, canvasUvs);

            // A boundary-only triangle that maps onto one side of the full-canvas
            // rectangle has an exact target area of zero. Weighted inverse-bone
            // reconstruction can leave a tiny world-space residual on either side
            // of zero, so its sign is not an orientation result. Keep this exception
            // narrowly tied to the proven boundary target and a small relative
            // residual; a material residual or any interior triangle still fails.
            double boundaryResidualTolerance = Math.Max(epsilon * 64.0, Math.Abs(before) * 1e-4);
            bool expectedBoundaryResidual = flattenedBoundary &&
                Math.Abs(after) <= boundaryResidualTolerance;

            if (!beforeZero && !afterZero && Math.Sign(before) != Math.Sign(after) &&
                !expectedBoundaryResidual)
                throw new InvalidOperationException(
                    $"{domain} triangle {offset / 3} changed orientation ({before} -> {after}); " +
                    DescribeTriangle(triangles, offset, hullVertices, baseline, current, canvasUvs));
            if (!beforeZero && afterZero && !flattenedBoundary)
                throw new InvalidOperationException(
                    $"{domain} triangle {offset / 3} became unexpectedly degenerate ({before} -> {after}); " +
                    DescribeTriangle(triangles, offset, hullVertices, baseline, current, canvasUvs));
        }
    }

    private static string DescribeTriangle(int[] triangles, int offset, int hullVertices,
        float[] baseline, float[] current, float[] canvasUvs)
    {
        int a = triangles[offset];
        int b = triangles[offset + 1];
        int c = triangles[offset + 2];
        return $"indices={a},{b},{c} hullVertices={hullVertices} " +
            $"baseline={Point(baseline, a)}|{Point(baseline, b)}|{Point(baseline, c)} " +
            $"current={Point(current, a)}|{Point(current, b)}|{Point(current, c)} " +
            $"canvas={Point(canvasUvs, a)}|{Point(canvasUvs, b)}|{Point(canvasUvs, c)}";
    }

    private static string Point(float[] points, int vertex) =>
        $"({points[vertex * 2]:0.######},{points[vertex * 2 + 1]:0.######})";

    private static void ValidateTriangleIndices(int[] triangles, int offset, int vertexCount, string domain)
    {
        int a = triangles[offset];
        int b = triangles[offset + 1];
        int c = triangles[offset + 2];
        if (a < 0 || b < 0 || c < 0 || a >= vertexCount || b >= vertexCount || c >= vertexCount)
            throw new InvalidOperationException(
                $"{domain} triangle {offset / 3} contains an invalid vertex index ({a},{b},{c}/{vertexCount}).");
    }

    private static double TriangleArea(float[] points, int[] triangles, int offset)
    {
        int a = triangles[offset] * 2;
        int b = triangles[offset + 1] * 2;
        int c = triangles[offset + 2] * 2;
        return ((points[b] - points[a]) * (points[c + 1] - points[a + 1]) -
                (points[b + 1] - points[a + 1]) * (points[c] - points[a])) * 0.5;
    }

    private static bool IsFlattenedBoundaryTriangle(int[] triangles, int offset,
        int hullVertices, float[] canvasUvs)
    {
        int a = triangles[offset];
        int b = triangles[offset + 1];
        int c = triangles[offset + 2];
        if (a >= hullVertices || b >= hullVertices || c >= hullVertices) return false;
        const float tolerance = 1e-6f;
        return SameBoundaryCoordinate(canvasUvs, a, b, c, 0, 0f, tolerance) ||
            SameBoundaryCoordinate(canvasUvs, a, b, c, 0, 1f, tolerance) ||
            SameBoundaryCoordinate(canvasUvs, a, b, c, 1, 0f, tolerance) ||
            SameBoundaryCoordinate(canvasUvs, a, b, c, 1, 1f, tolerance);
    }

    private static bool SameBoundaryCoordinate(float[] points, int a, int b, int c,
        int coordinate, float boundary, float tolerance) =>
        Math.Abs(points[a * 2 + coordinate] - boundary) <= tolerance &&
        Math.Abs(points[b * 2 + coordinate] - boundary) <= tolerance &&
        Math.Abs(points[c * 2 + coordinate] - boundary) <= tolerance;

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

    private static bool WeightsEqual(int[] bones, float[] baseline, float[] post)
    {
        if (baseline.Length != post.Length) return false;
        int boneCursor = 0;
        int vertexCursor = 0;
        while (boneCursor < bones.Length)
        {
            int influences = bones[boneCursor++];
            for (int influence = 0; influence < influences; influence++)
            {
                boneCursor++;
                if (BitConverter.SingleToInt32Bits(baseline[vertexCursor + 2]) !=
                    BitConverter.SingleToInt32Bits(post[vertexCursor + 2])) return false;
                vertexCursor += 3;
            }
        }
        return boneCursor == bones.Length && vertexCursor == baseline.Length;
    }

    private static ulong HashWeights(int[] bones, float[] vertices)
    {
        ulong hash = 1469598103934665603UL;
        int boneCursor = 0;
        int vertexCursor = 0;
        while (boneCursor < bones.Length)
        {
            int influences = bones[boneCursor++];
            for (int influence = 0; influence < influences; influence++)
            {
                boneCursor++;
                hash = Mix(hash, BitConverter.SingleToInt32Bits(vertices[vertexCursor + 2]));
                vertexCursor += 3;
            }
        }
        return hash;
    }

    private static ulong Hash(int[] values)
    {
        ulong hash = 1469598103934665603UL;
        foreach (int value in values) hash = Mix(hash, value);
        return hash;
    }

    private static ulong Mix(ulong hash, int value)
    {
        unchecked
        {
            hash ^= (uint)value;
            hash *= 1099511628211UL;
            return hash;
        }
    }

    private static bool Equal(int[] left, int[] right)
    {
        if (left.Length != right.Length) return false;
        for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
        return true;
    }

    private static bool Equal(float[] left, float[] right)
    {
        if (left.Length != right.Length) return false;
        for (int i = 0; i < left.Length; i++)
            if (BitConverter.SingleToInt32Bits(left[i]) != BitConverter.SingleToInt32Bits(right[i])) return false;
        return true;
    }

    private static int ReadInt(object instance, string name) => Convert.ToInt32(Read(instance, name));
    private static float ReadFloat(object instance, string name) => Convert.ToSingle(Read(instance, name));

    private static object Read(object instance, string name)
    {
        PropertyInfo? property = instance.GetType().GetProperty(name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property == null) throw new MissingMemberException(instance.GetType().FullName, name);
        return property.GetValue(instance) ?? throw new InvalidOperationException($"Placement value {name} is null.");
    }

    private static Il2CppStructArray<float> Floats(float[] values)
    {
        Il2CppStructArray<float> result = new(values.Length);
        for (int index = 0; index < values.Length; index++) result[index] = values[index];
        return result;
    }

    private static float[] Copy(Il2CppStructArray<float>? source)
    {
        if (source == null) return Array.Empty<float>();
        float[] result = new float[source.Length];
        for (int index = 0; index < result.Length; index++) result[index] = source[index];
        return result;
    }

    private static int[] Copy(Il2CppStructArray<int>? source)
    {
        if (source == null) return Array.Empty<int>();
        int[] result = new int[source.Length];
        for (int index = 0; index < result.Length; index++) result[index] = source[index];
        return result;
    }

    private static long Ptr(Attachment? attachment) => attachment?.Pointer.ToInt64() ?? 0;
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private sealed class MeshBaseline
    {
        internal bool Weighted { get; private init; }
        internal int[] Bones { get; private init; } = Array.Empty<int>();
        internal float[] Vertices { get; private init; } = Array.Empty<float>();
        internal float[] RegionUvs { get; private init; } = Array.Empty<float>();
        internal int[] Triangles { get; private init; } = Array.Empty<int>();
        internal int WorldVerticesLength { get; private init; }
        internal int HullLength { get; private init; }
        internal long DeformAttachmentId { get; private init; }
        internal long ParentMeshId { get; private init; }

        internal static MeshBaseline Capture(MeshAttachment mesh)
        {
            int[] bones = Copy(mesh.bones);
            return new MeshBaseline
            {
                Weighted = mesh.bones != null,
                Bones = bones,
                Vertices = Copy(mesh.vertices),
                RegionUvs = Copy(mesh.regionUVs),
                Triangles = Copy(mesh.triangles),
                WorldVerticesLength = mesh.worldVerticesLength,
                HullLength = mesh.hulllength,
                DeformAttachmentId = Ptr(mesh.deformAttachment),
                ParentMeshId = Ptr(mesh.parentMesh)
            };
        }

        internal bool IsCompatible(MeshAttachment mesh) =>
            Weighted == (mesh.bones != null) &&
            WorldVerticesLength == mesh.worldVerticesLength &&
            Vertices.Length == (mesh.vertices?.Length ?? 0) &&
            RegionUvs.Length == (mesh.regionUVs?.Length ?? 0) &&
            Equal(Bones, Copy(mesh.bones)) &&
            Equal(Triangles, Copy(mesh.triangles)) &&
            DeformAttachmentId == Ptr(mesh.deformAttachment) &&
            ParentMeshId == Ptr(mesh.parentMesh);
    }

    private readonly struct Bounds
    {
        internal readonly float MinX;
        internal readonly float MinY;
        internal readonly float MaxX;
        internal readonly float MaxY;
        internal float Width => MaxX - MinX;
        internal float Height => MaxY - MinY;

        private Bounds(float minX, float minY, float maxX, float maxY)
        {
            MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY;
        }

        internal static Bounds From(float[] values)
        {
            if (values.Length < 2) throw new InvalidOperationException("Cannot measure empty world geometry.");
            float minX = values[0], maxX = values[0], minY = values[1], maxY = values[1];
            for (int i = 2; i + 1 < values.Length; i += 2)
            {
                minX = Math.Min(minX, values[i]); maxX = Math.Max(maxX, values[i]);
                minY = Math.Min(minY, values[i + 1]); maxY = Math.Max(maxY, values[i + 1]);
            }
            return new Bounds(minX, minY, maxX, maxY);
        }

        public override string ToString() =>
            $"[{MinX:0.###},{MinY:0.###}]-[{MaxX:0.###},{MaxY:0.###}] size={Width:0.###}x{Height:0.###}";
    }
}
