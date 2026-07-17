# Azure Setup — Complete Start-to-Finish Runbook

A single ordered pass that takes ToDoApp from nothing to a fully working Azure deployment:
the **.NET API** on App Service, **Azure SQL** with passwordless managed identity, the **React SPA**
on Static Web Apps, **Google sign-in**, **CORS**, and the **JWT signing key in Key Vault** — with no
secrets committed anywhere.

Follow it top to bottom. Each phase depends on the ones before it, and the ordering is deliberate —
it avoids the chicken-and-egg traps (managed identity must exist before the SQL grant; the SPA URL
must exist before Google/CORS; the new build must be deployed before you delete the plaintext key).

For deeper explanation of any phase, see the focused guides:
[Azure reference](azure.md) · [Google sign-in](google-signin.md) · [Key Vault](key-vault.md) ·
[Database portability](../architecture/database-portability.md) · [Deployment guide](deployment.md)

> **← Back to the main [README](../../README.md).**

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
SQL_SERVER=todoapp-sql-$RANDOM        # globally unique
SQL_DB=todoapp
KV=todoapp-kv-$RANDOM                 # globally unique

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

> If `DOTNETCORE:10.0` isn't offered in your region yet, run
> `az webapp list-runtimes --os linux | grep DOTNET`, or deploy the container image, or publish
> `--self-contained`. See [AZURE.md §3](azure.md#3-deploy-the-net-api-to-app-service).

**Enable Basic Auth publishing** (App Service ships with SCM Basic Auth *off*, which blocks
`az webapp deploy`):

```bash
az resource update -g $RG --namespace Microsoft.Web \
  --resource-type basicPublishingCredentialsPolicies \
  --name scm --parent sites/$API_APP --set properties.allow=true
```

---

## Phase 3 — Turn on the App Service managed identity

This identity is what authenticates to **both** SQL and Key Vault — no passwords anywhere. It must
exist before the SQL grant and the Key Vault role assignment, so do it now.

```bash
az webapp identity assign -g $RG -n $API_APP
PRINCIPAL_ID=$(az webapp identity show -g $RG -n $API_APP --query principalId -o tsv)
```

---

## Phase 4 — Azure SQL (passwordless)

Create an Entra-only SQL server (you as admin), the database, and open the firewall so you can run the
grant script.

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
ALTER ROLE db_ddladmin  ADD MEMBER [<API_APP>];   -- lets EnsureCreated build the schema
```

Build the passwordless connection string (no user id or password in it):

```bash
SQL_CONNECTION="Server=tcp:${SQL_SERVER}.database.windows.net,1433;Database=${SQL_DB};Authentication=Active Directory Default;Encrypt=True;Connect Timeout=60;"
```

> `Connect Timeout=60` rides out the serverless cold-start; the app also retries transient failures.
> The first request after idle may take ~30–60s while the database resumes.

---

## Phase 5 — Key Vault for the JWT signing key

The signing key is the **only** real secret (the DB is passwordless and the Google client id is
public). Use a **Vault, Standard tier** — not Managed HSM, which stores keys, not secrets. The repo
ships the **configuration-provider** approach (activated by the `KeyVault__Uri` app setting).

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

> Prefer the portal? The click-by-click version is in
> [KEY_VAULT.md → Portal setup walkthrough](key-vault.md#portal-setup-walkthrough-step-by-step).

---

## Phase 6 — Configure the API's app settings

Everything the API needs, in one call. Note there is **no `Jwt__Key`** here — Key Vault supplies it
via `KeyVault__Uri`.

```bash
az webapp config appsettings set -g $RG -n $API_APP --settings \
  ASPNETCORE_ENVIRONMENT="Production" \
  Jwt__Issuer="TodoApp" \
  Jwt__Audience="TodoAppClient" \
  Database__Provider="SqlServer" \
  ConnectionStrings__DefaultConnection="$SQL_CONNECTION" \
  KeyVault__Uri="https://$KV.vault.azure.net/"
```

Google and CORS settings come later (Phases 9–10), once the SPA URL exists.

---

## Phase 7 — Build & deploy the API

```bash
dotnet publish src/TodoApp.WebApi/TodoApp.WebApi.csproj -c Release -o ./publish
(cd publish && zip -r ../api.zip .)
az webapp deploy -g $RG -n $API_APP --src-path api.zip --type zip

# If it doesn't start, pin the entry point:
az webapp config set -g $RG -n $API_APP --startup-file "dotnet TodoApp.WebApi.dll"
```

On first startup `EnsureCreated` builds the schema and seeds the demo user. **Verify:**

```bash
API_HOST=$(az webapp show -g $RG -n $API_APP --query defaultHostName -o tsv)
curl -s -X POST "https://$API_HOST/api/auth/login" \
  -H 'Content-Type: application/json' \
  -d '{"email":"demo@todoapp.local","password":"Password123!"}'
```

A token in the response proves the API, the Key Vault-sourced signing key, and the passwordless SQL
connection all work together. (A clean startup in `az webapp log tail -g $RG -n $API_APP` is the first
signal; if Key Vault or SQL is misconfigured, the app fails fast and you'll see it there.)

> **⚠️ Capture the *regional* hostname.** `$API_HOST` above is the full regional default domain
> (e.g. `todoapp-api-1234-abcdehg.centralus-01.azurewebsites.net`). Use **exactly this** for the SPA's
> `VITE_API_URL` in the next phase — the short `<API_APP>.azurewebsites.net` form does **not** resolve
> for newer App Services and is the single most common "failed to fetch" cause.

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

Full detail in [GOOGLE_SIGNIN.md](google-signin.md). Quick version:

1. [Google Cloud console](https://console.cloud.google.com/) → select/create a project.
2. **OAuth consent screen** → *External*; app name + support email; scopes `openid`, `email`,
   `profile`; add your account under *Test users* (or publish).
3. **Credentials → Create credentials → OAuth client ID → Web application.**
4. **Authorized JavaScript origins** — add both:
   - `http://localhost:5173` (local dev)
   - `$SWA_URL` (the production SPA URL from Phase 8)
   - *No redirect URIs needed* for this ID-token flow.
5. Copy the **Client ID** (`…apps.googleusercontent.com`) → this is `<web-client-id>` below.
6. Give it to the API:

   ```bash
   az webapp config appsettings set -g $RG -n $API_APP --settings \
     Authentication__Google__ClientId="<web-client-id>"
   ```

> If you skip Google entirely, leave the client id blank — the app just hides the Google button and
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

---

## Phase 11 — Wire CORS

The browser calls the API cross-origin from the SPA, so allow that origin on the API:

```bash
az webapp config appsettings set -g $RG -n $API_APP --settings \
  Cors__AllowedOrigins__0="$SWA_URL"
```

(You already added `$SWA_URL` to Google's Authorized JavaScript origins in Phase 9.)

> **Avoid CORS entirely (optional):** link the API as an SWA backend so `/api` is same-origin, then
> rebuild the SPA with `VITE_API_URL` empty. See [AZURE.md §5](azure.md#5-wire-the-two-together).

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

## Notes & gotchas (all pre-handled in this repo)

- **Regional hostname** for `VITE_API_URL` — the short `<app>.azurewebsites.net` form fails to
  resolve; always use `az webapp show --query defaultHostName`.
- **SCM Basic Auth** must be enabled (Phase 2) or `az webapp deploy` is rejected.
- **Serverless cold start** — first request after idle can take ~30–60s; `Connect Timeout=60` +
  EF retry handle it.
- **Multiple cascade paths** — SQL Server rejects the cascade cycle SQLite allows; already fixed in
  code with `ClientCascade`. See [DATABASE_PORTABILITY.md](../architecture/database-portability.md).
- **Deploy before deleting the plaintext key** — this runbook never sets `Jwt__Key`, so there's
  nothing to delete; the key comes from Key Vault from the first deploy.
- **Local dev is unaffected** — with no `KeyVault__Uri`, the app uses user-secrets and never calls
  Azure. See [KEY_VAULT.md](key-vault.md#working-locally-without-key-vault).

---

> **← Back to the main [README](../../README.md).**
