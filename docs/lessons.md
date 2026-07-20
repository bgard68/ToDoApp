# Lessons Learned — Shipping TaskBoard to Azure

_[← Back to the main README](../README.md)_

A running list of the real-world gotchas hit while building this .NET 10 + React app and
deploying it end to end (App Service + Azure SQL + Static Web Apps with CI/CD). Kept as a
reference for future deployments — most of these cost more time than the code did.

> For the blow-by-blow of the hardest stretch (getting the API + Key Vault live and the CI/CD
> pipelines green), see the **[Key Vault deployment troubleshooting log](deployment/troubleshooting-log.md)**.

## Top gotchas (quick index)

The ones that cost the most time — jump to the section for the full story:

- **A prod request 500s, shows a CORS error, or returns 405** → [Diagnosing a 500 in production](#diagnosing-a-500--failed-request-in-production): DB cold start, a wrong CORS origin, or a bad `VITE_API_URL`.
- **`azure/login` fails with "Not all values are present"** → [CI/CD](#cicd-github-actions): the Deployment Center secret-name suffix trio is mismatched.
- **The pipeline goes green but nothing lands on Azure** → [CI/CD](#cicd-github-actions): a workflow with no deploy step, or publishing the solution instead of the project.
- **`401 … "The signature key was not found"`** → [Local dev & auth testing](#local-dev--auth-testing): a stale/cross-instance token, or an env var overriding your user-secret.
- **Users get signed out of every device at once** → [The real find](#the-real-find--concurrent-refresh-signed-users-out-everywhere): parallel refreshes tripping reuse detection.
- **Serverless Azure SQL times out on the first request** → [Database](#database-sqlite-vs-azure-sql): auto-pause cold start (errors -2 / 40613) — retry, and keep seeding off the startup path.
- **`<name>.azurewebsites.net` won't resolve** → [Networking / hostnames](#networking--hostnames): use the Overview page's regional Default domain.
- **Drag-and-drop is dead on mobile** → [frontend notes](development/frontend-notes.md): the native HTML5 DnD API is touch-blind.
- **Light mode is ignored in a phone browser** → [frontend notes](development/frontend-notes.md#darklight-mode--mobile-browsers-force-darken-a-light-only-page): mobile auto-dark force-darkens a light-only page (`color-scheme: light` → `light dark`).
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

## Diagnosing a 500 / failed request in production

When the deployed frontend can't talk to the API, it's almost always one of three culprits — check them in this order before touching code:

- **The database is waking up (transient 500).** Azure SQL **serverless auto-pauses when idle**, and the first request after a pause cold-starts the database; if the app tries to use it before it's up, the request fails (errors **-2 / 40613**) and surfaces as a 500. This is expected on the first hit after a quiet period. The app is hardened against it — EF `EnableRetryOnFailure` (retrying error -2), a longer `Connect Timeout`, and non-blocking startup/seed — so **retry once or twice** and it should clear. A 500 that *persists* across retries is a real error (check Log Stream / Kudu), not a cold start.
- **CORS is misconfigured (browser blocks the response).** The API must allow the site's exact origin via `Cors__AllowedOrigins__0` — the **exact URL, no trailing slash**. If it's missing or wrong, the browser blocks the call and the console shows a CORS error even though the API itself is healthy (calling the API directly still works). Fix the app setting, not the frontend.
- **`VITE_API_URL` points at the wrong URL.** It must be the **API** URL (the one that shows Swagger) and **must include `https://`**. Without the scheme the browser treats it as a relative path and the call resolves to the SPA's own host → **405** (or a 404). It is *not* the site's own URL. Because `VITE_*` vars are baked in at build time, changing this requires a **rebuild/redeploy**, not just a settings toggle.

Quick triage: open the browser dev-tools Network tab. A blocked-by-CORS entry points to culprit #2; a request going to the SPA's own host (or a 405) points to #3; a 500 that succeeds on retry is #1.

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

## Frontend & UI engineering

The app-side engineering lessons — optimistic UI and the reload-order bug, the touch-blind
HTML5 drag-and-drop and the tap-to-move fix, and the masked `DateField` that restores
cross-segment editing — now live in **[frontend notes](development/frontend-notes.md)**.
## The real find — concurrent refresh signed users out everywhere

This was the subtle one, and worth its own note because it sits at the seam between the backend's security hardening and the frontend's concurrency.

**Symptom:** users occasionally got signed out of *every* device for no obvious reason, usually right after the access token expired.

**Root cause:** the access token lives 15 minutes, so when it expires the board often fires **several API calls at once** (todos, categories, etc.). Each call 401s, and each independently tried to refresh — POSTing the **same** refresh token. But the backend **rotates** refresh tokens on every refresh and, by design, treats a second use of an already-rotated token as **reuse / possible theft**: it rotates the user's security stamp and **revokes every outstanding session**. So the app's own concurrency was tripping the backend's compromise-response and logging the user out everywhere. The backend was behaving *correctly* — the bug was the client hammering refresh in parallel.

**Fix (client side):** de-duplicate refresh into a **single in-flight promise**. The first 401 starts the refresh; every other caller awaits the *same* promise instead of starting its own, so exactly one rotation happens:

```js
let refreshInFlight = null;
function refreshSession() {
  if (!refreshInFlight) {
    refreshInFlight = performRefresh().finally(() => { refreshInFlight = null; });
  }
  return refreshInFlight; // all concurrent callers share this one refresh
}
```

**Lesson:** when the server implements refresh-token rotation with reuse detection (a good, standard security pattern), the client **must** serialize refreshes. Rotation + reuse-detection + parallel refresh = accidental self-inflicted "sign out everywhere." This also argues for keeping the refresh token in an httpOnly cookie and letting a single interceptor own the refresh, rather than every request racing to do it.

## Dev-environment & tooling gotchas

High-level notes on the environment snags hit while doing this work (and how each was resolved), so they don't cost time again:

- **The remote-file bridge can't delete — only move.** A `git status` run through the device bridge left a stale `.git/index.lock`, which then blocks the next git command ("unable to unlink index.lock"). The bridge tooling can't `rm`, so the fix is to **`mv` the lock aside** (e.g. `mv .git/index.lock .git/index.lock.stale`); git then proceeds. Same rule applies to any file the bridge needs to "remove" — move it, don't delete it.
- **`vite build` won't run in the device's Linux VM** — the Windows `node_modules` has the wrong native rollup binary (`MODULE_NOT_FOUND` on `rollup/dist/native.js`). For a quick structural sanity check without a full bundler, parse the changed modules with **`@babel/parser`** plus a small import/export resolver; for the real thing, run `npm run build` / `npm test` **locally on Windows**.
- **The cloud sandbox's npm registry is blocked (403).** `npm install` / `npm ci` can't run there, so the frontend deps and the test suite must be installed and run **locally**. Remember to commit the updated **`package-lock.json`** afterward, or CI's `npm ci` fails on a lockfile mismatch.
- **When syncing text files byte-for-byte, verify with a checksum.** A base64 hand-off once flipped a single character (an em dash became an arrow); an **`md5sum` compare** after each sync catches it immediately, and re-copying fixes it. Cheap insurance for any file moved between environments.
