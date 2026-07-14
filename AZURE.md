# Azure Deployment, Google Sign-In & Secrets

End-to-end guide for deploying the **.NET API** to Azure App Service and the **React SPA**
to Azure Static Web Apps, wiring up **Google sign-in**, and managing **secrets / user-secrets**
(local dev, Azure app settings, and Key Vault).

For non-Azure targets (Docker, Linux + nginx) see [DEPLOYMENT.md](DEPLOYMENT.md).

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
LOCATION=eastus
SWA_LOCATION=eastus2            # Static Web Apps has a limited region list
PLAN=todoapp-plan
API_APP=todoapp-api-$RANDOM     # must be globally unique -> <API_APP>.azurewebsites.net
SWA_APP=todoapp-web
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
  ConnectionStrings__DefaultConnection="Data Source=/home/todoapp.db"
```

> `/home` is Azure App Service's persistent storage, so the SQLite file survives restarts. For
> real workloads use **Azure SQL** or **Azure Database for PostgreSQL** and switch the provider
> + migrations (see DEPLOYMENT.md §6); then set
> `ConnectionStrings__DefaultConnection` to that server's connection string.

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

Instead of putting `Jwt__Key` directly in app settings, store it in Key Vault and reference it,
so the secret never appears in configuration listings.

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

The app reads `Jwt:Key` exactly as before — App Service resolves the Key Vault reference at
runtime.

---

## 7. Environment variable reference

| Purpose | Local dev | Azure |
|---------|-----------|-------|
| JWT signing key | user-secret `Jwt:Key` | App setting `Jwt__Key` (or Key Vault ref) |
| Google client ID (API) | user-secret `Authentication:Google:ClientId` | App setting `Authentication__Google__ClientId` |
| DB connection | user-secret / default SQLite | App setting `ConnectionStrings__DefaultConnection` |
| Allowed CORS origins | n/a (dev proxy) | App setting `Cors__AllowedOrigins__0` = SPA URL |
| API base URL (SPA) | `frontend/.env` `VITE_API_URL` (empty) | build-time `VITE_API_URL` = API URL |
| Google client ID (SPA) | `frontend/.env` `VITE_GOOGLE_CLIENT_ID` | build-time `VITE_GOOGLE_CLIENT_ID` |
| Environment | (Development) | App setting `ASPNETCORE_ENVIRONMENT=Production` |

Rules of thumb: nested config keys map to env vars with `__` (double underscore); `Vite`
variables are **build-time** (rebuild the SPA when they change); and the API refuses to start
without a valid `Jwt:Key`.
