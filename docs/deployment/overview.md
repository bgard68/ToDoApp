# Deployment overview

This guide covers everything from a clean checkout to a running production deployment.
For a feature overview see the [README](../../README.md); for the HTTP endpoints and auth model see the [API reference](../architecture/api-reference.md).

- [1. Prerequisites](#1-prerequisites)
- [2. First-time setup](#2-first-time-setup)
- [3. Build & compile](#3-build--compile)
- [4. Run locally](#4-run-locally-development)
- [5. Configuration & secrets](#5-configuration--secrets)
- [6. Database in production](#6-database-in-production)
- [7. Deploy — Docker Compose](#7-deploy--docker-compose-recommended)
- [8. Deploy — Linux server (systemd + nginx)](#8-deploy--linux-server-systemd--nginx)
- [9. Deploy — Azure App Service](#9-deploy--azure-app-service)
- [10. Hosting the frontend](#10-hosting-the-frontend)
- [11. Post-deploy checklist](#11-post-deploy-checklist)

---

## 1. Prerequisites

| Tool | Version | Verify |
|------|---------|--------|
| .NET SDK | 10.0+ | `dotnet --version` |
| Node.js | 18+ (20/22 recommended) | `node --version` |
| npm | 9+ | `npm --version` |
| Docker (optional) | 24+ | `docker --version` |
| Git | any | `git --version` |

Visual Studio 2026 (or Rider / VS Code) is optional — everything below works from the CLI.

---

## 2. First-time setup

```bash
git clone <your-repo-url> TodoApp
cd TodoApp

# Provide the JWT signing key (never stored in appsettings). One-time, for local dev:
dotnet user-secrets init --project src/TodoApp.WebApi
dotnet user-secrets set "Jwt:Key" "$(openssl rand -base64 48)" --project src/TodoApp.WebApi
```

Optional — enable Google sign-in by setting the client ID in both places (full setup in the [Google sign-in guide](google-signin.md)):

```bash
dotnet user-secrets set "Authentication:Google:ClientId" "<web-client-id>" --project src/TodoApp.WebApi
# frontend/.env :  VITE_GOOGLE_CLIENT_ID=<web-client-id>
```

---

## 3. Build & compile

**Backend** (restores NuGet packages and compiles all projects):

```bash
dotnet build -c Release            # compile the whole solution
dotnet test                        # run unit + integration tests
```

**Frontend** (produces an optimized static bundle in `frontend/dist/`):

```bash
cd frontend
npm install
npm run build
```

**Publish the backend** for deployment (framework-dependent; needs the .NET runtime on the host):

```bash
dotnet publish src/TodoApp.WebApi/TodoApp.WebApi.csproj -c Release -o ./publish
```

For a **self-contained** build that bundles the runtime (no .NET install needed on the host),
add a runtime identifier:

```bash
dotnet publish src/TodoApp.WebApi/TodoApp.WebApi.csproj -c Release -o ./publish \
  -r linux-x64 --self-contained true
```

---

## 4. Run locally (development)

Two terminals:

```bash
# Terminal 1 — API at http://localhost:5080 (Swagger at /swagger)
dotnet run --project src/TodoApp.WebApi

# Terminal 2 — SPA at http://localhost:5173 (proxies /api to :5080)
cd frontend && npm run dev
```

Sign in with the seeded demo user `demo@todoapp.local` / `Password123!`, or register a new one.

---

## 5. Configuration & secrets

All settings can be supplied by environment variables. Nested keys use `__` (double
underscore): `Jwt:Key` → `Jwt__Key`. **Never commit real secrets** — `appsettings.json`
ships with an empty `Jwt:Key` on purpose.

| Setting | Env var | Notes |
|---------|---------|-------|
| JWT signing key | `Jwt__Key` | **Required.** ≥ 32 bytes. App won't start without it. |
| Access token lifetime | `Jwt__AccessTokenMinutes` | default 15 |
| Refresh token lifetime | `Jwt__RefreshTokenDays` | default 7 |
| DB connection | `ConnectionStrings__DefaultConnection` | default `Data Source=todoapp.db` |
| Allowed CORS origins | `Cors__AllowedOrigins__0`, `__1`, … | the SPA's origin(s) |
| Google client ID | `Authentication__Google__ClientId` | public value; blank disables Google |
| Environment | `ASPNETCORE_ENVIRONMENT` | `Production` in prod |
| Listen URL | `ASPNETCORE_URLS` | e.g. `http://+:8080` |

Behind a TLS-terminating reverse proxy you generally do not need HTTPS inside the app,
but you should forward the scheme (the proxy samples set `X-Forwarded-Proto`).

---

## 6. Database in production

The app ships with **SQLite** and calls `EnsureCreated()` at startup, which is perfect for
demos and small single-instance deployments. For anything larger, switch to a server
database and use EF Core **migrations**:

1. Add the provider package to `TodoApp.Infrastructure`, e.g.
   `Npgsql.EntityFrameworkCore.PostgreSQL` (Postgres) or
   `Microsoft.EntityFrameworkCore.SqlServer`.
2. In `src/TodoApp.Infrastructure/DependencyInjection.cs`, change `options.UseSqlite(...)`
   to `options.UseNpgsql(...)` / `options.UseSqlServer(...)`.
3. Replace `EnsureCreatedAsync()` in `DbInitializer.cs` with `MigrateAsync()`.
4. Create and apply the migration:

```bash
dotnet tool install --global dotnet-ef
dotnet ef migrations add InitialCreate \
  --project src/TodoApp.Infrastructure --startup-project src/TodoApp.WebApi
dotnet ef database update \
  --project src/TodoApp.Infrastructure --startup-project src/TodoApp.WebApi
```

---

## 7. Deploy — Docker Compose (recommended)

The repo includes `Dockerfile.api`, `frontend/Dockerfile`, `frontend/nginx.conf`, and
`docker-compose.yml`. The API runs internally; nginx serves the SPA and proxies `/api` to
it, so the browser only ever talks to one origin (no CORS to configure).

```bash
cp .env.example .env
# set JWT_KEY (openssl rand -base64 48) and optionally GOOGLE_CLIENT_ID in .env

docker compose up --build          # build images and start
# open http://localhost:8080
```

SQLite data persists in the `todo-data` named volume. To build the images individually:

```bash
docker build -f Dockerfile.api -t todoapp-api .
docker build -t todoapp-web ./frontend
```

Put a TLS-terminating proxy (or your cloud load balancer) in front of the `web` container
for HTTPS in production.

---

## 8. Deploy — Linux server (systemd + nginx)

For a VM without Docker. Publish, copy the artifacts, run the API under systemd, and let
nginx serve the SPA and proxy the API. Sample files are in `deploy/`.

```bash
# 1) Build artifacts (on a build machine or the server)
dotnet publish src/TodoApp.WebApi/TodoApp.WebApi.csproj -c Release -o ./publish
(cd frontend && npm install && npm run build)

# 2) Copy to the server
sudo mkdir -p /var/www/todoapp-api /var/www/todoapp-web
sudo cp -r publish/*        /var/www/todoapp-api/
sudo cp -r frontend/dist/*  /var/www/todoapp-web/

# 3) API service (edit the Jwt__Key first!)
sudo cp deploy/todoapp-api.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now todoapp-api

# 4) nginx site
sudo cp deploy/nginx-reverse-proxy.conf /etc/nginx/sites-available/todoapp
sudo ln -s /etc/nginx/sites-available/todoapp /etc/nginx/sites-enabled/todoapp
sudo nginx -t && sudo systemctl reload nginx

# 5) HTTPS
sudo certbot --nginx -d todo.example.com
```

The `.NET` runtime (`aspnetcore-runtime-10.0`) must be installed on the server unless you
published `--self-contained`.

---

## 9. Deploy — Azure App Service

A full, step-by-step Azure walkthrough — API on **App Service**, React on **Static Web
Apps**, plus **Google sign-in** and **Key Vault** secrets — lives in the **[Azure guide](azure.md)**.
The short version:

```bash
# Backend
dotnet publish src/TodoApp.WebApi/TodoApp.WebApi.csproj -c Release -o ./publish
cd publish && zip -r ../api.zip . && cd ..
az webapp deploy --resource-group <rg> --name <app> --src-path api.zip --type zip

# Secrets & config (App Service Application settings)
az webapp config appsettings set --resource-group <rg> --name <app> --settings \
  Jwt__Key="<long-random>" \
  Cors__AllowedOrigins__0="https://<your-spa-host>" \
  Authentication__Google__ClientId="<client-id>"
```

Host the built SPA on Azure Static Web Apps / Storage + CDN (set `VITE_API_URL` to the API
URL at build time), or serve it from the same App Service via the reverse-proxy pattern.

---

## 10. Hosting the frontend

The SPA is a static bundle (`frontend/dist`) and can be hosted two ways:

**A. Same origin as the API (recommended).** A reverse proxy (the nginx samples, Compose,
or App Service) serves the static files and forwards `/api` to the backend. Build with
`VITE_API_URL` empty so the app calls `/api` on its own origin — no CORS needed.

**B. Separate static host (Netlify / Vercel / S3+CloudFront / Static Web Apps).** Set the
API origin at build time and allow that origin on the backend:

```bash
# frontend/.env
VITE_API_URL=https://api.example.com
```

```bash
# backend
Cors__AllowedOrigins__0=https://app.example.com
```

Rebuild the SPA whenever `VITE_API_URL` changes (Vite inlines env vars at build time).

---

## 11. Post-deploy checklist

- [ ] `Jwt__Key` is a strong random secret supplied via env/secret store (not in a file).
- [ ] HTTPS terminated at the proxy/load balancer; `X-Forwarded-Proto` forwarded.
- [ ] `Cors__AllowedOrigins` lists exactly your SPA origin(s) — nothing wildcard.
- [ ] Google Cloud "Authorized JavaScript origins" include your production SPA URL, and the
      client ID matches on backend (`Authentication__Google__ClientId`) and frontend
      (`VITE_GOOGLE_CLIENT_ID`).
- [ ] The seeded demo user is removed or disabled (edit `DbInitializer.cs`).
- [ ] Production database uses migrations (not `EnsureCreated`) if you moved off SQLite.
- [ ] Logs/monitoring wired up; container/host restart policy set.
- [ ] `dotnet test` is green in CI before each deploy.
