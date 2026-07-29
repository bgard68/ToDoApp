# Security remediation — DevSecOps review, 2026-07-28

> **This is the `dapper` branch.** Everything below was applied here independently of `main` —
> the branches are maintained in parallel and `dapper` is not merged into `main`.
>
> The Azure resources are shared (both branches deploy to `taskboard-06-api`), so the H1 demo
> settings and the M2/M3/L6 data-tier fixes hold whichever branch is deployed there.

Findings from the Senior DevSecOps review of `main`, `dapper` and `frontend`, what was changed,
and what now stops each one coming back.

Findings are referenced by their review ID (H = high, M = medium, L = low) throughout the code, so
`grep -rn "finding H3"` lands on the control rather than on this page.

**Branch policy:** `main` (EF Core) and `dapper` (Dapper) are maintained in parallel — `dapper`
is *not* merged into `main`. Every application-layer fix below was therefore applied to both
branches independently, and the shared regression tests run on both.

---

## HIGH

### H1 — Demo account with a published password was seeded in every environment

**Was:** `DbInitializer` held `DemoEmail`/`DemoPassword = "Password123!"` as `const` fields and
`Program.cs` called it unconditionally at startup. Any fresh database — including production —
came up with a working account whose credentials are in a public repository.

**Fix**
- New `DemoSeedOptions` (`Seed` config section) with `DemoUser`, `Email`, `Password`.
- `DbInitializer.InitializeAsync` takes the options and returns immediately unless
  `DemoUser: true`. Seeding is now opt-in.
- The password comes from configuration (env var / Key Vault). The constants are gone from the
  assembly.
- **Fails closed:** seeding enabled with no configured password generates a random 32-byte one, so
  the account exists but nobody can sign in — rather than falling back to a known value.
- `appsettings.json` ships `Seed:DemoUser=false`; `appsettings.Development.json` turns it on for
  local work. Deployed environments must set `Seed__DemoUser` and `Seed__Password` explicitly.

**Tests** — `tests/TodoApp.UnitTests/Security/DemoSeedTests.cs` (5) and
`tests/TodoApp.IntegrationTests/SecurityHardeningTests.cs` (2):

| Test | Guards |
|---|---|
| `Does_not_seed_a_demo_user_by_default` | no options supplied → no users at all |
| `Does_not_seed_when_options_are_supplied_but_disabled` | explicit `false` is honoured |
| `Seeds_the_demo_user_when_explicitly_enabled` | the opt-in path still works |
| `Enabled_seed_without_a_configured_password_is_not_signable_in` | **the old `Password123!` no longer authenticates** |
| `Is_idempotent_when_users_already_exist` | re-running startup doesn't duplicate |
| `Demo_account_is_not_seeded_by_default` (integration) | real host, real pipeline |
| `The_old_hard_coded_demo_credentials_do_not_authenticate` (integration) | end-to-end 401 |

> **Deployment decision (owner, 2026-07-28): the live demo account stays public.**
> `demo@todoapp.local` / `Password123!` is a deliberate, shared sign-in so reviewers can try the
> app without registering. The finding was never "a demo account exists" — it was that the account
> was seeded **unconditionally, in every environment, from a constant compiled into the assembly**.
> That is fixed: seeding is opt-in, the credentials come from configuration, and any environment
> that doesn't ask for it gets no account at all.
>
> These settings are applied on `taskboard-06-api` (verified live):
>
> | Setting | Value |
> |---|---|
> | `Seed__DemoUser` | `true` |
> | `Seed__Email` | `demo@todoapp.local` |
> | `Seed__Password` | `Password123!` |
>
> **No data migration was needed.** The seed only runs against an empty database
> (`if (await context.Users.AnyAsync()) return;`), and the existing row — user id 1, role `User` —
> already holds exactly these credentials. Config and reality agree, so the reconciliation code and
> the manual SQL rotation that were considered are both unnecessary. Confirmed against the live API
> before and after the settings change: `POST /api/auth/login` returns 200.
>
> **Residual risk, accepted:** anyone can sign in to that one non-admin demo board, and it is an
> authenticated foothold for probing the API. The H3 rate limiter bounds that. If the account is
> ever abused, set `Seed__DemoUser=false` and delete the row — no redeploy required.

---

### H2 — No CSP or security headers, refresh token in `localStorage`

**Was:** neither the SWA config nor `nginx.conf` set a single security header, while the SPA keeps
a 7-day refresh token in `localStorage`. Any XSS meant silent, renewing account takeover.

