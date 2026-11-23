using Il2CppSpine;
using SkinForger.Main.Pure;

namespace SkinForger;

/// <summary>
/// One textured attachment of a skeleton, with the atlas region it draws from: the live Spine objects,
/// unlike <see cref="PartSpec"/>, which is the same part read off as plain data.
///
/// This type exists because SkinForger.Core works on the LIVE list. HexieBlock filters censored parts out
/// of it before anything looks up a PNG (it reads the <c>Attachment</c> and <c>Region</c> properties by
/// name and removes entries), so the list every replacement walks has to be the one Core can edit — see
/// SkinForgerMod.EnumerateAttachmentContexts.
/// </summary>
public sealed class AttachmentContext
{
    public AttachmentContext(Attachment attachment, AtlasRegion region, string atlas)
    {
        Attachment = attachment;
        Region = region;
        Atlas = atlas;
        Identity = RegionManifest.PartIdentity(atlas, region.name ?? string.Empty, attachment.Name ?? string.Empty);
    }

    /// <summary>The attachment. Read by name from SkinForger.Core — do not rename.</summary>
    public Attachment Attachment { get; }

    /// <summary>The atlas region it draws from. Read by name from SkinForger.Core — do not rename.</summary>
    public AtlasRegion Region { get; }

    /// <summary>The atlas page's name.</summary>
    public string Atlas { get; }

    /// <summary>atlas|region|attachment — the part identity the manifest and every skin index use.</summary>
    public string Identity { get; }

    public override string ToString() => Identity;
}
