using System.IO.Compression;

namespace SkinForger.SkinPackages;

/// <summary>
/// Whether a PNG is drawn on at all -- just the alpha channel, nothing else.
///
/// Why this exists (2026-11-21). The way to make a part INVISIBLE is to erase it: keep the file, keep its
/// size, make every pixel transparent. That is a real edit, and it was the one edit the export could not
/// see. `EditDetector`'s four rules all looked at something OTHER than the pixels:
///
///   * replaced\ history -- empty for a skin whose history was cleaned (or made before that feature);
///   * the recorded hash -- a Reload records the CURRENT file, so the erased version becomes the baseline
///     and stops counting as a change;
///   * the size against manifest.json -- an erased part is exactly the original size;
///   * the sidecar -- erasing needs no offset, no rotation, no scale.
///
/// So an erased part was packed as "unchanged", and the player who installed the package got the game's
/// own hair / eyes / mouth back on top of the new art. The author's intent was inverted by the export.
///
/// A fully transparent image is read as EDITED. A dump never writes one: it cuts a region out of the
/// game's atlas, and a region that holds nothing at all is not something the game ships. Even if one
/// existed, packing blank over blank changes nothing -- the cost of being wrong here is zero, and the
/// cost of the other mistake is a visibly broken skin.
///
/// Pure .NET, no Unity, no System.Drawing: the PNG is decoded here so the export can be checked offline.
/// </summary>
public static class PngAlpha
{
    private static readonly byte[] Signature = { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>
    /// True when every pixel of this PNG has alpha 0. False for anything this cannot decide, including a
    /// file it cannot read, a colour type with no alpha channel, an interlaced image, or 16-bit samples.
    /// "Cannot decide" always means "do not claim it was edited".
    /// </summary>
    public static bool IsFullyTransparent(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            return IsFullyTransparent(stream);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The same test on an open stream, for the offline checks.</summary>
    public static bool IsFullyTransparent(Stream stream)
    {
        var header = new byte[8];
        if (!Fill(stream, header, 8)) return false;
        for (int i = 0; i < 8; i++) if (header[i] != Signature[i]) return false;

        int width = 0, height = 0, bitDepth = 0, colourType = -1;
        var pixels = new MemoryStream();
        bool seenHeader = false;

        while (true)
        {
            var lengthBytes = new byte[4];
            if (!Fill(stream, lengthBytes, 4)) break;
            long length = ((long)lengthBytes[0] << 24) | ((long)lengthBytes[1] << 16) |
                          ((long)lengthBytes[2] << 8) | lengthBytes[3];
            var typeBytes = new byte[4];
            if (!Fill(stream, typeBytes, 4)) return false;
            string type = $"{(char)typeBytes[0]}{(char)typeBytes[1]}{(char)typeBytes[2]}{(char)typeBytes[3]}";
            if (length is < 0 or > int.MaxValue) return false;

            if (type == "IHDR")
            {
                if (length < 13) return false;
                var ihdr = new byte[(int)length];
                if (!Fill(stream, ihdr, ihdr.Length)) return false;
                width = (ihdr[0] << 24) | (ihdr[1] << 16) | (ihdr[2] << 8) | ihdr[3];
                height = (ihdr[4] << 24) | (ihdr[5] << 16) | (ihdr[6] << 8) | ihdr[7];
                bitDepth = ihdr[8];
                colourType = ihdr[9];
                if (ihdr[12] != 0) return false;                       // interlaced: not decoded here
                if (bitDepth != 8) return false;                       // 16-bit / sub-byte: not decoded here
                if (colourType != 6 && colourType != 4) return false;  // no alpha channel at all
                if (width <= 0 || height <= 0) return false;
                seenHeader = true;
            }
            else if (type == "IDAT")
            {
                if (!seenHeader) return false;
                var chunk = new byte[(int)length];
                if (!Fill(stream, chunk, chunk.Length)) return false;
                pixels.Write(chunk, 0, chunk.Length);
            }
            else if (type == "IEND")
            {
                break;
            }
            else
            {
                if (stream.CanSeek) stream.Seek(length, SeekOrigin.Current);
                else if (!Skip(stream, length)) return false;
            }

            var crc = new byte[4];
            if (!Fill(stream, crc, 4)) break;                          // a truncated file decides nothing
        }

        if (!seenHeader || pixels.Length == 0) return false;
        int samples = colourType == 6 ? 4 : 2;                         // RGBA or grey+alpha
        return AllAlphaZero(pixels.ToArray(), width, height, samples);
    }

    /// <summary>
    /// Undoes the per-scanline filters and looks at the alpha sample of every pixel.
    ///
    /// The filters have to be undone even though only alpha is wanted: filter types 2-4 predict a byte
    /// from its neighbours, so an alpha byte on its own means nothing until the line above and the pixel
    /// to the left are reconstructed.
    /// </summary>
    private static bool AllAlphaZero(byte[] deflated, int width, int height, int samples)
    {
        byte[] raw;
        try
        {
            // The zlib stream's own 2-byte header is skipped so this works without ZLibStream.
            using var source = new MemoryStream(deflated, 2, deflated.Length - 2);
            using var inflate = new DeflateStream(source, CompressionMode.Decompress);
            using var all = new MemoryStream();
            inflate.CopyTo(all);
            raw = all.ToArray();
        }
        catch (Exception)
        {
            return false;
        }

        int stride = width * samples;
        if (raw.Length < (long)(stride + 1) * height) return false;

        var previous = new byte[stride];
        var line = new byte[stride];
        int at = 0;
        for (int y = 0; y < height; y++)
        {
            byte filter = raw[at++];
            Buffer.BlockCopy(raw, at, line, 0, stride);
            at += stride;

            for (int x = 0; x < stride; x++)
            {
                int left = x >= samples ? line[x - samples] : 0;
                int up = previous[x];
                int upLeft = x >= samples ? previous[x - samples] : 0;
                line[x] = filter switch
                {
                    0 => line[x],
                    1 => (byte)(line[x] + left),
                    2 => (byte)(line[x] + up),
                    3 => (byte)(line[x] + ((left + up) >> 1)),
                    4 => (byte)(line[x] + Paeth(left, up, upLeft)),
                    _ => line[x],
                };
            }
            if (filter > 4) return false;                              // not a PNG filter: decide nothing

            for (int x = samples - 1; x < stride; x += samples)
                if (line[x] != 0) return false;                        // something is drawn here

            Buffer.BlockCopy(line, 0, previous, 0, stride);
        }
        return true;
    }

    private static int Paeth(int left, int up, int upLeft)
    {
        int estimate = left + up - upLeft;
        int toLeft = Math.Abs(estimate - left), toUp = Math.Abs(estimate - up), toCorner = Math.Abs(estimate - upLeft);
        return toLeft <= toUp && toLeft <= toCorner ? left : toUp <= toCorner ? up : upLeft;
    }

    private static bool Fill(Stream stream, byte[] buffer, int count)
    {
        int got = 0;
        while (got < count)
        {
            int read = stream.Read(buffer, got, count - got);
            if (read <= 0) return false;
            got += read;
        }
        return true;
    }

    private static bool Skip(Stream stream, long count)
    {
        var bin = new byte[4096];
        while (count > 0)
        {
            int read = stream.Read(bin, 0, (int)Math.Min(bin.Length, count));
            if (read <= 0) return false;
            count -= read;
        }
        return true;
    }
}
