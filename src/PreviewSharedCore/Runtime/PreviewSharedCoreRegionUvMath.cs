using System;

namespace SkinForger.Preview;

internal readonly struct PreviewSharedCoreUvContract
{
    internal PreviewSharedCoreUvContract(int atlasX, int atlasY, float u, float v, float u2, float v2)
    {
        AtlasX = atlasX;
        AtlasY = atlasY;
        U = u;
        V = v;
        U2 = u2;
        V2 = v2;
    }

    internal int AtlasX { get; }
    internal int AtlasY { get; }
    internal float U { get; }
    internal float V { get; }
    internal float U2 { get; }
    internal float V2 { get; }
}

internal static class PreviewSharedCoreRegionUvMath
{
    internal static bool TryBuild270(
        int atlasWidth,
        int atlasHeight,
        int packedX,
        int packedY,
        int packedWidth,
        int packedHeight,
        float width,
        float height,
        float originalWidth,
        float offsetX,
        float offsetY,
        out PreviewSharedCoreUvContract contract)
    {
        contract = default;
        if (atlasWidth <= 0 || atlasHeight <= 0 || packedWidth <= 0 || packedHeight <= 0 ||
            width <= 0 || height <= 0 || originalWidth <= 0)
            return false;

        float left = packedX + offsetY;
        float bottom = packedY + originalWidth - offsetX - width;
        float right = left + height;
        float top = bottom + width;
        const float epsilon = 0.01f;
        if (left < packedX - epsilon || bottom < packedY - epsilon ||
            right > packedX + packedWidth + epsilon || top > packedY + packedHeight + epsilon)
            return false;

        contract = new PreviewSharedCoreUvContract(
            RoundToInt(left),
            atlasHeight - RoundToInt(top),
            left / atlasWidth,
            top / atlasHeight,
            right / atlasWidth,
            bottom / atlasHeight);
        return true;
    }

    private static int RoundToInt(float value) =>
        (int)Math.Round(value, MidpointRounding.AwayFromZero);
}
