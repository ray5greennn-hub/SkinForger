using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using SkinForger.HexieBlock;
using UnityEngine;

namespace SkinForger.SkinPackages;

/// <summary>
/// Dumps the game's mouse cursor into SkinCreator\Cursor (2026-11-18, user request).
///
/// Finding it is the whole job. The cursor is NOT in the Addressables catalog: the Mouse0 / Mouse1 sprites
/// that are in there sit beside ArcadeModeA, ButtonWASD and Ps4ButtonTips, so they are the "left click /
/// right click" hint icons of the controls panel — white, 60x60, and not what follows the mouse. A Unity
/// game's cursor comes from the player settings or from Cursor.SetCursor, and neither leaves a trace in
/// the catalog. So this looks where it certainly is: among the textures the game has LOADED.
///
/// Two passes, because a name is not always enough:
///   1. by name — a loaded texture whose name speaks of a cursor, minus the hint icons above;
///   2. by colour — when no name matches, the small textures that are mostly pink are written into
///      Cursor\maybe, and every small texture is named in the log. The cursor is one of them.
///
/// Like every Dump: a file that is already there is kept, never overwritten. Dumping is all this does —
/// SkinForger cannot put a replaced cursor back into the game yet.
/// </summary>
internal static class CursorDump
{
    private const string Prefix = "[SkinForge][Cursor]";

    internal const string FolderName = "Cursor";

    /// <summary>
    /// The game's cursor, confirmed by the creator on 2026-11-19: a small pink arrow called MouseIcon.
    /// An earlier guess put MouseIcon on the exclusion list as "the program icon", which is why the dump
    /// kept missing the one picture it was looking for.
    /// </summary>
    private const string CursorName = "MouseIcon";

    /// <summary>
    /// Named like the cursor but not it: Mouse0 / Mouse1 are the controls panel's "left click / right
    /// click" hints — 60x60, near-white, sitting beside ArcadeModeA and ButtonWASD.
    /// </summary>
    private static readonly HashSet<string> NotTheCursor = new(StringComparer.OrdinalIgnoreCase)
    {
        "Mouse0", "Mouse1"
    };

    /// <summary>The one thing a creator has to know about this picture, shown under the button.</summary>
    internal const string Warning = "Do not enlarge the canvas: a cursor's position is tied to its hotspot, so a bigger picture makes clicks land wrong.";

    internal static string Status { get; private set; } = string.Empty;

    internal static string Folder => Path.Combine(SkinForgerPaths.Creator, FolderName);

    // ------------------------------------------------------------------ window

    /// <summary>The bottom of the Image page is drawn by <see cref="SeenPanel"/>.</summary>
    internal static void DrawFooter() => SeenPanel.Draw();

    // ------------------------------------------------------------------ dump

    internal static string Dump()
    {
        try
        {
            Il2CppArrayBase<Texture2D>? all;
            try { all = Resources.FindObjectsOfTypeAll<Texture2D>(); }
            catch (Exception exception)
            {
                MelonLogger.Warning($"{Prefix} the loaded textures could not be listed: {exception.GetBaseException().Message}");
                return Status = "The game's textures could not be read.";
            }

            Texture2D? cursor = null;
            foreach (Texture2D texture in all)
            {
                if (texture == null || texture.Pointer == IntPtr.Zero) continue;
                string name = texture.name ?? string.Empty;
                if (NotTheCursor.Contains(name) || HexiePolicy.IsHexieName(name)) continue;
                if (!string.Equals(name, CursorName, StringComparison.OrdinalIgnoreCase)) continue;
                cursor = texture;
                break;
            }

            if (cursor == null)
                return Status = $"{CursorName} is not loaded right now — open a screen that shows the cursor and press Dump again.";

            string folder = Folder;
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, CursorName + ".png");
            bool had = File.Exists(file);
            if (!had && !WriteTexture(cursor, file)) return Status = $"{CursorName} could not be read back.";

            string sidecar = SeenTextures.WriteSidecarFor(file, cursor.width, cursor.height, "the game cursor", "UI");
            Status = (had ? $"Kept {CursorName}.png (not overwritten)" : $"Wrote {CursorName}.png ({cursor.width}x{cursor.height})") + sidecar;
            MelonLogger.Msg($"{Prefix} {Status}");
            return Status;
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"{Prefix} failed: {exception.GetBaseException().Message}");
            return Status = "Cursor dump failed: " + exception.GetBaseException().Message;
        }
    }

    /// <summary>Writes one texture, reading it back first because a cursor texture is rarely readable.</summary>
    internal static bool WriteTexture(Texture2D texture, string file)
    {
        Texture2D? copy = null;
        try
        {
            Color32[] pixels = RoleSkins.ReadBack(texture);
            if (pixels.Length != texture.width * texture.height) return false;
            copy = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
            copy.SetPixels32(new Il2CppStructArray<Color32>(pixels));
            copy.Apply(false, false);
            Il2CppStructArray<byte> png = ImageConversion.EncodeToPNG(copy);
            if (png == null || png.Length == 0) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, (byte[])png);
            return true;
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"{Prefix} {Path.GetFileName(file)}: {exception.GetBaseException().Message}");
            return false;
        }
        finally
        {
            if (copy != null) UnityEngine.Object.Destroy(copy);
        }
    }

    private static string Safe(string name)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return name.Trim().Length == 0 ? "cursor" : name.Trim();
    }
}
