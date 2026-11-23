using Il2CppSpine;
using System.Linq;
using MelonLoader;
using SkinForger.Main.Pure;
using UnityEngine;

namespace SkinForger.Main.Unity;

/// <summary>What one atlas's dump produced.</summary>
public sealed record DumpResult(string Costume, string Atlas, int Parts, int Written, string Folder, List<string> Problems);

/// <summary>
/// The authored Dump, end to end: read the atlas, plan every part, write PNG + sidecar + manifest.
///
/// While the port is in progress it writes into a SHADOW root (SkinCreator\ImagesAuthored\&lt;Costume&gt;),
/// never into the creator's own SkinCreator\Images. Nothing the player or a skin creator owns can be
/// touched by code that has not been accepted yet, and the two outputs can be compared part by part
/// (SF日志\SF切割SkinChanger计划.md, stage 3). Only once a comparison comes back clean does the real
/// folder become the target.
/// </summary>
public static class DumpSession
{
    private const string Prefix = "[SkinForge][Main][Dump]";

    /// <summary>Shadow output folder while the port is in progress.</summary>
    public const string ShadowFolder = "ImagesAuthored";

    public static string ShadowRoot(MainLayout layout) => Path.Combine(layout.CreatorRoot, ShadowFolder);

    /// <summary>
    /// Dumps one atlas of one costume. <paramref name="atlasTexture"/> is the texture the skeleton actually
    /// draws from; pass the ORIGINAL atlas (a dump must never read SkinForger's own composed atlas, which is
    /// what Core's DumpIsolation makes sure of on the old path).
    /// </summary>
    public static DumpResult Run(Skeleton? skeleton, Texture2D? atlasTexture, string costume, string atlas,
        MainLayout layout, bool shadow = true)
    {
        using IDisposable scope = SkinForger.CharacterReplacement.ReplacementExecutionContext.Begin(preview: false, "dump");

        var clock = System.Diagnostics.Stopwatch.StartNew();   // so the Dump speed is a number, not a feeling
        var problems = new List<string>();
        string root = shadow ? ShadowRoot(layout) : layout.Images;
        string folder = Path.Combine(root, costume);

        if (DumpNaming.IsCensoredName(costume) || DumpNaming.IsCensoredName(atlas))
            return new DumpResult(costume, atlas, 0, 0, folder, new List<string> { "censored costume or atlas" });

        List<PartSpec> parts = PartCollector.Collect(skeleton, atlas);
        if (parts.Count == 0)
        {
            problems.Add("no part of this skeleton belongs to the atlas");
            return new DumpResult(costume, atlas, 0, 0, folder, problems);
        }

        // The GAME's atlas, never one SkinForger composed. The skeleton on screen can still be bound to a
        // composed page while the Dump runs -- Core's DumpIsolation sets the skin to Original, but the live
        // renderer keeps its material override until the next sweep. 2026-11-21: with the regions already
        // put back to the game's coordinates and the texture still the composed 2048x2240 one, 52 parts
        // reported a page mismatch and the 42 that were written came out of the wrong picture.
        // AtlasOwnershipManager remembers which original each runtime atlas was built from.
        Texture2D? original = SkinForger.AtlasOwnershipFix.AtlasOwnershipManager.ResolveSource(atlasTexture);
        if (original != null && atlasTexture != null && original.GetInstanceID() != atlasTexture.GetInstanceID())
        {
            MelonLogger.Msg($"{Prefix} {costume}/{atlas}: reading the game's own {original.width}x{original.height} atlas, " +
                            $"not the composed {atlasTexture.width}x{atlasTexture.height} one");
            atlasTexture = original;
        }

        byte[]? pixels = TextureBridge.ToRgba(atlasTexture, out int atlasWidth, out int atlasHeight);
        if (pixels == null)
        {
            problems.Add("the atlas texture could not be read");
            return new DumpResult(costume, atlas, parts.Count, 0, folder, problems);
        }

        // The regions must speak the same coordinates as the texture just read. When they do not, the
        // atlas was composed and nothing has put it back yet, so the Dump puts it back itself and asks
        // the skeleton again -- 2026-11-21, twice: first the texture was the composed one, then the page
        // was. Writing 42 pictures out of the wrong place is worse than taking a moment to be sure.
        if (parts.Any(part => part.PageWidth != atlasWidth || part.PageHeight != atlasHeight))
        {
            int put = RuntimeAtlas.PutRegionsBack();
            if (put > 0)
            {
                MelonLogger.Msg($"{Prefix} {costume}/{atlas}: the regions still pointed at a composed atlas; " +
                                $"{put} region(s) put back before reading");
                parts = PartCollector.Collect(skeleton, atlas);
            }
        }

        // A part whose rectangle was computed against a different page size cannot be cut out of this
        // texture: the atlas has grown or been replaced. Report it instead of writing a wrong image.
        foreach (PartSpec part in parts.Where(p => p.PageWidth != atlasWidth || p.PageHeight != atlasHeight))
            problems.Add($"{part.AttachmentName}: region belongs to a {part.PageWidth}x{part.PageHeight} page, " +
                         $"the texture is {atlasWidth}x{atlasHeight}");

        List<PlannedPart> planned = DumpPlan.ForAtlas(parts, pixels, atlasWidth, atlasHeight,
            RegionGeometry.AnchorOf, (part, reason) => problems.Add($"{part.AttachmentName}: {reason}"));

        int written = 0;
        Directory.CreateDirectory(folder);
        // A frame-by-frame folder keeps every anchor in one anchors.json (the author's rule, 2026-11-20).
        // The entries are collected here and written in a single pass at the end: hundreds of small files,
        // or hundreds of rewrites of one file, is the slowest thing a Dump can do.
        string atlasFolder = Path.Combine(folder, Path.GetDirectoryName(planned.Count > 0 ? planned[0].RelativePath : string.Empty) ?? string.Empty);
        bool oneFile = SkinFiles.Exists(Path.Combine(atlasFolder, FolderAnchorFile.FileName)) ||
                       DumpNaming.IsFrameByFrameAtlas(atlas) ||
                       (SkinForgerMod.FolderKeepsAnchorsInOneFile?.Invoke(atlasFolder) ?? false);
        var folderEntries = new List<(string Key, System.Text.Json.Nodes.JsonObject Entry)>();
        int kept = 0, folded = 0;
        foreach (PlannedPart part in planned)
        {
            string png = Path.Combine(folder, part.RelativePath);

            // A Dump FILLS IN what is missing. It never writes over a picture that is already there,
            // because that picture is the author's drawing: the old Main's ExtractAttachment did exactly
            // this ("if (File.Exists(text)) { WriteAnchorMetadataIfMissing(...); return; }") and the
            // authored Dump wrote unconditionally, so 2026-11-21 a Dump of Bassist destroyed hours of
            // painting. The anchor record is still filled in for an image that has none, which is the one
            // thing the old Dump also did on this path.
            if (File.Exists(png))
            {
                kept++;
                if (oneFile && !File.Exists(RegionAnchorFile.PathOf(png)) &&
                    FolderAnchorFile.ReadEntry(png) == null)
                    folderEntries.Add((Path.GetFileNameWithoutExtension(png),
                        FolderAnchorFile.EntryForDump(part.Anchor.AnchorX, part.Anchor.AnchorY, part.Anchor.Rotation)));
                else if (!oneFile && !File.Exists(RegionAnchorFile.PathOf(png)))
                    part.Anchor.Write(png);
                continue;
            }

            if (!TextureBridge.SavePng(png, part.Image, part.Width, part.Height))
            {
                problems.Add($"{part.RelativePath}: the PNG could not be written");
                continue;
            }
            if (oneFile && !File.Exists(RegionAnchorFile.PathOf(png)))
                folderEntries.Add((Path.GetFileNameWithoutExtension(png),
                    FolderAnchorFile.EntryForDump(part.Anchor.AnchorX, part.Anchor.AnchorY, part.Anchor.Rotation)));
            else part.Anchor.Write(png);
            written++;
        }
        // A folder that should keep one file, but has per-image JSONs from an earlier dump, is folded in
        // here: the entries move into anchors.json and the stray files go. Values are carried over as they
        // stand, so anything the author changed in one of them survives the move.
        if (oneFile)
        {
            foreach (PlannedPart part in planned)
            {
                string png = Path.Combine(folder, part.RelativePath);
                string stray = RegionAnchorFile.PathOf(png);
                if (!File.Exists(stray)) continue;
                string key = Path.GetFileNameWithoutExtension(png);
                try
                {
                    if (System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(stray)) is System.Text.Json.Nodes.JsonObject entry)
                    {
                        entry.Remove("version");
                        entry.Remove("origin");
                        folderEntries.RemoveAll(existing => existing.Key == key);
                        folderEntries.Add((key, entry));
                    }
                    File.Delete(stray);
                    folded++;
                }
                catch (Exception exception)
                {
                    problems.Add($"{key}.json could not be folded into {FolderAnchorFile.FileName}: {exception.GetBaseException().Message}");
                }
            }
        }

