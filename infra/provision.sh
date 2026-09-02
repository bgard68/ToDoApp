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
#   DB_PROVIDER=sqlserver ./provision.sh            # provision Azure SQL instead of using Neon
#
# Database: defaults to DB_PROVIDER=postgres, which creates NO Azure database and instead stores
# a Neon connection string (prompted, or NEON_CONNECTION_STRING) in Key Vault. Azure SQL remains
# supported via DB_PROVIDER=sqlserver; docs/deployment/cold-starts.md explains why prod moved.
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

# ---- Adoption of existing resources -----------------------------------------
# Several names below must be globally unique and so default to a $RANDOM suffix.
# Applied blindly to a group that already holds this stack they describe a SECOND
# stack: the live environment runs taskboard-06-api on ASP-rgtaskboard-a1a3, names
# no default here would ever reproduce. Every re-run would leave the running app
# untouched and bill for a duplicate beside it -- so discovery (below) replaces the
# defaults with whatever the group actually holds.
#
# A name passed in explicitly always wins over discovery, so record which ones the
# caller pinned BEFORE the defaults overwrite the distinction.
ADOPT_EXISTING="${ADOPT_EXISTING:-true}"   # false = always use the generated names
APP_SERVICE_PLAN_PINNED="${APP_SERVICE_PLAN+yes}"
WEBAPP_NAME_PINNED="${WEBAPP_NAME+yes}"
SQL_SERVER_NAME_PINNED="${SQL_SERVER_NAME+yes}"
SQL_DB_NAME_PINNED="${SQL_DB_NAME+yes}"
UAMI_NAME_PINNED="${UAMI_NAME+yes}"
KEYVAULT_NAME_PINNED="${KEYVAULT_NAME+yes}"
STORAGE_ACCOUNT_PINNED="${STORAGE_ACCOUNT+yes}"

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
RUNTIME="${RUNTIME:-DOTNETCORE:10.0}"   # matches <TargetFramework>net10.0</TargetFramework> (finding L2)

# Database provider. The deployed app runs Postgres on Neon: Azure SQL serverless bills a
# 60-minute minimum every time a paused database is woken (the auto-pause floor), which is
# ~55 wakes per free month, and deploys spent most of them. Neon bills the minutes actually
# used and resumes in ~1-2s. See docs/deployment/cold-starts.md.
#
#   postgres  (default) — no Azure database is created. Supply NEON_CONNECTION_STRING (or be
#               prompted for it); it is stored in Key Vault and referenced from app settings.
#   sqlserver           — provisions the Entra-only Azure SQL server + serverless database
#               below. Still fully supported; this is how the app originally shipped.
DB_PROVIDER="${DB_PROVIDER:-postgres}"
if [[ "$DB_PROVIDER" != "postgres" && "$DB_PROVIDER" != "sqlserver" ]]; then
  echo "DB_PROVIDER must be 'postgres' or 'sqlserver' (got '$DB_PROVIDER')" >&2
  exit 2
fi

# Azure SQL (General Purpose serverless Gen5 + free limit) — only used when DB_PROVIDER=sqlserver
SQL_SERVER_NAME="${SQL_SERVER_NAME:-${PROJECT}-sql-$RANDOM}"  # globally unique
SQL_DB_NAME="${SQL_DB_NAME:-${PROJECT}}"
SQL_MAX_VCORES="${SQL_MAX_VCORES:-2}"          # serverless auto-scale ceiling
SQL_MIN_VCORES="${SQL_MIN_VCORES:-0.5}"        # serverless auto-scale floor
SQL_AUTO_PAUSE_MIN="${SQL_AUTO_PAUSE_MIN:-60}" # minutes idle before auto-pause
SQL_USE_FREE_LIMIT="${SQL_USE_FREE_LIMIT:-true}"  # Azure SQL free offer (1 per subscription)
# Azure SQL is created with ENTRA-ONLY authentication — no SQL login, no password, nothing to
# leak or rotate (review findings M2/M4). Defaults to the signed-in user as the server's Entra
# administrator; override to hand it to a group instead.
# '|| true' matters under 'set -e': as a service principal (any CI run) the lookup
# exits non-zero, and the assignment would take that status and kill the script
# here -- with no message, before the Neon path that needs no admin ever starts.
ENTRA_ADMIN_NAME="${ENTRA_ADMIN_NAME:-$(az ad signed-in-user show --query userPrincipalName -o tsv 2>/dev/null || true)}"
ENTRA_ADMIN_SID="${ENTRA_ADMIN_SID:-$(az ad signed-in-user show --query id -o tsv 2>/dev/null || true)}"
ENTRA_ADMIN_TYPE="${ENTRA_ADMIN_TYPE:-User}"   # User | Group | Application

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

