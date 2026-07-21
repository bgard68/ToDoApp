#!/usr/bin/env bash
#
# provision.sh — Provision the "taskboard" stack on Azure with the Azure CLI.
#
# Aligned to the live environment in resource group 'rg-taskboard':
#   - Linux App Service plan on the F1 (Free) tier + Linux web app
#   - Azure SQL: General Purpose serverless (Gen5) database with the FREE limit
#   - Key Vault
#   - A user-assigned managed identity (for OIDC / CI-CD federated deploys)
#   - Region: centralus
#
# Storage account + static website are OPTIONAL (the live env has none):
#   set ENABLE_STORAGE=true to also create them.
#
# Idempotent: safe to re-run. Most `az ... create` commands are upserts.
#
# Prerequisites:
#   - Azure CLI logged in: `az login`  (in Cloud Shell you're already logged in)
#   - Subscription selected: `az account set --subscription <id-or-name>`
#   - openssl available (for password generation)
#
# Usage:
#   ./provision.sh                                  # defaults (matches rg-taskboard)
#   ENABLE_STORAGE=true ./provision.sh              # also add storage + static site
#   PROJECT=taskboard LOCATION=centralus ./provision.sh
#   ./provision.sh --what-if                        # print planned config and exit
#
#   # Recreate a captured environment in one run (see LOCAL-EXPORT-RUNBOOK.md):
#   RESOURCE_GROUP=rg-taskboard-copy \
#   IMPORT_SECRETS_FILE=azure-export/keyvault-taskboard-kv.secrets.json \
#   IMPORT_SETTINGS_FILE=azure-export/taskboard-06-api.settings.env \
#   ./provision.sh
# -----------------------------------------------------------------------------
set -euo pipefail

# ---- Configuration (override via env vars) ----------------------------------
PROJECT="${PROJECT:-taskboard}"                # short name, lowercase letters/digits
ENVIRONMENT="${ENVIRONMENT:-prod}"            # dev | test | prod
LOCATION="${LOCATION:-centralus}"             # Azure region (live env: centralus)
RESOURCE_GROUP="${RESOURCE_GROUP:-rg-${PROJECT}}"

# Web / App Service (Linux, Free tier — matches ASP-rgtaskboard / taskboard-06-api)
APP_SERVICE_PLAN="${APP_SERVICE_PLAN:-asp-${PROJECT}}"
APP_SKU="${APP_SKU:-F1}"                        # F1 = Free. Use B1/S1/P1v3 to scale up.
WEBAPP_NAME="${WEBAPP_NAME:-${PROJECT}-api-$RANDOM}"   # must be globally unique
# Linux runtime string for `az webapp create`. The live app kind is generic
# "app,linux"; adjust to match your stack, e.g. NODE:20-lts, PYTHON:3.12, JAVA:17.
RUNTIME="${RUNTIME:-DOTNETCORE:8.0}"

# Azure SQL (General Purpose serverless Gen5 + free limit — matches taskboard DB)
SQL_SERVER_NAME="${SQL_SERVER_NAME:-${PROJECT}-sql-$RANDOM}"  # globally unique
SQL_DB_NAME="${SQL_DB_NAME:-${PROJECT}}"
SQL_MAX_VCORES="${SQL_MAX_VCORES:-2}"          # serverless auto-scale ceiling
SQL_MIN_VCORES="${SQL_MIN_VCORES:-0.5}"        # serverless auto-scale floor
SQL_AUTO_PAUSE_MIN="${SQL_AUTO_PAUSE_MIN:-60}" # minutes idle before auto-pause
SQL_USE_FREE_LIMIT="${SQL_USE_FREE_LIMIT:-true}"  # Azure SQL free offer (1 per subscription)
SQL_ADMIN_USER="${SQL_ADMIN_USER:-sqladmin}"
SQL_ADMIN_PASSWORD="${SQL_ADMIN_PASSWORD:-}"   # if empty, generated & stored in Key Vault

