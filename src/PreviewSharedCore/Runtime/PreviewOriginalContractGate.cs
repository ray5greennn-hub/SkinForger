using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppSpine;
using Il2CppSpine.Unity;
using MelonLoader;
using UnityEngine;

namespace SkinForger.Preview;

public static class PreviewSharedCoreOriginalContractGate
{
    private const string Prefix = "[SkinForge][Preview][OriginalContractGate] ";

    public static bool ShouldRestore(SkeletonDataAsset previewAsset)
    {
        try
        {
            SkeletonData? data = previewAsset?.GetSkeletonData(false);
            if (data?.skins == null)
            {
                MelonLogger.Warning(Prefix + "restoreRequired=true reason=skeleton-data-unavailable");
                return true;
            }

            var entries = new Il2CppSystem.Collections.Generic.List<Skin.SkinEntry>();
            var pages = new HashSet<long>();
            var attachments = new HashSet<long>();
            int pageTextureMismatches = 0;
            int invalidRegionUvs = 0;
            int invalidAttachmentUvs = 0;
            string firstInvalid = string.Empty;
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
                        Attachment attachment = entries[entryIndex].Attachment;
                        if (attachment == null || !attachments.Add(attachment.Pointer.ToInt64())) continue;
                        AtlasRegion? region = attachment.TryCast<IHasRendererObject>()?.RendererObject?.TryCast<AtlasRegion>();
                        AtlasPage? page = region?.page;
                        Material? material = page?.rendererObject?.TryCast<Material>();
                        Texture? texture = material?.mainTexture;
                        if (region == null || page == null || texture == null)
                        {
                            pageTextureMismatches++;
                            if (string.IsNullOrEmpty(firstInvalid)) firstInvalid = attachment.Name ?? "<unnamed>";
                            continue;
                        }

                        if (pages.Add(page.Pointer.ToInt64()) &&
                            (page.width != texture.width || page.height != texture.height))
                        {
                            pageTextureMismatches++;
                            if (string.IsNullOrEmpty(firstInvalid)) firstInvalid = attachment.Name ?? "<unnamed>";
                        }

                        if (!PreviewSharedCoreOriginalContractDecision.IsFiniteUv(region.u) ||
                            !PreviewSharedCoreOriginalContractDecision.IsFiniteUv(region.v) ||
                            !PreviewSharedCoreOriginalContractDecision.IsFiniteUv(region.u2) ||
                            !PreviewSharedCoreOriginalContractDecision.IsFiniteUv(region.v2))
                        {
                            invalidRegionUvs++;
                            if (string.IsNullOrEmpty(firstInvalid)) firstInvalid = attachment.Name ?? "<unnamed>";
                        }

                        MeshAttachment? mesh = attachment.TryCast<MeshAttachment>();
                        RegionAttachment? quad = attachment.TryCast<RegionAttachment>();
                        if (!FiniteValues(mesh?.uvs ?? quad?.uvs))
                        {
                            invalidAttachmentUvs++;
                            if (string.IsNullOrEmpty(firstInvalid)) firstInvalid = attachment.Name ?? "<unnamed>";
                        }
                    }
                }
            }

            bool restoreRequired = PreviewSharedCoreOriginalContractDecision.ShouldRestore(
                pageTextureMismatches,
                invalidRegionUvs,
                invalidAttachmentUvs);
            MelonLogger.Msg(Prefix + "attachments=" + attachments.Count +
                " pages=" + pages.Count +
                " pageTextureMismatches=" + pageTextureMismatches +
                " invalidRegionUvs=" + invalidRegionUvs +
                " invalidAttachmentUvs=" + invalidAttachmentUvs +
                " firstInvalid=" + (string.IsNullOrEmpty(firstInvalid) ? "<none>" : firstInvalid) +
                " restoreRequired=" + restoreRequired +
                " restoreSkipped=" + (!restoreRequired));
            return restoreRequired;
        }
        catch (Exception exception)
        {
            MelonLogger.Warning(Prefix + "restoreRequired=true reason=gate-exception error=" + exception);
            return true;
        }
    }

    private static bool FiniteValues(Il2CppStructArray<float>? values)
    {
        if (values == null) return true;
        for (int index = 0; index < values.Length; index++)
            if (!PreviewSharedCoreOriginalContractDecision.IsFiniteUv(values[index])) return false;
        return true;
    }
}
