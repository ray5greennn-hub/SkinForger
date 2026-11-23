using Il2CppSpine;
using MelonLoader;
using SkinForger.Main.Pure;
using UnityEngine;

namespace SkinForger.Main.Unity;

/// <summary>What applying a skin to one atlas did.</summary>
public sealed record AtlasApplied(string Atlas, int Replaced, int Rebased, int Width, int Height, List<string> Problems);

/// <summary>
/// Puts a skin on screen: compose the runtime atlas, hand it to the renderer, and point every region at
/// its new place in it.
///
/// This is the part of SkinForger that has broken in the most ways, so every step here answers a specific
/// entry in SF日志\SF角色扩框.txt part 4 (禁止事项):
///
///   * the composition reads the ORIGINAL atlas only, never a composed one (otherwise the atlas grows
///     without end: 2048 -> 2738 -> 3428 -> 4118 on 2026-10-01);
///   * every replacement gets a fresh rectangle of its own (the game packs by polygon, so original
///     rectangles overlap: hand_right lost 1196 pixels to hair_4 on 2026-10-04);
///   * the composed texture is UNCOMPRESSED and LINEAR. That is not only correctness: the mesh-canvas
///     expansion recognises "this mesh is drawing from SkinForger's atlas" by exactly that, because the
///     game's own atlases are BC7;
///   * region GEOMETRY is never touched. Only the atlas region's UV rectangle moves, and the attachments
///     are refreshed through the one path that recomputes their UVs without rewriting regionUVs, vertices,
///     triangles or weights — MeshAttachment.RegionU/V/U2/V2 + UpdateUVs(), RegionAttachment.SetUVs();
///   * regions that no skin replaces are REBASED, because the atlas grew underneath them and their old
///     UVs were fractions of the old height.
///
/// Still open, and the reason this is not deployed: an AtlasRegion belongs to the skeleton DATA, which two
/// renderers of the same character share. Pointing a region at the composed atlas therefore points both of
/// them at it. The authored Main will own this through the AtlasOwnershipFix runtime (authored source
/// already exists, CONTRACT.md lists it), which binds the composed texture per renderer instead of writing
/// it back into the shared page material — the mistake that caused the 2026-10-01 recursion.
/// </summary>
public static class RuntimeAtlas
{
    private const string Prefix = "[SkinForge][Main][Atlas]";

    /// <summary>
    /// Every atlas region SkinForger has rewritten, with the numbers the GAME gave it.
    ///
    /// A region lives in the shared SkeletonData and outlives any renderer, so the uvs written into it
    /// stay there after a Reload destroys the composed texture. 2026-11-21: that is exactly what the
    /// author hit -- he deleted a skin folder, pressed Reload, and the character came apart. Forget() put
    /// the quads back and dropped the textures, but the regions still addressed a composed atlas that no
    /// longer existed, so every part sampled the original texture at composed coordinates. Undoing the
    /// region changes was the old build's OriginalStateRestore, which the authored Main does not compile
    /// in; this is that job, done where the changes are made.
    /// </summary>
    private readonly record struct PristineRegion(
        float U, float V, float U2, float V2, int X, int Y, bool Rotate, int Degrees,
        int Width, int Height, int OriginalWidth, int OriginalHeight, float OffsetX, float OffsetY);

    private static readonly Dictionary<IntPtr, (AtlasRegion Region, PristineRegion Was)> Pristine = new();

    /// <summary>
    /// The attachments that draw from each remembered region. An attachment keeps its OWN copy of the uvs
    /// (MeshAttachment.uvs, RegionAttachment.uvs), so putting the region's numbers back is only half of
    /// it -- without this the attachments would go on drawing the composed coordinates.
    /// </summary>
    private static readonly Dictionary<IntPtr, List<Attachment>> PristineUsers = new();

    /// <summary>
    /// The size each atlas PAGE had before SkinForger grew it, remembered once per page.
    ///
    /// 2026-11-21, and this is why the first version of the restore did not work: a page is ONE object
    /// shared by every region on it, and the size is written inside the per-part loop. Remembering it
    /// alongside each region captured 1024 for the first part and the already-grown 2264 for all the
    /// others, so the restore wrote 2264 straight back. The Dump then read the game's own 2048x1024
    /// texture while the regions insisted they lived on a 2048x2264 page -- 52 parts refused, 42 came out
    /// of the wrong place. One entry per page, taken before anything is changed.
    /// </summary>
    private static readonly Dictionary<IntPtr, (AtlasPage Page, int Width, int Height)> PristinePages = new();

    /// <summary>Writes down what a region looked like before SkinForger changed it. The first call wins.</summary>
    private static void RememberRegion(AtlasRegion? region, IEnumerable<Attachment>? users = null)
    {
        if (region == null || region.Pointer == IntPtr.Zero) return;
        if (users != null)
        {
            if (!PristineUsers.TryGetValue(region.Pointer, out List<Attachment>? list))
                PristineUsers[region.Pointer] = list = new List<Attachment>();
            foreach (Attachment user in users)
                if (user != null && !list.Any(known => known.Pointer == user.Pointer)) list.Add(user);
        }
        try
        {
            AtlasPage? page = region.page;
            if (page != null && page.Pointer != IntPtr.Zero && !PristinePages.ContainsKey(page.Pointer))
                PristinePages[page.Pointer] = (page, page.width, page.height);
        }
        catch (Exception) { }

        if (Pristine.ContainsKey(region.Pointer)) return;
        try
        {
            Pristine[region.Pointer] = (region, new PristineRegion(
                region.u, region.v, region.u2, region.v2, region.x, region.y,
                region.rotate, region.degrees, region.width, region.height,
                region.originalWidth, region.originalHeight, region.offsetX, region.offsetY));
        }
        catch (Exception) { /* a region whose page is gone cannot be remembered, and need not be */ }
    }

    /// <summary>
    /// Puts every region back to the game's own numbers, for a caller that needs the game's own art right
    /// now -- the Dump. It must read the game's atlas with the game's coordinates, and whether a Reload
    /// happened to run first is not something it should have to depend on.
    /// </summary>
    public static int PutRegionsBack() => RestoreRegions();

    /// <summary>Puts every region SkinForger rewrote back to the game's own numbers.</summary>
    private static int RestoreRegions()
    {
        int restored = 0;
        foreach ((AtlasRegion region, PristineRegion was) in Pristine.Values)
        {
            try
            {
                if (region == null || region.Pointer == IntPtr.Zero) continue;
                region.u = was.U;
                region.v = was.V;
                region.u2 = was.U2;
                region.v2 = was.V2;
                region.x = was.X;
                region.y = was.Y;
                region.rotate = was.Rotate;
                region.degrees = was.Degrees;
                region.width = was.Width;
                region.height = was.Height;
                region.originalWidth = was.OriginalWidth;
                region.originalHeight = was.OriginalHeight;
                region.offsetX = was.OffsetX;
                region.offsetY = was.OffsetY;
                // and the attachments that cached those uvs
                if (PristineUsers.TryGetValue(region.Pointer, out List<Attachment>? users))
                    foreach (Attachment user in users)
                    {
                        try
                        {
                            if (user != null && user.Pointer != IntPtr.Zero) RefreshUvs(user, region);
                        }
                        catch (Exception) { }
                    }
                restored++;
            }
            catch (Exception) { }
        }
        foreach ((AtlasPage page, int width, int height) in PristinePages.Values)
        {
            try
            {
                if (page == null || page.Pointer == IntPtr.Zero) continue;
                page.width = width;
                page.height = height;
            }
            catch (Exception) { }
        }
        Pristine.Clear();
        PristineUsers.Clear();
        PristinePages.Clear();
        return restored;
    }

    /// <summary>Composed atlases by renderer instance id and atlas name, so a rebuild reuses nothing stale.</summary>
    private static readonly Dictionary<string, Texture2D> Composed = new(StringComparer.Ordinal);

    /// <summary>
    /// The composed atlas of one renderer and one page, or null when there is none. This is what
    /// SkinForgerMod.TryBuildSynthesizedAtlasForSkeleton returns to SkinForger.Core.
    /// </summary>
    public static Texture2D? Composed_Of(int instanceId, string atlasName) =>
        Composed.TryGetValue(instanceId + "|" + atlasName, out Texture2D? texture) &&
        texture != null && texture.Pointer != IntPtr.Zero
            ? texture
            : null;

