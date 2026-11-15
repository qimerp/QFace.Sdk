using Npgsql;

namespace QimErp.Shared.Common.Outbox;

internal static class OutboxFailureText
{
    /// <summary>Never includes Postgres MessageText, Detail, Where or Hint: they can carry row values.</summary>
    public static string Describe(Exception exception)
    {
        if (FindPostgres(exception) is { } postgres)
        {
            return $"{exception.GetType().FullName}: Postgres {postgres.SqlState} " +
                   $"constraint={postgres.ConstraintName} " +
                   $"table={postgres.SchemaName}.{postgres.TableName} " +
                   $"column={postgres.ColumnName}";
        }

        var innermost = exception;
        while (innermost.InnerException is not null)
            innermost = innermost.InnerException;

        return $"{innermost.GetType().FullName}: {innermost.Message}";
    }

    /// <summary>Null when the chain holds a PostgresException, whose ToString() carries row values.</summary>
    public static Exception? LoggableCause(Exception exception) =>
        FindPostgres(exception) is null ? exception : null;

    private static PostgresException? FindPostgres(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
                return postgres;
        }

        return null;
    }
}
