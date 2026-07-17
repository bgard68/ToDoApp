# Azure Key Vault

What this project stores in Key Vault, why the list is so short, and the two ways to wire it in.
Companion to [AZURE.md](AZURE.md), which includes a copy-paste Key Vault step as part of the full
deploy.

> **← Back to the main [README](README.md).**

---

## What goes in Key Vault (and what deliberately doesn't)

The notable thing about this project is how *little* is secret — a direct payoff of going passwordless
on the database and using a public Google client ID.

| Value | Secret? | Where it lives | In Key Vault? |
| ----- | ------- | -------------- | ------------- |
| **`Jwt:Key`** — JWT signing key | ✅ yes, the one real app secret | user-secrets (dev) / env var or Key Vault (prod) | ✅ **yes** |
| DB connection string | ❌ no — passwordless (`Authentication=Active Directory Default`), no password to protect | app setting | ❌ no |
| `Authentication:Google:ClientId` | ❌ no — a *public* OAuth client id; ID-token flow uses no client secret | config / build var | ❌ no |
| Issuer, audience, token lifetimes | ❌ no — plain config | `appsettings.json` | ❌ no |
| CORS allowed origins | ❌ no — plain config | app setting | ❌ no |
| Refresh tokens | ❌ no — already **hashed** in the database, never in config | database | ❌ no |

**Bottom line: exactly one secret today — the JWT signing key.** That's not a gap; it's the point.
Passwordless managed-identity SQL means there's no DB password anywhere, and the Google flow uses no
client secret, so the entire secret surface collapses to the token-signing key.

### The forward-looking second candidate

If you add a feature that encrypts data at rest with **ASP.NET Data Protection** — the TOTP **2FA
secret** is the obvious example — Key Vault earns a *second, different* job: encrypting the Data
Protection **key ring** at rest via `ProtectKeysWithAzureKeyVault`. Note that's a Key Vault **key**
used to *wrap* keys, not a stored secret value, and it's only needed once something actually uses
Data Protection. Until then, skip it.

---

## Why Key Vault fits this project cleanly

The hardest prerequisite is **already done**: the App Service has a **managed identity** — that's how
it reaches Azure SQL passwordless. Key Vault reuses that *same* identity, so:

- **No new credential is introduced** — it stays true to the "no passwords/secrets anywhere" design.
- **The bootstrapping paradox is solved** — you don't need a secret to authenticate to the secret
  store, because the managed identity *is* the authentication.

---

## Two ways to wire it in

Both are valid; the project's [AZURE.md](AZURE.md) uses **Option A**. Option B is the more "12-factor"
code-based approach and is what you'd reach for if you want the app to pull *many* secrets or run
identically across clouds.

### Option A — App Service Key Vault reference (no code change)

App Service resolves a `@Microsoft.KeyVault(...)` token in an app setting at runtime. The app reads
`Jwt:Key` exactly as before and never knows Key Vault is involved.

```bash
KV=todoapp-kv-$RANDOM
az keyvault create -g $RG -n $KV -l $LOCATION
az keyvault secret set --vault-name $KV -n JwtKey --value "$(openssl rand -base64 48)"

# Reuse the API's existing managed identity; grant read access to secrets
PRINCIPAL_ID=$(az webapp identity show -g $RG -n $API_APP --query principalId -o tsv)
az keyvault set-policy -n $KV --object-id $PRINCIPAL_ID --secret-permissions get

# Point the app setting at the secret
az webapp config appsettings set -g $RG -n $API_APP --settings \
  Jwt__Key="@Microsoft.KeyVault(SecretUri=https://$KV.vault.azure.net/secrets/JwtKey/)"
```

**Pros:** zero code, zero new packages. **Cons:** the wiring lives in Azure config, not the app; one
app setting per secret.

### Option B — Key Vault as a configuration provider (code-based)

Register Key Vault as a configuration *source* so every secret in the vault flows into `IConfiguration`
using the `--` → `:` naming convention (`Jwt--Key` becomes `Jwt:Key`).

1. Add packages: `Azure.Identity`, `Azure.Extensions.AspNetCore.Configuration.Secrets`.
2. Store the secret with the config-mapped name:

   ```bash
   az keyvault secret set --vault-name $KV -n "Jwt--Key" --value "$(openssl rand -base64 48)"
   ```

3. One call early in `Program.cs`, gated on the vault being **configured** — not on the environment:

   ```csharp
   // Opt in only when a vault URI is present. No URI (local dev, CI, tests) => this is skipped
   // entirely and config falls back to user-secrets / env vars / appsettings. No Azure dependency.
   var keyVaultUri = builder.Configuration["KeyVault:Uri"];
   if (!string.IsNullOrWhiteSpace(keyVaultUri))
   {
       builder.Configuration.AddAzureKeyVault(
           new Uri(keyVaultUri),
           new DefaultAzureCredential());
   }
   ```

