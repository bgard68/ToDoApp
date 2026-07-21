# Azure infrastructure — `taskboard` (provision · export · import)

Scripts to **provision**, **export**, and **re-import** the `taskboard`
environment on Azure, aligned to the live resource group `rg-taskboard`. Bash
(Azure CLI) and PowerShell (Az) versions are provided and behave the same.

> **Where this fits:** these scripts live in **`infra/`** in the repo and are the scripted / infrastructure-as-code counterpart to the manual **[Azure deploy guide](../docs/deployment/azure.md)** and the **[Key Vault guide](../docs/deployment/key-vault.md)**. Run them from this `infra/` folder; exported output lands in `infra/azure-export/` (git-ignored).

## Overview

| File | Purpose |
|---|---|
| `provision.sh` / `Provision.ps1` | **Provision** the stack; optionally **import** captured settings + secrets |
| `export-azure.sh` / `Export-Azure.ps1` | **Export** (read-only) an existing environment to ARM + Bicep + inventory + app settings + Key Vault secret names |
| `LOCAL-EXPORT-RUNBOOK.md` | Step-by-step for running the export from local PowerShell |
| `.gitignore` | Keeps exported secret files out of git |
| `README.md` | This document |

Two workflows, one lifecycle:

```
                 export-azure.*                       provision.* (with import)
  ┌───────────┐  ─────────────►  ┌────────────────┐  ─────────────►  ┌───────────┐
  │  LIVE env │                  │  azure-export/ │                  │  NEW env  │
  │ rg-taskboard                 │  ARM / Bicep   │                  │  (clone / │
  │           │                  │  inventory     │                  │   DR /    │
  │           │                  │  *.settings.env│                  │   dev)    │
  │           │                  │  *.secrets.json│                  │           │
  └───────────┘                  └────────────────┘                  └───────────┘
     capture what exists   →   review / version non-secret parts   →   rebuild + re-apply config
```

- **Provision** stands up the infrastructure (idempotent, safe to re-run).
- **Export** is **read-only** — it never changes Azure; it captures the current
  state so you can document, version, or clone it.
- **Import** feeds an export back into `provision.*` so a clone of the environment
  (including secrets like `Jwt--Key` and app settings) is a single command.

