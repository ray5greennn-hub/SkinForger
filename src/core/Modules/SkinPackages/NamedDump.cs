using Il2CppPeroTools2.Resources;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;

namespace SkinForger.SkinPackages;

/// <summary>
/// Dumps a picture the game is not showing, by asking the game to load it (2026-11-19, user request).
///
/// The "pictures on screen" list can only offer what is loaded, and some screens exist for a few seconds
/// and never come back: the startup warnings are the ones that prompted this — PnlWarning (the photo-
/// sensitivity banner) and PnlHeadsetTip (wear headphones) are gone by the time the creator window can be
/// opened, so their names sat in the list with nothing behind them.
///
/// So this loads by name instead of waiting to be shown. A name is either:
///   * a prefab, which is brought in, stripped of every picture its renderers and UI graphics draw, and
///     thrown away again — nothing is added to the scene the player sees;
///   * a single picture, loaded directly.
///
/// Everything lands in SkinCreator\Images\Other with the same sidecar the rest of the Dump writes, and an
/// existing file is kept, never overwritten.
/// </summary>
internal static class NamedDump
{
    private const string Prefix = "[SkinForge][ByName]";

    /// <summary>The two startup panels, offered as buttons because they are the ones nobody can catch.</summary>
    internal static readonly (string Label, string Prefab)[] Suggestions =
    {
        ("Startup warning", "PnlWarning"),
        ("Headphone screen", "PnlHeadsetTip"),
    };

    internal static string Status { get; private set; } = string.Empty;

    internal static void Dump(string name)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            Status = "Type the name of a prefab or a picture first.";
            return;
        }

        try
        {
            ResourcesManager? resources = ResourcesManager.instance;
            if (resources == null)
            {
                Status = "The game's resource manager is not ready yet.";
                return;
            }

            if (global::SkinForger.HexieBlock.HexiePolicy.IsHexieName(name))
            {
                Status = $"{name} is not available in this build.";
                MelonLogger.Msg($"{Prefix} {Status}");
                return;
            }

            int written = 0, kept = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            // A prefab first: that is what a whole screen is.
            GameObject? prefab = null;
            try { prefab = resources.LoadFromName<GameObject>(name); }
            catch (Exception) { /* not a prefab: the picture path below still applies */ }

            if (prefab != null)
            {
                GameObject copy = UnityEngine.Object.Instantiate(prefab);
                copy.SetActive(false);                       // never let it reach the player's screen
                try
                {
                    foreach (Texture2D texture in PicturesIn(copy))
                        Write(texture, seen, ref written, ref kept);
                }
                finally { UnityEngine.Object.Destroy(copy); }
            }
            else
            {
                Texture2D? texture = null;
                try { texture = resources.LoadFromName<Texture2D>(name); }
                catch (Exception) { }
                if (texture == null)
                    try { texture = resources.LoadFromName<Sprite>(name)?.texture; }
                    catch (Exception) { }
                if (texture == null)
                {
                    Status = $"The game does not know a prefab or picture called \"{name}\".";
                    MelonLogger.Msg($"{Prefix} {Status}");
                    return;
                }
                Write(texture, seen, ref written, ref kept);
            }

            Status = written == 0 && kept == 0
                ? $"{name} holds no picture that could be read."
                : $"{name}: {written} written to Images\\Other" + (kept > 0 ? $", {kept} already there" : string.Empty);
            MelonLogger.Msg($"{Prefix} {Status}");
        }
        catch (Exception exception)
        {
            Status = "Failed: " + exception.GetBaseException().Message;
            MelonLogger.Warning($"{Prefix} {name}: {exception.GetBaseException().Message}");
        }
    }

    /// <summary>Every picture the prefab's renderers and UI graphics draw, each one once.</summary>
    private static IEnumerable<Texture2D> PicturesIn(GameObject root)
    {
        var found = new List<Texture2D>();
        try
        {
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                Material material = renderer.sharedMaterial;
                if (material == null || material.Pointer == IntPtr.Zero) continue;
                Texture2D? flat = material.mainTexture?.TryCast<Texture2D>();
                if (flat != null) found.Add(flat);
            }
            foreach (Graphic graphic in root.GetComponentsInChildren<Graphic>(true))
            {
                Texture2D? flat = graphic.mainTexture?.TryCast<Texture2D>();
                if (flat != null) found.Add(flat);
            }
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"{Prefix} reading {root.name}: {exception.GetBaseException().Message}");
        }
        return found;
    }

    private static void Write(Texture2D texture, HashSet<string> seen, ref int written, ref int kept)
    {
        string name = texture.name ?? string.Empty;
        if (name.Length == 0 || !seen.Add(name)) return;
        if (global::SkinForger.HexieBlock.HexiePolicy.IsHexieName(name)) return;

        string folder = Path.Combine(SkinForgerPaths.Creator, SeenTextures.OtherFolder);
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, Safe(name) + ".png");
        if (File.Exists(file)) { kept++; return; }
        if (!CursorDump.WriteTexture(texture, file)) return;
        SeenTextures.WriteSidecarFor(file, texture.width, texture.height, "loaded by name", "UI");
        written++;
        MelonLogger.Msg($"{Prefix} wrote {Path.GetFileName(file)} ({texture.width}x{texture.height})");
    }

    private static string Safe(string name)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return name.Trim().Length == 0 ? "picture" : name.Trim();
    }
}
