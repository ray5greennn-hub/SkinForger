using MelonLoader;
using SkinForger.Main.Pure;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace SkinForger.Main.Unity;

/// <summary>
/// Puts the pictures in Images\Other back on screen.
///
/// These are the ones that are not part of any costume: the warning screens, the headset tip, a UI panel,
/// a font atlas. SkinForger's "pictures seen on screen" list can dump them by NAME, and until 2026-11-20
/// nothing read them back — the folder was write-only, so an author could edit them forever and see
/// nothing change.
///
/// A costume's part is found through a skeleton and an atlas. These have neither: the only thing the dump
/// records, and the only thing available again at runtime, is the texture's own name. So this watches the
/// live scene and matches by name, the same way the dump found them in the first place.
///
/// What it touches, and what it puts back:
///   * RawImage  -- its texture, swapped directly;
///   * Image / SpriteRenderer -- a NEW sprite over the author's texture, with the original's rectangle,
///     pivot and border carried across (scaled when the author drew it at a different size, so the
///     picture stays the same size on screen).
/// The originals are remembered and restored by <see cref="Forget"/>, so "back to the game's own art"
/// works for these exactly as it does for everything else. Nothing is written to the game's assets.
/// </summary>
public static class OtherTextures
{
    private const string Prefix = "[SkinForge][Main][Other]";

    private static MainLayout? _layout;
    private static Dictionary<string, OtherPicture>? _pictures;
    private static string _picturesFrom = string.Empty;
    private static readonly Dictionary<string, Texture2D> Loaded = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Sprite> Sprites = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Logged = new(StringComparer.Ordinal);

    /// <summary>What a graphic showed before SkinForger touched it.</summary>
    private readonly record struct Original(RawImage? Raw, Texture? Texture, Image? Image,
        SpriteRenderer? Renderer, Sprite? Sprite);

    private static readonly List<Original> Restorable = new();

    private static float _nextSweep;
    private static int _quietSweeps;
    private static int _applied;
    /// <summary>
    /// While the game is still starting, look nearly every frame.
    ///
    /// The headset tip is on screen about a second after the game opens, and it is one of these pictures.
    /// A three-second sweep catches it late enough to see it change, which the player did. For the first
    /// stretch the interval is a fifth of a second instead; after that the ordinary backing-off takes over,
    /// because a song puts thousands of objects in the scene and almost never a new picture.
    /// </summary>
    private const float EagerSeconds = 25f;
    private const float EagerInterval = 0.2f;
    private static float _eagerUntil;

    /// <summary>
    /// Started EARLY, from OnInitializeMelon -- not with the other bundled runtimes.
    ///
    /// Some of these pictures are on screen while the game is still starting (the headset tip), which
    /// happens before OnLateInitializeMelon runs at all. Starting here, plus the eager sweep above, is
    /// what makes those appear already replaced instead of a second late.
    /// </summary>
    public static void Initialize(MainLayout layout)
    {
        _layout = layout;
        _pictures = null;
        _nextSweep = 0f;
        _eagerUntil = Time.unscaledTime + EagerSeconds;
        MelonLogger.Msg($"{Prefix} ready: pictures in Images\\{OtherPictures.FolderName} are matched by " +
                        $"texture name and put back on screen");
    }

    /// <summary>Re-reads the folder; called from a Reload so an edited picture shows without a restart.</summary>
    public static void Forget()
    {
        foreach (Original original in Restorable)
        {
            try
            {
                if (original.Raw != null && original.Raw.Pointer != IntPtr.Zero)
                    original.Raw.texture = original.Texture;
                else if (original.Image != null && original.Image.Pointer != IntPtr.Zero)
                    original.Image.sprite = original.Sprite;
                else if (original.Renderer != null && original.Renderer.Pointer != IntPtr.Zero)
                    original.Renderer.sprite = original.Sprite;
            }
            catch (Exception) { /* the object is gone: nothing to put back */ }
        }
        Restorable.Clear();
        _eagerUntil = Time.unscaledTime + EagerSeconds;   // a Reload looks hard again for a moment
        foreach (Sprite sprite in Sprites.Values)
            try { if (sprite != null && sprite.Pointer != IntPtr.Zero) UnityEngine.Object.Destroy(sprite); }
            catch (Exception) { }
        Sprites.Clear();
        foreach (Texture2D texture in Loaded.Values)
            try { if (texture != null && texture.Pointer != IntPtr.Zero) UnityEngine.Object.Destroy(texture); }
            catch (Exception) { }
        Loaded.Clear();
        _pictures = null;
        _nextSweep = 0f;
        _quietSweeps = 0;
        _applied = 0;
    }

