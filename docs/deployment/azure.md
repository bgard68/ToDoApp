# Azure Deployment, Google Sign-In & Secrets

End-to-end guide for deploying the **.NET API** to Azure App Service and the **React SPA**
to Azure Static Web Apps, wiring up **Google sign-in**, and managing **secrets / user-secrets**
(local dev, Azure app settings, and Key Vault).

For non-Azure targets (Docker, Linux + nginx) see [DEPLOYMENT.md](deployment.md).

- [Architecture on Azure](#architecture-on-azure)
- [0. Prerequisites](#0-prerequisites)
- [1. Secrets & user-secrets (local dev)](#1-secrets--user-secrets-local-dev)
- [2. Set up Google sign-in](#2-set-up-google-sign-in)
- [3. Deploy the .NET API to App Service](#3-deploy-the-net-api-to-app-service)
- [4. Deploy the React SPA to Static Web Apps](#4-deploy-the-react-spa-to-static-web-apps)
- [5. Wire the two together (CORS + Google origins)](#5-wire-the-two-together)
- [6. Production secrets with Key Vault](#6-production-secrets-with-key-vault-recommended)
- [7. Environment variable reference](#7-environment-variable-reference)

---

## Architecture on Azure

```
Browser ──> Azure Static Web Apps (React SPA)  ──HTTPS──>  Azure App Service (.NET API)
                                                              └── SQLite (/home) or Azure SQL / Postgres
Google Identity Services (ID token) ──> POST /api/auth/google ──> API verifies + issues app tokens
```

The SPA and API are deployed independently. The browser calls the API cross-origin, so the
API's CORS must allow the SPA's origin (step 5). Google is only an identity source — the API
still issues and revokes its own JWTs.

---

## 0. Prerequisites

```bash
# Azure CLI + Static Web Apps CLI
az login
az account set --subscription "<your-subscription>"
npm install -g @azure/static-web-apps-cli
```

Set shell variables reused below (choose globally-unique app names):

```bash
RG=todoapp-rg
LOCATION=centralus
SWA_LOCATION=centralus          # Static Web Apps has a limited region list; Central US is supported
PLAN=todoapp-plan
API_APP=todoapp-api-$RANDOM     # must be globally unique -> <API_APP>.azurewebsites.net
SWA_APP=todoapp-web

# Database (passwordless via managed identity — no secret in the string).
SQL_SERVER=<your-sql-server>    # your Azure SQL logical server name (globally unique)
SQL_DB=<your-database>          # e.g. the database you created
SQL_CONNECTION="Server=tcp:${SQL_SERVER}.database.windows.net,1433;Database=${SQL_DB};Authentication=Active Directory Default;Encrypt=True;Connect Timeout=60;"
```

---

## 1. Secrets & user-secrets (local dev)

Nothing secret lives in `appsettings.json`. Locally, the .NET **Secret Manager** stores the
signing key and Google client ID outside the repo (under `~/.microsoft/usersecrets/…` on the
dev machine).

```bash
# One-time init (safe to re-run)
dotnet user-secrets init --project src/TodoApp.WebApi

# Required: JWT signing key (>= 32 bytes)
dotnet user-secrets set "Jwt:Key" "$(openssl rand -base64 48)" --project src/TodoApp.WebApi

# Optional: Google client ID (needed only if you enable Google sign-in)
dotnet user-secrets set "Authentication:Google:ClientId" "<web-client-id>" --project src/TodoApp.WebApi

# List what's stored
dotnet user-secrets list --project src/TodoApp.WebApi
```

The **frontend** uses build-time env vars in `frontend/.env` (copy from `.env.example`):

```bash
# frontend/.env
VITE_API_URL=                       # empty in dev -> uses the Vite proxy to :5080
VITE_GOOGLE_CLIENT_ID=<web-client-id>
```

> In Azure these same values become **App Service application settings** (for the API) and
> **build-time env vars** (for the SPA). See step 7 for the full mapping.

---

## 2. Set up Google sign-in

Google sign-in uses Google Identity Services on the client to obtain an **ID token**, which
the API verifies. You only need an OAuth **client ID** (public); there is no client secret in
this flow.

1. Go to the [Google Cloud console](https://console.cloud.google.com/) and select or create a
   project.
2. **APIs & Services → OAuth consent screen**: choose *External*, fill in app name and support
   email, add scopes `openid`, `email`, `profile`. While in "Testing", add your Google account
   under *Test users* (or *Publish* the app for public use).
3. **APIs & Services → Credentials → Create credentials → OAuth client ID**:
   - Application type: **Web application**.
   - **Authorized JavaScript origins** — add every origin the button loads from:
     - `http://localhost:5173` (local dev)
     - `https://<SWA_APP>.azurestaticapps.net` (your production SPA URL, from step 4)
     - your custom domain, if any.
   - *Authorized redirect URIs* are **not required** for this ID-token/popup flow.
4. Copy the **Client ID** (looks like `1234567890-abc.apps.googleusercontent.com`).
5. Provide it in both places (they must match):
   - **Backend**: `Authentication:Google:ClientId` (user-secret locally; App Service setting in
     Azure).
   - **Frontend**: `VITE_GOOGLE_CLIENT_ID` (`frontend/.env` locally; build-time env in CI/SWA).

If the client ID is blank, the app simply hides the Google button and email/password auth works
normally.

---

## 3. Deploy the .NET API to App Service

```bash
# Resource group + Linux plan + web app
az group create -n $RG -l $LOCATION
az appservice plan create -g $RG -n $PLAN --is-linux --sku B1
az webapp create -g $RG -p $PLAN -n $API_APP --runtime "DOTNETCORE:10.0"
```

> If `DOTNETCORE:10.0` isn't offered yet in your region, run
> `az webapp list-runtimes --os linux | grep DOTNET` to see options, or deploy the container
> image instead (`Dockerfile.api` → Azure Container Registry → App Service for Containers), or
> publish `--self-contained`.

Configure settings (secrets come from the CLI here; Key Vault option in step 6):

```bash
az webapp config appsettings set -g $RG -n $API_APP --settings \
  ASPNETCORE_ENVIRONMENT="Production" \
  Jwt__Key="$(openssl rand -base64 48)" \
  Jwt__Issuer="TodoApp" \
  Jwt__Audience="TodoAppClient" \
  Authentication__Google__ClientId="<web-client-id>" \
  Database__Provider="SqlServer" \
  ConnectionStrings__DefaultConnection="$SQL_CONNECTION"
```

> The connection string is taken from the `$SQL_CONNECTION` shell variable defined in step 0 —
> it isn't hardcoded here. It's a passwordless (managed-identity) string with no secret, so it's
> safe either way, but keeping it in a variable means the literal server name lives in one place.
> If you set the value in the portal instead, you can drop the `ConnectionStrings__DefaultConnection`
> line from this command entirely — App Service already holds it.

> **Database provider.** The app runs on SQLite by default; setting `Database__Provider=SqlServer`
> plus a SQL connection string switches it to **Azure SQL** with no code change (the provider is
> chosen in `AddInfrastructure`). On first startup the app's `EnsureCreated` builds the schema
> and seeds the demo data.
>
> **Passwordless connection (recommended).** The connection string above has **no user id or
> password** — `Authentication=Active Directory Default` makes the API connect as its Azure
> **managed identity**, so no secret is stored anywhere. Set it up once:
>
> 1. App Service → **Identity → System assigned → On** (creates an Entra identity named `$API_APP`).
> 2. Ensure the SQL *server* has you as its **Microsoft Entra admin** (set at server creation
>    with "Both" authentication).
> 3. Connect to the `taskboard` database as that Entra admin (portal **Query editor**, Microsoft
>    Entra sign-in) and grant the identity access — the user name must match the App Service name:
>
>    ```sql
>    CREATE USER [<API_APP>] FROM EXTERNAL PROVIDER;
>    ALTER ROLE db_datareader ADD MEMBER [<API_APP>];
>    ALTER ROLE db_datawriter ADD MEMBER [<API_APP>];
>    ALTER ROLE db_ddladmin  ADD MEMBER [<API_APP>];  -- lets EnsureCreated build the schema
>    ```
>
>    (On the free serverless tier the first connection may return "database is not currently
>    available" while it resumes from auto-pause — wait ~30–60s and retry.)
>
> **SQL-auth alternative.** If you'd rather use a login/password, copy the ADO.NET string from
> the portal (SQL **database → Connection strings → ADO.NET**) and replace `{your_password}`;
> it then contains a secret, so keep it only in app settings or Key Vault, never in the repo.
>
> **No Azure SQL at all?** Omit both settings and use
> `ConnectionStrings__DefaultConnection="Data Source=/home/todoapp.db"` — `/home` is App
> Service's persistent storage, so the SQLite file survives restarts (single instance only).

Publish and deploy:

```bash
dotnet publish src/TodoApp.WebApi/TodoApp.WebApi.csproj -c Release -o ./publish
(cd publish && zip -r ../api.zip .)
az webapp deploy -g $RG -n $API_APP --src-path api.zip --type zip

# If the app doesn't start, set the entry point explicitly:
az webapp config set -g $RG -n $API_APP --startup-file "dotnet TodoApp.WebApi.dll"
```

Verify: `https://<API_APP>.azurewebsites.net/swagger` (Swagger shows in non-Production too; in
Production, test `POST /api/auth/login` with the seeded demo user).

---

## 4. Deploy the React SPA to Static Web Apps

Create the Static Web App and grab its deployment token:

```bash
az staticwebapp create -g $RG -n $SWA_APP -l $SWA_LOCATION --sku Free
SWA_TOKEN=$(az staticwebapp secrets list -g $RG -n $SWA_APP --query "properties.apiKey" -o tsv)
SWA_URL="https://$(az staticwebapp show -g $RG -n $SWA_APP --query defaultHostname -o tsv)"
echo "SPA will be at: $SWA_URL"
```

Build the SPA **pointing at the API** (Vite inlines env vars at build time), then deploy the
`dist` folder:

```bash
cd frontend
npm install
VITE_API_URL="https://$API_APP.azurewebsites.net" \
VITE_GOOGLE_CLIENT_ID="<web-client-id>" \
  npm run build

swa deploy ./dist --env production --deployment-token "$SWA_TOKEN"
cd ..
```

`staticwebapp.config.json` (included in `frontend/`) handles SPA deep-link fallback to
`index.html`.

> **Alternative — GitHub CI/CD:** `az staticwebapp create` with
> `--source <repo-url> --branch main --app-location "frontend" --output-location "dist"
> --login-with-github` scaffolds a GitHub Action that builds and deploys on every push. Add
> `VITE_API_URL` / `VITE_GOOGLE_CLIENT_ID` as repository/Action env vars.

---

## 5. Wire the two together

The browser calls the API from the SPA's origin, so allow that origin on the API, and add it
to Google:

```bash
# Allow the SPA origin on the API's CORS policy
az webapp config appsettings set -g $RG -n $API_APP --settings \
  Cors__AllowedOrigins__0="$SWA_URL"
```

Then in the Google console (step 2) add `$SWA_URL` to **Authorized JavaScript origins**.

> **Avoid CORS entirely (optional):** link the API as a Static Web Apps backend so `/api` is
> proxied from the SPA origin:
> `az staticwebapp backends link -g $RG -n $SWA_APP --backend-resource-id <api-resource-id> --backend-region $LOCATION`.
> Then rebuild the SPA with `VITE_API_URL` empty so it calls same-origin `/api`.

---

## 6. Production secrets with Key Vault (recommended)

The JWT signing key is the **only** real secret this project has — the database is passwordless
(managed identity) and the Google client id is public — so Key Vault holds exactly one value. Use a
**Vault (Standard tier)**, not Managed HSM (Managed HSM stores keys, not secrets).

There are two ways to wire it in. **This repo ships the config-provider approach (Option B).** For the
full treatment — what to store, the exact source diff, the step-by-step **portal walkthrough**, keeping
it optional so the app still runs locally with no vault, and how to verify — see **[KEY_VAULT.md](key-vault.md)**.

**Option A — app-setting reference (no code):** App Service resolves a `@Microsoft.KeyVault(...)` token
at runtime; the app reads `Jwt:Key` unchanged.

```bash
KV=todoapp-kv-$RANDOM
az keyvault create -g $RG -n $KV -l $LOCATION
az keyvault secret set --vault-name $KV -n JwtKey --value "$(openssl rand -base64 48)"

# Give the API a managed identity and read access to the vault
az webapp identity assign -g $RG -n $API_APP
PRINCIPAL_ID=$(az webapp identity show -g $RG -n $API_APP --query principalId -o tsv)
az keyvault set-policy -n $KV --object-id $PRINCIPAL_ID --secret-permissions get

# Reference the secret from the app setting
az webapp config appsettings set -g $RG -n $API_APP --settings \
  Jwt__Key="@Microsoft.KeyVault(SecretUri=https://$KV.vault.azure.net/secrets/JwtKey/)"
```

**Option B — configuration provider (what this repo does):** the code registers Key Vault only when
`KeyVault__Uri` is set, so nothing changes locally. Store the secret as `Jwt--Key`, grant the identity
the **Key Vault Secrets User** role (RBAC), and set one app setting instead of the reference:

```bash
az keyvault create -g $RG -n $KV -l $LOCATION --enable-rbac-authorization true
az keyvault secret set --vault-name $KV -n "Jwt--Key" --value "$(openssl rand -base64 48)"

PRINCIPAL_ID=$(az webapp identity show -g $RG -n $API_APP --query principalId -o tsv)
az role assignment create --assignee $PRINCIPAL_ID \
  --role "Key Vault Secrets User" --scope $(az keyvault show -n $KV --query id -o tsv)

# Activate the vault (and remove the old plaintext key setting)
az webapp config appsettings set -g $RG -n $API_APP --settings \
  KeyVault__Uri="https://$KV.vault.azure.net/"
az webapp config appsettings delete -g $RG -n $API_APP --setting-names Jwt__Key
```

Either way the app reads `Jwt:Key` exactly as before, and it still **fails fast** at startup if no
source supplies the key.

---

## 7. Environment variable reference

| Purpose | Local dev | Azure |
|---------|-----------|-------|
| JWT signing key | user-secret `Jwt:Key` | App setting `Jwt__Key`, or (Option A) a Key Vault ref, or (Option B) removed in favor of `Jwt--Key` in the vault |
| Key Vault URI (Option B) | (unset → no vault) | App setting `KeyVault__Uri=https://<vault>.vault.azure.net/` — activates the vault config source |
| Google client ID (API) | user-secret `Authentication:Google:ClientId` | App setting `Authentication__Google__ClientId` |
| DB provider | (unset → SQLite) | App setting `Database__Provider=SqlServer` |
| DB connection | user-secret / default SQLite | App setting `ConnectionStrings__DefaultConnection` (passwordless: `Authentication=Active Directory Default`) |
| Allowed CORS origins | n/a (dev proxy) | App setting `Cors__AllowedOrigins__0` = SPA URL |
| API base URL (SPA) | `frontend/.env` `VITE_API_URL` (empty) | build-time `VITE_API_URL` = API URL |
| Google client ID (SPA) | `frontend/.env` `VITE_GOOGLE_CLIENT_ID` | build-time `VITE_GOOGLE_CLIENT_ID` |
| Environment | (Development) | App setting `ASPNETCORE_ENVIRONMENT=Production` |

Rules of thumb: nested config keys map to env vars with `__` (double underscore); `Vite`
variables are **build-time** (rebuild the SPA when they change); and the API refuses to start
without a valid `Jwt:Key`.
