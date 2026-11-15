# Durable outbox (DATA-09) — module migration

> **Breaking schema step (QimErp.Shared.Common 3.11.55+).** The SDK writes and claims outbox rows by
> a new `"Origin"` column. A service that bumps the package without the `AddOutboxOrigin` migration
> would fail every save that writes an outbox row (Postgres 42703), and its InMemory tests would still
> pass. On PostgreSQL the host now refuses to start (logged at Critical) when the outbox table or its
> `"Origin"` column is missing; InMemory hosts skip the check. Follow
> [Consumer upgrade steps](#consumer-upgrade-steps-each-service-that-owns-an-outboxmessages-table)
> before deploying.

`AddDbContextWithOutbox*` now registers a **real same-transaction outbox** plus an Akka relay
(poller → consistent-hash router → dispatch workers), matching QFace Data Standard DATA-09 /
Appendix 1.

Previously the name was a misnomer: `AuditEntitySaveChangesInterceptor` published via MediatR /
started Temporal **after** `SaveChanges`, and `OutboxMessage` was unused. That gap is closed in
the SDK. Modules must stop `Tell` / Temporal-start after commit and add the table migration.

## What the SDK does

1. **`IOutbox.Add(...)`** inserts one `OutboxMessages` row per configured transport on the current
   `DbContext` (same unit of work as the business change).
2. **Domain events** raised with `entity.AddDomainEvent(...)` are pulled during `SavingChanges`
   and written to the outbox (not published post-commit).
3. **Workflow approval** (`InProgress` transition) enqueues `WorkflowApprovalRequired` for the
   **temporal** transport; the relay calls `IWorkflowTriggerBridge` after commit.
4. **Relay** (`OutboxRelayHostedService`) claims rows with a lease, dispatches by `EntityId`, and
   marks `ProcessedAt` only after the transport confirms (retries / dead-letter on failure).

## Forbidden (DATA-09)

```csharp
await db.SaveChangesAsync(ct);
actorService.Tell(new SyncEmployee(...));          // NO — in-memory mailbox, lost on crash
await temporal.StartWorkflowAsync(...);            // NO — gap before Temporal accepts start
```

## Required pattern

```csharp
employee.Rename(cmd.FirstName, cmd.LastName);
employee.AddDomainEvent(new EmployeeChangedEvent(...)); // or outbox.Add(...)

// Optional explicit API (same transaction):
outbox.Add(new EmployeeChangedEvent(...), employee.Id, employee.Version);

await db.SaveChangesAsync(ct);
// Do not Tell / start Temporal here — the relay delivers.
return Result.Success();
```

## EF migration (every module DbContext)

`ApplicationDbContext` maps `DbSet<OutboxMessage> OutboxMessages`. Add a migration in each
module that owns a database:

```bash
dotnet ef migrations add AddOutboxMessages --project <Module>.Infrastructure --startup-project <Module>.WebApi
dotnet ef database update
```

Table shape (Postgres):

| Column | Type |
| --- | --- |
| Id | uuid PK (UUIDv7) |
| TenantId | text |
| EventType | text |
| Transport | text (`temporal` / `inprocess` / `rabbitmq` / `kafka`) |
| EntityId | uuid |
| EntityVersion | bigint |
| Payload | jsonb |
| OccurredAt / NextAttemptAt | timestamptz |
| LockedUntil / LockedBy | timestamptz / text |
| Attempts | int |
| LastError | text |
| ProcessedAt / DeadAt | timestamptz |

| Origin | text, nullable (3.11.55+) |

Indexes: ready rows on `NextAttemptAt`, and on `(Origin, NextAttemptAt)` (`ix_outbox_origin_ready`, 3.11.55+),
both where `ProcessedAt` and `DeadAt` are null.

## Origin: one database, many machines (3.11.55+)

Laptops and the deployed services share one database per module. Every row records the writer's
effective `Temporal:TaskQueueSuffix` in `Origin` (`""` when deployed), and a relay claims only rows
of its own origin, so a laptop never runs or dispatches work written by Contabo or another laptop.

| Row `Origin` | Claimed by |
| --- | --- |
| `""` | the unsuffixed (deployed) relay |
| `"-local"`, `"-local-pd"`, ... | only a relay with that exact suffix |
| `NULL` (written by an SDK before 3.11.55) | only the unsuffixed (deployed) relay |

`IOutbox.Add` stamps the origin. Rows added directly (`db.OutboxMessages.Add(new OutboxMessage { ... })`)
are stamped by `AuditEntitySaveChangesInterceptor` when `Origin` is null, so contexts registered with
`AddDbContextWithOutbox*` are covered. A row whose writer never comes back (a laptop that went offline)
stays pending; replay it from that origin or update its `Origin` by hand.
`OutboxBackstopIntegrityCheck` counts only stuck rows of its own origin and reports other origins in
`IntegrityCheckResult.NotOwnedCounts` (logged as `data_integrity_not_owned`).

### Consumer upgrade steps (each service that owns an `OutboxMessages` table)

`OutboxMessageConfiguration` is applied by `ApplicationDbContext`, so bumping the package changes
the model and EF reports pending changes until a migration exists.

1. Bump `QimErp.Shared.Common` to 3.11.55 or later.
2. `dotnet ef migrations add AddOutboxOrigin --project <Infrastructure/Shared project> --startup-project <WebApi>`
   (once per DbContext that maps `OutboxMessage`).
3. Keep the scaffolded `AddColumn` (`Origin`, `text`, nullable; no default, so Postgres adds it
   without a rewrite). Replace the scaffolded `CreateIndex` with a concurrent build so writers are
   not blocked. Qualify every statement with the table's schema (`public` for most services,
   `reporting` for Reporting). A `CREATE INDEX CONCURRENTLY` that was interrupted leaves an invalid
   index that `IF NOT EXISTS` would keep, so drop an invalid one first:

   ```csharp
   const string schema = "public";

   migrationBuilder.Sql(
       $$"""
       SET LOCAL lock_timeout = '15s';
       DO $drop$
       BEGIN
           IF EXISTS (
               SELECT 1
                 FROM pg_index i
                 JOIN pg_class c ON c.oid = i.indexrelid
                 JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = '{{schema}}'
                  AND c.relname = 'ix_outbox_origin_ready'
                  AND NOT i.indisvalid) THEN
               DROP INDEX "{{schema}}".ix_outbox_origin_ready;
           END IF;
       END
       $drop$;
       """);

   migrationBuilder.Sql(
       $$"""
       CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_outbox_origin_ready
           ON "{{schema}}"."OutboxMessages" ("Origin", "NextAttemptAt")
           WHERE "ProcessedAt" IS NULL AND "DeadAt" IS NULL;
       """,
       suppressTransaction: true);
   ```

   `Down`, before the scaffolded `DropColumn`:

   ```csharp
   migrationBuilder.Sql(
       $$"""DROP INDEX CONCURRENTLY IF EXISTS "{{schema}}".ix_outbox_origin_ready;""",
       suppressTransaction: true);
   ```

   After applying, confirm the index is valid:

   ```sql
   SELECT i.indisvalid
     FROM pg_index i
     JOIN pg_class c ON c.oid = i.indexrelid
     JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname = 'public' AND c.relname = 'ix_outbox_origin_ready';
   ```
