using System.Text.Json;

namespace SkinForger.Main.Pure;

/// <summary>A show prefab and the costume it belongs to.</summary>
/// <param name="Prefab">The prefab's resource name, as the config spells it.</param>
/// <param name="Index">The costume's row in its table — the key Saved.json uses.</param>
/// <param name="Elfin">Which table it came from.</param>
/// <param name="Cat">A "cat" skin: frame-by-frame animation rather than a Spine skeleton.</param>
public sealed record PrefabOwner(string Prefab, int Index, bool Elfin, bool Cat)
{
    public override string ToString() => $"{Prefab} -> {(Elfin ? "elfin" : "character")} {Index}{(Cat ? " (cat)" : string.Empty)}";
}

/// <summary>
/// Which costume a Spine object on screen belongs to.
///
/// The game's config gives every character and elfin the names of its show prefabs (its main-menu, battle,
/// victory and fail appearances), and a character with more than one skin — Maid, Bassist — carries a
/// "skins" array whose entries have the same fields again. A skeleton's GameObject is named after the
/// prefab it came from, so the costume is found by matching that name against this index.
///
/// Two rules that matter:
///   * LONGEST NAME FIRST. "char_1_rock" is a prefix of "char_1_rock_cat"; matching the short one first
///     would file every cat-skin skeleton under the wrong skin. SkinForger.Core's RoleSkins keeps its own
///     owner list sorted the same way, for the same reason.
///   * A censored skin ("hexie": true) contributes nothing, and neither does a censored prefab name.
///
/// Field names are not hard-coded beyond what is needed: every string property whose name ends in "show",
/// plus "prefabName" / "prefab", is treated as a prefab name. That way a field the game adds later is
/// picked up, and a field this code has never heard of cannot silently drop a costume.
/// Pure (no Unity): the game hands the table over as JSON text.
/// </summary>
public static class PrefabIndex
{
    public const string SkinsField = "skins";
    public const string CensoredField = "hexie";
    public const string CatField = "isCat";

    /// <summary>Owners from one config table, unsorted.</summary>
    public static List<PrefabOwner> Parse(string? json, bool elfin)
    {
        var owners = new List<PrefabOwner>();
        if (string.IsNullOrWhiteSpace(json)) return owners;

        JsonDocument document;
        try { document = JsonDocument.Parse(json!, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException) { return owners; }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array) return owners;
            int index = 0;
            foreach (JsonElement row in document.RootElement.EnumerateArray())
            {
                int here = index++;
                if (row.ValueKind != JsonValueKind.Object) continue;
                Collect(row, here, elfin, cat: IsTrue(row, CatField), owners);

                if (!row.TryGetProperty(SkinsField, out JsonElement skins) || skins.ValueKind != JsonValueKind.Array) continue;
                foreach (JsonElement skin in skins.EnumerateArray())
                {
                    if (skin.ValueKind != JsonValueKind.Object) continue;
                    if (IsTrue(skin, CensoredField)) continue;          // a censored skin is not dumped or replaced
                    Collect(skin, here, elfin, cat: IsTrue(skin, CatField), owners);
                }
            }
        }
        return owners;
    }

    /// <summary>Both tables, sorted the way <see cref="Match"/> needs: longest prefab name first.</summary>
    public static List<PrefabOwner> Build(string? characterJson, string? elfinJson)
    {
        var owners = new List<PrefabOwner>();
        owners.AddRange(Parse(characterJson, elfin: false));
        owners.AddRange(Parse(elfinJson, elfin: true));
        return Sorted(owners);
    }

    /// <summary>Longest prefab name first, then alphabetically so the order never depends on chance.</summary>
    public static List<PrefabOwner> Sorted(IEnumerable<PrefabOwner> owners) =>
        owners.GroupBy(o => o.Prefab + "|" + o.Index + "|" + o.Elfin, StringComparer.OrdinalIgnoreCase)
              .Select(g => g.First())
              .OrderByDescending(o => o.Prefab.Length)
              .ThenBy(o => o.Prefab, StringComparer.OrdinalIgnoreCase)
              .ToList();

    /// <summary>
    /// The owner of a Spine object, by its name, or null when none of the known prefabs appears in it.
    /// Unity appends "(Clone)" and parents may add prefixes, so the name is searched, not compared.
    /// </summary>
    public static PrefabOwner? Match(IReadOnlyList<PrefabOwner> owners, string? objectName)
    {
        if (string.IsNullOrEmpty(objectName)) return null;
        foreach (PrefabOwner owner in owners)
            if (objectName!.IndexOf(owner.Prefab, StringComparison.OrdinalIgnoreCase) >= 0) return owner;
        return null;
    }

    /// <summary>Every prefab of one costume, longest first (the order the Dump spawns them in).</summary>
    public static List<string> PrefabsOf(IReadOnlyList<PrefabOwner> owners, int index, bool elfin) =>
        owners.Where(o => o.Index == index && o.Elfin == elfin).Select(o => o.Prefab).ToList();

    private static void Collect(JsonElement row, int index, bool elfin, bool cat, List<PrefabOwner> into)
    {
        foreach (JsonProperty property in row.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String) continue;
            if (!IsPrefabField(property.Name)) continue;
            string name = (property.Value.GetString() ?? string.Empty).Trim();
            if (name.Length == 0 || DumpNaming.IsCensoredName(name)) continue;
            into.Add(new PrefabOwner(name, index, elfin, cat));
        }
    }

    /// <summary>A config field that names a prefab.</summary>
    public static bool IsPrefabField(string? name) =>
        !string.IsNullOrEmpty(name) &&
        (name!.EndsWith("show", StringComparison.OrdinalIgnoreCase) ||
         name.Equals("prefabName", StringComparison.OrdinalIgnoreCase) ||
         name.Equals("prefab", StringComparison.OrdinalIgnoreCase)) &&
        !DumpNaming.IsCensoredName(name);

    private static bool IsTrue(JsonElement row, string field) =>
        row.TryGetProperty(field, out JsonElement value) && value.ValueKind == JsonValueKind.True;
}
