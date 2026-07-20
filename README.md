# Todo App — .NET 10 Clean Architecture + React (with JWT Auth)

A full-stack, multi-user **Kanban board** — tasks flow across To Do / In Progress / Done
lanes as draggable, category-colored post-it notes. The backend is an ASP.NET Core Web API
organized with Clean Architecture (Domain / Application / Infrastructure / WebApi) using CQRS
(MediatR), FluentValidation, and EF Core (SQLite). Authentication is JWT-based with
refresh-token rotation and **revocable tokens** for compromised accounts. The frontend
is a React (Vite) single-page app.

## Highlights

- **Kanban board** — three lanes (To Do / In Progress / Done) with native HTML5
  drag-and-drop to change a task's status, tasks shown as post-it notes colored by category,
  a check mark on Done cards, and a category filter.
- **User-managed categories** — each user creates, renames, recolors, and deletes their own
  categories (name + hex color), with a starter set seeded on sign-up. Deleting a category
  leaves its tasks uncategorized rather than removing them.
- **Clean Architecture + CQRS** — dependencies point inward (WebApi → Infrastructure →
  Application → Domain); handlers depend on interfaces, not EF Core or ASP.NET.
- **SOLID, no repository over-abstraction** — EF Core's `DbContext`/`DbSet` used directly
  behind `IApplicationDbContext`.
- **JWT auth with real revocation** — short-lived access tokens carry a per-user security
  stamp validated on every request; refresh tokens are hashed, single-use, and rotated with
  reuse detection. "Sign out everywhere" instantly invalidates all sessions.
- **Google sign-in** — verifies a Google ID token and links/creates a local user, issuing
  the app's own tokens.
- **Per-user authorization** — every todo is scoped to its owner; cross-user access returns 404.
- **Optimistic concurrency** — a concurrency token on todos surfaces conflicting edits as 409
  with the current server state; unique-constraint races become 409 instead of 500.
- **Testable time** — the system clock is abstracted behind `IDateTimeProvider`; tests use a
  fake clock for deterministic, timezone-independent results.
- **Tested** — xUnit unit tests (domain + handlers over real in-memory SQLite) and
  `WebApplicationFactory` integration tests over the full HTTP pipeline.
- **Secrets done right** — nothing sensitive in source; the signing key comes from
  user-secrets (dev) or environment/Key Vault (prod), and the app fails fast without it.
- **Deployable** — Docker Compose, Linux + nginx, and Azure (App Service + Static Web Apps)
  guides, with runnable Dockerfiles and compose files. See the
  [deployment guide](docs/deployment/deployment.md) and [Azure guide](docs/deployment/azure.md).

**Tech stack (at a glance):**

- **Backend:** .NET 10 · ASP.NET Core Minimal APIs · Clean Architecture + CQRS (MediatR) · FluentValidation · EF Core 10 · Swagger
- **Frontend:** React 18 · Vite 5 · custom hooks · `fetch`-based API client · Google Identity Services
- **Data:** SQLite (dev) / Azure SQL (prod) via a config-driven provider switch
- **Auth:** JWT · refresh-token rotation + reuse detection · security-stamp revocation · PBKDF2 · Google sign-in · Key Vault
- **Testing:** Vitest + React Testing Library (frontend) · xUnit + FluentAssertions + `WebApplicationFactory` (backend)
- **Hosting & CI/CD:** Azure App Service · Azure SQL · Static Web Apps · GitHub Actions (OIDC)

→ See the **[full tech-stack reference](docs/architecture/tech-stack.md)** for a one-line explanation of what each piece does and why it's there.

## Project layout

```
TodoApp.sln
src/
  TodoApp.Domain/          # Entities (User, RefreshToken, TodoItem, Category), enums, business rules
  TodoApp.Application/     # CQRS commands/queries, DTOs, validation, interfaces
  TodoApp.Infrastructure/  # EF Core, password hashing, JWT + current-user services
  TodoApp.WebApi/          # Minimal API endpoints, JWT wiring, error handling
frontend/                  # React + Vite client (login, tokens, todos)
```

Dependencies point inward: WebApi → Infrastructure → Application → Domain. The
Application layer defines interfaces (`IApplicationDbContext`, `IJwtTokenService`,
`IPasswordHasher`, `ICurrentUserService`, `IDateTimeProvider`) that Infrastructure
implements, so the core has no direct dependency on EF Core or ASP.NET.

