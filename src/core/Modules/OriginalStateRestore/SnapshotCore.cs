namespace SkinForger.OriginalStateRestore;

internal enum RestoreStatus
{
    Restored,
    ValidationFailed,
    ApplyFailed,
    VerificationFailed,
    RollbackFailed
}

internal readonly record struct RestoreResult(RestoreStatus Status, string Detail)
{
    internal bool Succeeded => Status == RestoreStatus.Restored;
}

internal readonly record struct ReloadPreparationResult(
    bool Proceed,
    bool Rebased,
    bool CacheInvalidationPending,
    string Stage,
    string Detail);

internal static class SnapshotCore
{
    internal static T[] Clone<T>(T[] source) => (T[])source.Clone();

    internal static bool Equal<T>(T[] left, T[] right) where T : IEquatable<T>
    {
        if (left.Length != right.Length) return false;
        for (int index = 0; index < left.Length; index++)
            if (!left[index].Equals(right[index])) return false;
        return true;
    }

    internal static RestoreResult Execute(
        Func<string?> validate,
        Action apply,
        Func<string?> verify,
        Action rollback)
    {
        string? validation = validate();
        if (validation != null) return new RestoreResult(RestoreStatus.ValidationFailed, validation);

        try
        {
            apply();
        }
        catch (Exception ex)
        {
            return RollBack(RestoreStatus.ApplyFailed, ex.GetBaseException().Message, rollback);
        }

        string? verification;
        try
        {
            verification = verify();
        }
        catch (Exception ex)
        {
            return RollBack(RestoreStatus.VerificationFailed, ex.GetBaseException().Message, rollback);
        }

        return verification == null
            ? new RestoreResult(RestoreStatus.Restored, "restored")
            : RollBack(RestoreStatus.VerificationFailed, verification, rollback);
    }

    internal static ReloadPreparationResult PrepareAssetReload(
        bool hasSnapshots,
        bool cacheInvalidationPending,
        Func<RestoreResult> restoreAllAndClear,
        Action invalidateReplacementCaches)
    {
        if (!hasSnapshots && !cacheInvalidationPending)
            return new ReloadPreparationResult(true, false, false, "none", "no-snapshot");

        bool rebased = false;
        if (hasSnapshots)
        {
            RestoreResult restore = restoreAllAndClear();
            if (!restore.Succeeded)
                return new ReloadPreparationResult(false, false, cacheInvalidationPending,
                    "restore", $"{restore.Status}:{restore.Detail}");
            rebased = true;
            cacheInvalidationPending = true;
        }

        try
        {
            invalidateReplacementCaches();
            return new ReloadPreparationResult(true, rebased, false, "ready", "restored-and-invalidated");
        }
        catch (Exception ex)
        {
            return new ReloadPreparationResult(false, rebased, true, "cache-invalidation",
                ex.GetBaseException().Message);
        }
    }

    private static RestoreResult RollBack(RestoreStatus failure, string detail, Action rollback)
    {
        try
        {
            rollback();
            return new RestoreResult(failure, detail);
        }
        catch (Exception rollbackError)
        {
            return new RestoreResult(RestoreStatus.RollbackFailed,
                $"{failure}:{detail}; rollback:{rollbackError.GetBaseException().Message}");
        }
    }
}
