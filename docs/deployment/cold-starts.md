# Cold starts on the free tier — why the first request is slow, and how the app handles it

Both halves of the deployment run on Azure's **free** tiers, and both **sleep** when
idle — so the *first* request after a quiet spell can take ~30–60s while things wake up.
That's expected on the free plan, not a bug. This explains exactly why it happens, what
you'll see, and the three things the app does so it degrades gracefully instead of looking
broken.

> **← Back to the main [README](../../README.md).**

## Why it happens — two things sleep

1. **App Service (Linux F1 Free)** unloads the API after ~20 minutes of inactivity. The
   next request has to reload the .NET app — an app **cold start**.
2. **Azure SQL serverless** auto-pauses the database after its idle delay (60 min here).
   The first query after a pause has to **resume** the database (transient errors `-2` /
   `40613`, ~30–60s) before it can answer.

They're independent: a request can hit a warm app but a paused DB, a cold app with a warm
DB, or both cold. The free tiers trade this first-hit latency for $0 cost — the right deal
for a demo.

## What you'll see during a wake-up

- **A browser network error** — `fetch()` rejects with `TypeError: Failed to fetch`. This
  is **not** an HTTP status code; it means the browser couldn't reach the server *at all*
  because the instance is still starting (the container/TLS isn't ready to accept the
  connection yet).
- **HTTP 502 / 503 / 504** — Bad Gateway / Service Unavailable / Gateway Timeout. Azure's
  front end returns these while it's spinning the instance up and can't yet route your
  request to a ready app. They're **transient warm-up** responses, not real server errors.

A **500** is different — the app is up but threw an error — so the app deliberately does
**not** treat 500 as "warming up."

## What the app does about it

### 1. Server-side database resilience (already in place)

The API is hardened against the SQL cold start: EF Core **`EnableRetryOnFailure`** (retries
the transient `-2`), a longer **`Connect Timeout`**, and **non-blocking startup/seeding** —
so a waking database surfaces as a slightly delayed success rather than a 500. See
[Lessons — Database](../lessons.md#database-sqlite-vs-azure-sql) and the
[production-500 triage](../lessons.md#diagnosing-a-500--failed-request-in-production).

### 2. Client-side retry + a human message — `frontend/src/lib/apiClient.js`

Every request goes through a `wakeFetch` wrapper. When it hits a **cold-start failure** —
a **network error** (the "Failed to fetch" above) or a **502 / 503 / 504** — it retries
with exponential backoff (~50s budget) instead of failing immediately, and signals the UI
to show **"Waking the server up…"** on the loading and login screens (`App.jsx`,
`AuthForm.jsx`).

It deliberately **does not** retry other responses — **`400 / 401 / 403 / 404 / 409 / 500`**
are returned straight away — so genuine errors (bad input, wrong password, conflicts, real
bugs) still fail fast rather than making the user wait out a retry budget.

### 3. Keep-warm ping — `.github/workflows/keep-warm.yml`

A scheduled GitHub Action pings the API **root** (`/`, which 302-redirects to Swagger) about
every 10 minutes so the F1 App Service instance stays loaded and users rarely hit an app
cold start.

- **App-only, by design.** The ping loads the app but runs **no database query**, so it does
  **not** keep the serverless DB awake and does **not** burn the Azure SQL **free-limit**
  vCore-seconds. The occasional DB cold start that still happens is covered by #1 and #2.
- **Free on a public repo.** GitHub Actions minutes are **unlimited on public repositories**,
  so ~144 runs/day cost nothing. On a **private** repo they count against the 2,000 free
  minutes/month (each run rounds up to 1 min → ~4,320 min/month, over budget) — there, widen
  the interval or drop the workflow and rely on #2.
- **F1 CPU quota is fine.** 144 lightweight redirects/day is a few CPU-seconds, far under
  F1's ~60 CPU-minutes/day.
- **Config.** It uses the repo Actions **Variable `VITE_API_URL`** (the API base URL — the one
  that shows Swagger) and skips cleanly if that's unset. Scheduled workflows only start once
  the file is on the default branch, and GitHub can delay or skip runs under load — so treat
  "10 min" as *roughly*.

## Tuning

- **Change the frequency** — edit the `cron` in `keep-warm.yml` (GitHub's minimum is 5 min;
  an interval **≥ ~15 min lets the app sleep** between pings, so cold starts return).
- **Turn it off** — delete `keep-warm.yml` (or disable it in the Actions tab). The client-side
  retry (#2) still keeps cold starts from ever showing "Failed to fetch."
- **Eliminate cold starts entirely (costs money)** — move App Service to **B1** and enable
  **Always On** (removes the *app* cold start), and disable SQL serverless auto-pause (removes
  the *DB* cold start). Both incur ongoing cost and lose the free/serverless savings, so
  they're unnecessary for a demo.