**Fix (API, this branch):** `SecurityHeadersMiddleware` adds `X-Content-Type-Options`,
`X-Frame-Options`, `Referrer-Policy`, `Permissions-Policy` and a CSP to **every** response,
including error responses. API routes get `default-src 'none'`; the Swagger UI route gets a
policy that still forbids remote origins and framing but lets its own bundle run. `UseHsts()` and
`UseHttpsRedirection()` are enabled outside Development, so the app no longer depends solely on the
App Service `httpsOnly` flag.

**Fix (SPA):** see the `frontend` branch — headers added to `staticwebapp.config.json` and
`nginx.conf`, with the CSP allowing `https://accounts.google.com` for Google Identity Services.

**Tests** — `SecurityHardeningTests`:
`Responses_carry_baseline_security_headers` (theory, 3 headers),
`Api_responses_carry_a_locked_down_content_security_policy`,
`Security_headers_are_present_on_error_responses_too`.

**Now fixed (2026-07-29).** The deferral is closed. The refresh token is delivered as an
`httpOnly; Secure; SameSite=None` cookie scoped to `/api/auth`, so no script can read it — see the
`frontend` branch's `SECURITY-REMEDIATION.md` for the client half and the caveat about browsers
that block third-party cookies.

---

### H3 — No rate limiting on auth endpoints; unbounded password length

**Was:** `/api/auth/*` was anonymous and unthrottled, and neither validator capped password
length, so one request could drive 100k PBKDF2 iterations over a multi-megabyte string on a Free
(F1) instance.

**Fix**
- `AddRateLimiter` with a named `auth` policy (default **10 requests / 60s per client**) applied to
  the whole `/api/auth` group, plus a global backstop limiter (200/60s). Rejections return **429**
  with `Retry-After` and an RFC 7807 body.
- Limits are configurable under `RateLimiting`, so a deployment can tune them and tests can drive
  them.
- Client partitioning: `RemoteIpAddress` by default. Behind a reverse proxy every request looks
  like it comes from the proxy, which would throttle all users as one — so
  `RateLimiting:TrustForwardedFor` opts into using the **last** `X-Forwarded-For` hop (the entry
  Azure App Service appends). Off by default, because trusting a client-supplied header where no
  such proxy exists lets an attacker forge their way around the limit.
- `PasswordPolicy` (`MinLength 8`, `MaxLength 128`, `MaxEmailLength 256`) is now shared by the
  login and register validators so they cannot drift.

**Tests**
- `tests/TodoApp.UnitTests/Security/PasswordPolicyTests.cs` (5) — over-length rejected on both
  paths, a 4 MB password rejected, boundary length accepted, minimum/composition still enforced.
- `SecurityHardeningTests.Auth_endpoints_return_429_once_the_window_is_exhausted` — a host with a
  5-request window; asserts requests inside the window succeed and the limit then trips.
- `SecurityHardeningTests.A_throttled_response_tells_the_client_when_to_retry` — `Retry-After`.
- `SecurityHardeningTests.An_oversized_password_is_rejected_as_a_validation_error` — 400, not a
  hashing stall.

---

## MEDIUM

### M1 — Log-forging fix existed on `main` but not on `dapper`

**Was:** `main` stripped CR/LF from `Request.Path` before logging (CodeQL `cs/log-forging`);
`dapper` logged it raw — a fix that had already been made, reviewed and written up, missing on the
other branch.

> **Correction (2026-07-29).** This finding was originally justified by claiming
> `workflow_dispatch` could ship `dapper` to the same App Service. That was wrong: the deploy
> history shows **every** deploy is `main`/push and `dapper` has never been deployed. The
> capability existed in the workflow file, so it was a latent footgun rather than an active
> exposure — and it has now been removed, so `dapper` cannot deploy at all.
>
> The fix still stands on its own: a security fix present on one branch and absent on its twin is
> a defect regardless of what ships. But its severity was **overstated**, and the M1 rating should
> be read as code-consistency, not production exposure.

**Fix:** the sanitisation moved into `TodoApp.Application.Common.Logging.LogSanitizer`, which
strips **all** control characters (not just CR/LF — ESC enables terminal escape injection too).
Both branches call the same helper. Fixing it by extraction rather than by cherry-pick is the
point: there is now one implementation and one test, so the branches cannot silently diverge here
again.

