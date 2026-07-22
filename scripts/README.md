# scripts

Operational and developer helper scripts for TaskBoard.

## `todoapp-smoketest.ps1` — API smoke test

An end-to-end smoke test that hits **every** API endpoint over HTTP against a *running* instance and
prints a pass/fail report. Use it as a quick manual health check after a change, or as a post-deploy
smoke test in CI. It covers auth, the JWT revocation check, category and todo CRUD, optimistic-concurrency
conflicts, the delete-category cascade, and the session-lifecycle flows (refresh rotation + reuse
detection, logout, revoke-all).

### Run it

Start the API, then run the script in a second terminal (both from the repo root):

```powershell
# terminal 1 — leave running
dotnet run --project src\TodoApp.WebApi

# terminal 2
powershell -ExecutionPolicy Bypass -File .\scripts\todoapp-smoketest.ps1
```

- `-BaseUrl <url>` targets a different host/port (default `http://localhost:5080`), e.g. a deployed URL.
- It registers throwaway users each run, so it needs no database setup and never touches the seeded demo user.
- Google sign-in is the one endpoint skipped by default — see [Google sign-in in the smoke test](#google-sign-in-in-the-smoke-test) below for why, and how to make it testable.

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

### Google sign-in in the smoke test

`POST /api/auth/google` is the **one endpoint the smoke test skips by default**, and it's worth
understanding why — it's not an oversight, it's a property of how Google sign-in is verified.

**Why the happy path can't be smoke-tested with the real validator.** The production validator
(`GoogleTokenValidator`) calls `GoogleJsonWebSignature.ValidateAsync`, which checks a real Google
**cryptographic signature, expiry, issuer, and audience** against your configured client ID. A
script can't forge a token that passes those checks — only Google can sign one. So the "valid
identity → 200" case genuinely requires a real ID token minted by a real Google sign-in, which is
why it's left as a **manual check from the SPA**.

**Why even the negative case is awkward on a bare dev run.** You might expect "garbage token → 401"
to be testable without Google, but on a plain local run it isn't reliable: if no
`Authentication:Google:ClientId` is configured, the real validator throws
*"Google sign-in is not configured"* **before** it ever inspects the token — so you'd get a `500`,
not a clean `401`. It only returns `401` when the API is running with a real client ID set.

**How to make both cases deterministically testable — the fake-validator pattern.** The clean fix
is to swap the real validator for a **Development-only fake** behind the same
`IGoogleTokenValidator` interface. The fake treats a token shaped like `fake:{email}` as a verified
Google identity and rejects everything else with `401` — mirroring the real validator's contract
with **no real Google dependency and no client ID required**. Enable it before starting the API:

```powershell
$env:ASPNETCORE_ENVIRONMENT        = "Development"
$env:Authentication__Google__UseFake = "true"
dotnet run --project src\TodoApp.WebApi
```

With that in place, the smoke test can assert **both** Google paths deterministically:

| The check… | sends | expects | because |
| ---------- | ----- | ------- | ------- |
| a fake **valid** Google identity | `idToken = "fake:{email}"` | **200** + a token, new account created | a verified Google user must sign in and be provisioned |
| an **invalid** token | `idToken = "not-a-real-token"` | **401** | an unverifiable token must be rejected |

The negative case is the one that catches a real regression — e.g. a misconfigured or disabled token
validator silently accepting junk (an auth-bypass), exactly what a security review cares about.

> The fake validator (`FakeGoogleTokenValidator`) and its DI wiring, together with the two Google
> assertions above, live on the **`refactor/dapper`** branch. The version on `main` prints a
> `[SKIP]` for Google sign-in; the env vars above only take effect where the fake validator exists.
> Because the script and the backend fake are a matched pair, porting the Google coverage means
> bringing over all three: `FakeGoogleTokenValidator`, the `Program.cs`/DI toggle, and the smoke-test
> additions — the script alone won't work without the backend piece.

See the [testing guide](../docs/development/testing.md) for how this fits alongside the automated suites.
