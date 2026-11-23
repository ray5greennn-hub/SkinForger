using Il2CppAssets.Scripts.PeroTools.Commons;
using Il2CppAssets.Scripts.PeroTools.Managers;
using Il2CppPeroTools2.Resources;
using MelonLoader;
using SkinForger.Main.Pure;
using UnityEngine;

namespace SkinForger.Main.Unity;

/// <summary>
/// The game's show prefabs: which costume each one belongs to, and how to bring one into the scene so the
/// Dump can read its atlases.
///
/// The index itself is <see cref="PrefabIndex"/> (pure and tested); this is the part that has to ask the
/// game. The non-localised config tables are used, because the prefab names are the same in every language.
/// </summary>
public static class GamePrefabs
{
    private const string Prefix = "[SkinForge][Main][Prefabs]";
    public const string CharacterTable = "character";
    public const string ElfinTable = "elfin";

    private static List<PrefabOwner>? _owners;

    /// <summary>Every known show prefab, longest name first. Empty while the game's config is not loaded.</summary>
    public static IReadOnlyList<PrefabOwner> Owners => _owners ??= Build();

    /// <summary>Forgets the index; the next use asks the game again (after a Reload).</summary>
    public static void Forget() => _owners = null;

    /// <summary>The costume a Spine object belongs to, or null when its name matches no known prefab.</summary>
    public static PrefabOwner? OwnerOf(string? objectName) => PrefabIndex.Match(Owners, objectName);

    /// <summary>The prefabs of one costume, in the order the Dump should spawn them.</summary>
    public static List<string> PrefabsOf(int index, bool elfin) => PrefabIndex.PrefabsOf(Owners, index, elfin);

    private static List<PrefabOwner> Build()
    {
        List<PrefabOwner> owners = PrefabIndex.Build(TableText(CharacterTable), TableText(ElfinTable));
        MelonLogger.Msg($"{Prefix} {owners.Count} show prefabs indexed " +
                        $"({owners.Count(o => !o.Elfin)} character, {owners.Count(o => o.Elfin)} elfin)");
        return owners;
    }

    /// <summary>A config table as JSON text; see GameRoster for why this call must be statically typed.</summary>
    private static string? TableText(string table)
    {
        try
        {
            ConfigManager? config = Singleton<ConfigManager>.instance;
            var rows = config?.GetJson(table, false);
            if (rows == null) return null;
            string text = rows.ToString();
            return text.Length > 0 && (text[0] == '[' || text[0] == '{') ? text : null;
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"{Prefix} {table}: {exception.GetBaseException().Message}");
            return null;
        }
    }

    /// <summary>
    /// Brings a show prefab into the scene, hands it to <paramref name="use"/>, and takes it away again.
    /// The copy is parked far from the camera and switched off, so nothing of it is ever seen: the Dump
    /// only needs its atlases to exist for a moment.
    /// </summary>
    public static bool Spawn(string prefabName, Action<GameObject> use)
    {
        if (string.IsNullOrEmpty(prefabName) || DumpNaming.IsCensoredName(prefabName)) return false;

        ResourcesManager? resources = ResourcesManager.instance;
        if (resources == null)
        {
            MelonLogger.Warning($"{Prefix} the game's resource manager is not ready");
            return false;
        }

        GameObject? prefab;
        try { prefab = resources.LoadFromName<GameObject>(prefabName); }
        catch (Exception exception)
        {
            MelonLogger.Warning($"{Prefix} {prefabName}: {exception.GetBaseException().Message}");
            return false;
        }
        if (prefab == null || prefab.Pointer == IntPtr.Zero)
        {
            MelonLogger.Warning($"{Prefix} {prefabName}: the game has no such prefab");
            return false;
        }

        GameObject? copy = null;
        try
        {
            copy = UnityEngine.Object.Instantiate(prefab);
            copy.name = prefabName;
            copy.transform.position = new Vector3(10000f, 10000f, 0f);
            use(copy);
            return true;
        }
        catch (Exception exception)
        {
            MelonLogger.Warning($"{Prefix} {prefabName}: {exception.GetBaseException().Message}");
            return false;
        }
        finally
        {
            if (copy != null && copy.Pointer != IntPtr.Zero) UnityEngine.Object.Destroy(copy);
        }
    }
}