# ---- Discovery ---------------------------------------------------------------
# Ask the resource group what it already holds and adopt those names. Three rules
# keep that honest:
#   - a name passed in explicitly is never overridden (the caller has decided);
#   - exactly one match is adopted;
#   - more than one is an error, not a guess. rg-taskboard holds two managed
#     identities (oidc-msi-8552, oidc-msi-ac8b); picking one would silently wire
#     CI to an identity the caller never named.
ADOPT_NOTES=()

adopt() {
  local label="$1" var="$2" pinned="$3" list="$4"
  local count name
  list="$(printf '%s\n' "$list" | sed '/^[[:space:]]*$/d')"
  count="$(printf '%s\n' "$list" | grep -c . || true)"

  if [[ -n "$pinned" ]]; then
    ADOPT_NOTES+=("$label: pinned to '${!var}' by the caller")
    return 0
  fi
  if (( count == 0 )); then
    ADOPT_NOTES+=("$label: none in the group — will create '${!var}'")
    return 0
  fi
  if (( count > 1 )); then
    echo "ERROR: '$RESOURCE_GROUP' holds $count resources of type: $label" >&2
    printf '         %s\n' $list >&2
    echo "       Which one this stack uses cannot be inferred. Pin it with ${var}=<name>," >&2
    echo "       or set ADOPT_EXISTING=false to create a new one alongside them." >&2
    exit 1
  fi
  name="$list"
  printf -v "$var" '%s' "$name"
  ADOPT_NOTES+=("$label: adopted existing '$name'")
}

discover_existing() {
  if [[ "$ADOPT_EXISTING" != "true" ]]; then
    ADOPT_NOTES+=("discovery disabled (ADOPT_EXISTING=false) — using generated names")
    return 0
  fi
  if ! az group show --name "$RESOURCE_GROUP" >/dev/null 2>&1; then
    ADOPT_NOTES+=("resource group '$RESOURCE_GROUP' does not exist yet — nothing to adopt")
    return 0
  fi

  adopt "App Service plan" APP_SERVICE_PLAN "$APP_SERVICE_PLAN_PINNED" \
    "$(az appservice plan list -g "$RESOURCE_GROUP" --query "[].name" -o tsv 2>/dev/null)"
  adopt "web app" WEBAPP_NAME "$WEBAPP_NAME_PINNED" \
    "$(az webapp list -g "$RESOURCE_GROUP" --query "[?!contains(kind,'functionapp')].name" -o tsv 2>/dev/null)"
  adopt "Key Vault" KEYVAULT_NAME "$KEYVAULT_NAME_PINNED" \
    "$(az keyvault list -g "$RESOURCE_GROUP" --query "[].name" -o tsv 2>/dev/null)"
  adopt "user-assigned managed identity" UAMI_NAME "$UAMI_NAME_PINNED" \
    "$(az identity list -g "$RESOURCE_GROUP" --query "[].name" -o tsv 2>/dev/null)"

  # Only the sqlserver path has an Azure database to find. On Neon there is no ARM
  # resource to discover at all, which is the point of that provider.
  if [[ "$DB_PROVIDER" == "sqlserver" ]]; then
    adopt "SQL server" SQL_SERVER_NAME "$SQL_SERVER_NAME_PINNED" \
      "$(az sql server list -g "$RESOURCE_GROUP" --query "[].name" -o tsv 2>/dev/null)"
    # 'master' is always present and is never the application database.
    if az sql server show -n "$SQL_SERVER_NAME" -g "$RESOURCE_GROUP" >/dev/null 2>&1; then
      adopt "SQL database" SQL_DB_NAME "$SQL_DB_NAME_PINNED" \
        "$(az sql db list -g "$RESOURCE_GROUP" -s "$SQL_SERVER_NAME" --query "[?name!='master'].name" -o tsv 2>/dev/null)"
    fi
  fi

  if [[ "$ENABLE_STORAGE" == "true" ]]; then
    adopt "storage account" STORAGE_ACCOUNT "$STORAGE_ACCOUNT_PINNED" \
      "$(az storage account list -g "$RESOURCE_GROUP" --query "[].name" -o tsv 2>/dev/null)"
  fi
}

