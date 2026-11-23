using UnityEngine;
using UnityEngine.UI;

namespace SkinForger.SelectionThumbnail;

internal sealed class ThumbnailCacheEntry
{
    public string Path = string.Empty;
    public string Stamp = string.Empty;
    public Texture2D? Texture;
    public Sprite? Sprite;
}

internal sealed class ThumbnailImageBinding
{
    public Image? Image;
    public Sprite? Original;
    public int ListIndex;
    public bool Elfin;
    public bool Hexie;
}

internal sealed class ThumbnailOriginal
{
    public Sprite? Sprite;
    public string AddressKey = string.Empty;
    public int ListIndex;
    public bool Elfin;
    public bool Hexie;
}
