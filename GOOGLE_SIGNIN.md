# Google Sign-In Setup

_[← Back to the main README](README.md)_

This guide walks through enabling **Sign in with Google** for TaskBoard from scratch —
creating the Google Cloud project, configuring the consent screen, generating an OAuth
Client ID, and wiring that ID into the app. It's optional: if you skip it, the Google
button is simply hidden and email/password sign-in works exactly as normal.

> **No secrets involved.** This flow uses Google Identity Services with an **ID token**,
> which relies only on the **Client ID** — a public value. There is **no Google client
> secret** to manage or leak, so the whole setup is safe to do on a repo you push publicly.

---

## How it works

The React front end reads `VITE_GOOGLE_CLIENT_ID` to render the button and request a
Google **ID token**. It posts that token to `POST /api/auth/google`, and the .NET backend
verifies it (signature, issuer, audience, expiry) via `Google.Apis.Auth`, then finds or
links a local user and issues the app's own JWT + refresh tokens — so the revocation model
is unchanged. The backend reads `Authentication:Google:ClientId` to know which audience to
accept.

Both values must be the **same Client ID**. Set only one and sign-in fails.

```
Browser (React) ──ID token──▶ POST /api/auth/google ──▶ API verifies (audience == ClientId) ──▶ issues app tokens
      ▲ VITE_GOOGLE_CLIENT_ID                                 ▲ Authentication:Google:ClientId
```

---

## 1. Create a Google Cloud project

In the [Google Cloud Console](https://console.cloud.google.com/), create a new project.

| Field | What to enter |
|-------|---------------|
| **Project name** | `TaskBoard` (human-readable; can be changed later) |
| **Project ID** | Auto-generated is fine, or set your own like `taskboard-123` — **globally unique and permanent** (lowercase, digits, hyphens) |
| **Organization** | **No organization** (personal Gmail accounts have none) |
| **Location / parent** | **No organization** |

## 2. Configure the OAuth consent screen

Open **APIs & Services → OAuth consent screen** and configure it **before** creating credentials.

- **Audience / User type:** **External** (Internal is only available with a Google Workspace org).
- **App name:** `TaskBoard` — this is shown to users on the Google sign-in dialog. It may
  not contain "Google" or imply Google affiliation.
- **User support email:** your own Google account.
- **Developer contact information:** the same email.
- Everything else (logo, homepage, privacy policy) is **optional** — leave blank for a portfolio project.

Leave the app in **Testing** publishing status and add your own Google account(s) under
**Test users**. In Testing mode only listed test users can sign in, which is exactly what
you want — no Google verification process required. You only need to "Publish" if you want
sign-in open to the general public.

## 3. Create the OAuth Client ID

Open **APIs & Services → Credentials → Create Credentials → OAuth client ID**.

- **Application type:** **Web application** (this is the type that exposes the JavaScript
  origins box).
- **Name:** `TaskBoard Web Client` — internal label only, never shown to users. Use
  `TaskBoard Web (localhost)` if you'll add a separate production client later.
- **Authorized JavaScript origins:** add `http://localhost:5173` (Vite's dev server). Add
  your production origin here too when you deploy.

Click **Create** and copy the **Client ID** — it ends in `.apps.googleusercontent.com`.
(You can ignore the client *secret* Google shows alongside it; this flow doesn't use one.)

## 4. Set the Client ID in the front end

Vite only exposes variables prefixed with `VITE_`, and reads them from an env file in the
**`frontend/`** folder (not the repo root). Create `frontend/.env.local`:

```
VITE_GOOGLE_CLIENT_ID=YOUR_ID.apps.googleusercontent.com
```

Use `.env.local` (not `.env`) so it stays out of git — the repo's `.gitignore` already
excludes it. Vite loads env files only at startup, so **restart `npm run dev`** after adding it.

## 5. Set the matching Client ID on the backend

From the `src/TodoApp.WebApi` folder:

```bash
dotnet user-secrets set "Authentication:Google:ClientId" "YOUR_ID.apps.googleusercontent.com"
```

The Client ID isn't secret, so `appsettings.json` under `Authentication:Google:ClientId`
would also work — but user-secrets keeps local config in one place and out of the repo. In
production, set the environment variable `Authentication__Google__ClientId` instead.

## 6. Restart and test

Restart **both** the API and the Vite dev server. The **Sign in with Google** button now
renders in place of the "Set VITE_GOOGLE_CLIENT_ID to enable Google sign-in" placeholder,
and clicking it completes the round trip.

---

## Troubleshooting

- **Audience-mismatch / token rejected by the backend** — the frontend and backend Client
  IDs aren't identical. They must be the exact same value.
- **`400`, `redirect_uri`, or origin error from Google** — the origin you're loading the
  app from isn't in **Authorized JavaScript origins**. Add the exact scheme, host, and port
  (e.g. `http://localhost:5173`, not `https`, not `127.0.0.1`).
- **Button doesn't appear at all** — `VITE_GOOGLE_CLIENT_ID` isn't set, or you didn't
  restart the dev server after adding it.
- **"Access blocked / app not verified"** — your Google account isn't in the **Test users**
  list on the consent screen.

---

_[← Back to the main README](README.md)_