# User-assigned managed identity (the live env has oidc-msi-* identities for CI/CD)
UAMI_NAME="${UAMI_NAME:-${PROJECT}-oidc-msi}"

# Key Vault (live env: taskboard-kv)
KEYVAULT_NAME="${KEYVAULT_NAME:-${PROJECT}-kv}"

# Optional: re-apply captured config from an export (see LOCAL-EXPORT-RUNBOOK.md).
# Point these at files produced by export-azure.sh / Export-Azure.ps1 to recreate
# the environment's app settings and secrets in one run.
IMPORT_SETTINGS_FILE="${IMPORT_SETTINGS_FILE:-}"   # e.g. azure-export/taskboard-06-api.settings.env
IMPORT_SECRETS_FILE="${IMPORT_SECRETS_FILE:-}"     # e.g. azure-export/keyvault-taskboard-kv.secrets.json (or .env)

# Optional storage + static website (NOT in the live env; off by default)
ENABLE_STORAGE="${ENABLE_STORAGE:-false}"
STORAGE_ACCOUNT="${STORAGE_ACCOUNT:-st${PROJECT}$RANDOM}"
STORAGE_CONTAINER="${STORAGE_CONTAINER:-app-data}"
STORAGE_SKU="${STORAGE_SKU:-Standard_LRS}"
STATIC_INDEX="${STATIC_INDEX:-index.html}"
STATIC_404="${STATIC_404:-404.html}"
STATIC_UPLOAD_SAMPLE="${STATIC_UPLOAD_SAMPLE:-1}"

TAGS="project=${PROJECT} environment=${ENVIRONMENT} managedBy=provision.sh"

# ---- Helpers ----------------------------------------------------------------
log()  { printf '\n\033[1;34m==>\033[0m %s\n' "$*"; }
ok()   { printf '\033[1;32m  ✓\033[0m %s\n' "$*"; }

STORAGE_ACCOUNT="$(echo "$STORAGE_ACCOUNT" | tr -cd 'a-z0-9' | cut -c1-24)"

print_plan() {
  cat <<EOF
Planned deployment
------------------------------------------------------------
  Resource group     : $RESOURCE_GROUP        ($LOCATION)
  App Service plan   : $APP_SERVICE_PLAN      (Linux, $APP_SKU)
  Web app            : $WEBAPP_NAME           (runtime $RUNTIME)
  SQL server         : $SQL_SERVER_NAME
  SQL database       : $SQL_DB_NAME           (GP serverless Gen5, free-limit=$SQL_USE_FREE_LIMIT)
                       vCores ${SQL_MIN_VCORES}-${SQL_MAX_VCORES}, auto-pause ${SQL_AUTO_PAUSE_MIN}m
  SQL admin user     : $SQL_ADMIN_USER
  User-assigned MI   : $UAMI_NAME             (OIDC / CI-CD)
  Key Vault          : $KEYVAULT_NAME
  Storage + static   : $( [[ "$ENABLE_STORAGE" == "true" ]] && echo "ENABLED ($STORAGE_ACCOUNT)" || echo "disabled (set ENABLE_STORAGE=true)" )
  Import secrets     : ${IMPORT_SECRETS_FILE:-<none>}
  Import settings    : ${IMPORT_SETTINGS_FILE:-<none>}
------------------------------------------------------------
EOF
}

case "${1:-}" in
  -h|--help)
    grep '^#' "$0" | sed 's/^#//'; exit 0;;
  --what-if|--dry-run)
    print_plan; echo "(--what-if) No resources created."; exit 0;;
  "")
    ;;
  *)
    echo "Unknown arg: $1" >&2
    echo "Try: ./provision.sh --help" >&2
    exit 1;;
esac

# ---- Preflight ---------------------------------------------------------------
command -v az >/dev/null 2>&1 || { echo "ERROR: Azure CLI (az) not found." >&2; exit 1; }
az account show >/dev/null 2>&1 || { echo "ERROR: Not logged in. Run 'az login'." >&2; exit 1; }

