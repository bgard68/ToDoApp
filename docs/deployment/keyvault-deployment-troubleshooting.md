# Azure Deployment & Key Vault — Troubleshooting Log

A complete, chronological record of the effort to get **ToDoApp's .NET API** running on Azure App
Service with its **JWT signing key in Azure Key Vault** — including every symptom, how it was
diagnosed, the log techniques used (Log Stream, Kudu, the VFS API, container/`docker.log`), the root
causes, and every PowerShell / `az` / SQL command run along the way.

> **← Back to the main [README](../../README.md).**

---

## 1. Goal

Move the API's one real secret — the **JWT signing key** — out of App Service application settings and
into **Azure Key Vault**, read at runtime via the App Service **managed identity**, so the deployed app
stores **no secrets anywhere** (the database was already passwordless via managed identity).

Design already in the code (`src/TodoApp.WebApi/Program.cs`): Key Vault is registered as a
**configuration source**, gated on a `KeyVault:Uri` value being present, so it is skipped entirely
when unset (local dev / CI) and activates only in Azure.

```csharp
var keyVaultUri = builder.Configuration["KeyVault:Uri"];
if (!string.IsNullOrWhiteSpace(keyVaultUri))
{
    builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), new ManagedIdentityCredential());
}
```

---

## 2. What we ended with (final architecture)

- **API** — `taskboard-06-api` on App Service (Linux, .NET 10), deployed by a **GitHub Actions**
  pipeline that publishes **only the WebApi project**.
- **Database** — Azure SQL, **passwordless** via managed identity (`Authentication=Active Directory Default`).
- **JWT signing key** — stored in **Key Vault** (`taskboard-kv`, secret `Jwt--Key`), read via the
  app's managed identity holding the **Key Vault Secrets User** role. No `Jwt__Key` app setting.
- **Frontend** — React SPA on Static Web Apps, `VITE_API_URL` pointing at the API over `https://`.

The big realisation: **almost none of the pain was Key Vault.** The real blockers were (a) the API had
**no deployment pipeline at all**, and (b) once one existed, it **published the whole solution** into
`wwwroot`, which stopped the app from serving. Key Vault itself went in cleanly once the app was
healthy.

---

## 3. TL;DR — the root causes, in order of discovery

1. **The deployed build was stale.** Crash stack traces pointed at `Program.cs:line 31`, but the
   Key-Vault-aware code put the failing call at line ~48/57. Line 31 = *old code with no Key Vault
   block*.
2. **The only GitHub Actions workflow deployed the *frontend* (Static Web Apps), not the API.** So
   every "redeploy through GitHub" updated the SPA and never touched the API — the API sat on a build
   from days earlier.
