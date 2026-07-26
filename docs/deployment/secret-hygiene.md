# Secret hygiene — keeping Azure & app secrets out of git

_[← Back to the main README](../../README.md)_

How this repo makes sure **no Azure secrets or app env/vars/secrets leak into git** — the
ignore rules, the automated scanning, and the audit that confirmed the repo is clean. Applied
across all branches (`main`, `dapper`, `frontend`).

---

## The model: defense in depth

No single mechanism is trusted on its own. Four layers stack up:

1. **Secrets never live in source.** The app reads its signing key and connection strings from
   user-secrets (dev) or environment variables / Azure Key Vault (prod), and fails fast if the JWT
   key is missing. The tracked config files (`appsettings.json`, `appsettings.Development.json`)
   hold only empty placeholders and non-secret defaults. See the [Key Vault guide](key-vault.md).
2. **`.gitignore` blocks secret-bearing files** so they can't be committed by accident.
3. **gitleaks scans** for secrets both locally (pre-commit) and in CI (every push/PR).
4. **A periodic audit** verifies the current tree *and* full history stay clean.

The rest of this doc covers layers 2–4.

---

## 1. What `.gitignore` ignores (and what it deliberately keeps)

`.gitignore` only stops files git isn't **already** tracking — so these rules were added *before*
any secret existed. Key entries in the hardened ignore list:

- **Env files:** `.env` and `.env.*` are ignored, with `!.env.example` re-included so the blank
  template stays tracked. (A bare `.env` is the most common leak; `.env.*` alone does **not** match
  it, so both are listed.)
- **Azure Connected Services / Visual Studio files** that can carry endpoints, keys, or deploy
  creds: `serviceDependencies.json`, `serviceDependencies.local.json`, `ServiceConfiguration.*.cscfg`,
  `ApplicationInsights.config`, `local.settings.json`, `*.publishsettings`, `azureauth.json`, `.azure/`.
- **Environment-specific config that tends to hold secrets:** `appsettings.Production.json`,
  `appsettings.Local.json`, `appsettings.*.Local.json`.
- **.NET user secrets & certificates:** `secrets.json`, `*.pfx`, `*.p12`, `*.pem`, `*.key`.
- **The real secrets export folder:** `azure-export/` (the infra toolkit writes real JWT keys and SQL
  connection strings there — it must never be tracked).

**Deliberately kept tracked** (they contain no secrets): `appsettings.json`,
`appsettings.Development.json`, `launchSettings.json`, and `.env.example`.

### Verifying an ignore rule

`git check-ignore` is the authoritative test — it prints the matching rule if a path is ignored,
and nothing if the file is trackable:

```bash
git check-ignore -v .env                      # -> ignored (shows the rule + line)
git check-ignore -v src/TodoApp.WebApi/appsettings.json   # -> no output (correctly trackable)
```

> **`.gitignore` can't un-leak a secret.** It prevents *new* accidental commits; it cannot remove a
> secret already written into a **tracked** file, nor scrub one from history. That's what the scans
> below are for — and if a real secret is ever committed, the fix is to **rotate the secret**, not
> just delete the file.

---

## 2. Automated scanning with gitleaks

Two places, one config.

### `.gitleaks.toml`

Extends the built-in gitleaks ruleset (`useDefault = true`) and adds a **narrow allowlist** for
constructs that look secret-ish but hold no real value — so CI doesn't fail on legitimate templates:

- `Password=$SqlAdminPassword` / `Password=${SQL_ADMIN_PASSWORD}` — variable references in the infra
  provisioning scripts, not literal passwords.
- `AccountKey=$(...)` — a storage key fetched at runtime, not a stored value.
- `$(openssl rand ...)` — a password generated at run time.
- `Authentication=Active Directory Default` — passwordless Entra connection strings.

The allowlist only excuses these variable/template patterns; a real key pasted in would not match
them and would still be flagged.

### CI gate — `.github/workflows/secret-scan.yml`

A **standalone** workflow (nothing else calls it — GitHub triggers it from its `on:` block):

```yaml
on:
  push:
  pull_request:
  workflow_dispatch:
```

So it runs automatically on **every push and every pull request, on all branches**, and can also be
run manually. It checks out full history (`fetch-depth: 0`) and runs `gitleaks/gitleaks-action@v2`,
which reads `.gitleaks.toml` automatically. gitleaks-action is free on public repos (no license
needed). A green check means no secrets; a red X means it caught something and the run fails.

### Local pre-commit hook — `.pre-commit-config.yaml` (optional)

Catches a secret a few seconds earlier — before it even commits. Requires Python's `pre-commit`.
One-time setup on your machine:

```bash
pip install pre-commit     # or: pipx install pre-commit
pre-commit autoupdate      # pin the current gitleaks release
pre-commit install         # activate the git hook
```

After that, gitleaks scans staged changes on every `git commit`. This is a convenience layer — the
CI gate protects the branches whether or not anyone installs it.

---

## 3. CI & supply-chain hardening

Beyond secret scanning, the workflows are hardened against token leakage and runaway jobs:

- **`persist-credentials: false`** on every `actions/checkout` (`api-ci-cd.yml`, `secret-scan.yml`),
  so the automatic job token isn't written into the runner's `.git/config` where a later step could
  read it. The checkout-less workflows (`cleanup-runs.yml`, `keep-warm.yml`) have no token to clear.
