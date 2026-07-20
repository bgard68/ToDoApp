# Tech Stack — TaskBoard (ToDoApp)

_[← Back to the main README](../../README.md)_

The full technology stack, grouped by area, with a one-line note on what each piece does and why
it's here. For the at-a-glance summary, see the [README](../../README.md).

---

## Backend — .NET 10 Web API

- **.NET 10 / ASP.NET Core** — the runtime and web framework; the API is built with **minimal APIs**
  (endpoints registered via `MapGroup`/`MapPost`, no MVC controllers).
- **Clean Architecture** (four projects: `Domain`, `Application`, `Infrastructure`, `WebApi`) —
  enforces inward-only dependencies so business rules don't depend on framework or database details.
- **MediatR** — implements **CQRS**: every use case is a Command or Query with its own handler,
  keeping endpoints thin.
- **FluentValidation** — declarative request validation, run automatically before each handler via a
  MediatR pipeline behavior (`ValidationBehavior`).
- **Swagger / OpenAPI (Swashbuckle)** — auto-generated interactive API docs (enabled in Development).
- **RFC 7807 Problem Details** — a single `GlobalExceptionHandler` maps each application exception type
  to a consistent, structured HTTP error response.

## Data

- **Entity Framework Core 10** — the ORM; also serves as the Unit of Work / Repository layer
  (`DbContext` + `DbSet`) behind the `IApplicationDbContext` port.
- **SQLite** (local dev) and **Azure SQL Server** (production) — the same code runs on both via a
  config-driven provider switch (`Database:Provider`). A value converter stores `DateTimeOffset` as UTC
  ticks so SQLite can sort and compare them.

## Auth & security

- **JWT bearer tokens** — short-lived (15-minute) access tokens for API authentication.
- **Refresh-token rotation with reuse detection** — refresh tokens are single-use; replaying an
  already-rotated token revokes every session (theft response). Only SHA-256 hashes are stored.
- **Security-stamp revocation** — a stamp embedded in each token is re-checked on every request, so
  "sign out everywhere" works instantly despite JWTs being stateless.
- **PBKDF2 password hashing** (`Rfc2898DeriveBytes`, 100k iterations, fixed-time comparison) — secure
  local passwords.
- **Google Sign-In** (`Google.Apis.Auth`) — validates Google ID tokens and links/creates a local user.
- **Azure Key Vault** (`Azure.Identity` / `ManagedIdentityCredential`) — holds the JWT signing key in
  production, read via the app's managed identity.

## Frontend — React SPA

- **React 18** — the UI library; state is managed with **custom hooks** (`useTodos`, `useCategories`)
  rather than Redux or heavy Context.
- **Vite 5** — dev server and build tool; **`@vitejs/plugin-react`** (Babel under the hood) transforms
  JSX.
- **`fetch`-based API client** (`lib/apiClient.js`) — plain browser `fetch` with a JWT/session layer and
  a transparent refresh-and-retry on 401; no axios.
- **Google Identity Services** — the Google sign-in button on the client.
- Hand-written **CSS** — the post-it-note board styling.

## Testing

- **Frontend:** **Vitest** (runner) + **React Testing Library** + **jsdom**, with jest-dom and
  user-event — unit, component, and hook tests.
- **Backend:** **xUnit** + **FluentAssertions** over an in-memory **SQLite** context for domain/handler
  tests, and **`WebApplicationFactory`** (Microsoft.AspNetCore.Mvc.Testing) for full in-process HTTP
  integration tests.

See the [testing guide](../development/testing.md) for how the suites are set up and how to add tests.

## Hosting — Azure

- **Azure App Service (Linux)** — hosts the .NET API (`taskboard-06-api`).
- **Azure SQL (serverless)** — the database; **passwordless** access via the app's managed identity. It
  auto-pauses when idle, which is why the app has cold-start retry handling.
- **Azure Static Web Apps** — hosts the built React SPA (`salmon-field`), with PR preview environments.
- **Managed identity** — gives the app passwordless access to both SQL and Key Vault, so no database
  credentials are stored anywhere.

## CI/CD — GitHub Actions

- **Two workflows** — `api-ci-cd.yml` (build → test → publish → deploy the API) and
  `frontend-ci-cd.yml` (test-gate → build → deploy the SPA).
- **OIDC federated identity** (`azure/login`) — deploys to Azure with no long-lived stored credentials.
- **Deploy gating** — deploys only on push to `main`; PRs build and test but never ship; failing tests
  or a hollow publish stop the pipeline. See the [CI/CD pipeline testing guide](../deployment/pipeline.md).

## Also in the repo

- **`deploy/`** — an nginx reverse-proxy config and a systemd service file, for self-hosting the API
  outside Azure (e.g. a Linux VM or Docker) as an alternative to App Service.

---

_See also: [Architecture & practices assessment](assessment.md) · [Database portability](database-portability.md) · [Testing guide](../development/testing.md) · [Deployment guide](../deployment/overview.md) · [Lessons learned](../lessons.md)._

> **← Back to the main [README](../../README.md).**
