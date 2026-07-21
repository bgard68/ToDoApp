# Local export runbook — capturing `rg-taskboard` from PowerShell

A step-by-step guide to capturing the full **taskboard** environment (infra,
app settings / env vars, and Key Vault secrets) from **your own machine** using
PowerShell + the Azure CLI. Everything here is **read-only** — it reads from
Azure and writes files locally; it never changes your Azure resources.

## Why run this locally instead of Cloud Shell

Azure Cloud Shell brokers access tokens per-audience, and the Key Vault
data-plane audience (`https://vault.azure.net`) frequently fails with:

```
A Cloud Shell credential problem occurred ...
Timeout waiting for token from portal. Audience: https://vault.azure.net
```

That's a Cloud Shell quirk, not a permissions error. Running from a local
terminal (where `az login` uses a normal browser token) avoids it, so **local is
the recommended way to pull Key Vault secrets.** App settings still work fine in
Cloud Shell (they use the management plane), but Key Vault is the sticking point.

## Prerequisites

- **Azure CLI** installed locally — https://aka.ms/azcli
- Signed in: `az login`
- The right subscription active: `az account show` (switch with
  `az account set --subscription "<name-or-id>"`)
- To read Key Vault secret **values**, you need data-plane access on the vault
  (`Key Vault Secrets User` RBAC role, or a "get/list" access policy).

## Step 1 — pick a working folder

Run everything from a **parent** folder (e.g. `C:\ToDoApp-Azure\infra`, where these scripts live), NOT from inside
`azure-export`. All commands below write into an `azure-export\` subfolder. If you
`cd` into `azure-export` first, drop the `azure-export/` prefix from the paths or
you'll get a doubled path like `...\azure-export\azure-export\...`.

```powershell
cd C:\ToDoApp-Azure\infra
New-Item -ItemType Directory -Force -Path azure-export | Out-Null
```

## Step 2 — infra + inventory + settings (the export script)

The bundled script captures ARM + Bicep + inventory **and** app settings + Key
Vault secret names in one go:

```powershell
./Export-Azure.ps1 -ResourceGroup rg-taskboard
```

If Key Vault listing fails with the Cloud Shell token timeout, you're in Cloud
Shell — switch to a local terminal. If it fails with `Forbidden` (403), see
"Granting Key Vault access" below.

The manual commands in the next steps do the same thing piece by piece — use them
if you prefer to run each part yourself (this is what was used to build this
runbook).

## Step 3 — app settings (env vars) → file

App Service exposes app settings to the running app as **environment variables**.
Pull them and flatten to `KEY=value`:

```powershell
az webapp config appsettings list -g rg-taskboard -n taskboard-06-api --query "[].{n:name,v:value}" -o json |
  ConvertFrom-Json | ForEach-Object { "$($_.n)=$($_.v)" } |
  Set-Content azure-export/taskboard-06-api.settings.env

# view it
Get-Content azure-export/taskboard-06-api.settings.env
```

Also keep the raw JSON (useful for connection strings, slot flags, etc.):

```powershell
az webapp config appsettings list -g rg-taskboard -n taskboard-06-api -o json |
  Set-Content azure-export/taskboard-06-api.appsettings.json
```

## Step 4 — Key Vault secrets → file

List names, then pull each value into a structured object. **JSON output** is the
right format — it safely encodes values that contain `=`, spaces, or newlines
(connection strings, signing keys), which a flat `KEY=value` file mangles.

```powershell
$names = az keyvault secret list --vault-name taskboard-kv --query "[].name" -o tsv

$secrets = foreach ($n in $names) {
  [pscustomobject]@{
    Name  = $n
    Value = (az keyvault secret show --vault-name taskboard-kv --name $n --query value -o tsv)
  }
}

# structured file (recommended)
$secrets | ConvertTo-Json -Depth 10 | Set-Content azure-export/keyvault-taskboard-kv.secrets.json

