# Security remediation — DevSecOps review, 2026-07-28 (`frontend` branch)

Findings from the Senior DevSecOps review that apply to the React SPA, what was changed, what now
stops each one coming back, and the problems hit while doing it.

The API branches (`main`, `dapper`) carry their own copy of this write-up at
`docs/development/security-remediation.md`. They are maintained in parallel — `dapper` is not
merged from `main` — so every shared fix was applied to each branch independently.

---

## H2 (HIGH) — No CSP or security headers, with the refresh token in `localStorage`

### The analysis

`src/lib/apiClient.js` keeps the access token in memory (good) but persists the **refresh token**
in `localStorage`:

```js
export function setSession(auth) {
  accessToken = auth.accessToken;
  localStorage.setItem(REFRESH_KEY, auth.refreshToken);   // 7-day credential, readable by any JS
}
```

That trade-off is disclosed in a code comment, which is better practice than most codebases
manage. The problem was that **nothing compensated for it**:

- `staticwebapp.config.json` had a `globalHeaders` block that set only `Cache-Control`.
- `nginx.conf` set no headers at all.
- No `Content-Security-Policy`, `X-Content-Type-Options`, `Referrer-Policy`,
  `Permissions-Policy`, `Strict-Transport-Security`, or framing control anywhere.

So any XSS — including one arriving through a compromised npm dependency, which nothing in the
pipeline was scanning (M6 below) — yields the refresh token, and therefore a 7-day silently
renewing session. That is full account takeover, and it survives a password change unless the
security stamp is rotated.

### The fix

Headers added to **both** delivery paths, with a deny-by-default CSP:

```
default-src 'self';
script-src 'self' 'sha256-GW825FdRS8YFXkaacjvphmbKysoTTeAxlXyal7guZew=' https://accounts.google.com;
style-src 'self' 'unsafe-inline';
img-src 'self' data: https://accounts.google.com https://*.googleusercontent.com;
connect-src 'self' https://taskboard-06-api-aehtbcg8eha6fyf8.centralus-01.azurewebsites.net https://accounts.google.com;
frame-src https://accounts.google.com;
font-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'
```

plus `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`, `Permissions-Policy` and
(SWA only) `Strict-Transport-Security`. Full rationale per directive is in
[SECURITY-HEADERS.md](SECURITY-HEADERS.md).

Two details worth calling out:

- **`https://accounts.google.com` is required.** `GoogleButton.jsx` injects Google Identity
  Services at runtime and renders it in an iframe. A CSP that omits `script-src`/`frame-src` for
  that origin silently breaks the "Sign in with Google" button.
- **The inline theme script is allowed by hash, not by `'unsafe-inline'`.** `index.html` runs a
  small script before paint to avoid a flash of the wrong theme. Static hosting can't issue a
  per-response nonce, so it is allow-listed by SHA-256. `script-src` contains no `'unsafe-inline'`
  and no `'unsafe-eval'`.

**Not fixed — deliberately deferred:** moving the refresh token to an `httpOnly` cookie. That's a
change to the auth contract (CSRF protection, cookie domain, the SWA/App-Service cross-origin
split), not a hardening tweak. The headers above remove the practical exploit path. It remains the
right long-term move.

### How it's prevented going forward

`src/lib/csp.test.js` — **10 new tests**, run by `npm test`, which is already a deploy gate:

| Test | Guards |
|---|---|
| `allows the current inline theme script by hash` | recomputes the SHA-256 from `index.html` and fails if the policy is stale — **the failure message contains the replacement hash** |
| `denies everything by default and forbids framing` | `default-src 'self'`, `frame-ancestors`, `object-src`, `base-uri` |
| `permits Google Identity Services...` | stops a well-meant tightening from silently killing Google sign-in |
| `does not allow arbitrary inline or eval-ed script` | no `'unsafe-inline'` / `'unsafe-eval'` creeping into `script-src` |
| baseline header assertions (×4) | the four headers, on the SWA config and in `nginx.conf` |
| `emits nginx headers on error responses too` | every `add_header` carries `always` — without it nginx drops them on 4xx/5xx, exactly the responses being probed |

