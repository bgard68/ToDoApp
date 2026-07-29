#!/usr/bin/env bash
#
# check-azure-posture.sh — assert the data-tier security posture of the live environment.
#
# Every check here corresponds to a finding from the 2026-07-28 DevSecOps review (M2/M3 and the
# issues found while remediating them). Run it after any infrastructure change, and periodically —
# these are settings a well-meaning "just get it working" change can silently undo, and none of
# them are visible from the application code.
#
# Read-only: makes no changes. Exit code 0 = compliant, 1 = at least one check failed.
#
# Prerequisites:
#   az login   (an account with reader access to the resource group)
#
# Usage:
#   ./check-azure-posture.sh
#   RESOURCE_GROUP=rg-taskboard SQL_SERVER=taskboard-05-sql WEBAPP=taskboard-06-api ./check-azure-posture.sh
#
# NOTE: this is deliberately NOT a GitHub Actions workflow. The deploy identity is scoped to
# "Website Contributor" on the App Service alone and cannot read SQL configuration. Widening it
# just to run a checker would trade a real privilege increase for a convenience — the opposite of
# what these checks are protecting. Run it from a shell or Cloud Shell instead.
# -----------------------------------------------------------------------------
set -uo pipefail

RESOURCE_GROUP="${RESOURCE_GROUP:-rg-taskboard}"
SQL_SERVER="${SQL_SERVER:-taskboard-05-sql}"
WEBAPP="${WEBAPP:-taskboard-06-api}"

pass=0
fail=0

ok()   { printf '  \033[1;32m[PASS]\033[0m %s\n' "$1"; pass=$((pass+1)); }
bad()  { printf '  \033[1;31m[FAIL]\033[0m %s\n' "$1"; fail=$((fail+1)); }
note() { printf '         %s\n' "$1"; }

command -v az >/dev/null 2>&1 || { echo "ERROR: Azure CLI (az) not found." >&2; exit 1; }
az account show >/dev/null 2>&1 || { echo "ERROR: not logged in. Run 'az login'." >&2; exit 1; }

echo
echo "Azure security posture — $RESOURCE_GROUP / $SQL_SERVER / $WEBAPP"
echo "------------------------------------------------------------"

# ---- M3: the SQL firewall must not be open to all of Azure -------------------
# A rule of 0.0.0.0-0.0.0.0 is Azure's "allow all Azure services" special case. It admits
# resources from ANY Azure tenant, not just this subscription.
rules="$(az sql server firewall-rule list -g "$RESOURCE_GROUP" --server "$SQL_SERVER" \
          --query "[].{name:name,start:startIpAddress,end:endIpAddress}" -o tsv 2>/dev/null)"

if [ -z "$rules" ]; then
  bad "could not read firewall rules (permissions? wrong server name?)"
else
  if printf '%s\n' "$rules" | awk -F'\t' '$2=="0.0.0.0" && $3=="0.0.0.0"' | grep -q .; then
    bad "M3: a rule allows ALL Azure services (0.0.0.0). Any Azure tenant can reach this server."
    note "offending rule(s): $(printf '%s\n' "$rules" | awk -F'\t' '$2=="0.0.0.0"{print $1}' | tr '\n' ' ')"
  else
    ok "M3: no all-of-Azure (0.0.0.0) firewall rule"
  fi

  # Any rule spanning a huge range is equally suspect.
  if printf '%s\n' "$rules" | awk -F'\t' '$2=="0.0.0.0" && $3=="255.255.255.255"' | grep -q .; then
    bad "M3: a rule allows the entire internet (0.0.0.0-255.255.255.255)"
  else
    ok "M3: no allow-the-internet firewall rule"
  fi

  # The App Service must still be able to reach SQL. Every possible outbound IP should be allowed,
  # otherwise the app breaks the next time it moves within its scale unit.
  missing=0
  for ip in $(az webapp show -n "$WEBAPP" -g "$RESOURCE_GROUP" --query possibleOutboundIpAddresses -o tsv 2>/dev/null | tr ',' ' '); do
    printf '%s\n' "$rules" | awk -F'\t' -v want="$ip" '$2==want' | grep -q . || missing=$((missing+1))
  done
  if [ "$missing" -eq 0 ]; then
    ok "App Service: every possible outbound IP is allow-listed"
  else
    bad "App Service: $missing possible outbound IP(s) NOT allow-listed — the app will fail intermittently"
    note "re-run the outbound-IP allow-list step in provision.sh"
  fi
fi

# ---- M2: no SQL password authentication -------------------------------------
# Entra-only auth means the server-admin login and its password stop being a usable credential.
adonly="$(az sql server ad-only-auth get -g "$RESOURCE_GROUP" -n "$SQL_SERVER" \
           --query azureAdOnlyAuthentication -o tsv 2>/dev/null)"
if [ "$adonly" = "true" ]; then
  ok "M2: Entra-only authentication is enforced (SQL password logins disabled)"
else
  bad "M2: SQL password authentication is ENABLED — the server-admin password is a live credential"
  note "fix: az sql server ad-only-auth enable -g $RESOURCE_GROUP -n $SQL_SERVER"
fi

# An Entra admin must exist, or enabling Entra-only auth locks everyone out.
admin="$(az sql server ad-admin list -g "$RESOURCE_GROUP" --server "$SQL_SERVER" --query "[0].login" -o tsv 2>/dev/null)"
if [ -n "$admin" ] && [ "$admin" != "None" ]; then
  ok "M2: an Entra administrator is configured ($admin)"
else
  bad "M2: no Entra administrator — with Entra-only auth on, nobody can administer this server"