    /// <summary>
    /// The folder, read once and remembered. Every PNG in it counts, with or without a sidecar; the
    /// sidecar only adds the size it was dumped at.
    /// </summary>
    private static Dictionary<string, OtherPicture> Pictures()
    {
        // The packages are mounted by Core, which loads after the Main and remounts whenever the player
        // turns one on or off. So the remembered list is only good for as long as that mount is the same.
        string packages = SkinFiles.PackageOtherPictures ?? string.Empty;
        if (_pictures != null && packages == _picturesFrom) return _pictures;

        var found = new Dictionary<string, OtherPicture>(StringComparer.OrdinalIgnoreCase);
        string own = _layout == null
            ? string.Empty
            : Path.Combine(_layout.Images, OtherPictures.FolderName);

        // The enabled packages FIRST and the player's own folder second, because the second pass
        // overwrites: SkinCreator (your own work) > enabled packages > the game's own art, the same order
        // the Skin Library states for a costume's parts.
        foreach (string folder in new[] { packages, own })
        {
            if (folder.Length == 0) continue;
            try
            {
                if (!SkinFiles.DirectoryExists(folder)) continue;
                foreach (string file in SkinFiles.GetFiles(folder, "*.png", SearchOption.TopDirectoryOnly))
                {
                    string stem = Path.GetFileNameWithoutExtension(file);
                    if (DumpNaming.IsCensoredName(stem)) continue;   // censored pictures are never read
                    string sidecar = Path.ChangeExtension(file, ".json");
                    byte[]? json = SkinFiles.Exists(sidecar) ? SkinFiles.ReadAllBytes(sidecar) : null;
                    found[stem] = OtherPictures.Describe(file, json);
                }
            }
            catch (Exception exception)
            {
                MelonLogger.Warning($"{Prefix} could not read {folder}: " +
                                    exception.GetBaseException().Message);
            }
        }
        _pictures = found;
        _picturesFrom = packages;
        if (found.Count > 0 && Logged.Add("count|" + found.Count))
            MelonLogger.Msg($"{Prefix} {found.Count} picture(s) to put back: " +
                            string.Join(", ", found.Keys.Take(8)));
        return found;
    }

    private static Texture2D? TextureFor(OtherPicture picture)
    {
        if (Loaded.TryGetValue(picture.Stem, out Texture2D? had) && had != null && had.Pointer != IntPtr.Zero)
            return had;
        byte[]? pixels = TextureBridge.LoadPng(picture.File, out int width, out int height);
        if (pixels == null) return null;
        Texture2D made = TextureBridge.FromRgba(pixels, width, height, linear: false,
            name: "SkinForger:Other:" + picture.Stem);
        SkinForgerMod.ApplyTextureSettings(made);
        Loaded[picture.Stem] = made;
        return made;
    }

