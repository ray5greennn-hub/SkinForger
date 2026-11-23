using Il2CppSpine;
using MelonLoader;
using SkinForger.Main.Pure;
using UnityEngine;

namespace SkinForger.Main.Unity;

/// <summary>One part of a skeleton, as the Dump sees it. Plain data: no Il2Cpp object is kept past collection.</summary>
/// <summary>
/// Walks a Spine skeleton and reports every textured part the Dump should write.
///
/// The traversal follows what SkinForger.Core already does against the same objects (HexieBlock's skin
/// scan and UntouchedRegionRebase's capture): every skin of the skeleton data, every slot of that skin,
/// every attachment entry; the atlas region comes from the attachment's RendererObject.
///
/// Il2Cpp rules that apply here (SF日志, part 11):
///   - C#'s `is` is always false on an Il2Cpp proxy object: cast with TryCast;
///   - a proxy may be null while the handle is not: check Pointer against IntPtr.Zero.
///
/// Parts left out, deliberately:
///   - censored (hexie) attachments, regions, skins and pages;
///   - attachments with no atlas region, or with region metadata that does not describe a usable
///     rectangle (the same validity rule Core's packed-rect repair applies);
///   - duplicates: one region reached through several skins or slots is one part.
/// </summary>
public static class PartCollector
{
    private const string Prefix = "[SkinForge][Main][Collect]";

    /// <summary>
    /// Every part of the skeleton that belongs to <paramref name="atlasName"/> (the page / atlas the Dump is
    /// writing a folder for), in a stable order: the order the skins and slots declare them, which is what
    /// the file-name numbering follows.
    /// </summary>
    public static List<PartSpec> Collect(Skeleton? skeleton, string atlasName) =>
        PartsOf(Walk(skeleton), atlasName).Select(pair => pair.Part).ToList();

    /// <summary>
    /// Every textured attachment of the skeleton, with its region, in the order the skins and slots declare
    /// them — which is the order the Dump's file numbering follows, so it must stay stable.
    ///
    /// This is the ONE walk of the skeleton. SkinForgerMod.EnumerateAttachmentContexts hands the result to
    /// SkinForger.Core, which may remove entries (censored parts) before anything reads a PNG, so both the
    /// Dump and the replacement path start from the same, already-filtered list.
    /// </summary>
    public static List<AttachmentContext> Walk(Skeleton? skeleton)
    {
        var contexts = new List<AttachmentContext>();
        var seen = new HashSet<(IntPtr Region, IntPtr Attachment)>();
        if (skeleton == null || skeleton.Pointer == IntPtr.Zero) return contexts;

        SkeletonData? data = skeleton.Data;
        if (data == null || data.Pointer == IntPtr.Zero) return contexts;

        int slotCount = data.slots?.Count ?? 0;
        var skins = new List<Skin>();
        if (data.defaultSkin != null) skins.Add(data.defaultSkin);
        for (int i = 0; i < (data.skins?.Count ?? 0); i++)
        {
            Skin? skin = data.skins!.Items[i];
            if (skin != null && skin.Pointer != IntPtr.Zero && !skins.Any(s => s.Pointer == skin.Pointer))
                skins.Add(skin);
        }

        var entries = new Il2CppSystem.Collections.Generic.List<Skin.SkinEntry>();
        foreach (Skin skin in skins)
        {
            if (DumpNaming.IsCensoredName(skin.name)) continue;
            for (int slot = 0; slot < slotCount; slot++)
            {
                entries.Clear();
                try { skin.GetAttachments(slot, entries); }
                catch (Exception exception)
                {
                    MelonLogger.Warning($"{Prefix} slot {slot} of skin {skin.name}: {exception.GetBaseException().Message}");
                    continue;
                }
                for (int e = 0; e < entries.Count; e++)
                {
                    Attachment? attachment = entries[e]?.Attachment;
                    if (attachment == null || attachment.Pointer == IntPtr.Zero) continue;
                    AtlasRegion? region = RegionOf(attachment);
                    AtlasPage? page = region?.page;
                    if (region == null || page == null || page.Pointer == IntPtr.Zero) continue;
                    if (!seen.Add((region.Pointer, attachment.Pointer))) continue;
                    contexts.Add(new AttachmentContext(attachment, region, page.name ?? string.Empty));
                }
            }
        }
        return contexts;
    }

    /// <summary>
    /// The parts of one atlas, taken from a walk's contexts: the same validity and censorship rules as
    /// before (<see cref="Describe"/>), one entry per part identity, first occurrence winning.
    /// </summary>
    public static List<(AttachmentContext Context, PartSpec Part)> PartsOf(
        IEnumerable<AttachmentContext> contexts, string atlasName)
    {
        var parts = new List<(AttachmentContext, PartSpec)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (AttachmentContext context in contexts)
        {
            if (!SamePage(context.Atlas, atlasName)) continue;
            PartSpec? part = Describe(context.Attachment, atlasName);
            if (part == null) continue;
            if (!seen.Add(part.Identity)) continue;
            parts.Add((context, part));
        }
        return parts;
    }