The layering as an onion — the Domain sits at the core, each layer wraps the one inside it,
and every dependency points inward:

![TaskBoard onion architecture](docs/architecture/onion-architecture.svg)

Solid arrows are the request/data flow; the dashed arrow is **dependency inversion** — the
Application defines `IApplicationDbContext`, and Infrastructure implements it, which is why
the same handlers run unchanged on SQLite locally and Azure SQL in production.

The system clock is abstracted behind **`IDateTimeProvider`** (production
implementation `DateTimeProvider` returns `DateTimeOffset.UtcNow`). Handlers inject it
and pass the timestamp into domain operations, so entities never read the ambient clock
— which keeps time-dependent behavior (token expiry, audit stamps) deterministic and
testable via a `FakeDateTimeProvider`.

> **Deploying?** See the **[deployment guide](docs/deployment/deployment.md)** for full setup, build/compile,
> and deployment instructions (Docker Compose, Linux + nginx, and Azure), plus the
> included `Dockerfile.api`, `frontend/Dockerfile`, `docker-compose.yml`, and `deploy/`
> samples. For a step-by-step **Azure** deploy (App Service + Static Web Apps), **Google
> sign-in** setup, and **secrets/user-secrets** management, see the **[Azure setup runbook](docs/deployment/azure-setup.md)**.
> Hit a wall getting the API or Key Vault live on Azure? The **[Key Vault deployment troubleshooting log](docs/deployment/keyvault-deployment-troubleshooting.md)**
> is a chronological post-mortem of every symptom, log technique, root cause, and command.

## Documentation

All guides live under [`docs/`](docs/), grouped by topic. New to the project? Start with the
**Azure setup runbook**.

**Deployment & operations** — [`docs/deployment/`](docs/deployment/)

- **[Azure setup runbook](docs/deployment/azure-setup.md)** — **start-to-finish Azure guide**: one ordered pass from an empty subscription to a fully working deployment (App Service, passwordless Azure SQL, Static Web Apps, Google sign-in, CORS, Key Vault). Start here for a fresh setup.
- **[Azure reference](docs/deployment/azure.md)** — the detailed Azure deploy reference (App Service + Static Web Apps), passwordless database via managed identity, Google sign-in, and secrets / Key Vault.
- **[Deployment guide](docs/deployment/deployment.md)** — build, compile, and deploy anywhere (Docker Compose, Linux + nginx, Azure), with the included Dockerfiles and compose samples.
- **[Google sign-in](docs/deployment/google-signin.md)** — end-to-end Google sign-in setup: Cloud project, consent screen, OAuth client, wiring the client ID into the frontend and backend, and troubleshooting.
- **[Key Vault](docs/deployment/key-vault.md)** — what this project stores in Azure Key Vault (just the JWT signing key — passwordless DB and a public client id mean nothing else), the two ways to wire it in, RBAC vs. access-policy, and how it stays optional locally.
- **[Key Vault deployment troubleshooting](docs/deployment/keyvault-deployment-troubleshooting.md)** — a full chronological post-mortem of getting the API + Key Vault working on Azure: every symptom, how we read the logs (Kudu VFS API, `docker.log`), the root-cause chain (stale build, wwwroot pollution, `DefaultAzureCredential` hang, missing `https://`, SQL cold start), the clean rebuild, and every PowerShell / `az` / SQL / bash command used.
- **[Pipeline testing & error handling](docs/deployment/pipeline-testing.md)** — how the GitHub Actions pipeline is structured, how GitHub's fail-fast + notifications stop a broken build/test/publish from ever deploying, the "Verify publish output" guard against a hollow publish, and how to test all of it (including a zero-change local check).

**Architecture & design** — [`docs/architecture/`](docs/architecture/)

