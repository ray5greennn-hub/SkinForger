using System;
using System.Collections;
using System.Globalization;
using System.Text;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSpine;
using Il2CppSpine.Unity;
using Il2CppSpine.Unity.AttachmentTools;
using MelonLoader;
using UnityEngine;

namespace SkinForger.Preview;

public static class PreviewOriginalSpineDataRestore
{
    private const string Prefix = "[SkinForge][Preview][OriginalSpineRestore] ";

    public static void Restore(SkeletonDataAsset previewAsset)
    {
        try
        {
            RestoreCore(previewAsset);
        }
        catch (Exception exception)
        {
            MelonLogger.Error(Prefix + "FAILED: " + exception);
        }
    }

    private static void RestoreCore(SkeletonDataAsset previewAsset)
    {
        SkeletonData? previewData = previewAsset?.GetSkeletonData(false);
        if (previewData == null)
        {
            MelonLogger.Error(Prefix + "FAILED: Preview SkeletonData is unavailable");
            return;
        }

        var pages = ParseAtlasMetadata(previewAsset);
        if (pages.Count == 0)
        {
            MelonLogger.Error(Prefix + "FAILED: original atlas TextAsset metadata is unavailable");
            return;
        }

        var restoredPointers = new Hashtable();
        int attachmentCount = 0;
        int regionCount = 0;
        int missingCount = 0;
        bool geometryUnchanged = true;
        bool regionUvsUnchanged = true;

        if (previewData?.skins != null)
        {
            var entries = new Il2CppSystem.Collections.Generic.List<Skin.SkinEntry>();
            int slotCount = previewData.slots?.Count ?? 0;
            for (int skinIndex = 0; skinIndex < previewData.skins.Count; skinIndex++)
            {
                Skin skin = previewData.skins.Items[skinIndex];
                for (int slotIndex = 0; slotIndex < slotCount; slotIndex++)
                {
                    entries.Clear();
                    skin.GetAttachments(slotIndex, entries);
                    for (int entryIndex = 0; entryIndex < entries.Count; entryIndex++)
                    {
                        Attachment attachment = entries[entryIndex].Attachment;
                        AtlasRegion? region = RegionOf(attachment);
                        if (attachment == null || region == null) continue;
                        attachmentCount++;
                        PageMetadata? pageMetadata = FindPage(pages, region.page?.name);
                        RegionMetadata? metadata = FindRegion(pageMetadata, region.name);
                        if (pageMetadata == null || metadata == null)
                        {
                            missingCount++;
                            MelonLogger.Warning(Prefix + "REGION_METADATA_MISSING name=" + (region.name ?? "<null>") +
                                " page=" + (region.page?.name ?? "<null>"));
                            continue;
                        }

                        long pointer = region.Pointer.ToInt64();
                        if (!restoredPointers.ContainsKey(pointer))
                        {
                            RestoreRegion(region, pageMetadata, metadata);
                            restoredPointers[pointer] = true;
                            regionCount++;
                        }

                        MeshAttachment? mesh = attachment.TryCast<MeshAttachment>();
                        string beforeGeometry = mesh == null ? string.Empty : MeshGeometry(mesh);
                        string beforeRegionUvs = mesh == null ? string.Empty : Floats(mesh.regionUVs);
                        if (mesh != null)
                        {
                            AttachmentRegionExtensions.SetRegion(mesh, region, true);
                        }
                        else
                        {
                            RegionAttachment? quad = attachment.TryCast<RegionAttachment>();
                            if (quad != null) AttachmentRegionExtensions.SetRegion(quad, region, true);
                        }
                        if (mesh != null)
                        {
                            geometryUnchanged &= beforeGeometry == MeshGeometry(mesh);
                            regionUvsUnchanged &= beforeRegionUvs == Floats(mesh.regionUVs);
                        }
                    }
                }
            }
        }

        MelonLogger.Msg(Prefix + "RESTORE_COMPLETE atlasSource=SkeletonDataAsset.atlasAssets/SpineAtlasAsset.atlasFile" +
            " pages=" + pages.Count + " attachments=" + attachmentCount + " regions=" + regionCount +
            " missing=" + missingCount + " meshGeometryUnchanged=" + geometryUnchanged +
            " meshRegionUVsUnchanged=" + regionUvsUnchanged);
        bool ponytail = LogTarget(previewData, "ponytail", 309, false);
        bool hair = LogTarget(previewData, "hair_3", 401, true);
        MelonLogger.Msg(Prefix + "PREVIEW_ORIGINAL_CONTRACT=" + (ponytail && hair));
    }

    private static ArrayList ParseAtlasMetadata(SkeletonDataAsset? asset)
    {
        var pages = new ArrayList();
        if (asset?.atlasAssets == null) return pages;
        for (int i = 0; i < asset.atlasAssets.Length; i++)
        {
            AtlasAssetBase atlasAsset = asset.atlasAssets[i];
            SpineAtlasAsset? spineAtlas = atlasAsset?.TryCast<SpineAtlasAsset>();
            TextAsset? atlasFile = spineAtlas?.atlasFile;
            if (atlasFile == null || string.IsNullOrEmpty(atlasFile.text)) continue;
            ParseAtlasText(atlasFile.text, atlasFile.name, pages);
        }
        return pages;
    }