4. Apply the migration before any 3.11.55 binary of that service starts; otherwise the startup
   check stops the host. The change is additive, so binaries on older SDKs keep working against
   the migrated table.
5. Upgrade the deployed service and every developer machine. Until all of them run 3.11.55+, an
   older relay still claims rows of any origin.
6. Do not backfill `Origin` on existing rows: pending `NULL` rows belong to the deployed relay.

## Configuration

```json
{
  "Outbox": {
    "RelayEnabled": true,
    "PollInterval": "00:00:01",
    "BatchSize": 100,
    "DispatchWorkerCount": 8,
    "MaxAttempts": 10,
    "DefaultTransports": [ "inprocess" ],
    "Routing": {
      "EmployeeChangedEvent": [ "temporal" ],
      "LeaveApproved": [ "temporal" ],
      "WorkflowApprovalRequired": [ "temporal" ]
    },
    "Temporal": {
      "EmployeeChangedEvent": {
        "WorkflowType": "EmployeeSyncWorkflow",
        "TaskQueue": "qimerp-corehr-employee-sync"
      }
    }
  }
}
```

Notes:

- **Default transport is `inprocess`** so existing MediatR handlers keep working via the relay
  until modules opt into Temporal. Level **S** should set `DefaultTransports: ["temporal"]`
  (or per-event `Routing`) once workers exist.
- **`WorkflowApprovalRequired`** defaults to Temporal even without config.
- Set `Outbox:RelayEnabled=false` in unit-test hosts that should not start Akka.
- Register event CLR types for in-process deserialization when the type name is ambiguous:
  `services.AddOutboxEventType<EmployeeChangedEvent>();`
- RabbitMQ / Kafka transports are stubs until a decision log approves a bus (DATA-09); rows
  routed there will retry and eventually dead-letter.

## Package version

Shipped in **QimErp.Shared.Common 3.8.33+**. Bump module PackageReferences, then remove
post-save `Tell` / Temporal starts in Features.

## Compliance checklist

- [ ] Migration applied for `OutboxMessages`
- [ ] No `Tell` / `StartWorkflow` / `SignalWithStart` immediately after `SaveChanges` for
      cross-module hand-off
- [ ] Side effects use `AddDomainEvent` or `IOutbox.Add` before save
- [ ] Temporal routes configured for S-level sync events
- [ ] Relay left enabled in WebApi / worker hosts
