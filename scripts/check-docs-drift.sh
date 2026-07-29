#!/usr/bin/env bash
#
# check-docs-drift.sh — catch security documentation that no longer matches the repository.
#
# Documentation drift was the single most recurring defect in the 2026-07-28 remediation: four
# separate rounds of it. The cause was always the same shape — a correction appended somewhere new
# while the statement it corrected stayed where a reader hits it first.
#
# Two checks, both chosen because they fail on FACTS rather than on wording:
#
#   1. Every repo-relative path the docs cite must exist. A doc that points at
#      `src/lib/csp.test.js` after someone renames it is worse than no doc: it reads as verified.
#   2. A short list of claims that were true once and are now false. Deliberately small — a big
#      banned-phrase list turns into a game of avoiding words rather than keeping docs true.
#
# Read-only. Exit 0 = consistent, 1 = at least one stale reference.
#
# Usage:  ./scripts/check-docs-drift.sh
# -----------------------------------------------------------------------------
set -uo pipefail

pass=0
fail=0
ok()  { printf '  \033[1;32m[PASS]\033[0m %s\n' "$1"; pass=$((pass+1)); }
bad() { printf '  \033[1;31m[FAIL]\033[0m %s\n' "$1"; fail=$((fail+1)); }

# Security docs on whichever branch this runs on. Missing ones are simply skipped: the API and
# frontend branches carry different sets.
DOCS=()
for candidate in \
  docs/development/security-remediation.md \
  docs/security.md \
  SECURITY-REMEDIATION.md \
  SECURITY-HEADERS.md
do
  [ -f "$candidate" ] && DOCS+=("$candidate")
done

if [ ${#DOCS[@]} -eq 0 ]; then
  echo "  no security docs on this branch — nothing to check"
  exit 0
fi

echo
echo "Documentation drift check"
echo "------------------------------------------------------------"
echo "  docs: ${DOCS[*]}"
echo

# ---- 1. Cited files must exist ----------------------------------------------
missing=0
for doc in "${DOCS[@]}"; do
  # Backticked tokens that look like repo paths: contain a '/' and a file extension we use.
  while IFS= read -r ref; do
    [ -z "$ref" ] && continue
    # Strip a trailing ':123' line reference if present.
    path="${ref%%:*}"
    if [ ! -e "$path" ]; then
      bad "$doc cites '$path', which does not exist"
      missing=$((missing+1))
    fi
  done < <(grep -oE '`[A-Za-z0-9_./-]+\.(cs|js|jsx|json|yml|yaml|sh|ps1|sql|props|md)`' "$doc" \
            | tr -d '`' \
            | grep '/' \
            | sort -u)
done
[ "$missing" -eq 0 ] && ok "every file path cited in the docs exists"

# ---- 2. Claims that were true once and are now false -------------------------
# Keep this list SHORT and factual. Each entry is a claim the repository disproves.
check_absent() {
  local pattern="$1" why="$2" found=0
  for doc in "${DOCS[@]}"; do
    if grep -qi -- "$pattern" "$doc"; then
      bad "$doc still claims: $why"
      found=1
    fi
  done
  return $found
}

drift=0
check_absent "deploys whichever branch is selected" \
  "workflow_dispatch can ship any branch to the App Service (dapper has no deploy job)" || drift=1
check_absent "double-submit companion cookie" \
  "CSRF uses a double-submit cookie (replaced: the SPA cannot read an API-domain cookie)" || drift=1
check_absent "refresh token is persisted so a page reload" \
  "the refresh token is persisted client-side (it is an httpOnly cookie)" || drift=1
[ "$drift" -eq 0 ] && ok "no known-false claims present"

# ---- 3. Controls the docs promise must actually be wired ----------------------
promise() {
  local doc_pattern="$1" file="$2" file_pattern="$3" label="$4"
  local claimed=0
  for doc in "${DOCS[@]}"; do
    grep -qi -- "$doc_pattern" "$doc" && claimed=1
  done
  [ "$claimed" -eq 0 ] && return 0          # not claimed on this branch; nothing to verify
  if [ -f "$file" ] && grep -q -- "$file_pattern" "$file"; then
    ok "$label — documented and present in the code"
  else
    bad "$label — documented but NOT found in $file"
  fi
}

promise "LogSanitizer"    "src/TodoApp.Application/Common/Logging/LogSanitizer.cs" "Sanitize" "M1 log sanitiser"
promise "DemoSeedOptions" "src/TodoApp.Infrastructure/Persistence/DemoSeedOptions.cs" "DemoUser" "H1 opt-in demo seed"
promise "httpOnly"        "src/TodoApp.WebApi/Authentication/RefreshTokenCookie.cs" "HttpOnly = true" "H2 httpOnly refresh cookie"
promise "600,000"         "src/TodoApp.Infrastructure/Authentication/PasswordHasher.cs" "600_000" "L8 PBKDF2 work factor"

echo "------------------------------------------------------------"
printf '  %d passed, %d failed\n\n' "$pass" "$fail"
[ "$fail" -eq 0 ] || exit 1
