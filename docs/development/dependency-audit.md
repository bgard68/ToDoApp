# Frontend Dependency Audit — Running `npm audit` & Upgrading

_[← Back to the main README](../../README.md)_

How to check the frontend's npm dependencies for known vulnerabilities, read the report, and apply
fixes safely — plus a worked example of the **Vite 8 / Vitest 4 security upgrade** that cleared all
findings. The frontend is the only part of the stack with an npm dependency tree; the .NET side is
audited separately via NuGet lock files.

For the background on *why* `package.json` and `package-lock.json` both belong in git, see
[§5 of the testing guide](testing.md#5-dependencies--packagejson--package-lockjson).

---

## 1. Running the audit

`npm audit` cross-references your installed dependency tree against the GitHub Advisory Database.
Run it from the `frontend/` directory:

```bash
cd frontend
npm audit
```

Useful variants:

```bash
npm audit --json          # machine-readable, full advisory detail (source, CWE, CVSS, ranges)
npm audit --omit=dev      # production dependencies only — the deps that actually ship
npm audit --audit-level=high   # exit non-zero only at/above a severity (handy as a CI gate)
```

> **Dev vs. prod matters.** This app's runtime dependencies are just `react` and `react-dom`;
> everything else (`vite`, `vitest`, the testing-library packages, `jsdom`) is a **dev/build**
> dependency. A vulnerability in the dev toolchain does **not** ship to users — the exposure is on
> developer and CI machines running the dev server or test runner. `npm audit --omit=dev` is the
> fastest way to separate "ships to production" from "local tooling only."

---

## 2. Reading the report

Each finding lists the package, a **severity** (low / moderate / high / critical), whether it's a
**direct** or transitive dependency, the advisory (with CVSS score and a GHSA link), the affected
**version range**, and — critically — the **`fixAvailable`** field, which tells you the version that
resolves it and whether that's a **semver-major** bump:

```jsonc
"fixAvailable": { "name": "vite", "version": "8.1.5", "isSemVerMajor": true }
```

`isSemVerMajor: true` is the important flag: `npm audit fix` **won't** apply major upgrades
automatically (they can contain breaking changes), so those need a deliberate bump plus verification.

---

## 3. Applying fixes

```bash
npm audit fix          # safe, in-range patches only (no major bumps)
npm audit fix --force  # also applies semver-major bumps — can break; verify afterward
```

Prefer **explicit, targeted upgrades** over `--force` when the fix is a major version — you stay in
control of exactly what changes:

```bash
npm install -D <pkg>@<version>
```

**Always verify after any upgrade** — install alone doesn't prove the app still builds or passes:

```bash
npm run build && npm test
```

And commit **both** `package.json` and `package-lock.json` together — CI runs `npm ci`, which fails
if they're out of sync.

---

## 4. Worked example — the Vite 8 / Vitest 4 upgrade (July 2026)

### What the audit found

`npm audit` reported **5 vulnerabilities — 1 critical, 1 high, 3 moderate**, all in the **dev
toolchain** (`vite`/`vitest` and their transitive deps). Production deps (`react`, `react-dom`) were
clean, so nothing was exposed in the deployed Azure Static Web App — the risk was limited to the
local dev server and test runner.

| Severity | Package | Advisory | Notes |
| -------- | ------- | -------- | ----- |
| **Critical** (9.8) | `vitest` | [GHSA-5xrq-8626-4rwp](https://github.com/advisories/GHSA-5xrq-8626-4rwp) — arbitrary file read/execute when the Vitest **UI** server is listening | We run `vitest run` (no UI), so real exposure was low |
| **High** (7.5) | `vite` | [GHSA-fx2h-pf6j-xcff](https://github.com/advisories/GHSA-fx2h-pf6j-xcff) — `server.fs.deny` bypass on Windows alternate paths | Dev server only |
| Moderate | `vite` | launch-editor NTLMv2 hash disclosure (Windows UNC) + path traversal in `.map` handling | Dev server only |
| Moderate | `esbuild` | [GHSA-67mh-4wv8-2f99](https://github.com/advisories/GHSA-67mh-4wv8-2f99) — any website could send requests to the dev server and read responses | Dev server only |
| Moderate | `@vitest/mocker`, `vite-node` | transitive via `vite` | — |

Both fixes were **semver-major** (`vite` → 8, `vitest` → 4), so `npm audit fix` alone wouldn't touch
them.

### The fix

```bash
cd frontend
npm install -D vite@^8 vitest@^4 @vitejs/plugin-react@latest
npm run build && npm test
```

Result:

- `vite` `^5.4.11` → **8.1.5**
- `vitest` `^2.1.8` → **4.1.10**
- `@vitejs/plugin-react` `^4.3.4` → **6.0.4**
- `react` / `react-dom` — unchanged

### Why the fix removed the vulnerabilities rather than just patching them

This was an **architecture shift**, not only a version bump — which is why the install tree actually
*shrank* (218 → 164 packages):

- **Vite 8 replaced esbuild with Rolldown** (a Rust/oxc bundler). `esbuild` is now only an *optional
  peer dependency* and isn't installed, so the esbuild dev-server advisory is eliminated entirely,
  not merely upgraded.
- **Vitest 4 dropped `vite-node`**, removing that transitive vulnerability source from the tree.

After the upgrade: **`npm audit` reports 0 vulnerabilities**, `npm run build` succeeds, and the full
Vitest suite passes.

### One follow-up to watch — Node engine

Vite 8 raises the required Node version to **`^20.19.0 || >=22.12.0`**. CI pins `node-version: '20'`
in [`frontend-ci-cd.yml`](../../.github/workflows/frontend-ci-cd.yml), which `actions/setup-node`
resolves to the latest 20.x (currently ≥ 20.19, so it passes) — but pinning to `'20.19'` or `'22'`
makes that guarantee explicit rather than incidental.

---

_See also: [Testing guide](testing.md) · [CI/CD pipeline testing](../deployment/pipeline.md)._

> **← Back to the main [README](../../README.md).**