- **[Tech stack](docs/architecture/tech-stack.md)** — the full stack (backend, frontend, data, auth, testing, hosting, CI/CD) with a one-line explanation of what each technology does and why it's used.
- **[Request flow: login → board](docs/architecture/request-flow.md)** — a worked, end-to-end trace of one real path through the app (sign-in, the JWT + security-stamp check, and the user-scoped query that populates the board), with a sequence diagram and the exact files/handlers involved.
- **[Database portability](docs/architecture/database-portability.md)** — keeping behavior identical across relational providers (SQLite / SQL Server / PostgreSQL): the provider switch, collation & cascade gotchas, multi-provider CI, and what a non-relational port (Cosmos / MongoDB / DynamoDB) would actually take.
- **[Onion architecture diagram](docs/architecture/onion-architecture.svg)** — the layered dependency diagram used above.
- **[Architecture & practices assessment](docs/architecture/assessment.md)** — an evidence-based review of how well the project adheres to Clean Architecture, SOLID, design patterns, and CI/CD best practices, with graded verdicts and a prioritized improvement backlog.

**Development** — [`docs/development/`](docs/development/)

- **[Testing guide](docs/development/testing.md)** — how the frontend (Vitest + React Testing Library) and API (xUnit unit + `WebApplicationFactory` integration) test suites are set up, **step-by-step instructions for adding a new test** on each side, the from-scratch Vitest/RTL standup (including where Babel fits), and the deploy gate that blocks a release when tests fail.
- **[API smoke test](scripts/README.md)** — `scripts/todoapp-smoketest.ps1`: an end-to-end PowerShell check that hits every endpoint against a running instance, how to run it, and why a green run is a mix of expected status codes (not all 200).

**Reference**

- **[Lessons learned](docs/lessons.md)** — real-world gotchas hit building and shipping this (SQLite vs Azure SQL, serverless cold starts, deployment, hostnames, CI/CD, config & secrets).

## Prerequisites

- .NET 10 SDK (or Visual Studio 2026)
- Node.js 18+ and npm (for the frontend)

## Run the backend

```bash
dotnet restore
dotnet run --project src/TodoApp.WebApi
```

The API starts at `http://localhost:5080`; Swagger UI is at `http://localhost:5080/swagger`.
On first run it creates a SQLite database (`todoapp.db`) and seeds a demo user:

- **Email:** `demo@todoapp.local`
- **Password:** `Password123!`

In Visual Studio 2026: open `TodoApp.sln`, set **TodoApp.WebApi** as the startup
project, and press F5.

### JWT signing key (required — no secrets in appsettings)

No secrets are committed to `appsettings.json` (the `Jwt:Key` there is an empty
placeholder). You must supply the signing key externally; the app **fails fast** on
startup if it's missing or under 32 bytes. For local development, use user-secrets
(one-time setup):

```bash
dotnet user-secrets init --project src/TodoApp.WebApi
dotnet user-secrets set "Jwt:Key" "$(openssl rand -base64 48)" --project src/TodoApp.WebApi
```

In production, provide it via an environment variable or a secrets manager (Azure Key
Vault, AWS/GCP Secrets Manager, etc.):

```bash
export Jwt__Key="<a-long-random-secret>"
```

The integration tests inject their own throwaway key via an environment variable, so
`dotnet test` needs no setup.

> **Using Azure Key Vault?** The JWT signing key is the one real secret this project has —
> the database is passwordless (managed identity) and the Google client id is public, so
> nothing else needs a vault. For what to store, the exact code changes, how it stays optional
> so the app still runs locally without a vault, and how to verify it, see
> **[KEY_VAULT.md](docs/deployment/key-vault.md)**.

### Test the API locally

Protected endpoints require a JWT. The flow is always: **log in → get an access token → send it
as `Authorization: Bearer <token>`.** Three ways to do it:

**Swagger UI** (easiest — `http://localhost:5080/swagger`):

1. Expand **POST `/api/auth/login`** → **Try it out**, send the demo credentials:

   ```json
   { "email": "demo@todoapp.local", "password": "Password123!" }
   ```

   Execute, then copy the `accessToken` value from the response.
2. Click **Authorize** (top-right padlock) and paste **only the raw token — no `Bearer ` prefix**
   (this scheme adds it for you). Authorize → Close.
3. Call any protected endpoint, e.g. **GET `/api/categories`** → **Try it out** → **Execute** → 200.

**curl / bash:**

```bash
TOKEN=$(curl -s -X POST http://localhost:5080/api/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"email":"demo@todoapp.local","password":"Password123!"}' | jq -r .accessToken)
curl -s http://localhost:5080/api/categories -H "Authorization: Bearer $TOKEN"
```