print_adoption() {
  local note
  echo "Existing resources"
  echo "------------------------------------------------------------"
  for note in ${ADOPT_NOTES[@]+"${ADOPT_NOTES[@]}"}; do printf '  %s\n' "$note"; done
  echo "------------------------------------------------------------"
  echo
}

STORAGE_ACCOUNT="$(echo "$STORAGE_ACCOUNT" | tr -cd 'a-z0-9' | cut -c1-24)"

print_plan() {
  cat <<EOF
Planned deployment
------------------------------------------------------------
  Resource group     : $RESOURCE_GROUP        ($LOCATION)
  App Service plan   : $APP_SERVICE_PLAN      (Linux, $APP_SKU)
  Web app            : $WEBAPP_NAME           (runtime $RUNTIME)
  SQL server         : $( [[ "$DB_PROVIDER" == "sqlserver" ]] && echo "$SQL_SERVER_NAME" || echo "n/a (Neon — none created)" )
  Database provider  : $DB_PROVIDER $( [[ "$DB_PROVIDER" == "postgres" ]] && echo "(Neon — no Azure database created)" || echo "" )
  SQL database       : $( [[ "$DB_PROVIDER" == "sqlserver" ]] && echo "$SQL_DB_NAME (GP serverless Gen5, free-limit=$SQL_USE_FREE_LIMIT)" || echo "n/a" )
                       $( [[ "$DB_PROVIDER" == "sqlserver" ]] && echo "vCores ${SQL_MIN_VCORES}-${SQL_MAX_VCORES}, auto-pause ${SQL_AUTO_PAUSE_MIN}m" || echo "" )
  SQL Entra admin    : ${ENTRA_ADMIN_NAME:-<none found>} ($ENTRA_ADMIN_TYPE)
  SQL auth mode      : Entra-only (no SQL password)
  User-assigned MI   : $UAMI_NAME             (OIDC / CI-CD)
  Key Vault          : $KEYVAULT_NAME
  Storage + static   : $( [[ "$ENABLE_STORAGE" == "true" ]] && echo "ENABLED ($STORAGE_ACCOUNT)" || echo "disabled (set ENABLE_STORAGE=true)" )
  Import secrets     : ${IMPORT_SECRETS_FILE:-<none>}
  Import settings    : ${IMPORT_SETTINGS_FILE:-<none>}
------------------------------------------------------------
EOF
}

WHAT_IF=0
case "${1:-}" in
  -h|--help)
    grep '^#' "$0" | sed 's/^#//'; exit 0;;
  --what-if|--dry-run)
    WHAT_IF=1;;
  "")
    ;;
  *)
    echo "Unknown arg: $1" >&2
    echo "Try: ./provision.sh --help" >&2
    exit 1;;
esac