**Tests** — `tests/TodoApp.UnitTests/Security/LogSanitizerTests.cs` (6, running on both branches):
CRLF injection stripped, ESC/BEL/NUL stripped, ordinary and non-ASCII values untouched.

**Also:** `codeql.yml` now runs on `main` *and* `dapper`, so the original detection covers both.

---

### M2 / M3 / M4 — SQL identity, firewall, and password handling

**M4 fixed.** `provision.sh` passed the SQL admin password as an `az` argv element, readable via
`/proc/<pid>/cmdline` and logged by the CLI. It now goes in on stdin (`--admin-password @-`), and
Key Vault writes use `--file` with a mode-600 temp file removed via an `EXIT` trap. The PowerShell
twin already used `SecureString`/`PSCredential`.

> **SUPERSEDED — see the M2/M3 addendum at the end of this document (2026-07-29).** Both are
> now fixed in the live environment and in the provision scripts, and verifying the live
> environment showed the M2 description below was wrong about production. The paragraph that
> follows is kept as the original finding, not as current state.

**M2 and M3 were NOT fixed in code at the time of the original review** — they are changes to a live Azure environment, not to the
repository, and applying them blind would risk breaking the running deployment. Recorded here as
accepted-and-open:

- **M2:** the app connects as `sqladmin` (server administrator). Any leak or injection yields the
  whole server, not just the app's tables. Fix: a contained user with `db_datareader` +
  `db_datawriter`, or Entra managed identity with `Authentication=Active Directory Default`.
- **M3:** the `AllowAzureServices` firewall rule (`0.0.0.0`) admits resources from *any* Azure
  tenant. Fix: Private Endpoint, or restrict to the App Service outbound IPs.

Both need an Azure change window and a connection-string rotation. They are the top of the
infrastructure backlog.

---

### M5 — Lock files were generated but never enforced, over floating versions

**Was:** every `PackageReference` floated (`Google.Apis.Auth 1.*`, `MediatR 12.*`,
`Microsoft.EntityFrameworkCore 10.0.*`), `RestoreLockedMode` was commented out, and CI ran
`dotnet restore --use-lock-file` — which *writes* the lock file rather than *enforcing* it. The
lock files looked like pinning without being it.

**Fix**
- All 18 direct package references pinned to the exact versions that were already resolved
  (no version changes, so no behavioural risk).
- `Directory.Build.props` **moved to the repository root** so it covers `tests/` as well as `src/`
  — previously the test projects were outside its scope entirely.
- `RestoreLockedMode` enabled when `ContinuousIntegrationBuild=true`; the CI workflow sets that and
  restores with `--locked-mode`, so a drifting resolution **fails the build** instead of rewriting
  the lock file.
- `Dockerfile.api` copies the lock files and restores with `--locked-mode` too, so the image can't
  be built from unreviewed packages either.
- `EnableNETAnalyzers` + `AnalysisLevel latest` now apply repo-wide.

**Verification:** `dotnet restore TodoApp.sln --locked-mode` passes; changing any pinned version
without refreshing the lock files fails with `NU1004` (confirmed during this work).

---

### M6 — No SAST, dependency, or container scanning; npm scanned by nothing

**Was:** the pipeline ran build, unit tests and gitleaks. CodeQL existed only as GitHub UI
"default setup" (invisible to review, unversioned, non-portable). No dependency-vulnerability
gate. `dependabot.yml` declared only `github-actions` and `nuget`, and only for the default
branch — so the React/Vite tree on `frontend` was monitored by nothing at all.

**Fix**
- **`.github/workflows/codeql.yml`** — CodeQL as versioned configuration, `security-and-quality`
  query pack, on `main` and `dapper`, plus a weekly schedule to catch newly published queries
  against unchanged code.
- **Dependency audit gate** in `api-ci-cd.yml`: `dotnet list package --vulnerable
  --include-transitive`, failing the build on any Critical/High/Moderate advisory.
- **`dependabot.yml`** extended with `target-branch` entries: nuget + actions for `dapper`, and
  **npm + actions for `frontend`**.

> If CodeQL "default setup" is enabled in Settings → Code security, disable it before merging —
> GitHub rejects an advanced-setup workflow while default setup is active. This is called out at
> the top of `codeql.yml`.

**Still open:** container image scanning (Trivy/Grype) is not wired up — there is no image
registry in this deployment (App Service takes a zip), so it would scan an artefact nothing runs.
Worth adding if the Docker path ever becomes the deployment path.

---