**PowerShell** (Windows — put each statement on its own line, no backtick continuations):

```powershell
$base = "http://localhost:5080"
$body = '{"email":"demo@todoapp.local","password":"Password123!"}'
$login = Invoke-RestMethod -Uri "$base/api/auth/login" -Method Post -ContentType "application/json" -Body $body
$token = $login.accessToken
Invoke-RestMethod -Uri "$base/api/categories" -Headers @{ Authorization = "Bearer $token" }
```

Gotchas worth knowing:

- **Use a fresh token each session.** Reusing a token minted by an earlier run (whose signing key
  differed) fails with `401 … "The signature key was not found"` — that's a key mismatch, not a bug.
  Always log in against the same running instance you're calling.
- **Swagger Authorize takes the raw token**, not `Bearer <token>` — double-prefixing gives a 401.
- **Access tokens last 15 minutes.** After that, protected calls 401; log in again for a new one.
- **PowerShell:** don't paste multi-line commands that use backtick (`` ` ``) continuations — pasting
  splits them and the request loses its body (a 415 Unsupported Media Type). Keep each call on one line.

For the automated tests instead, `dotnet test` runs the unit and integration suites with no setup
(they inject their own signing key).

#### Troubleshooting `401 … "The signature key was not found"`

This means the token was signed with a different key than the running app validates with. Usual
causes: a stale/cross-instance token, or an environment variable overriding your user-secret (env
vars win over user-secrets in ASP.NET Core config). Diagnose on **Windows PowerShell**:

```powershell
# 1) Is a Jwt__Key env var set in any scope? (blank = not set = good)
$env:Jwt__Key
[Environment]::GetEnvironmentVariable('Jwt__Key','User')
[Environment]::GetEnvironmentVariable('Jwt__Key','Machine')

# 2) What key is actually stored for the project?
dotnet user-secrets list --project .\src\TodoApp.WebApi
```

If any of the env-var checks print a value, it's overriding your user-secret — clear it and restart
the app so signing and validation use the same key:

```powershell
Remove-Item Env:\Jwt__Key -ErrorAction SilentlyContinue          # current session
[Environment]::SetEnvironmentVariable('Jwt__Key', $null, 'User') # persistent User scope, if set
```

If `user-secrets list` shows no `Jwt:Key`, set one (it persists across restarts, so tokens stay
valid):

```powershell
dotnet user-secrets set "Jwt:Key" ([Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Max 256 }))) --project .\src\TodoApp.WebApi
```

Then get a **fresh** token and retry — old tokens minted before the fix won't validate.

### Google sign-in (optional)

Users can sign in with Google. The client uses Google Identity Services to obtain a
Google **ID token**, posts it to `POST /api/auth/google`, and the backend verifies it
(signature, issuer, audience, expiry) via `Google.Apis.Auth`, then finds/links a local
user and issues our own tokens — so the JWT + revocation model is unchanged. A Google
user with no local password can't use password login; if their Google email matches an
existing account, the two are linked.

To enable it, quick version:

1. In the [Google Cloud console](https://console.cloud.google.com/apis/credentials),
   create an **OAuth 2.0 Client ID** of type **Web application**.
2. Add `http://localhost:5173` under **Authorized JavaScript origins**.
3. Put the resulting client ID in **both** places (they must match):
   - Backend: `Authentication:Google:ClientId` (env var `Authentication__Google__ClientId`
     or user-secrets).
   - Frontend: `VITE_GOOGLE_CLIENT_ID` in `frontend/.env.local`.

If the client ID is left blank, the Google button is simply hidden and email/password
auth works as normal.

📄 **Full step-by-step (project, consent screen, credentials, no-secrets notes, and
troubleshooting): [GOOGLE_SIGNIN.md](docs/deployment/google-signin.md).**

## Run the frontend

```bash
cd frontend
npm install
npm run dev
```

The app runs at `http://localhost:5173` and proxies `/api` to the backend, so start the
backend first. Sign in with the demo account or register a new one.

## Authentication & authorization

Every `/api/todos` endpoint requires a valid access token, and todos are **scoped to the
signed-in user** — you can only see and modify your own.

**Token model**

- **Access token** — a short-lived (15 min) JWT sent as `Authorization: Bearer <token>`.
  It carries the user id (`sub`), `role`, and a per-user **security stamp** (`sstamp`).
- **Refresh token** — a long-lived (7 day), single-use random token. Only its SHA-256
  hash is stored server-side. It's returned in the response body once and rotated on
  every refresh.

**How revocation works (compromised accounts)**

JWTs are stateless and normally can't be un-issued before they expire. Two mechanisms
solve that:

1. *Security stamp.* Every access token embeds the user's current stamp, and every
   request re-checks it against the database (`OnTokenValidated`). Rotating the stamp
   invalidates **all** of that user's outstanding access tokens instantly.
2. *Refresh-token store.* Refresh tokens are persisted (hashed) and individually
   revocable, with rotation and **reuse detection** — presenting an already-rotated
   token is treated as theft and triggers a full revocation of the user's sessions.

`POST /api/auth/revoke-all` ("sign out everywhere") rotates the stamp **and** revokes all
refresh tokens — the response to a suspected compromise. A user can revoke their own
sessions; an `Admin` may pass a `userId` to revoke another user's.

## API

Auth (`/api/auth`):

| Method | Route          | Auth        | Description                                   |
|--------|----------------|-------------|-----------------------------------------------|
| POST   | `/register`    | anonymous   | Create an account; returns tokens             |
| POST   | `/login`       | anonymous   | Sign in; returns tokens                       |
| POST   | `/refresh`     | anonymous\* | Exchange a refresh token for a new pair       |
| POST   | `/google`      | anonymous   | Exchange a Google ID token for our tokens     |
| POST   | `/logout`      | bearer      | Revoke the presented refresh token            |
| POST   | `/revoke-all`  | bearer      | Revoke ALL sessions (compromise response)     |
| GET    | `/me`          | bearer      | Current user profile                          |

\* `/refresh` takes the refresh token in the body rather than an access token.

Todos (`/api/todos`, **all require bearer auth**):

| Method | Route                     | Description                                  |
|--------|---------------------------|----------------------------------------------|
| GET    | `/api/todos`              | List your todos. Query: `filter`, `search`   |
| GET    | `/api/todos/{id}`         | Get one of your todos                        |
| POST   | `/api/todos`              | Create a todo                                |
| PUT    | `/api/todos/{id}`         | Update a todo                                |
| PATCH  | `/api/todos/{id}/status`  | Move to another lane (To Do / In Progress / Done) |
| DELETE | `/api/todos/{id}`         | Delete a todo                                |

Categories (`/api/categories`, **all require bearer auth**):

| Method | Route                     | Description                                  |
|--------|---------------------------|----------------------------------------------|
| GET    | `/api/categories`         | List your categories                         |
| POST   | `/api/categories`         | Create a category (`name`, `color`)          |
| PUT    | `/api/categories/{id}`    | Rename / recolor a category                  |
| DELETE | `/api/categories/{id}`    | Delete a category (its tasks become uncategorized) |

`priority` is an integer (`0 = Low`, `1 = Medium`, `2 = High`) and `status` is `0 = To Do`,
`1 = In Progress`, `2 = Done`. A todo's `categoryId` is the id of one of your categories (or
`null` for uncategorized); `color` is a `#RRGGBB` hex string. Validation errors return
`400` (RFC 7807 `ValidationProblemDetails`); auth failures `401`; forbidden `403`;
missing/for­eign resources `404`; duplicate email `409`.

Example:

```bash
# 1) Log in
curl -s -X POST http://localhost:5080/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"demo@todoapp.local","password":"Password123!"}'

# 2) Use the accessToken from the response
curl http://localhost:5080/api/todos \
  -H "Authorization: Bearer <accessToken>"
```

In Swagger UI, click **Authorize** and paste the access token to call protected endpoints.

## Concurrency & conflicts

Two kinds of write conflict are handled explicitly, both returning **409**:

- **Optimistic concurrency (lost-update protection).** Each `TodoItem` carries a
  `ConcurrencyToken` (returned in `TodoItemDto`). Send it back on `PUT /api/todos/{id}`
  (the React client does automatically); EF Core includes it in the `UPDATE ... WHERE`
  clause, so if the item changed since you loaded it the save affects zero rows and the
  API responds `409` with the current server state under a `current` field. The client
  reloads and asks the user to re-apply. Omitting the token falls back to last-writer-wins.
