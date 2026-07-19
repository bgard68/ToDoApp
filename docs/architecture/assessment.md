# Architecture & Practices Assessment — TaskBoard (ToDoApp)

_[← Back to the main README](../../README.md)_

An honest, evidence-based review of how well this project adheres to **Clean Architecture**,
**SOLID**, and **CI/CD pipeline** best practices, based on a read of the actual source and workflows.
Grades are relative to what a strict senior reviewer would expect, not a beginner tutorial.

**Summary grades**

| Area | Grade | One-line verdict |
| ---- | ----- | ---------------- |
| Clean Architecture | **A−** | Textbook layering and a genuinely rich domain; loses purity points only for the pragmatic EF-in-Application trade. |
| SOLID | **A−** | Strong across all five; DIP is the standout. |
| Design patterns | **A** | Mediator/CQRS, pipeline behaviors, ports & adapters, options, strategy — used idiomatically, not decoratively. |
| CI/CD pipeline | **B+** | Modern and secure (OIDC, least-privilege); real gains left in testing and post-deploy verification. |

---

## Clean Architecture — A−

**What's right**

- **Dependency direction is textbook-correct.** `Domain` references nothing; `Application → Domain`;
  `Infrastructure → Application + Domain`; `WebApi` is the composition root on top. Dependencies point
  inward; nothing in the core reaches outward.
- **The domain is rich, not anemic.** `TodoItem` has private setters, a validating constructor, and
  behavior methods (`MoveTo`, `Update`, `SetTitle`) that protect invariants. It never reads the clock —
  timestamps are passed in via `IDateTimeProvider`, keeping the domain deterministic and testable.
- **Ports and adapters are separated.** Interfaces live in `Application/Common/Interfaces`
  (`ICurrentUserService`, `IPasswordHasher`, `IJwtTokenService`, `IDateTimeProvider`,
  `IGoogleTokenValidator`, `IApplicationDbContext`); implementations live in `Infrastructure`.
- **CQRS with MediatR**, organized as vertical feature slices (`Auth`, `Categories`, `Todos`), each with
  Commands/Queries/Validators/DTOs. Explicit DTO mapping (`FromEntity`) rather than reflection-based
  mapping.

**The honest caveat**