See **[Best practices](#best-practices)** before running against a real
subscription.

## Current deployed environment (`rg-taskboard`)

Captured from Azure on 2026-07-20 (region **Central US**). The provisioning
scripts are modelled on this:

| Resource | Type | Notes |
|---|---|---|
| `taskboard-kv` | Key Vault | Secrets store |
| `taskboard-05-sql` | SQL server (v12.0) | Logical server |
| `taskboard-05-sql/taskboard` | SQL database | **GP serverless Gen5**, **free-limit** enabled |
| `ASP-rgtaskboard-a1a3` | App Service plan | **Linux, F1 (Free)** |
| `taskboard-06-api` | Web app | **Linux** (`app,linux`) |
| `oidc-msi-8552`, `oidc-msi-ac8b` | User-assigned managed identities | **OIDC / CI-CD** federated deploy |

Notably there is **no storage account** in the live environment (so no static
website is deployed there).

### How the scripts were aligned

| Was (initial scripts) | Now (aligned to live env) |
|---|---|
| Windows App Service, `B1` | **Linux App Service, `F1` (Free)** |
| Azure SQL, fixed `S0` | **GP serverless Gen5 + free limit**, auto-pause |
| Region `eastus` | **`centralus`** |
| System-assigned identity only | System-assigned (app→Key Vault) **plus a user-assigned `oidc-msi` identity** for CI/CD |
| Storage + static site always created | **Optional** — off by default (`ENABLE_STORAGE` / `-EnableStorage`) since the live env has none |

## What gets created

| Resource | Purpose |
|---|---|
| Resource group | Container for everything below |
| **User-assigned managed identity** (`<project>-oidc-msi`) | Standalone identity for OIDC / CI-CD federated deploys (GitHub Actions / Azure DevOps) |
| App Service plan (**Linux**) | Compute for the web app (`F1`/Free by default) |
| Web app (App Service) | Linux web app, HTTPS-only, with a system-assigned **managed identity** for Key Vault |
| Azure SQL server + database | **General Purpose serverless Gen5** database, auto-pause, **free-limit** by default |
| SQL firewall rule | "Allow Azure services" so the web app can reach the DB |
| Key Vault | Holds the SQL connection string and SQL admin password (+ storage string if enabled) |
| *(optional)* Storage account + blob container + **static website** | Only when `ENABLE_STORAGE=true` / `-EnableStorage`; `StorageV2`, TLS 1.2, public access off, `$web` static hosting seeded with starter pages |

The web app's app settings reference the Key Vault secrets, so connection strings
are resolved at runtime through the managed identity — no secrets are baked into
config.

> **Azure SQL free limit:** the free offer allows **one** free-limit database per
> subscription. If `rg-taskboard` already uses it, set `SQL_USE_FREE_LIMIT=false`
> (bash) / `-SqlUseFreeLimit $false` (PS) when provisioning a second database, or
> it will fail.

## Prerequisites

**Bash / Azure CLI**
- [Azure CLI](https://aka.ms/azcli) installed
- `az login` then `az account set --subscription <id-or-name>`
- `openssl` (used to generate the SQL password)

**PowerShell / Az**
- PowerShell 7+ recommended
- `Install-Module Az -Scope CurrentUser` (a **recent Az.Sql** is needed for the
  serverless / free-limit parameters)
- `Connect-AzAccount` then `Set-AzContext -Subscription <id-or-name>`

> In **Azure Cloud Shell** all of the above is already installed and you're
> already signed in — just upload the script and run it.

## Usage

Preview the planned resource names without creating anything:

```bash
./provision.sh --what-if
```
```powershell
./Provision.ps1 -WhatIfPlan
```

Run with defaults:

```bash
chmod +x provision.sh
./provision.sh
```
```powershell
./Provision.ps1
```

Also create the optional storage account + static website:

```bash
ENABLE_STORAGE=true ./provision.sh
```
```powershell
./Provision.ps1 -EnableStorage
```

Override anything you like:

```bash
PROJECT=taskboard LOCATION=centralus APP_SKU=B1 SQL_MAX_VCORES=4 ./provision.sh
```
```powershell
./Provision.ps1 -Project taskboard -Location centralus -AppSku B1 -SqlMaxVCores 4
```

## Parameters (defaults)

| Bash env var | PowerShell param | Default | Notes |
|---|---|---|---|
| `PROJECT` | `-Project` | `taskboard` | Short name; used in resource names |
| `ENVIRONMENT` | `-Environment` | `prod` | `dev` / `test` / `prod` |
| `LOCATION` | `-Location` | `centralus` | Azure region |
| `RESOURCE_GROUP` | `-ResourceGroup` | `rg-<project>` | |
| `APP_SKU` | `-AppSku` | `F1` | App Service plan SKU (Free). Use `B1`/`S1`/`P1v3` to scale up |
| `RUNTIME` | `-Runtime` | `DOTNETCORE:8.0` (bash) / `DOTNETCORE\|8.0` (PS) | Linux runtime; e.g. `NODE:20-lts`, `PYTHON:3.12`, `JAVA:17` |
| `SQL_MAX_VCORES` | `-SqlMaxVCores` | `2` | Serverless auto-scale ceiling |
| `SQL_MIN_VCORES` | `-SqlMinVCores` | `0.5` | Serverless auto-scale floor |
| `SQL_AUTO_PAUSE_MIN` | `-SqlAutoPauseMin` | `60` | Minutes idle before auto-pause |
| `SQL_USE_FREE_LIMIT` | `-SqlUseFreeLimit` | `true` | Azure SQL free offer (one per subscription) |
| `SQL_ADMIN_USER` | `-SqlAdminUser` | `sqladmin` | |
| `SQL_ADMIN_PASSWORD` | `-SqlAdminPassword` | *(auto-generated)* | Stored in Key Vault if generated |
| `UAMI_NAME` | `-UamiName` | `<project>-oidc-msi` | User-assigned identity for CI/CD |
| `KEYVAULT_NAME` | `-KeyVaultName` | `<project>-kv` | |
| `ENABLE_STORAGE` | `-EnableStorage` | `false` / off | Also create storage account + static website |
| `STORAGE_SKU` | `-StorageSku` | `Standard_LRS` | Only when storage enabled |
| `STATIC_INDEX` / `STATIC_404` | `-StaticIndex` / `-Static404` | `index.html` / `404.html` | Static site documents |
| `STATIC_UPLOAD_SAMPLE` | `-NoStaticSample` | seed on | bash: set `0` to skip; PS: pass `-NoStaticSample` |

Globally-unique names (web app, SQL server, storage account) get a random suffix
by default so first runs don't collide. Pin them via the matching variable/param
(e.g. `WEBAPP_NAME`, `SQL_SERVER_NAME`) if you want stable names or want to update
the existing `taskboard` resources in place.

## Wiring up OIDC / CI-CD deploys

The scripts create a **user-assigned managed identity** (`<project>-oidc-msi`) to
mirror the live env's `oidc-msi-*` identities. To let GitHub Actions or Azure
DevOps deploy **without stored secrets**, add a federated credential to it and
grant it a deploy role. Bash:

```bash
az identity federated-credential create \
  --name github-main --identity-name taskboard-oidc-msi -g rg-taskboard \
  --issuer https://token.actions.githubusercontent.com \
  --subject "repo:<ORG>/<REPO>:ref:refs/heads/main" \
  --audiences api://AzureADTokenExchange
```

PowerShell uses `New-AzFederatedIdentityCredential` (see the commented block in
`Provision.ps1`). Then give the identity a scoped role, e.g. **Website
Contributor** on the web app, and reference its `clientId` in your CI login step.

## Retrieving the generated SQL password

```bash
az keyvault secret show --vault-name <kv-name> --name SqlAdminPassword --query value -o tsv
```
```powershell
Get-AzKeyVaultSecret -VaultName <kv-name> -Name SqlAdminPassword -AsPlainText
```

## Tear down

```bash
az group delete --name <resource-group> --yes --no-wait
```
```powershell
Remove-AzResourceGroup -Name <resource-group> -Force -AsJob
```

## The static website

Both scripts enable **static website hosting** on the storage account and seed a
starter `index.html` + `404.html`. After a run, the site is live at the account's
web endpoint (printed in the summary), e.g.
`https://<storageaccount>.z13.web.core.windows.net/`.

Publish your own site by uploading your build output to the `$web` container:

```bash
az storage blob upload-batch \
  --account-name <storageaccount> --account-key <key> \
  --source ./dist --destination '$web' --overwrite
```
```powershell
Get-ChildItem ./dist -Recurse -File | ForEach-Object {
  $blob = $_.FullName.Substring((Resolve-Path ./dist).Path.Length + 1) -replace '\\','/'
  Set-AzStorageBlobContent -Container '$web' -Context $ctx -File $_.FullName -Blob $blob -Force
}
```

Skip seeding the starter pages with `STATIC_UPLOAD_SAMPLE=0 ./provision.sh`
(bash) or `./Provision.ps1 -NoStaticSample` (PowerShell).

**Custom domain + HTTPS:** the storage web endpoint serves HTTPS on the
`*.web.core.windows.net` host. To put it on your own domain with a managed
certificate, front it with **Azure Front Door** or **Azure CDN** and add a
custom-domain rule — happy to add that to the scripts if you want it.

### Alternative: Azure Static Web Apps

If your static site is really a SPA (React/Vue/etc.) or needs CI/CD, free managed
SSL on custom domains, staging environments, or a built-in serverless API, use the
dedicated **Azure Static Web Apps** service instead. One command creates it and
wires up a GitHub Actions workflow:

```bash
az staticwebapp create \
  --name swa-<project>-<env> \
  --resource-group <resource-group> \
  --location eastus2 \
  --source https://github.com/<org>/<repo> \
  --branch main \
  --app-location "/" \
  --output-location "dist" \
  --login-with-github
```

(The `Free` SKU is generous for most static sites. This path is independent of the
storage-based site above — pick one.)

## Exporting the existing setup (capture what's already deployed)

To capture the current state of resources **as they exist in Azure** — for
version control, documentation, or to reproduce an environment — use the export
scripts. They are **read-only** (they never modify Azure) and produce three
things: ARM templates (authoritative JSON), decompiled **Bicep** (clean and
re-deployable), and an **inventory** (Markdown table + CSV).

> **Running from local PowerShell?** See **[`LOCAL-EXPORT-RUNBOOK.md`](LOCAL-EXPORT-RUNBOOK.md)**
> for the exact step-by-step commands used to capture `rg-taskboard` from a local
> machine — including how to pull Key Vault secrets (which time out in Cloud
> Shell), the `Jwt--Key` / `Jwt__Issuer` → `Jwt:*` config mapping, session
> cleanup, and how to keep secret files out of git.

```bash
# One resource group
./export-azure.sh -g rg-contoso-dev

# Every resource group in the current subscription
./export-azure.sh --all

# Custom output folder, skip Bicep decompile
./export-azure.sh -g rg-contoso-dev -o ./export --no-bicep
```
```powershell
./Export-Azure.ps1 -ResourceGroup rg-contoso-dev
./Export-Azure.ps1 -All -OutputDir ./export
```

Output lands in `./azure-export/` by default:

| File | What it is |
|---|---|
| `<rg>.arm.json` | Exact ARM template of the resource group (authoritative) |
| `<rg>.bicep` | Same, decompiled to Bicep — the cleanest re-deployable form |
| `inventory.md` / `inventory.csv` | Every resource: name, type, group, location, SKU, kind |
| `<app>.settings.env` | App settings (env vars) of each web/API app, flattened to `KEY=value`; Key Vault-backed values shown as `KEY=@kv:<SecretName>` |
| `<app>.appsettings.json` / `<app>.connectionstrings.json` | Raw app settings and connection strings for each web/API app |
| `keyvault-<vault>.secrets.txt` | Secret **names** in each Key Vault (values not exported) |

### Capturing app settings + Key Vault secrets

The export also pulls, for **every web/API app** in the group, its application
settings (which App Service exposes to the app as **environment variables**) and
connection strings — and for **every Key Vault**, the list of secret **names**.

Key Vault *references* in app settings are captured as `@kv:<SecretName>`, never
resolved, so **no secret values leak** into `<app>.settings.env` by default.

```bash
./export-azure.sh -g rg-taskboard                     # includes settings + secret names
./export-azure.sh -g rg-taskboard --no-app-config     # skip this capture entirely
./export-azure.sh -g rg-taskboard --include-secret-values   # ALSO dump secret VALUES (sensitive!)
```
```powershell
./Export-Azure.ps1 -ResourceGroup rg-taskboard
./Export-Azure.ps1 -ResourceGroup rg-taskboard -NoAppConfig
./Export-Azure.ps1 -ResourceGroup rg-taskboard -IncludeSecretValues
```

Notes:
- Listing Key Vault secret **names** needs data-plane access on the vault
  (`Key Vault Secrets User`, or a "list" access policy). Without it the script
  warns and skips that vault — the resource still appears in the inventory.
- `--include-secret-values` / `-IncludeSecretValues` writes real secret values to
  `keyvault-<vault>.secrets.env`. Treat that file as sensitive — don't commit or
  share it. Left off, only names are captured.
- The bundled **`.gitignore`** already excludes the sensitive outputs
  (`*.settings.env`, `keyvault-*.secrets.json/.env`, etc.), so committing the
  `azure-export/` folder won't leak secrets. Names-only files (`*.secrets.txt`)
  and the ARM/Bicep/inventory remain committable.

Re-deploy a captured group elsewhere with:

```bash
az deployment group create -g <target-rg> --template-file azure-export/<rg>.bicep
```

**Caveats of `az group export`:** it's a point-in-time snapshot and a few
resource types export incompletely or not at all (the script warns and continues).
Secrets are **not** exported (Key Vault secret *values*, connection strings, admin
passwords stay in Azure). Always diff the Bicep before trusting it for a rebuild.

### Alternative: capture as Terraform

If you'd rather manage the existing setup in Terraform, use Microsoft's
**Azure Export for Terraform** (`aztfexport`), which generates `.tf` config **and**
imports real resources into Terraform state:

```bash
# install (Linux): see https://aka.ms/aztfexport
aztfexport resource-group rg-contoso-dev      # one RG
aztfexport query "resourceGroup =~ 'rg-contoso-dev'"   # by Resource Graph query
```

This is the right tool if Terraform is your target IaC — it does the import step
that a plain ARM/Bicep export can't.

## Recreating a captured environment (one command)

Once you've exported an environment (app settings + secrets), the provisioning
scripts can **re-apply** that config while they build a fresh stack — so a full
recreate is a single run. Point them at the exported files:

```bash
RESOURCE_GROUP=rg-taskboard-copy \
IMPORT_SECRETS_FILE=azure-export/keyvault-taskboard-kv.secrets.json \
IMPORT_SETTINGS_FILE=azure-export/taskboard-06-api.settings.env \
./provision.sh
```
```powershell
./Provision.ps1 -ResourceGroup rg-taskboard-copy `
  -ImportSecretsFile  azure-export/keyvault-taskboard-kv.secrets.json `
  -ImportSettingsFile azure-export/taskboard-06-api.settings.env
```

What the import does:

- **Secrets** (`IMPORT_SECRETS_FILE` / `-ImportSecretsFile`) — every secret in the
  file (e.g. `Jwt--Key`) is created in the **new** Key Vault. Accepts the exported
  `.secrets.json` (`[{Name,Value}]`) or a `KEY=value` `.env` file.
- **App settings** (`IMPORT_SETTINGS_FILE` / `-ImportSettingsFile`) — every line of
  the `.settings.env` is applied to the new web app (e.g. `Jwt__Issuer`,
  `Jwt__Audience`, `ASPNETCORE_ENVIRONMENT`). Any `@kv:<SecretName>` value is
  rewritten to reference the **new** vault. The script's own freshly-built
  `SqlConnectionString` is kept (a stale captured one won't overwrite it).

Both scripts **merge** rather than replace, so existing settings on the app are
preserved.

> **One manual check after a recreate:** if the app loads Key Vault through the
> configuration provider (taskboard does, for `Jwt--Key`), it needs to point at
> the *new* vault. If any captured setting holds the old vault URI (e.g.
> `KeyVault__Uri` / `VaultUri`), update it to the new vault — the scripts print a
> reminder when an import runs.

## Best practices

Applies across provision, export, and import.

**Secrets**
- Exported `*.settings.env` and `keyvault-*.secrets.json/.env` can contain real
  secret values — the bundled `.gitignore` excludes them. Never commit them; keep
  only names-only (`*.secrets.txt`) and the ARM/Bicep/inventory if you want a
  committable snapshot.
- Export secret **values** only when you need them (`--include-secret-values` /
  `-IncludeSecretValues` is off by default). Prefer capturing **names** and
  re-referencing the vault over copying values around.
- In the app, prefer **Key Vault references** (`@Microsoft.KeyVault(...)`) or the
  Key Vault config provider over inline secrets in app settings.
- Clear secret values from your shell session when done
  (`Remove-Variable names, secrets`), and rotate anything that was ever committed
  or shared.

**Least privilege**
- Export only needs **Reader** on the resource group, plus **Key Vault Secrets
  User** (or a list/get policy) to read secret names/values.
- For CI/CD, use the **user-assigned identity + OIDC federated credential**
  (no stored secrets) and scope its role to just what it deploys (e.g. *Website
  Contributor* on the web app).

**Safe to run**
- Both provisioning scripts are **idempotent** — re-running converges rather than
  duplicating.
- Preview first: `./provision.sh --what-if` / `./Provision.ps1 -WhatIfPlan`.
- Export is strictly **read-only**; it never mutates Azure.
- App-setting writes **merge** (existing settings are preserved, not replaced).

**Environments & naming**
- Use a distinct `PROJECT` / `RESOURCE_GROUP` per environment (e.g.
  `rg-taskboard`, `rg-taskboard-dev`) so a clone never touches production.
- Globally-unique names get a random suffix by default; pin them only when you
  intentionally want to update existing resources in place.
- Resources are **tagged** (`project`, `environment`, `managedBy`) for cost
  tracking and cleanup.

**After an import/clone**
- If the app loads Key Vault via the configuration provider, point it at the
  **new** vault (update any captured `KeyVault__Uri` / `VaultUri`).
- Verify with a quick smoke test before switching traffic; tear down throwaway
  clones with `az group delete` / `Remove-AzResourceGroup`.

## Notes & possible next steps

- The "Allow Azure services" firewall rule (`0.0.0.0`) is convenient but broad.
  For tighter security, switch to a **Private Endpoint** / VNet integration, or
  use **Microsoft Entra (Azure AD) authentication** to SQL with the web app's
  managed identity instead of a SQL admin password.
- The scripts use SQL auth for the connection string. If you prefer passwordless
  access end-to-end, we can add `Authentication=Active Directory Managed Identity`
  and grant the identity a DB role.
- For repeatable, reviewable deployments consider **Bicep** or **Terraform** —
  happy to port this to either.
