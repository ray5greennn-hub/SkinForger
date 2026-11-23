using System;
using System.Collections.Generic;

namespace SkinForger.Preview;

internal static class PreviewSharedCoreTransactionValidation
{
    internal static PreviewSharedCoreTransactionValidationResult Validate(
        IEnumerable<string> expected,
        IEnumerable<string> committed,
        IEnumerable<string> invalid)
    {
        HashSet<string> expectedSet = Collect(expected, out List<string> duplicateExpected);
        HashSet<string> committedSet = Collect(committed, out List<string> duplicateCommitted);
        HashSet<string> invalidSet = Collect(invalid, out _);

        var missing = new List<string>();
        foreach (string key in expectedSet)
            if (!committedSet.Contains(key)) missing.Add(key);

        var unexpected = new List<string>();
        foreach (string key in committedSet)
            if (!expectedSet.Contains(key)) unexpected.Add(key);

        missing.Sort(StringComparer.OrdinalIgnoreCase);
        unexpected.Sort(StringComparer.OrdinalIgnoreCase);
        duplicateExpected.Sort(StringComparer.OrdinalIgnoreCase);
        duplicateCommitted.Sort(StringComparer.OrdinalIgnoreCase);
        var invalidPlacements = new List<string>(invalidSet);
        invalidPlacements.Sort(StringComparer.OrdinalIgnoreCase);

        return new PreviewSharedCoreTransactionValidationResult(
            expectedSet.Count,
            committedSet.Count,
            missing.ToArray(),
            unexpected.ToArray(),
            duplicateExpected.ToArray(),
            duplicateCommitted.ToArray(),
            invalidPlacements.ToArray());
    }

    private static HashSet<string> Collect(IEnumerable<string> values, out List<string> duplicates)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        duplicates = new List<string>();
        foreach (string value in values)
        {
            if (string.IsNullOrEmpty(value)) continue;
            if (!result.Add(value)) duplicates.Add(value);
        }
        return result;
    }
}

internal sealed class PreviewSharedCoreTransactionValidationResult
{
    internal PreviewSharedCoreTransactionValidationResult(
        int expectedCount,
        int committedCount,
        string[] missing,
        string[] unexpected,
        string[] duplicateExpected,
        string[] duplicateCommitted,
        string[] invalidPlacements)
    {
        ExpectedCount = expectedCount;
        CommittedCount = committedCount;
        Missing = missing;
        Unexpected = unexpected;
        DuplicateExpected = duplicateExpected;
        DuplicateCommitted = duplicateCommitted;
        InvalidPlacements = invalidPlacements;
    }

    internal int ExpectedCount { get; }
    internal int CommittedCount { get; }
    internal string[] Missing { get; }
    internal string[] Unexpected { get; }
    internal string[] DuplicateExpected { get; }
    internal string[] DuplicateCommitted { get; }
    internal string[] InvalidPlacements { get; }

    internal bool Success =>
        Missing.Length == 0 &&
        Unexpected.Length == 0 &&
        DuplicateExpected.Length == 0 &&
        DuplicateCommitted.Length == 0 &&
        InvalidPlacements.Length == 0;
}