3. **The auto-generated API pipeline published the entire solution.** `dotnet publish` with no project
   dumped test DLLs, multiple entry points, and `hostingstart.html` into `wwwroot`; the app started but
   **served no routes** (every request 404'd; Azure served the placeholder page).
4. **`DefaultAzureCredential` hung on App Service**, contributing to 230-second container-start
   timeouts → switched to `ManagedIdentityCredential`.
5. **`WEBSITE_RUN_FROM_PACKAGE=1` is a Windows pattern** — setting it on Linux stopped the app from
   launching at all (a wrong turn we reverted).
6. **Couldn't clean the polluted `wwwroot`** (Kudu SSH hung; deploys overlay instead of cleaning), so
   the fix was to **recreate the App Service fresh**.
7. **`VITE_API_URL` was missing `https://`** → the browser appended the API host as a *path* under the
   SPA origin → **405**.
8. **Serverless SQL cold start** → an occasional **500** on the first request after idle; rides out on
   retry.

---

## 4. Chronological troubleshooting log

Each entry: **Symptom → How we looked → What we found → Fix**, with the exact commands.

### 4.1 Local: `401 … "The signature key was not found"`

**Symptom:** logged in locally (200), then a protected call returned `401` with
`www-authenticate: Bearer error="invalid_token", error_description="The signature key was not found"`.

**Meaning:** the token was signed with a *different* key than the one validating it — a key mismatch,
not a bug (the app fail-fasts if `Jwt:Key` is missing, so a running app *has* a key).

**How we looked (Windows PowerShell):**
```powershell
# Is a Jwt__Key env var overriding user-secrets? (env vars win over user-secrets in ASP.NET Core)
$env:Jwt__Key
[Environment]::GetEnvironmentVariable('Jwt__Key','User')
[Environment]::GetEnvironmentVariable('Jwt__Key','Machine')
printenv Jwt__Key          # same check from a bash/WSL shell — prints nothing if not set

# Are we even in Development? (user-secrets only load when ASPNETCORE_ENVIRONMENT=Development)
$env:ASPNETCORE_ENVIRONMENT

# What key is actually stored for the project?
dotnet user-secrets list --project .\src\TodoApp.WebApi
```

**Clearing a stray override** (an env var was shadowing the user-secret):
```powershell
Remove-Item Env:\Jwt__Key                 -ErrorAction SilentlyContinue
Remove-Item Env:\ASPNETCORE_ENVIRONMENT   -ErrorAction SilentlyContinue   # let it default, or set to Development
```

**Generating / pinning a signing key** — three variants we used across shells (all produce a base64 key):
```powershell
# a) PowerShell, no crypto types (works everywhere, incl. Windows PowerShell 5.1):
[Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Maximum 256 }))

# b) Windows PowerShell 5.1 with the RNG (note: 5.1 has no static GetBytes(int) — use Create().GetBytes):
$b = New-Object byte[] 48; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b)

# c) .NET / PowerShell 7 (the static overload that only exists on modern .NET):
[Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(48))

# bash equivalent:
openssl rand -base64 48
```

**Pinning it into user-secrets so it survives restarts** (an ad-hoc key per run invalidates old tokens):
```powershell
dotnet user-secrets set "Jwt:Key" "<paste-a-long-random-value>" --project .\src\TodoApp.WebApi
# (bash) dotnet user-secrets set "Jwt:Key" "$(openssl rand -base64 48)" --project src/TodoApp.WebApi
```

**Root causes seen:** a stale token from an earlier run (different key), or `ASPNETCORE_ENVIRONMENT`
not being `Development` (user-secrets only load in Development), or a leftover env var overriding the
secret.

**Fix / clean local test:** run the API, get a **fresh** token, use it immediately:
```powershell
$base = "http://localhost:5080"
$body = '{"email":"demo@todoapp.local","password":"Password123!"}'
$login = Invoke-RestMethod -Uri "$base/api/auth/login" -Method Post -ContentType "application/json" -Body $body
$token = $login.accessToken
Invoke-RestMethod -Uri "$base/api/categories" -Headers @{ Authorization = "Bearer $token" }
```

Gotchas learned here:
- **PowerShell:** don't paste multi-line commands with backtick continuations — pasting splits them and
  the request loses its body (`415 Unsupported Media Type`). Keep each call on one line.
- **Swagger** is disabled in Production (`if (app.Environment.IsDevelopment())`), so `/swagger` returns
  a **404 in prod by design** — a 404 there means the app *is* responding.
- The Swagger **Authorize** box takes the **raw token, no `Bearer ` prefix**.

### 4.2 Deployed: `500`, then `No instances found`, then various states

**Symptoms and what each meant:**

| What the browser/portal showed | Meaning |
| ------------------------------ | ------- |
| `HTTP 500` on login | app running but the request threw (often DB cold start) |
| `No instances found` (Log Stream) | no running worker — app crashed on startup or is stopped |
| `403 – This web app is stopped` | the App Service was **stopped** (Kudu is also 403 when stopped) |
| `:( Application Error` | container running but the app **crashed** on startup |
| `Your web app is running and waiting for your content` | the default **`hostingstart.html` placeholder** — the app isn't serving |
| Container exit code **134** | `SIGABRT` — .NET **unhandled exception** aborted the process |
| `ContainerTimeout … 230s` | the container **hung** (didn't bind a port in time) |

**First real clue — the fail-fast exception:**
```
Unhandled exception. System.InvalidOperationException: Jwt:Key must be configured and at least 32 bytes …
   at TodoApp.WebApi.Authentication.AuthenticationSetup.AddJwtAuthentication(...) in …AuthenticationSetup.cs:line 21
   at Program.<Main>$(String[] args) in …Program.cs:line 31
```
The **`Program.cs:line 31`** was the giveaway: the Key-Vault-aware `Program.cs` has that call much
lower (line ~48/57). **Line 31 = old code.** The build running in Azure predated the Key Vault change.

### 4.3 How we read the logs (the toolbox)

Log Stream was useless here (it shows *nothing* when there's "no instances"), and **Kudu SSH / Debug
console hung** whenever the app was down (its container can't start either). What actually worked was
the **Kudu VFS API in the browser** — plain `GET` URLs we typed straight into the address bar, no
console, no `az`, no SSH. The whole technique is **URL surgery**: start from the app's own address and
*edit it* into a log-browsing address, then walk the filesystem by hand-editing the path.

**Step 1 — turn the app URL into the Kudu (SCM) URL by editing the hostname.**
Every App Service has a companion **Kudu / SCM** site at the same hostname with **`.scm`** inserted
right after the app-name segment. So you take the app address and splice `.scm` in:

```
app:   https://taskboard-05-api-<hash>.centralus-01.azurewebsites.net
                                  ▲ insert ".scm" here
Kudu:  https://taskboard-05-api-<hash>.scm.centralus-01.azurewebsites.net
```

> **Important:** newer App Services use a **regional hostname** (`<app>-<hash>.<region>-01.azurewebsites.net`).
> The short `taskboard-05-api.azurewebsites.net` does **not** resolve — and neither does
> `taskboard-05-api.scm.azurewebsites.net`. You must keep the `-<hash>.centralus-01` part in **both** the
> app host and the `.scm` host. The reliable way to get the exact SCM URL without guessing the hash:
> portal → the app → **Advanced Tools (Kudu) → Go**; it opens `https://<app>-<hash>.scm.<region>-01.azurewebsites.net`.
> Copy that root and append the `/api/vfs/...` paths below. No separate login is needed — the browser
> session you used for the portal already authenticates Kudu (that's also why a **`403`** here means the
> *app is stopped*, not that you're unauthorized).

**Step 2 — append the VFS API path to browse the filesystem.** Kudu exposes the app's disk over a REST
"virtual file system" endpoint at **`/api/vfs/`**. A URL that **ends in `/`** lists a **directory** (as
JSON); a URL that ends in a **filename** returns that **file's raw bytes**. You navigate purely by
editing the path — no clicking, just changing the URL:

```
# the log directory — lists every log file as JSON (name, mtime, size, href):
https://taskboard-05-api-<hash>.scm.centralus-01.azurewebsites.net/api/vfs/LogFiles/
```
The newest **`*_docker.log`** (highest `mtime`, still-growing `size`) is the live container log.

**Step 3 — open one file by pasting its name into the path.** Copy the `name` from that JSON listing and
replace the trailing `/` (the directory) with the filename — same URL, deeper path:

```
# was:  …/api/vfs/LogFiles/
# now:  …/api/vfs/LogFiles/<filename>       ← drilled one level in by editing the path
https://taskboard-05-api-<hash>.scm.centralus-01.azurewebsites.net/api/vfs/LogFiles/2026_07_17_10-30-0-214_docker.log
```
The browser renders the raw log as plain text, so you just **Ctrl+F** in the page to search it. (Same
idea for nested folders — e.g. `…/api/vfs/LogFiles/kudu/trace/` for deployment traces: keep the trailing
`/` to list, drop it to a filename to read.)

Search terms we relied on:
- **`Now listening`** / `Application started` — proves the ASP.NET host actually bound. **Its absence**
  (with no exception either) meant the .NET app wasn't launching and Azure was serving the placeholder.
- **`xception`**, **`Jwt:Key`**, **`Program.cs:line`** — the actual startup exception and its line.
- **`exit code 134`**, **`ContainerTimeout`** — crash vs hang.
- **`Site is running with deployment version: <guid>`** — which deployment is live (a *new* guid after a
  real deploy; an unchanged guid means the deploy didn't land).

**Inspect what's actually deployed (`wwwroot`) — same trick, different path.** To look at the deployed
files instead of the logs, keep the identical host and just swap the path segment after `/api/vfs/`
from `LogFiles/` to `site/wwwroot/`:
```
# logs were:  …/api/vfs/LogFiles/
# app files:  …/api/vfs/site/wwwroot/     ← same host, edited path
https://<app>-<hash>.scm.centralus-01.azurewebsites.net/api/vfs/site/wwwroot/
```
Again a trailing `/` lists the directory as JSON (every DLL with its `mtime` and `size`), and dropping to
a filename would download that file. Two things this told us:
- **`TodoApp.WebApi.dll` `mtime`** — was days old (July 15) despite July-17 pushes → **the deploy
  wasn't landing.** Also the DLL **size changed** (49,152 → 49,664 bytes) once the Key Vault code
  finally deployed.
- **Presence of `Azure.Identity.dll` / `Azure.Extensions.AspNetCore.Configuration.Secrets.dll`** →
  confirmed the Key-Vault packages were (or weren't) in the build.
- **Presence of `TodoApp.UnitTests.dll`, `TodoApp.IntegrationTests.dll`, `xunit.*`,
  `FluentAssertions.dll`, `testhost.dll`, `hostingstart.html`** → proved the **whole solution** had
  been published into `wwwroot` (the pollution).

**Other log routes we tried:**
- Portal → **Diagnose and solve problems** → *Application Logs* / *Container Crash* (in-browser; only
  startup failures captured unless App Service Logs are on).
- **Live log stream** (only useful once an instance is actually up):
  ```powershell
  az webapp log tail --resource-group rg-taskboard --name taskboard-05-api
  ```
- **Download the logs and read them locally** (worked, but the local `Expand-Archive` choked on the zip —
  noise, not signal; the `tail` one-liner is the bash/Cloud-Shell version that did work):
  ```powershell
  az webapp log download -g rg-taskboard -n taskboard-05-api --log-file logs.zip
  Expand-Archive -Path logs.zip -DestinationPath logs -Force        # this is what choked on Windows
  ```
  ```bash
  az webapp log download -g rg-taskboard -n taskboard-05-api --log-file logs.zip && unzip -o logs.zip -d logs && tail -n 40 logs/LogFiles/*_docker.log
  ```
- Kudu **Advanced Tools → Go** to reach the SCM site (it gives the correct regional URL to then append
  `/api/vfs/...` to).
- Handy while flailing — confirm the app exists / its state / its real hostname:
  ```powershell
  az webapp list -o table
  az webapp show -g rg-taskboard -n taskboard-06-api --query defaultHostName -o tsv
  ```

### 4.4 Root cause #1 — the deploy wasn't landing (stale build)

**How we proved it:** `TodoApp.WebApi.dll` `mtime` in `wwwroot` stayed at **July 15** while we pushed
on July 17, and crashes kept pointing at `line 31` (old code).

**Then we checked GitHub Actions** — and found the deploy job **"skipped"**, and that the only workflow
was:
```yaml
name: Azure Static Web Apps CI/CD
on:
  push: { branches: [ main ] }
jobs:
  build_and_deploy_job:
    steps:
      - uses: Azure/static-web-apps-deploy@v1
        with:
          app_location: "./frontend"   # ← FRONTEND only
          output_location: "dist"
```

**Root cause:** this workflow deploys the **React frontend** to Static Web Apps. **There was no
pipeline for the .NET API at all.** Every "redeploy through GitHub" only rebuilt the SPA; the API stayed
on whatever was last hand-deployed (July 15).

### 4.5 Root cause #2 — the API pipeline published the whole solution

We created an API pipeline via **Deployment Center → GitHub** (User-assigned identity / OIDC). Its
auto-generated workflow had:
```yaml
- run: dotnet build --configuration Release
- run: dotnet publish -c Release -o ${{env.DOTNET_ROOT}}/myapp   # ← no project specified
```
In a multi-project repo, `dotnet publish` with no project publishes **everything** — test projects,
class libraries, and `hostingstart.html` — into one folder. `wwwroot` ended up with test DLLs and
multiple runnable entry points, and the app **started but served no routes** (`/api/auth/login` and
`/swagger` both 404; `Now listening` never appeared → Azure served the placeholder).

**The fix (targets only the API):**
```yaml
- run: dotnet build   src/TodoApp.WebApi/TodoApp.WebApi.csproj --configuration Release
- run: dotnet publish src/TodoApp.WebApi/TodoApp.WebApi.csproj -c Release -o ${{env.DOTNET_ROOT}}/myapp
```

### 4.6 Root cause #3 — `DefaultAzureCredential` hang → `ManagedIdentityCredential`

`DefaultAzureCredential` probes ~8 credential sources in sequence; on App Service those probes stall and
the stacked timeouts blew the **230-second** container-start limit (a *hang*, not a clean error).

**Fix — target the managed identity directly** (fast, fails fast with a real error):
```csharp
new ManagedIdentityCredential()   // instead of new DefaultAzureCredential()
```

### 4.7 Wrong turn — `WEBSITE_RUN_FROM_PACKAGE=1`

Tried to shadow the polluted `wwwroot` by mounting the deploy package. **On Linux App Service this is
unsupported** (it's a Windows pattern) and it stopped the app from launching entirely (no .NET output at
all in `docker.log`). **Reverted it** (deleted the setting).
```powershell
# the wrong turn:
az webapp config appsettings set    -g rg-taskboard -n taskboard-05-api --settings "WEBSITE_RUN_FROM_PACKAGE=1"
# the revert:
az webapp config appsettings delete -g rg-taskboard -n taskboard-05-api --setting-names WEBSITE_RUN_FROM_PACKAGE
az webapp restart -g rg-taskboard -n taskboard-05-api
```

**Manual zip-deploy path (how the pre-pipeline builds got there).** Before there was any GitHub pipeline
for the API, the app was hand-deployed by publishing the WebApi project locally, zipping it, and pushing
with `az webapp deploy`. This is also how the **stale July-15 build** came to be running — worth recording
because these are the commands to fall back on when CI is the thing that's broken:
```powershell
# publish ONLY the API project (never a bare `dotnet publish` — see §4.5):
dotnet publish src\TodoApp.WebApi\TodoApp.WebApi.csproj -c Release -o publish

# zip it — use tar, NOT Compress-Archive: Compress-Archive writes Windows backslashes that break Linux zip-deploy:
tar -a -c -f publish.zip -C publish .
# (the trap we hit first) Compress-Archive -Path publish\* -DestinationPath publish.zip -Force

# push the zip straight to the app:
az webapp deploy --resource-group rg-taskboard --name taskboard-05-api --src-path publish.zip --type zip
Remove-Item publish.zip -ErrorAction SilentlyContinue
```

### 4.8 The reset — recreate the App Service

`wwwroot` was polluted and couldn't be cleaned (SSH hung; `azure/webapps-deploy` overlays instead of
cleaning), so we rebuilt the API on a **fresh** App Service (`taskboard-06-api`) — see §5 for the exact
steps. A fresh, empty `wwwroot` + a WebApi-only publish = the app finally served routes and returned a
token.

### 4.9 Frontend `405` — missing `https://`

After repointing the SPA, login gave `405`. DevTools showed the request URL was:
```
https://salmon-field-…azurestaticapps.net/taskboard-06-api-…azurewebsites.net/api/auth/login
```
The API host had been appended as a **path** under the SPA origin — because `VITE_API_URL` was set
**without `https://`**, so the browser treated it as relative. **Fix:** set the variable to the full
`https://…` URL and re-run the Static Web Apps build (Vite inlines it at build time).

### 4.10 Transient `500` — serverless SQL cold start

Free-tier serverless Azure SQL auto-pauses after idle; the first request cold-starts and can `500`. It
rides out on the second try (the app also has EF `EnableRetryOnFailure` + `Connect Timeout=60`).

---

## 5. The clean rebuild (what actually fixed it) — API-only recreate

The frontend, SQL server/database, and Key Vault were **kept**; only the App Service was recreated.

**1. Create the Web App** (Portal → *Web App*, not *Static Web App*):
Resource group `rg-taskboard`, name `taskboard-06-api`, Publish **Code**, Runtime **.NET 10 (LTS)**,
OS **Linux**, Region **Central US**, reuse the existing plan (or Basic B1 — avoid Free F1's CPU quota).

**2. Turn on identity:** app → **Identity → System assigned → On**.

**3. Grant it in SQL** — find the DB, then run the grant as the **Microsoft Entra** admin in the SQL
database's **Query editor** (confirm you're connected as your Entra account — it returns your email as
the login if so):
```sql
SELECT SUSER_SNAME() AS LoginName, USER_NAME() AS DbUser;
```
then:
```sql
CREATE USER [taskboard-06-api] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [taskboard-06-api];
ALTER ROLE db_datawriter ADD MEMBER [taskboard-06-api];
ALTER ROLE db_ddladmin  ADD MEMBER [taskboard-06-api];
```
Finding the database name (Cloud Shell):
```powershell
az sql server list -g "rg-taskboard" --query "[0].name" -o tsv
az sql db list -g "rg-taskboard" -s "taskboard-05-sql" --query "[].name" -o tsv   # → taskboard, master
```

**4. App settings** (Cloud Shell PowerShell — the `$settings` array avoids paste/quoting issues):
```powershell
$RG = "rg-taskboard"; $NEW_APP = "taskboard-06-api"
$sqlServer = "taskboard-05-sql"; $sqlDb = "taskboard"
$conn = "Server=tcp:$sqlServer.database.windows.net,1433;Database=$sqlDb;Authentication=Active Directory Default;Encrypt=True;Connect Timeout=60;"
$jwt  = [Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Maximum 256 }))
$settings = @(
  "ASPNETCORE_ENVIRONMENT=Production",
  "Jwt__Issuer=TodoApp",
  "Jwt__Audience=TodoAppClient",
  "Jwt__Key=$jwt",
  "Database__Provider=SqlServer",
  "ConnectionStrings__DefaultConnection=$conn",
  "Cors__AllowedOrigins__0=https://salmon-field-054249810.7.azurestaticapps.net"
)
az webapp config appsettings set -g $RG -n $NEW_APP --settings $settings
```
> Note: `az webapp config appsettings set` prints `"value": null` for each setting — a **display
> quirk**. Verify the real values:
> ```powershell
> az webapp config appsettings list -g $RG -n $NEW_APP --query "[].{name:name, value:value}" -o table
> # spot-check a single setting (e.g. CORS origin):
> az webapp config appsettings list -g $RG -n $NEW_APP --query "[?name=='Cors__AllowedOrigins__0'].value" -o tsv
> ```
> Alternatively, **copy the settings across from the old app** and re-apply them (then edit what changed):
> ```powershell
> $old = az webapp config appsettings list -g $RG -n "taskboard-05-api" | ConvertFrom-Json
> # inspect $old, adjust names/values, then set them on $NEW_APP as above.
> ```
> If you fat-finger a setting name, delete the stray one:
> ```powershell
> az webapp config appsettings delete -g $RG -n $NEW_APP --setting-names OLDJwt__KeyOLD
> ```

**5. Deployment Center → GitHub** → repo, branch `main`, **User-assigned identity** (create new). Save.

**6. Fix the generated workflow** (`.github/workflows/main_taskboard-06-api.yml`) to publish only the
API (the §4.5 change), commit → clean deploy into the fresh, empty `wwwroot`.

**7. Test the API directly:**
```powershell
$body = '{"email":"demo@todoapp.local","password":"Password123!"}'
Invoke-RestMethod -Uri "https://taskboard-06-api-<hash>.centralus-01.azurewebsites.net/api/auth/login" -Method Post -ContentType "application/json" -Body $body
# → returns an accessToken  ✅
```

**8. Repoint the frontend:** GitHub → Settings → Secrets and variables → Actions → **Variables** →
`VITE_API_URL = https://taskboard-06-api-<hash>.centralus-01.azurewebsites.net` (with `https://`, no
trailing slash) → re-run the **Static Web Apps** workflow → hard-refresh the site.

**9. Delete the old app** (`taskboard-05-api`).

---

## 6. Adding Key Vault (cleanly, on the healthy app)

**A0. Put the secret in the vault first.** Under the RBAC permission model you must grant **yourself**
*Key Vault Secrets Officer* before the portal (or CLI) will let you add a secret — being subscription
Owner is not enough for data-plane operations:
```powershell
# grant yourself write access to secrets (inline sub-queries resolve your object id and the vault id):
az role assignment create `
  --assignee (az ad signed-in-user show --query id -o tsv) `
  --role "Key Vault Secrets Officer" `
  --scope (az keyvault show -n taskboard-kv --query id -o tsv)

# then create the secret (name uses a double DASH → maps to Jwt:Key; value is a fresh base64 key):
az keyvault secret set --vault-name taskboard-kv -n "Jwt--Key" --value "<random-base64-key>"
```
(If you ever need to remove it: `az keyvault secret delete --vault-name taskboard-kv -n "Jwt--Key"`.)

**A. Grant the *app's* identity read access, then wait ~5 min for RBAC to propagate:**
```powershell
$RG = "rg-taskboard"; $APP = "taskboard-06-api"; $KV = "taskboard-kv"
$principal = az webapp identity show -g $RG -n $APP --query principalId -o tsv
$kvId      = az keyvault show -n $KV --query id -o tsv
az role assignment create --assignee $principal --role "Key Vault Secrets User" --scope $kvId
# verify (also works with the literal principal id in place of $principal):
az role assignment list --assignee $principal --scope $kvId --query "[].roleDefinitionName" -o tsv
# → Key Vault Secrets User
```

**B. Point the app at the vault (keep `Jwt__Key` as a fallback for the test), restart, test:**
```powershell
az webapp config appsettings set -g $RG -n $APP --settings "KeyVault__Uri=https://taskboard-kv.vault.azure.net/"
az webapp restart -g $RG -n $APP
# wait ~60s
$body = '{"email":"demo@todoapp.local","password":"Password123!"}'
Invoke-RestMethod -Uri "https://taskboard-06-api-<hash>.centralus-01.azurewebsites.net/api/auth/login" -Method Post -ContentType "application/json" -Body $body
```

**C. Prove the vault is the source — remove the fallback, restart, test again:**
```powershell
az webapp config appsettings delete -g $RG -n $APP --setting-names Jwt__Key
az webapp restart -g $RG -n $APP
# wait ~60s, test again → still a token  ✅  → the key now comes from Key Vault
```

**Instant revert if the vault ever fails** (falls back to a plain key, drops the vault):
```powershell
$jwt = [Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Maximum 256 }))
az webapp config appsettings set -g $RG -n $APP --settings "Jwt__Key=$jwt"
az webapp config appsettings delete -g $RG -n $APP --setting-names KeyVault__Uri
az webapp restart -g $RG -n $APP
```

**Key Vault facts confirmed along the way:**
- Use a **Vault (Standard tier)**, **not Managed HSM** — Managed HSM stores *keys*, not *secrets*, so it
  can't hold `Jwt--Key`.
- The vault must be **RBAC** permission model (Access configuration), or role assignments are ignored.
- The secret name is **`Jwt--Key`** (double **dash** — Key Vault forbids underscores; `--` maps to `:`).
- The app setting is **`KeyVault__Uri`** (double **underscore** — env-var convention for `:`).
- Under RBAC you also need **Key Vault Secrets Officer** on *yourself* to add secrets in the portal.

---

## 7. Command reference

**Read logs / inspect the deployed app (browser — no console needed).**
Get the SCM host by inserting **`.scm`** after the app name in the app's own URL (portal → app →
**Advanced Tools → Go** gives it exactly). Then edit only the path after `/api/vfs/`: a trailing `/`
lists a directory as JSON, a filename returns the raw file.
```
# app URL:  https://<app>-<hash>.centralus-01.azurewebsites.net
# SCM URL:  https://<app>-<hash>.scm.centralus-01.azurewebsites.net   (".scm" inserted)

# list log files (directory → JSON):
https://<app>-<hash>.scm.centralus-01.azurewebsites.net/api/vfs/LogFiles/
# open one docker log (filename → raw text; Ctrl+F for: Now listening | xception | Jwt:Key | exit code 134 | deployment version):
https://<app>-<hash>.scm.centralus-01.azurewebsites.net/api/vfs/LogFiles/<newest>_docker.log
# inspect what was deployed — same host, path swapped LogFiles/ → site/wwwroot/ (DLL mtime, pollution, Azure.Identity.dll present?):
https://<app>-<hash>.scm.centralus-01.azurewebsites.net/api/vfs/site/wwwroot/
```

**Local diagnostics (Windows PowerShell):**
```powershell
$env:Jwt__Key                                             # env var overriding user-secrets?
[Environment]::GetEnvironmentVariable('Jwt__Key','User')
dotnet user-secrets list --project .\src\TodoApp.WebApi   # stored dev key
Remove-Item Env:\Jwt__Key -ErrorAction SilentlyContinue   # clear a stray override
[Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Maximum 256 }))  # generate a key
```

**Generate a JWT signing key (three shells):**
```powershell
[Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Maximum 256 }))                       # any PowerShell
$b=New-Object byte[] 48;[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b);[Convert]::ToBase64String($b)   # PS 5.1
[Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(48))          # PS 7 / .NET
```
```bash
openssl rand -base64 48
```

**Azure (Cloud Shell — local `az` was broken in the VS Developer PowerShell):**
```powershell
az login                                                                    # Cloud Shell auto-auths; run locally if needed
az webapp list -o table                                                     # find the app / confirm it exists
az webapp show    -g rg-taskboard -n <app> --query defaultHostName -o tsv   # regional hostname
az webapp restart -g rg-taskboard -n <app>
az webapp start   -g rg-taskboard -n <app>