        if (folderEntries.Count > 0) FolderAnchorFile.WriteEntries(atlasFolder, folderEntries);
        if (folded > 0)
            MelonLogger.Msg($"{Prefix} {costume}/{atlas}: {folded} per-image anchor file(s) folded into {FolderAnchorFile.FileName}");
        if (kept > 0) problems.Add($"{kept} image(s) were already there and were left alone (a Dump never overwrites your drawings)");

        string manifestPath = Path.Combine(folder, RegionManifest.FileName);
        RegionManifest manifest = DumpPlan.Manifest(planned, ReadManifest(manifestPath, problems));
        List<RegionManifestEntry> stale = DumpPlan.Stale(manifest, planned);
        foreach (RegionManifestEntry entry in stale)
            problems.Add($"{entry.File}: in the manifest but no longer in the game's data");
        manifest.Write(manifestPath);

        // The first few problems, in the log. A bare count ("72 problem(s)") says something is wrong and
        // nothing about what, which cost a whole round trip on 2026-11-21.
        foreach (string problem in problems.Take(8)) MelonLogger.Warning($"{Prefix} {costume}/{atlas}: {problem}");
        if (problems.Count > 8) MelonLogger.Warning($"{Prefix} {costume}/{atlas}: and {problems.Count - 8} more problem(s)");