# readable on screen, one block per secret
$secrets | Format-List
```

For **names only** (no values — safe to keep around):

```powershell
$names | Set-Content azure-export/keyvault-taskboard-kv.secrets.txt
```

## The config-key mapping (important)

.NET config keys use `:` for hierarchy, but that character isn't allowed
everywhere, so two different escapes appear:

| Where | Escape for `:` | Example | Becomes |
|---|---|---|---|
| App Service **app setting** | `__` (double underscore) | `Jwt__Issuer` | `Jwt:Issuer` |
| **Key Vault** secret name | `--` (double dash) | `Jwt--Key` | `Jwt:Key` |

For the taskboard API this means the **JWT** config is split across both:

- `Jwt__Issuer`, `Jwt__Audience` (+ expiry) live in the **app settings**
  (`taskboard-06-api.settings.env`) → `Jwt:Issuer`, `Jwt:Audience`
- `Jwt--Key` (the signing key) lives in **Key Vault** → `Jwt:Key`

The app pulls the vault in through the Azure Key Vault configuration provider,
which maps `--` → `:` automatically. So to reproduce auth in any copy of the
environment you must recreate a `Jwt--Key` secret with the same value.

## Session cleanup — do you need to reset anything?

**Nothing persistent was changed**, so there's no required reset. Two things to be
aware of for hygiene:

1. **In-memory secret values.** `$names` and `$secrets` hold real secret values in
   your PowerShell session. Clear them when done (closing the window also clears
   them):

   ```powershell
   Remove-Variable names, secrets -ErrorAction SilentlyContinue
   ```

2. **Active subscription.** If you ran `az account set --subscription ...` to
   switch, that choice persists for future `az` commands. Check and restore if
   needed:

   ```powershell
   az account show --query name -o tsv
   az account set --subscription "<your-usual-subscription>"
   ```

No `$env:` environment variables are set by any of these commands, and `az`
stays logged in (that's fine — read-only).

## Protect the output — keep secrets out of git

`taskboard-06-api.settings.env`, `keyvault-*.secrets.json`, and
`keyvault-*.secrets.env` can contain **real secret values** (the JWT key,
connection strings). The included `.gitignore` excludes them. If you commit this
folder, double-check they are ignored:

```powershell
git status --ignored
```

Keep only `*.secrets.txt` (names) and the ARM/Bicep/inventory if you want a
committable, non-sensitive snapshot.

## Recreating the environment elsewhere (one command)

The provisioning scripts can rebuild the stack **and** re-apply the captured
settings + secrets in a single run — just point them at the files you exported:

```powershell
./Provision.ps1 -ResourceGroup rg-taskboard-copy `
  -ImportSecretsFile  azure-export/keyvault-taskboard-kv.secrets.json `
  -ImportSettingsFile azure-export/taskboard-06-api.settings.env
```
```bash
RESOURCE_GROUP=rg-taskboard-copy \
IMPORT_SECRETS_FILE=azure-export/keyvault-taskboard-kv.secrets.json \
IMPORT_SETTINGS_FILE=azure-export/taskboard-06-api.settings.env \
./provision.sh
```

This creates the new resource group, Linux F1 web app, serverless SQL, a Key
Vault, and a user-assigned identity — then seeds the vault with your secrets
(including `Jwt--Key`) and applies the API's app settings (`Jwt__Issuer`,
`Jwt__Audience`, etc.). `@kv:` values are re-pointed at the new vault
automatically.

**After the run:** if the app reads Key Vault via the configuration provider (as
taskboard does for `Jwt--Key`), confirm it points at the *new* vault. Update any
captured setting holding the old vault URI (e.g. `KeyVault__Uri`) — the scripts
print a reminder when an import runs.

Prefer to do it by hand instead? The manual equivalent:

1. `az deployment group create -g <new-rg> --template-file azure-export/rg-taskboard.bicep`
2. Apply the captured app settings to the new web app.
3. `az keyvault secret set --vault-name <new-kv> --name "Jwt--Key" --value "<value>"`
4. Point the new web app's Key Vault config at the new vault.

## Troubleshooting

**`Timeout waiting for token from portal. Audience: https://vault.azure.net`** —
Cloud Shell token broker. Restart the Cloud Shell session, or run locally.

**`...\azure-export\azure-export\... could not find part of the path`** — you're
already inside `azure-export`; drop the `azure-export/` prefix (or `cd ..`).

**`Forbidden` / `(403)` on Key Vault** — real permissions gap. Grant access:

```powershell
az role assignment create --role "Key Vault Secrets User" `
  --assignee <your-email> `
  --scope $(az keyvault show --name taskboard-kv --query id -o tsv)
```

(or `az keyvault set-policy --name taskboard-kv --upn <your-email> --secret-permissions get list`
if the vault uses access policies rather than RBAC).
