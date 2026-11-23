using System;
using System.Collections;
using System.IO;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSpine;
using Il2CppSpine.Unity;
using Il2CppSpine.Unity.AttachmentTools;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SkinForger.Preview;

public static class PreviewPrivateReplacement
{
    private const string Prefix = "[SkinForge][Preview][PrivateReplacement] ";
    private static readonly ArrayList OwnedObjects = new();
    private static readonly ArrayList MeshBindings = new();

    public delegate void MeshGeometryApply(
        Skeleton skeleton,
        bool elfin,
        int slotIndex,
        Attachment attachment,
        object placement,
        string regionPath);

    public static void Apply(SkeletonDataAsset previewAsset, int listIndex)
    {
        try
        {
            ApplyCore(previewAsset, listIndex);
        }
        catch (Exception exception)
        {
            MelonLogger.Error(Prefix + "PREVIEW_REPLACEMENT_LOAD_FAILED error=" + exception);
        }
    }

    public static void Release()
    {
        try
        {
            MeshBindings.Clear();
            for (int i = OwnedObjects.Count - 1; i >= 0; i--)
            {
                Object? value = OwnedObjects[i] as Object;
                if (value != null) Object.Destroy(value);
            }
            OwnedObjects.Clear();
        }
        catch (Exception exception)
        {
            MelonLogger.Warning(Prefix + "RELEASE_FAILED error=" + exception);
            MeshBindings.Clear();
            OwnedObjects.Clear();
        }
    }

    public static void ApplyGeometry(Skeleton skeleton, MeshGeometryApply applyGeometry)
    {
        if (skeleton == null || applyGeometry == null) return;

        int applied = 0;
        for (int i = 0; i < MeshBindings.Count; i++)
        {
            PreviewMeshBinding? binding = MeshBindings[i] as PreviewMeshBinding;
            if (binding == null || binding.Mesh == null || binding.Region == null) continue;

            applyGeometry(skeleton, false, binding.SlotIndex, binding.Mesh, binding.Placement, binding.RegionPath);
            AttachmentRegionExtensions.SetRegion(binding.Mesh, binding.Region, true);
            applied++;
            MelonLogger.Msg(Prefix + "PREVIEW_GEOMETRY_APPLIED attachment=" + binding.AttachmentName +
                " slot=" + binding.SlotIndex + " mesh=0x" + binding.Mesh.Pointer.ToInt64().ToString("X") +
                " canvas=" + binding.Placement.NormalWidth + "x" + binding.Placement.NormalHeight +
                " secondSetRegion=true");
        }

        MelonLogger.Msg(Prefix + "PREVIEW_GEOMETRY_COMPLETE applied=" + applied +
            " bindings=" + MeshBindings.Count);
    }

