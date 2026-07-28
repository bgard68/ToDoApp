# Security remediation — DevSecOps review, 2026-07-28

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

> **Deployment note:** if the live App Service currently relies on the demo account, set
> `Seed__DemoUser=true` and `Seed__Password=<new value>` before deploying, and rotate away from
> `Password123!`. Existing rows are untouched by this change — the old demo user, if already in
> the production database, **must be deleted or have its password rotated manually**.

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

**Not fixed — deliberately deferred:** moving the refresh token to an `httpOnly` cookie. That is a
cross-cutting change to the auth contract (CSRF protection, cookie domain, the SWA/App Service
cross-origin split) rather than a hardening tweak, and the headers above remove the practical
exploit path. It remains the right long-term move.

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
`dapper` logged it raw. Since `workflow_dispatch` deploys whichever branch is selected to the same
App Service, shipping `dapper` regressed a fix that had already been made and written up.

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

**M2 and M3 are NOT fixed in code** — they are changes to a live Azure environment, not to the
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

### Low findings deliberately left open

- **L6** (Key Vault access policies rather than RBAC, no purge protection) — an Azure-side change
  to a live vault, same category as M2/M3.
- **L8** (PBKDF2 100k iterations, below OWASP's 600k) — raising it multiplies the cost of every
  login on a Free-tier instance, and the H3 rate limiter now removes the brute-force pressure that
  makes the iteration count urgent. The stored format already carries the iteration count, so a
  rehash-on-login upgrade is available when the tier allows. **Argon2id would be the better move
  than simply raising the count.**
- **L9** (weak password policy, no breach-list check) — a HIBP k-anonymity lookup adds an outbound
  dependency to the registration path; worth doing, but it's a feature decision.
- **L10** (`AllowedHosts: "*"`) — the correct value is the deployment's hostname, which belongs in
  App Service configuration, not in a committed default that would break local dev and CI.
- **L11** (`TreatWarningsAsErrors`) — analyzers are now on repo-wide; turning warnings into errors
  should follow once the existing warning surface is at zero, so it doesn't land as a wall of
  unrelated failures.

---

## Verification

```
dotnet restore TodoApp.sln --locked-mode      # lock files authoritative
dotnet build   TodoApp.sln -c Release         # 0 warnings, 0 errors
dotnet test    TodoApp.sln -c Release
```

**80 tests pass** on `main` (53 unit + 27 integration), up from 53 (36 + 17).
**27 new tests**, all of which fail against the pre-fix code.