The hash test is the important one: the CSP and `index.html` are silently coupled, and the failure
mode without it is invisible (no crash, just the theme flash returning plus a console error).

Also added `.gitattributes` pinning `index.html`, `nginx.conf` and `staticwebapp.config.json` to
LF — see the bug log below for why that turned out to matter.

---

## M6 (MEDIUM) — The npm dependency tree was monitored by nothing

### The analysis

`dependabot.yml` on this branch declared only `github-actions` and `nuget`, with the comment
"NuGet packages across the solution (TodoApp.sln at the repo root)" — a byte-identical copy of
`main`'s, describing files that don't exist here. And Dependabot only reads that file from the
repository's **default** branch, so this copy was inert regardless.

Net effect: React, Vite, Vitest and their entire transitive tree were never checked for
advisories, and nothing in the pipeline ran `npm audit`.

### The fix

- `main`'s `dependabot.yml` gained an `npm` ecosystem entry with `target-branch: "frontend"` (plus
  `github-actions` for this branch). That's the copy Dependabot actually reads.
- This branch's copy is now synced to match, with a comment stating plainly that it is inert and
  that changes must be made on `main`.
- `deploy.yml` runs `npm audit --audit-level=high` as a build step, so a high/critical advisory
  fails the deploy rather than shipping.

**Current state:** `npm audit` reports **0 vulnerabilities**.

---

## M8 (MEDIUM) — This workflow missed the hardening applied to the API workflows

### The analysis

`api-ci-cd.yml`, `secret-scan.yml`, `cleanup-runs.yml` and `keep-warm.yml` all declare an explicit
least-privilege `permissions:` block and pass `persist-credentials: false`. `deploy.yml` did
neither, so it inherited the repository-default `GITHUB_TOKEN` scope and left the checkout
credential in the runner's git config for the whole job — including while running `npm ci` and
`npm test`, which execute third-party package code.

It also had no `timeout-minutes` and no `concurrency` group.

### The fix

```yaml
permissions:
  contents: read
  pull-requests: write     # the SWA action comments on PRs
concurrency:
  group: frontend-deploy-${{ github.ref }}
  cancel-in-progress: true
```

plus `persist-credentials: false`, `timeout-minutes` on both jobs, and every action pinned to a
commit SHA rather than a mutable tag (M11).

One extra: the deploy step is now skipped for pull requests from forks. Secrets aren't exposed to
fork PRs, so that path previously produced a confusing authentication failure rather than a
meaningful result. Pushes and same-repo PRs are unaffected.

---

## M10 (MEDIUM) — Container ran as root and ignored the lock file

### The analysis

```dockerfile
COPY package*.json ./
RUN npm install          # ignores package-lock.json
...
FROM nginx:alpine        # master process runs as root
```

`npm install` resolves whatever satisfies the ranges in `package.json` and will happily rewrite the
lock file, so the image could ship a dependency tree nobody reviewed — and a *different* one from
CI, which already used `npm ci`. Two build systems, same commit, potentially different bytes.

### The fix

- `npm ci` — installs exactly what `package-lock.json` records and fails if it disagrees with
  `package.json`.
- Base image switched to `nginxinc/nginx-unprivileged:alpine`, which runs as uid 101. Because a
  non-root process cannot bind a port below 1024, nginx now listens on **8080**; `nginx.conf` and
  the `docker-compose.yml` port mapping on `main`/`dapper` were updated to match.
- Added a real `HEALTHCHECK` (the unprivileged nginx image includes `wget`).
- `server_tokens off` so the exact nginx version isn't advertised.

**Not verified at runtime:** the Docker engine wasn't running on this machine, so the image build
was not executed. The changes are mechanical, but they have not been proven to build. Run
`docker build -t todoapp-web .` before relying on the container path.

**Still open:** base images are pinned by tag, not digest. Digest pinning needs a registry
round-trip to resolve and a process to refresh them.

---

## Verification

```
npm ci
npm audit --audit-level=high     # 0 vulnerabilities
npm test                         # 38 pass (28 pre-existing + 10 new)
npm run build                    # succeeds
```

