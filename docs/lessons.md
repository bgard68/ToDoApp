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
- **First request after idle is slow / "Failed to fetch"** → [Cold starts on the free tier](deployment/cold-starts.md): the app *and* DB wake from sleep; the client retries `502/503/504` + network errors, and a keep-warm ping keeps the app loaded — from an external monitor, because GitHub's cron silently skips runs (a five-hour gap was observed).
- **`<name>.azurewebsites.net` won't resolve** → [Networking / hostnames](#networking--hostnames): use the Overview page's regional Default domain.
- **Drag-and-drop is dead on mobile** → [frontend notes](development/frontend-notes.md): the native HTML5 DnD API is touch-blind.
- **Light mode is ignored in a phone browser** → [frontend notes](development/frontend-notes.md#darklight-mode--mobile-browsers-force-darken-a-light-only-page): mobile auto-dark force-darkens a light page; opt out with `color-scheme: only light` (the `only` keyword — `light dark` does not opt out).
- **CodeQL flags a log line as "log forging"** → [Security scanning](#security-scanning-codeql-gitleaks--dependabot): user input (e.g. `Request.Path`) logged even via structured logging; strip `\r`/`\n` before logging.
- **Every Dependabot PR is red and none of them caused it** → [Security scanning](#security-scanning-codeql-gitleaks--dependabot): a required check failing on `main` blocks every open PR behind it; fix `main` first, then re-run.
- **A red CI gate names no CVE, just `exit code 1`** → [Security scanning](#security-scanning-codeql-gitleaks--dependabot): Trivy writing SARIF prints nothing readable, and failing the step skips the upload.
- **`severity: CRITICAL,HIGH` on a Trivy step does nothing** → [Security scanning](#security-scanning-codeql-gitleaks--dependabot): `trivy-action` ignores `severity` when `format: sarif`, so the gate blocks on every severity.
- **A NuGet bump fails with `NU1004` in CI but the PR looks fine** → [Security scanning](#security-scanning-codeql-gitleaks--dependabot): one bump changes several `packages.lock.json` files; Dependabot regenerates only one.
- **`@dependabot rebase` says "already up-to-date" but the branch is behind** → [Security scanning](#security-scanning-codeql-gitleaks--dependabot): that means "my change still applies", not "contains your base commits" — use `@dependabot recreate`.

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

> **Now handled gracefully client-side:** the app retries these transient wake-ups (network errors, 502/503/504) and shows "Waking the server up…" instead of failing outright — see [Cold starts on the free tier](deployment/cold-starts.md).

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

## Security scanning (CodeQL, gitleaks & Dependabot)

- **Log forging (CWE-117 / `cs/log-forging`).** CodeQL flags logging a user-controlled value — here
  `httpContext.Request.Path` in `GlobalExceptionHandler.cs` — *even with structured logging* (`{Path}` as a
  parameter). Structured logging avoids string concatenation, but the default console/file formatters still
  render the parameter into the text line without stripping control chars, so a percent-encoded `%0A` in the
  path could inject a fake log line. Fix: strip `\r`/`\n` before logging
  (`.Replace("\r", string.Empty).Replace("\n", string.Empty)`) — the recognized sanitizer clears the alert.
- **CodeQL default setup** (Settings → Advanced Security → Code scanning) is one click, free on public repos,
  supports C#, and re-scans on every push — no workflow file to maintain. It found the log-forging issue above.
- The rest of the security posture — `.gitignore` hardening, gitleaks (pre-commit + CI), Dependabot, and the
  GitHub-side protections (secret scanning, push protection, the `protect-main` ruleset) — lives in
  **[Secret hygiene](deployment/secret-hygiene.md)**.

### A gate that works but can't report reads as noise (August 2026)

Twenty-two Dependabot PRs sat blocked for two weeks. The instinct was that CI had become noisy and the
old runs were clutter worth deleting. Deleting them would have destroyed the evidence and left two
live HIGH vulnerabilities in place. Both gates were working correctly the entire time; neither could
say what it had found.

- **One red required check on `main` blocks every open PR.** `Build API image & scan` went red on a
  scheduled scan, and because it is required, all 22 PRs queued behind a failure none of them caused
  and none could clear. Check `main`'s own status before investigating any individual PR.
- **A failing Trivy step skips `upload-sarif`.** SARIF is machine-readable, so a red run printed
  `exit code 1` and named no CVE — and because the step failed, the findings never reached the
  Security tab. The tab received results *only* on runs with nothing to report. Fix: scan with
  `continue-on-error`, upload under `always()`, raise the failure in a later step, and add a
  `format: table` pass so the log names the CVE and its fixed version.
- **`trivy-action` ignores `severity` when `format: sarif`.** It logs `Building SARIF report with all
  severities` and scans everything, so a gate built on that step blocks on LOW and MEDIUM while the
  configuration claims CRITICAL,HIGH. Gate on the table scan, where the input is honoured, and keep
  SARIF as a separate non-blocking reporter.
- **Digest-pinned base images do not receive patches.** Pinning buys reproducibility and costs you
  automatic security updates. `aspnet:10.0` carried **CVE-2026-62901** (HIGH, .NET DoS) in runtime
  10.0.10 until the pins were refreshed to pick up 10.0.11.
- **Re-running a workflow replays the old commit.** It does not pick up a fixed base branch — the
  re-run used the same stale digests and the same old workflow file. Only a new head commit helps.

### Dependabot cannot express "these must land together" (August 2026)

Two failure modes where the PR is born red and no amount of rebasing helps, because nothing about the
base branch is wrong:

- **Lock files.** Bumping one package changes every `packages.lock.json` that resolves it
  transitively — five of them here for a single `Microsoft.EntityFrameworkCore` bump. Dependabot
  regenerates only the one belonging to the project it edited, so `dotnet restore --locked-mode`
  fails with `NU1004`. Fix by hand: bump the family together, run
  `dotnet restore TodoApp.sln --force-evaluate`, and commit every regenerated lock file.
- **`codeql-action` sub-actions.** `init`, `analyze` and `upload-sarif` must run the same version, or
  the job fails with `Loaded a configuration file for version X, but running version Y`. Dependabot
  opens one PR per sub-action, so each one *creates* that mismatch. They have to move in one commit.

Both are avoidable with `groups` in `dependabot.yml`, which makes Dependabot open one PR per group.

- **`@dependabot rebase` can be a silent no-op.** It replies *"already up-to-date with <branch>"* when
  its change still applies cleanly — which is not the same as containing your base commits. Verify
  with `git merge-base --is-ancestor <fix-commit> <pr-head>` rather than taking the reply at face
  value; use `@dependabot recreate` to actually rebuild from the current base.
- **Check results belong to a commit, not a branch.** Merging a fix into the base does not turn a
  PR's stale red X green, and the PR can keep showing a check from days earlier. That stale check is
  also how a PR can look one green tick from mergeable while its branch would *revert* a security fix.

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

## The untestable line — IPv6 callers were exempt from rate limiting

**Symptom:** none. Nothing failed, nothing was logged, and the test asserting that auth endpoints return **429** passed the whole time.

**Root cause:** the partition key came from a local function inside `Program.cs` that stripped the `:port` App Service appends by hand:

```csharp
var colon = last.LastIndexOf(':');
if (colon > 0 && last.IndexOf(':') == colon) { last = last[..colon]; }  // only one colon → IPv4
return last.Trim('[', ']');
```

The guard is deliberately conservative — it only slices when there is exactly one colon, so a bare IPv6 address is left alone. But `[2001:db8::1]:51514` has many colons, so the port is never removed, and `Trim('[', ']')` only strips the *leading* bracket because the string ends in a digit. The key became `2001:db8::1]:51514`.

Source ports are ephemeral, so every connection from that client produced a **different partition**. An IPv6 caller was never rate limited at all — on the anonymous login endpoint, where each attempt costs 100k PBKDF2 iterations.

**Why it survived review and CI:** the logic was a closure in `Program.cs`, so nothing could reach it. The one rate-limiting test drives the limiter through `HttpClient` over IPv4 and asserts a 429 arrives — true, and true for the wrong protocol. Coverage was green over a line no test could exercise.

**Fix:** move the rules into `ClientAddress`, where tests can reach them, and parse instead of slice — `IPAddress.TryParse` first (so a bare IPv6 address is not mistaken for `host:port`), then `IPEndPoint.TryParse` (which handles both `1.2.3.4:80` and `[::1]:80`). Anything that parses as neither is discarded rather than used as an identity. Fourteen tests now cover the cases the closure never could.

**The second finding, from the same read:** `RateLimiting:TrustForwardedFor` shipped `false` and **neither provisioning script set it**. App Service *is* a reverse proxy, so in Azure every request arrived carrying the platform's address and all callers shared one partition — the entire app throttled at 200 requests a minute and 10 sign-ins a minute, collectively. Both `provision.sh` and `Provision.ps1` now set it, which is safe precisely because only the last hop is read.

**Lesson:** the two bugs are opposites — one exempted a caller from the limiter, the other applied it to everybody at once — and both were invisible. A rate limiter has no natural failure signal: working, wide open, and capping the whole world all look identical from the outside. That makes the partition key one of the few pieces of code where *reachable by a test* is a security property, not a style preference. A local function is not free.

## Dev-environment & tooling gotchas

High-level notes on the environment snags hit while doing this work (and how each was resolved), so they don't cost time again:

- **The remote-file bridge can't delete — only move.** A `git status` run through the device bridge left a stale `.git/index.lock`, which then blocks the next git command ("unable to unlink index.lock"). The bridge tooling can't `rm`, so the fix is to **`mv` the lock aside** (e.g. `mv .git/index.lock .git/index.lock.stale`); git then proceeds. Same rule applies to any file the bridge needs to "remove" — move it, don't delete it.
- **`vite build` won't run in the device's Linux VM** — the Windows `node_modules` has the wrong native rollup binary (`MODULE_NOT_FOUND` on `rollup/dist/native.js`). For a quick structural sanity check without a full bundler, parse the changed modules with **`@babel/parser`** plus a small import/export resolver; for the real thing, run `npm run build` / `npm test` **locally on Windows**.
- **The cloud sandbox's npm registry is blocked (403).** `npm install` / `npm ci` can't run there, so the frontend deps and the test suite must be installed and run **locally**. Remember to commit the updated **`package-lock.json`** afterward, or CI's `npm ci` fails on a lockfile mismatch.
- **When syncing text files byte-for-byte, verify with a checksum.** A base64 hand-off once flipped a single character (an em dash became an arrow); an **`md5sum` compare** after each sync catches it immediately, and re-copying fixes it. Cheap insurance for any file moved between environments.
