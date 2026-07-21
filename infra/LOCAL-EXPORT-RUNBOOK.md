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

## Step 0 — Secure the secrets folder in git FIRST

**Do all of this before the folder holds any secret.** The export writes **real
secret values** (the JWT signing key, SQL connection strings, the SQL admin
password) into `azure-export/`. Git only ignores files it isn't **already**
tracking, so the ignore rule must be committed **before** those files exist —
otherwise a secret can slip into a commit. The same steps apply to **any** folder
that will hold exported secrets; substitute its name for `azure-export/`.

### 1. Create (or append to) the repo's root `.gitignore`

From the repo root (creates the file if missing; add only the lines you don't
already have):

```powershell
@'
# Azure export output — may contain REAL secret values. Never commit.
azure-export/

# Belt-and-suspenders: ignore exported secret files by name anywhere in the repo.
*.secrets.json
*.secrets.env
*.settings.env
'@ | Add-Content -Path .gitignore
```

What it should contain, at minimum:

- **`azure-export/`** — the trailing `/` ignores the whole output directory and
  everything under it. Put it in the **root** `.gitignore` so it applies no matter
  where you run the export from. (Want to keep a *non-secret* snapshot —
  ARM/Bicep/inventory — committable instead? Ignore only the sensitive files; see
  [Protect the output](#protect-the-output--keep-secrets-out-of-git).)
- **`*.secrets.json` / `*.secrets.env` / `*.settings.env`** — a by-name safety net,
  so an exported secret file is ignored even if it ever lands outside
  `azure-export/`.

> A fuller template — the toolkit's own bundled file — is shown in the README:
> [Example — the bundled `.gitignore`](README.md#example--the-bundled-gitignore).

### 2. Commit and push the `.gitignore` FIRST — before any secret exists

This is the whole reason for the ordering: put the guard in place **and share it**
while the folder is still empty, so the commit is guaranteed safe.

```powershell
git add .gitignore
git commit -m "chore: ignore azure-export secrets before exporting"
git push
```

### 3. Verify the ignore is active — before you export

Ask git directly:

```powershell
git check-ignore -v azure-export/keyvault-taskboard-kv.secrets.json
```

Git should echo the matching rule (e.g. `.gitignore:NN:azure-export/`). **No output
means the folder is NOT ignored** — fix the rule, re-commit, and re-check before
continuing.

### 4. Only now create / export the sensitive files

Run the export (Steps 1-4 below). The secret files are written into the
already-ignored folder.

### 5. Sanity-check on every later commit

```powershell
git status            # exported secret files must NOT be listed for commit
git status --ignored  # they should appear under "Ignored files"
```

> ⚠️ **Never force past the ignore.** `git add -f` / `git add --force` overrides
> `.gitignore` and stages the file anyway — the most common way an ignored secret
> still gets committed. If you genuinely need a *non-secret* file out of an ignored
> folder, add a specific allow rule (e.g. `!azure-export/inventory.md`) — never
> force-add a secrets folder.

> **This repo is already set up:** its root `.gitignore` already contains
> `azure-export/` (committed and pushed), so exports here are ignored out of the
> box — sub-steps 1-2 are already done. Repeat them when you point the toolkit at a
> **different** repo or a **differently-named** output folder.

> **Already committed a secret?** Adding it to `.gitignore` afterward does nothing —
> git keeps tracking a file it already tracks. Untrack it (`git rm --cached <file>`),
> **rotate the secret** (treat it as compromised), and scrub it from history with
> `git filter-repo` (or the BFG) before pushing.

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

> The **first** thing to do — ignoring the folder *before* you export — is [Step 0](#step-0--secure-the-secrets-folder-in-git-first). This section is about keeping a *non-secret* snapshot committable.

`taskboard-06-api.settings.env`, `keyvault-*.secrets.json`, and
`keyvault-*.secrets.env` can contain **real secret values** (the JWT key,
connection strings). The included `.gitignore` excludes them. If you commit this
folder, double-check they are ignored:

```powershell
git status --ignored
```

Keep only `*.secrets.txt` (names) and the ARM/Bicep/inventory if you want a
committable, non-sensitive snapshot.

## Scan for committed secrets (gitleaks / trufflehog)

`.gitignore` stops *new* mistakes; a secret scanner catches anything already in
**git history**. Run one of these from the repo root before you push — both scan
committed history, so your untracked `azure-export/` is never read.

### gitleaks

Install (pick one):

```powershell
winget install gitleaks.gitleaks
# or:  scoop install gitleaks   |   choco install gitleaks
# or a binary from https://github.com/gitleaks/gitleaks/releases
```

If `gitleaks` isn't found right after install, reopen the shell (winget updates
`PATH`) or refresh it in-session:

```powershell
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')
```

Scan the full history:

```powershell
gitleaks git . --redact --verbose --report-path gitleaks-report.json
```

`no leaks found` (exit code 0) = clean. Otherwise it writes redacted findings to
`gitleaks-report.json` (exit code 1); review it, then delete it
(`Remove-Item gitleaks-report.json`) — never commit the report. On older gitleaks
the command is `gitleaks detect --source . --redact`.

### trufflehog

Install (pick one):

```powershell
scoop install trufflehog        # or:  choco install trufflehog
# or a binary from https://github.com/trufflesecurity/trufflehog/releases
```

Scan the full history (add `--only-verified` to show only confirmed-live secrets):

```powershell
trufflehog git file://.
```

Or with Docker, nothing to install:

```powershell
docker run --rm -v "${PWD}:/repo" trufflesecurity/trufflehog:latest git file:///repo
```

> **Use the `git` modes above.** They scan committed history, so the untracked
> `azure-export/` secrets are excluded. A **filesystem** scan
> (`gitleaks ... --no-git` or `trufflehog filesystem .`) *will* read `azure-export/`
> — only run those with that folder excluded.

**Make it automatic:** add gitleaks as a pre-commit hook and/or a CI step so a
future secret is blocked before it can ever be committed.

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