fi

# ---- M2: the app's connection string must carry no password ------------------
cs="$(az webapp config appsettings list -n "$WEBAPP" -g "$RESOURCE_GROUP" \
       --query "[?name=='ConnectionStrings__DefaultConnection'].value" -o tsv 2>/dev/null)"
if [ -z "$cs" ]; then
  bad "could not read the connection string app setting"
else
  if printf '%s' "$cs" | grep -qiE '(^|;)[[:space:]]*(password|pwd)[[:space:]]*='; then
    bad "M2: the connection string contains an embedded password"
    note "fix: use 'Authentication=Active Directory Default' with the App Service managed identity"
  else
    ok "M2: connection string carries no password"
  fi

  if printf '%s' "$cs" | grep -qi 'Authentication=Active Directory'; then
    ok "M2: connection string uses Entra (managed identity) authentication"
  else
    bad "M2: connection string does not use Entra authentication"
  fi

  if printf '%s' "$cs" | grep -qi 'Encrypt=True'; then
    ok "connection string enforces TLS (Encrypt=True)"
  else
    bad "connection string does not set Encrypt=True"
  fi
fi

# ---- App Service transport ---------------------------------------------------
https="$(az webapp show -n "$WEBAPP" -g "$RESOURCE_GROUP" --query httpsOnly -o tsv 2>/dev/null)"
[ "$https" = "true" ] && ok "App Service enforces HTTPS only" || bad "App Service httpsOnly is not enabled"

minTls="$(az sql server show -g "$RESOURCE_GROUP" -n "$SQL_SERVER" --query minimalTlsVersion -o tsv 2>/dev/null)"
[ "$minTls" = "1.2" ] && ok "SQL minimum TLS is 1.2" || bad "SQL minimum TLS is '$minTls' (want 1.2)"

# ---- Demo account: intentional, but it must be a deliberate setting ----------
# The point of H1 was that the account appeared with no configuration at all. If it is enabled,
# the password must be supplied explicitly.
seed="$(az webapp config appsettings list -n "$WEBAPP" -g "$RESOURCE_GROUP" \
         --query "[?name=='Seed__DemoUser'].value" -o tsv 2>/dev/null)"
if [ "$seed" = "true" ]; then
  seedpw="$(az webapp config appsettings list -n "$WEBAPP" -g "$RESOURCE_GROUP" \
             --query "[?name=='Seed__Password'].value" -o tsv 2>/dev/null)"
  if [ -n "$seedpw" ]; then
    ok "H1: demo seeding is ON and its password is explicitly configured (deliberate public demo)"
  else
    bad "H1: demo seeding is ON but no password is configured — the account will be unusable"
  fi
else
  ok "H1: demo seeding is off"
fi

# ---- L6: Key Vault hardening -------------------------------------------------
KEYVAULT="${KEYVAULT:-taskboard-kv}"

# One property per call. A multi-value --query returns one value PER LINE (not tab-separated),
# and on Windows each carries a trailing CR — so `cut -f1` reads the whole block and the
# comparison silently never matches. Single-value queries are clean on both platforms.
kv_prop() {
  az keyvault show -n "$KEYVAULT" -g "$RESOURCE_GROUP" --query "$1" -o tsv 2>/dev/null | tr -d '\r'
}

kv_rbac="$(kv_prop properties.enableRbacAuthorization)"
kv_soft="$(kv_prop properties.enableSoftDelete)"
kv_purge="$(kv_prop properties.enablePurgeProtection)"

if [ -z "$kv_rbac" ] && [ -z "$kv_soft" ] && [ -z "$kv_purge" ]; then
  bad "L6: could not read Key Vault '$KEYVAULT' (name? permissions?)"
else
  if [ "$kv_rbac" = "true" ]; then
    ok "L6: Key Vault uses RBAC (not legacy access policies)"
  else
    bad "L6: Key Vault still uses access policies"
  fi

  if [ "$kv_soft" = "true" ]; then
    ok "L6: Key Vault soft-delete is on"
  else
    bad "L6: Key Vault soft-delete is off"
  fi

  if [ "$kv_purge" = "true" ]; then
    ok "L6: Key Vault purge protection is on"
  else
    bad "L6: Key Vault purge protection is off - secrets can be permanently destroyed"
  fi
fi

# ---- L10: host header validation --------------------------------------------
hosts="$(az webapp config appsettings list -n "$WEBAPP" -g "$RESOURCE_GROUP" \
          --query "[?name=='AllowedHosts'].value" -o tsv 2>/dev/null)"
if [ -n "$hosts" ] && [ "$hosts" != "*" ]; then
  ok "L10: AllowedHosts is pinned to a hostname ($hosts)"
else
  bad "L10: AllowedHosts is unset or '*' - no host header validation"
fi

# ---- H2: the refresh token must stay out of the response body ----------------
inBody="$(az webapp config appsettings list -n "$WEBAPP" -g "$RESOURCE_GROUP" \
           --query "[?name=='Auth__RefreshTokenInBody'].value" -o tsv 2>/dev/null)"
lowered="$(printf '%s' "$inBody" | tr 'A-Z' 'a-z')"
if [ -z "$inBody" ] || [ "$lowered" = "false" ]; then
  ok "H2: refresh token is cookie-only (not returned in the response body)"
else
  bad "H2: Auth__RefreshTokenInBody is enabled - the SPA can put the refresh token back in storage"
fi

echo "------------------------------------------------------------"
printf '  %d passed, %d failed\n\n' "$pass" "$fail"
[ "$fail" -eq 0 ] || exit 1