    /// <summary>
    /// One composed page, shared by every skeleton that draws from it.
    ///
    /// It MUST be shared. Two skeletons that use the same atlas page also share its AtlasRegion objects,
    /// and a region holds one set of UVs. Composing per renderer gave each one its own layout, and the
    /// atlas height depends on which parts were in it, so the same part came out at a different v
    /// (measured: 0.45 against a 120-tall atlas, 0.389 against a 108-tall one). Whichever skeleton wrote
    /// last won, and every other renderer was left sampling its own texture at the wrong height. That is
    /// the shape of "only some parts look wrong".
    /// </summary>
    private sealed record Layout(
        Texture2D Texture,
        Dictionary<string, PlacedReplacement> Placed,
        Dictionary<string, string> Files,
        Dictionary<string, (float X, float Y)> Anchors,
        Dictionary<string, (float X, float Y)> Offsets,
        Dictionary<string, float> Turns,
        Dictionary<string, (float X, float Y)> Defaults,
        Dictionary<string, string> CanvasAnchors,
        Dictionary<string, float> Scales);

    private static readonly Dictionary<string, Layout> Layouts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The canvas size each region had before SkinForger first touched it, by region pointer.
    ///
    /// It has to be remembered: after one apply the region carries OUR numbers, so measuring it again on
    /// the next apply would compare the replacement against itself and the part would creep.
    /// </summary>
    private static readonly Dictionary<IntPtr, (int Width, int Height)> PristineCanvas = new();

    /// <summary>
    /// The x / y / rotation each QUAD attachment had before SkinForger first moved it, by attachment
    /// pointer. The author's nudge is applied FROM these, never added to what the attachment currently
    /// says: a skin is applied again for every renderer and every scene, so adding would walk the part
    /// further off each time.
    /// </summary>
    private static readonly Dictionary<IntPtr, (RegionAttachment Quad, float X, float Y, float Rotation,
        float Width, float Height, float ScaleX, float ScaleY)> PristineQuad = new();

    /// <summary>
    /// Remembers every quad attachment of a part as the GAME has it, before anything is moved.
    ///
    /// It runs before the anchors are read, and that order is the point. A quad's anchor is computed FROM
    /// its x / y / rotation (RegionGeometry.QuadAnchor), and PartCollector reads those off the live
    /// attachment. Once the author's nudge has been written into it, a second apply -- another renderer,
    /// another scene, and the elfin alone is applied four times in one session -- would read the moved
    /// values back as if they were the game's, and the canvas placement would shift by the nudge again
    /// every time. Measuring the game's anchor from here instead keeps it the same on every apply.
    /// </summary>
    private static void RememberQuads(IEnumerable<Attachment> attachments)
    {
        foreach (Attachment attachment in attachments)
        {
            if (attachment == null || attachment.Pointer == IntPtr.Zero) continue;
            if (PristineQuad.ContainsKey(attachment.Pointer)) continue;
            RegionAttachment? quad = attachment.TryCast<RegionAttachment>();
            if (quad == null) continue;
            PristineQuad[attachment.Pointer] =
                (quad, quad.x, quad.y, quad.rotation, quad.width, quad.height, quad.scaleX, quad.scaleY);
        }
    }

    /// <summary>
    /// The placement the GAME gave a quad attachment, when SkinForger has seen it before anything moved
    /// it. SkinForgerMod.GetRegionAnchor asks here, so the default anchor it answers is the same on every
    /// apply instead of drifting by the author's nudge each time.
    /// </summary>
    internal static bool TryPristineQuad(Attachment? attachment,
        out (float X, float Y, float Rotation) placement)
    {
        placement = default;
        if (attachment == null || attachment.Pointer == IntPtr.Zero) return false;
        if (!PristineQuad.TryGetValue(attachment.Pointer,
                out (RegionAttachment Quad, float X, float Y, float Rotation,
                     float Width, float Height, float ScaleX, float ScaleY) known)) return false;
        placement = (known.X, known.Y, known.Rotation);
        return true;
    }

    /// <summary>
    /// The part's anchor in a canvas of the given size, measured from the placement the GAME gave it.
    /// For a mesh this is the canvas centre, exactly as <see cref="RegionGeometry.AnchorOf"/> says; for a
    /// quad it is the bone origin, taken from the remembered pristine placement rather than from whatever
    /// the attachment says now.
    /// </summary>
    private static (float X, float Y) GameAnchorOf(List<Attachment> attachments, PartSpec part,
        int canvasWidth, int canvasHeight)
    {
        foreach (Attachment attachment in attachments)
        {
            if (!PristineQuad.TryGetValue(attachment.Pointer,
                    out (RegionAttachment Quad, float X, float Y, float Rotation,
                         float Width, float Height, float ScaleX, float ScaleY) pristine)) continue;
            return RegionGeometry.QuadAnchor(pristine.X, pristine.Y,
                pristine.Width, pristine.Height, pristine.Rotation, canvasWidth, canvasHeight);
        }
        return RegionGeometry.AnchorOf(part, canvasWidth, canvasHeight);
    }

    /// <summary>
    /// Whether an expanded canvas can be described to SkinForger.Core's mesh canvas at all: the game's
    /// canvas has to sit INSIDE the author's picture, either where the sidecar's anchor puts it or
    /// centred. This mirrors Core's CanvasSpec.Problem(), including its 0.51 px tolerance, so Main and
    /// Core agree about which parts can be expanded.
    /// </summary>
    /// <summary>
    /// The slot an attachment is shown in, or -1. Found by walking the skeleton's slots, because an
    /// attachment does not know its own slot.
    /// </summary>
    private static int SlotIndexOf(Skeleton skeleton, Attachment attachment)
    {
        try
        {
            int count = skeleton.Slots?.Count ?? 0;
            for (int slot = 0; slot < count; slot++)
                if (skeleton.Slots!.Items[slot]?.Attachment?.Pointer == attachment.Pointer) return slot;
        }
        catch (Exception) { }
        return -1;
    }

    /// <summary>
    /// Whether the game's canvas can be placed inside the author's picture at all -- by the sidecar's
    /// anchor, or failing that centred.
    ///
    /// <paramref name="defaultAnchor"/> is the anchor the part has in the GAME's canvas
    /// (<see cref="RegionGeometry.AnchorOf"/>): the centre for a mesh, the bone origin for a quad. It is
    /// taken as an argument rather than worked out here so this and <see cref="RegionGeometry.CanvasStart"/>
    /// cannot answer differently -- a second copy of the rule, which read the centre for everything, is
    /// how the elfin's quads were judged undescribable on 2026-11-20.
    /// </summary>
    private static bool CanvasDescribable(PixelRect placed, int canvasWidth, int canvasHeight,
        (float X, float Y)? anchor, (float X, float Y) defaultAnchor)
    {
        if (canvasWidth <= 0 || canvasHeight <= 0) return false;

        if (anchor != null &&
            RegionGeometry.Inside(anchor.Value.X - defaultAnchor.X, anchor.Value.Y - defaultAnchor.Y,
                                  canvasWidth, canvasHeight, placed.Width, placed.Height))
            return true;

        return RegionGeometry.Inside((placed.Width - canvasWidth) / 2.0, (placed.Height - canvasHeight) / 2.0,
                                     canvasWidth, canvasHeight, placed.Width, placed.Height);
    }

    /// <summary>One line per part for the notes that would otherwise repeat on every apply.</summary>
    private static readonly HashSet<string> Logged = new(StringComparer.Ordinal);

    /// <summary>The canvas a region started with, learning it the first time it is seen.</summary>
    private static (int Width, int Height) CanvasOf(AtlasRegion region)
    {
        if (PristineCanvas.TryGetValue(region.Pointer, out (int Width, int Height) known)) return known;
        var measured = (region.originalWidth, region.originalHeight);
        PristineCanvas[region.Pointer] = measured;
        return measured;
    }

