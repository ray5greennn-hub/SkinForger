namespace SkinForger.Main.Pure;

/// <summary>What a costume can be shown as, in the order the buttons appear on its row.</summary>
/// <param name="Position">The value stored in Saved.json: <see cref="SelectionStore.Original"/>, or the
/// index of this skin in the costume's skin list.</param>
/// <param name="Label">The button's caption.</param>
/// <param name="Directory">Where the images come from; empty for the game's own art.</param>
public sealed record SkinChoice(int Position, string Label, string Directory)
{
    public bool IsOriginal => Position == SelectionStore.Original;
}

/// <summary>One row of the Image page: a character or elfin, its choices, and which one is showing.</summary>
public sealed record CostumeRow(int Index, string Name, bool Elfin, IReadOnlyList<SkinChoice> Choices, int Selected)
{
    /// <summary>The choice currently showing, which is Original when the stored position is unknown.</summary>
    public SkinChoice Current =>
        Choices.FirstOrDefault(c => c.Position == Selected) ?? Choices[0];

    /// <summary>True while this costume shows the game's own art.</summary>
    public bool IsOriginal => Current.IsOriginal;

    /// <summary>True when the creator has a folder of images for this costume (the Dump has run).</summary>
    public bool HasEdited => Choices.Any(c => c.Label == WindowModel.EditedLabel);
}

/// <summary>
/// The Image page as data: which rows exist, what each row can be set to, and which one is showing.
/// Pure, so the whole page can be checked without drawing anything.
///
/// The buttons on a row are, in order: Original, Dump, then one per skin the costume has. "Edited" is the
/// creator's own folder under SkinCreator\Images — upstream SkinChanger called that button "Root", which
/// told a player nothing; the authored window labels it for what it holds (2026-11-17, user's choice).
///
/// Positions are NOT relabelled: Saved.json keeps meaning what it meant (-1 = the game's own art, otherwise
/// the index into the costume's skin list), because Core reads the choice through the same contract.
/// </summary>
public static class WindowModel
{
    public const string OriginalLabel = "Original";
    public const string DumpLabel = "Dump";
    /// <summary>The creator's own images. Upstream's name for this was "Root".</summary>
    public const string EditedLabel = "Edited";

    /// <summary>The choices of one costume: the game's own art first, then its skins in list order.</summary>
    public static List<SkinChoice> ChoicesOf(string costume, IReadOnlyList<string> skinDirectories, string creatorRoot)
    {
        var choices = new List<SkinChoice> { new(SelectionStore.Original, OriginalLabel, string.Empty) };
        string edited = Path.Combine(creatorRoot, costume);
        for (int position = 0; position < skinDirectories.Count; position++)
        {
            string directory = skinDirectories[position];
            bool isEdited = SamePath(directory, edited);
            choices.Add(new SkinChoice(position, isEdited ? EditedLabel : LabelOf(directory), directory));
        }
        return choices;
    }

    /// <summary>The caption of a skin that is not the creator's own work: the folder's own name.</summary>
    public static string LabelOf(string directory)
    {
        string name = directory.TrimEnd('\\', '/');
        int slash = name.LastIndexOfAny(new[] { '\\', '/' });
        return slash >= 0 ? name[(slash + 1)..] : name;
    }

    /// <summary>
    /// The rows of one list (characters or elfins). <paramref name="skinsOf"/> gives a costume's skin
    /// directories, lowest position first; <paramref name="selectionOf"/> gives the stored position.
    /// </summary>
    public static List<CostumeRow> Rows(IReadOnlyList<RosterEntry> roster, bool elfin, string creatorRoot,
        Func<string, IReadOnlyList<string>> skinsOf, Func<int, int> selectionOf)
    {
        var rows = new List<CostumeRow>();
        foreach (RosterEntry entry in roster)
        {
            if (DumpNaming.IsCensoredName(entry.Name)) continue;
            List<SkinChoice> choices = ChoicesOf(entry.Name, skinsOf(entry.Name), creatorRoot);
            rows.Add(new CostumeRow(entry.Index, entry.Name, elfin, choices, selectionOf(entry.Index)));
        }
        return rows;
    }

    /// <summary>
    /// The position the Edited button sets, or <see cref="SelectionStore.Original"/> when this costume has
    /// no folder of the creator's own images yet (the Dump has not run for it).
    /// </summary>
    public static int EditedPosition(CostumeRow row) =>
        row.Choices.FirstOrDefault(c => c.Label == EditedLabel)?.Position ?? SelectionStore.Original;

    /// <summary>
    /// What clicking a choice should store. Clicking the choice that is already showing puts the costume
    /// back to the game's own art, so one button both applies and undoes.
    /// </summary>
    public static int PositionAfterClick(CostumeRow row, SkinChoice clicked) =>
        row.Selected == clicked.Position ? SelectionStore.Original : clicked.Position;

    /// <summary>Rows whose costume is not showing the game's own art.</summary>
    public static IEnumerable<CostumeRow> Replaced(IEnumerable<CostumeRow> rows) => rows.Where(r => !r.IsOriginal);

    public static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/').Replace('/', '\\'), b.TrimEnd('\\', '/').Replace('/', '\\'),
                      StringComparison.OrdinalIgnoreCase);
}
