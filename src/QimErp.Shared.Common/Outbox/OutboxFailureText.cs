using Npgsql;

namespace QimErp.Shared.Common.Outbox;

internal static class OutboxFailureText
{
    /// <summary>Never includes Postgres MessageText, Detail, Where or Hint: they can carry row values.</summary>
    public static string Describe(Exception exception)
    {
        var innermost = exception;
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return $"{exception.GetType().FullName}: Postgres {postgres.SqlState} " +
                       $"constraint={postgres.ConstraintName} " +
                       $"table={postgres.SchemaName}.{postgres.TableName} " +
                       $"column={postgres.ColumnName}";
            }

            innermost = current;
        }

        return $"{innermost.GetType().FullName}: {innermost.Message}";
    }
}