SUBSCRIPTION_ID="$(az account show --query id -o tsv)"
print_plan
log "Using subscription: $SUBSCRIPTION_ID"

if [[ -z "$SQL_ADMIN_PASSWORD" ]]; then
  SQL_ADMIN_PASSWORD="$(openssl rand -base64 18)Aa1!"
  GENERATED_PW=1
else
  GENERATED_PW=0
fi

# ---- 1. Resource group -------------------------------------------------------
log "Creating resource group '$RESOURCE_GROUP'"
az group create --name "$RESOURCE_GROUP" --location "$LOCATION" --tags $TAGS --output none
ok "Resource group ready"

# ---- 2. User-assigned managed identity (OIDC / CI-CD) -----------------------
log "Creating user-assigned managed identity '$UAMI_NAME'"
az identity create --name "$UAMI_NAME" --resource-group "$RESOURCE_GROUP" \
  --location "$LOCATION" --tags $TAGS --output none
UAMI_ID="$(az identity show -n "$UAMI_NAME" -g "$RESOURCE_GROUP" --query id -o tsv)"
UAMI_PRINCIPAL_ID="$(az identity show -n "$UAMI_NAME" -g "$RESOURCE_GROUP" --query principalId -o tsv)"
UAMI_CLIENT_ID="$(az identity show -n "$UAMI_NAME" -g "$RESOURCE_GROUP" --query clientId -o tsv)"
ok "Managed identity ready (clientId: $UAMI_CLIENT_ID)"

# To let GitHub Actions / Azure DevOps deploy WITHOUT secrets, add a federated
# credential to this identity (fill in your org/repo/branch), then use it in CI:
#   az identity federated-credential create \
#     --name github-main --identity-name "$UAMI_NAME" -g "$RESOURCE_GROUP" \
#     --issuer https://token.actions.githubusercontent.com \
#     --subject "repo:<ORG>/<REPO>:ref:refs/heads/main" \
#     --audiences api://AzureADTokenExchange

# ---- 3. App Service plan (Linux) + Web app ----------------------------------
log "Creating Linux App Service plan '$APP_SERVICE_PLAN' ($APP_SKU)"
az appservice plan create \
  --name "$APP_SERVICE_PLAN" \
  --resource-group "$RESOURCE_GROUP" \
  --location "$LOCATION" \
  --is-linux \
  --sku "$APP_SKU" \
  --tags $TAGS \
  --output none
ok "App Service plan ready"

log "Creating web app '$WEBAPP_NAME'"
az webapp create \
  --name "$WEBAPP_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --plan "$APP_SERVICE_PLAN" \
  --runtime "$RUNTIME" \
  --tags $TAGS \
  --output none 2>/dev/null || \
az webapp create \
  --name "$WEBAPP_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --plan "$APP_SERVICE_PLAN" \
  --output none
az webapp update -n "$WEBAPP_NAME" -g "$RESOURCE_GROUP" --set httpsOnly=true --output none

# Runtime identity: system-assigned, used to read Key Vault secrets.
# (The user-assigned oidc-msi identity above is standalone — it's the CI/CD
#  federated-deploy identity and does not need to be attached to the app.)
# NOTE: F1 (Free) supports managed identity but not Always On / deployment slots.
WEBAPP_PRINCIPAL_ID="$(az webapp identity assign \
  --name "$WEBAPP_NAME" --resource-group "$RESOURCE_GROUP" \
  --query principalId -o tsv)"
ok "Web app ready (system identity: $WEBAPP_PRINCIPAL_ID)"

# ---- 4. Azure SQL server + serverless database (free limit) -----------------
log "Creating Azure SQL server '$SQL_SERVER_NAME'"
az sql server create \
  --name "$SQL_SERVER_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --location "$LOCATION" \
  --admin-user "$SQL_ADMIN_USER" \
  --admin-password "$SQL_ADMIN_PASSWORD" \
  --output none
ok "SQL server ready"