    private static void ParseAtlasText(string text, string sourceName, ArrayList pages)
    {
        string[] lines = text.Replace("\r", string.Empty).Split('\n');
        PageMetadata? page = null;
        RegionMetadata? region = null;
        bool expectPage = true;
        for (int i = 0; i < lines.Length; i++)
        {
            string raw = lines[i];
            if (string.IsNullOrWhiteSpace(raw))
            {
                page = null;
                region = null;
                expectPage = true;
                continue;
            }

            string line = raw.Trim();
            int separator = line.IndexOf(':');
            if (separator < 0)
            {
                if (expectPage || page == null)
                {
                    page = new PageMetadata { Name = line, SourceName = sourceName };
                    pages.Add(page);
                    region = null;
                    expectPage = false;
                }
                else
                {
                    region = new RegionMetadata { Name = line };
                    page.Regions.Add(region);
                }
                continue;
            }

            if (separator == 0 || page == null) continue;
            string key = line.Substring(0, separator).Trim();
            string value = line.Substring(separator + 1).Trim();
            if (region == null)
            {
                if (key == "size" && Pair(value, out int width, out int height))
                {
                    page.Width = width;
                    page.Height = height;
                }
                continue;
            }

            if (key == "rotate")
            {
                region.Degrees = value.Equals("true", StringComparison.OrdinalIgnoreCase) ? 90 :
                    value.Equals("false", StringComparison.OrdinalIgnoreCase) ? 0 : Int(value);
            }
            else if (key == "xy" && Pair(value, out int x, out int y))
            {
                region.X = x;
                region.Y = y;
            }
            else if (key == "size" && Pair(value, out int width, out int height))
            {
                region.Width = width;
                region.Height = height;
            }
            else if (key == "orig" && Pair(value, out int originalWidth, out int originalHeight))
            {
                region.OriginalWidth = originalWidth;
                region.OriginalHeight = originalHeight;
            }
            else if (key == "offset" && PairFloat(value, out float offsetX, out float offsetY))
            {
                region.OffsetX = offsetX;
                region.OffsetY = offsetY;
            }
        }
    }

    private static void RestoreRegion(AtlasRegion region, PageMetadata pageMetadata, RegionMetadata metadata)
    {
        AtlasPage? page = region.page;
        if (page == null) return;
        page.width = pageMetadata.Width;
        page.height = pageMetadata.Height;
        region.x = metadata.X;
        region.y = metadata.Y;
        region.width = metadata.Width;
        region.height = metadata.Height;
        region.originalWidth = metadata.OriginalWidth > 0 ? metadata.OriginalWidth : metadata.Width;
        region.originalHeight = metadata.OriginalHeight > 0 ? metadata.OriginalHeight : metadata.Height;
        region.offsetX = metadata.OffsetX;
        region.offsetY = metadata.OffsetY;
        region.degrees = NormalizeDegrees(metadata.Degrees);
        region.rotate = region.degrees == 90;

        bool swapsAxes = region.degrees == 90 || region.degrees == 270;
        int packedWidth = swapsAxes ? region.height : region.width;
        int packedHeight = swapsAxes ? region.width : region.height;
        region.u = region.x / (float)page.width;
        region.v = 1f - region.y / (float)page.height;
        region.u2 = (region.x + packedWidth) / (float)page.width;
        region.v2 = 1f - (region.y + packedHeight) / (float)page.height;
    }

