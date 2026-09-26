# API reference

The HTTP surface of the backend: the authentication/authorization model, rate limiting,
every endpoint with its auth requirement, how write conflicts are reported, and the
production-hardening notes for the security-sensitive pieces.

> Want to call these endpoints by hand? See **[local development](../development/local-dev.md)**
> for log-in-and-Bearer-token walkthroughs (Swagger, curl, PowerShell). Want to
> exercise all of them at once? See the **[API smoke test](../../scripts/README.md)**.

## Authentication & authorization

Every `/api/todos` and `/api/categories` endpoint requires a valid access token, and data is
**scoped to the signed-in user** — you can only see and modify your own.

**Signing in**

- **Email + password** — `POST /register` or `/login`. Passwords are 8–128 characters and
  hashed with PBKDF2 (see [security notes](#security-notes--production-hardening)).
  Registration rejects passwords found in known breaches.
- **Google** — the SPA obtains a Google ID token and posts it to `POST /google`. The API
  validates it, requires Google's `email_verified`, then signs in the user already linked to
  that Google account, or links it to an existing local account with the same email, or
  creates a new account (with the starter categories).

Either way the API issues **its own** tokens — Google is only an identity source.

**Token model**

- **Access token** — a short-lived (15 min) HS256 JWT sent as `Authorization: Bearer <token>`.
  It carries the user id (`sub`), `email`, `role`, and a per-user **security stamp** (`sstamp`).
  The SPA keeps it **in memory only**.
- **Refresh token** — a 7-day, single-use random value (32 bytes). Only its SHA-256 hash is
  stored server-side. It is delivered as an **`httpOnly`, `Secure`, `SameSite=None` cookie**
  (`todo_rt`, path `/api/auth`) — JavaScript cannot read it, so an XSS cannot steal it.
  `SameSite=None` is required because the SPA (`*.azurestaticapps.net`) and API
  (`*.azurewebsites.net`) are different sites. It is rotated on every refresh.
- **CSRF on `/refresh`** — because the cookie is sent cross-site, `/refresh` also requires an
  `X-Refresh-CSRF` header (any non-empty value). A cross-site form or image tag cannot add a
  custom header without a CORS preflight, and CORS only allows the SPA's origin.
- **Legacy body delivery** — `Auth:RefreshTokenInBody=true` also returns the refresh token in
  the JSON body (used by the smoke test). It is `false` in deployed environments.

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

The same per-request check also rejects a **deactivated** user (`IsActive = false`): their
outstanding access tokens stop working immediately, and `/login`, `/google` and `/refresh`
refuse them.

**Authorization**

- **Endpoint level** — everything except `/register`, `/login`, `/refresh` and `/google`
  requires a valid bearer token (`RequireAuthorization()`).
- **Row level** — every todo/category handler filters by the caller's id
  (`t.Id == id && t.UserId == userId`). Someone else's resource returns **`404`, not `403`**,
  so the API never confirms that it exists.
- **Roles** — `User` (0, the default) and `Admin` (1), carried in the `role` claim. The only
  admin-only action today is `POST /api/auth/revoke-all` with another user's `userId`. Admins
  cannot read other users' todos.

**Making a user an admin**

Nothing in the app grants the role — no endpoint changes it, and the demo seed creates a plain
`User`. Set it directly in the database:

```sql
-- PostgreSQL (Neon): EF Core created mixed-case identifiers, so quote them
UPDATE "Users" SET "Role" = 1 WHERE "Email" = 'you@example.com';

-- SQL Server / SQLite
UPDATE Users SET Role = 1 WHERE Email = 'you@example.com';
```

Then **sign out and back in** — the role lives in the access token, so it only takes effect on a
newly issued one. Never promote the demo account: its password is public.

## Rate limiting

ASP.NET Core's rate limiter uses fixed windows, partitioned **per client IP**:

| Policy | Applies to | Default (`appsettings.json`) | Development |
| ------ | ---------- | ---------------------------- | ----------- |
| `auth` | all of `/api/auth` | **10 requests / 60 s** | 100 / 60 s |
| global | every request | **200 requests / 60 s** | 200 / 60 s |

A rejected request gets **`429`** with a `Retry-After` header (seconds, taken from whichever
limiter rejected it) and a Problem Details body. Tune under `RateLimiting:Auth` and
`RateLimiting:Global` (`PermitLimit`, `WindowSeconds`).

**Behind App Service, set `RateLimiting:TrustForwardedFor=true`.** App Service is a reverse
proxy: every request reaches the app from the platform's address. With the setting off (the
shipped default), all callers share **one** partition — the whole site gets 10 sign-ins and
200 requests a minute between them. With it on, `ClientAddress.Resolve` uses the **last**
`X-Forwarded-For` entry, which App Service appends itself. Earlier entries are ignored because
the client controls them and could vary them per request to escape the limit. Both
provisioning scripts set it. To check a live app:

```powershell
az webapp config appsettings list -g rg-taskboard -n taskboard-06-api -o table |
  Select-String TrustForwardedFor
```

The last column is the value (`true`); the `False` beside it is the unrelated *slot setting* flag.
Callers behind one public IP (an office, carrier NAT) still share a partition. Background:
[security remediation H3](../development/security-remediation.md#h3--no-rate-limiting-on-auth-endpoints-unbounded-password-length)
and [lessons — the untestable line](../lessons.md#the-untestable-line--ipv6-callers-were-exempt-from-rate-limiting).

## Endpoints

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

\* `/refresh` needs no access token: it reads the refresh token from the `todo_rt` cookie
(send `credentials: 'include'`) and requires the `X-Refresh-CSRF` header. A token in the body
is still accepted for legacy clients. All `/api/auth` routes are under the `auth` rate limit.

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
missing/foreign resources `404`; duplicate email `409`; rate-limited `429`.

Example:

```bash
# 1) Log in
curl -s -X POST http://localhost:5080/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"demo@todoapp.local","password":"Password123!"}'

# 2) Use the accessToken from the response
#    (the refresh token arrives as the todo_rt cookie, not in the body)
curl http://localhost:5080/api/todos \
  -H "Authorization: Bearer <accessToken>"
```

In Swagger UI, click **Authorize** and paste the access token to call protected endpoints.

## How the endpoints are wired

The routes above aren't declared in `Program.cs`. Each resource group lives in its own
static class under `src/TodoApp.WebApi/Endpoints/` — `AuthEndpoints`, `CategoryEndpoints`,
`TodoEndpoints` — exposed as an extension method on `IEndpointRouteBuilder`. `Program.cs`
just composes them, in order, after the auth middleware:

```csharp
app.UseAuthentication();
app.UseAuthorization();

app.MapAuthEndpoints();
app.MapCategoryEndpoints();
app.MapTodoEndpoints();

app.MapGet("/", () => Results.Redirect("/swagger"));   // root → Swagger UI
```

Keeping the mapping out of `Program.cs` means each resource's routes are self-contained and
the startup file stays a readable table of contents. The extension methods return the builder,
so they read fluently and could be chained.

**A group per resource.** Each method opens with `MapGroup`, which fixes the base path once and
lets every route hang off it. Shared metadata is applied to the group so it doesn't repeat per
route — `WithTags` sets the Swagger section, and for the fully-protected resources authorization
is applied at the **group** level:

```csharp
var group = app.MapGroup("/api/todos")
    .WithTags("Todos")
    .RequireAuthorization();        // every route in this group needs a bearer token

group.MapGet("/{id:int}", /* ... */)
     .WithName("GetTodoById")
     .Produces<TodoItemDto>()
     .Produces(StatusCodes.Status404NotFound);
```

`Todos` and `Categories` are protected this way — one `.RequireAuthorization()` on the group
covers all their routes. `Auth` is the exception: its group carries only `.WithTags("Auth")`, and
authorization is set **per route** because the group is mixed — `.AllowAnonymous()` on
`register`, `login`, `refresh`, and `google`, and `.RequireAuthorization()` on `logout`,
`revoke-all`, and `me`.

**Thin handlers.** Every route delegates straight to MediatR — the lambda injects `ISender`,
sends a command or query, and maps the result to an HTTP status. There's no business logic in the
web layer:

```csharp
group.MapPost("/", async (CreateTodoCommand command, ISender sender) =>
{
    var created = await sender.Send(command);
    return Results.CreatedAtRoute("GetTodoById", new { id = created.Id }, created);
})
.WithName("CreateTodo")
.Produces<TodoItemDto>(StatusCodes.Status201Created)
.ProducesValidationProblem();
```

A few conventions are worth calling out:

- **Route constraints do the first validation.** `{id:int}` means a non-numeric id never
  reaches a handler — it 404s at routing time.
- **The route id is never trusted from the body.** Updates bind a dedicated request record
  (`UpdateTodoRequest`, `StatusRequest`, `CategoryRequest`) and the handler builds the command
  from the **route** `id` plus the body fields, so a mismatched id in the payload can't act on a
  different resource.
- **`WithName` is load-bearing, not decoration.** Named routes give `Results.CreatedAtRoute(...)`
  a target for the `Location` header (see `CreateTodo` → `GetTodoById` above) and set a stable
  Swagger `operationId`.
- **`Produces` / `ProducesValidationProblem` describe the contract.** They don't change behavior —
  they tell Swagger/OpenAPI which status codes and payload shapes each route can return, which is
  why the generated docs show `201`/`404`/`409`/validation responses accurately.

So the shape of any endpoint is the same: `MapGroup` for the path and shared policy, a `Map{Verb}`
per route with a constraint where an id is involved, a thin `ISender.Send` handler returning a
`Results.*`, and fluent metadata (`WithName`, `Produces`) that feeds routing and Swagger.

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

## Security notes & production hardening

- Passwords are hashed with **PBKDF2 (SHA-256, 600,000 iterations — OWASP's current guidance —
  128-bit per-password salt)** and compared in constant time. The iteration count is stored in
  each hash, so older 100k hashes still verify and are **re-hashed at 600k on the user's next
  login**. Login for an unknown email burns the same PBKDF2 work, so response time doesn't
  reveal which emails exist.
- Registration checks the password against **Have I Been Pwned** (k-anonymity range API — only
  a 5-character hash prefix leaves the server). It **fails open**: if HIBP is slow or down
  (`PasswordBreachCheck:TimeoutSeconds`, default 3), the password is allowed and a warning logged.
- The React client keeps the **access token in memory**; the **refresh token is an `httpOnly`
  cookie** the page cannot read. On reload the SPA simply calls `/refresh` (with
  `credentials: 'include'`) and treats `401` as "not signed in". Concurrent `401`s share one
  in-flight refresh, because two refreshes with the same token would trip reuse detection and
  sign the user out everywhere ([lessons](../lessons.md#the-real-find--concurrent-refresh-signed-users-out-everywhere)).
- The per-request security-stamp check is a DB read on **every authenticated request** — so an
  active session keeps a scale-to-zero database (Neon) awake. Cache it (e.g. Redis) if it ever
  becomes a hotspot, or add a Redis `jti` denylist for instant single-token revocation at scale.
- **No secrets in `appsettings.json`.** The signing key comes from user-secrets or the
  environment (see [local development](../development/local-dev.md#jwt-signing-key-required--no-secrets-in-appsettings));
  the app refuses to start without it. The Google *client ID* is not a secret (it's public
  and embedded in the frontend), and this flow uses no Google client *secret* at all — it
  only verifies Google-issued ID tokens. Demo seeding is **opt-in** (`Seed:DemoUser`), with
  the password supplied by configuration ([H1](../development/security-remediation.md#h1--demo-account-with-a-published-password-was-seeded-in-every-environment)).
- Serve everything over HTTPS in production.
- NuGet versions are pinned exactly, and CI restores in locked mode against
  `packages.lock.json`.
