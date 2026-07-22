# scripts

> **← Back to the main [README](../README.md).**

Operational and developer helper scripts for TaskBoard.

## `todoapp-smoketest.ps1` — API smoke test

An end-to-end smoke test that hits **every** API endpoint over HTTP against a *running* instance and
prints a pass/fail report. Use it as a quick manual health check after a change, or as a post-deploy
smoke test in CI. It covers auth, the JWT revocation check, category and todo CRUD, optimistic-concurrency
conflicts, the delete-category cascade, the session-lifecycle flows (refresh rotation + reuse
detection, logout, revoke-all), and **Google sign-in** — both the success and reject paths — via the
Development-only fake Google validator (no real Google token needed).

### Run it

Start the API with the fake Google validator enabled, then run the script in a second terminal
(both from the repo root):

```powershell
# terminal 1 — leave running
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:Authentication__Google__UseFake = "true"
dotnet run --project src\TodoApp.WebApi

# terminal 2
powershell -ExecutionPolicy Bypass -File .\scripts\todoapp-smoketest.ps1
```

- `-BaseUrl <url>` targets a different host/port (default `http://localhost:5080`), e.g. a deployed URL.
- It registers throwaway users each run, so it needs no database setup and never touches the seeded demo user.
- **Google sign-in** is tested through the fake validator: a token of the form `fake:{email}` is accepted
  as a verified identity (**200**, creates the user), and any other token is rejected (**401**). The fake
  validator is wired in only when `ASPNETCORE_ENVIRONMENT=Development` **and**
  `Authentication:Google:UseFake=true` — it can never run in production. Without that flag the two Google
  checks fail (the real validator rejects the fake token, or errors if no Google client id is configured).

### Reading the output — green does **not** mean "200"

Every line is `[PASS]` (green) or `[FAIL]` (red). **The goal is all green, not all 200.** Many checks
deliberately expect an **error** status and pass green when they get it, because for those requests the
correct behavior *is* an error:

| The check… | expects | because |
| ---------- | ------- | ------- |
| calls a protected endpoint with no token | **401** | anonymous access must be rejected |
| creates a duplicate category name | **409** | the unique-name rule must fire |
| sends an invalid color / bad body | **400** | validation must reject it |
| deletes, logs out, or revokes-all | **204** | success, no content |
| updates with a stale concurrency token | **409** | lost-update protection must fire |
| replays a rotated refresh token | **401** | reuse detection must reject it |

So a healthy run is a **mix** of `200`, `201`, `204`, `400`, `401`, and `409` — all shown green. You do
**not** need to inspect each line: only a red `[FAIL]` (a status other than the one the check expected)
means something is wrong, and the script prints the response body under a failing line to help diagnose
it. The final summary is `Result: N passed, M failed` — `0 failed` is a clean run.

See the [testing guide](../docs/testing.md) for how this fits alongside the automated suites, and the
[bugs encountered log](../docs/refactor-bugs.md) for the issues found (and fixed) while getting this
smoke test green on the Dapper build.