Because it's a configuration source, `Jwt:Key` now resolves from the vault automatically —
**`AuthenticationSetup.cs` does not change**; it still just reads `settings.Key` and neither knows nor
cares where the value came from.

> **Why gate on `KeyVault:Uri` presence, not `!IsDevelopment()`?** Presence-based gating means the
> app runs anywhere the vault isn't set up — local dev, CI, unit/integration tests, a teammate's
> machine with no Azure login — and *automatically* uses the vault the moment `KeyVault__Uri` is set
> (which happens only in Azure). It also can't break by accidentally running Production locally.
> Deployed, set the app setting `KeyVault__Uri=https://<vault-name>.vault.azure.net/` and it activates.

**Pros:** wiring lives in code and version control; adding more secrets is free (no per-secret app
setting). **Cons:** two packages and one line of startup code.

---

## How this changes the existing App Service settings

Introducing Key Vault touches **exactly one** of the API's app settings — the rest stay as plain
settings because none of them are secrets.

| App setting | Secret? | What happens |
| ----------- | ------- | ------------ |
| `Jwt__Key` | ✅ yes | **Removed**, replaced by `KeyVault__Uri` + the `Jwt--Key` secret in the vault (Option B). The only one that moves. |
| `KeyVault__Uri` | ❌ no (just a URL) | **New** — `https://<vault>.vault.azure.net/`. Its presence activates the vault. |
| `Authentication__Google__ClientId` | ❌ no — public client id | **Stays** as-is. |
| `ConnectionStrings__DefaultConnection` | ❌ no — passwordless (`Authentication=Active Directory Default`) | **Stays** as-is; no password to protect. |
| `Database__Provider=SqlServer` | ❌ no — plain config | **Stays**. |
| `Cors__AllowedOrigins__0` | ❌ no — a URL | **Stays**. |
| `ASPNETCORE_ENVIRONMENT=Production` | ❌ no | **Stays** — and no longer relates to Key Vault activation, which gates on `KeyVault__Uri` presence, not environment name. |

**Net change to the API config: delete `Jwt__Key`, add `KeyVault__Uri`.** Everything else is
untouched — the same "only one real secret" story again: the connection string and Google client id
*look* vault-worthy but contain no secret, so moving them adds ceremony with zero security gain.

Two things about how they coexist:

- **Precedence.** `AddAzureKeyVault` is registered *after* the default providers (appsettings, env
  vars, and App Service settings all surface as environment variables), so the vault wins for any
  overlapping key. Removing the `Jwt__Key` app setting is therefore cleaner than leaving it — the
  vault would win regardless, but a leftover setting is easy to mistake for the source of truth.
- **Fail-fast still protects you.** After deleting `Jwt__Key`, if the vault doesn't actually supply it
  (wrong secret name, missing role, bad URI), `AuthenticationSetup.cs` throws at startup rather than
  running keyless — so the removal is self-verifying.

> **Option A note:** with the app-setting *reference* approach you instead **keep** `Jwt__Key` and
> change its *value* to `@Microsoft.KeyVault(SecretUri=…)`, and you do **not** add `KeyVault__Uri`.
> Same end state, different mechanism — pick one, not both.

---

## Access model: RBAC vs. access policy

`az keyvault set-policy` (used above and in AZURE.md) is the legacy **access-policy** model — simple
and fine for one app. The modern default is **RBAC**: create the vault with
`--enable-rbac-authorization` and grant the identity the built-in **`Key Vault Secrets User`** role
instead of a set-policy call. Either works; RBAC is preferred for new work because it's the same
role-based model you already use for SQL (`db_datareader` / `datawriter`).

```bash
# RBAC variant
az keyvault create -g $RG -n $KV -l $LOCATION --enable-rbac-authorization true
az role assignment create \
  --assignee $PRINCIPAL_ID \
  --role "Key Vault Secrets User" \
  --scope $(az keyvault show -n $KV --query id -o tsv)
```

---

## Working locally without Key Vault

**Key Vault is optional at runtime.** Because the app registers it only when `KeyVault:Uri` is
present (Option B) — or, with Option A, because the reference lives entirely in Azure app settings —
a machine with no vault, no `az login`, and no network to Azure runs exactly as before:

```
Jwt:Key resolution order (last non-empty wins):
  appsettings.json  →  env vars / user-secrets  →  Key Vault (only if KeyVault:Uri is set)
```

- **Local dev / CI / tests:** `KeyVault:Uri` is unset, so the Key Vault provider is never added. There
  is no call to Azure, no credential lookup, no timeout — `Jwt:Key` comes from user-secrets (or an env
  var) just like today.
- **Azure:** `KeyVault__Uri` is set as an app setting, so the provider activates and the secret flows
  from the vault.
