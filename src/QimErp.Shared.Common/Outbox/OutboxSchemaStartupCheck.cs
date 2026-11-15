using Microsoft.Extensions.Hosting;

namespace QimErp.Shared.Common.Outbox;

internal sealed class OutboxSchemaStartupCheck<TContext>(
    IServiceScopeFactory scopes,
    ILogger<OutboxSchemaStartupCheck<TContext>> logger) : IHostedService
    where TContext : DbContext
{
    internal const string RequiredColumn = nameof(OutboxMessage.Origin);

    internal Func<TContext, OutboxTableName, CancellationToken, Task<OutboxSchemaState>> Probe { get; init; } = ProbeAsync;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        if (!IsPostgres(db))
            return;

        var entity = db.Model.FindEntityType(typeof(OutboxMessage));
        if (entity is null)
            return;

        var table = new OutboxTableName(entity.GetSchema(), entity.GetTableName() ?? "OutboxMessages");
        OutboxSchemaState state;
        try
        {
            state = await Probe(db, table, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "Could not verify the outbox table {Table} for {Context}; refusing to start.",
                table, typeof(TContext).Name);
            throw;
        }

        if (state == OutboxSchemaState.Ready)
            return;

        var message = state == OutboxSchemaState.TableMissing
            ? $"Outbox table {table} does not exist for {typeof(TContext).Name}. Apply the module's EF migrations before starting the host."
            : $"Outbox table {table} has no \"{RequiredColumn}\" column for {typeof(TContext).Name}. " +
              $"QimErp.Shared.Common 3.11.55+ writes and claims outbox rows by {RequiredColumn}, so every save that writes an outbox row would fail with 42703. " +
              "Add and apply the AddOutboxOrigin migration (docs/outbox-data-09.md, \"Consumer upgrade steps\"), " +
              "or pin QimErp.Shared.Common below 3.11.55.";
        logger.LogCritical("{Message}", message);
        throw new InvalidOperationException(message);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal static FormattableString ProbeSql(OutboxTableName table) =>
        $"""
        SELECT
            EXISTS (SELECT 1 FROM information_schema.tables
                     WHERE table_schema = COALESCE(NULLIF({table.Schema ?? string.Empty}, ''), current_schema())
                       AND table_name = {table.Table}) AS "TableExists",
            EXISTS (SELECT 1 FROM information_schema.columns
                     WHERE table_schema = COALESCE(NULLIF({table.Schema ?? string.Empty}, ''), current_schema())
                       AND table_name = {table.Table}
                       AND column_name = {RequiredColumn}) AS "ColumnExists"
        """;

    private static async Task<OutboxSchemaState> ProbeAsync(TContext db, OutboxTableName table, CancellationToken cancellationToken)
    {
        var row = (await db.Database.SqlQuery<OutboxSchemaProbeRow>(ProbeSql(table)).ToListAsync(cancellationToken)).Single();
        if (!row.TableExists)
            return OutboxSchemaState.TableMissing;
        return row.ColumnExists ? OutboxSchemaState.Ready : OutboxSchemaState.ColumnMissing;
    }

    private static bool IsPostgres(DbContext db) =>
        db.Database.IsRelational()
        && (db.Database.ProviderName ?? string.Empty).Contains("Npgsql", StringComparison.OrdinalIgnoreCase);
}

internal enum OutboxSchemaState
{
    Ready,
    TableMissing,
    ColumnMissing,
}

internal sealed record OutboxTableName(string? Schema, string Table)
{
    public override string ToString() => Schema is null ? $"\"{Table}\"" : $"\"{Schema}\".\"{Table}\"";
}

internal sealed class OutboxSchemaProbeRow
{
    public bool TableExists { get; set; }

    public bool ColumnExists { get; set; }
}
