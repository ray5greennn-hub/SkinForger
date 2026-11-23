namespace SkinForger.BattleAtlasEdgeIsolation;

internal static class EdgeIsolationPolicy
{
    public static bool ShouldActivate(bool elfin, bool contextKnown, bool preview) =>
        !elfin && (!contextKnown || !preview);
}