# app settings
az webapp config appsettings list   -g rg-taskboard -n <app> -o table
az webapp config appsettings list   -g rg-taskboard -n <app> --query "[].{name:name, value:value}" -o table
az webapp config appsettings list   -g rg-taskboard -n <app> --query "[?name=='Cors__AllowedOrigins__0'].value" -o tsv
az webapp config appsettings set    -g rg-taskboard -n <app> --settings "KEY=VALUE"
az webapp config appsettings set    -g rg-taskboard -n <app> --settings "KeyVault__Uri=https://taskboard-kv.vault.azure.net/"
az webapp config appsettings set    -g rg-taskboard -n <app> --settings "Authentication__Google__ClientId=<client-id>"
az webapp config appsettings set    -g rg-taskboard -n <app> --settings Jwt__Key="$(openssl rand -base64 48)"   # bash
az webapp config appsettings delete -g rg-taskboard -n <app> --setting-names KEY
$old = az webapp config appsettings list -g rg-taskboard -n <old-app> | ConvertFrom-Json   # copy settings across

# identity + logs
az webapp identity show -g rg-taskboard -n <app> --query principalId -o tsv
az webapp log tail      -g rg-taskboard -n <app>
az webapp log download  -g rg-taskboard -n <app> --log-file logs.zip

