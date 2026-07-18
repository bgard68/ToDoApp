# Lessons Learned — Shipping TaskBoard to Azure

_[← Back to the main README](../README.md)_

A running list of the real-world gotchas hit while building this .NET 10 + React app and
deploying it end to end (App Service + Azure SQL + Static Web Apps with CI/CD). Kept as a
reference for future deployments — most of these cost more time than the code did.

> For the blow-by-blow of the hardest stretch (getting the API + Key Vault live and the CI/CD
> pipelines green), see the **[Key Vault deployment troubleshooting log](deployment/keyvault-deployment-troubleshooting.md)**.

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

## CI/CD (GitHub Actions)

The repo ships **two** workflows in `.github/workflows/`: `api-ci-cd.yml` (build → test → publish → deploy the API to App Service via OIDC) and `frontend-ci-cd.yml` (build → deploy the SPA to Static Web Apps). Lessons from getting both green:

- **One app = one pipeline.** A Static Web Apps workflow only deploys the SPA; the API needs its *own* workflow. A push that only updates the SWA workflow will never redeploy the API.
- **"Build and Deploy" in the name doesn't mean it deploys.** A workflow that builds, tests, and *uploads an artifact* but has no `azure/login` + `azure/webapps-deploy` steps produces nothing on Azure. Confirm the deploy steps actually exist.
- **Publish the project, not the solution** — `dotnet publish src/TodoApp.WebApi/TodoApp.WebApi.csproj`, never a bare `dotnet publish` in a multi-project repo (it dumps test DLLs into `wwwroot` and the app serves no routes).
- **OIDC (federated) deploy needs `permissions: id-token: write`** at the workflow/job level, or `azure/login` fails.
- **Deployment Center appends a random suffix to the secret names** it creates (`AZUREAPPSERVICE_CLIENTID_<hex>`), and each of the three (CLIENTID/TENANTID/SUBSCRIPTIONID) gets its *own* suffix — you can't group them by name. The **generated workflow** (in git history) is the source of truth for which trio belongs to which app. A name mismatch resolves to empty → `azure/login` errors with *"Not all values are present. Ensure 'client-id' and 'tenant-id' are supplied."*
- **Reconnecting Deployment Center for a new app leaves the old app's secrets behind.** After deleting an App Service, its `AZUREAPPSERVICE_*` trio and any dead Static Web App's `AZURE_STATIC_WEB_APPS_API_TOKEN_*` linger in the repo → delete the unreferenced ones. Cross-check against what the live workflows actually reference before deleting.
- Vite env vars (`VITE_*`) are **build-time** — set them as GitHub repository **Variables** and pass them in the workflow's `env:`, not in the SWA's runtime config.
- `VITE_API_URL` is the **API** URL (the one that shows Swagger) and **must include `https://`**, never the site's own URL — without the scheme the browser treats it as a relative path (→ 405).
- A Static Web App only deploys from the branch it watches — the workflow must live on `main`; recreate the SWA to repoint it.
- **Removing a leftover / obsolete workflow.** The Actions tab only lets you *disable* a workflow, not delete it — a workflow exists because its `.yml` file is in `.github/workflows/` on the default branch, and deleting the Azure resource behind it (App Service / Static Web App) does **not** remove that file. To actually get rid of it, delete the file from the repo:
  ```bash
  git rm .github/workflows/<old-workflow>.yml
  git commit -m "Remove obsolete workflow"
  git push
  ```
  Local and GitHub are the **same** repo, so that one commit clears it from both. A stray `.yml` "residing in both" just means a delete was done on one side and not synced — reconcile with `git pull` / `git push` (confirm with `git status`). If you deleted it via GitHub's web UI instead, `git pull` locally to catch up. Then delete the orphaned **deployment-token / OIDC secret**, and check **other branches** — a lingering copy on a feature branch makes the workflow reappear. Old *runs* stay in history; that's fine, they're just logs.
- **Trigger a workflow without code changes.** For a `push`-triggered workflow, an **empty commit** re-runs it:
  ```bash
  git commit --allow-empty -m "Trigger CI"
  git push
  ```
  Cleaner still, if the workflow declares `workflow_dispatch` (both here do), use the **Run workflow** button on the Actions tab — no commit at all. Add `[skip ci]` to a commit message to do the opposite and *skip* the workflows for that push.
