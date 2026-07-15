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
  guides, with runnable Dockerfiles and compose files. See
  [DEPLOYMENT.md](DEPLOYMENT.md) and [AZURE.md](AZURE.md).

**Tech:** .NET 10, ASP.NET Core Minimal APIs, EF Core, MediatR, FluentValidation, JWT,
React 18 + Vite, xUnit, Docker.

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

The system clock is abstracted behind **`IDateTimeProvider`** (production
implementation `DateTimeProvider` returns `DateTimeOffset.UtcNow`). Handlers inject it
and pass the timestamp into domain operations, so entities never read the ambient clock
— which keeps time-dependent behavior (token expiry, audit stamps) deterministic and
testable via a `FakeDateTimeProvider`.

> **Deploying?** See **[DEPLOYMENT.md](DEPLOYMENT.md)** for full setup, build/compile,
> and deployment instructions (Docker Compose, Linux + nginx, and Azure), plus the
> included `Dockerfile.api`, `frontend/Dockerfile`, `docker-compose.yml`, and `deploy/`
> samples. For a step-by-step **Azure** deploy (App Service + Static Web Apps), **Google
> sign-in** setup, and **secrets/user-secrets** management, see **[AZURE.md](AZURE.md)**.

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
troubleshooting): [GOOGLE_SIGNIN.md](GOOGLE_SIGNIN.md).**

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