- This is the **pragmatic (Jason Taylor–style)** flavor, not the purist one. `Application` references
  EF Core, `IApplicationDbContext` exposes `DbSet<T>`, and handlers query with EF LINQ
  (`FirstOrDefaultAsync`) directly. That couples the application layer to EF Core's abstractions — a
  deliberate, widely-accepted trade (far less boilerplate) that a strict reviewer would still dock.
  A purist alternative hides persistence behind repository interfaces returning domain types.
  This trade-off — what exactly leaks, whether it can be abstracted, and when it starts to hurt as
  the project grows — is examined in depth in [its own section below](#the-ef-core--application-layer-coupling--trade-off--future-risk).

---

## SOLID — A−

- **SRP** — one handler per use case, each small and focused.
- **OCP** — new validators and MediatR pipeline behaviors are added without modifying existing code;
  `ValidationBehavior` auto-discovers all registered validators.
- **LSP** — implementations substitute cleanly; nothing problematic.
- **ISP** — interfaces are mostly small and role-specific. Mild exception: `IApplicationDbContext`
  (five `DbSet`s + two methods) is a bit broad, but reasonable for the pattern.
- **DIP** — the standout. Everything depends on abstractions, injected via constructors, wired at the
  composition root. Options pattern for settings (`JwtSettings`, `GoogleAuthSettings`).

---

## Design patterns — rich and idiomatic

The codebase uses a lot of patterns, and — the part that matters — uses them *correctly*, to solve a
real problem, rather than sprinkling them on for show:

- **Mediator + CQRS** (MediatR). Every use case is a `Command` or `Query` with a dedicated handler,
  organized as vertical feature slices (`Auth`, `Categories`, `Todos`). Endpoints do nothing but
  `sender.Send(...)` — no business logic in the transport layer.
- **Chain of Responsibility / pipeline** — `ValidationBehavior<TRequest,TResponse>` is a MediatR
  pipeline behavior that runs every registered FluentValidation validator before the handler. New
  cross-cutting concerns (logging, timing) slot in the same way without touching handlers.
- **Ports & Adapters (Hexagonal)** — interfaces in `Application/Common/Interfaces` are the ports;
  their implementations in `Infrastructure` are the adapters (`GoogleTokenValidator`,
  `CurrentUserService` over `HttpContext`, `JwtTokenService`, `PasswordHasher`).
- **Repository + Unit of Work** — provided by EF Core itself: each `DbSet<T>` is a repository and the
  `DbContext` is the unit of work (`SaveChangesAsync` commits the tracked graph atomically).
- **Options pattern** — `JwtSettings`, `GoogleAuthSettings` bound from configuration and injected as
  `IOptions<T>`.
- **Static factory methods** — `User.CreateExternal(...)` for password-less accounts and
  `Category.DefaultsFor(...)` for the starter set, keeping construction rules inside the domain.
- **Strategy** — the persistence provider is chosen at composition time (`Database:Provider` →
  SQLite locally, SQL Server in Azure) behind one registration.
- **RFC 7807 Problem Details** — a single `GlobalExceptionHandler` maps each application exception
  type to the correct status code and a structured body, so the API's error contract is uniform.

Beyond the textbook catalogue, the **security design** is a cut above what an app this size usually
ships, and it's where the patterns earn their keep: **stateless-JWT revocation via a security stamp**
re-checked on every request (`OnTokenValidated`), **refresh-token rotation with reuse detection** (a
replayed, already-rotated token rotates the stamp and revokes *every* session), **PBKDF2 with a
fixed-time comparison**, and storing only the **SHA-256 hash** of refresh tokens. These are the kinds
of decisions that are easy to get wrong and are gotten right here.

The one notable *absence* is **domain events** — MediatR is used for requests but nothing raises or
handles domain events, so side effects (e.g. "email on registration") would today live inside a
handler rather than being decoupled. That's fine at this size; it's the natural next pattern if the
app grows.

---

## The EF Core / Application-layer coupling — trade-off & future risk

This is the single deliberate compromise in the architecture, so it's worth spelling out precisely.

**What actually leaks.** Three concrete spots where `Application` touches EF Core directly:

1. **The port exposes an EF type.** `IApplicationDbContext` has `using Microsoft.EntityFrameworkCore;`
   and its members are `DbSet<TodoItem>`, `DbSet<Category>`, etc. `DbSet<T>` is an EF class, so the
   interface meant to *hide* persistence is written in one ORM's vocabulary.
2. **Handlers call EF query operators.** `FirstOrDefaultAsync`, `AnyAsync`, `ToListAsync`,
   `AsNoTracking` are `Microsoft.EntityFrameworkCore` extension methods — so the `Application` project
   references the EF Core package to compile.
3. **A handler catches an EF exception.** `ChangeTodoStatusCommandHandler` catches
   `DbUpdateConcurrencyException` — an EF type — inside application logic. This is the least tidy of
   the three.

**Why it's a caveat.** Clean Architecture says inner layers depend only on abstractions they own,
expressed in domain terms; the ORM is an infrastructure detail that should be swappable without
touching business logic. Here the Application layer is nailed to EF Core: moving a query to Dapper or
a document store would mean rewriting handler code, not just an Infrastructure implementation. EF's
`IQueryable` semantics also leak — a LINQ expression that EF translates to SQL may not translate on
another provider, so query *behavior* is coupled, not just query *code*. And testing a handler needs
a real EF context (the suite uses in-memory SQLite) rather than a trivial fake.

**Can it be abstracted? Yes.** The purist alternative is repository interfaces returning domain types,
defined in `Application`, implemented in `Infrastructure`:

```csharp
public interface ITodoRepository
{
    Task<TodoItem?> GetForUserAsync(int id, int userId, CancellationToken ct);
    Task<IReadOnlyList<TodoItem>> ListForUserAsync(int userId, TodoFilter filter, string? search, CancellationToken ct);
    void Add(TodoItem item);
}
public interface IUnitOfWork { Task SaveChangesAsync(CancellationToken ct); } // throws a *domain* ConcurrencyConflictException
```

Handlers then depend on `ITodoRepository` with **zero** EF imports; the concurrency conflict is caught
inside Infrastructure's `SaveChangesAsync` and rethrown as the existing domain
`ConcurrencyConflictException`, so the handler never sees an EF type.

**Why the project (reasonably) didn't** — it's the same call the canonical Jason Taylor template
makes: `DbSet<T>` *already is* a repository and `DbContext` *already is* a unit of work, so wrapping
them re-abstracts an existing abstraction; you lose EF's composable `IQueryable` querying (filtering,
search, paging run in the DB), forcing either a method-per-query explosion or the extra machinery of
the **Specification** pattern; and the payoff — swapping ORMs — is largely theoretical and rarely
survives the swap intact anyway.

