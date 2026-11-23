namespace SkinForger.Main.Pure;

/// <summary>
/// The per-pixel work the Dump and the runtime atlas need, over plain RGBA byte buffers: alpha bounds,
/// un-premultiplying, cropping and 90° rotation. No Unity types, so <c>Tests\</c> can check every rule
/// outside the game; the Unity side only converts a Texture2D to and from these buffers.
///
/// Buffer convention: 4 bytes per pixel (R, G, B, A), rows top to bottom, <c>width * height * 4</c> long.
/// Unity's own buffers are bottom-up; the adapter flips, so everything here is top-left origin — the same
/// origin the Dump writes into manifest.json and the anchor sidecars ("origin": "top-left").
/// </summary>
public static class PixelMath
{
    public const int BytesPerPixel = 4;

    public static int BufferLength(int width, int height) => width * height * BytesPerPixel;

    private static void CheckBuffer(byte[] pixels, int width, int height, string name)
    {
        if (width < 0 || height < 0) throw new ArgumentOutOfRangeException(nameof(width), "Negative size.");
        if (pixels.Length != BufferLength(width, height))
            throw new ArgumentException($"{name}: {pixels.Length} bytes for {width}x{height} (expected {BufferLength(width, height)}).", name);
    }

    /// <summary>
    /// The smallest rectangle containing every pixel whose alpha is above <paramref name="threshold"/>,
    /// or <see cref="PixelRect.Empty"/> when the image is fully transparent. The Dump uses this to find the
    /// drawing inside a transparent canvas.
    /// </summary>
    public static PixelRect AlphaBounds(byte[] pixels, int width, int height, byte threshold = 0)
    {
        CheckBuffer(pixels, width, height, nameof(pixels));
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (int y = 0; y < height; y++)
        {
            int row = y * width * BytesPerPixel;
            for (int x = 0; x < width; x++)
            {
                if (pixels[row + x * BytesPerPixel + 3] <= threshold) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        return maxX < minX ? PixelRect.Empty : new PixelRect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>
    /// Undoes premultiplied alpha in place: the game's atlases store colour already multiplied by alpha, so a
    /// dumped PNG that skips this step looks dark at the edges or grows an outline (SF日志: PMA).
    /// Fully transparent pixels keep their colour bytes, because no colour can be recovered from them.
    /// </summary>
    public static void UnpremultiplyInPlace(byte[] pixels, int width, int height)
    {
        CheckBuffer(pixels, width, height, nameof(pixels));
        for (int i = 0; i < pixels.Length; i += BytesPerPixel)
        {
            byte alpha = pixels[i + 3];
            if (alpha == 0 || alpha == 255) continue;
            for (int channel = 0; channel < 3; channel++)
            {
                int value = (pixels[i + channel] * 255 + alpha / 2) / alpha;
                pixels[i + channel] = (byte)(value > 255 ? 255 : value);
            }
        }
    }

    /// <summary>Multiplies colour by alpha in place: what a PNG needs before it is written into a PMA atlas.</summary>
    public static void PremultiplyInPlace(byte[] pixels, int width, int height)
    {
        CheckBuffer(pixels, width, height, nameof(pixels));
        for (int i = 0; i < pixels.Length; i += BytesPerPixel)
        {
            byte alpha = pixels[i + 3];
            if (alpha == 255) continue;
            for (int channel = 0; channel < 3; channel++)
                pixels[i + channel] = (byte)((pixels[i + channel] * alpha + 127) / 255);
        }
    }

    /// <summary>A fully transparent buffer of the given size.</summary>
    public static byte[] Transparent(int width, int height) => new byte[BufferLength(width, height)];

    /// <summary>
    /// Copies <paramref name="rect"/> out of the buffer. Parts of the rectangle outside the source stay
    /// transparent, so a rectangle larger than the source gives the source on a transparent canvas.
    /// </summary>
    public static byte[] Crop(byte[] pixels, int width, int height, PixelRect rect)
    {
        CheckBuffer(pixels, width, height, nameof(pixels));
        byte[] result = Transparent(rect.Width, rect.Height);
        for (int y = 0; y < rect.Height; y++)
        {
            int sourceY = rect.Y + y;
            if (sourceY < 0 || sourceY >= height) continue;
            int from = Math.Max(0, -rect.X);
            int count = Math.Min(rect.Width - from, width - Math.Max(0, rect.X));
            if (count <= 0) continue;
            Buffer.BlockCopy(pixels, (sourceY * width + rect.X + from) * BytesPerPixel,
                             result, (y * rect.Width + from) * BytesPerPixel, count * BytesPerPixel);
        }
        return result;
    }

    /// <summary>
    /// Draws <paramref name="source"/> into <paramref name="target"/> at (x, y), replacing the pixels there
    /// (no blending: a replacement image owns its rectangle). Anything outside the target is clipped.
    /// </summary>
    public static void Blit(byte[] target, int targetWidth, int targetHeight,
                            byte[] source, int sourceWidth, int sourceHeight, int x, int y)
    {
        CheckBuffer(target, targetWidth, targetHeight, nameof(target));
        CheckBuffer(source, sourceWidth, sourceHeight, nameof(source));
        for (int row = 0; row < sourceHeight; row++)
        {
            int targetY = y + row;
            if (targetY < 0 || targetY >= targetHeight) continue;
            int from = Math.Max(0, -x);
            int count = Math.Min(sourceWidth - from, targetWidth - Math.Max(0, x));
            if (count <= 0) continue;
            Buffer.BlockCopy(source, (row * sourceWidth + from) * BytesPerPixel,
                             target, (targetY * targetWidth + x + from) * BytesPerPixel, count * BytesPerPixel);
        }
    }

    /// <summary>
    /// Rotates clockwise by 0, 90, 180 or 270 degrees. 90 and 270 swap width and height — the contract an
    /// atlas region stored rotated needs (SF日志: 旋转存放的契约算错 was the 2026-10-04 root cause).
    /// </summary>
    public static byte[] RotateClockwise(byte[] pixels, int width, int height, int degrees, out int newWidth, out int newHeight)
    {
        CheckBuffer(pixels, width, height, nameof(pixels));
        int turns = ((degrees / 90) % 4 + 4) % 4;
        if (degrees % 90 != 0) throw new ArgumentOutOfRangeException(nameof(degrees), "Only multiples of 90 degrees.");
        newWidth = turns % 2 == 0 ? width : height;
        newHeight = turns % 2 == 0 ? height : width;
        if (turns == 0) return (byte[])pixels.Clone();

        byte[] result = Transparent(newWidth, newHeight);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                (int tx, int ty) = turns switch
                {
                    1 => (newWidth - 1 - y, x),              //  90° clockwise
                    2 => (width - 1 - x, height - 1 - y),    // 180°
                    _ => (y, newHeight - 1 - x)              // 270° clockwise
                };
                Buffer.BlockCopy(pixels, (y * width + x) * BytesPerPixel,
                                 result, (ty * newWidth + tx) * BytesPerPixel, BytesPerPixel);
            }
        return result;
    }

    /// <summary>
    /// Reverses the row order: the one step between Unity's bottom-up pixel buffers and the top-left
    /// convention SkinForger writes to disk. The Unity side converts Color32 to bytes and calls this, so
    /// the flip itself exists once and is checked here.
    /// </summary>
    public static byte[] FlipRows(byte[] pixels, int width, int height)
    {
        CheckBuffer(pixels, width, height, nameof(pixels));
        byte[] result = new byte[pixels.Length];
        int stride = width * BytesPerPixel;
        for (int y = 0; y < height; y++)
            Buffer.BlockCopy(pixels, y * stride, result, (height - 1 - y) * stride, stride);
        return result;
    }

    /// <summary>True when both buffers are the same size and every byte matches (used by the double-run Dump comparison).</summary>
    public static bool Same(byte[] a, byte[] b) => a.Length == b.Length && a.AsSpan().SequenceEqual(b);
}
