# Cold starts on the free tier — why the first request is slow, and how the app handles it

Both halves of the deployment run on **free** tiers, and both **sleep** when idle — so
the *first* request after a quiet spell pays a wake-up cost. That's expected on the free
plan, not a bug. This explains exactly why it happens, what you'll see, and the three
things the app does so it degrades gracefully instead of looking broken.

> **Since 2026-09: the database is Postgres on Neon**, and its share of the cold start is
> now a second or two instead of 30–60s. The reason for the switch is at the
> [bottom of this page](#why-the-database-moved-to-neon).

> **← Back to the main [README](../../README.md).**

## Why it happens — two things sleep

1. **App Service (Linux F1 Free)** unloads the API after ~20 minutes of inactivity. The
   next request has to reload the .NET app — an app **cold start**.
2. **Neon Postgres** suspends its compute after **~5 minutes** idle (scale-to-zero). The
   first query after that has to **resume** it — measured at **~1–2s**, absorbed by the
   retry below and usually invisible. (The previous database, Azure SQL serverless, paused
   after 60 min and took **30–60s** to resume — see
   [why the database moved](#why-the-database-moved-to-neon).)

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

The API is hardened against the database cold start: EF Core **`EnableRetryOnFailure`**
(Npgsql classifies a resuming compute's connection failures as transient on its own), longer
timeouts in the connection string, and **non-blocking startup/seeding** — so a waking
database surfaces as a slightly delayed success rather than a 500. Schema creation at boot
is **opt-in** (`Database:InitializeOnStartup`, default off in deployed environments) so a
redeploy never wakes the database just to check a schema that already exists. See
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

### 3. Keep-warm ping — external monitor (primary) + `.github/workflows/keep-warm.yml` (backup)

Something pings the API **root** (`/`, a static `{"status":"ok"}` payload) every few minutes so
the F1 App Service instance stays loaded and visitors rarely hit an app cold start.

**App-only, by design.** The ping loads the app but runs **no database query**, so it does
**not** keep the database awake and does **not** burn Neon's free compute allowance. The
residual DB resume (~1–2s) is covered by #1 and #2 above.

#### Why the GitHub Action is not the primary

GitHub states scheduled workflows are **best-effort**: under scheduler load runs are delayed
or dropped, high-frequency crons first, and skipped runs are **not logged anywhere** — they
simply never happen.

> **Observed 2026-09-01.** `keep-warm.yml` last ran 06:36 UTC; by 11:45 UTC it had not run
> again — a **five-hour gap** where a `*/10` cron should have fired ~31 times. GitHub Actions
> reported no incident. The F1 instance had unloaded hours earlier, and a probe of the API
> paid a **~90-second** cold start. The workflow was `active` and correctly configured; there
> was nothing to fix.

That gap matters more than the length suggests: the client's `wakeFetch` retry budget is
**~50s**, so a **DB** resume (30–60s) mostly fits inside it and surfaces as "Waking the server
up…", while an **app** cold start (~90s) **exceeds** it and surfaces as a hard error. Keeping
the app loaded is what keeps every remaining failure inside the window the client can absorb
politely.

#### Primary: an external uptime monitor

Use a service whose whole job is firing HTTP requests on time — [UptimeRobot](https://uptimerobot.com)
(free: 50 monitors, 5-minute interval) or [cron-job.org](https://cron-job.org) (free, 1-minute
minimum). Both also alert on downtime, which the GitHub Action never did.

Setup (one monitor, ~5 minutes):

1. Create a free account and add an **HTTP(s)** monitor.
2. **URL** — the API base URL, the one that shows Swagger (same value as the repo Actions
   variable `VITE_API_URL`), with a trailing `/`.
3. **Interval** — 5 minutes. Anything under F1's ~20-minute unload window works; 5 minutes
   leaves room for a missed check.
4. Expect **HTTP 200** with body `{"status":"ok"}`. Leave alerting on to hear about real outages.

#### Backup: keep the GitHub Action

`keep-warm.yml` stays enabled at `*/10` as the fallback for a lapsed monitor account or a
monitor outage. Overlapping pings are harmless, and it keeps a manual **"wake it now"** button
for the minutes before you expect traffic:

```bash
gh workflow run keep-warm.yml
```

It uses the repo Actions **Variable `VITE_API_URL`** and skips cleanly if that's unset.

#### Free-tier accounting (both pingers running)

| Limit | Usage | Verdict |
|---|---|---|
| GitHub Actions minutes | Unlimited on **public** repos (this repo is public) | Free |
| UptimeRobot free plan | 1 of 50 monitors, 5-min interval allowed | Free |
| F1 CPU (~60 CPU-min/day) | ~430 static responses/day — a few CPU-seconds | Far under |
| F1 bandwidth (165 MB/day) | ~430 × ~1 KB ≈ 0.5 MB/day | Far under |
| Neon free compute (100 CU-hrs/mo) | Ping never touches the DB | Untouched |

On a **private** repo the Action's ~144 runs/day would round up to ~4,320 minutes/month against
the 2,000 free — there, drop the backup and keep only the external monitor.

## Tuning

- **Change the frequency** — edit the monitor's interval (or the `cron` in `keep-warm.yml`;
  GitHub's minimum is 5 min). An interval **≥ ~15 min lets the app sleep** between pings, so
  cold starts return.
- **Turn the backup off** — delete `keep-warm.yml` (or disable it in the Actions tab) once the
  external monitor is running. The client-side retry (#2) still handles what gets through.
- **Eliminate cold starts entirely (costs money)** — move App Service to **B1** and enable
  **Always On** (removes the *app* cold start), and move Neon to a paid plan with scale-to-zero
  disabled (removes the *DB* resume). Both incur ongoing cost, and with the DB resume already
  at ~1–2s there is little left to buy.

## Why the database moved to Neon

The original production database was **Azure SQL serverless** on its free offer
(100,000 vCore-seconds/month). The problem is billing **granularity**, not the size of the
allowance:

- Azure SQL bills a minimum while awake and its auto-pause delay floors at **60 minutes** —
  so *every* wake costs a full hour: **~1,800 vCore-seconds**, no matter how brief the visit.
- 100,000 ÷ 1,800 ≈ **55 wakes a month** — fewer than two demo visits a day. August 2026
  closed at **98,554 of 100,000** consumed.
- Worse, most of those wakes were not visitors: schema initialization ran on every boot, so
  **every deploy and instance recycle spent an hour of database time** (fixed at the same
  time by making startup schema creation opt-in).
- On exhaustion the database **pauses until the next month** — the demo's sign-in would
  simply go dark for days.

**Neon Postgres** (free plan) bills the minutes the compute actually runs, suspends after
5 minutes idle, resumes in ~1–2s (measured), and on exhaustion suspends rather than bills.
The same monthly allowance that bought ~55 Azure SQL wakes buys thousands of Neon visits,
and the resume dropped from 30–60s to under the threshold anyone notices. The provider
switch was config-only (`Database:Provider=Postgres`); Azure SQL remains supported and was
kept untouched as the rollback during cutover.