    /// <summary>Drops every composed texture (a Reload, or going back to the game's own art).</summary>
    public static void Forget()
    {
        SkinForger.AtlasOwnershipFix.AtlasOwnershipManager.InvalidateAll();
        int regions = RestoreRegions();
        if (regions > 0) MelonLogger.Msg($"{Prefix} {regions} region(s) put back to the game's own coordinates");
        // Destroy from Layouts, not from Composed: a composed page is now SHARED, so Composed holds the
        // same texture once per renderer and destroying it from there would destroy it several times.
        foreach (Layout layout in Layouts.Values)
            if (layout.Texture != null && layout.Texture.Pointer != IntPtr.Zero)
                UnityEngine.Object.Destroy(layout.Texture);
        Layouts.Clear();
        Composed.Clear();
        // Region pointers are reused after a scene change, so a remembered canvas must not outlive one.
        PristineCanvas.Clear();
        // Put every moved quad back first. Spine attachments live in the shared SkeletonData and outlive a
        // scene, so a nudge written into one STAYS there; clearing the memory of it without undoing it
        // would make the next apply read the moved placement as the game's and nudge it again from there.
        foreach ((RegionAttachment quad, float x, float y, float rotation,
                  float quadWidth, float quadHeight, float scaleX, float scaleY) in PristineQuad.Values)
        {
            try
            {
                if (quad == null || quad.Pointer == IntPtr.Zero) continue;
                if (quad.x == x && quad.y == y && quad.rotation == rotation &&
                    quad.width == quadWidth && quad.height == quadHeight &&
                    quad.scaleX == scaleX && quad.scaleY == scaleY) continue;
                quad.x = x;
                quad.y = y;
                quad.rotation = rotation;
                quad.width = quadWidth;
                quad.height = quadHeight;
                quad.scaleX = scaleX;
                quad.scaleY = scaleY;
                quad.UpdateOffset();
            }
            catch (Exception) { /* the attachment's skeleton is gone: nothing to put back */ }
        }
        PristineQuad.Clear();
    }

