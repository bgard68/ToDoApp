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
- [ ] (Optional, purist) Introduce repository interfaces if you want to decouple `Application` from EF Core.

## Related operational follow-ups (not code)

- [ ] Delete the four unreferenced GitHub secrets (old `taskboard-05-api` OIDC trio + `BRAVE_GLACIER` SWA token).
- [ ] Commit & push the doc updates and `.gitattributes` line-ending normalization.

---

_See also: [Lessons Learned](../lessons.md) · [Key Vault deployment troubleshooting](../deployment/keyvault-deployment-troubleshooting.md) · [Database portability](database-portability.md)._

> **← Back to the main [README](../../README.md).**
