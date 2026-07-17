# Database Portability

How this project stays behavior-consistent across relational database providers — and
what it would take to support "any" database. Companion to [LESSONS.md](LESSONS.md),
which lists the specific gotchas hit in production.

> **← Back to the main [README](README.md).**

---

## The short version

Swapping the relational provider is mechanically a **one-line config change**: `AddInfrastructure`
in `src/TodoApp.Infrastructure/DependencyInjection.cs` reads `Database:Provider` and selects the EF
Core provider (`UseSqlite` vs `UseSqlServer`). Everything above it — MediatR handlers, DTOs, the
`IApplicationDbContext` interface — is provider-agnostic, and because the schema is built with
`EnsureCreated` (not migrations) there are no provider-specific migration files to regenerate.

But *mechanically swappable* is not the same as *behaves identically*. Databases disagree on
defaults, and every place code leans on a provider's default is a place a swap can silently change
meaning. The goal of this document is the stronger property: **identical behavior on any relational
provider**, achieved not by trusting each database to match, but by never depending on
database-specific behavior in the first place.

---

## The governing principle

**A business rule's semantics must never live in the database's provider-specific behavior.**

Enforce every rule in the domain/application layer, and use the database purely for storage plus
constraints that behave the same everywhere. When that holds, "works the same on SQLite, SQL Server,
and PostgreSQL" is a property you can prove with tests, not a hope.

---

## The behaviors that differ, and how each is neutralized

### 1. Unique-index case sensitivity — normalize, don't rely on collation

The one behavior that is genuinely provider-dependent unless handled explicitly. The category
uniqueness rule is a `(UserId, Name)` unique index. Providers disagree on whether `"Work"` and
`"work"` collide:

| Provider    | Default collation | `"Work"` vs `"work"` |
| ----------- | ----------------- | -------------------- |
| SQL Server  | case-insensitive  | **collide** (409)    |
| SQLite      | case-sensitive    | allowed              |
| PostgreSQL  | case-sensitive    | allowed              |

So a rule that *feels* enforced on Azure SQL quietly loosens on SQLite or Postgres.

**Fix (the ASP.NET Identity pattern):** store a normalized column and constrain on that.

- Add `NameNormalized` to the `Category` entity, set from the domain whenever the name changes
  (`Name.ToUpperInvariant()`).
- Move the unique index to `(UserId, NameNormalized)`.
- Keep the original `Name` for display.

Now uniqueness is defined by *your* normalization, identical on every provider regardless of
collation.

### 2. Cascade deletes — let EF decide, not the database

SQL Server rejects "multiple cascade paths" (error 1785); SQLite silently allows the same model.
The project already uses `DeleteBehavior.ClientCascade` on the `Category → User` FK
(`CategoryConfiguration.cs`), so EF Core — not the database — materializes and applies the cascade.
That is the provider-neutral choice and also keeps PostgreSQL happy.

**Policy:** no foreign key relies on DB-level cascade semantics for anything that matters.

### 3. Timestamps — already normalized

`ApplicationDbContext.ConfigureConventions` converts every `DateTimeOffset` to UTC ticks (a `long`)
via `DateTimeOffsetToUtcTicksConverter`, so ordering and comparison are byte-identical on every
provider. The trade-off is deliberate: no native `datetimeoffset`/`timestamptz` column, in exchange
for uniform behavior.

### 4. Optimistic concurrency — already neutral

The `ConcurrencyToken` is an application-managed `Guid` marked `IsConcurrencyToken`, so it behaves
the same everywhere. **Do not** switch to SQL Server `rowversion` — it has no SQLite/Postgres
equivalent and would break portability.

### 5. Conflict detection — already neutral

The 409 path catches EF Core's `DbUpdateException` / `DbUpdateConcurrencyException`, never provider
error numbers (SQL Server 2601 / 1785, etc.). **Policy:** enforce every uniqueness rule with a real
DB constraint (so it is race-safe) and translate the *EF* exception, never the raw SQL error code.

### 6. Transient-failure retry — abstract behind the provider branch

`EnableRetryOnFailure` exists on each provider but with different tuning; the SQL Server error-number
list (`-2` for serverless cold starts) is meaningless to Npgsql. Configure the execution strategy
*inside* the same branch that picks the provider, so each provider gets its own correct retry policy.

---

## What's already portable vs what needs a change

| Behavior                    | Status                                             |
| --------------------------- | -------------------------------------------------- |
| Provider selection          | ✅ config switch (`Database:Provider`)             |
| Schema creation             | ✅ `EnsureCreated` — no provider-specific files    |
| Cascade deletes             | ✅ `ClientCascade` (EF-side)                       |
| Timestamps                  | ✅ UTC-ticks converter                             |
| Concurrency token           | ✅ app-managed `Guid`                              |
| Conflict → 409              | ✅ catches EF exception, not error codes           |
| Enum / bool / max-length    | ✅ provider-agnostic conversions                   |
| **Unique-index casing**     | ⚠️ **needs `NameNormalized` column + index**      |
| Retry policy                | ⚠️ tune per provider inside the provider branch    |
| Connection / auth           | ⚠️ deployment concern (e.g. managed identity is Azure-specific) |

---

## Adding a provider (worked example: PostgreSQL)

1. Add the package: `Npgsql.EntityFrameworkCore.PostgreSQL`.
2. Add a branch in `AddInfrastructure`:

   ```csharp
   else if (provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
   {
       var conn = configuration.GetConnectionString("DefaultConnection")
           ?? throw new InvalidOperationException(
               "ConnectionStrings:DefaultConnection is required when Database:Provider is Postgres.");
       options.UseNpgsql(conn, npgsql => npgsql.EnableRetryOnFailure());
   }
   ```