**Future risk as the project grows** — the coupling is cheap *now* and gets marginally more expensive
with scale, in roughly this order:

- **More query shapes → the leak spreads.** Every new handler adds more `FirstOrDefaultAsync`/`Where`
  calls in `Application`. It stays readable, but the surface area coupled to EF only grows.
- **A second read model / reporting need** (dashboards, analytics) is where `IQueryable`-in-handlers
  starts to bite: complex projections are hard to unit-test and easy to write in a way that silently
  falls back to client-side evaluation. This is the point where a Specification pattern (or dedicated
  read-side queries via Dapper) pays off.
- **Team size.** With more contributors, "the handler can run any query" is less of a guardrail than a
  repository's explicit, named methods — the interface stops documenting what data access is allowed.
- **Provider divergence.** The app already straddles SQLite and SQL Server; a third provider, or heavy
  provider-specific SQL, magnifies the "query behavior leaks" problem.

**Recommendation** — *keep the current design.* For an app this size the pragmatic trade is the
correct engineering call, and chasing the abstraction would add indirection users never feel. The one
cheap improvement worth making is closing leak #3: move the `DbUpdateConcurrencyException` handling
into an Infrastructure `SaveChangesAsync` that rethrows the domain `ConcurrencyConflictException` (~15
lines, removes an EF type from business logic, no downside). Revisit full repositories +
specifications only if/when a real second read model or reporting workload appears.

---

## Frontend refactoring — SOLID & React/Vite best practices

The frontend was refactored in the same spirit as the backend; documenting what changed since it's
part of this assessment.

**What was found.** Two SRP violations. `api.js` was a single module doing HTTP transport, token/auth
state, *and* domain constants/colors/category helpers. `KanbanBoard.jsx` mixed data-fetching and
server mutations with rendering, so the view owned side effects and was hard to test.

**What changed.**

- **`api.js` split by responsibility** into `lib/apiClient.js` (HTTP client + JWT session +
  `AuthApi`/`CategoryApi`/`TodoApi`), `lib/constants.js` (`PRIORITIES`, `STATUSES`, `STATUS`),
  `lib/colors.js` (`tint`), and `lib/categories.js` (`findCategory`). `api.js` became a **barrel**
  (`export * from './lib/apiClient.js'` + the others) so existing imports keep working — an
  **Open/Closed**-friendly move that reorganizes without breaking callers.
- **Data logic extracted into hooks.** `useTodos()` now owns the todo collection and every server
  operation (optimistic move + reconcile, create/update/delete, concurrency handling); `useCategories()`
  owns categories. `KanbanBoard.jsx` is now a thin view that composes the two hooks — **SRP** restored
  and the logic is independently unit-testable (see the `useTodos` hook tests).
