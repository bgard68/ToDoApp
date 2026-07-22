# Testing (Dapper edition)

Three layers of verification, all green on the Dapper build.

| Layer | What | Count |
|-------|------|-------|
| **Unit** | Domain rules + CQRS handlers over a **real in-memory SQLite** database (through the repositories) | 36 |
| **Integration** | The full HTTP pipeline via `WebApplicationFactory` over a file-backed SQLite database | 17 |
| **Smoke** | Every endpoint over HTTP against a running instance (`scripts/todoapp-smoketest.ps1`) | 37 checks |

## Run the automated tests

```bash
dotnet test                 # both unit + integration suites, no setup
```

CI (`api-ci-cd.yml`) runs the **unit** project on every push/PR to `main` and gates deploys on it.

### How the harness works now

Because there's no `DbContext` anymore, the test harness was rebuilt on the Dapper primitives:

- **`tests/…UnitTests/TestSupport/TestDatabase.cs`** — opens one shared in-memory `SQLite` connection,
  runs the real `SchemaInitializer` to build the schema, and exposes the actual repositories + a
  `UnitOfWork` bound to that connection. Handler unit tests construct handlers with these repositories,
  so they exercise the same SQL that runs in production.
- **`tests/…IntegrationTests/CustomWebApplicationFactory.cs`** — points the real app at a **private,
  file-backed SQLite database** (unique temp file per factory) via config override, letting the app's
  own startup path build the schema and seed demo data. (A file DB rather than in-memory sidesteps the
  shared-connection lifetime issue — see [refactor-bugs.md](refactor-bugs.md#3-in-memory-sqlite-died-under-per-request-connections).)

## Smoke test (every endpoint, incl. Google)

`scripts/todoapp-smoketest.ps1` hits **every** Swagger endpoint against a running instance and prints a
pass/fail report — including Google sign-in via the **Development-only fake validator** (no real Google
token needed). See [scripts/README.md](../scripts/README.md) for full details.

```powershell
# terminal 1 — API with the fake Google validator on
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:Authentication__Google__UseFake = "true"
dotnet run --project src\TodoApp.WebApi

# terminal 2
powershell -ExecutionPolicy Bypass -File .\scripts\todoapp-smoketest.ps1
```

A green run is a **mix** of expected statuses (`200/201/204` plus deliberate `400/401/409`), not all
`200` — the error cases are asserting that validation, authorization, and concurrency protection all
fire. `Result: N passed, 0 failed` is a clean run.

### The fake Google identity

`FakeGoogleTokenValidator` (Infrastructure) replaces the real validator **only** when
`ASPNETCORE_ENVIRONMENT=Development` **and** `Authentication:Google:UseFake=true` — enforced in the
composition root, so it can never activate in production. It accepts a token of the form `fake:{email}`
as a verified identity (drives the create-user success path) and rejects anything else with `401`. The
smoke test uses both to cover the success and reject paths of `/api/auth/google`.

## SQL Server verification

The automated suites run on SQLite. The SQL Server dialect path (identity via `SCOPE_IDENTITY`, the DDL,
`uniqueidentifier` concurrency token, unique-violation codes) was verified end-to-end against **SQL
Server LocalDB**: schema init, demo seed, CRUD, `409` concurrency, and `409` duplicate all behaved
correctly. To reproduce, run the app with `Database:Provider=SqlServer` and a LocalDB/SQL connection
string, then run the smoke test against it.