# manual zip-deploy (fallback when CI is broken)
dotnet publish src/TodoApp.WebApi/TodoApp.WebApi.csproj -c Release -o publish
tar -a -c -f publish.zip -C publish .                                       # NOT Compress-Archive (backslashes break Linux)
az webapp deploy -g rg-taskboard -n <app> --src-path publish.zip --type zip

# Key Vault
az keyvault show   -n taskboard-kv --query id -o tsv
az keyvault secret set    --vault-name taskboard-kv -n "Jwt--Key" --value "<random>"
az keyvault secret delete --vault-name taskboard-kv -n "Jwt--Key"
az role assignment create --assignee (az ad signed-in-user show --query id -o tsv) --role "Key Vault Secrets Officer" --scope (az keyvault show -n taskboard-kv --query id -o tsv)
az role assignment create --assignee <principal> --role "Key Vault Secrets User"    --scope <kvId>
az role assignment list   --assignee <principal> --scope <kvId> --query "[].roleDefinitionName" -o tsv
```

**SQL (Query editor, as the Entra admin):**
```sql
SELECT SUSER_SNAME() AS LoginName, USER_NAME() AS DbUser;   -- confirm you're the Entra admin
CREATE USER [<app-name>] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [<app-name>];
ALTER ROLE db_datawriter ADD MEMBER [<app-name>];
ALTER ROLE db_ddladmin   ADD MEMBER [<app-name>];
```

**Find the SQL server / database (Cloud Shell):**
```powershell
az sql server list -g rg-taskboard --query "[0].name" -o tsv
az sql db list -g rg-taskboard -s taskboard-05-sql --query "[].name" -o tsv   # → taskboard, master
```

**Login test (PowerShell — each `Invoke-RestMethod` on ONE line):**
```powershell
$body = '{"email":"demo@todoapp.local","password":"Password123!"}'
Invoke-RestMethod -Uri "https://<host>/api/auth/login" -Method Post -ContentType "application/json" -Body $body
```

**Login test (bash / curl):**
```bash
curl -s -X POST "https://<host>/api/auth/login" \
  -H 'Content-Type: application/json' \
  -d '{"email":"demo@todoapp.local","password":"Password123!"}'
