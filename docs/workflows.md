# GitHub Actions workflows & how to run them

> **← Back to the main [README](../README.md).**

Four workflows live in [`.github/workflows/`](../.github/workflows/). This branch adds one change to
the API pipeline so **either the EF Core (`main`) or Dapper (`refactor/dapper`) branch can be deployed
to the same Azure App Service on demand.**

| Workflow | File | Triggers | What it does |
|----------|------|----------|--------------|
| **Build & Deploy API** | `api-ci-cd.yml` | push `main`, PR → `main`, manual | Build + unit tests; deploy the API to App Service `taskboard-06-api`. |
| **Build & Deploy Frontend** | `frontend-ci-cd.yml` | push `main`, PR → `main`, manual | Build the React app; deploy to Azure Static Web Apps (+ PR preview environments). |
| **Keep API warm** | `keep-warm.yml` | every ~10 min, manual | Pings the API root so the free App Service instance doesn't unload. No DB hit. |
| **Cleanup old runs** | `cleanup-runs.yml` | daily 06:00 UTC, manual | Deletes old Actions runs, keeping the most recent per workflow. |

## The API pipeline and its deploy gate

`api-ci-cd.yml` always runs: checkout → setup .NET → restore → **build (Release)** → **run unit tests**
→ publish → upload a build artifact. Only the final two steps (Azure login + Deploy) are gated:

```yaml
- name: Azure login
  if: github.ref == 'refs/heads/main' || github.event_name == 'workflow_dispatch'
- name: Deploy to Azure Web App
  if: github.ref == 'refs/heads/main' || github.event_name == 'workflow_dispatch'
```

> The `|| github.event_name == 'workflow_dispatch'` half is the change introduced on this branch. It
> lets a **manual** run deploy whatever branch you pick, while automatic PR builds still never deploy.
> Auth to Azure is passwordless (OIDC / federated credentials) — no stored publish profile.

### What each action does

| You do… | CI runs? | Deploys to Azure? |
|---------|----------|-------------------|
| Push a feature branch (e.g. `refactor/dapper`) | ❌ no (trigger is `main` only) | ❌ no |
| Open/update a PR → `main` | ✅ build + unit tests | ❌ no (gate false for PRs) |
| **Actions → Run workflow → pick a branch** | ✅ | ⚠️ **yes** |
| Push `main` | ✅ | ⚠️ **yes** |
| Merge a PR into `main` | ✅ | ⚠️ **yes** |

## The EF ↔ Dapper branch-choice deploy

`main` is EF Core; `refactor/dapper` is Dapper. They deploy to the **same** App Service, so whichever
you deploy last is live.

- **`main`** auto-deploys EF Core on every push (unchanged behavior).
- **Either branch** can be deployed on demand: **Actions → "Build and Deploy taskboard-06-api" → Run
  workflow → choose the branch → Run**. A manual run uses *that branch's* workflow file, so dispatching
  `refactor/dapper` hits the relaxed gate and deploys the Dapper build.

### Deploying `refactor/dapper` to Azure

1. Go to the repo's **Actions** tab → **Build and Deploy taskboard-06-api**.
2. Click **Run workflow**, select branch **`refactor/dapper`**, **Run workflow**.
3. It builds, tests, and deploys Dapper to `taskboard-06-api`.

To switch back to EF Core: Run workflow on **`main`** (or push `main`).

> ⚠️ **This is a production deploy, not a staging slot.** The workflow targets the live App Service
> against its configured **Azure SQL** connection string — the **shared** production database. That's
> safe because the Dapper schema matches EF's and `SchemaInitializer`'s DDL is idempotent (it reuses
> the existing tables), but the Dapper build immediately serves real traffic against real data. Only
> one branch is live at a time. See [deployment/azure.md](deployment/azure.md#deploying-the-refactordapper-branch)
> for the connection-string / provider settings.

## Frontend, keep-warm, cleanup

- **`frontend-ci-cd.yml`** builds the Vite app and deploys to Static Web Apps. It is path-filtered to
  `frontend/**`, so the Dapper refactor (which only touches `src/**`) never triggers it. PRs get an
  ephemeral SWA preview environment, torn down when the PR closes. Build-time env comes from repo
  Variables `VITE_API_URL` and `VITE_GOOGLE_CLIENT_ID`.
- **`keep-warm.yml`** pings the API root (which 302s to Swagger) every ~10 minutes to keep the free-tier
  instance loaded — root only, so it doesn't burn the serverless-SQL free limit.
- **`cleanup-runs.yml`** prunes old Actions runs daily (the keep-warm job piles up), keeping the latest
  run per workflow. Manual runs accept a `keep` input.

## Secrets & variables the pipelines expect

Configured under **Settings → Secrets and variables → Actions**:

| Name | Kind | Used by |
|------|------|---------|
| `AZUREAPPSERVICE_CLIENTID_…` / `TENANTID_…` / `SUBSCRIPTIONID_…` | Secret | API deploy (OIDC login) |
| `AZURE_STATIC_WEB_APPS_API_TOKEN_…` | Secret | Frontend deploy |
| `VITE_API_URL` | Variable | Frontend build + keep-warm ping |
| `VITE_GOOGLE_CLIENT_ID` | Variable | Frontend build (Google button) |
