# Lessons Learned — Shipping TaskBoard to Azure

_[← Back to the main README](../README.md)_

A running list of the real-world gotchas hit while building this .NET 10 + React app and
deploying it end to end (App Service + Azure SQL + Static Web Apps with CI/CD). Kept as a
reference for future deployments — most of these cost more time than the code did.

## Database (SQLite vs Azure SQL)

- Behavior differs between SQLite and Azure SQL Server — code that runs locally can fail in the cloud.
- Multiple cascade paths: SQL Server rejects FK cascade cycles (error 1785) that SQLite happily allows → fix with `DeleteBehavior.ClientCascade`.
- SQLite can't `ORDER BY` a `DateTimeOffset` → store it as UTC ticks via a value converter.
- A value converter used in `ConfigureConventions` needs a parameterless constructor, or EF can't instantiate it.
- Serverless Azure SQL auto-pauses when idle → the first request cold-starts and times out (errors -2 / 40613).
- Running `EnsureCreated`/seeding at startup blocks the app from booting when the DB is asleep → move it off the startup path.
- Cold-start fixes: EF `EnableRetryOnFailure`, a longer `Connect Timeout`, and resilient (non-blocking) startup.

## Deployment

- `ASPNETCORE_ENVIRONMENT` defaults to Production when unset, which hides Swagger → set it to Development to expose Swagger.
- Visual Studio "no existing instances" comes from an account, subscription, or OS-type mismatch (Linux vs Windows).
- App Service ships with **basic auth publishing disabled**, so the publish profile has no credentials and VS can't deploy.
- PowerShell's `Compress-Archive` writes Windows backslashes, which break Linux zip-deploy → use `tar` instead.
- Linux App Service has no Kudu ZipDeployUI (Windows-only) → "no route registered"; deploy with `az webapp deploy`.
- The build must land in `/home/site/wwwroot`; an empty wwwroot means the deploy never actually reached the app.

## Networking / hostnames

- Newer App Services use a unique regional hostname (`<name>-<hash>.<region>-01.azurewebsites.net`); the short `<name>.azurewebsites.net` may not resolve → always use the Overview page's Default domain.
- The API must allow the site's origin via CORS (`Cors__AllowedOrigins__0`), exact URL, **no trailing slash**.
- A GET to a POST-only endpoint (e.g. `/api/auth/login`) returns 405 — that's expected, not a bug; test through the app, not the address bar.

## Frontend / CI-CD

- Vite env vars (`VITE_*`) are **build-time** — they must be set in the GitHub Actions workflow, not in the SWA's runtime config.
- `VITE_API_URL` is the **API** URL (the one that shows Swagger), never the site's own URL.
- A Static Web App only deploys from the branch it watches — the workflow must live on `main`; recreate the SWA to repoint it.
- Deleting a Static Web App leaves its workflow file and deployment-token secret behind in the repo → clean them up manually.

## Config / secrets

- Passwordless DB access uses a managed identity: enable system-assigned identity, then `CREATE USER ... FROM EXTERNAL PROVIDER` with `db_datareader` / `db_datawriter` / `db_ddladmin` roles.
- The connection string uses `Authentication=Active Directory Default` — no user ID or password stored anywhere.
- Nested config keys become env vars with **double underscores** (`Cors__AllowedOrigins__0`, `Authentication__Google__ClientId`).
- The JWT signing key must be present or the app fails fast — keep it in user-secrets locally, an app setting in Azure.
- `RandomNumberGenerator.GetBytes(int)` doesn't exist in Windows PowerShell 5.1 → use `Create().GetBytes($bytes)`.
- Visual Studio publish profiles (`*.pubxml`) can leak your App Service name → add them to `.gitignore`.
- Google sign-in: the client ID is public (no secret), must match on frontend and backend, and the site's origin must be added to Google's Authorized JavaScript origins.