### M7 — Fake Google token validator compiled into the production assembly

Applies to `dapper` only. See that branch's entry in this file.

---

### M8 — Frontend deploy workflow missed the API workflows' hardening

Applies to `frontend`. See that branch's entry in this file.

---

### M9 — Daily run cleanup destroyed the CI/CD audit trail

**Was:** `cleanup-runs.yml` kept one run per workflow and deleted the rest, daily — taking
build/deploy and secret-scan history with it. After an incident there was nothing to reconstruct
what shipped, when, and whether it passed.

**Fix:** the job is now scoped to an explicit `PRUNABLE` allowlist (`keep-warm.yml`,
`cleanup-runs.yml`) resolved by workflow file path. Build, deploy, CodeQL and secret-scan history
is retained. Only the 10-minute heartbeat noise — the actual motivation — is pruned.

The workflow's existing good practice was kept: the `keep` input is routed through `env:` rather
than interpolated into the shell, so it can't be used for script injection.

---

### M10 — Containers ran as root

**Fix (`Dockerfile.api`):** `USER app` (the non-privileged user the .NET base images ship, uid
1654). Restore switched to `--locked-mode`, publish to `--no-restore`.

**Deliberately no `HEALTHCHECK`:** the `aspnet` runtime image ships neither `curl` nor `wget`, and
a check that shells out to `dotnet --info` would report "healthy" for a hung app — worse than no
check. Liveness is probed externally against `GET /`, which returns a static payload without
touching the database. This is documented in the Dockerfile itself.

**Verified in CI, not locally.** The Docker engine wasn't running on the machine this work was
done on. Rather than leave the image unproven, `.github/workflows/container-build.yml` now builds
and checks it on every relevant change — which turned out better than a local build would have
been:

- builds the image, which also exercises the M5 locked-mode restore (a lock-file/project mismatch
  fails the build),
- **fails if `Config.User` is empty, `root` or `0`**, so the non-root fix can't silently regress,
- scans with Trivy and uploads SARIF to the Security tab — closing the container-scanning gap
  listed as still-open under M6.

Trivy reports rather than blocks: base-image CVEs appear and are fixed on Microsoft's schedule,
not ours, so failing the build would just train everyone to ignore a red X.

**Still open:** base images are pinned by tag (`mcr.microsoft.com/dotnet/aspnet:10.0`), not by
digest. Digest pinning needs a registry round-trip to resolve and a process to refresh them;
worth doing, not done here.

---

### M11 — Actions pinned to mutable tags

**Fix:** every action in every workflow is pinned to a full commit SHA with the version in a
trailing comment, resolved from the GitHub API at the time of this change:

```
actions/checkout@11d5960a326750d5838078e36cf38b85af677262 # v4
actions/setup-dotnet@67a3573c9a986a3f9c594539f4ab511d57bb3ce9 # v4
actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02 # v4
actions/download-artifact@d3f86a106a0bac45b974a628896c90dbdf5c8093 # v4
azure/login@a457da9ea143d694b1b9c7c869ebb04ebe844ef5 # v2
azure/webapps-deploy@02a81bead70021f5284939794bcec79c271ab383 # v3
gitleaks/gitleaks-action@ff98106e4c7b2bc287b24eaf42907196329070c7 # v2
github/codeql-action@4187e74d05793876e9989daffde9c3e66b4acd07 # v3
```

Dependabot understands SHA pins and will keep raising bumps.

**Also in `api-ci-cd.yml`:** deploy split into its own job gated on `environment: production`, so
a manual `workflow_dispatch` deploy of a non-`main` branch can be held for approval via the
environment's protection rules instead of shipping straight from whatever branch was picked.
*(The environment's reviewers still need configuring in repo Settings — the workflow provides the
hook, not the policy.)*

---

## LOW