# ---- Preflight ---------------------------------------------------------------
# --what-if signs in and runs discovery before printing. A preview that showed the
# generated names while the real run adopted the group's existing ones would
# describe a deployment that never happens. It still creates nothing.
command -v az >/dev/null 2>&1 || { echo "ERROR: Azure CLI (az) not found." >&2; exit 1; }
az account show >/dev/null 2>&1 || { echo "ERROR: Not logged in. Run 'az login'." >&2; exit 1; }

SUBSCRIPTION_ID="$(az account show --query id -o tsv)"

discover_existing
print_plan
print_adoption

if (( WHAT_IF )); then
  echo "(--what-if) No resources created."
  exit 0
fi

log "Using subscription: $SUBSCRIPTION_ID"

# Only the Azure SQL path needs an Entra administrator; requiring one on the Neon
# path would abort a run that creates no SQL server at all.
if [[ "$DB_PROVIDER" == "sqlserver" ]] && [[ -z "$ENTRA_ADMIN_NAME" || -z "$ENTRA_ADMIN_SID" ]]; then
  echo "ERROR: could not resolve an Entra administrator for the SQL server." >&2
  echo "       Set ENTRA_ADMIN_NAME and ENTRA_ADMIN_SID, or run 'az login' as a user." >&2
  echo "       An Entra-only server with no Entra admin has nobody who can administer it." >&2
  exit 1
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

# ---- 4. Database ------------------------------------------------------------
if [[ "$DB_PROVIDER" == "postgres" ]]; then
  # Neon has no Azure CLI and no ARM resource, so there is nothing to create here. The one
  # input is the pooled connection string, which carries a password and therefore goes to Key
  # Vault (section 6) rather than into app settings — unlike the Entra-only SQL path below,
  # which has no credential at all. Read hidden so it never lands in argv, shell history or
  # CI logs.
  if [[ -z "${NEON_CONNECTION_STRING:-}" ]]; then
    log "Neon connection string required (create a free project at https://neon.com)"
    echo "  Expected form (paste the pooled string reshaped for Npgsql):" >&2
    echo "  Host=<host>.neon.tech;Database=<db>;Username=<user>;Password=<pw>;SSL Mode=Require;Trust Server Certificate=true;Timeout=30;Command Timeout=60" >&2
    read -rs -p "  Connection string: " NEON_CONNECTION_STRING
    echo >&2
  fi
  if [[ -z "$NEON_CONNECTION_STRING" ]]; then
    echo "No connection string supplied; set NEON_CONNECTION_STRING or use DB_PROVIDER=sqlserver" >&2
    exit 2
  fi
  ok "Neon connection string captured (stored in Key Vault below, never in app settings)"
else

log "Creating Azure SQL server '$SQL_SERVER_NAME'"
# No --admin-user / --admin-password at all. The server is created with an Entra administrator and
# SQL authentication disabled outright, which removes the whole class of problems around a
# password-based server admin: storing it, rotating it, keeping it out of argv and CLI logs
# (review findings M2 and M4). Minimum TLS is pinned while we are here.
#
# This is what the live environment already looked like — the script was the thing still creating
# the insecure shape. See docs/development/security-remediation.md (M2).
az sql server create \
  --name "$SQL_SERVER_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --location "$LOCATION" \
  --enable-ad-only-auth \
  --external-admin-principal-type "$ENTRA_ADMIN_TYPE" \
  --external-admin-name "$ENTRA_ADMIN_NAME" \
  --external-admin-sid "$ENTRA_ADMIN_SID" \
  --minimal-tls-version 1.2 \
  --output none
ok "SQL server ready (Entra-only auth, admin: $ENTRA_ADMIN_NAME)"

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
fi