- **Safety net either way:** `AuthenticationSetup.cs` still fails fast if `Jwt:Key` ends up empty from
  *all* sources, so a misconfigured environment is caught at startup rather than issuing unsigned
  tokens.

If you ever *want* the vault in dev, just set `KeyVault:Uri` in user-secrets;
`DefaultAzureCredential` then uses your `az login` / Visual Studio identity — no secret needed there
either. Leaving it unset is the normal local path.

---

## What implementing it actually costs

| Step | Effort |
| ---- | ------ |
| Create the vault | one `az keyvault create` |
| Grant access | one role assignment / set-policy on an identity **that already exists** |
| Store the secret | one `az keyvault secret set` |
| Wire the app | nothing (Option A) or two packages + one line (Option B) |
| App code that reads it | **unchanged** either way |

Net effect: one new Azure resource and one role assignment on the App Service's existing managed
identity remove the last real secret from environment variables — with no change to the code that
consumes it.

---

## Verifying it works

The app gives you a built-in pass/fail signal: `AuthenticationSetup.cs` **throws at startup** if
`Jwt:Key` is missing or under 32 bytes. So "did the secret resolve?" reduces to "did the app start
**and** can it mint and validate a token?" — you never need to print the secret (and shouldn't). The
correct verification is **behavioral**: app starts → login issues a token → a protected route accepts
it.

### Locally

By design the app hits Key Vault only when `KeyVault:Uri` is configured, so on a normal machine it
doesn't touch the vault at all — "locally" splits in two.

**1. Normal dev path — secret from user-secrets** (exercises the consumption code, no Azure):

```bash
dotnet user-secrets set "Jwt:Key" "$(openssl rand -base64 48)"
dotnet run --project src/TodoApp.WebApi
```

Then confirm end to end: log in with the demo account, get an access token, and call a protected
endpoint with it:

```bash
TOKEN=$(curl -s -X POST http://localhost:5000/api/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"email":"demo@todoapp.local","password":"Password123!"}' | jq -r .accessToken)
curl -s http://localhost:5000/api/categories -H "Authorization: Bearer $TOKEN"   # expect 200
```

A valid round trip proves the same key that *signed* the token also *validates* it. **Negative test:**
`dotnet user-secrets remove "Jwt:Key"` and confirm the app refuses to start — that's fail-fast working.

**2. The Key Vault path itself, from your machine** (optional, worth doing once before deploying).
No code change needed — just point the app at the vault via user-secrets and sign in:

```bash
az login
az role assignment create --assignee $(az ad signed-in-user show --query id -o tsv) \
  --role "Key Vault Secrets User" --scope $(az keyvault show -n $KV --query id -o tsv)
dotnet user-secrets set "KeyVault:Uri" "https://$KV.vault.azure.net/"
dotnet run --project src/TodoApp.WebApi
```

Setting `KeyVault:Uri` activates the provider; `DefaultAzureCredential` picks up your `az login`
identity, so the app pulls `Jwt:Key` straight from the vault on your laptop. A successful login proves
the vault wiring. Remove it (`dotnet user-secrets remove "KeyVault:Uri"`) to return to the normal
offline path. `az keyvault secret show --vault-name $KV -n "Jwt--Key" --query value -o tsv` confirms
the secret is set.

### Once deployed

1. **Clean startup is the first signal.** Restart and watch the log stream — if the identity can't
   read the secret, `AuthenticationSetup` throws and you'll see it:

   ```bash
   az webapp restart -g $RG -n $API_APP
   az webapp log tail -g $RG -n $API_APP
   ```

2. **Functional end to end.** Hit the deployed API and expect a token, then a 200 from a protected
   route:

   ```bash
   curl -s -X POST https://<api-host>/api/auth/login \
     -H 'Content-Type: application/json' \
     -d '{"email":"demo@todoapp.local","password":"Password123!"}'
   ```

3. **Option A — confirm the reference *resolved*, not left literal.** In the Portal, Configuration →
   `Jwt__Key` shows a green "resolved" indicator for Key Vault references (red = identity lacks access
   or the SecretUri is wrong). From the CLI, `az webapp config appsettings list` shows whether it's
   still the `@Microsoft.KeyVault(...)` token.

4. **Deliberate negative test.** Remove the identity's access (or point the reference at a bad
   SecretUri), restart, and confirm the app fails to start — this proves it genuinely depends on the
   vault and isn't quietly falling back to a stale env var.

5. **Rotation.** Set a new secret version (`az keyvault secret set …`) and restart (the config provider
   reads at startup). Existing access tokens then fail validation because the signing key changed —
   the expected behavior of rotating a signing key, and a clean confirmation the new value is in use.

> Note: none of these checks print the secret. The security-correct verification is behavioral —
> app starts, token issues, protected route accepts it — precisely because a real secret should never
> appear in logs or test output.

---

> **← Back to the main [README](README.md).**