    /// <summary>The atlas region an attachment draws from, or null when it has none.</summary>
    public static AtlasRegion? RegionOf(Attachment? attachment)
    {
        if (attachment == null || attachment.Pointer == IntPtr.Zero) return null;
        AtlasRegion? region = attachment.TryCast<RegionAttachment>()?.RendererObject?.TryCast<AtlasRegion>()
                              ?? attachment.TryCast<MeshAttachment>()?.RendererObject?.TryCast<AtlasRegion>();
        return region != null && region.Pointer != IntPtr.Zero ? region : null;
    }

    /// <summary>Null when the attachment is not a part of this atlas, is censored, or has unusable metadata.</summary>
    public static PartSpec? Describe(Attachment attachment, string atlasName)
    {
        AtlasRegion? region = RegionOf(attachment);
        if (region == null) return null;

        AtlasPage? page = region.page;
        if (page == null || page.Pointer == IntPtr.Zero) return null;
        if (!SamePage(page.name, atlasName)) return null;

        string attachmentName = attachment.Name ?? string.Empty;
        string regionName = region.name ?? attachmentName;
        if (DumpNaming.IsCensoredName(attachmentName) || DumpNaming.IsCensoredName(regionName) ||
            DumpNaming.IsCensoredName(page.name)) return null;

        int pageWidth = page.width, pageHeight = page.height;
        if (pageWidth <= 0 || pageHeight <= 0) return null;
        if (!UsableMetadata(region)) return null;

        PixelRect stored = RegionGeometry.StoredRect(region.u, region.v, region.u2, region.v2, pageWidth, pageHeight);
        // Trimmed to the page, NOT refused for hanging off it. The game's own char_1_rock packs hair_1
        // two pixels past the right edge (u2 = 1.0009766 of a 2048-wide atlas); refusing it dropped that
        // part out of SkinForger entirely, so it was never replaced and -- the part that actually showed
        // -- never rebased when the atlas grew, leaving it to read a 512-tall layout out of a 1176-tall
        // texture. The player saw its hair come apart in a song (2026-11-20).
        stored = stored.ClampedTo(pageWidth, pageHeight);
        if (stored.IsEmpty) return null;

        // A region (quad) attachment carries its own placement, and the anchor is computed from it
        // (RegionGeometry.QuadAnchor). A mesh attachment has none and is anchored by its centre.
        RegionAttachment? quad = attachment.TryCast<RegionAttachment>();

        return new PartSpec(
            Atlas: atlasName,
            AttachmentName: attachmentName,
            RegionName: regionName,
            Stored: stored,
            Rotate: region.rotate,
            Degrees: RegionGeometry.Normalize(region.degrees),
            OriginalWidth: Mathf.RoundToInt(region.originalWidth),
            OriginalHeight: Mathf.RoundToInt(region.originalHeight),
            OffsetX: region.offsetX,
            OffsetY: region.offsetY,
            U: region.u, V: region.v, U2: region.u2, V2: region.v2,
            PageWidth: pageWidth, PageHeight: pageHeight,
            HasQuad: quad != null,
            QuadX: quad?.x ?? 0f,
            QuadY: quad?.y ?? 0f,
            QuadWidth: quad?.width ?? 0f,
            QuadHeight: quad?.height ?? 0f,
            QuadRotation: quad?.rotation ?? 0f);
    }

    /// <summary>
    /// The same validity rule Core's packed-rect repair uses: finite, positive, and the trimmed rectangle
    /// actually inside the untrimmed size. A region failing this cannot be dumped meaningfully.
    /// </summary>
    public static bool UsableMetadata(AtlasRegion region)
    {
        float width = region.width, height = region.height;
        float originalWidth = region.originalWidth, originalHeight = region.originalHeight;
        float offsetX = region.offsetX, offsetY = region.offsetY;
        const float epsilon = 0.01f;
        return float.IsFinite(width) && float.IsFinite(height) &&
               float.IsFinite(originalWidth) && float.IsFinite(originalHeight) &&
               float.IsFinite(offsetX) && float.IsFinite(offsetY) &&
               width > 0f && height > 0f && originalWidth > 0f && originalHeight > 0f &&
               offsetX >= -epsilon && offsetY >= -epsilon &&
               offsetX + width <= originalWidth + epsilon &&
               offsetY + height <= originalHeight + epsilon;
    }

    /// <summary>
    /// Whether an atlas page is the one being dumped. A page name may carry an extension or a path, and the
    /// runtime texture may be named after the atlas instead, so both are compared by their bare name.
    /// </summary>
    public static bool SamePage(string? pageName, string? atlasName)
    {
        if (string.IsNullOrEmpty(pageName) || string.IsNullOrEmpty(atlasName)) return false;
        return string.Equals(BareName(pageName!), BareName(atlasName!), StringComparison.OrdinalIgnoreCase);
    }

    // One definition of a page's bare name, kept in Pure, because part identities are built from it and
    // the two must never drift apart (see RegionManifest.BarePageName).
    private static string BareName(string value) => RegionManifest.BarePageName(value);
}