log "Creating serverless SQL database '$SQL_DB_NAME'"
FREE_LIMIT_ARGS=()
if [[ "$SQL_USE_FREE_LIMIT" == "true" ]]; then
  # The Azure SQL free offer allows ONE free-limit database per subscription.
  FREE_LIMIT_ARGS=(--use-free-limit --free-limit-exhaustion-behavior AutoPause)
fi
az sql db create \
  --name "$SQL_DB_NAME" \
  --server "$SQL_SERVER_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --edition GeneralPurpose \
  --compute-model Serverless \
  --family Gen5 \
  --capacity "$SQL_MAX_VCORES" \
  --min-capacity "$SQL_MIN_VCORES" \
  --auto-pause-delay "$SQL_AUTO_PAUSE_MIN" \
  --backup-storage-redundancy Local \
  "${FREE_LIMIT_ARGS[@]}" \
  --output none
ok "SQL database ready (serverless, min ${SQL_MIN_VCORES} / max ${SQL_MAX_VCORES} vCores)"

log "Configuring SQL firewall (allow Azure services)"
az sql server firewall-rule create \
  --resource-group "$RESOURCE_GROUP" --server "$SQL_SERVER_NAME" \
  --name AllowAzureServices --start-ip-address 0.0.0.0 --end-ip-address 0.0.0.0 \
  --output none
ok "Firewall rule set"

# ---- 5. Key Vault + secrets --------------------------------------------------
log "Creating Key Vault '$KEYVAULT_NAME'"
az keyvault create \
  --name "$KEYVAULT_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --location "$LOCATION" \
  --enable-rbac-authorization false \
  --tags $TAGS \
  --output none
ok "Key Vault ready"

SQL_CONNECTION_STRING="Server=tcp:${SQL_SERVER_NAME}.database.windows.net,1433;Initial Catalog=${SQL_DB_NAME};Persist Security Info=False;User ID=${SQL_ADMIN_USER};Password=${SQL_ADMIN_PASSWORD};MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"

log "Storing secrets in Key Vault"
az keyvault secret set --vault-name "$KEYVAULT_NAME" --name SqlConnectionString --value "$SQL_CONNECTION_STRING" --output none
az keyvault secret set --vault-name "$KEYVAULT_NAME" --name SqlAdminPassword --value "$SQL_ADMIN_PASSWORD" --output none
ok "Secrets stored"

log "Granting identities access to Key Vault secrets"
# App runtime (system-assigned) + the user-assigned/CI identity get read access.
az keyvault set-policy --name "$KEYVAULT_NAME" --object-id "$WEBAPP_PRINCIPAL_ID" \
  --secret-permissions get list --output none
az keyvault set-policy --name "$KEYVAULT_NAME" --object-id "$UAMI_PRINCIPAL_ID" \
  --secret-permissions get list --output none
ok "Access policies set"

# ---- 5b. Import captured secrets (e.g. Jwt--Key) into the new Key Vault ------
if [[ -n "$IMPORT_SECRETS_FILE" ]]; then
  if [[ ! -f "$IMPORT_SECRETS_FILE" ]]; then
    echo "WARNING: IMPORT_SECRETS_FILE '$IMPORT_SECRETS_FILE' not found — skipping." >&2
  else
    log "Importing secrets from '$IMPORT_SECRETS_FILE' into '$KEYVAULT_NAME'"
    if [[ "$IMPORT_SECRETS_FILE" == *.json ]]; then
      # JSON: array of {Name,Value} (as produced by the export). python passes
      # values straight to az as argv, so special characters are safe.
      python3 - "$IMPORT_SECRETS_FILE" "$KEYVAULT_NAME" <<'PY'
import json, sys, subprocess
data = json.load(open(sys.argv[1])); kv = sys.argv[2]
items = data if isinstance(data, list) else [data]
n = 0
for it in items:
    name = it.get("Name") or it.get("name")
    val  = it.get("Value")
    if val is None: val = it.get("value")
    if not name: continue
    subprocess.run(["az","keyvault","secret","set","--vault-name",kv,
                    "--name",name,"--value","" if val is None else str(val),
                    "--output","none"], check=False)
    n += 1
