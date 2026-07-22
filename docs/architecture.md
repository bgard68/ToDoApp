# Architecture (Dapper edition)

> **← Back to the main [README](../README.md).**

> This is the **`refactor/dapper`** branch: the same Clean-Architecture Todo app as `main`, with the
> persistence layer rebuilt on **Dapper** instead of EF Core. Everything outside the data layer —
> the domain model, the API contract, auth, and the React frontend — is unchanged. For *why* and
> *how* the swap was done, see the [infrastructure guide](infrastructure.md); for the issues hit
> along the way, the [bugs-encountered log](refactor-bugs.md).

## Onion / Clean Architecture

Dependencies point **inward**. Each layer only knows about the layers inside it; the outermost layer
(Web API + Infrastructure) depends on everything, and the Domain at the center depends on nothing.

![Onion architecture](onion-architecture.svg)

| Layer | Project | Responsibility | Depends on |
|-------|---------|----------------|-----------|
| **Domain** | `TodoApp.Domain` | Entities (`User`, `TodoItem`, `Category`, `RefreshToken`, `ExternalLogin`), enums, and business invariants. No framework references. | — (nothing) |
| **Application** | `TodoApp.Application` | CQRS commands/queries + MediatR handlers, DTOs, FluentValidation, and the **interfaces** the handlers depend on. | Domain |
| **Infrastructure** | `TodoApp.Infrastructure` | Concrete implementations: **Dapper repositories**, connection factory, unit of work, schema initializer, JWT/password/Google services. | Application, Domain |
| **Web API** | `TodoApp.WebApi` | Minimal-API endpoints, JWT wiring, CORS, error handling, composition root. | Application, Infrastructure |

## Dependency inversion — the key seam

The Application layer never references Dapper or ADO.NET. Instead it declares **interfaces**, and
Infrastructure implements them:

```
Application (defines)                     Infrastructure (implements, with Dapper)
────────────────────────                  ─────────────────────────────────────────
ITodoRepository                    ◀────── TodoRepository
ICategoryRepository                ◀────── CategoryRepository
IUserRepository                    ◀────── UserRepository
IRefreshTokenRepository            ◀────── RefreshTokenRepository
IExternalLoginRepository           ◀────── ExternalLoginRepository
IUnitOfWork                        ◀────── UnitOfWork
IJwtTokenService / IPasswordHasher ◀────── JwtTokenService / PasswordHasher
IGoogleTokenValidator              ◀────── GoogleTokenValidator (or FakeGoogleTokenValidator in dev)
IDateTimeProvider                  ◀────── DateTimeProvider
```

A handler like `UpdateTodoCommandHandler` takes an `ITodoRepository` in its constructor and has no
idea whether the rows come from SQLite or SQL Server, or that Dapper is involved at all. That's what
keeps the core testable (handler unit tests run against real in-memory SQLite through the same
interfaces) and lets the **same handlers run unchanged on SQLite locally and Azure SQL in production**.

### What changed from the EF Core version (`main`)

| Concern | EF Core (`main`) | Dapper (`refactor/dapper`) |
|---------|------------------|-----------------------------|
| Query surface | `IApplicationDbContext` exposing `DbSet<T>` + LINQ | Five focused repository interfaces |
| Unit of work | `SaveChanges()` change tracking | Explicit `IUnitOfWork` transaction around multi-write flows |
| Schema | `EnsureCreated()` from the model | `SchemaInitializer` runs idempotent, per-dialect DDL |
| Concurrency | `[ConcurrencyToken]` + `DbUpdateConcurrencyException` | `UPDATE … WHERE ConcurrencyToken = @expected` + rows-affected |
| Type mapping | value converter (`DateTimeOffset` → ticks) | Dapper `TypeHandler`s (`DateTimeOffset`/`Guid`) |

See the [infrastructure guide](infrastructure.md) for the full details of each.

## Related docs

- [Infrastructure & the Dapper data layer](infrastructure.md)
- [Testing](testing.md) · [Workflows & deploys](workflows.md) · [Bugs encountered](refactor-bugs.md)
- [Azure deployment](deployment/azure.md)