    /// <summary>
    /// Walks the live scene and puts every matching picture on. Called from OnUpdate; backs off the same
    /// way the skeleton sweep does, because a song has thousands of objects and almost never a new one.
    /// </summary>
    public static void Sweep(float seconds = 3f)
    {
        if (_layout == null) return;
        float now = Time.unscaledTime;
        if (now < _nextSweep) return;
        bool eager = now < _eagerUntil;
        float wait = eager ? EagerInterval : seconds * (1 << Math.Min(_quietSweeps / 4, 3));
        _nextSweep = now + wait;

        Dictionary<string, OtherPicture> pictures = Pictures();
        if (pictures.Count == 0) return;

        int before = _applied;
        try
        {
            // Scene roots with GetComponentsInChildren(true), never FindObjectsOfType: that one skips
            // components on inactive objects, and a popup that is not showing is exactly that -- which is
            // the whole reason the warning pictures were hard to dump in the first place.
            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                Scene scene = SceneManager.GetSceneAt(index);
                if (!scene.isLoaded) continue;
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    if (root == null || root.Pointer == IntPtr.Zero) continue;
                    foreach (RawImage raw in root.GetComponentsInChildren<RawImage>(true))
                        TryApplyRaw(raw, pictures);
                    foreach (Image image in root.GetComponentsInChildren<Image>(true))
                        TryApplyImage(image, pictures);
                    foreach (SpriteRenderer renderer in root.GetComponentsInChildren<SpriteRenderer>(true))
                        TryApplyRenderer(renderer, pictures);
                }
            }
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"{Prefix} sweep: {exception.GetBaseException().Message}");
        }
        _quietSweeps = _applied == before ? _quietSweeps + 1 : 0;
    }

    private static bool Wanted(Texture? texture, Dictionary<string, OtherPicture> pictures,
        out OtherPicture picture)
    {
        picture = default;
        if (texture == null || texture.Pointer == IntPtr.Zero) return false;
        string name = texture.name ?? string.Empty;
        if (name.Length == 0 || name.StartsWith("SkinForger:", StringComparison.Ordinal)) return false;
        return pictures.TryGetValue(OtherPictures.FileStemOf(name), out picture);
    }

    private static void TryApplyRaw(RawImage raw, Dictionary<string, OtherPicture> pictures)
    {
        if (raw == null || raw.Pointer == IntPtr.Zero) return;
        if (!Wanted(raw.texture, pictures, out OtherPicture picture)) return;
        Texture2D? replacement = TextureFor(picture);
        if (replacement == null) return;
        Restorable.Add(new Original(raw, raw.texture, null, null, null));
        raw.texture = replacement;
        Note(picture, "RawImage", raw.name);
    }

    private static void TryApplyImage(Image image, Dictionary<string, OtherPicture> pictures)
    {
        if (image == null || image.Pointer == IntPtr.Zero) return;
        Sprite? sprite = image.sprite;
        if (sprite == null || sprite.Pointer == IntPtr.Zero) return;
        if (!Wanted(sprite.texture, pictures, out OtherPicture picture)) return;
        Sprite? made = SpriteFor(sprite, picture);
        if (made == null) return;
        Restorable.Add(new Original(null, null, image, null, sprite));
        image.sprite = made;
        Note(picture, "Image", image.name);
    }

    private static void TryApplyRenderer(SpriteRenderer renderer, Dictionary<string, OtherPicture> pictures)
    {
        if (renderer == null || renderer.Pointer == IntPtr.Zero) return;
        Sprite? sprite = renderer.sprite;
        if (sprite == null || sprite.Pointer == IntPtr.Zero) return;
        if (!Wanted(sprite.texture, pictures, out OtherPicture picture)) return;
        Sprite? made = SpriteFor(sprite, picture);
        if (made == null) return;
        Restorable.Add(new Original(null, null, null, renderer, sprite));
        renderer.sprite = made;
        Note(picture, "SpriteRenderer", renderer.name);
    }

    /// <summary>
    /// A sprite over the author's texture that draws the same thing, the same size, in the same place.
    /// Built once per (sprite, picture) and reused: a sweep runs for as long as the game does.
    /// </summary>
    private static Sprite? SpriteFor(Sprite original, OtherPicture picture)
    {
        string key = original.GetInstanceID() + "|" + picture.Stem;
        if (Sprites.TryGetValue(key, out Sprite? had) && had != null && had.Pointer != IntPtr.Zero) return had;

        Texture2D? replacement = TextureFor(picture);
        if (replacement == null) return null;
        try
        {
            Texture oldTexture = original.texture;
            int fromWidth = picture.DumpedWidth > 0 ? picture.DumpedWidth : oldTexture.width;
            int fromHeight = picture.DumpedHeight > 0 ? picture.DumpedHeight : oldTexture.height;

            Rect rect = original.rect;
            PixelRect scaled = OtherPictures.SpriteRect(
                new PixelRect((int)rect.x, (int)rect.y, (int)rect.width, (int)rect.height),
                fromWidth, fromHeight, replacement.width, replacement.height);

            // The pivot is kept where it is RELATIVE to the rectangle, so the picture hangs off the same
            // point however the author resized it.
            Vector2 pivot = rect.width <= 0f || rect.height <= 0f
                ? new Vector2(0.5f, 0.5f)
                : new Vector2(original.pivot.x / rect.width, original.pivot.y / rect.height);

            float perUnit = OtherPictures.PixelsPerUnit(original.pixelsPerUnit, fromWidth, replacement.width);
            Sprite made = Sprite.Create(replacement,
                new Rect(scaled.X, scaled.Y, scaled.Width, scaled.Height), pivot,
                perUnit <= 0f ? 100f : perUnit);
            made.name = "SkinForger:Other:" + picture.Stem;
            Sprites[key] = made;
            return made;
        }
        catch (Exception exception)
        {
            if (Logged.Add("sprite|" + picture.Stem))
                MelonLogger.Warning($"{Prefix} {picture.Stem}: could not build a sprite: " +
                                    exception.GetBaseException().Message);
            return null;
        }
    }

    private static void Note(OtherPicture picture, string kind, string? owner)
    {
        _applied++;
        if (!Logged.Add("put|" + picture.Stem + "|" + kind)) return;
        MelonLogger.Msg($"{Prefix} {picture.Stem}: put back on a {kind} ({owner}) " +
                        (picture.DumpedWidth > 0
                            ? $"-- dumped at {picture.DumpedWidth}x{picture.DumpedHeight}"
                            : "-- no sidecar, size taken from the game"));
    }
}