- **CRLF vs LF:** cloning on Windows can make git show *every file* as "modified" (line-ending drift). Add a `.gitattributes` with `* text=auto eol=lf`, run `git add --renormalize .`, and commit once — the noise disappears for good. Keep YAML on LF so a stray CRLF never masks a real change.

## GitHub Actions secrets in a public repo

Making the repo public does **not** expose your Actions secrets — provided you never hardcode a value into a file and always reference `${{ secrets.NAME }}` (both workflows here do).

- Secrets are stored **encrypted** and never appear in the source or git history.
- They're **write-only** — no one, not even you or a collaborator, can read a value back through the UI or API; you can only overwrite or delete.
- They're **masked** (`***`) in the public run logs if a workflow ever prints one.
- Secrets are **not given to workflows triggered by fork pull requests**, and a first-time contributor's workflow run needs manual approval — that's the main defense against a malicious PR exfiltrating them.
- **Real risks to guard:** anyone with **write/admin** access can obtain secrets (they can push a workflow that uses them) → only add trusted collaborators. Avoid the **`pull_request_target`** trigger (it *does* expose secrets to fork PRs — a common footgun); this repo uses plain `push` / `pull_request`. And never `echo` a secret or write it to an artifact.
- The three `AZUREAPPSERVICE_*` values aren't even sensitive — they're just identifiers (client/tenant/subscription IDs); security comes from the **OIDC federated-trust**, not from them staying hidden. The one true secret is the **Static Web Apps deploy token**, which GitHub keeps encrypted.
- Reference: GitHub's *"Security hardening for GitHub Actions"* documentation.

## Config / secrets

- Passwordless DB access uses a managed identity: enable system-assigned identity, then `CREATE USER ... FROM EXTERNAL PROVIDER` with `db_datareader` / `db_datawriter` / `db_ddladmin` roles.
- The connection string uses `Authentication=Active Directory Default` — no user ID or password stored anywhere.
- Nested config keys become env vars with **double underscores** (`Cors__AllowedOrigins__0`, `Authentication__Google__ClientId`).
- The JWT signing key must be present or the app fails fast — keep it in user-secrets locally, an app setting in Azure.
- Environment variables **win over user-secrets** in ASP.NET Core config → a leftover `Jwt__Key` env var silently overrides the user-secret; check `$env:Jwt__Key` (and User/Machine scopes) when the key seems wrong.
- Azure Key Vault holds exactly **one** secret here (the JWT key) — passwordless DB and a public Google client id mean nothing else needs vaulting.
- Register Key Vault as a config source **gated on a `KeyVault:Uri` app setting**, not on the environment name → it stays optional, so the app runs locally with no vault, CI, or Azure login.
- Use a **Vault (Standard tier)**, not **Managed HSM** — Managed HSM stores cryptographic keys, not secrets, so it can't even hold the JWT key.
- `RandomNumberGenerator.GetBytes(int)` doesn't exist in Windows PowerShell 5.1 → use `Create().GetBytes($bytes)`.
- Visual Studio publish profiles (`*.pubxml`) can leak your App Service name → add them to `.gitignore`.
- Google sign-in: the client ID is public (no secret), must match on frontend and backend, and the site's origin must be added to Google's Authorized JavaScript origins.

## Local dev & auth testing

- `401 … "The signature key was not found"` means the token was signed with a **different key** than the running app validates with — almost always a stale token from an earlier run or a cross-instance token (deployed vs local), not a code bug. Get a fresh token from the same instance you're calling.
- Pin `Jwt:Key` in user-secrets so it **survives restarts** — an ad-hoc key (random per run) invalidates every previously issued token.
- Swagger's **Authorize** box takes the **raw token, no `Bearer ` prefix** (the HTTP bearer scheme adds it); double-prefixing gives a 401.
- Access tokens live **15 minutes** by design → protected calls 401 after that; log in again for a new one.
- In PowerShell, don't paste multi-line commands using backtick (`` ` ``) continuations — pasting splits them and the request loses its body (**415 Unsupported Media Type**). Keep each call on one line, or use `Invoke-RestMethod` with a `$body` variable.