print(f"  imported {n} secret(s)")
PY
    else
      # KEY=value lines (skip comments/blank)
      while IFS= read -r line; do
        [[ -z "$line" || "$line" == \#* ]] && continue
        name="${line%%=*}"; val="${line#*=}"
        [[ -z "$name" ]] && continue
        az keyvault secret set --vault-name "$KEYVAULT_NAME" --name "$name" --value "$val" --output none
      done < "$IMPORT_SECRETS_FILE"
    fi
    ok "Secrets imported"
  fi
fi

# ---- 6. Optional storage account + static website ---------------------------
STATIC_WEB_URL=""
if [[ "$ENABLE_STORAGE" == "true" ]]; then
  log "Creating storage account '$STORAGE_ACCOUNT'"
  az storage account create \
    --name "$STORAGE_ACCOUNT" --resource-group "$RESOURCE_GROUP" --location "$LOCATION" \
    --sku "$STORAGE_SKU" --kind StorageV2 --min-tls-version TLS1_2 \
    --allow-blob-public-access false --tags $TAGS --output none
  ok "Storage account ready"

  STORAGE_KEY="$(az storage account keys list --account-name "$STORAGE_ACCOUNT" \
    --resource-group "$RESOURCE_GROUP" --query '[0].value' -o tsv)"

  log "Creating blob container '$STORAGE_CONTAINER'"
  az storage container create --name "$STORAGE_CONTAINER" \
    --account-name "$STORAGE_ACCOUNT" --account-key "$STORAGE_KEY" --output none
  ok "Container ready"

  log "Enabling static website hosting"
  az storage blob service-properties update --account-name "$STORAGE_ACCOUNT" \
    --account-key "$STORAGE_KEY" --static-website \
    --index-document "$STATIC_INDEX" --404-document "$STATIC_404" --output none
  STATIC_WEB_URL="$(az storage account show --name "$STORAGE_ACCOUNT" \
    --resource-group "$RESOURCE_GROUP" --query 'primaryEndpoints.web' -o tsv)"
  ok "Static website enabled"

  if [[ "$STATIC_UPLOAD_SAMPLE" == "1" ]]; then
    log "Seeding starter pages into \$web"
    TMP_SITE="$(mktemp -d)"
    cat > "$TMP_SITE/$STATIC_INDEX" <<HTML
<!doctype html><html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${PROJECT} — static site</title>
<style>body{font-family:system-ui,Segoe UI,Arial,sans-serif;margin:0;display:grid;
place-items:center;min-height:100vh;background:#0b1220;color:#e6edf3}
.card{padding:2.5rem 3rem;border:1px solid #223;border-radius:16px;background:#111a2e;text-align:center}</style>
</head><body><div class="card"><h1>${PROJECT} static site is live 🚀</h1>
<p>Served from Azure Storage static website hosting.</p></div></body></html>
HTML
    cat > "$TMP_SITE/$STATIC_404" <<HTML
<!doctype html><html lang="en"><head><meta charset="utf-8"><title>404</title></head>
<body style="font-family:system-ui;text-align:center;margin-top:15vh"><h1>404</h1>
<p>That page doesn't exist.</p><a href="/">Go home</a></body></html>
HTML
    az storage blob upload-batch --account-name "$STORAGE_ACCOUNT" --account-key "$STORAGE_KEY" \
      --source "$TMP_SITE" --destination '$web' --overwrite true --output none
    rm -rf "$TMP_SITE"
    ok "Starter pages uploaded"
  fi

  STORAGE_CONNECTION_STRING="$(az storage account show-connection-string \
    --name "$STORAGE_ACCOUNT" --resource-group "$RESOURCE_GROUP" --query connectionString -o tsv)"
  az keyvault secret set --vault-name "$KEYVAULT_NAME" --name StorageConnectionString \
    --value "$STORAGE_CONNECTION_STRING" --output none
  ok "Storage connection string stored in Key Vault"
fi

# ---- 7. Wire app settings (Key Vault references + imported settings) --------
log "Configuring web app settings"
APP_SETTINGS=("SqlConnectionString=@Microsoft.KeyVault(VaultName=${KEYVAULT_NAME};SecretName=SqlConnectionString)")
if [[ "$ENABLE_STORAGE" == "true" ]]; then
  APP_SETTINGS+=("StorageConnectionString=@Microsoft.KeyVault(VaultName=${KEYVAULT_NAME};SecretName=StorageConnectionString)")
fi

# Merge in captured app settings (e.g. Jwt__Issuer, Jwt__Audience, ASPNETCORE_*).
# @kv:<SecretName> values are rewritten to reference THIS deployment's Key Vault.
if [[ -n "$IMPORT_SETTINGS_FILE" ]]; then
  if [[ ! -f "$IMPORT_SETTINGS_FILE" ]]; then
    echo "WARNING: IMPORT_SETTINGS_FILE '$IMPORT_SETTINGS_FILE' not found — skipping." >&2
  else
    log "Applying captured app settings from '$IMPORT_SETTINGS_FILE'"
    imported=0
    while IFS= read -r line; do
      [[ -z "$line" || "$line" == \#* ]] && continue
      [[ "$line" != *"="* ]] && continue
      key="${line%%=*}"; val="${line#*=}"
      [[ -z "$key" ]] && continue
      # Don't let a stale captured value clobber our freshly-built connection string.
      [[ "$key" == "SqlConnectionString" ]] && continue
      if [[ "$val" == @kv:* ]]; then
        val="@Microsoft.KeyVault(VaultName=${KEYVAULT_NAME};SecretName=${val#@kv:})"
      fi
      APP_SETTINGS+=("${key}=${val}")
      imported=$((imported+1))
    done < "$IMPORT_SETTINGS_FILE"
    ok "Merged $imported captured setting(s)"
  fi
fi

# `az ... appsettings set --settings` MERGES: it only touches the keys listed,
# leaving any other existing settings on the app intact.
az webapp config appsettings set --name "$WEBAPP_NAME" --resource-group "$RESOURCE_GROUP" \
  --settings "${APP_SETTINGS[@]}" --output none
ok "App settings configured"

# ---- Summary -----------------------------------------------------------------
log "Deployment complete 🎉"
cat <<EOF

Resources created in resource group: $RESOURCE_GROUP
------------------------------------------------------------
  Web app URL        : https://${WEBAPP_NAME}.azurewebsites.net
  SQL server FQDN    : ${SQL_SERVER_NAME}.database.windows.net
  SQL database       : $SQL_DB_NAME  (serverless, free-limit=$SQL_USE_FREE_LIMIT)
  User-assigned MI   : $UAMI_NAME  (clientId $UAMI_CLIENT_ID)
  Key Vault          : $KEYVAULT_NAME
$( [[ "$ENABLE_STORAGE" == "true" ]] && echo "  Static website URL : $STATIC_WEB_URL" )
EOF

if [[ "$GENERATED_PW" == "1" ]]; then
  echo
  echo "NOTE: SQL admin password generated & stored in Key Vault. Retrieve with:"
  echo "  az keyvault secret show --vault-name $KEYVAULT_NAME --name SqlAdminPassword --query value -o tsv"
fi

if [[ -n "$IMPORT_SETTINGS_FILE" || -n "$IMPORT_SECRETS_FILE" ]]; then
  cat <<EOF

NOTE: captured config was imported. If your app loads Key Vault via the
      configuration provider (as taskboard does for 'Jwt--Key'), make sure the
      app points at THIS vault ($KEYVAULT_NAME) — update any captured setting
      that holds the old vault URI (e.g. KeyVault__Uri / VaultUri).
EOF
fi
echo
echo "Tear down:  az group delete --name $RESOURCE_GROUP --yes --no-wait"