| ID | Fix | Guard |
|---|---|---|
| **L1** | Integration tests now run in CI — they were built but never executed, so the auth flows were unverified on every deploy. | `api-ci-cd.yml` step "Run integration tests" |
| **L2** | Provisioning runtime `DOTNETCORE 8.0` → `10.0`, matching `net10.0`. A clean provision previously produced an App Service that couldn't run the app. | both `provision.sh` and `Provision.ps1` |
| **L3** | `docker-compose.yml` referenced `./frontend`, which moved to its own branch — the file could not build. Now takes `FRONTEND_PATH` with a `git worktree` recipe in the comment, and fails with a clear message if unset. | `${FRONTEND_PATH:?...}` |
| **L4** | Login no longer skips password verification for unknown emails — it verifies against a decoy hash and discards the result, so a miss and a hit take the same time. The decoy is derived from the live hasher, so it tracks any iteration-count change. | `LoginCommandHandler.DummyHash` |
| **L5** | `UseHsts()` + `UseHttpsRedirection()` outside Development. | part of the H2 pipeline change |
| **L7** | An expired refresh token is now marked revoked on presentation, so it can't sit active forever without ever tripping reuse detection. | `RefreshTokenCommandHandler` |
| **L12** | Removed the CI step that dumped the repo tree on every run. | `api-ci-cd.yml` |
| **L13** | `GET /` returned a 302 to Swagger in production, where Swagger isn't mapped — a redirect into a 404. Now a static `{"status":"ok"}` outside Development, which also gives the keep-warm ping something real to hit. | `Program.cs` |

### Low findings — ALL NOW CLOSED (2026-07-29)

This section previously listed these as deliberately left open. Every one has since been fixed;
the reasoning is kept because the trade-offs were real, not because the items are still pending.

- **L6** — Key Vault: RBAC and soft-delete turned out to be already live (the finding was read off
  `provision.sh`, same mistake as M2). Purge protection was genuinely missing and is now enabled.
- **L8** — PBKDF2 raised to OWASP's 600,000. The concern was CPU cost on the Free tier; solved by
  storing the count in the hash and upgrading each account in place on next login, so nobody is
  locked out and nothing is re-hashed in bulk. Argon2id remains the better primitive but needs a
  third-party package.
- **L9** — breached-password rejection via the free HIBP k-anonymity API. The concern was an
  outbound dependency on the registration path; solved by failing **open** on any error, so the
  lookup being down never blocks a signup.
- **L10** — `AllowedHosts` set to the real hostname as an App Service setting. The committed
  default stays `"*"` because hard-coding one host breaks local dev, CI and every other
  environment — the value belongs to the deployment, not the repository.
- **L11** — `TreatWarningsAsErrors` on, after clearing the warning surface to zero first so it
  landed as a guarantee rather than a wall of unrelated failures.

All are covered by the posture check (`scripts/check-azure-posture.sh`, 16 assertions) or by unit
tests. See the H2/L8/L9 entries above and the addendum for detail.
---

## Verification

```
dotnet restore TodoApp.sln --locked-mode      # lock files authoritative
dotnet build   TodoApp.sln -c Release         # 0 warnings, 0 errors
dotnet test    TodoApp.sln -c Release
```

**80 tests pass** on `main` (53 unit + 27 integration), up from 53 (36 + 17).
**27 new tests**, all of which fail against the pre-fix code.

---

## Bugs and surprises hit during this work

Recorded because several of them are traps the next person will hit too.

### 1. `ConfigureAppConfiguration` did not override `appsettings.Development.json` in the test host

The integration-test factory first set rate limits with
`builder.ConfigureAppConfiguration(c => c.AddInMemoryCollection(...))`. It had no effect — the
limiter kept using the Development value, so the "returns 429" test saw eight `401`s instead of
tripping the limit. Switching to `builder.UseSetting(key, value)` fixed it.

Worth knowing before debugging a `WebApplicationFactory` override that appears to be ignored under
minimal hosting. `CustomWebApplicationFactory.TestConfiguration` now carries a comment saying so.

### 2. `--` is illegal inside an XML comment

`Directory.Build.props` failed to load with `MSB4024` after a comment mentioned `--locked-mode`,
and again after it mentioned `--force-evaluate`. XML forbids `--` inside comments entirely. The
comments now spell the flags out in words. The failure is confusing because MSBuild reports it
once per project, so a one-character mistake produces a wall of identical errors.

### 3. Pinning package versions invalidated the lock files

Changing `MediatR 12.*` to `12.5.0` changes the *requested range*, so `--locked-mode` restore
failed with `NU1004` even though the *resolved* version was identical. One
`dotnet restore --force-evaluate` regenerates them.

This is exactly the behaviour that was wanted — it is the enforcement working — but it explains why
the diff shows lock-file churn with no actual version changes. Any future version bump needs the
same regeneration step; `Directory.Build.props` documents it.

### 4. An existing integration test depended on the demo seed