```

---

## 8. Lessons learned

- **A green GitHub run ≠ your app deployed.** Confirm *what* deployed: check the `wwwroot` DLL `mtime`,
  the crash's `Program.cs` line number, and the `deployment version` guid in `docker.log`.
- **Match the pipeline to the resource.** A Static Web Apps workflow deploys the SPA; the API needs its
  *own* pipeline. One app's workflow will not deploy another.
- **Publish the project, not the solution.** `dotnet publish <project>.csproj` — never a bare
  `dotnet publish` in a multi-project repo, or test DLLs pollute `wwwroot` and can stop the app serving.
- **Read logs via the Kudu VFS API in the browser** when Log Stream shows nothing and SSH hangs. It's
  pure URL surgery: insert **`.scm`** after the app name to reach Kudu, then edit the path after
  `/api/vfs/` — a trailing `/` lists a folder as JSON, a filename returns the raw file. Point it at
  `LogFiles/` for logs or `site/wwwroot/` for the deployed files. Search `docker.log` for `Now listening`
  (bound?), `xception`/`Jwt:Key` (why it crashed), `exit code 134` (SIGABRT) vs `ContainerTimeout` (hang).
- **Use `ManagedIdentityCredential` in Azure**, not `DefaultAzureCredential`, to avoid credential-chain
  hangs at startup.
- **`WEBSITE_RUN_FROM_PACKAGE=1` is Windows-only** — don't set it on Linux App Service.
- **Regional hostnames** (`<app>-<hash>.<region>-01.azurewebsites.net`) — the short name may not
  resolve, for the app *and* its `.scm.` Kudu host.
- **`VITE_*` are build-time.** Changing the variable does nothing until the SPA is rebuilt, and
  `VITE_API_URL` must include `https://` or the browser treats it as a relative path (→ 405).
- **Serverless SQL cold-starts** cause an occasional first-request 500 — expected; retry.
- **When a deployment's state is too tangled to clean** (SSH won't open, deploys overlay), **recreate
  the App Service** — it's faster and more reliable than fighting the corrupted folder. Keep the SQL
  server, Key Vault, and Static Web App; only rebuild the API.

---

> **← Back to the main [README](../../README.md).**
