# Data access & SQL dialects — EF Core, Dapper, and the SQL they produce

_[← Back to the main README](../../README.md)_

The same application runs on two data-access stacks and three databases. This page shows **one
query expressed four ways** — LINQ/EF Core, Dapper, and the SQL each database actually receives —
and documents the **dialect differences that have bitten this codebase**, with the fix applied in
each case.

It is written from the code in this repository, not from generic advice: every SQL snippet is the
shape these providers really emit, and every "trap" below was a real defect with a real fix.

---

## Contents

- [1. The two stacks and three databases](#1-the-two-stacks-and-three-databases)
- [2. One query, four ways](#2-one-query-four-ways)
- [3. SQLite vs PostgreSQL — the differences that matter](#3-sqlite-vs-postgresql--the-differences-that-matter)
- [4. Traps this codebase actually hit](#4-traps-this-codebase-actually-hit)
- [5. Choosing between EF Core and Dapper](#5-choosing-between-ef-core-and-dapper)

---

## 1. The two stacks and three databases

The branches are maintained in parallel and are **not** merged into each other. They deliberately
support different database sets:

| | `main` | `dapper` |
|---|---|---|
| Data access | **EF Core** (LINQ → SQL) | **Dapper** (hand-written SQL) |
| Local / test | SQLite | SQLite |
| Production | **PostgreSQL** (Neon) | **Azure SQL** (SQL Server) |
| Also supported | SQL Server | — |
| Schema created by | EF Core model + conventions | idempotent per-dialect DDL scripts |

Provider selection is configuration-only — `Database:Provider` plus a connection string — so the
same build runs on any supported backend with no code change.

> **Why this matters for correctness:** local development and the entire test suite run on
> **SQLite**, while production runs on **PostgreSQL** or **Azure SQL**. Any behaviour that differs
> between those engines is, by definition, a bug the tests cannot see. Section 4 is the list of
> times that actually happened.

---

## 2. One query, four ways

The board query: *a user's todos, optionally filtered by status, optionally text-searched, ordered
high-priority first, then soonest due (nulls last), then newest.*

### 2a. LINQ / EF Core (`main`)

```csharp
var query = _context.TodoItems
    .AsNoTracking()                                   // read-only: skip change tracking
    .Where(t => t.UserId == userId);

query = request.Filter switch
{
    TodoFilter.Active    => query.Where(t => t.Status != TodoStatus.Done),
    TodoFilter.Completed => query.Where(t => t.Status == TodoStatus.Done),
    _                    => query
};

if (!string.IsNullOrWhiteSpace(request.Search))
{
    var term = request.Search.Trim().ToLower();
    query = query.Where(t =>
        t.Title.ToLower().Contains(term) ||
        (t.Description != null && t.Description.ToLower().Contains(term)));
}

var entities = await query
    .OrderByDescending(t => t.Priority)
    .ThenBy(t => t.DueDate == null)                   // explicit "nulls last" — see §4.3
    .ThenBy(t => t.DueDate)
    .ThenByDescending(t => t.CreatedAt)
    .ToListAsync(cancellationToken);
```

### 2b. Dapper (`dapper`)

The same logic, composed as SQL text with parameters. Nothing is translated — what you write is
what runs:

```csharp
var sql = new StringBuilder(
    "SELECT * FROM TodoItems WHERE UserId = @UserId");
var parameters = new DynamicParameters();
parameters.Add("UserId", userId);

if (filter != TodoFilter.All)
{
    sql.Append(filter == TodoFilter.Active ? " AND Status <> @Done" : " AND Status = @Done");
    parameters.Add("Done", (int)TodoStatus.Done);
}

if (!string.IsNullOrWhiteSpace(search))
{
    sql.Append(@" AND (LOWER(Title) LIKE @Search ESCAPE '\'
                    OR (Description IS NOT NULL AND LOWER(Description) LIKE @Search ESCAPE '\'))");
    parameters.Add("Search", $"%{EscapeLike(search.Trim().ToLower())}%");
}

sql.Append(" ORDER BY Priority DESC, CASE WHEN DueDate IS NULL THEN 1 ELSE 0 END, DueDate, CreatedAt DESC");
```

Two details worth copying: wildcards live in the **parameter**, not the SQL string (so no
concatenation-operator portability problem), and `ESCAPE '\'` with an escaping helper means a
search for `50%` matches a literal `50%` instead of acting as a wildcard.

### 2c. The SQL — PostgreSQL (production on `main`)

EF Core emits, for *Active + search*:

```sql
SELECT t."Id", t."UserId", t."Title", t."Description", t."Status",
       t."CategoryId", t."Priority", t."DueDate", t."CreatedAt",
       t."UpdatedAt", t."ConcurrencyToken"
FROM "TodoItems" AS t
WHERE t."UserId" = @__userId_0
  AND t."Status" <> 2                                       -- enum stored as int
  AND (strpos(lower(t."Title"), @__term_1) > 0
       OR (t."Description" IS NOT NULL
           AND strpos(lower(t."Description"), @__term_1) > 0))
ORDER BY t."Priority" DESC,
         (t."DueDate" IS NULL),                             -- false(0) before true(1) → nulls last
         t."DueDate",
         t."CreatedAt" DESC;
```

### 2d. The SQL — SQLite (local & tests, both branches)

Same query, different function names and no quoted identifiers:

```sql
SELECT "t"."Id", "t"."UserId", "t"."Title", "t"."Description", "t"."Status",
       "t"."CategoryId", "t"."Priority", "t"."DueDate", "t"."CreatedAt",
       "t"."UpdatedAt", "t"."ConcurrencyToken"
FROM "TodoItems" AS "t"
WHERE "t"."UserId" = @__userId_0
  AND "t"."Status" <> 2
  AND (instr(lower("t"."Title"), @__term_1) > 0
       OR ("t"."Description" IS NOT NULL
           AND instr(lower("t"."Description"), @__term_1) > 0))
ORDER BY "t"."Priority" DESC, ("t"."DueDate" IS NULL), "t"."DueDate", "t"."CreatedAt" DESC;
```

The only difference in the generated SQL is `strpos` vs `instr` — but see §4.1 for the behavioural
difference that *isn't* visible here.

### 2e. Aggregates — done in the database, not in memory

Existence checks compile to `EXISTS`, fetching no rows:

```csharp
await _context.Categories.AnyAsync(c => c.Id == categoryId && c.UserId == userId, ct);
```

```sql
-- PostgreSQL / SQLite
SELECT EXISTS (SELECT 1 FROM "Categories" AS c
               WHERE c."Id" = @__categoryId_0 AND c."UserId" = @__userId_1);
```

The anti-pattern to avoid is `.ToListAsync()` followed by `.Any()` / `.Count()` in C#, which pulls
every row across the wire to compute a boolean.

---

## 3. SQLite vs PostgreSQL — the differences that matter

| Area | SQLite | PostgreSQL | Consequence here |
|---|---|---|---|
| **Type system** | Dynamic ("type affinity") — any value in any column | Strict, static types | SQLite tolerates shape mistakes that Postgres rejects |
| **Booleans** | No boolean type — `INTEGER` 0/1 | Native `BOOLEAN` | Stored as int either way by the model |
| **Auto-increment** | `INTEGER PRIMARY KEY AUTOINCREMENT` | `IDENTITY` / `serial` | Handled by the provider |
| **Strings** | `TEXT` (no length limit) | `text` / `varchar(n)` | `HasMaxLength` is enforced by Postgres, effectively advisory on SQLite |
| **Dates** | No date type at all | Rich `timestamptz` | This repo stores **UTC ticks as an integer on every provider** (§4.2) |
| **`LIKE` case** | **Case-insensitive** for ASCII | **Case-sensitive** (`ILIKE` for insensitive) | The §4.1 bug — invisible locally, real in production |
| **`instr`/`strpos`** | `instr()` — case-**sensitive** | `strpos()` — case-**sensitive** | `.Contains()` is case-sensitive on *both*, unlike `LIKE` on SQLite |
| **NULL ordering** | `ASC` → NULLs **first** | `ASC` → NULLs **last** | The §4.3 trap; `NULLS LAST` exists in both but not in SQL Server |
| **Identifier case** | Case-insensitive, preserved | Unquoted folds to **lowercase**; quoted is exact | EF quotes everything, so `"TodoItems"` stays PascalCase |
| **Pagination** | `LIMIT n OFFSET m` | `LIMIT n OFFSET m` | Same (SQL Server needs `OFFSET…FETCH`) |
| **Concurrency** | Single-writer, file-locked | True MVCC, high concurrency | Fine for tests; only Postgres/Azure SQL serve real traffic |
| **DDL idempotency** | `CREATE TABLE IF NOT EXISTS` | `IF NOT EXISTS` supported | SQL Server needs `IF OBJECT_ID(…) IS NULL` guards instead |

### Schema DDL, side by side

The `dapper` branch keeps hand-written per-dialect scripts, which makes the contrast concrete:

```sql
-- SQLite
CREATE TABLE IF NOT EXISTS Users (
    Id             INTEGER NOT NULL CONSTRAINT PK_Users PRIMARY KEY AUTOINCREMENT,
    Email          TEXT    NOT NULL,
    Role           INTEGER NOT NULL,
    IsActive       INTEGER NOT NULL,          -- no BOOLEAN type
    CreatedAt      INTEGER NOT NULL           -- UTC ticks, no date type
);
```

```sql
-- SQL Server / Azure SQL
IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Users (
        Id             INT            IDENTITY(1,1) NOT NULL CONSTRAINT PK_Users PRIMARY KEY,
        Email          NVARCHAR(256)  NOT NULL,
        Role           INT            NOT NULL,
        IsActive       BIT            NOT NULL,
        CreatedAt      BIGINT         NOT NULL
    );
END
```

The PostgreSQL equivalent (generated by EF Core on `main`) uses `bigint` identity columns,
`boolean`, `text`, and quoted PascalCase identifiers.

---

## 4. Traps this codebase actually hit

### 4.1 Case-sensitive search that only failed in production

**Symptom.** Searching `sarah` did not find *"Email Sarah about the proposal"* — but only on the
deployed app. Local development and every test passed.

**Cause.** Two different mechanisms, same outcome:

- **EF Core branch:** `.Contains(term)` compiles to `instr()` / `strpos()`, which are
  case-**sensitive on both** SQLite and PostgreSQL.
- **Dapper branch:** raw `Title LIKE @Search` — and SQLite's `LIKE` is case-**insensitive** for
  ASCII while PostgreSQL's is case-**sensitive**. The local behaviour was the *wrong* one to trust.

**Why it matters beyond spelling:** mobile keyboards auto-capitalise the first character of an
input, so a user typing `dentist` sends `Dentist`.

**Fix — lower both sides, on both stacks:**

```csharp
// EF Core
var term = request.Search.Trim().ToLower();
query = query.Where(t => t.Title.ToLower().Contains(term) || …);
```

```sql
-- Dapper
AND (LOWER(Title) LIKE @Search ESCAPE '\' OR …)      -- term lowercased in C#
```

**Rejected alternative:** `EF.Functions.ILike` is Npgsql-only. It reads better and is faster
(it can use a trigram index), but it **throws at query time on SQLite** — which would break local
development and the entire test suite. `LOWER()` is standard SQL and works on all three providers.

> **Scale note.** `LOWER(col)` prevents a plain B-tree index from being used. At a personal todo
> app's row counts that is irrelevant. On a large table the answer is an **expression index**
> (`CREATE INDEX … ON "TodoItems" (lower("Title"))`) or Postgres trigram/full-text search.

### 4.2 `DateTimeOffset` has no SQLite representation

**Cause.** SQLite has no date/time type. Storing a `DateTimeOffset` naively makes ordering and
comparison either fail at translation or silently compare strings.

**Fix.** Every `DateTimeOffset` is converted to a **UTC-tick `long`** on *every* provider — via an
EF `ConfigureConventions` value converter on `main`, and a Dapper `TypeHandler` on `dapper`. Applied
uniformly (not just for SQLite) so the on-disk shape and ordering semantics are identical
everywhere, and a round-trip test guards that the conversion is lossless.

The trade-off is explicit: you lose native date functions and human-readable timestamps in the
database, and gain identical behaviour across three engines.

### 4.3 NULL ordering is not portable

`ORDER BY DueDate ASC` does **not** mean the same thing everywhere:

| Engine | NULLs on `ASC` |
|---|---|
| PostgreSQL | last |
| SQLite | first |
| SQL Server | first |

"Todos with no due date go at the bottom" therefore cannot rely on the default. Both branches make
it explicit instead:

```csharp
.ThenBy(t => t.DueDate == null)     // EF → ORDER BY (DueDate IS NULL)
```
```sql
ORDER BY CASE WHEN DueDate IS NULL THEN 1 ELSE 0 END, DueDate   -- Dapper
```

A sort key of `false(0)` before `true(1)` puts non-null rows first on every engine. `NULLS LAST`
would also work on Postgres and modern SQLite, but **not** on SQL Server — so the portable form
wins.

### 4.4 Enum storage is a contract, not a detail

`Status` and `Priority` are persisted as **`int`** (`HasConversion<int>()` / an explicit cast in
Dapper). That is why the generated SQL reads `WHERE "Status" <> 2` rather than comparing a string.

The consequence: **reordering an enum member silently reinterprets existing rows.** New members
must be appended, never inserted.

---

## 5. Choosing between EF Core and Dapper

Maintaining both branches makes the trade-off concrete rather than theoretical:

| | EF Core | Dapper |
|---|---|---|
| Query authoring | LINQ, compiler-checked, refactor-safe | SQL strings, checked only at run time |
| Dialect portability | The provider writes the dialect for you | You write per-dialect SQL yourself |
| Schema | Model + migrations | Hand-written idempotent DDL per dialect |
| Change tracking | Built in (opt out with `AsNoTracking`) | None — you write the `UPDATE` |
| Control over SQL | Indirect; inspect the generated SQL | Total |
| Surprise surface | Translation rules, tracking, lazy loading | None from the ORM; all yours |

**The honest summary:** EF Core removes dialect differences *until it doesn't* — §4.1 and §4.3 are
exactly the cases where the abstraction is thin and you must know the underlying engine. Dapper
never pretends to hide them, at the cost of writing each dialect yourself.

Neither choice removes the core obligation: **know which engine runs in production, and test
against behaviour it shares with your local database** — or verify the difference explicitly.

### Seeing the SQL for yourself

EF Core logs every statement under `Microsoft.EntityFrameworkCore.Database.Command` at
`Information`. The development config already sets `Default: Information`, so generated SQL appears
in the console on `dotnet run`. Parameter *values* are redacted by default; that is deliberate, and
`EnableSensitiveDataLogging()` should stay off anywhere real data flows.

---

_[← Back to the main README](../../README.md)_