        MelonLogger.Msg($"{Prefix} {costume}/{atlas}: {written} written, {kept} kept, of {parts.Count} parts in {clock.ElapsedMilliseconds} ms -> {folder}" +
                        (problems.Count > 0 ? $"  ({problems.Count} problem(s))" : string.Empty));
        return new DumpResult(costume, atlas, parts.Count, written, folder, problems);
    }

    /// <summary>
    /// Compares a shadow dump of one atlas with what the old Dump left in SkinCreator\Images, part by part.
    /// Nothing is written; the report goes to the log and is returned.
    /// </summary>
    public static string Compare(Skeleton? skeleton, Texture2D? atlasTexture, string costume, string atlas,
        MainLayout layout, int tolerance = 0)
    {
        List<PartSpec> parts = PartCollector.Collect(skeleton, atlas);
        byte[]? pixels = TextureBridge.ToRgba(atlasTexture, out int atlasWidth, out int atlasHeight);
        if (pixels == null) return $"{Prefix} {costume}/{atlas}: the atlas texture could not be read";

        List<PlannedPart> planned = DumpPlan.ForAtlas(parts, pixels, atlasWidth, atlasHeight, RegionGeometry.AnchorOf);
        string oldFolder = Path.Combine(layout.Images, costume);

        var differences = new List<DumpDifference>();
        string manifestPath = Path.Combine(oldFolder, RegionManifest.FileName);
        if (SkinFiles.Exists(manifestPath))
        {
            var problems = new List<string>();
            RegionManifest? old = ReadManifest(manifestPath, problems);
            if (old != null)
                differences.AddRange(DumpComparison.Manifests(old, DumpPlan.Manifest(planned), new[] { atlas }));
        }
        else differences.Add(new DumpDifference("missing", costume, "the old dump has no manifest.json"));

        differences.AddRange(DumpComparison.Files(planned, oldFolder, TextureBridge.LoadPng, tolerance));

        string report = $"{Prefix} {costume}/{atlas} vs the old dump: {DumpComparison.Report(differences)}";
        if (DumpComparison.IsClean(differences)) MelonLogger.Msg(report);
        else MelonLogger.Warning(report);
        return report;
    }

    private static RegionManifest? ReadManifest(string path, List<string> problems)
    {
        if (!SkinFiles.Exists(path)) return null;
        try { return RegionManifest.Read(path); }
        catch (Exception exception)
        {
            problems.Add($"{RegionManifest.FileName} could not be read ({exception.GetBaseException().Message}); a new one is written");
            return null;
        }
    }
}
