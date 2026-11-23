using System.Reflection;
using HarmonyLib;
using Il2CppSpine;
using MelonLoader;
using UnityEngine;

namespace SkinForger.SkinPackages;

/// <summary>
/// Character Gameplay: rebases the UVs of attachments that have NO replacement PNG after Main grew the atlas.
///
/// Main's synthesized atlas keeps the original atlas pixels at the same bottom-origin rows and grows the texture
/// (EnsureAtlasCapacity) to make room for replacement images. Main recomputes UVs only for regions it placed, so
/// every untouched region kept UVs normalized to the ORIGINAL page size and sampled unrelated rows of the bigger
/// atlas. A skin that has a PNG for every part never showed it (the October 197-image Bassist dump); a skin with
/// missing parts did (2026-11-14: 129-image dump -> angry / shy / yawn face parts drawn as fragments), and so would
/// every edited-only .sfskin package.
///
/// This mirrors the Preview runtime's verified PreviewSharedCore.RebaseUntouched: capture each region's packed pixel
/// rect before the build, and after it, for regions Main did not touch, recompute region x/y/u/v for the final atlas
/// size and refresh the attachments' UVs (geometry unchanged). OriginalStateRestore records every region and
/// attachment first, so Original restores them as before. Preview and Elfin builds are not touched.
/// </summary>
internal static class UntouchedRegionRebase
{
    private const string Prefix = "[SkinForge][UntouchedRebase]";

    private sealed class RegionCapture
    {
        internal AtlasRegion Region = null!;
        internal int PageWidth, PageHeight, PackedX, PackedY, PackedWidth, PackedHeight;
        internal float U, V, U2, V2;
        internal readonly List<Attachment> Attachments = new();
    }

    private sealed class BuildCapture
    {
        internal string TextureName = string.Empty;
        internal string SamplePage = string.Empty;
        internal readonly Dictionary<IntPtr, RegionCapture> Regions = new();
    }

    private static readonly Stack<BuildCapture?> Builds = new();
    private static readonly HashSet<string> Logged = new();

    internal static int Install(HarmonyLib.Harmony harmony)
    {
        Type host = AccessTools.TypeByName("SkinForger.SkinForgerMod") ?? throw new TypeLoadException("SkinForger.SkinForgerMod");
        MethodInfo build = AccessTools.DeclaredMethod(host, "TryBuildSynthesizedAtlasForSkeleton")
                           ?? throw new MissingMethodException("TryBuildSynthesizedAtlasForSkeleton");
        // Prefix first (before warm reuse restores placements); finalizer first (while OriginalStateRestore's build
        // context is still open, so its Capture() records what is changed here).
        harmony.Patch(build,
            prefix: new HarmonyMethod(typeof(UntouchedRegionRebase), nameof(BuildPrefix)) { priority = HarmonyLib.Priority.First },
            finalizer: new HarmonyMethod(typeof(UntouchedRegionRebase), nameof(BuildFinalizer)) { priority = HarmonyLib.Priority.First });
        return 1;
    }

    private static void BuildPrefix(Skeleton skeleton, string textureName, bool elfin)
    {
        BuildCapture? capture = null;
        try
        {
            if (!elfin && skeleton != null && skeleton.Pointer != IntPtr.Zero && !string.IsNullOrEmpty(textureName))
                capture = Capture(skeleton, textureName);
        }
        catch (Exception exception)
        {
            LogOnce("capture", $"{Prefix} capture skipped: {exception.GetBaseException().Message}");
        }
        Builds.Push(capture);
    }

    private static Exception? BuildFinalizer(Texture2D? __result, Exception? __exception)
    {
        BuildCapture? capture = Builds.Count > 0 ? Builds.Pop() : null;
        if (capture == null || __result == null || __exception != null) return __exception;
        try
        {
            // OriginalStateRestore is recording only for Character Gameplay builds (not Preview, not Elfin).
            if (!global::SkinForger.OriginalStateRestore.OriginalStateRuntime.CaptureActive) return __exception;
            Rebase(capture, __result);
        }
        catch (Exception exception)
        {
            LogOnce("rebase", $"{Prefix} rebase failed: {exception.GetBaseException().Message}");
        }
        return __exception;
    }

