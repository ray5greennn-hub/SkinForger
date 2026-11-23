namespace SkinForger.CharacterReplacement;

/// <summary>
/// What SkinForger is doing right now, while it is doing it.
///
/// This is the one type the old Main carried that has NO authored source anywhere
/// (CONTRACT.md), so it is written here from the only thing that defines it: how SkinForger.Core uses it.
/// Four Core modules ask the same question through reflection —
///
///     ReplacementExecutionContext.Current?.Preview        (bool)
///
/// — R4_12MeshCanvas, OriginalStateRestore, BattleAtlasEdgeIsolation and CharacterPolygonPackedRectRepair.
/// They need to know whether the work being done belongs to the Preview window or to the game itself,
/// because the Preview builds its own atlases and must not be treated like a character on screen.
///
/// The shape is therefore fixed: a static <see cref="Current"/> whose value has an instance property
/// <see cref="Preview"/>. Both are found by reflection with Public | NonPublic, so neither may be renamed.
/// Current is null when nothing is going on, and every reader already handles that.
///
/// It is a stack, not a flag: a Preview build can happen while a gameplay build is waiting, and the inner
/// one must not leave the outer one thinking it is a Preview.
/// </summary>
public sealed class ReplacementExecutionContext
{
    [ThreadStatic] private static Stack<ReplacementExecutionContext>? _stack;

    private ReplacementExecutionContext(bool preview, string what)
    {
        Preview = preview;
        What = what;
    }

    /// <summary>The work being done now, or null when SkinForger is not building anything.</summary>
    public static ReplacementExecutionContext? Current =>
        _stack != null && _stack.Count > 0 ? _stack.Peek() : null;

    /// <summary>True when this work belongs to the Preview window rather than to the game.</summary>
    public bool Preview { get; }

    /// <summary>What is being done, for the log ("dump", "apply", "preview").</summary>
    public string What { get; }

    /// <summary>True while anything is being built.</summary>
    public static bool Active => Current != null;

    /// <summary>
    /// Marks the work for as long as the returned value is alive:
    /// <c>using (ReplacementExecutionContext.Begin(preview: false, "apply")) { ... }</c>
    /// </summary>
    public static IDisposable Begin(bool preview, string what)
    {
        _stack ??= new Stack<ReplacementExecutionContext>();
        var context = new ReplacementExecutionContext(preview, what);
        _stack.Push(context);
        return new Scope(context);
    }

    public override string ToString() => $"{What}{(Preview ? " (preview)" : string.Empty)}";

    private sealed class Scope : IDisposable
    {
        private readonly ReplacementExecutionContext _context;
        private bool _ended;

        internal Scope(ReplacementExecutionContext context) => _context = context;

        public void Dispose()
        {
            if (_ended) return;
            _ended = true;
            // Pop only our own: an unbalanced inner scope must not unwind the outer one.
            if (_stack != null && _stack.Count > 0 && ReferenceEquals(_stack.Peek(), _context)) _stack.Pop();
        }
    }
}
