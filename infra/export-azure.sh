#!/usr/bin/env bash
#
# export-azure.sh — Capture the CURRENT Azure setup as it exists, into:
#     1. ARM template(s)  (JSON, authoritative)
#     2. Bicep file(s)    (decompiled from ARM — cleaner, re-deployable)
#     3. An inventory      (Markdown table + CSV) of every resource
#     4. Web-app settings (env vars) + connection strings, per web/API app
#     5. Key Vault secret NAMES (values only with --include-secret-values)
#
# It reads from Azure; it does NOT change anything.
#
# Prerequisites:
#   - Azure CLI logged in:  az login
#   - Subscription selected: az account set --subscription <id-or-name>
#   - Bicep CLI (auto-installed on demand by `az bicep`)
#   - To list Key Vault secret names: data-plane access (Key Vault Secrets User
#     or a "list" access policy) on the vault.
#
# Usage:
#   ./export-azure.sh -g rg-contoso-dev              # one resource group
#   ./export-azure.sh --all                          # every RG in the subscription
#   ./export-azure.sh -g rg-contoso-dev -o ./export  # custom output dir
#   ./export-azure.sh -g rg-contoso-dev --no-bicep   # skip Bicep decompile
#   ./export-azure.sh -g rg-contoso-dev --no-app-config       # skip settings/secrets
#   ./export-azure.sh -g rg-contoso-dev --include-secret-values  # ALSO dump secret VALUES (sensitive!)
# -----------------------------------------------------------------------------
set -euo pipefail

RESOURCE_GROUP=""
ALL_GROUPS=0
OUTPUT_DIR="./azure-export"
DO_BICEP=1
DO_INVENTORY=1
DO_APPCONFIG=1
INCLUDE_SECRET_VALUES=0

usage() { grep '^#' "$0" | sed 's/^#//'; exit "${1:-0}"; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    -g|--resource-group)     RESOURCE_GROUP="$2"; shift 2;;
    --all)                   ALL_GROUPS=1; shift;;
    -o|--output)             OUTPUT_DIR="$2"; shift 2;;
    --no-bicep)              DO_BICEP=0; shift;;
    --no-inventory)          DO_INVENTORY=0; shift;;
    --no-app-config)         DO_APPCONFIG=0; shift;;
    --include-secret-values) INCLUDE_SECRET_VALUES=1; shift;;
    -h|--help)               usage 0;;
    *) echo "Unknown arg: $1" >&2; usage 1;;
  esac
done

log() { printf '\n\033[1;34m==>\033[0m %s\n' "$*"; }
ok()  { printf '\033[1;32m  ✓\033[0m %s\n' "$*"; }
warn(){ printf '\033[1;33m  !\033[0m %s\n' "$*"; }

command -v az >/dev/null 2>&1 || { echo "ERROR: Azure CLI (az) not found." >&2; exit 1; }
az account show >/dev/null 2>&1 || { echo "ERROR: Not logged in. Run 'az login'." >&2; exit 1; }

if [[ "$ALL_GROUPS" == "0" && -z "$RESOURCE_GROUP" ]]; then
  echo "ERROR: specify a resource group with -g <name>, or --all for the whole subscription." >&2
  usage 1
fi

SUB_ID="$(az account show --query id -o tsv)"
SUB_NAME="$(az account show --query name -o tsv)"
mkdir -p "$OUTPUT_DIR"
log "Subscription: $SUB_NAME ($SUB_ID)"
log "Output directory: $OUTPUT_DIR"

# Determine which resource groups to process
if [[ "$ALL_GROUPS" == "1" ]]; then
  mapfile -t GROUPS < <(az group list --query '[].name' -o tsv)
  log "Exporting ALL resource groups (${#GROUPS[@]} found)"
else
  GROUPS=("$RESOURCE_GROUP")
fi

# ---- Per-group ARM + Bicep export -------------------------------------------
for rg in "${GROUPS[@]}"; do
  log "Resource group: $rg"
  if ! az group show --name "$rg" >/dev/null 2>&1; then
    warn "Resource group '$rg' not found — skipping."
    continue
  fi

  arm_file="$OUTPUT_DIR/${rg}.arm.json"
  # --skip-all-params keeps the template self-contained (no required params).
  if az group export --name "$rg" --skip-all-params -o json > "$arm_file" 2>/dev/null; then
    ok "ARM template  -> $arm_file"
  else
    # Some resource types don't support export; retry without the flag and warn.
    if az group export --name "$rg" -o json > "$arm_file" 2>/dev/null; then
      warn "ARM exported with warnings (some resources may be incomplete) -> $arm_file"
    else
      warn "Could not export ARM for '$rg' (unsupported resource types?). Skipping."
      continue
    fi
  fi

  if [[ "$DO_BICEP" == "1" ]]; then
    bicep_file="$OUTPUT_DIR/${rg}.bicep"
    if az bicep decompile --file "$arm_file" --outfile "$bicep_file" 2>/dev/null; then
      ok "Bicep         -> $bicep_file"
    else
      warn "Bicep decompile failed for '$rg' (ARM kept). Ensure Bicep CLI is available: az bicep install"
    fi
  fi

  # ---- App settings (env vars) + Key Vault secret names ---------------------
  if [[ "$DO_APPCONFIG" == "1" ]]; then
    # Web apps (API/web): application settings + connection strings.
    while IFS= read -r app; do
      [[ -z "$app" ]] && continue
      if ! az webapp config appsettings list -g "$rg" -n "$app" -o json \
             > "$OUTPUT_DIR/${app}.appsettings.json" 2>/dev/null; then
        warn "Could not read app settings for '$app' (permissions?)."
        rm -f "$OUTPUT_DIR/${app}.appsettings.json"
        continue
      fi
      az webapp config connection-string list -g "$rg" -n "$app" -o json \
        > "$OUTPUT_DIR/${app}.connectionstrings.json" 2>/dev/null || true

      # Flatten to KEY=value. Key Vault references become @kv:SecretName so no
      # secret values leak into this file.
      env_file="$OUTPUT_DIR/${app}.settings.env"
      {
        echo "# App settings (env vars) exported from web app '$app' — resource group '$rg'"
        echo "# Key Vault-backed values are shown as @kv:<SecretName> (values stay in the vault)."
      } > "$env_file"
      python3 - "$OUTPUT_DIR/${app}.appsettings.json" >> "$env_file" <<'PY'