`Login_WithSeededDemoUser_Succeeds` signed in as `demo@todoapp.local` / `Password123!`. The first
grep for the seed's usages missed it, because the test hard-codes the strings rather than
referencing `DbInitializer.DemoEmail`. It now stands up a host with seeding explicitly enabled and
supplies its own password — which has the side benefit of giving the H1 opt-in path direct
coverage.

**Lesson for the deployment:** if anything else in the estate signs in as the demo user, it will
break the same way. Grep for the literal, not just the constant.

### 5. A name collision on `dapper` (`CS0841`)

`DbInitializer` there already had a local `var seed = new[] {...}` (the sample todos). Adding a
`seed` options parameter produced "cannot use local variable before it is declared". The local was
renamed to `items`.

### 6. A `HEALTHCHECK` that would have lied

The first draft of `Dockerfile.api` used `HEALTHCHECK CMD dotnet --info`. That always exits 0, so
it would have reported a hung app as healthy — actively worse than no check, because orchestrators
trust it. The .NET runtime image ships neither `curl` nor `wget`, so the Dockerfile now has **no**
HEALTHCHECK, with a comment explaining why and pointing at the external probe instead.

### 7. Pre-existing `IDX00001` warning on `dapper`, surfaced by pinning

`System.IdentityModel.Tokens.Jwt` had floated to 8.20.0 while the `Microsoft.IdentityModel.*`
packages sat at a different version; that package warns the mismatch can cause `TypeLoadException`
at runtime. It predates this work — it was in the baseline build output — but floating versions are
why it drifted. Pinned to **8.19.2**, matching `main`. The warning is gone and both branches now
resolve the same identity stack.

### 8. On the `frontend` branch, the CSP hash was wrong because of line endings

Caught by the very test written to prevent future drift. A browser hashes the exact bytes it
receives; the deployed artefact is built on Linux (LF), but a container built from a Windows
working copy would have served CRLF — a script its own CSP rejects, failing silently. Fixed with
LF normalisation in the test plus a `.gitattributes` rule. Detail in that branch's
`SECURITY-REMEDIATION.md`.

### 9. Docker engine unavailable — and a readiness check that lied

`docker --version` reported 27.2.0, but the Desktop engine was never running: the backing Windows
service (`com.docker.service`) was `Stopped`/`Manual`, and a `nohup` launch of Docker Desktop
silently did nothing.

Worse, the wait-for-ready loop written to poll for it **printed "engine ready" while the engine was
still down** — `docker info --format` exited zero with empty output on one iteration. A check that
can report success without the thing being healthy is the same mistake as the `dotnet --info`
HEALTHCHECK in item 6, made twice in one session. Both are now gone.

Resolved by not depending on a local engine: the image is built and checked in CI, where the
assertion is a real `docker image inspect`.

### 10. The frontend CSP named the wrong API origin

On the `frontend` branch, `connect-src` was written as `https://taskboard-06-api.azurewebsites.net`
— inferred from the App Service *name* in the deploy workflow. The real origin, the `VITE_API_URL`
repository Variable the bundle is built against, is
`https://taskboard-06-api-aehtbcg8eha6fyf8.centralus-01.azurewebsites.net`.

As written, the deployed SPA would have been blocked by its own CSP from calling its own API. Found
only because the owner asked for the account details to be **confirmed** rather than assumed — a
good instinct that caught a bug in an adjacent change.

`deploy.yml` now extracts the origin from `VITE_API_URL` at build time and fails the deploy if the
CSP doesn't allow it. A unit test can't cover it: the origin lives in a repo Variable, not the tree.

**Wider lesson:** three of the ten items here (this, the CSP hash, the readiness loop) were cases of
*inferring* a value that could have been *looked up*. Verify against the live system.

### 11. The compose stack's port had to move

Making the frontend container non-root meant nginx could no longer bind port 80, so it listens on
8080. `docker-compose.yml` here was updated from `8080:80` to `8080:8080` to match. The browser
URL is unchanged (`http://localhost:8080`), but a stale container image will fail to connect until
it is rebuilt.

---

# Addendum — M2 / M3 remediation, 2026-07-29

The data-tier findings, actioned. This section supersedes the "not fixed in code" note under
M2/M3 above.

**Headline: the original M2 finding was wrong about production.** It was read off the provisioning
scripts, not the live environment. Verifying first would have caught it — the same lesson three
earlier bugs in this work taught (see the bug log). The finding was still *correct about the
scripts*, which would have recreated the insecure shape on any new environment.

## What was actually there

