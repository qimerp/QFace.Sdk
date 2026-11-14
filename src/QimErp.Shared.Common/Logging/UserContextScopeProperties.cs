namespace QimErp.Shared.Common.Logging;

// Deliberately does NOT put UserEmail/UserName into the log scope: BeginScope values are
// carried on every log line emitted for the lifetime of the scope (a full HTTP request or
// consumer action), so raw PII placed here leaks into every downstream log sink. TenantId
// and UserId (an opaque identifier, not an email) are the only identity fields safe to
// carry ambiently. Call sites that need email/name for their own logic (audit context,
// notifications) still receive them as regular parameters — only the logging scope is
// restricted here.
internal static class UserContextScopeProperties
{
    public static List<KeyValuePair<string, object>> From(
        string? tenantId,
        string? userEmail,
        string? userName,
        string? userId)
    {
        return From(tenantId, userId);
    }

    public static List<KeyValuePair<string, object>> From(string? tenantId, string? userId)
    {
        var items = new List<KeyValuePair<string, object>>(2);
        if (!string.IsNullOrWhiteSpace(tenantId))
            items.Add(new KeyValuePair<string, object>("TenantId", tenantId));
        if (!string.IsNullOrWhiteSpace(userId) &&
            !string.Equals(userId, "system", StringComparison.OrdinalIgnoreCase))
            items.Add(new KeyValuePair<string, object>("UserId", userId));
        return items;
    }
}

internal sealed class UserContextScopeState(string? tenantId, string? userId, AnonymousRequestContext? anonymousRequest)
    : IReadOnlyList<KeyValuePair<string, object>>
{
    private List<KeyValuePair<string, object>> Items =>
        UserContextScopeProperties.From(tenantId, anonymousRequest?.IsAnonymous == true ? null : userId);

    public int Count => Items.Count;

    public KeyValuePair<string, object> this[int index] => Items[index];

    public IEnumerator<KeyValuePair<string, object>> GetEnumerator() => Items.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