    private static void ApplyCore(SkeletonDataAsset previewAsset, int listIndex)
    {
        MeshBindings.Clear();
        SkeletonData? data = previewAsset?.GetSkeletonData(false);
        if (data?.skins == null)
        {
            MelonLogger.Warning(Prefix + "PREVIEW_REPLACEMENT_LOAD_FAILED reason=preview-data-unavailable");
            return;
        }

        string skinPath = ResolveSkinPath(listIndex);
        if (string.IsNullOrEmpty(skinPath))
        {
            MelonLogger.Warning(Prefix + "PREVIEW_REPLACEMENT_LOAD_FAILED reason=skin-path-unavailable listIndex=" + listIndex);
            return;
        }

        MelonLogger.Msg(Prefix + "PREVIEW_REPLACEMENT_BEGIN skinPath=" + skinPath);
        var processed = new Hashtable();
        int applied = 0;
        int found = 0;
        var entries = new Il2CppSystem.Collections.Generic.List<Skin.SkinEntry>();
        int slotCount = data.slots?.Count ?? 0;
        for (int skinIndex = 0; skinIndex < data.skins.Count; skinIndex++)
        {
            Skin skin = data.skins.Items[skinIndex];
            for (int slotIndex = 0; slotIndex < slotCount; slotIndex++)
            {
                entries.Clear();
                skin.GetAttachments(slotIndex, entries);
                for (int entryIndex = 0; entryIndex < entries.Count; entryIndex++)
                {
                    Skin.SkinEntry entry = entries[entryIndex];
                    string key = entry.Name ?? string.Empty;
                    found++;
                    Attachment attachment = entry.Attachment;
                    MeshAttachment? mesh = attachment?.TryCast<MeshAttachment>();
                    if (mesh == null)
                    {
                        MelonLogger.Warning(Prefix + "PREVIEW_REPLACEMENT_LOAD_FAILED attachment=" + key +
                            " reason=not-mesh type=" + (attachment == null ? "<null>" : attachment.GetType().FullName));
                        continue;
                    }

                    long attachmentId = mesh.Pointer.ToInt64();
                    if (processed.ContainsKey(attachmentId)) continue;
                    processed[attachmentId] = true;
                    string context = ResolveContextName(mesh);
                    string file = ResolveReplacementPath(skinPath, context, key, out bool exists);
                    MelonLogger.Msg(Prefix + "PREVIEW_CONTEXT_NAME=" + context);
                    MelonLogger.Msg(Prefix + "PREVIEW_REPLACEMENT_PATH attachment=" + key +
                        " path=" + file + " exists=" + exists.ToString().ToLowerInvariant());
                    if (!exists)
                    {
                        MelonLogger.Warning(Prefix + "PREVIEW_REPLACEMENT_FILE_MISSING attachment=" + key + " file=" + file);
                        continue;
                    }

                    if (ApplyMesh(mesh, slotIndex, key, context, file)) applied++;
                }
            }
        }

        MelonLogger.Msg(Prefix + "PREVIEW_REPLACEMENT_APPLIED=" + (applied > 0) +
            " applied=" + applied + " targetEntries=" + found);
        MelonLogger.Msg(Prefix + "PREVIEW_GAMEPLAY_DATA_MUTATED=false");
    }

