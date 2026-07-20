# CI/CD Pipeline — How It Works, Error Handling & Testing

_[← Back to the main README](../../README.md)_

How the API's GitHub Actions pipeline is structured, how failures are caught and stop the pipeline,
and how to test all of that — including a zero-change local check anyone can run in 20 seconds.

---

## 1. The pipeline at a glance

The API pipeline lives in [`.github/workflows/api-ci-cd.yml`](../../.github/workflows/api-ci-cd.yml).
It's a single job (`build-and-deploy`) that runs top to bottom:

```
checkout → setup .NET → restore → build → run unit tests → publish → verify publish output → upload artifact → Azure login → deploy
```

- Triggers: every **push** to `main`, every **pull request** to `main`, and **manual** runs
  (`workflow_dispatch`, the "Run workflow" button).
- The **deploy** steps (Azure login + `webapps-deploy`) are gated with
  `if: github.ref == 'refs/heads/main'`, so **pull requests build and test but never deploy** —
  only a push to `main` actually ships.

The frontend has its own pipeline, [`frontend-ci-cd.yml`](../../.github/workflows/frontend-ci-cd.yml),
which **runs the Vitest suite as a gate**, then builds the Vite SPA and deploys it to Azure Static Web
Apps — if a frontend test fails, the deploy is skipped (see §5 and the
[Testing guide](../development/testing.md) for how the gate and the test suites work).

---

## 2. How GitHub Actions error handling works

The pipeline's fail-fast behavior is mostly **built into GitHub Actions** — you don't have to code it:

- **Every `run:` step executes under `bash -eo pipefail`.** Any command that returns a **non-zero
  exit code** immediately fails the step.
- **`dotnet build`, `dotnet test`, and `dotnet publish` all return non-zero on failure** — a compile
  error, a failing test, or a publish error each produce a non-zero exit.
- **A failed step fails the whole job** — the run shows a red ❌, and the steps after it are skipped.
- **A failed job stops the pipeline** — because the deploy steps are gated to a healthy `main` run
  and the job has already failed, **a broken build/test/publish can never reach the deploy.**
- **GitHub notifies you automatically** — when a workflow run fails, GitHub emails the person who
  pushed the commit and marks the run red in the **Actions** tab. No extra configuration needed.

The net guarantee: **if the build breaks, the tests fail, or the publish errors, the pipeline stops
and you're told — nothing ships.**

---

## 3. The publish guard — "Verify publish output"

There's one failure mode GitHub can't catch on its own: a publish that **exits successfully but
produces nothing useful** (a "hollow" publish). This bit us during the original deployment
(see the [Key Vault deployment troubleshooting log](troubleshooting-log.md)), where a
mis-scoped publish put the wrong files in `wwwroot` and the app served no routes.

So the pipeline adds an explicit guard right after the publish step:

```yaml
      - name: Verify publish output
        run: |
          if [ ! -f ./publish/TodoApp.WebApi.dll ]; then
            echo "::error title=Publish failed::No TodoApp.WebApi.dll in ./publish - refusing to continue."
            exit 1
          fi
          echo "Publish OK: $(ls -1 ./publish | wc -l) files."
```

If the API assembly isn't where it should be, the step writes a red `::error::` annotation and
`exit 1` — which fails the job and (per section 2) skips the deploy and emails you. If the DLL is
present, it logs a quick file count and the pipeline continues. This turns a silent, hollow publish
into a loud, hard stop.

---

## 4. How to test the pipeline

Three levels, from zero-effort to full end-to-end.

### 4a. Test the guard logic locally (zero changes, ~20 seconds)

The verify guard is a self-contained bash snippet, so you can run its exact logic by hand — nothing in
the repo is touched. Open **Git Bash** (the workflow runs bash, so this matches it) and paste these one
at a time:

```bash
mkdir -p /tmp/pubtest                                   # 1. empty folder = a hollow publish
if [ ! -f /tmp/pubtest/TodoApp.WebApi.dll ]; then echo "FAIL: no DLL"; else echo "OK"; fi   # 2. → FAIL
touch /tmp/pubtest/TodoApp.WebApi.dll                   # 3. now simulate a good publish
if [ ! -f /tmp/pubtest/TodoApp.WebApi.dll ]; then echo "FAIL"; else echo "OK: DLL present"; fi   # 4. → OK
```

Step 2 printing `FAIL` and step 4 printing `OK` proves the guard stops a hollow publish and passes a
good one. (Clean up anytime with `rm -r /tmp/pubtest`.)

### 4b. Watch it run in the real workflow (browser, no changes)

Once the workflow is on GitHub (it is, after any push to `main`), you can watch the guard run in the
actual pipeline:

1. Open the repo on GitHub → **Actions** tab.
2. Click the most recent run (named after your commit).
3. Click the **build-and-deploy** job.
4. Expand **"Verify publish output"**.

On a healthy build it shows `Publish OK: N files.` with a green ✔ — confirming the step is wired in at
the right place (after publish, before deploy). You can also start a run on demand with the
**Run workflow** button (enabled via `workflow_dispatch`).

### 4c. Force a real failure — safely, on a branch (no source changes to `main`)

To watch the pipeline actually **fail and stop** in GitHub, something has to be broken on purpose.
Because deploy is gated to `main`, do this on a **throwaway branch** — the full build → test → publish
→ verify sequence runs, but **nothing deploys**, and `main` is never touched.

To exercise the **publish guard**, add a temporary step just before "Verify publish output" that
removes the assembly, then push the branch:

```yaml
      - name: (TEST ONLY) simulate hollow publish
        run: rm -f ./publish/TodoApp.WebApi.dll
```

You'll see "Verify publish output" go red with the `::error::` message, the job stop, and no deploy.
Delete the branch when done. (To exercise **build/test** failure instead, a compile error or a flipped
assertion on the branch does it — but that's rarely necessary, since GitHub's step-failure behavior
from section 2 is guaranteed.)

---

## 5. What we did (summary)

- Kept the pipeline as a **single build-and-deploy job** (standard for a one-app, single-target
  deploy) rather than splitting into separate build/deploy jobs — appropriate for this project's scale.
- Added the **"Verify publish output"** guard so a hollow publish fails loudly instead of shipping
  nothing.
- Confirmed the fail-fast + notification behavior is **built into GitHub Actions** (non-zero exit →
  failed step → failed job → skipped deploy → email to the commit author).
- **Validated** the guard locally with the Git Bash check in §4a — no repo or code changes required.

---

_See also: [Testing guide (frontend + API)](../development/testing.md) · [Azure guide](azure.md) · [Key Vault deployment troubleshooting](troubleshooting-log.md) · [Lessons learned](../lessons.md) · [Architecture & practices assessment](../architecture/assessment.md)._

> **← Back to the main [README](../../README.md).**