    private static bool LogTarget(SkeletonData? data, string target, int expectedY, bool expectedRotate)
    {
        if (data?.skins == null)
        {
            MelonLogger.Warning(Prefix + "VERIFY name=" + target + " missing=true reason=no-skeleton-data");
            return false;
        }

        var entries = new Il2CppSystem.Collections.Generic.List<Skin.SkinEntry>();
        int slots = data.slots?.Count ?? 0;
        for (int skinIndex = 0; skinIndex < data.skins.Count; skinIndex++)
        {
            Skin skin = data.skins.Items[skinIndex];
            for (int slotIndex = 0; slotIndex < slots; slotIndex++)
            {
                entries.Clear();
                skin.GetAttachments(slotIndex, entries);
                for (int i = 0; i < entries.Count; i++)
                {
                    Attachment attachment = entries[i].Attachment;
                    if (attachment == null || !string.Equals(attachment.Name, target, StringComparison.OrdinalIgnoreCase)) continue;
                    AtlasRegion? region = RegionOf(attachment);
                    AtlasPage? page = region?.page;
                    Material? material = page?.rendererObject?.TryCast<Material>();
                    Texture? texture = material?.mainTexture;
                    MeshAttachment? mesh = attachment.TryCast<MeshAttachment>();
                    RegionAttachment? quad = attachment.TryCast<RegionAttachment>();
                    bool contract = region != null && page != null && texture != null &&
                        page.width == texture.width && page.height == texture.height &&
                        page.width == 512 && page.height == 2048 && region.y == expectedY && region.rotate == expectedRotate;
                    MelonLogger.Msg(Prefix + "VERIFY name=" + target + " attachmentType=" +
                        (mesh != null ? "MeshAttachment" : quad != null ? "RegionAttachment" : "Other") +
                        " region=" + Region(region) + " regionUVs=" + Floats(mesh?.regionUVs) +
                        " finalUVs=" + Floats(mesh?.uvs ?? quad?.uvs) +
                        " pageSize=" + (page == null ? "<null>" : page.width + "x" + page.height) +
                        " PREVIEW_TEXTURE_SOURCE=SkeletonDataAsset.atlasAssets/SpineAtlasAsset.atlasFile" +
                        " PREVIEW_TEXTURE_SIZE=" + (texture == null ? "<null>" : texture.width + "x" + texture.height) +
                        " PREVIEW_TEXTURE_INSTANCE_ID=" + (texture == null ? 0 : texture.GetInstanceID()) +
                        " material=" + (material == null ? "<null>" : material.name + "#" + material.GetInstanceID()) +
                        " contract=" + contract);
                    return contract;
                }
            }
        }
        MelonLogger.Warning(Prefix + "VERIFY name=" + target + " missing=true");
        return false;
    }

    private static PageMetadata? FindPage(ArrayList pages, string? pageName)
    {
        foreach (object item in pages)
        {
            var page = (PageMetadata)item;
            if (string.Equals(page.Name, pageName, StringComparison.OrdinalIgnoreCase)) return page;
        }
        return null;
    }

    private static RegionMetadata? FindRegion(PageMetadata? page, string? regionName)
    {
        if (page == null) return null;
        foreach (object item in page.Regions)
        {
            var region = (RegionMetadata)item;
            if (string.Equals(region.Name, regionName, StringComparison.Ordinal)) return region;
        }
        return null;
    }

    private static AtlasRegion? RegionOf(Attachment? attachment) =>
        attachment?.TryCast<IHasRendererObject>()?.RendererObject?.TryCast<AtlasRegion>();

    private static string MeshGeometry(MeshAttachment mesh) =>
        "vertices=" + Floats(mesh.vertices) + ";triangles=" + Ints(mesh.triangles) +
        ";bones=" + Ints(mesh.bones) + ";world=" + mesh.worldVerticesLength;

    private static string Region(AtlasRegion? value) => value == null ? "<null>" :
        (value.name ?? "<null>") + "@0x" + value.Pointer.ToInt64().ToString("X") +
        " xy=" + value.x + "," + value.y + " size=" + value.width + "x" + value.height +
        " original=" + value.originalWidth + "x" + value.originalHeight +
        " offset=" + F(value.offsetX) + "," + F(value.offsetY) +
        " rotate=" + value.rotate + " degrees=" + value.degrees +
        " uv=" + F(value.u) + "," + F(value.v) + "," + F(value.u2) + "," + F(value.v2);

    private static int NormalizeDegrees(int degrees)
    {
        degrees %= 360;
        if (degrees < 0) degrees += 360;
        return degrees == 90 || degrees == 180 || degrees == 270 ? degrees : 0;
    }

    private static bool Pair(string value, out int first, out int second)
    {
        first = 0;
        second = 0;
        string[] parts = value.Split(',');
        return parts.Length >= 2 && int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out first) &&
            int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out second);
    }

    private static bool PairFloat(string value, out float first, out float second)
    {
        first = 0f;
        second = 0f;
        string[] parts = value.Split(',');
        return parts.Length >= 2 && float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out first) &&
            float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out second);
    }

    private static int Int(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ? result : 0;
    private static string F(float value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Floats(Il2CppStructArray<float>? values)
    {
        if (values == null) return "<null>";
        var text = new StringBuilder("len=").Append(values.Length).Append('[');
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0) text.Append(',');
            text.Append(F(values[i]));
        }
        return text.Append(']').ToString();
    }

    private static string Ints(Il2CppStructArray<int>? values)
    {
        if (values == null) return "<null>";
        var text = new StringBuilder("len=").Append(values.Length).Append('[');
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0) text.Append(',');
            text.Append(values[i]);
        }
        return text.Append(']').ToString();
    }

    private sealed class PageMetadata
    {
        public string Name = string.Empty;
        public string SourceName = string.Empty;
        public int Width;
        public int Height;
        public readonly ArrayList Regions = new();
    }

    private sealed class RegionMetadata
    {
        public string Name = string.Empty;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public int OriginalWidth;
        public int OriginalHeight;
        public float OffsetX;
        public float OffsetY;
        public int Degrees;
    }
}