| | Claimed by the review | Actually live |
|---|---|---|
| App to SQL auth | `sqladmin` + password in the connection string | **`Authentication=Active Directory Default`** — passwordless, via the App Service managed identity |
| App's DB rights | server administrator | contained user in one database: `db_datareader`, `db_datawriter`, **`db_ddladmin`** |
| SQL firewall | `AllowAzureServices` (0.0.0.0) | confirmed — `AllowAllWindowsAzureIps`, plus a Query Editor client IP |

So M2 was already largely solved in production by work predating this review. Three genuine
problems remained, two of which the review had not identified at all.

## What was fixed

### 1. An orphaned identity had write access to production data *(new finding)*

`sys.database_principals` held **`taskboard-05-api`** — an external (Entra) user for a retired App
Service — still a member of `db_datareader`, `db_datawriter` and `db_ddladmin`. The app itself no
longer exists (`az webapp list` shows only `taskboard-06-api`), so nothing had removed its database
user.

    DROP USER [taskboard-05-api];

This is the kind of thing only an inventory of the *database* surfaces — no amount of reading
application code or ARM templates would show it.

### 2. The live app had `db_ddladmin` it does not need

Reader + writer is sufficient at runtime. Checked before changing anything:

- **`main`** — EF's `EnsureCreatedAsync()` no-ops against a database that already has tables; it
  never issues DDL.
- **`dapper`** — `Schema.SqlServer.sql` guards every statement with
  `IF OBJECT_ID(N'dbo.X', N'U') IS NULL`, so nothing executes against an existing schema.

    ALTER ROLE db_ddladmin DROP MEMBER [taskboard-06-api];

Schema creation is now a provisioning-time task, documented in both provision scripts. The point:
`db_datawriter` already lets a compromised app delete every row — but `db_ddladmin` additionally
lets it drop tables and create objects. Removing it shrinks what an application-level compromise
can reach.

### 3. SQL password authentication was still enabled *(new finding)*

`azureAdOnlyAuthentication` was **false**. The app was passwordless, but the `taskmgr` server-admin
login and its password remained a valid credential — reachable from anywhere the firewall allowed.
A passwordless app in front of a still-password-protected server is only half the control.

    az sql server ad-only-auth enable -g rg-taskboard -n taskboard-05-sql

Safe here because an Entra administrator already exists and both the app and the tooling
authenticate via Entra. **Risk accepted and worth stating:** with Entra-only auth on, losing that
Entra account means losing administrative access to the server.

### 4. M3 — the firewall no longer admits all of Azure

`AllowAllWindowsAzureIps` (`0.0.0.0`) is Azure's "allow all Azure services" special case. It admits
resources from **any Azure tenant**, not just this subscription — so with SQL auth still enabled
(item 3), the only barrier was the password.

Replaced with 22 rules, one per address in the App Service's `possibleOutboundIpAddresses`, then
the blanket rule deleted.

**Why not a Private Endpoint** — the stronger control needs VNet integration, which requires a
Basic or higher App Service plan. This stack runs on **F1 (Free)**. Allow-listing outbound IPs is
the correct answer *at this tier*, and is noted as such in the scripts so nobody "fixes" it later
without knowing the constraint.

**Why `possibleOutboundIpAddresses`, not `outboundIpAddresses`** — the latter is only the addresses
in use right now. An App Service can move within its scale unit, so allow-listing the current set
produces intermittent, hard-to-diagnose failures weeks later.

One rule was left in place: `QueryEditorClientIPAddress_...` (the owner's own IP, for the portal
Query Editor). Removing it would have taken away their console access.

## How it was tested

Each change was applied, then the **live** app was restarted and exercised before moving on — not
batched and hoped for:

| After | Test | Result |
|---|---|---|
| dropping the orphan + `db_ddladmin` | restart, then login / GET / POST / DELETE against the live API | 200 / 200 / created id 13 / 204 |
| narrowing the firewall | restart, login + read | 200 / 200 |
| enabling Entra-only auth | restart, login + read | 200 / 200 |

Full CRUD under reader+writer alone is the meaningful result: it proves the app never needed
`db_ddladmin`, rather than assuming it.

A throwaway .NET console tool (`Microsoft.Data.SqlClient` with
`Authentication=Active Directory Default`) was used to run the SQL, because it authenticates with
the same Entra credential chain as the app — `sqlcmd` on this machine could not present an Entra
token. It lives in the session scratchpad, not the repo.

## How it is prevented from recurring

