using MelonLoader;

namespace SkinForger.HexieBlock;

/// <summary>
/// Called directly from IL injected into SkinForger.Melon.dll (Main 2026-11-14 hardening). Main therefore
/// references SkinForger.Core and cannot run without it, and its censored-resource checks live here, next to the
/// rest of HexieBlock. Signatures are part of the Main contract: do not rename or change them.
/// </summary>
public static class MainGuard
{
    public const string ContractVersion = "1";

    /// <summary>Called at the start of Main.OnInitializeMelon. Fails if Core's guard is unavailable.</summary>
    public static void Verify()
    {
        MelonLogger.Msg("[SkinForge][HexieBlock] Main guard bound (contract " + ContractVersion + "): Steam release only, censored resources disabled");
    }

    /// <summary>True when a resource / texture / path name is censored.</summary>
    public static bool Name(string? value)
    {
        if (!HexiePolicy.IsHexieName(value)) return false;
        HexiePolicy.Block("main", value!);
        return true;
    }

    /// <summary>True when a prefab resource is censored (name or censored-only config entry).</summary>
    public static bool Resource(string? value)
    {
        if (!HexiePolicy.IsHexieResource(value)) return false;
        HexiePolicy.Block("main-prefab", value!);
        return true;
    }

    /// <summary>True when an attachment is censored (its name, its region, or it exists only in a censored Spine skin).</summary>
    public static bool Attachment(Il2CppSpine.Attachment? attachment)
    {
        if (attachment == null) return false;
        bool hexie = HexieBlockMod.IsCensored(attachment, null);
        if (hexie) HexiePolicy.Block("main-attachment", attachment.Name);
        return hexie;
    }

    /// <summary>True when an attachment / region / texture triple is censored.</summary>
    public static bool Region(Il2CppSpine.Attachment? attachment, Il2CppSpine.AtlasRegion? region, string? textureName)
    {
        bool hexie = HexiePolicy.IsHexieName(textureName) || HexieBlockMod.IsCensored(attachment, region);
        if (hexie) HexiePolicy.Block("main-region", textureName + "/" + attachment?.Name);
        return hexie;
    }
}