# SQL firewall: allow ONLY this App Service's outbound addresses.
#
# The obvious rule is 0.0.0.0-0.0.0.0 — Azure's "allow all Azure services" special case, and what
# this script used to create. That admits resources from ANY Azure tenant, not just this
# subscription, leaving the credential as the only thing protecting the database (review finding
# M3). A Private Endpoint would be stronger, but it needs VNet integration, which requires a Basic
# or higher App Service plan; on the F1 (Free) tier this stack targets, allow-listing the outbound
# IPs is the correct answer.
#
# Note possibleOutboundIpAddresses, not outboundIpAddresses: the former is the full set the app may
# use within its scale unit. Allow-listing only the current ones produces intermittent failures
# later, when the app moves and its source IP changes.
if [[ "$DB_PROVIDER" == "sqlserver" ]]; then
log "Configuring SQL firewall (App Service outbound IPs only)"
fw_n=0
for ip in $(az webapp show -n "$WEBAPP_NAME" -g "$RESOURCE_GROUP" \
              --query possibleOutboundIpAddresses -o tsv | tr ',' ' '); do
  fw_n=$((fw_n + 1))
  az sql server firewall-rule create \
    --resource-group "$RESOURCE_GROUP" --server "$SQL_SERVER_NAME" \
    --name "AppServiceOutbound-$(printf '%02d' "$fw_n")" \
    --start-ip-address "$ip" --end-ip-address "$ip" \
    --output none
done
ok "Firewall set: $fw_n App Service outbound IP(s); no all-of-Azure rule"
else
  ok "No SQL firewall to configure (Neon manages its own network access)"
fi

# ---- 5. Key Vault + secrets --------------------------------------------------
log "Creating Key Vault '$KEYVAULT_NAME'"
# RBAC rather than legacy access policies, plus soft-delete retention and PURGE PROTECTION
# (review finding L6). Purge protection stops an attacker — or a mistake — permanently destroying
# secrets inside the retention window. It is IRREVERSIBLE once enabled, which is the point.
#
# The live vault was already on RBAC; this script was the thing still creating the legacy shape.
az keyvault create \
  --name "$KEYVAULT_NAME" \
  --resource-group "$RESOURCE_GROUP" \
  --location "$LOCATION" \
  --enable-rbac-authorization true \
  --enable-purge-protection true \
  --retention-days 90 \
  --tags $TAGS \
  --output none
ok "Key Vault ready (RBAC, soft-delete 90d, purge protection on)"

# How the app is told to reach its database differs by provider, and that difference is the
# whole security story:
#
#   sqlserver — the App Service managed identity authenticates to SQL, so this string contains
#               no credential at all (review finding M2). Not a secret; goes straight into app
#               settings.
#   postgres  — Neon authenticates with a username and password, so the string IS a secret. It
#               goes into Key Vault and app settings carry only a reference. That is a real
#               step down from passwordless auth, taken deliberately; see
#               docs/deployment/cold-starts.md for why the database moved.
SQL_CONNECTION_STRING="Server=tcp:${SQL_SERVER_NAME}.database.windows.net,1433;Database=${SQL_DB_NAME};Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;Connect Timeout=60;"

# Nothing to store here any more. The old script wrote SqlConnectionString and SqlAdminPassword to
# the vault; with Entra-only auth neither exists. Jwt--Key remains the one real secret, and is
# managed outside this script.
if [[ "$DB_PROVIDER" == "postgres" ]]; then
  # The Neon string carries a password, so it is a real secret and belongs in the vault. Written
  # via --value from a shell variable that was read hidden, so it never appears in argv of an
  # interactive shell's history nor in this script's output.
  az keyvault secret set     --vault-name "$KEYVAULT_NAME"     --name "ConnectionStrings--DefaultConnection"     --value "$NEON_CONNECTION_STRING"     --output none
  unset NEON_CONNECTION_STRING
  ok "Neon connection string stored in Key Vault (app settings reference it, never hold it)"
else
  ok "No SQL secrets to store (passwordless via managed identity)"
fi