3. Set `Database:Provider=Postgres` and a connection string.
4. Run the integration test suite against it (below). No handler, query, or domain change required.

Estimated effort for a relational provider: **about an hour**, most of it re-verifying the
case-sensitivity and cascade behaviors above.

---

## Proving it: multi-provider CI

Careful reading is a strong hope; a passing test on three engines is proof. Run the **same**
integration tests against every provider claimed as supported:

- **SQLite** — in-memory / file, fast, no container.
- **SQL Server** — via [Testcontainers](https://testcontainers.com/) (`mcr.microsoft.com/mssql/server`).
- **PostgreSQL** — via Testcontainers (`postgres:16`).

Focus the assertions on the behaviors that differ: duplicate-name casing produces a 409 on all
providers, delete-user removes the user's categories on all providers, concurrent update yields a
409 on all providers. This is what catches collation-style surprises before a deploy does.

---

## Non-relational stores (Cosmos DB, MongoDB, DynamoDB, key-value)

"Any relational provider" is achievable with the patterns above. **"Any database" is not** — not
without re-homing the rules that currently live in relational constraints. This is a port, not a
config flip, and it's worth being precise about *why*, because the reasons are the same principle
from the top of this document seen from the other side: on a relational store the database can safely
own storage while your app owns semantics; on a non-relational store there is no constraint engine to
lean on even when you want to, so **every rule must move into the application layer.**

### What the app currently relies on that non-relational stores don't provide

| The app depends on…                     | Relational | Cosmos DB (SQL API) | MongoDB | DynamoDB |
| --------------------------------------- | ---------- | ------------------- | ------- | -------- |
| Multi-column **unique index** (`UserId,Name`) | ✅ | ❌ (only the id/partition key is unique) | ⚠️ unique index, but not partition-scoped the same way | ❌ (only the primary key) |
| **Foreign keys + cascade** (`Category → User`, `TodoItem → Category`) | ✅ | ❌ no cross-document FKs | ❌ | ❌ |
| **Cross-entity transactions** (create category + touch items) | ✅ | ⚠️ only within one partition key | ⚠️ multi-doc txns exist but constrained | ⚠️ `TransactWriteItems`, limited |
| **Optimistic concurrency** | ✅ Guid token | ✅ but via `_etag`, not an arbitrary column | ✅ different mechanism | ✅ via conditional writes |
| Rich **LINQ / joins** across entities | ✅ | ⚠️ partial; no cross-container joins | ❌ (aggregation pipeline instead) | ❌ (query/scan only) |
| Server-enforced **max length / not-null** | ✅ | ❌ (schema-less) | ❌ | ❌ |

### EF Core's Cosmos DB provider specifically

EF Core *does* ship a Cosmos DB provider, so it looks like "just another `UseCosmos(...)` branch" —
but it is the exception that proves the rule. It does **not** support unique indexes (other than the
document id), cross-container foreign keys or cascades, cross-partition transactions, or a large part
of the LINQ surface (many queries that translate to SQL throw at runtime on Cosmos). It also models
data differently — related entities are typically **embedded** as a document tree (owned types under
an aggregate root) rather than referenced across containers. So even with the same ORM, the
`(UserId, Name)` uniqueness rule and the `Category`/`TodoItem` relationships would have to be
redesigned, not reconfigured.

### What a real port would look like

1. **Change the data model from relational to aggregate-oriented.** Decide the partition key
   (`UserId` is the natural choice here) and whether categories/items are embedded in a user
   document or kept as separate documents in the same partition. This is a modeling decision, not a
   setting.
2. **Move every constraint into the application layer.** Uniqueness becomes an explicit
   read-then-write guarded by an ETag/conditional write (or a deterministic document id like
   `user:{id}:cat:{normalizedName}` so a duplicate simply collides on the id). Cascades become code
   in the handler that deletes/updates the related documents. "Not null / max length" become
   validation (which FluentValidation already does — the DB was only a backstop).
3. **Replace concurrency and conflict handling.** Swap the Guid `ConcurrencyToken` for the store's
   native mechanism — Cosmos `_etag`, a Mongo version field, a DynamoDB conditional expression — and
   translate *its* concurrency failure into the same 409 the API already returns.
4. **Introduce a real persistence abstraction.** `IApplicationDbContext` currently exposes
   `DbSet<T>` and `SaveChangesAsync`, which is an EF-shaped (unit-of-work) interface. A clean
   non-relational port would hide the store behind repository interfaces
   (`ICategoryRepository.AddAsync`, etc.) so handlers don't know whether persistence is a `DbSet` or
   a Cosmos container. The Clean Architecture layering already makes this a contained change — only
   Infrastructure and the interface change; domain and handlers keep their shape.
5. **Rewrite the tests against the new store.** The behavior contract (duplicate name → 409, delete
   user removes their data, concurrent update → 409) stays the same; the setup and the store under
   test change.

### The honest bottom line

You can make the code **behave identically across any relational provider** covered by the test
suite — that's the realistic, provable target, and most of it is already done. Supporting a
**non-relational** store (Cosmos, MongoDB, DynamoDB) is possible but is a deliberate re-architecture
of the persistence layer, because the guarantees the app currently delegates to a relational engine
have to be re-implemented in application code. The Clean Architecture boundaries make that port
*contained* (domain and handlers barely move), but it is engineering work, not a configuration flag.

---

> **← Back to the main [README](README.md).**