    /// <summary>
    /// Applies a skin to one atlas of one skeleton.
    /// <paramref name="originalAtlas"/> must be the GAME's atlas, never one SkinForger composed before.
    /// <paramref name="images"/> maps part identity to the PNG that replaces it — the answer already
    /// resolved, for callers that have no folder (the Preview).
    /// <paramref name="skinDirectory"/> is the skin folder being applied; when it is given, each part's
    /// file is asked for through SkinForgerMod.GetExistingRegionPath instead, which is the seam
    /// SkinForger.Core redirects to mounted packages.
    /// </summary>
    /// <param name="elfin">
    /// Whether this skin belongs to an elfin. Passed straight through to the geometry step, which is where
    /// SkinForger.Core's mesh canvas learns it -- it used to refuse elfins outright.
    /// </param>
    public static AtlasApplied Apply(Skeleton skeleton, Texture2D originalAtlas, string atlasName,
        IReadOnlyDictionary<string, string> images, int instanceId, string skinDirectory = "",
        bool elfin = false)
    {
        // Four Core modules ask ReplacementExecutionContext.Current?.Preview while this runs, to tell a
        // gameplay build from a Preview one. Marking the work is part of the contract, not bookkeeping.
        using IDisposable scope = SkinForger.CharacterReplacement.ReplacementExecutionContext.Begin(preview: false, "apply");

        var problems = new List<string>();

        // ONE walk of the skeleton, through the seam: HexieBlock removes censored entries from this list
        // before anything below reads a PNG, so the Dump and the replacement path agree on what exists.
        List<AttachmentContext> contexts = SkinForgerMod.EnumerateAttachmentContexts(skeleton);
        List<(AttachmentContext Context, PartSpec Part)> parts = PartCollector.PartsOf(contexts, atlasName);

        // Before ANY anchor is asked for. SkinForgerMod.GetRegionAnchor measures a quad's default anchor
        // from the placement the game gave it, and this is where that placement is learned; asking first
        // and remembering afterwards would mean the second apply measured from a moved attachment.
        RememberQuads(parts.Select(entry => entry.Context.Attachment));

        if (parts.Count == 0)
            return new AtlasApplied(atlasName, 0, 0, 0, 0, new List<string> { "no part of this skeleton belongs to the atlas" });

        // Ownership. ResolveSource walks back to the texture the game shipped, so a second apply composes
        // from the ORIGINAL again instead of from what SkinForger built last time — the rule that keeps the
        // atlas from growing without end (2026-10-01: 2048 -> 2738 -> 3428 -> 4118).
        SkinForger.AtlasOwnershipFix.AtlasOwnershipManager.BeginSkeleton(skeleton);
        Texture2D source = SkinForger.AtlasOwnershipFix.AtlasOwnershipManager.ResolveSource(originalAtlas) ?? originalAtlas;

        byte[]? original = TextureBridge.ToRgba(source, out int width, out int height);
        if (original == null)
            return new AtlasApplied(atlasName, 0, 0, 0, 0, new List<string> { "the game's atlas could not be read" });

        // ---- 1. the images that actually replace something here
        var replacements = new List<Replacement>();
        var anchors = new Dictionary<string, (float X, float Y)>(StringComparer.Ordinal);
        // The author's own nudge and turn for this part, from the same sidecar as the anchor. They move
        // the WHOLE part rigidly, and the two kinds of part are moved by different hands:
        //
        //   * a MESH is moved by SkinForger.Core's PartTransforms, which reads OffsetX / OffsetY off the
        //     placement Main hands over and transforms the deformed vertices every frame;
        //   * a QUAD is moved by Main itself -- Core says so where it declines to ("region parts:
        //     unchanged (Main moves region quads by offset itself)"). The authored Main never did, so
        //     every hand-placed quad sat where the game put it: the elfin's head carries (-18, 30) and
        //     its fork (70, 20), and 15 of the player's parts carry one.
        var offsets = new Dictionary<string, (float X, float Y)>(StringComparer.Ordinal);
        var turns = new Dictionary<string, float>(StringComparer.Ordinal);
        // The DEFAULT anchor, as SkinForgerMod.GetRegionAnchor answered it. Kept beside the anchor itself
        // because Core's postfixes may move either one, and a default worked out again later would not be
        // the one the anchor was measured against.
        var defaults = new Dictionary<string, (float X, float Y)>(StringComparer.Ordinal);
        // The author's own word for which side they added the room to, when they have given one. It is
        // the only thing that settles a part the author REDREW: there is no original left in the picture
        // to find, and their anchor may not place the canvas.
        var corners = new Dictionary<string, string>(StringComparer.Ordinal);
        var scales = new Dictionary<string, float>(StringComparer.Ordinal);
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        int composedWidth = width, composedHeight = height;
        foreach ((AttachmentContext context, PartSpec part) in parts)
        {
            // Which file replaces this part. With a skin folder the question goes through the seam, so
            // Core's package mounts and its "untouched image of another skin" rule both apply; without
            // one (the Preview) the caller has already resolved it.
            string? file = skinDirectory.Length > 0
                ? SkinForgerMod.GetExistingRegionPath(skinDirectory, context.Attachment, context.Region, atlasName)
                : null;
            if (file == null && !images.TryGetValue(part.Identity, out file)) continue;
            if (string.IsNullOrEmpty(file)) continue;

            // Through the per-region seam, never straight to GetTexture: HexieBlock refuses censored
            // regions in a prefix there, and OffsetCanvas reads the part's identity from its arguments so
            // it can pad the image when the anchor puts the original outside the canvas.
            if (!SkinForgerMod.ApplyRegionPngToAtlas(source, context.Attachment, context.Region,
                    atlasName, file, part.Identity, replacements))
            {
                problems.Add($"{part.AttachmentName}: {Path.GetFileName(file)} was not used");
                continue;
            }

            // The part's own anchor, if its sidecar records one. This is what positions a replacement
            // whose canvas is bigger than the game's: 23 of the player's 29 expanded parts are NOT
            // centred, so without it every one of them would be moved to the middle of its canvas.
            found[part.Identity] = file;

            // THROUGH THE SEAM, never straight from the file.
            //
            // SkinForger.Core says what this method is for, where it patches it: "Postfix on Main's
            // GetRegionAnchor (every consumer -- region attachments, R4-12 mesh canvas, ElfinMeshCanvas --
            // takes its anchor from there)". Two modules hang off it:
            //
            //   * AutoAnchor, whose whole job is the case the elfin is in -- "enlarging the canvas never
            //     moves the anchor". It finds the ORIGINAL dumped image (replaced\<name>.png) inside the
            //     author's bigger picture and answers default + that offset, which is the right placement
            //     measured from the pixels rather than guessed;
            //   * OffsetCanvas, which pads a mesh's picture in memory when the author's nudge would push
            //     the game's canvas off it, and moves the anchor by the margin it added.
            //
            // The authored Main declared this method for Core to patch and then never called it, reading
            // the sidecar itself instead. Both modules were therefore dead, and the four elfin parts whose
            // anchors AutoAnchor exists to work out were left on stale values and centred (2026-11-20).
            (int anchorCanvasW, int anchorCanvasH) = CanvasOf(context.Region);
            SkinForgerMod.RegionAnchor placement = SkinForgerMod.GetRegionAnchor(
                file, context.Attachment, context.Region, atlasName, anchorCanvasW, anchorCanvasH);
            // Only a REAL anchor goes in. Without one, a quad keeps the placement the game gave it
            // exactly, instead of being recomputed from a default and drifting by a rounding step.
            if (placement.FromSidecar ||
                placement.AnchorX != placement.DefaultAnchorX || placement.AnchorY != placement.DefaultAnchorY)
                anchors[part.Identity] = (placement.AnchorX, placement.AnchorY);
            defaults[part.Identity] = (placement.DefaultAnchorX, placement.DefaultAnchorY);
            scales[part.Identity] = placement.Scale;
            offsets[part.Identity] = (placement.OffsetX, placement.OffsetY);
            turns[part.Identity] = placement.Rotation;
            RegionAnchorFile? sidecar = RegionAnchorFile.ReadIfExists(file);
            if (!string.IsNullOrWhiteSpace(sidecar?.CanvasAnchor))
                corners[part.Identity] = sidecar!.CanvasAnchor!;
        }
        if (replacements.Count == 0)
            return new AtlasApplied(atlasName, 0, 0, width, height, problems);

        // ---- 2. one layout per skin and page, shared by every skeleton
        //
        // The key is the SKIN, not the renderer. Reusing it is what keeps the shared regions and the
        // texture everyone samples describing the same thing; it also means the second and third renderer
        // of a character cost nothing to dress.
        string key = (skinDirectory.Length > 0 ? skinDirectory : instanceId.ToString()) + "|" + atlasName;
        Layouts.TryGetValue(key, out Layout? cached);

        // Everything this skin is known to put on this page: what earlier skeletons found, plus what this
        // one found. A later skeleton may use a part the first one did not, and the layout has to contain
        // it or that part could not be replaced at all.
        var files = cached == null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(cached.Files, StringComparer.Ordinal);
        var allAnchors = cached == null
            ? new Dictionary<string, (float X, float Y)>(StringComparer.Ordinal)
            : new Dictionary<string, (float X, float Y)>(cached.Anchors, StringComparer.Ordinal);
        var allOffsets = cached == null
            ? new Dictionary<string, (float X, float Y)>(StringComparer.Ordinal)
            : new Dictionary<string, (float X, float Y)>(cached.Offsets, StringComparer.Ordinal);
        var allTurns = cached == null
            ? new Dictionary<string, float>(StringComparer.Ordinal)
            : new Dictionary<string, float>(cached.Turns, StringComparer.Ordinal);
        var allDefaults = cached == null
            ? new Dictionary<string, (float X, float Y)>(StringComparer.Ordinal)
            : new Dictionary<string, (float X, float Y)>(cached.Defaults, StringComparer.Ordinal);
        var allCorners = cached == null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(cached.CanvasAnchors, StringComparer.Ordinal);
        var allScales = cached == null
            ? new Dictionary<string, float>(StringComparer.Ordinal)
            : new Dictionary<string, float>(cached.Scales, StringComparer.Ordinal);
        bool grew = false;
        foreach (Replacement replacement in replacements)
            if (!files.ContainsKey(replacement.Identity)) grew = true;
        foreach ((string identity, string file) in found) files[identity] = file;
        foreach ((string identity, (float X, float Y) anchor) in anchors) allAnchors[identity] = anchor;
        foreach ((string identity, (float X, float Y) offset) in offsets) allOffsets[identity] = offset;
        foreach ((string identity, float turn) in turns) allTurns[identity] = turn;
        foreach ((string identity, (float X, float Y) fallback) in defaults) allDefaults[identity] = fallback;
        foreach ((string identity, string corner) in corners) allCorners[identity] = corner;
        foreach ((string identity, float scale) in scales) allScales[identity] = scale;

        Layout layout;
        if (cached != null && !grew && cached.Texture != null && cached.Texture.Pointer != IntPtr.Zero)
        {
            layout = cached;   // the same skin on the same page: the work is already done
        }
        else
        {
            // Compose from every identity this skin is known to place here, so the result depends on the
            // SKIN and not on which skeleton happened to ask first. Parts this skeleton did not walk are
            // read straight from disk: their own skeleton already took them through the per-region seam.
            List<Replacement> all = replacements.ToList();
            foreach ((string identity, string file) in files)
            {
                if (all.Any(item => item.Identity == identity)) continue;
                Texture2D? extra = SkinForgerMod.GetTexture(file, atlasName, linear: true);
                if (extra == null) continue;
                byte[]? bytes = TextureBridge.ToRgba(extra, out int extraWidth, out int extraHeight);
                UnityEngine.Object.Destroy(extra);
                if (bytes != null) all.Add(new Replacement(identity, bytes, extraWidth, extraHeight));
            }

            AtlasComposition composition = AtlasComposer.Compose(original, width, height, all);
            problems.AddRange(composition.Refused);

            if (cached?.Texture != null && cached.Texture.Pointer != IntPtr.Zero)
                UnityEngine.Object.Destroy(cached.Texture);
            Texture2D texture = TextureBridge.FromRgba(composition.Pixels, composition.Width, composition.Height,
                linear: true, name: "SkinForger:" + atlasName);
            texture.filterMode = source.filterMode;
            texture.wrapMode = source.wrapMode;
            SkinForgerMod.ApplyTextureSettings(texture);   // Core's one place to adjust runtime sampling

            layout = new Layout(texture,
                composition.Placed.ToDictionary(place => place.Identity, StringComparer.Ordinal),
                files, allAnchors, allOffsets, allTurns, allDefaults, allCorners, allScales);
            Layouts[key] = layout;
        }

        Composed[instanceId + "|" + atlasName] = layout.Texture;
        Texture2D composedTexture = layout.Texture;
        composedWidth = composedTexture.width;
        composedHeight = composedTexture.height;

        // Hand the composed atlas to the ownership manager: it remembers which original it came from, and
        // binds it to THIS renderer rather than writing it back into the page material every renderer of
        // this character shares (the 2026-10-01 recursion came from doing the latter).
        SkinForger.AtlasOwnershipFix.AtlasOwnershipManager.RegisterRuntime(composedTexture, source);
        Material? pageMaterial = MaterialOf(skeleton, atlasName);
        if (pageMaterial != null)
            SkinForger.AtlasOwnershipFix.AtlasOwnershipManager.Record(skeleton, pageMaterial, composedTexture);
        else
            problems.Add("the atlas page has no material: the composed atlas could not be bound to this renderer");

        // ---- 4. point the regions at it
        Dictionary<string, PlacedReplacement> placed = layout.Placed;
        // The author's pictures as they were decoded, by part. Only needed for the parts whose anchor
        // cannot place the game's canvas: those are matched against the game's own drawing instead.
        var pictures = new Dictionary<string, Replacement>(StringComparer.Ordinal);
        foreach (Replacement replacement in replacements) pictures[replacement.Identity] = replacement;
        int replaced = 0, rebased = 0;
        var handled = new HashSet<IntPtr>();
        foreach ((AtlasRegion region, List<Attachment> attachments, PartSpec part) in RegionsOf(contexts, atlasName, parts))
        {
            handled.Add(region.Pointer);
            ReplacedRegion? wholeImage = null;
            try
            {
                RememberQuads(attachments);
                if (placed.TryGetValue(part.Identity, out PlacedReplacement? place))
                {
                    (int canvasW, int canvasH) = CanvasOf(region);

                    // A mesh samples the piece of the picture that matches the GAME's canvas, placed by the
                    // anchor; a quad takes the whole picture and scales to it.
                    //
                    // The difference is not a preference, it is what each one can express: a quad's size
                    // comes from the region, so a bigger picture makes a bigger quad, while a mesh's shape
                    // is its vertices and no region setting can add any. Every attempt to make a mesh
                    // "grow" through the region instead -- claiming trim, claiming a different canvas,
                    // handing the numbers to an adapter -- distorted or displaced the part, which is the
                    // elfin of 2026-11-19 in its several forms.
                    bool hasMesh = attachments.Any(one => one.TryCast<MeshAttachment>() != null);
                    PixelRect window = place.Rect;
                    // Expansion proper is SkinForger.Core's mesh canvas: it appends band geometry to the
                    // mesh generator's transient buffers each frame, so the mesh covers the author's whole
                    // canvas with its deformation and animation intact. For that it needs the region to
                    // expose the WHOLE picture, which is why the window below is only a fallback.
                    //
                    // It is used when the canvas cannot be described at all -- neither the sidecar's anchor
                    // nor a centred placement puts the game's canvas inside the author's picture. Then no
                    // expansion is possible and the honest rendering is the game-sized window, which at
                    // least keeps the part's position, proportion and animation right.
                    if (hasMesh && (place.Rect.Width != canvasW || place.Rect.Height != canvasH) &&
                        !CanvasDescribable(place.Rect, canvasW, canvasH,
                            layout.Anchors.TryGetValue(part.Identity, out (float X, float Y) known) ? known : null,
                            layout.Defaults.TryGetValue(part.Identity, out (float X, float Y) gate)
                                ? gate
                                : RegionGeometry.AnchorOf(part, canvasW, canvasH)))
                    {
                        (float X, float Y) inGame = layout.Defaults.TryGetValue(part.Identity, out (float X, float Y) seamDefault)
                            ? seamDefault
                            : GameAnchorOf(attachments, part, canvasW, canvasH);
                        (float X, float Y) inAuthor = layout.Anchors.TryGetValue(part.Identity, out (float X, float Y) side)
                            ? side
                            : GameAnchorOf(attachments, part, place.Rect.Width, place.Rect.Height);
                        window = RegionGeometry.MeshWindow(place.Rect, canvasW, canvasH,
                            inAuthor.X, inAuthor.Y, inGame.X, inGame.Y);
                        if (Logged.Add("window|" + part.Identity))
                            MelonLogger.Msg($"{Prefix} {part.AttachmentName}: mesh keeps the game's " +
                                            $"{canvasW}x{canvasH} window of a {place.Rect.Width}x{place.Rect.Height} image");
                    }

                    PlacedReplacement shown = AtlasComposer.Place(part.Identity, window, composedWidth, composedHeight);
                    RememberRegion(region, attachments);   // before the first number changes
                    region.u = shown.U;
                    region.v = shown.V;
                    region.u2 = shown.U2;
                    region.v2 = shown.V2;
                    region.x = window.X;
                    region.y = window.Y;
                    // Where the GAME's canvas starts inside the author's picture: the sidecar's anchor
                    // measured against the anchor the part has in the game's own canvas, which is the
                    // contract SkinForger.Core states and reads Main's placement by --
                    //
                    //     left = AnchorX - DefaultAnchorX
                    //
                    // and nothing else. Two things that look like they belong in it do not:
                    //
                    //   * the DEFAULT anchor is the part's own (RegionGeometry.AnchorOf): the canvas centre
                    //     for a mesh, the bone origin for a quad. Using the centre for a quad is what
                    //     crammed the elfin's head, wings, fork and leg together, because a quad's anchor
                    //     is routinely outside its picture (head 157 in 143, wing_l 130 in 90);
                    //   * the author's offsetX / offsetY are a RIGID MOVE of the part, applied by Core's
                    //     PartTransforms from the RegionAnchor Main hands over below -- not a canvas shift.
                    //     Subtracting them here threw head (-18, 30) and fork (70, 20) out of their own
                    //     pictures and recentred them.
                    //
                    // Only for a canvas that actually grew. On an unchanged canvas nothing may move: that
                    // invariant is what stopped 470 parts being flung across the screen on 2026-11-19.
                    int canvasWidth = canvasW, canvasHeight = canvasH;
                    double left = 0, top = 0;
                    if (window.Width != canvasWidth || window.Height != canvasHeight)
                    {
                        // The default the seam answered, not one worked out again here: Core's
                        // AutoAnchor may have moved the anchor, and an anchor only means anything against
                        // the default it was measured from.
                        (float X, float Y) expected = layout.Defaults.TryGetValue(part.Identity, out (float X, float Y) given)
                            ? given
                            : GameAnchorOf(attachments, part, canvasWidth, canvasHeight);
                        (float X, float Y) anchor = layout.Anchors.TryGetValue(part.Identity, out (float X, float Y) side)
                            ? side
                            : expected;
                        (left, top) = RegionGeometry.CanvasStart(window, canvasWidth, canvasHeight,
                            anchor.X, anchor.Y, expected.X, expected.Y);
                        string how = "the sidecar's anchor";

                        // The anchor could not place the game's canvas inside the picture, so CanvasStart
                        // fell back to centring -- a guess, and on the player's elfin a wrong one for four
                        // of its six enlarged parts. Look at the pixels instead: the game's own drawing is
                        // in memory here, and so is the author's, so where the original sits inside the
                        // bigger picture can be MEASURED. This is what SkinForger used to do -- wing_r's
                        // sidecar still records it, "matched game atlas turn 2, score=0.19".
                        if (!RegionGeometry.Inside(anchor.X - expected.X, anchor.Y - expected.Y,
                                                   canvasWidth, canvasHeight, window.Width, window.Height))
                        {
                            how = "centred (the anchor does not fit the picture)";

                            // The author's own word first. Nothing can out-argue it: they are the only
                            // one who knows which side they added the room to, and when they redrew the
                            // part there is no original left in the picture for anything to measure.
                            (int Left, int Top)? corner = layout.CanvasAnchors.TryGetValue(part.Identity,
                                out string? said)
                                ? RegionGeometry.CanvasAnchorStart(said,
                                    window.Width - canvasWidth, window.Height - canvasHeight)
                                : null;
                            if (corner != null)
                            {
                                left = corner.Value.Left;
                                top = corner.Value.Top;
                                how = $"canvasAnchor \"{said}\" in the sidecar";
                            }
                            else if (pictures.TryGetValue(part.Identity, out Replacement? picture) &&
                                picture.Width == window.Width && picture.Height == window.Height)
                            {
                                byte[] drawn = RegionGeometry.BuildPartImage(original, width, height,
                                    part.Stored, part.Degrees, part.Rotate, part.OffsetX, part.OffsetY,
                                    canvasWidth, canvasHeight, out int drawnWidth, out int drawnHeight);
                                CanvasMatch.Found match = CanvasMatch.Locate(drawn, drawnWidth, drawnHeight,
                                    picture.Image, picture.Width, picture.Height);
                                if (match.Confident)
                                {
                                    left = match.Left;
                                    top = match.Top;
                                    how = $"the game's own drawing, found in the picture " +
                                          $"(score {match.Score:0.###} against {match.Centred:0.###} centred)";
                                }
                                else
                                {
                                    // Not found -- the author redrew the part rather than enlarging it.
                                    // Line the two drawings up by their ink instead of their canvases.
                                    CanvasMatch.Found ink = CanvasMatch.AlignByInk(drawn, drawnWidth,
                                        drawnHeight, picture.Image, picture.Width, picture.Height);
                                    if (ink.Confident)
                                    {
                                        left = ink.Left;
                                        top = ink.Top;
                                        how = $"lining the drawings up (the part was redrawn, not " +
                                              $"enlarged: best match {match.Score:0.###})";
                                    }
                                    else
                                    {
                                        how = $"centred: the anchor does not fit, the drawing was not " +
                                              $"found (best {match.Score:0.###}) and there is no ink to line up";
                                    }
                                }
                            }
                        }

                        if (Logged.Add("canvas|" + part.Identity))
                            MelonLogger.Msg($"{Prefix} {part.AttachmentName}: the game's {canvasWidth}x{canvasHeight} " +
                                            $"canvas starts at ({left:0.###}, {top:0.###}) of a " +
                                            $"{window.Width}x{window.Height} picture, by {how} " +
                                            $"(anchor {anchor.X:0.###},{anchor.Y:0.###} against {expected.X:0.###},{expected.Y:0.###})");
                    }
                    // THE AUTHOR'S PICTURE IS THE WHOLE CANVAS, AND NOTHING IS TRIMMED.
                    //
                    // This is the rule the old SkinForger used, and the player's own build tool states it
                    // in full: RegionAttachmentFix\Build\Program.cs rewrites the old Main so that an
                    // expanded region attachment is given, right before UpdateOffset,
                    //
                    //     regionOffsetX = 0                regionOffsetY = 0
                    //     regionWidth   = NormalWidth      regionHeight   = NormalHeight
                    //     regionOriginalWidth = NormalWidth    regionOriginalHeight = NormalHeight
                    //
                    // -- NormalWidth / NormalHeight being the AUTHOR's picture. Spine then computes the
                    // quad as -width/2 .. +width/2: the same size, in the same place, with the author's
                    // whole picture drawn into it. The quad NEVER MOVES AND NEVER GROWS.
                    //
                    // That is why the player's parts "were never once in the wrong place" before the cut,
                    // and why they have been wrong ever since: the authored Main grew each expanded quad
                    // by picture/canvas and shifted it by where it thought the canvas started, so every
                    // expanded part moved -- the elfin's head, both wings, the fork and a leg. The canvas
                    // placement is real, but it belongs to the MESH path (SkinForger.Core's band geometry
                    // reads it off the placement below); it must not touch the region's own numbers.
                    //
                    // left / top are still worked out above, and still handed to Core, for exactly that.
                    ReplacedRegion after = RegionGeometry.AfterReplacement(window, window.Width, window.Height);
                    region.rotate = after.Rotate;
                    region.degrees = after.Degrees;
                    region.width = after.Width;
                    region.height = after.Height;
                    region.originalWidth = after.OriginalWidth;
                    region.originalHeight = after.OriginalHeight;
                    region.offsetX = after.OffsetX;
                    region.offsetY = after.OffsetY;
                    wholeImage = after;

                    // The quads of this part, by SkinForger's own rule. Before RefreshUvs, because
                    // UpdateOffset rebuilds the quad from x / y / width / height / rotation.
                    PlaceQuads(attachments, part, layout, (canvasWidth, canvasHeight), window);
                    replaced++;
                }
                else if (composedHeight != height)
                {
                    RememberRegion(region, attachments);   // a rebased region is changed too
                    (float v, float v2) = AtlasComposer.Rebase(region.v, region.v2, height, composedHeight);
                    region.v = v;
                    region.v2 = v2;
                    rebased++;
                }
                else continue;

                if (region.page != null) { region.page.width = composedWidth; region.page.height = composedHeight; }

                foreach (Attachment attachment in attachments) RefreshUvs(attachment, region, wholeImage);

                // Where a MESH actually ends up sampling, against the rectangle its picture was placed in.
                //
                // Spine recomputes a mesh's uvs from the AtlasRegion, and its arithmetic BRANCHES ON THE
                // REGION'S DEGREES -- 0, 90, 180 and 270 each have their own formula, and the player's own
                // CharacterPolygonPackedRectRepair notes that 270 is the odd one ("Spine's UpdateUVs uses
                // unrotated width/height spans"). char_1_rock/hair_1 is the only part of its atlas packed
                // at 270, and the only one the player saw in fragments. This says plainly whether its uvs
                // land inside the rectangle SkinForger put its picture in, so the next look at the log
                // names the broken parts instead of guessing at them.
                if (wholeImage != null && placed.TryGetValue(part.Identity, out PlacedReplacement? sampled))
                    foreach (Attachment attachment in attachments)
                    {
                        MeshAttachment? sampling = attachment.TryCast<MeshAttachment>();
                        if (sampling == null || !Logged.Add("uvs|" + part.Identity)) continue;
                        try
                        {
                            float[]? uvs = sampling.uvs?.ToArray();
                            if (uvs is not { Length: >= 2 }) continue;
                            float lowU = uvs[0], highU = uvs[0], lowV = uvs[1], highV = uvs[1];
                            for (int at = 0; at + 1 < uvs.Length; at += 2)
                            {
                                if (uvs[at] < lowU) lowU = uvs[at];
                                if (uvs[at] > highU) highU = uvs[at];
                                if (uvs[at + 1] < lowV) lowV = uvs[at + 1];
                                if (uvs[at + 1] > highV) highV = uvs[at + 1];
                            }
                            // The rectangle the picture went into, in the same 0..1 terms.
                            double wantLowU = sampled.Rect.X / (double)composedWidth;
                            double wantHighU = (sampled.Rect.X + sampled.Rect.Width) / (double)composedWidth;
                            double wantHighV = 1.0 - sampled.Rect.Y / (double)composedHeight;
                            double wantLowV = 1.0 - (sampled.Rect.Y + sampled.Rect.Height) / (double)composedHeight;
                            double slackU = 1.5 / composedWidth, slackV = 1.5 / composedHeight;
                            bool outside = lowU < wantLowU - slackU || highU > wantHighU + slackU ||
                                           lowV < wantLowV - slackV || highV > wantHighV + slackV;
                            string verdict = outside ? "OUTSIDE its own rectangle" : "inside its rectangle";
                            string line = $"{Prefix} {part.AttachmentName}: mesh samples u {lowU:0.######}..{highU:0.######}, " +
                                          $"v {lowV:0.######}..{highV:0.######}; the picture went to " +
                                          $"u {wantLowU:0.######}..{wantHighU:0.######}, v {wantLowV:0.######}..{wantHighV:0.######} " +
                                          $"-- {verdict} (region degrees {region.degrees}, rotate {region.rotate}, " +
                                          $"{region.width}x{region.height} of {region.originalWidth}x{region.originalHeight} " +
                                          $"at {region.offsetX},{region.offsetY})";
                            if (outside) MelonLogger.Warning(line); else MelonLogger.Msg(line);
                        }
                        catch (Exception) { /* a probe must never affect a build */ }
                    }

                // The LAST number before Spine draws: the quad's four corners, as UpdateOffset left them.
                // Everything else is arithmetic that can be checked on paper; this is the only value that
                // says what the game will actually put on screen, and how many picture pixels fit in it.
                if (wholeImage != null)
                    foreach (Attachment attachment in attachments)
                    {
                        RegionAttachment? drawn = attachment.TryCast<RegionAttachment>();
                        if (drawn == null || !Logged.Add("drawn|" + part.Identity)) continue;
                        try
                        {
                            float[]? quadCorners = drawn.offset?.ToArray();
                            if (quadCorners is not { Length: >= 8 }) continue;
                            float lowX = quadCorners[0], highX = quadCorners[0];
                            float lowY = quadCorners[1], highY = quadCorners[1];
                            for (int at = 0; at < 8; at += 2)
                            {
                                if (quadCorners[at] < lowX) lowX = quadCorners[at];
                                if (quadCorners[at] > highX) highX = quadCorners[at];
                                if (quadCorners[at + 1] < lowY) lowY = quadCorners[at + 1];
                                if (quadCorners[at + 1] > highY) highY = quadCorners[at + 1];
                            }
                            float acrossUnits = highX - lowX, downUnits = highY - lowY;
                            double acrossPixels = wholeImage.Width <= 0 ? 0 : acrossUnits / wholeImage.Width;
                            double downPixels = wholeImage.Height <= 0 ? 0 : downUnits / wholeImage.Height;
                            MelonLogger.Msg($"{Prefix} {part.AttachmentName}: DRAWN {acrossUnits:0.####} x {downUnits:0.####} " +
                                            $"units for a {wholeImage.Width}x{wholeImage.Height} picture -- " +
                                            $"{acrossPixels:0.######} across vs {downPixels:0.######} down per pixel, " +
                                            $"squash {(acrossPixels <= 0 ? 0 : downPixels / acrossPixels):0.####} " +
                                            $"(1 = none)");
                        }
                        catch (Exception) { /* a probe must never affect a build */ }
                    }

                // Tell Core that this region has been placed. ElfinMeshCanvas listens here and grows a
                // mesh to the author's bigger canvas by adding vertices -- the one part of "expand the
                // canvas" that an atlas cannot express, because a mesh's shape is its vertices.
                if (wholeImage != null && placed.TryGetValue(part.Identity, out PlacedReplacement? announced))
                {
                    (int baseWidth, int baseHeight) = CanvasOf(region);
                    var placement = new SkinForgerMod.RegionAnchor
                    {
                        NormalWidth = announced.Rect.Width,
                        NormalHeight = announced.Rect.Height,
                        BaseNormalWidth = baseWidth,
                        BaseNormalHeight = baseHeight,
                    };
                    // The two anchors are measured in DIFFERENT canvases, and that is the whole point:
                    //
                    //     u = (DefaultAnchorX - AnchorX + OffsetX + uv.x * NormalWidth) / BaseNormalWidth
                    //
                    // AnchorX is where the anchor sits in the author's canvas (what the sidecar records);
                    // DefaultAnchorX is where it sat in the GAME's canvas. Their difference is what keeps
                    // the part anchored while the canvas grows around it.
                    //
                    // Measuring both in the author's canvas -- which is what this did earlier today --
                    // makes the difference meaningless and pulls every expanded part toward one spot: the
                    // elfin drawn with its pieces crammed together.
                    (float X, float Y) inGameCanvas = layout.Defaults.TryGetValue(part.Identity, out (float X, float Y) fromSeam)
                        ? fromSeam
                        : GameAnchorOf(attachments, part, baseWidth, baseHeight);
                    placement.DefaultAnchorX = inGameCanvas.X;
                    placement.DefaultAnchorY = inGameCanvas.Y;
                    if (layout.Anchors.TryGetValue(part.Identity, out (float X, float Y) placedAnchor))
                    {
                        placement.AnchorX = placedAnchor.X;
                        placement.AnchorY = placedAnchor.Y;
                        placement.FromSidecar = true;
                    }
                    else
                    {
                        // No sidecar: the anchor is wherever it would be in the author's canvas.
                        (float X, float Y) inAuthorCanvas =
                            GameAnchorOf(attachments, part, announced.Rect.Width, announced.Rect.Height);
                        placement.AnchorX = inAuthorCanvas.X;
                        placement.AnchorY = inAuthorCanvas.Y;
                    }

                    placement.PackedRect = new SkinForgerMod.RegionAnchor.PackedRectangle
                    {
                        X = place.Rect.X, Y = place.Rect.Y,
                        Width = place.Rect.Width, Height = place.Rect.Height,
                    };
                    placement.Degrees = 0;   // AtlasComposer never stores a fresh rectangle turned
                    if (layout.Offsets.TryGetValue(part.Identity, out (float X, float Y) nudge))
                    {
                        placement.OffsetX = nudge.X;
                        placement.OffsetY = nudge.Y;
                    }

                    string file = layout.Files.TryGetValue(part.Identity, out string? path) ? path : string.Empty;
                    foreach (Attachment attachment in attachments)
                    {
                        SkinForgerMod.ApplyRegionPlacementToAttachment(
                            attachment, region, placement, atlasName, file, skinDirectory);

                        // The geometry step. SkinForger.Core's mesh canvas hangs its whole registration off
                        // this call -- it records the attachment, calibrates the placement, resolves the
                        // canvas spec, and marks the atlas folder as one it owns. Without the marker its
                        // UI-Spine path refuses the skeleton outright, which is why the elfin never had
                        // canvas expansion.
                        //
                        // Main's own body is a no-op for elfins and is skipped for characters by Core's
                        // prefix, so what matters here is that the call HAPPENS.
                        try
                        {
                            SkinForger.CharacterMeshGeometry.CharacterMeshGeometry.Apply(
                                skeleton, elfin, SlotIndexOf(skeleton, attachment), attachment, placement, file);
                        }
                        catch (Exception exception)
                        {
                            if (Logged.Add("geometry|" + part.Identity))
                                MelonLogger.Warning($"{Prefix} {part.AttachmentName}: geometry step: " +
                                                    exception.GetBaseException().Message);
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                problems.Add($"{part.AttachmentName}: {exception.GetBaseException().Message}");
            }
        }

        // THE SAFETY NET. Every region of this page that the loop above never reached still has to be
        // told the atlas grew, or it goes on reading the old layout out of the new texture.
        //
        // A part can be left out for several reasons -- its name is censored, its page does not match,
        // its metadata is unusable, or (until 2026-11-20) its rectangle hung off the page. Each of those
        // is a decision about REPLACING it, and none of them should decide whether it is still drawn
        // correctly. char_1_rock/hair_1 was dropped for the last reason and came apart on screen in a
        // song: not because SkinForger refused to replace it, but because nothing afterwards rebased it.
        //
        // Rebasing reads no image and changes nothing but the vertical mapping, so it is safe for every
        // part, censored ones included -- leaving those wrong would be the greater harm.
        int rescued = 0;
        if (composedHeight != height)
            foreach (AttachmentContext context in contexts)
            {
                AtlasRegion region = context.Region;
                if (region == null || region.Pointer == IntPtr.Zero) continue;
                if (!PartCollector.SamePage(context.Atlas, atlasName)) continue;
                if (!handled.Add(region.Pointer)) continue;
                try
                {
                    (float v, float v2) = AtlasComposer.Rebase(region.v, region.v2, height, composedHeight);
                    region.v = v;
                    region.v2 = v2;
                    if (region.page != null) { region.page.width = composedWidth; region.page.height = composedHeight; }
                    RefreshUvs(context.Attachment, region);
                    rebased++;
                    rescued++;
                }
                catch (Exception exception)
                {
                    problems.Add($"{context.Attachment?.Name ?? "?"}: could not be rebased: " +
                                 exception.GetBaseException().Message);
                }
            }
        if (rescued > 0 && Logged.Add("rescued|" + atlasName))
            MelonLogger.Msg($"{Prefix} {atlasName}: {rescued} part(s) SkinForger does not replace were " +
                            $"rebased to the taller atlas as well");

        MelonLogger.Msg($"{Prefix} {atlasName}: {replaced} replaced, {rebased} rebased, " +
                        $"atlas {width}x{height} -> {composedWidth}x{composedHeight}" +
                        (problems.Count > 0 ? $" ({problems.Count} problem(s))" : string.Empty));
        return new AtlasApplied(atlasName, replaced, rebased, composedWidth, composedHeight, problems);
    }

    /// <summary>
    /// Recomputes an attachment's UVs from its region. This is the ONLY change allowed to an attachment:
    /// regionUVs, vertices, triangles and weights are never written (SF日志 part 4), so the deformation the
    /// original author set up is kept exactly. It matches what SkinForger.Core's rebase and the Preview
    /// runtime already do.
    /// </summary>
    /// <param name="wholeImage">
    /// The geometry a MESH must be given: the whole replacement image, no trim, no rotation.
    ///
    /// A mesh is not a quad. Its shape is its vertices, and Spine only uses these six numbers to map its
    /// regionUVs onto the region -- so telling a mesh "you are 130 wide inside a 110-wide canvas" makes it
    /// sample a smaller area of the picture, and the drawing is pulled inward. That is what an expanded
    /// canvas looked like on the elfin, whose parts are all meshes (SkinForger.Core's mesh module reports
    /// scope=all-character-meshes and regionAttachments=unchanged, and it is the one that produced the
    /// elfin's SPEC lines).
    ///
    /// Growing a mesh to fill a bigger canvas needs vertices added, which is that module's job, not
    /// something these six numbers can express. Null means "the region's own numbers are right".
    /// </param>
    /// <summary>
    /// Puts every QUAD attachment of a part where the author asked for it.
    ///
    /// This is SkinForger's own rule, and it is written out in full in the player's own sources --
    /// ReplacementDispatcher.ApplyGeometry, with AnchorFromAttachment and CenterFromAnchor beside it,
    /// and RegionAttachmentFix\Build\Program.cs for the region numbers that go with it. In one place:
    ///
    ///   * an ANCHOR is a pixel position in the author's picture where the bone origin must land. There
    ///     is no test of whether the game's old canvas "fits" inside the picture and no centred
    ///     fallback. Both were invented on 2026-11-20, and they are what moved the elfin's head, wings,
    ///     fork and leg;
    ///   * the quad GROWS with the picture -- width = picture px x units-per-pixel -- so the drawing
    ///     keeps its size. Leaving the quad alone squeezes the picture into the old one, which is the
    ///     head the player saw flattened;
    ///   * the author's nudge is added last, in the bone's axes, NOT turned by the attachment's
    ///     rotation, scaled only from pixels into units;
    ///   * the author's turn is added to the attachment's rotation, and their scale multiplies its own.
    ///
    /// Always from the PRISTINE placement, so applying the same skin again -- every renderer, every
    /// scene -- lands the part in the same place instead of walking it further each time.
    /// </summary>
    private static void PlaceQuads(List<Attachment> attachments, PartSpec part, Layout layout,
        (int Width, int Height) canvas, PixelRect picture)
    {
        bool hasAnchor = layout.Anchors.TryGetValue(part.Identity, out (float X, float Y) said);
        (float X, float Y) nudge = layout.Offsets.TryGetValue(part.Identity, out (float X, float Y) move)
            ? move : (0f, 0f);
        float turn = layout.Turns.TryGetValue(part.Identity, out float asked) ? asked : 0f;
        float scale = layout.Scales.TryGetValue(part.Identity, out float sized) && sized > 0f ? sized : 1f;

        foreach (Attachment attachment in attachments)
        {
            RegionAttachment? quad = attachment.TryCast<RegionAttachment>();
            if (quad == null) continue;
            if (!PristineQuad.TryGetValue(attachment.Pointer,
                    out (RegionAttachment Quad, float X, float Y, float Rotation,
                         float Width, float Height, float ScaleX, float ScaleY) was))
                continue;   // RememberQuads runs first; without it there is no game placement to work from

            (double unitX, double unitY) =
                RegionGeometry.QuadUnits(was.Width, was.Height, canvas.Width, canvas.Height);
            bool expanded = picture.Width > canvas.Width || picture.Height > canvas.Height;
            int shownWidth = expanded ? picture.Width : canvas.Width;
            int shownHeight = expanded ? picture.Height : canvas.Height;

            if (expanded || hasAnchor)
            {
                (float X, float Y) anchor = hasAnchor
                    ? said
                    : RegionGeometry.QuadAnchorIn(was.X, was.Y, was.Rotation, was.ScaleX, was.ScaleY,
                        canvas.Width, canvas.Height, unitX, unitY);
                (float x, float y) = RegionGeometry.QuadCentre(was.Rotation, was.ScaleX, was.ScaleY,
                    shownWidth, shownHeight, anchor.X, anchor.Y, nudge.X, nudge.Y, unitX, unitY);
                quad.x = x;
                quad.y = y;
            }
            else
            {
                quad.x = was.X;
                quad.y = was.Y;
            }

            quad.width = expanded ? (float)(shownWidth * unitX) : was.Width;
            quad.height = expanded ? (float)(shownHeight * unitY) : was.Height;
            quad.rotation = was.Rotation + turn;
            quad.scaleX = was.ScaleX * scale;
            quad.scaleY = was.ScaleY * scale;

            if (Logged.Add("quad|" + part.Identity))
                MelonLogger.Msg($"{Prefix} {part.AttachmentName}: quad {was.Width:0.###}x{was.Height:0.###} " +
                                $"-> {quad.width:0.###}x{quad.height:0.###}, x {was.X:0.###} -> {quad.x:0.###}, " +
                                $"y {was.Y:0.###} -> {quad.y:0.###} " +
                                $"(canvas {canvas.Width}x{canvas.Height}, picture {picture.Width}x{picture.Height}, " +
                                $"anchor {(hasAnchor ? $"{said.X:0.##},{said.Y:0.##} from the sidecar" : "the game's own")}, " +
                                $"nudge {nudge.X:0.##},{nudge.Y:0.##}, turn {turn:0.##}, scale {scale:0.###}, " +
                                $"spun {was.Rotation:0.##} deg)");
        }
    }

    public static void RefreshUvs(Attachment attachment, AtlasRegion region, ReplacedRegion? wholeImage = null)
    {
        MeshAttachment? mesh = attachment.TryCast<MeshAttachment>();
        if (mesh != null)
        {
            // Spine's OWN way of handing a region to a mesh, first.
            //
            // The player's CharacterMeshUvCommit says where this belongs: "MeshAttachment Region
            // acquisition uses Spine AttachmentRegionExtensions". Setting the cached fields by hand gets
            // the ordinary part right, but Spine's UpdateUVs branches on the region's DEGREES and reads
            // the trim through the page size, and a hand-written copy only reproduces the branch it was
            // written for. char_1_rock/hair_1 is the one part of that atlas packed at 270 degrees --
            // 181 of 182 pixels, turned -- and it is the one part the player saw come out in fragments.
            //
            // The six fields are still written below: they are the same numbers from the same region, so
            // the end state does not change, and they stand in if this call is ever unavailable.
            try { Il2CppSpine.Unity.AttachmentTools.AttachmentRegionExtensions.SetRegion(mesh, region, false); }
            catch (Exception exception)
            {
                if (Logged.Add("setregion"))
                    MelonLogger.Warning($"{Prefix} Spine's SetRegion is unavailable, " +
                                        $"falling back to the fields: {exception.GetBaseException().Message}");
            }

            mesh.RegionU = region.u;
            mesh.RegionV = region.v;
            mesh.RegionU2 = region.u2;
            mesh.RegionV2 = region.v2;
            mesh.RegionRotate = region.rotate;
            mesh.RegionDegrees = region.degrees;

            // A mesh carries its OWN copy of the region's size and trim, and Spine maps every vertex
            // through it. Leaving it on the old numbers is what made the hair wrong in a song:
            // char_1_rock/hair_1 is packed 181x176 rotated 270 degrees out of a 182x176 canvas, so after
            // the replacement went into a fresh 182x176 unrotated rectangle the mesh was still mapping
            // through "1 pixel trimmed, turned a quarter turn". SkinForger.Core's mesh module computes its
            // band from the same fields: its worst band-area ratio went from about -19 under the old Main
            // to -218 here (2026-11-19), which is the number that pointed at this.
            //
            // The old Main never had to touch these because it wrote pixels INTO the existing rectangle,
            // so every one of them stayed true. A fresh rectangle makes them all stale at once.
            mesh.regionWidth = wholeImage?.Width ?? region.width;
            mesh.regionHeight = wholeImage?.Height ?? region.height;
            mesh.regionOriginalWidth = wholeImage?.OriginalWidth ?? region.originalWidth;
            mesh.regionOriginalHeight = wholeImage?.OriginalHeight ?? region.originalHeight;
            mesh.regionOffsetX = wholeImage?.OffsetX ?? region.offsetX;
            mesh.regionOffsetY = wholeImage?.OffsetY ?? region.offsetY;

            mesh.UpdateUVs();
            return;
        }

        RegionAttachment? quad = attachment.TryCast<RegionAttachment>();
        if (quad != null)
        {
            // A region attachment carries the same six numbers, and its QUAD is computed from them:
            // Spine scales the attachment's own width/height by regionWidth / regionOriginalWidth. Setting
            // only the UVs leaves a replacement whose canvas grew being drawn in the old, smaller quad --
            // the flattened elfin (body 31x54 -> 40x54, wing_l 110x81 -> 130x90). UpdateOffset is what
            // rebuilds the quad from them, and nothing was calling it.
            quad.regionWidth = region.width;
            quad.regionHeight = region.height;
            quad.regionOriginalWidth = region.originalWidth;
            quad.regionOriginalHeight = region.originalHeight;
            quad.regionOffsetX = region.offsetX;
            quad.regionOffsetY = region.offsetY;
            quad.SetUVs(region.u, region.v, region.u2, region.v2, region.rotate);
            quad.UpdateOffset();
            return;
        }
    }

    /// <summary>The material an atlas page is drawn with, which is what a binding is made against.</summary>
    private static Material? MaterialOf(Skeleton skeleton, string atlasName)
    {
        try
        {
            SkeletonData? data = skeleton.Data;
            int slotCount = data?.slots?.Count ?? 0;
            if (data == null) return null;
            var entries = new Il2CppSystem.Collections.Generic.List<Skin.SkinEntry>();
            for (int s = 0; s < (data.skins?.Count ?? 0); s++)
            {
                Skin? skin = data.skins!.Items[s];
                if (skin == null) continue;
                for (int slot = 0; slot < slotCount; slot++)
                {
                    entries.Clear();
                    try { skin.GetAttachments(slot, entries); } catch (Exception) { continue; }
                    for (int e = 0; e < entries.Count; e++)
                    {
                        AtlasRegion? region = PartCollector.RegionOf(entries[e]?.Attachment);
                        AtlasPage? page = region?.page;
                        if (page == null || !PartCollector.SamePage(page.name, atlasName)) continue;
                        Material? material = page.rendererObject?.TryCast<Material>();
                        if (material != null && material.Pointer != IntPtr.Zero) return material;
                    }
                }
            }
        }
        catch (Exception) { /* no material: reported by the caller */ }
        return null;
    }

    /// <summary>
    /// The regions of one atlas with the attachments that draw from each of them, grouped from the walk
    /// the caller already has. It reuses that list rather than walking the skeleton a second time, so a
    /// part SkinForger.Core removed as censored is absent here too.
    /// </summary>
    private static IEnumerable<(AtlasRegion Region, List<Attachment> Attachments, PartSpec Part)> RegionsOf(
        IEnumerable<AttachmentContext> contexts, string atlasName,
        IReadOnlyList<(AttachmentContext Context, PartSpec Part)> parts)
    {
        var byIdentity = new Dictionary<string, PartSpec>(StringComparer.Ordinal);
        foreach ((AttachmentContext _, PartSpec part) in parts) byIdentity[part.Identity] = part;

        var found = new Dictionary<IntPtr, (AtlasRegion Region, List<Attachment> Attachments, PartSpec Part)>();
        foreach (AttachmentContext context in contexts)
        {
            if (!PartCollector.SamePage(context.Atlas, atlasName)) continue;
            if (!byIdentity.TryGetValue(context.Identity, out PartSpec? part)) continue;

            AtlasRegion region = context.Region;
            Attachment attachment = context.Attachment;
            if (found.TryGetValue(region.Pointer, out var existing))
            {
                if (!existing.Attachments.Any(a => a.Pointer == attachment.Pointer)) existing.Attachments.Add(attachment);
            }
            else found[region.Pointer] = (region, new List<Attachment> { attachment }, part);
        }
        return found.Values;
    }
}
