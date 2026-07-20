# Local development

Everything you need to build, run, and manually exercise the app on your own
machine: prerequisites, running the backend and frontend, supplying the JWT
signing key, three ways to test the API by hand, a `401` troubleshooting
playbook, and the local database story.

> Just want the automated tests? See the **[testing guide](testing.md)**. Want a
> one-shot end-to-end check of every endpoint? See the
> **[API smoke test](../../scripts/README.md)**.

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
> so the app still runs locally without a vault, and how to verify it, see the
> **[Key Vault guide](../deployment/key-vault.md)**.

## Test the API locally

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
(they inject their own signing key). To hit every endpoint in one pass, run the
[smoke test](../../scripts/README.md).

### Troubleshooting `401 … "The signature key was not found"`

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

## Run the frontend

```bash
cd frontend
npm install
npm run dev
```

The app runs at `http://localhost:5173` and proxies `/api` to the backend, so start the
backend first. Sign in with the demo account or register a new one.

## Google sign-in (optional, local)

Users can sign in with Google. If the client ID is left blank, the Google button is simply
hidden and email/password auth works as normal. To enable it locally, create an **OAuth 2.0
Client ID** (Web application) in the [Google Cloud console](https://console.cloud.google.com/apis/credentials),
add `http://localhost:5173` as an authorized origin, and put the client ID in **both**
`Authentication:Google:ClientId` (backend) and `VITE_GOOGLE_CLIENT_ID` in
`frontend/.env.local` (they must match).

📄 Full step-by-step (project, consent screen, credentials, no-secrets notes, and
troubleshooting): **[Google sign-in guide](../deployment/google-signin.md)**.

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