    private static bool ApplyMesh(
        MeshAttachment mesh,
        int slotIndex,
        string attachmentName,
        string textureName,
        string file)
    {
        Texture2D? texture = null;
        Material? material = null;
        try
        {
            AtlasRegion? originalRegion = mesh.RendererObject?.TryCast<AtlasRegion>();
            Material? originalMaterial = originalRegion?.page?.rendererObject?.TryCast<Material>();
            if (originalRegion == null || originalMaterial == null)
                throw new InvalidOperationException("Preview private source region/material is unavailable");

            string verticesBefore = Floats(mesh.vertices);
            string trianglesBefore = Ints(mesh.triangles);
            string bonesBefore = Ints(mesh.bones);
            int worldVerticesBefore = mesh.worldVerticesLength;
            string regionUvsBefore = Floats(mesh.regionUVs);

            byte[] bytes = File.ReadAllBytes(file);
            texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(texture, (Il2CppStructArray<byte>)bytes, false))
                throw new InvalidDataException("Texture2D.LoadImage returned false");
            if (texture.width < 1 || texture.height < 1 ||
                texture.width > SystemInfo.maxTextureSize || texture.height > SystemInfo.maxTextureSize)
                throw new InvalidDataException("PNG dimensions are unsupported: " + texture.width + "x" + texture.height);
            texture.name = "SkinForger Preview " + Path.GetFileName(file);
            texture.filterMode = originalMaterial.mainTexture?.filterMode ?? FilterMode.Bilinear;
            texture.wrapMode = TextureWrapMode.Clamp;

            material = new Material(originalMaterial)
            {
                name = "SkinForger Preview " + Path.GetFileName(file),
                mainTexture = texture,
                mainTextureScale = Vector2.one,
                mainTextureOffset = Vector2.zero
            };

            int width = texture.width;
            int height = texture.height;
            PreviewPlacement placement = CreatePlacement(
                mesh, originalRegion, textureName, file, width, height);
            var page = new AtlasPage
            {
                name = Path.GetFileName(file),
                width = width,
                height = height,
                rendererObject = material
            };
            var region = new AtlasRegion
            {
                name = attachmentName,
                page = page,
                x = 0,
                y = 0,
                width = width,
                height = height,
                originalWidth = width,
                originalHeight = height,
                offsetX = 0,
                offsetY = 0,
                rotate = false,
                degrees = 0,
                u = 0,
                v = 1,
                u2 = 1,
                v2 = 0
            };

            AttachmentRegionExtensions.SetRegion(mesh, region, true);
            bool topologyPreserved = verticesBefore == Floats(mesh.vertices) &&
                trianglesBefore == Ints(mesh.triangles) && bonesBefore == Ints(mesh.bones) &&
                worldVerticesBefore == mesh.worldVerticesLength && regionUvsBefore == Floats(mesh.regionUVs);
            if (!topologyPreserved) throw new InvalidOperationException("MeshAttachment topology changed during SetRegion");

            OwnedObjects.Add(texture);
            OwnedObjects.Add(material);
            MeshBindings.Add(new PreviewMeshBinding
            {
                Mesh = mesh,
                Region = region,
                Placement = placement,
                SlotIndex = slotIndex,
                AttachmentName = attachmentName,
                RegionPath = file
            });
            MelonLogger.Msg(Prefix + "attachment=" + attachmentName + " file=" + file +
                " textureSize=" + width + "x" + height +
                " originalAttachmentType=MeshAttachment replacementAttachmentType=MeshAttachment" +
                " replacementRegion=0,0," + width + "," + height +
                " replacementTextureInstanceId=" + texture.GetInstanceID() +
                " replacementMaterialInstanceId=" + material.GetInstanceID() +
                " topologyPreserved=" + topologyPreserved + " setRegionUpdateUVs=true");
            MelonLogger.Msg(Prefix + "PREVIEW_REPLACEMENT_APPLIED attachment=" + attachmentName +
                " textureSize=" + width + "x" + height);
            texture = null;
            material = null;
            return true;
        }
        catch (Exception exception)
        {
            MelonLogger.Warning(Prefix + "PREVIEW_REPLACEMENT_LOAD_FAILED attachment=" + attachmentName +
                " file=" + file + " error=" + exception);
            return false;
        }
        finally
        {
            if (material != null) Object.Destroy(material);
            if (texture != null) Object.Destroy(texture);
        }
    }

    private static PreviewPlacement CreatePlacement(
        MeshAttachment mesh,
        AtlasRegion originalRegion,
        string textureName,
        string file,
        int width,
        int height)
    {
        int baseWidth = Math.Max(1, originalRegion.originalWidth > 0
            ? originalRegion.originalWidth
            : originalRegion.width);
        int baseHeight = Math.Max(1, originalRegion.originalHeight > 0
            ? originalRegion.originalHeight
            : originalRegion.height);
        var placement = new PreviewPlacement
        {
            BaseNormalWidth = baseWidth,
            BaseNormalHeight = baseHeight,
            NormalWidth = width,
            NormalHeight = height,
            AnchorX = baseWidth * 0.5f,
            AnchorY = baseHeight * 0.5f,
            DefaultAnchorX = baseWidth * 0.5f,
            DefaultAnchorY = baseHeight * 0.5f
        };

        try
        {
            Type? host = FindHostType();
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            MethodInfo? getAnchor = host?.GetMethod("GetRegionAnchor", flags);
            object? anchor = getAnchor?.Invoke(null,
                new object[] { file, mesh, originalRegion, textureName, baseWidth, baseHeight });
            if (anchor != null)
            {
                placement.AnchorX = ReadFloat(anchor, "AnchorX", placement.AnchorX);
                placement.AnchorY = ReadFloat(anchor, "AnchorY", placement.AnchorY);
                placement.OffsetX = ReadFloat(anchor, "OffsetX", 0f);
                placement.OffsetY = ReadFloat(anchor, "OffsetY", 0f);
                placement.DefaultAnchorX = ReadFloat(anchor, "DefaultAnchorX", placement.DefaultAnchorX);
                placement.DefaultAnchorY = ReadFloat(anchor, "DefaultAnchorY", placement.DefaultAnchorY);
            }
        }
        catch (Exception exception)
        {
            MelonLogger.Warning(Prefix + "PREVIEW_PLACEMENT_ANCHOR_FALLBACK attachment=" + mesh.Name +
                " error=" + exception.GetType().Name + ":" + exception.Message);
        }

        return placement;
    }

    private static float ReadFloat(object instance, string propertyName, float fallback)
    {
        PropertyInfo? property = instance.GetType().GetProperty(propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        object? value = property?.GetValue(instance);
        return value == null ? fallback : Convert.ToSingle(value);
    }

    private static Type? FindHostType()
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? host = assembly.GetType("SkinForger.SkinForgerMod", false);
            if (host != null) return host;
        }
        return null;
    }

    private static string ResolveContextName(MeshAttachment mesh)
    {
        AtlasRegion? region = mesh.RendererObject?.TryCast<AtlasRegion>();
        Material? material = region?.page?.rendererObject?.TryCast<Material>();
        return material?.mainTexture?.name ?? string.Empty;
    }

    private static string ResolveReplacementPath(
        string skinPath,
        string context,
        string attachmentName,
        out bool exists)
    {
        string fileName = Sanitize(attachmentName) + ".png";
        string nested = Path.Combine(skinPath, Sanitize(context), fileName);
        if (File.Exists(nested))
        {
            exists = true;
            return nested;
        }

        string flat = Path.Combine(skinPath, fileName);
        exists = File.Exists(flat);
        return exists ? flat : nested;
    }

    private static string Sanitize(string value)
    {
        string result = value ?? string.Empty;
        foreach (char invalid in Path.GetInvalidFileNameChars()) result = result.Replace(invalid, '_');
        result = result.Replace('/', '_').Replace('\\', '_').Replace(':', '_').Trim();
        return string.IsNullOrEmpty(result) ? "attachment" : result;
    }

    private static string ResolveSkinPath(int listIndex)
    {
        Type? host = FindHostType();
        if (host == null) return string.Empty;
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        MethodInfo? getSelected = host.GetMethod("GetSelected", flags);
        MethodInfo? getSkinPath = host.GetMethod("GetSkinPath", flags);
        if (getSelected == null || getSkinPath == null) return string.Empty;
        object? selectedValue = getSelected.Invoke(null, new object[] { listIndex, false });
        if (selectedValue is not int selected || selected < 0) return string.Empty;
        return getSkinPath.Invoke(null, new object[] { listIndex, selected, false }) as string ?? string.Empty;
    }

    private static string Floats(Il2CppStructArray<float>? values)
    {
        if (values == null) return "<null>";
        var result = new System.Text.StringBuilder(values.Length * 8);
        for (int i = 0; i < values.Length; i++) result.Append(values[i].ToString("R")).Append(';');
        return result.ToString();
    }

    private static string Ints(Il2CppStructArray<int>? values)
    {
        if (values == null) return "<null>";
        var result = new System.Text.StringBuilder(values.Length * 4);
        for (int i = 0; i < values.Length; i++) result.Append(values[i]).Append(';');
        return result.ToString();
    }

    private sealed class PreviewMeshBinding
    {
        internal MeshAttachment Mesh { get; init; } = null!;
        internal AtlasRegion Region { get; init; } = null!;
        internal PreviewPlacement Placement { get; init; } = null!;
        internal int SlotIndex { get; init; }
        internal string AttachmentName { get; init; } = string.Empty;
        internal string RegionPath { get; init; } = string.Empty;
    }

    private sealed class PreviewPlacement
    {
        public int BaseNormalWidth { get; init; }
        public int BaseNormalHeight { get; init; }
        public int NormalWidth { get; init; }
        public int NormalHeight { get; init; }
        public float AnchorX { get; set; }
        public float AnchorY { get; set; }
        public float OffsetX { get; set; }
        public float OffsetY { get; set; }
        public float DefaultAnchorX { get; set; }
        public float DefaultAnchorY { get; set; }
    }

}
