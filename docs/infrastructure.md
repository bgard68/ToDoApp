# Infrastructure — the Dapper data layer

How persistence works on the `refactor/dapper` branch. All of this lives under
`src/TodoApp.Infrastructure/Persistence/`.

## Component map

| Component | File | Role |
|-----------|------|------|
| `IDbConnectionFactory` / `DbConnectionFactory` | `Persistence/*.cs` | Creates a provider-specific ADO.NET connection; opens SQL Server with transient-retry. **Singleton.** |
| `IDbConnectionContext` / `DbConnectionContext` | `Persistence/*.cs` | Owns **one** open connection per DI scope + the ambient transaction. **Scoped.** |
| `IUnitOfWork` / `UnitOfWork` | `Application` / `Persistence` | `ExecuteInTransactionAsync(...)` around multi-write flows. **Scoped.** |
| `*Repository` | `Persistence/Repositories/` | Hand-written SQL per aggregate, via Dapper. **Scoped.** |
| `DapperConfig` + type handlers | `Persistence/Dapper/` | Registers `DateTimeOffset`↔ticks and `Guid`↔text handlers once. |
| `ISchemaInitializer` / `SchemaInitializer` | `Persistence/*.cs` | Runs idempotent DDL for the active provider (replaces `EnsureCreated`). |
| `Schema.Sqlite.sql` / `Schema.SqlServer.sql` | `Persistence/Schema/` | Embedded, per-dialect DDL. |
| `DbExceptionExtensions` | `Persistence/*.cs` | Maps provider unique-violation errors to a neutral `DuplicateKeyException`. |

Registration is in `Infrastructure/DependencyInjection.cs` (`AddInfrastructure`).

## Connection & transaction model

EF's scoped `DbContext` gave you one connection and one `SaveChanges` unit of work per request. The
Dapper layer reproduces that with two scoped services:

- **`DbConnectionContext`** lazily opens a single connection the first time a repository asks for it,
  and holds the current `DbTransaction`. Every repository in the request shares it, so their commands
  run on the same connection and enlist in the same transaction.
- **`UnitOfWork.ExecuteInTransactionAsync`** begins a transaction on that shared connection, runs the
  delegate, and commits (or rolls back on any exception). Only the three multi-write flows use it:
  **register**, **Google sign-in** (create user + categories + external login), and **refresh-token
  rotation**. Single-write handlers just call the repository and rely on autocommit.

The factory reproduces EF's `EnableRetryOnFailure` for Azure SQL: `CreateOpenAsync` retries transient
`SqlException`s (including error `-2`, the timeout while a serverless database wakes from auto-pause)
with capped exponential backoff.

## Repositories & entity materialization

Repositories are thin: a `SELECT` with the aggregate's columns, Dapper maps rows straight onto the
domain entities. This works **without persistence POCOs or AutoMapper** because the entities have a
private parameterless constructor and private-set properties, and EF used convention column names
(= property names) — so Dapper materializes them directly via the non-public constructor + setters.
Encapsulation is preserved; the domain objects are the read model.

Writes pass explicit parameter objects (enums cast to `int`), and inserts use a provider-specific
identity fetch (`SELECT last_insert_rowid()` on SQLite, `SELECT CAST(SCOPE_IDENTITY() AS INT)` on SQL
Server) surfaced by `RepositoryBase.InsertAsync`.

## Type handlers — and the one gotcha

`DateTimeOffset` is stored as a UTC-tick `long` (so SQLite can order/compare timestamps, matching what
the old EF value converter wrote), and `Guid` is round-tripped as text for cross-provider consistency.

> **Gotcha:** `DateTimeOffset` and `Guid` are in Dapper's built-in type map, so `LookupDbType`
> short-circuits and never consults a custom handler **on the write path**. The fix is
> `SqlMapper.RemoveTypeMap(typeof(DateTimeOffset))` (and `Guid`) in `DapperConfig` *before* adding the
> handlers, so they own both reads and writes. Without it, writes silently stored ISO strings and reads
> blew up. See [refactor-bugs.md](refactor-bugs.md#1-datetimeoffsetguid-handlers-ignored-on-writes).

## Optimistic concurrency

`TodoItem.ConcurrencyToken` (a `Guid`) is regenerated on every mutation. The repository update is:

```sql
UPDATE TodoItems SET …, ConcurrencyToken = @NewToken
WHERE Id = @Id AND UserId = @UserId AND ConcurrencyToken = @ExpectedToken
```

`UpdateAsync` returns `false` when zero rows match (the token moved on) and the handler throws
`ConcurrencyConflictException` → HTTP **409**, with a fresh snapshot of the current row.

## Schema management

`EnsureCreated()` is gone. `SchemaInitializer` executes an embedded, **idempotent** DDL script chosen
by provider at startup (and in the test harness):

- `Schema.Sqlite.sql` — `CREATE TABLE IF NOT EXISTS …`, tick columns as `INTEGER`, `Guid` as `TEXT`.
- `Schema.SqlServer.sql` — `IF OBJECT_ID(...) IS NULL CREATE TABLE …`, `bigint` ticks, `uniqueidentifier`,
  `bit`. **`Category.UserId` uses `NO ACTION`** (not cascade) to avoid SQL Server's multiple-cascade-paths
  error — the same reason the old EF config used `ClientCascade`.

Because the SQL Server script is guarded by existence checks, it's safe to run against the shared
production database (whose tables EF originally created) — it simply skips creation.

> Production migration path: for real schema evolution, adopt a versioned runner such as **DbUp**. The
> idempotent scripts here intentionally mirror the project's original "no migrations / `EnsureCreated`"
> approach.

## Dual-provider dialect differences

Selected by `Database:Provider` (`Sqlite` default, `SqlServer` for Azure). The handful of dialect
divergences the repositories account for:

| Concern | SQLite | SQL Server |
|---------|--------|-----------|
| New identity | `SELECT last_insert_rowid()` | `SELECT CAST(SCOPE_IDENTITY() AS INT)` |
| Unique-violation code | `SqliteErrorCode 19` | `SqlException 2601 / 2627` |
| FK enforcement | off by default → factory sets `Foreign Keys=True` | always on |
| Search | `LIKE @p ESCAPE '\'` | `LIKE @p ESCAPE '\'` (same) |

Search (`Title/Description LIKE`), ordering (tick columns), and enum/bool mapping are identical across
both. See [testing.md](testing.md) for how both providers are verified.