- **A real bug fixed along the way** — concurrent 401s each refreshing the same rotated token and
  tripping the backend's reuse-detection (see [Lessons — "the real find"](../lessons.md#the-real-find--concurrent-refresh-signed-users-out-everywhere)),
  plus the optimistic-UI change that removed the post-move "post back"
  ([Lessons — Frontend](../lessons.md#frontend-react--vite)).

**Verification note.** The sandbox couldn't run `vite build` (native rollup binary mismatch under the
device VM), so the refactor was validated by parsing every changed module with `@babel/parser` and a
custom import/export resolver to prove nothing was structurally broken, then by the new Vitest suite
once dependencies were installed locally.

---

## CI/CD pipeline — B+

**What's genuinely best-practice**

- **Two separate workflows** — `api-ci-cd.yml` (API) and `frontend-ci-cd.yml` (SPA) — the right
  separation of concerns.
- **OIDC federated identity** (`azure/login@v2`) instead of stored publish profiles — the modern gold
  standard; no long-lived deploy credentials.
- **Least-privilege permissions** (`id-token: write`, `contents: read`).
- **Deploy gated to `main`**; PRs build and test but never deploy.
- **Reproducible builds** — lock-file restore (`--use-lock-file`) plus NuGet caching.
- **Project-scoped publish** (`TodoApp.WebApi.csproj`), not the whole solution.
- **`workflow_dispatch`** for manual runs; **PR preview environments** on the Static Web App.

**Gaps worth closing (roughly priority order)**

1. **Integration tests don't run in CI.** The API workflow runs only `TodoApp.UnitTests`; the
   WebApplicationFactory-based `TodoApp.IntegrationTests` project exists but never executes — so the
   highest-value tests don't gate deploys.
2. **No post-deploy smoke test.** The pipeline deploys and stops; it doesn't hit `/api/auth/login`
   afterward to confirm the deploy is actually healthy — exactly the check that catches
   "green build but the app isn't really serving."
3. **No environment protection / approval gate** on the production deploy (a GitHub Environment with a
   required reviewer).
4. **No dependency/security scanning** (Dependabot, CodeQL).
5. **No `concurrency:` group** to cancel superseded runs.
6. **Leftover diagnostic step** — the `Debug: repo files & lockfiles` step can be dropped now.

---

## Improvement backlog (future reference)

- [ ] Run integration tests in `api-ci-cd.yml` (add a `dotnet test tests/TodoApp.IntegrationTests/...` step).
- [ ] Add a post-deploy smoke test step that calls `/api/auth/login` and fails the run on non-200.
- [ ] Add a GitHub Environment (e.g. `production`) with a required reviewer for the API deploy.
- [ ] Enable Dependabot and/or CodeQL scanning.
- [ ] Add `concurrency: { group: ..., cancel-in-progress: true }` to both workflows.
- [ ] Remove the `Debug: repo files & lockfiles` step.
- [ ] (Cheap, recommended) Move the `DbUpdateConcurrencyException` handling out of `ChangeTodoStatusCommandHandler` into an Infrastructure `SaveChangesAsync` that rethrows the domain `ConcurrencyConflictException` — removes the one EF type that leaks into application logic (~15 lines).
- [ ] (Optional, purist) Introduce repository interfaces + the Specification pattern if you want to fully decouple `Application` from EF Core — see the EF coupling trade-off section for when this pays off.

## Related operational follow-ups (not code)

- [ ] Delete the four unreferenced GitHub secrets (old `taskboard-05-api` OIDC trio + `BRAVE_GLACIER` SWA token).
- [ ] Commit & push the doc updates and `.gitattributes` line-ending normalization.

---

_See also: [Lessons Learned](../lessons.md) · [Key Vault deployment troubleshooting](../deployment/keyvault-deployment-troubleshooting.md) · [Database portability](database-portability.md)._

> **← Back to the main [README](../../README.md).**
