using Il2CppInterop.Runtime.InteropTypes.Arrays;
using SkinForger.Main.Pure;
using MelonLoader;
using UnityEngine;

namespace SkinForger.Main.Unity;

/// <summary>
/// The only place that converts between Unity textures and the plain RGBA buffers everything else works on.
/// Keeping it here is what lets <c>Pure\</c> stay testable outside the game.
///
/// Two conventions meet here:
///   - Unity's pixel buffers start at the BOTTOM-left row;
///   - SkinForger's buffers, the dumped PNGs, manifest.json and the anchor sidecars ("origin": "top-left")
///     all start at the TOP-left row.
/// Every method below flips, so callers never think about it.
///
/// The read-back path (blit into a RenderTexture, then ReadPixels) is used because the game's atlases are
/// compressed and not readable: GetPixels32 on them throws or returns nothing. This is the same approach
/// SkinForger.Core's AutoAnchor already uses against the same textures.
/// </summary>
public static class TextureBridge
{
    private const string Prefix = "[SkinForge][Main][Images]";

    /// <summary>
    /// A readable RGBA32 copy of any texture, compressed or not. The caller owns the copy and should
    /// Destroy it. Returns null when the texture is missing or has no size.
    /// </summary>
    public static Texture2D? ReadableCopy(Texture2D? source)
    {
        if (source == null || source.Pointer == IntPtr.Zero || source.width <= 0 || source.height <= 0) return null;

        RenderTexture previous = RenderTexture.active;
        RenderTexture temporary = RenderTexture.GetTemporary(source.width, source.height, 0,
            RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        try
        {
            temporary.filterMode = FilterMode.Point;
            Graphics.Blit(source, temporary);
            RenderTexture.active = temporary;
            var copy = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false) { name = source.name };
            copy.ReadPixels(new Rect(0f, 0f, source.width, source.height), 0, 0);
            copy.Apply(false, false);
            return copy;
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(temporary);
        }
    }

    /// <summary>
    /// The texture as a top-left RGBA buffer. Reads the texture directly when it is readable, and goes
    /// through <see cref="ReadableCopy"/> when it is not. Returns null when nothing could be read.
    /// </summary>
    public static byte[]? ToRgba(Texture2D? texture, out int width, out int height)
    {
        width = height = 0;
        if (texture == null || texture.Pointer == IntPtr.Zero) return null;

        Color32[]? colours = TryGetPixels(texture);
        Texture2D? copy = null;
        if (colours == null)
        {
            copy = ReadableCopy(texture);
            if (copy == null) return null;
            colours = TryGetPixels(copy);
        }
        Texture2D measured = copy ?? texture;
        try
        {
            if (colours == null || colours.Length != measured.width * measured.height) return null;
            width = measured.width;
            height = measured.height;
            return FlipToTopLeft(colours, width, height);
        }
        finally
        {
            if (copy != null) UnityEngine.Object.Destroy(copy);
        }
    }

    private static Color32[]? TryGetPixels(Texture2D texture)
    {
        try
        {
            Il2CppStructArray<Color32> pixels = texture.GetPixels32();
            return pixels == null || pixels.Length == 0 ? null : (Color32[])pixels;
        }
        catch (Exception)
        {
            return null;   // not readable (compressed atlas): the caller falls back to a read-back copy
        }
    }

    /// <summary>Unity's bottom-up Color32 rows to a top-left RGBA byte buffer.</summary>
    public static byte[] FlipToTopLeft(Color32[] colours, int width, int height)
    {
        // Colour to bytes here, row order in PixelMath.FlipRows: the flip lives in one tested place.
        byte[] bottomUp = PixelMath.Transparent(width, height);
        for (int i = 0, b = 0; i < colours.Length; i++, b += PixelMath.BytesPerPixel)
        {
            Color32 colour = colours[i];
            bottomUp[b] = colour.r;
            bottomUp[b + 1] = colour.g;
            bottomUp[b + 2] = colour.b;
            bottomUp[b + 3] = colour.a;
        }
        return PixelMath.FlipRows(bottomUp, width, height);
    }

    /// <summary>A top-left RGBA byte buffer back to Unity's bottom-up Color32 rows.</summary>
    public static Color32[] FlipFromTopLeft(byte[] bytes, int width, int height)
    {
        byte[] bottomUp = PixelMath.FlipRows(bytes, width, height);
        var colours = new Color32[width * height];
        for (int i = 0, b = 0; i < colours.Length; i++, b += PixelMath.BytesPerPixel)
            colours[i] = new Color32(bottomUp[b], bottomUp[b + 1], bottomUp[b + 2], bottomUp[b + 3]);
        return colours;
    }

