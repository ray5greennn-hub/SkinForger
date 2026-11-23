using System.Text;

namespace SkinForger.SkinPackages;

/// <summary>Reads the family name of a .ttf / .otf / .ttc (OpenType "name" table, nameID 1). Pure .NET.</summary>
public static class FontFile
{
    public static string? FamilyName(byte[] data)
    {
        try
        {
            int font = 0;
            if (Tag(data, 0) == "ttcf") font = (int)U32(data, 12);   // collection: first font
            int tables = U16(data, font + 4);
            for (int t = 0; t < tables; t++)
            {
                int record = font + 12 + t * 16;
                if (Tag(data, record) != "name") continue;
                int name = (int)U32(data, record + 8);
                int count = U16(data, name + 2), strings = name + U16(data, name + 4);
                string? windows = null, windowsUs = null, mac = null;
                for (int i = 0; i < count; i++)
                {
                    int r = name + 6 + i * 12;
                    int platform = U16(data, r), language = U16(data, r + 4), id = U16(data, r + 6), length = U16(data, r + 8), offset = U16(data, r + 10);
                    if (id != 1 || strings + offset + length > data.Length) continue;
                    if (platform == 3)
                    {
                        string value = Encoding.BigEndianUnicode.GetString(data, strings + offset, length);
                        if (language == 0x0409) windowsUs ??= value; else windows ??= value;
                    }
                    else if (platform == 1) mac ??= Encoding.ASCII.GetString(data, strings + offset, length);
                }
                return windowsUs ?? windows ?? mac;
            }
        }
        catch { }
        return null;
    }

    private static string Tag(byte[] d, int o) => Encoding.ASCII.GetString(d, o, 4);
    private static int U16(byte[] d, int o) => (d[o] << 8) | d[o + 1];
    private static uint U32(byte[] d, int o) => (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);
}