**`scripts/check-azure-posture.sh`** and **`scripts/Check-AzurePosture.ps1`** — 11 assertions
covering every item above, plus TLS versions, `httpsOnly`, and the H1 demo-seed setting. Read-only,
exit 1 on any failure.

**The checker was negative-tested**, because two earlier checks in this work reported success while
the thing they checked was broken (`dotnet --info` as a HEALTHCHECK; a Docker readiness loop that
printed "engine ready" with the engine down). The all-of-Azure rule was briefly re-added; the check
went red and exited 1; the rule was removed by an `EXIT` trap. A check that has never failed is not
yet a check.

**Deliberately not a GitHub Actions workflow.** The deploy identity (`oidc-msi-ac8b`) holds
`Website Contributor` on the App Service alone and cannot read SQL configuration. Widening it just
to run a checker would trade a real, permanent privilege increase for convenience — the opposite of
what these checks protect. (An attempt to grant it `Reader` on the SQL server failed with
`MissingSubscription` regardless.) Run the checker from a shell or Cloud Shell.

**Both provision scripts rewritten** so a *new* environment is created in this shape by default:

- `az sql server create` takes `--enable-ad-only-auth` with `--external-admin-*` and **no**
  `--admin-user` / `--admin-password` — there is no SQL password to store, rotate, or keep off the
  command line. This retires finding M4 rather than merely mitigating it.
- `--minimal-tls-version 1.2` pinned at creation.
- Firewall built from `possibleOutboundIpAddresses`; the 0.0.0.0 rule is gone, with a comment
  explaining the free-tier constraint.
- Connection string is the passwordless Entra form, set directly as an app setting — it holds no
  credential, so it no longer needs Key Vault. `SqlAdminPassword` / `SqlConnectionString` secrets
  are no longer created.
- The settings-import path refuses to overwrite `ConnectionStrings__DefaultConnection`, so
  replaying a captured environment cannot silently reintroduce a password-bearing string.
- Both scripts end by printing the `CREATE USER ... FROM EXTERNAL PROVIDER` plus reader/writer
  grants, since creating a server does not create a database user.

**`.github/workflows/scripts-lint.yml`** — the infra scripts had no coverage of any kind. Now every
`.sh` is checked with `bash -n` and `shellcheck --severity=error`, and every `.ps1` is parsed.

## Bugs found along the way

### 12. `infra/Export-Azure.ps1` could never run *(pre-existing, unrelated to this work)*

    Write-Host "`nContents of $OutputDir:"

`$OutputDir:` is parsed as a drive-qualified variable reference (the `$env:PATH` form), which is a
**parse error** — the entire script failed to load, and had done for as long as that line existed.
Fixed with `${OutputDir}:`. This is exactly why `scripts-lint.yml` now exists: nothing in the repo
executed these files, so a fatal error sat in one indefinitely.

### 13. Two wrong diagnoses of that same parse failure, before the right one

Windows PowerShell 5.1's `ParseFile` reported **45 errors** in the *pristine* `Provision.ps1`.

1. First theory: the `.gitattributes` `eol=lf` rule broke backtick line-continuations. Plausible —
   and wrong. I got as far as **adding `*.ps1 text eol=crlf` to `.gitattributes`** before testing
   it; restoring CRLF changed nothing. The change was reverted, because a fix justified by a false
   premise is not a fix.
2. Second theory: `$x = if (...) {...}` is PowerShell 7-only syntax. Tested directly in 5.1 —
   parses fine. Also wrong.
3. Actual cause: the file is **UTF-8 without a BOM**, and `ParseFile` on 5.1 falls back to ANSI,
   mangling the em-dashes in its comments into byte sequences that break string parsing. Reading
   the file explicitly as UTF-8 and calling `ParseInput` parses it cleanly.

My edits had been correct the whole time; the *validator* was broken. `scripts-lint.yml` reads
files explicitly as UTF-8 for this reason, with a comment saying why — otherwise the next person
gets the same wall of misleading errors.

### 14. `&& echo "success"` after a failed command

    az role assignment create ... -o none 2>&1 | tail -2 && echo "  Reader granted"

`tail` succeeded, so the `&&` fired and printed success — while `az` had failed with
`MissingSubscription`. Same shape as the Docker readiness loop (item 9) and the `dotnet --info`
HEALTHCHECK (item 6): **three separate false-success reports in one piece of work.** The pattern is
always checking the wrong thing's exit status. Re-run with an explicit `echo "exit code: $?"` after
the command itself, not after a pipeline.