    /// <summary>
    /// A new RGBA32 texture holding a top-left buffer. <paramref name="linear"/> matches what the runtime
    /// atlas is created with (SkinForger's atlases are uncompressed and linear).
    /// </summary>
    public static Texture2D FromRgba(byte[] bytes, int width, int height, bool linear = false, string? name = null)
    {
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, linear);
        if (name != null) texture.name = name;
        texture.SetPixels32(new Il2CppStructArray<Color32>(FlipFromTopLeft(bytes, width, height)));
        texture.Apply(false, false);
        return texture;
    }

    /// <summary>Decoded images, by path: (file length, write time, pixels, size). Cleared when it gets big.</summary>
    private static readonly Dictionary<string, (long Length, long Ticks, byte[] Pixels, int Width, int Height)> Decoded =
        new(StringComparer.OrdinalIgnoreCase);
    private static long _decodedBytes;
    /// <summary>How much decoded image data to keep. Past this, the lot is dropped and read again as needed.</summary>
    private const long DecodedBudget = 256L * 1024L * 1024L;

    /// <summary>Forgets every decoded image (a Dump rewrote the folder, or the player asked for a clean start).</summary>
    public static void ForgetDecoded()
    {
        if (Decoded.Count == 0) return;
        MelonLogger.Msg($"{Prefix} forgot {Decoded.Count} decoded image(s) ({_decodedBytes / (1024 * 1024)} MB)");
        Decoded.Clear();
        _decodedBytes = 0;
    }

    /// <summary>
    /// Reads a PNG from disk as a top-left RGBA buffer. Null when the file is missing or not an image.
    ///
    /// The result is REMEMBERED, keyed by the file's length and write time: an atlas is composed several
    /// times in a session (every object of a costume, every Reload) and decoding the same hundreds of PNGs
    /// again each time was the single biggest cost of a rebuild. A file that changed on disk fails the
    /// stamp and is read again, so an edit always wins -- that is the whole point of the Reload button.
    /// (SkinForger.Core's ReloadFilter did this for the old Main through its GameplayScope.DecodedTextures;
    /// this Main has no such scope, so it keeps its own.)
    /// </summary>
    public static byte[]? LoadPng(string path, out int width, out int height)
    {
        width = height = 0;
        // The replacement image itself. A packaged skin's PNGs are in memory, never on disk, so this one
        // call is the difference between a .sfskin replacing everything and replacing nothing at all.
        if (!SkinFiles.Exists(path)) return null;

        (long Length, long Ticks) stamp = (0L, 0L);
        bool onDisk = false;
        try
        {
            var file = new FileInfo(path);
            if (file.Exists) { stamp = (file.Length, file.LastWriteTimeUtc.Ticks); onDisk = true; }
        }
        catch (Exception) { onDisk = false; }   // a package file: in memory, never cached here

        if (onDisk && Decoded.TryGetValue(path, out var known) && known.Length == stamp.Length && known.Ticks == stamp.Ticks)
        {
            width = known.Width;
            height = known.Height;
            return known.Pixels;
        }
        // One scratch texture for every decode: LoadImage resizes it to fit. Hundreds of images are read
        // on a build, and a Texture2D each was allocation the game had to clean up afterwards (2026-11-20).
        Texture2D texture = _scratch ??= new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        try
        {
            if (texture == null || texture.Pointer == IntPtr.Zero)
                texture = _scratch = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            if (!ImageConversion.LoadImage(texture, SkinFiles.ReadAllBytes(path))) return null;
            Color32[]? colours = TryGetPixels(texture);
            if (colours == null) return null;
            width = texture.width;
            height = texture.height;
            byte[] pixels = FlipToTopLeft(colours, width, height);
            if (onDisk)
            {
                if (_decodedBytes + pixels.LongLength > DecodedBudget) ForgetDecoded();
                if (Decoded.TryGetValue(path, out var stale)) _decodedBytes -= stale.Pixels.LongLength;
                Decoded[path] = (stamp.Length, stamp.Ticks, pixels, width, height);
                _decodedBytes += pixels.LongLength;
            }
            return pixels;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The one texture every PNG is decoded into; kept so hundreds of reads allocate one object.</summary>
    private static Texture2D? _scratch;

    /// <summary>
    /// Writes a top-left RGBA buffer as a PNG, complete file at once. Censored names are refused here as
    /// well as upstream, so no path can slip past the rule by reaching this method directly.
    /// </summary>
    public static bool SavePng(string path, byte[] bytes, int width, int height)
    {
        if (DumpNaming.IsCensoredName(Path.GetFileName(path)) || DumpNaming.IsCensoredName(Path.GetFileName(Path.GetDirectoryName(path))))
            return false;

        // A Dump writes hundreds of these in a row (Bassist alone: 489). Building a Texture2D for each one
        // just to encode it -- allocate, upload, Apply, encode, Destroy -- was most of the cost, and the
        // textures pile up until Unity collects them. EncodeArrayToPNG encodes the pixels directly.
        // 2026-11-20. If that entry point is not there, the old way still runs.
        byte[]? encoded = EncodeRgba(bytes, width, height);
        if (encoded == null || encoded.Length == 0) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".partial";
        File.WriteAllBytes(temporary, encoded);
        if (File.Exists(path)) File.Delete(path);
        File.Move(temporary, path);
        return true;
    }

    /// <summary>
    /// A top-left RGBA buffer as PNG bytes.
    ///
    /// Through a Texture2D, deliberately. 2026-11-20 this took a short cut -- ImageConversion's
    /// EncodeArrayToPNG, to save building a texture for each of the hundreds of parts a Dump writes -- and
    /// 2026-11-21 the author found the dumped images UPSIDE DOWN: that entry point does not take the rows
    /// in the order FromRgba's own conversion produces. A Dump that writes wrong pictures is worth far
    /// more than the milliseconds, so the proven path is back and stays.
    /// </summary>
    private static byte[]? EncodeRgba(byte[] bytes, int width, int height)
    {
        Texture2D texture = FromRgba(bytes, width, height);
        try
        {
            Il2CppStructArray<byte> png = ImageConversion.EncodeToPNG(texture);
            return png == null || png.Length == 0 ? null : (byte[])png;
        }
        finally { UnityEngine.Object.Destroy(texture); }
    }
}
