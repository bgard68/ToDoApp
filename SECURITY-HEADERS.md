# Response security headers (review finding H2)

The SPA keeps its refresh token in `localStorage` (see `src/lib/apiClient.js`), which is a
deliberate, documented trade-off — but until now nothing compensated for it. Neither delivery
path set a single security header, so any XSS meant handing over a 7-day, silently-renewing
session: full account takeover that survives a password change.

Headers are now set in **both** delivery paths, and they must stay in step:

| Path | File | Used by |
|---|---|---|
| Azure Static Web Apps (production) | `staticwebapp.config.json` → `globalHeaders` | the deployed site |
| nginx container (docker compose) | `nginx.conf` → `add_header` | local full-stack runs |

## Two values that must be kept in sync

### 1. The inline-script hash

`index.html` runs a small inline script before paint so the page doesn't flash the wrong theme.
Under CSP, inline script needs an explicit allowance. Static hosting can't issue a per-response
nonce, so the script is allowed by **hash**:

```
script-src 'self' 'sha256-GW825FdRS8YFXkaacjvphmbKysoTTeAxlXyal7guZew=' https://accounts.google.com
```

**Editing that inline script — even by one character — invalidates the hash**, and the browser
silently refuses to run it. The failure is subtle: no crash, just the theme flash coming back plus
a console error.

`src/lib/csp.test.js` recomputes the hash from `index.html` on every test run and fails if it
doesn't match the policy, so the mismatch is caught in CI rather than in production. When you
change the script, the test failure message contains the replacement hash.

### 2. The API origin

`connect-src` names the API host explicitly:

```
connect-src 'self' https://taskboard-06-api.azurewebsites.net https://accounts.google.com
```

This must match the `VITE_API_URL` repository Variable that the build is given. If the API moves,
update both — otherwise every request from the SPA is blocked by the browser.

## Why each directive

| Directive | Reason |
|---|---|
| `default-src 'self'` | deny by default; everything else is an explicit exception |
| `script-src` + `https://accounts.google.com` | `GoogleButton.jsx` injects Google Identity Services from there |
| `frame-src https://accounts.google.com` | GSI renders its button and consent flow in an iframe |
| `img-src ... googleusercontent.com` | user avatars returned by Google sign-in |
| `style-src 'unsafe-inline'` | GSI injects inline styles; unavoidable while that widget is used |
| `object-src 'none'`, `base-uri 'self'` | kill plugin embedding and `<base>` hijacking |
| `frame-ancestors 'none'` | clickjacking; `X-Frame-Options: DENY` repeats it for older browsers |
| `form-action 'self'` | a script-injected form cannot POST credentials off-origin |
| `Strict-Transport-Security` | the SWA serves HTTPS; pin it |

`style-src 'unsafe-inline'` is the one genuine weakness left. It is required by Google Identity
Services and cannot be hashed, because GSI generates styles at runtime. It permits CSS injection
but not script execution, so it does not by itself re-open the token-theft path.