- **Unique-constraint races.** Registration and Google sign-in pre-check for an existing
  email, but a concurrent duplicate insert is still caught (`DbUpdateException` on the
  unique index) and translated to `409` instead of a 500.

## Tests

A focused xUnit suite lives in `tests/TodoApp.UnitTests`:

```bash
dotnet test
```

It covers domain invariants and the security-critical handlers, using in-memory SQLite
for a real `IApplicationDbContext` plus small fakes for the other interfaces. Highlights:

- **Ownership** — a user cannot read, update, or delete another user's todo (404), and
  `GetTodos` returns only the caller's items.
- **Revocation** — `revoke-all` rotates the security stamp and revokes every refresh
  token; refresh-token **reuse detection** revokes all sessions and rotates the stamp.
- **Auth** — register/login happy paths, duplicate-email conflict, wrong password, and
  external-only (Google) users being rejected by password login.
- **Google** — new-email create, existing-email link (no duplicate), invalid and
  unverified tokens rejected.

Time is injected through `IDateTimeProvider`, so tests use a `FakeDateTimeProvider` to
make token-expiry and audit-timestamp behavior deterministic.

- **Concurrency** — updating a todo with a stale `ConcurrencyToken` returns `409`
  (unit + integration), and with the current token succeeds and rotates the token.

### End-to-end smoke test

Beyond the automated suites, **`scripts/todoapp-smoketest.ps1`** hits **every** API endpoint over HTTP
against a running instance and prints a pass/fail report — a fast health check after a change, or a
post-deploy smoke test. Start the API, then run it in a second terminal:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\todoapp-smoketest.ps1
```

**Reading the results: green means "got the expected status," not "returned 200."** Several checks
deliberately assert an error code — `401` (no token), `400` (bad input), `409` (duplicate name / stale
concurrency token), `204` (delete / logout / revoke-all) — and show green when they receive it. You don't
need to read every line; only a red `[FAIL]` needs attention. Full details, including the per-check table,
are in the [testing guide](docs/development/testing.md) (§3.4), and in [`scripts/README.md`](scripts/README.md).

## Database & migrations

For simplicity the app calls `EnsureCreated()` at startup (no migration files). To switch
to EF Core migrations:

```bash
dotnet tool install --global dotnet-ef
dotnet ef migrations add InitialCreate \
  --project src/TodoApp.Infrastructure --startup-project src/TodoApp.WebApi
dotnet ef database update \
  --project src/TodoApp.Infrastructure --startup-project src/TodoApp.WebApi
```

Then replace `EnsureCreatedAsync()` in `DbInitializer.cs` with `MigrateAsync()`.

**SQLite & `DateTimeOffset`.** SQLite has no native `DateTimeOffset` type and can't `ORDER BY`
one, so the context stores every `DateTimeOffset` as a UTC-tick `long` via a value converter
(`DateTimeOffsetToUtcTicksConverter`, registered in `ConfigureConventions`). This keeps
ordering/filtering in the database and sorts correctly by instant. It changes the column
storage type, so if you're upgrading from an older `todoapp.db`, delete the file (or use
migrations) so the schema is rebuilt.

## Security notes & production hardening

- Passwords are hashed with **PBKDF2 (SHA-256, 100k iterations, per-password salt)** and
  compared in constant time. Swap in Argon2id if you prefer.
- The React client keeps the **access token in memory** and persists the **refresh token
  in `localStorage`** so a reload can re-authenticate. `localStorage` is exposed to XSS;
  for production, deliver the refresh token in an **httpOnly, Secure, SameSite cookie**
  instead (the token model here already supports it — only the transport changes).
- The per-request security-stamp check is a lightweight DB read; cache it (e.g. Redis)
  if it ever becomes a hotspot, or add a Redis `jti` denylist for instant single-token
  revocation at scale.
- **No secrets in `appsettings.json`.** The signing key comes from user-secrets or the
  environment (above); the app refuses to start without it. The Google *client ID* is not
  a secret (it's public and embedded in the frontend), and this flow uses no Google client
  *secret* at all — it only verifies Google-issued ID tokens. The demo user's password is
  seed data in `DbInitializer` for convenience; remove it for any real deployment.
- Serve everything over HTTPS in production.
- NuGet versions use floating ranges (e.g. `10.0.*`); pin exact versions for
  reproducible builds.
