namespace SkinForger.Main.Pure;

/// <summary>
/// A rectangle of texture pixels, top-left origin, as the Dump and the runtime atlas use it.
/// Pure (no Unity), so <c>Tests\</c> can check it outside the game.
/// </summary>
public readonly struct PixelRect : IEquatable<PixelRect>
{
    public PixelRect(int x, int y, int width, int height)
    {
        X = x;
        Y = y;
        Width = width < 0 ? 0 : width;
        Height = height < 0 ? 0 : height;
    }

    public int X { get; }
    public int Y { get; }
    public int Width { get; }
    public int Height { get; }

    public static PixelRect Empty => new(0, 0, 0, 0);
    public bool IsEmpty => Width == 0 || Height == 0;
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public long Area => (long)Width * Height;

    /// <summary>True when this rectangle lies completely inside a texture of the given size.</summary>
    public bool FitsIn(int textureWidth, int textureHeight) =>
        X >= 0 && Y >= 0 && Right <= textureWidth && Bottom <= textureHeight;

    public bool Contains(PixelRect other) =>
        !other.IsEmpty && other.X >= X && other.Y >= Y && other.Right <= Right && other.Bottom <= Bottom;

    public bool Overlaps(PixelRect other) =>
        !IsEmpty && !other.IsEmpty && X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;

    /// <summary>The rectangle moved by (dx, dy).</summary>
    public PixelRect Offset(int dx, int dy) => new(X + dx, Y + dy, Width, Height);

    /// <summary>
    /// This rectangle with any part of it that lies outside a texture trimmed away. Empty when none of it
    /// is inside.
    ///
    /// A region CAN hang off its own page: Muse Dash's own char_1_rock packs hair_1 at u2 = 1.0009766,
    /// two pixels past the right edge of a 2048-wide atlas. The sampler clamps and the game draws it
    /// fine, so it is not a broken part -- and refusing it, which the authored Main did until
    /// 2026-11-20, meant SkinForger never saw that part at all: not replaced, and worse, NOT REBASED
    /// when the atlas grew, so it went on reading 0..512 coordinates out of a 1176-tall texture. That is
    /// the fragmented hair the player saw in a song.
    /// </summary>
    public PixelRect ClampedTo(int textureWidth, int textureHeight)
    {
        int left = Math.Max(0, X), top = Math.Max(0, Y);
        int right = Math.Min(textureWidth, Right), bottom = Math.Min(textureHeight, Bottom);
        return right <= left || bottom <= top ? new PixelRect(0, 0, 0, 0)
            : new PixelRect(left, top, right - left, bottom - top);
    }

    /// <summary>Width and height swapped: the same region stored rotated by 90° or 270°.</summary>
    public PixelRect Transposed => new(X, Y, Height, Width);

    public bool Equals(PixelRect other) => X == other.X && Y == other.Y && Width == other.Width && Height == other.Height;
    public override bool Equals(object? obj) => obj is PixelRect other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(X, Y, Width, Height);
    public static bool operator ==(PixelRect a, PixelRect b) => a.Equals(b);
    public static bool operator !=(PixelRect a, PixelRect b) => !a.Equals(b);
    public override string ToString() => $"{Width}x{Height}@{X},{Y}";
}