- **`timeout-minutes` on every job** (API 20, secret-scan 10, cleanup 15, keep-warm 5), so a hung
  step can't burn Actions minutes indefinitely.
- **`.github/dependabot.yml`** opens weekly update PRs for **GitHub Actions** and **NuGet**, so action
  pins and package versions don't silently go stale and miss security fixes. (Dependabot alerts must
  also be enabled in the repo Settings for these to surface.)

These workflow-hardening changes, together with the `.gitignore` hardening and the gitleaks
configuration, are applied on **all three branches** (`main`, `dapper`, `frontend`).

### Dependency vulnerability auditing

NuGet runs a vulnerability audit on `dotnet restore` and emits `NU1901`-`NU1904` for any referenced
package with a published advisory. On the .NET 9+ SDK that audit covers **transitive** packages by
default, so this repo already gets full-graph coverage on `net10.0`. Because the build does **not**
set `TreatWarningsAsErrors`, an advisory surfaces as a visible warning without breaking CI - the fix
then arrives as a Dependabot PR rather than an emergency build outage.

### Dependabot and the dependency graph - how GitHub runs it

Dependabot is **not** a GitHub Actions workflow you author or run - it's a GitHub-hosted service.
The only file it needs is `.github/dependabot.yml`, which is a **config file, not a workflow**. That
is the key difference from `secret-scan.yml` / `api-ci-cd.yml`, which *are* Actions workflows that
execute steps on a runner.

The moving parts, in the order they build on each other:

- **Dependency graph** - GitHub parses the repo's manifests and lockfiles (`*.csproj`,
  `packages.lock.json`, and the `uses:` lines in `.github/workflows/*`) to build the list of what
  the project depends on. It's the foundation the rest reads from. Free on public repos; turn it on
  under Settings -> Advanced Security.
- **Dependabot alerts** - GitHub compares that graph against the GitHub Advisory Database and raises
  an alert when a dependency has a published vulnerability. Requires the dependency graph. Alerts
  appear under the repo's **Security** tab.
- **Dependabot security updates** - optional automatic PRs that bump a *vulnerable* dependency to a
  fixed version when an alert fires.
- **Dependabot version updates** - the scheduled PRs driven by `.github/dependabot.yml` that keep
  dependencies *current* regardless of vulnerabilities. This is what the committed config enables.

How the config actually runs: GitHub reads `.github/dependabot.yml` and, on the schedule it declares,
its own hosted infrastructure checks each configured ecosystem and opens PRs - no runner minutes of
yours are spent, and there is no workflow to invoke. This repo's config declares two ecosystems on a
weekly cadence:

```yaml
version: 2
updates:
  - package-ecosystem: "github-actions"   # actions pinned in .github/workflows/*
    directory: "/"
    schedule:
      interval: "weekly"
  - package-ecosystem: "nuget"            # NuGet packages across TodoApp.sln
    directory: "/"
    schedule:
      interval: "weekly"
```

Version updates (the PRs) run from this config alone. Alerts and security updates additionally
require the **dependency graph** and **Dependabot alerts** to be enabled in Settings -> Advanced
Security - the config file does not turn those on by itself.

## 4. GitHub-side protections (server-enforced)

Enforced by GitHub itself, independent of anything in the repo — the strongest layer, because it
acts before or regardless of a local commit:

- **Secret scanning** (labelled *Secret Protection* under Settings -> Advanced Security) runs
  automatically on public repositories and is enabled. Findings appear under the repo's **Security**
  tab.
- **Push protection** is enabled - it blocks a push that contains a recognized secret from reaching
  GitHub at all, which is stronger than any after-the-fact scan.
- **Branch protection** - an active ruleset named **`protect-main`** targets `main` and **blocks
  force pushes** and **branch deletion**, with no bypass list. Direct pushes to `main` still work;
  its history simply can't be rewritten and the branch can't be deleted.
- **Dependabot alerts / Dependency graph** - enable these under Settings -> Advanced Security so the
  committed `.github/dependabot.yml` starts surfacing advisories and opening update PRs.

## 5. Audit result (verified)

A full scan was run across `main`, `dapper`, and `frontend`:

- **Current tree — clean.** The only sensitive-named tracked files are `.env.example`,
  `appsettings.json`, `appsettings.Development.json`, and `launchSettings.json`, and every secret
  field in them is an empty placeholder.
- **`azure-export/` — not tracked** on any branch.
- **`infra/` scripts & docs** — the connection-string matches are all templates / variables /
  runtime-generated values, never literal secrets.
- **Full git history (all 147 commits) — clean.** No secret was ever committed and later removed,
  and no secret-bearing file (`.env`, `local.settings.json`, `*.pfx`, etc.) was ever added.

Bottom line: nothing is leaking, and the ignore rules + scans keep it that way going forward.

---

## Related

- [Key Vault](key-vault.md) — where the real JWT signing key lives in production.
- [CI/CD pipeline](pipeline.md) — the API/frontend build-and-deploy workflows and the other
  automation (`cleanup-runs.yml`, `keep-warm.yml`).
- [Azure guide](azure.md) — passwordless SQL, managed identity, and CORS setup.