import json, sys, re
try:
    data = json.load(open(sys.argv[1]))
except Exception:
    sys.exit(0)
for s in data:
    name = s.get("name", "")
    val  = s.get("value") or ""
    m = re.search(r'SecretName=([^;)\s]+)', val)
    if "Microsoft.KeyVault" in val and m:
        print(f"{name}=@kv:{m.group(1)}")
    else:
        print(f"{name}={val}")
PY
      ok "App settings  -> ${app}.settings.env  (+ ${app}.appsettings.json)"
    done < <(az webapp list -g "$rg" --query '[].name' -o tsv 2>/dev/null)

    # Key Vaults: secret NAMES by default; VALUES only with --include-secret-values.
    while IFS= read -r kv; do
      [[ -z "$kv" ]] && continue
      if ! names="$(az keyvault secret list --vault-name "$kv" --query '[].name' -o tsv 2>/dev/null)"; then
        warn "No data-plane access to list secrets in '$kv' (need 'Key Vault Secrets User' or a list policy)."
        continue
      fi
      count="$(printf '%s\n' "$names" | grep -c . || true)"
      if [[ "$INCLUDE_SECRET_VALUES" == "1" ]]; then
        warn "Exporting SECRET VALUES from '$kv' to disk — handle the output file carefully."
        out="$OUTPUT_DIR/keyvault-${kv}.secrets.env"
        echo "# Key Vault '$kv' secrets WITH VALUES — SENSITIVE. Do not commit or share." > "$out"
        while IFS= read -r nm; do
          [[ -z "$nm" ]] && continue
          v="$(az keyvault secret show --vault-name "$kv" --name "$nm" --query value -o tsv 2>/dev/null || true)"
          echo "${nm}=${v}" >> "$out"
        done <<< "$names"
        ok "Key Vault     -> keyvault-${kv}.secrets.env  ($count secrets WITH VALUES)"
      else
        out="$OUTPUT_DIR/keyvault-${kv}.secrets.txt"
        echo "# Key Vault '$kv' — secret NAMES only (values NOT exported; use --include-secret-values)." > "$out"
        printf '%s\n' "$names" >> "$out"
        ok "Key Vault     -> keyvault-${kv}.secrets.txt  ($count secrets)"
      fi
    done < <(az keyvault list -g "$rg" --query '[].name' -o tsv 2>/dev/null)
  fi
done

# ---- Subscription/RG inventory (Markdown + CSV) -----------------------------
if [[ "$DO_INVENTORY" == "1" ]]; then
  log "Building resource inventory"
  inv_csv="$OUTPUT_DIR/inventory.csv"
  inv_md="$OUTPUT_DIR/inventory.md"

  # JMESPath projection of the useful columns
  QUERY="[].{name:name, type:type, group:resourceGroup, location:location, sku:sku.name, kind:kind}"

  if [[ "$ALL_GROUPS" == "1" ]]; then
    az resource list --query "$QUERY" -o json > "$OUTPUT_DIR/.resources.json"
    scope_label="subscription '$SUB_NAME'"
  else
    az resource list --resource-group "$RESOURCE_GROUP" --query "$QUERY" -o json > "$OUTPUT_DIR/.resources.json"
    scope_label="resource group '$RESOURCE_GROUP'"
  fi

  python3 - "$OUTPUT_DIR/.resources.json" "$inv_csv" "$inv_md" "$scope_label" <<'PY'
import json, sys, csv, datetime
src, csv_path, md_path, scope = sys.argv[1:5]
data = json.load(open(src))
data.sort(key=lambda r: (r.get("group") or "", r.get("type") or "", r.get("name") or ""))

cols = ["name", "type", "group", "location", "sku", "kind"]
with open(csv_path, "w", newline="") as f:
    w = csv.DictWriter(f, fieldnames=cols)
    w.writeheader()
    for r in data:
        w.writerow({c: (r.get(c) or "") for c in cols})

with open(md_path, "w") as f:
    f.write(f"# Azure resource inventory — {scope}\n\n")
    f.write(f"Total resources: **{len(data)}**\n\n")
    f.write("| Name | Type | Resource group | Location | SKU | Kind |\n")
    f.write("|---|---|---|---|---|---|\n")
    for r in data:
        f.write("| {name} | {type} | {group} | {location} | {sku} | {kind} |\n".format(
            **{c: (r.get(c) or "") for c in cols}))
print(f"  {len(data)} resources written")
PY
  rm -f "$OUTPUT_DIR/.resources.json"
  ok "Inventory     -> $inv_md  (+ inventory.csv)"
fi

log "Export complete 🎉"
echo
echo "Contents of $OUTPUT_DIR:"
ls -1 "$OUTPUT_DIR"
cat <<EOF

Next steps:
  • Review the .bicep file(s) — they are the cleanest re-deployable form.
  • Re-deploy a captured group with:
      az deployment group create -g <target-rg> --template-file $OUTPUT_DIR/<rg>.bicep
  • The ARM JSON is the authoritative source if any Bicep decompile looked off.
EOF
