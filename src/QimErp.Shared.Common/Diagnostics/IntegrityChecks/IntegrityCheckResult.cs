namespace QimErp.Shared.Common.Diagnostics.IntegrityChecks;

/// <summary>
/// Outcome of one <see cref="IIntegrityCheck"/> run. Mirrors the DATA-06
/// <c>IntegrityRuns (RunAt, TenantId, Check, Count, SampleIds)</c> shape, minus RunAt/TenantId
/// which the caller attaches when it logs or persists the result.
/// </summary>
public sealed record IntegrityCheckResult(string CheckName, long Count, IReadOnlyList<string>? SampleIds = null)
{
    /// <summary>Findings this service does not own, keyed by owner. Excluded from <see cref="Count"/> and <see cref="HasFindings"/>.</summary>
    public IReadOnlyDictionary<string, long>? NotOwnedCounts { get; init; }

    public bool HasFindings => Count > 0;

    public static IntegrityCheckResult Clean(string checkName) => new(checkName, 0);
}
