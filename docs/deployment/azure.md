# Azure Deployment — Start-to-Finish Runbook

A single ordered pass that takes TaskBoard from nothing to a fully working Azure deployment: the
**.NET API** on App Service, **Azure SQL** with passwordless managed identity, the **React SPA** on
Static Web Apps, **Google sign-in**, **CORS**, and the **JWT signing key in Key Vault** — with no
secrets committed anywhere.

Follow it top to bottom. Each phase depends on the ones before it, and the ordering is deliberate — it
avoids the chicken-and-egg traps (the managed identity must exist before the SQL grant; the SPA URL must
exist before Google/CORS; the new build must be deployed before you'd ever remove a plaintext key).

For the Dapper data layer and how the schema is created on Azure SQL, see
[Infrastructure](../infrastructure.md); for the CI/CD workflows and branch-choice deploys, see
[Workflows](../workflows.md).

> **← Back to the main [README](../../README.md).**

---

## Deploying the `refactor/dapper` branch

This branch (Dapper) and `main` (EF Core) deploy to the **same** App Service, so whichever you deploy
last is live. The runbook below provisions the Azure environment once; after that, either branch ships
on demand — **nothing about the App Service configuration below changes**, because only the persistence
layer differs.

- **`main` (EF Core)** auto-deploys on every push (default pipeline behavior).
- **`refactor/dapper` (Dapper)** deploys via **Actions → "Build and Deploy taskboard-06-api" → Run
  workflow → select `refactor/dapper` → Run**. See
  [Workflows → branch-choice deploy](../workflows.md#the-ef--dapper-branch-choice-deploy).

Both branches use the **same Azure SQL database** (the App Service's `ConnectionStrings__DefaultConnection`).
That's safe because the Dapper schema matches EF's and the schema initializer's DDL is idempotent — it
reuses the existing tables. To switch branches, just deploy the other one; no settings change needed.

> ⚠️ This is a **production** deploy to the live App Service and real data — not a staging slot. To try
> Dapper in isolation, point a separate App Service (or the SQLite fallback under Phase 4) at its own
> database.

---

## What you end up with

```
Browser ──> Azure Static Web Apps (React SPA) ──HTTPS──> Azure App Service (.NET API) ──> Azure SQL
                                                              │                             (passwordless,
Google Identity (ID token) ──> POST /api/auth/google ────────┘                              managed identity)
                                                              │
                                              Jwt:Key ◄── Azure Key Vault (managed identity)
```

| Resource | Purpose | Secret stored? |
| -------- | ------- | -------------- |
| Resource group + App Service plan | hosting | — |
| App Service (Linux, .NET 10) | the API | none — managed identity for SQL + Key Vault |
| Azure SQL server + database | data | none — passwordless (Entra) |
| Static Web App | the SPA | none |
| Key Vault (Standard) | the JWT signing key | `Jwt--Key` (the one real secret) |

The SPA and API deploy independently; the browser calls the API cross-origin, so the API's CORS must
allow the SPA's origin (Phase 11). Google is only an identity source — the API still issues and revokes
its own JWTs.

---

## Phase 0 — Tools & login

```bash
az login
az account set --subscription "<your-subscription>"
npm install -g @azure/static-web-apps-cli   # SWA CLI
# also required locally: .NET 10 SDK, Node 18+, openssl
```

---

## Phase 1 — Shell variables

Set these once; every later command reuses them. Pick globally-unique names for the app, SQL server,
and vault.

```bash
RG=todoapp-rg
LOCATION=centralus
SWA_LOCATION=centralus                 # SWA supports a limited region list; Central US is fine
PLAN=todoapp-plan
API_APP=todoapp-api-$RANDOM            # -> <API_APP>.azurewebsites.net (must be globally unique)
SWA_APP=todoapp-web
SQL_SERVER=todoapp-sql-$RANDOM         # globally unique
SQL_DB=todoapp
KV=todoapp-kv-$RANDOM                  # globally unique

# Your own Entra identity — used as the SQL admin and to add Key Vault secrets
ADMIN_UPN=$(az ad signed-in-user show --query userPrincipalName -o tsv)
ADMIN_OID=$(az ad signed-in-user show --query id -o tsv)
```

---

## Phase 2 — Resource group + App Service plan + web app

```bash
az group create -n $RG -l $LOCATION
az appservice plan create -g $RG -n $PLAN --is-linux --sku B1
az webapp create -g $RG -p $PLAN -n $API_APP --runtime "DOTNETCORE:10.0"
```

> If `DOTNETCORE:10.0` isn't offered in your region yet, run `az webapp list-runtimes --os linux | grep
> DOTNET`, or deploy the container image (`Dockerfile.api` → ACR → App Service for Containers), or
> publish `--self-contained`.

**Enable Basic Auth publishing** (App Service ships with SCM Basic Auth *off*, which blocks
`az webapp deploy`):

```bash
az resource update -g $RG --namespace Microsoft.Web \
  --resource-type basicPublishingCredentialsPolicies \
  --name scm --parent sites/$API_APP --set properties.allow=true
```

---

## Phase 3 — Turn on the App Service managed identity

This identity authenticates to **both** SQL and Key Vault — no passwords anywhere. It must exist before
the SQL grant and the Key Vault role assignment, so do it now.

```bash
az webapp identity assign -g $RG -n $API_APP
PRINCIPAL_ID=$(az webapp identity show -g $RG -n $API_APP --query principalId -o tsv)
```

---

## Phase 4 — Azure SQL (passwordless)

The app runs on SQLite by default; setting `Database__Provider=SqlServer` plus a connection string
switches it to **Azure SQL** with no code change (the provider is chosen in `AddInfrastructure`). Create
an Entra-only SQL server (you as admin), the database, and open the firewall so you can run the grant.

```bash
az sql server create -g $RG -n $SQL_SERVER -l $LOCATION \
  --enable-ad-only-auth \
  --external-admin-principal-type User \
  --external-admin-name "$ADMIN_UPN" \
  --external-admin-sid "$ADMIN_OID"

# Serverless free-tier database (auto-pauses when idle)
az sql db create -g $RG -s $SQL_SERVER -n $SQL_DB \
  --edition GeneralPurpose --compute-model Serverless \
  --family Gen5 --capacity 1 --auto-pause-delay 60

# Allow Azure services + your current IP (for the Query editor)
az sql server firewall-rule create -g $RG -s $SQL_SERVER \
  -n AllowAzure --start-ip-address 0.0.0.0 --end-ip-address 0.0.0.0
```

**Grant the managed identity access to the database.** In the portal, open the `todoapp` database →
**Query editor**, sign in with **Microsoft Entra**, and run (the user name must match `$API_APP`):

```sql
CREATE USER [<API_APP>] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [<API_APP>];
ALTER ROLE db_datawriter ADD MEMBER [<API_APP>];
ALTER ROLE db_ddladmin  ADD MEMBER [<API_APP>];   -- lets the schema initializer create tables
```

Build the passwordless connection string (no user id or password in it):

```bash
SQL_CONNECTION="Server=tcp:${SQL_SERVER}.database.windows.net,1433;Database=${SQL_DB};Authentication=Active Directory Default;Encrypt=True;Connect Timeout=60;"
```

> `Authentication=Active Directory Default` makes the API connect as its managed identity, so no secret
> is stored anywhere. `Connect Timeout=60` rides out the serverless cold-start; the first request after
> idle may take ~30–60s while the database resumes, and the app retries transient failures.

Alternatives, if you don't want passwordless Azure SQL:

- **SQL auth** — copy the ADO.NET string from the portal (SQL **database → Connection strings →
  ADO.NET**) and replace `{your_password}`. It then contains a secret, so keep it only in app settings or
  Key Vault, never in the repo.
- **No Azure SQL** — omit `Database__Provider` and set
  `ConnectionStrings__DefaultConnection="Data Source=/home/todoapp.db"`; `/home` is App Service's
  persistent storage, so the SQLite file survives restarts (single instance only).

---

## Phase 5 — Key Vault for the JWT signing key

The signing key is the **only** real secret (the DB is passwordless and the Google client id is public).
Use a **Vault, Standard tier** — not Managed HSM, which stores keys, not secrets. The repo ships the
**configuration-provider** approach, activated by the `KeyVault__Uri` app setting.

```bash
# RBAC vault
az keyvault create -g $RG -n $KV -l $LOCATION --enable-rbac-authorization true

# Let YOURSELF write secrets (Contributor alone can't, under RBAC)
az role assignment create --assignee $ADMIN_OID \
  --role "Key Vault Secrets Officer" --scope $(az keyvault show -n $KV --query id -o tsv)

# Let the API's managed identity READ secrets
az role assignment create --assignee $PRINCIPAL_ID \
  --role "Key Vault Secrets User" --scope $(az keyvault show -n $KV --query id -o tsv)

# Store the key (double-dash maps to the config key Jwt:Key)
az keyvault secret set --vault-name $KV -n "Jwt--Key" --value "$(openssl rand -base64 48)"
```

> **Alternative — app-setting reference (no config provider).** Instead of `KeyVault__Uri`, App Service
> can resolve a `@Microsoft.KeyVault(...)` token at runtime while the app reads `Jwt:Key` unchanged:
> store the secret as `JwtKey`, grant the identity `get` on secrets, and set
> `Jwt__Key="@Microsoft.KeyVault(SecretUri=https://$KV.vault.azure.net/secrets/JwtKey/)"`.

---

## Phase 6 — Configure the API's app settings

Everything the API needs, in one call. Note there is **no `Jwt__Key`** here — Key Vault supplies it via
`KeyVault__Uri`. Google and CORS come later (Phases 9 and 11), once the SPA URL exists.

```bash
az webapp config appsettings set -g $RG -n $API_APP --settings \
  ASPNETCORE_ENVIRONMENT="Production" \
  Jwt__Issuer="TodoApp" \
  Jwt__Audience="TodoAppClient" \
  Database__Provider="SqlServer" \
  ConnectionStrings__DefaultConnection="$SQL_CONNECTION" \
  KeyVault__Uri="https://$KV.vault.azure.net/"
```

---

## Phase 7 — Build & deploy the API

```bash
dotnet publish src/TodoApp.WebApi/TodoApp.WebApi.csproj -c Release -o ./publish
(cd publish && zip -r ../api.zip .)
az webapp deploy -g $RG -n $API_APP --src-path api.zip --type zip

# If it doesn't start, pin the entry point:
az webapp config set -g $RG -n $API_APP --startup-file "dotnet TodoApp.WebApi.dll"
```

On first startup the **schema initializer** runs the idempotent DDL to build the schema and seeds the
demo user (see [Infrastructure → Schema management](../infrastructure.md#schema-management)). **Verify:**

```bash
API_HOST=$(az webapp show -g $RG -n $API_APP --query defaultHostName -o tsv)
curl -s -X POST "https://$API_HOST/api/auth/login" \
  -H 'Content-Type: application/json' \
  -d '{"email":"demo@todoapp.local","password":"Password123!"}'
```

A token in the response proves the API, the Key Vault-sourced signing key, and the passwordless SQL
connection all work together. (A clean startup in `az webapp log tail -g $RG -n $API_APP` is the first
signal; if Key Vault or SQL is misconfigured, the app fails fast and you'll see it there.)

> **⚠️ Capture the *regional* hostname.** `$API_HOST` above is the full regional default domain (e.g.
> `todoapp-api-1234-abcdehg.centralus-01.azurewebsites.net`). Use **exactly this** for the SPA's
> `VITE_API_URL` in Phase 10 — the short `<API_APP>.azurewebsites.net` form does **not** resolve for
> newer App Services and is the single most common "failed to fetch" cause.

---

## Phase 8 — Create the Static Web App (get its URL)

Create the SWA resource now so its URL exists for Google and CORS. Deploy the built SPA in Phase 10.

```bash
az staticwebapp create -g $RG -n $SWA_APP -l $SWA_LOCATION --sku Free
SWA_TOKEN=$(az staticwebapp secrets list -g $RG -n $SWA_APP --query "properties.apiKey" -o tsv)
SWA_URL="https://$(az staticwebapp show -g $RG -n $SWA_APP --query defaultHostname -o tsv)"
echo "SPA will be at: $SWA_URL"
```

---

## Phase 9 — Google sign-in

Google is only an identity source — the API still issues and revokes its own JWTs. Quick version:

1. [Google Cloud console](https://console.cloud.google.com/) → select/create a project.
2. **OAuth consent screen** → *External*; app name + support email; scopes `openid`, `email`, `profile`;
   add your account under *Test users* (or publish).
3. **Credentials → Create credentials → OAuth client ID → Web application.**
4. **Authorized JavaScript origins** — add both `http://localhost:5173` (local dev) and `$SWA_URL` (the
   production SPA URL from Phase 8). *No redirect URIs needed* for this ID-token flow.
5. Copy the **Client ID** (`…apps.googleusercontent.com`) → this is `<web-client-id>` below.
6. Give it to the API:

   ```bash
   az webapp config appsettings set -g $RG -n $API_APP --settings \
     Authentication__Google__ClientId="<web-client-id>"
   ```

> Skip Google entirely by leaving the client id blank — the app just hides the Google button and
> email/password login works normally.

---

## Phase 10 — Build & deploy the SPA

Vite inlines env vars at **build time**, so set them on the build command. Use the **regional API
hostname** from Phase 7.

```bash
cd frontend
npm install
VITE_API_URL="https://$API_HOST" \
VITE_GOOGLE_CLIENT_ID="<web-client-id>" \
  npm run build

swa deploy ./dist --env production --deployment-token "$SWA_TOKEN"
cd ..
```

`staticwebapp.config.json` (already in `frontend/`) handles SPA deep-link fallback to `index.html`.

> **Alternative — GitHub CI/CD:** `az staticwebapp create` with `--source <repo-url> --branch main
> --app-location "frontend" --output-location "dist" --login-with-github` scaffolds a GitHub Action that
> builds and deploys on every push. Add `VITE_API_URL` / `VITE_GOOGLE_CLIENT_ID` as repository/Action
> variables. See [Workflows](../workflows.md).

---

## Phase 11 — Wire CORS

The browser calls the API cross-origin from the SPA, so allow that origin on the API:

```bash
az webapp config appsettings set -g $RG -n $API_APP --settings \
  Cors__AllowedOrigins__0="$SWA_URL"
```

(You already added `$SWA_URL` to Google's Authorized JavaScript origins in Phase 9.)

> **Avoid CORS entirely (optional):** link the API as an SWA backend so `/api` is proxied from the SPA
> origin — `az staticwebapp backends link -g $RG -n $SWA_APP --backend-resource-id <api-resource-id>
> --backend-region $LOCATION` — then rebuild the SPA with `VITE_API_URL` empty so it calls same-origin
> `/api`.

---

## Phase 12 — Final verification checklist

- [ ] **API up:** `https://$API_HOST/swagger` loads; `POST /api/auth/login` with the demo user returns a token.
- [ ] **Key Vault:** API started cleanly (`az webapp log tail`) — proves the managed identity read `Jwt--Key`.
- [ ] **SQL:** login/board data persists across an app restart — proves passwordless SQL, not the ephemeral fallback.
- [ ] **SPA up:** `$SWA_URL` loads, and the network calls hit the **regional** API host (not the short name).
- [ ] **Auth end to end:** register/login on the live site, create a task, reload — it persists.
- [ ] **Google:** the Google button appears and completes sign-in (its origin matches `$SWA_URL`).
- [ ] **No secrets in the repo:** `appsettings.json` has empty `Jwt:Key`/`KeyVault:Uri`; the only stored secret is `Jwt--Key` in the vault.

---

## Environment variable reference

| Purpose | Local dev | Azure |
|---------|-----------|-------|
| JWT signing key | user-secret `Jwt:Key` | Key Vault secret `Jwt--Key` (via `KeyVault__Uri`); or app setting `Jwt__Key` / a `@Microsoft.KeyVault(...)` reference |
| Key Vault URI | (unset → no vault) | App setting `KeyVault__Uri=https://<vault>.vault.azure.net/` — activates the vault config source |
| Google client ID (API) | user-secret `Authentication:Google:ClientId` | App setting `Authentication__Google__ClientId` |
| DB provider | (unset → SQLite) | App setting `Database__Provider=SqlServer` |
| DB connection | user-secret / default SQLite | App setting `ConnectionStrings__DefaultConnection` (passwordless: `Authentication=Active Directory Default`) |
| Allowed CORS origins | n/a (dev proxy) | App setting `Cors__AllowedOrigins__0` = SPA URL |
| API base URL (SPA) | `frontend/.env` `VITE_API_URL` (empty) | build-time `VITE_API_URL` = regional API URL |
| Google client ID (SPA) | `frontend/.env` `VITE_GOOGLE_CLIENT_ID` | build-time `VITE_GOOGLE_CLIENT_ID` |
| Environment | (Development) | App setting `ASPNETCORE_ENVIRONMENT=Production` |

Rules of thumb: nested config keys map to env vars with `__` (double underscore); `VITE_*` variables are
**build-time** (rebuild the SPA when they change); and the API refuses to start without a valid `Jwt:Key`.

---

## Notes & gotchas (all pre-handled in this repo)

- **Regional hostname** for `VITE_API_URL` — the short `<app>.azurewebsites.net` form fails to resolve;
  always use `az webapp show --query defaultHostName`.
- **SCM Basic Auth** must be enabled (Phase 2) or `az webapp deploy` is rejected.
- **Serverless cold start** — first request after idle can take ~30–60s; `Connect Timeout=60` + the app's
  transient retry handle it. A persistent 500 is a real error — check `az webapp log tail`.
- **Multiple cascade paths** — SQL Server rejects a cascade cycle SQLite allows; handled in the Dapper
  DDL by making `Category.UserId` `NO ACTION`. See [Infrastructure → Schema management](../infrastructure.md#schema-management).
- **Local dev is unaffected** — with no `KeyVault__Uri`, the app uses user-secrets and never calls Azure.
- Hit a wall the first time? Issues found (and fixed) during the Dapper refactor are logged in
  [Bugs encountered](../refactor-bugs.md).

---

> **← Back to the main [README](../../README.md).**