    private static BuildCapture Capture(Skeleton skeleton, string textureName)
    {
        var capture = new BuildCapture { TextureName = textureName };
        SkeletonData? data = skeleton.Data;
        if (data == null) return capture;
        int slotCount = data.slots?.Count ?? 0;
        var entries = new Il2CppSystem.Collections.Generic.List<Skin.SkinEntry>();
        var skins = new List<Skin>();
        if (data.defaultSkin != null) skins.Add(data.defaultSkin);
        for (int i = 0; i < (data.skins?.Count ?? 0); i++)
        {
            Skin? skin = data.skins!.Items[i];
            if (skin != null && !skins.Any(s => s.Pointer == skin.Pointer)) skins.Add(skin);
        }
        foreach (Skin skin in skins)
            for (int slot = 0; slot < slotCount; slot++)
            {
                entries.Clear();
                skin.GetAttachments(slot, entries);
                for (int e = 0; e < entries.Count; e++)
                {
                    Attachment? attachment = entries[e]?.Attachment;
                    AtlasRegion? region = RegionOf(attachment);
                    if (attachment == null || region == null || region.Pointer == IntPtr.Zero || region.page == null) continue;
                    if (capture.SamplePage.Length == 0) capture.SamplePage = region.page.name ?? string.Empty;
                    if (!SamePage(region.page.name, textureName)) continue;
                    if (!capture.Regions.TryGetValue(region.Pointer, out RegionCapture? entry))
                    {
                        int width = region.page.width, height = region.page.height;
                        if (width <= 0 || height <= 0) continue;
                        int left = Round(Math.Min(region.u, region.u2) * width), right = Round(Math.Max(region.u, region.u2) * width);
                        int bottom = Round(Math.Min(region.v, region.v2) * height), top = Round(Math.Max(region.v, region.v2) * height);
                        entry = new RegionCapture
                        {
                            Region = region,
                            PageWidth = width, PageHeight = height,
                            PackedX = left, PackedY = bottom,
                            PackedWidth = Math.Max(1, right - left), PackedHeight = Math.Max(1, top - bottom),
                            U = region.u, V = region.v, U2 = region.u2, V2 = region.v2
                        };
                        capture.Regions.Add(region.Pointer, entry);
                    }
                    if (!entry.Attachments.Any(a => a.Pointer == attachment.Pointer)) entry.Attachments.Add(attachment);
                }
            }
        return capture;
    }

    private static void Rebase(BuildCapture capture, Texture2D atlas)
    {
        int width = atlas.width, height = atlas.height, rebased = 0, placed = 0;
        if (capture.Regions.Count == 0)
        {
            LogOnce("nomatch|" + capture.TextureName, $"{Prefix} atlas={capture.TextureName}: no region matched (first page seen: {capture.SamplePage})");
            return;
        }
        foreach (RegionCapture entry in capture.Regions.Values)
        {
            AtlasRegion region = entry.Region;
            if (region.Pointer == IntPtr.Zero) continue;
            bool untouched = region.u == entry.U && region.v == entry.V && region.u2 == entry.U2 && region.v2 == entry.V2;
            if (!untouched) { placed++; continue; }                                   // Main placed a replacement here
            if (entry.PageWidth == width && entry.PageHeight == height) continue;     // atlas did not grow (or already rebased)

            // Record the original state first, so Original / reload restore these too.
            global::SkinForger.OriginalStateRestore.OriginalStateRuntime.Capture(region);
            foreach (Attachment attachment in entry.Attachments)
                global::SkinForger.OriginalStateRestore.OriginalStateRuntime.Capture(attachment, region);

            region.x = entry.PackedX;
            region.y = height - entry.PackedY - entry.PackedHeight;
            region.u = (float)entry.PackedX / width;
            region.u2 = (float)(entry.PackedX + entry.PackedWidth) / width;
            region.v = (float)(entry.PackedY + entry.PackedHeight) / height;
            region.v2 = (float)entry.PackedY / height;
            if (region.page != null) { region.page.width = width; region.page.height = height; }
            foreach (Attachment attachment in entry.Attachments) RefreshAttachmentUvs(attachment, region);
            rebased++;
        }
        if (rebased > 0)
            LogOnce($"{capture.TextureName}|{width}x{height}|{rebased}",
                $"{Prefix} atlas={capture.TextureName} {width}x{height}: {rebased} untouched region(s) rebased, {placed} replaced");
    }

    /// <summary>Same as the Preview runtime's RefreshAttachmentUvs: UVs only, geometry untouched.</summary>
    private static void RefreshAttachmentUvs(Attachment attachment, AtlasRegion region)
    {
        MeshAttachment? mesh = attachment.TryCast<MeshAttachment>();
        if (mesh != null)
        {
            mesh.RegionU = region.u;
            mesh.RegionV = region.v;
            mesh.RegionU2 = region.u2;
            mesh.RegionV2 = region.v2;
            mesh.RegionRotate = region.rotate;
            mesh.RegionDegrees = region.degrees;
            mesh.UpdateUVs();
            return;
        }
        attachment.TryCast<RegionAttachment>()?.SetUVs(region.u, region.v, region.u2, region.v2, region.rotate);
    }

    private static AtlasRegion? RegionOf(Attachment? attachment)
    {
        if (attachment == null) return null;
        Il2CppSystem.Object? renderer = attachment.TryCast<MeshAttachment>()?.RendererObject ?? attachment.TryCast<RegionAttachment>()?.RendererObject;
        return renderer?.TryCast<AtlasRegion>();
    }

    private static bool SamePage(string? pageName, string textureName) =>
        !string.IsNullOrEmpty(pageName) &&
        string.Equals(Path.GetFileNameWithoutExtension(pageName), Path.GetFileNameWithoutExtension(textureName), StringComparison.OrdinalIgnoreCase);

    private static int Round(float value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    private static void LogOnce(string key, string message)
    {
        if (Logged.Add(key) && Logged.Count < 200) MelonLogger.Msg(message);
    }
}