The build output was checked explicitly: the inline script in `dist/index.html` hashes to
`sha256-GW825FdRS8YFXkaacjvphmbKysoTTeAxlXyal7guZew=`, matching the deployed CSP. Vite passes the
script through unchanged, so the policy is correct for the artefact that actually ships — not just
for the source file.

---

## Bugs and surprises hit during this work

Recorded because several of them are traps the next person will hit too.

### 1. The CSP hash was wrong because of line endings — caught by the new test

The first hash was computed with Python (which normalises `\r\n` to `\n` when reading text). The
test computed it with Node's `readFileSync`, which does **not** normalise. On a Windows checkout
`index.html` has CRLF, so the two disagreed and the test failed immediately.

That's the correct answer, and it exposed a real deployment hazard: a browser hashes the exact
bytes it receives. The artefact is built on Linux (LF), but a Docker image built from a Windows
working copy would have served CRLF — a script the CSP rejects, failing silently.

Fixed on both sides: the test normalises to LF (the authoritative form), and `.gitattributes` pins
the three coupled files to `eol=lf` so a local build agrees with production. **A test written to
catch future drift caught a bug in its own change first.**

### 2. `ConfigureAppConfiguration` didn't override `appsettings.Development.json` in the test host

On the API branches, the integration-test factory set rate limits via
`builder.ConfigureAppConfiguration(c => c.AddInMemoryCollection(...))`. It had no effect — the
limiter kept using the Development value, so the "returns 429" test saw eight `401`s instead.
Switching to `builder.UseSetting(key, value)` fixed it. Worth knowing before debugging a
`WebApplicationFactory` override that appears to be ignored.

### 3. `--` is illegal inside an XML comment

`Directory.Build.props` failed to load with `MSB4024` after a comment mentioned `--locked-mode`,
then again after it mentioned `--force-evaluate`. XML forbids `--` inside comments entirely. The
comments now spell the flags out in words.

### 4. Pinning package versions invalidated the lock files

Changing `MediatR 12.*` to `12.5.0` changes the *requested range*, so `--locked-mode` restore
failed with `NU1004` even though the *resolved* version was identical. One
`dotnet restore --force-evaluate` regenerates them. Expected in hindsight, surprising in the
moment — and worth knowing when reviewing the diff, which shows lock-file churn with no version
changes.

### 5. An existing integration test depended on the demo seed

`Login_WithSeededDemoUser_Succeeds` signed in as `demo@todoapp.local` / `Password123!`. The first
grep for the seed's usages missed it because the test hard-codes the strings rather than
referencing `DbInitializer.DemoEmail`. It now stands up a host with seeding explicitly enabled and
its own password — which also gives the opt-in path direct coverage.

### 6. A name collision on `dapper` (`CS0841`)

`DbInitializer` already had a local `var seed = new[] {...}` (the sample todos). Adding a `seed`
options parameter produced "cannot use local variable before it is declared". The local is now
`items`.

### 7. A `HEALTHCHECK` that would have lied

The first draft of `Dockerfile.api` used `HEALTHCHECK CMD dotnet --info`. That always exits 0, so
it would have reported a hung app as healthy — worse than no check. The .NET runtime image ships
neither `curl` nor `wget`, so that Dockerfile now has **no** HEALTHCHECK, with a comment explaining
why and pointing at the external probe. The frontend image keeps one because the nginx image does
have `wget`.

### 8. Pre-existing `IDX00001` warning on `dapper`, surfaced by pinning

`System.IdentityModel.Tokens.Jwt` had floated to 8.20.0 while the `Microsoft.IdentityModel.*`
packages sat elsewhere; the package warns this can cause `TypeLoadException` at runtime. It
predates this work — it was in the baseline build output. Pinning to **8.19.2** (matching `main`)
cleared it and aligned both branches on the same identity stack.

### 9. Docker engine unavailable

`docker --version` reports 27.2.0 but the Desktop engine wasn't running, so neither image build was
executed. Stated plainly rather than assumed — see M10.