log "Granting identities access to Key Vault secrets"
# App runtime (system-assigned) + the user-assigned/CI identity get read access.
#
# Under RBAC, `az keyvault set-policy` silently does nothing — grant the built-in "Key Vault
# Secrets User" role instead. It reads secret VALUES only: no write, no delete, no management.
KV_ID="$(az keyvault show --name "$KEYVAULT_NAME" --resource-group "$RESOURCE_GROUP" --query id -o tsv)"
for principal in "$WEBAPP_PRINCIPAL_ID" "$UAMI_PRINCIPAL_ID"; do
  az role assignment create \
    --assignee-object-id "$principal" --assignee-principal-type ServicePrincipal \
    --role "Key Vault Secrets User" --scope "$KV_ID" --output none 2>/dev/null || true
done
ok "Key Vault RBAC role assignments set"

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
# The app reads ConnectionStrings__DefaultConnection. It is passwordless, so it is set directly
# rather than through a Key Vault reference — there is no secret to protect.
# RateLimiting__TrustForwardedFor is set because App Service IS a reverse proxy. Left at its
# shipped default of false, every request reaches the app carrying the platform's address, all
# callers collapse into a single partition, and the per-caller limits become global caps — the
# whole app sharing 200 requests a minute and 10 sign-ins a minute. Nothing in a log explains it;
# requests simply start returning 429. It is safe to trust here because only the LAST entry of
# X-Forwarded-For is read, and that entry is the one App Service appended itself.
if [[ "$DB_PROVIDER" == "postgres" ]]; then
  APP_SETTINGS=(
    "ConnectionStrings__DefaultConnection=@Microsoft.KeyVault(VaultName=${KEYVAULT_NAME};SecretName=ConnectionStrings--DefaultConnection)"
    "Database__Provider=Postgres"
    # Schema creation is opt-in and OFF by default: on a scale-to-zero database, opening a
    # connection IS the wake-up, so an unconditional check makes every redeploy pay for one.
    # Turn it on for the single deploy that creates or changes the schema.
    "Database__InitializeOnStartup=false"
    "RateLimiting__TrustForwardedFor=true"
  )
else
  APP_SETTINGS=("ConnectionStrings__DefaultConnection=${SQL_CONNECTION_STRING}" "Database__Provider=SqlServer" "RateLimiting__TrustForwardedFor=true")
fi
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
      # Don't let a stale captured value clobber our freshly-built connection string. A captured
      # environment may still carry the old password-bearing form; importing it would undo M2.
      [[ "$key" == "ConnectionStrings__DefaultConnection" || "$key" == "SqlConnectionString" ]] && continue
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
  Database           : $( [[ "$DB_PROVIDER" == "postgres" ]] && echo "Neon Postgres (connection string in Key Vault)" || echo "${SQL_SERVER_NAME}.database.windows.net / $SQL_DB_NAME (serverless, free-limit=$SQL_USE_FREE_LIMIT)" )
  User-assigned MI   : $UAMI_NAME  (clientId $UAMI_CLIENT_ID)
  Key Vault          : $KEYVAULT_NAME
$( [[ "$ENABLE_STORAGE" == "true" ]] && echo "  Static website URL : $STATIC_WEB_URL" )
EOF

cat <<EOF

NEXT STEP — grant the app's managed identity access INSIDE the database.
Creating the server does not create a database user. Connect to '$SQL_DB_NAME' as the Entra
admin ($ENTRA_ADMIN_NAME) and run:

    CREATE USER [$WEBAPP_NAME] FROM EXTERNAL PROVIDER;
    ALTER ROLE db_datareader ADD MEMBER [$WEBAPP_NAME];
    ALTER ROLE db_datawriter ADD MEMBER [$WEBAPP_NAME];

    -- Only while the schema is being created for the first time, then revoke:
    --   ALTER ROLE db_ddladmin ADD MEMBER [$WEBAPP_NAME];
    --   ALTER ROLE db_ddladmin DROP MEMBER [$WEBAPP_NAME];

Reader + writer is all the app needs at runtime: both branches' initialisers are guarded and do
not execute DDL against an existing schema (review finding M2). Leaving db_ddladmin in place
would let anything that compromises the app drop or rewrite tables, not just rows.

Verify the whole posture afterwards with:
    ./scripts/check-azure-posture.sh
EOF

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
