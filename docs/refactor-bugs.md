# Bugs encountered during the Dapper refactor (and how they were fixed)

A running log of the real problems hit while replacing EF Core with Dapper — found by the automated
test suite and by running the smoke test — with the root cause and fix for each. All are fixed on this
branch; the suite (36 unit + 17 integration) and the 37-check smoke test are green.

---

## 1. DateTimeOffset/Guid handlers ignored on writes

**Symptom.** After wiring up the repositories, 15 unit tests failed. Reads blew up with:
`System.FormatException: The input string '2026-01-01 00:00:00+00:00' was not in a correct format`
inside `DateTimeOffsetTicksHandler.Parse` — the column held an **ISO date string**, not the tick `long`
the handler expected.

**Cause.** `DateTimeOffset` (and `Guid`) are in Dapper's **built-in type map**. `SqlMapper.LookupDbType`
finds them there and returns *before* it ever consults custom type handlers **on the parameter (write)
path**. So my custom handlers ran on reads but not writes: writes stored the provider default (an ISO
string via Microsoft.Data.Sqlite), and the read handler then choked trying to parse that string as ticks.

**Fix.** Remove the built-in mappings so the custom handlers own both directions, in `DapperConfig`:

```csharp
SqlMapper.RemoveTypeMap(typeof(DateTimeOffset));
SqlMapper.RemoveTypeMap(typeof(DateTimeOffset?));
SqlMapper.RemoveTypeMap(typeof(Guid));
SqlMapper.RemoveTypeMap(typeof(Guid?));
SqlMapper.AddTypeHandler(new DateTimeOffsetTicksHandler());
SqlMapper.AddTypeHandler(new GuidTypeHandler());
```

**Lesson.** To override how Dapper handles a *built-in* type, `RemoveTypeMap` first — `AddTypeHandler`
alone only affects reads for those types.

---

## 2. Scoped connection context threw on startup disposal

**Symptom.** All 17 integration tests failed before any request, with:
`InvalidOperationException: 'DbConnectionContext' type only implements IAsyncDisposable. Use DisposeAsync
to dispose the container.` — thrown from the app's startup seed scope.

**Cause.** `DbConnectionContext` implemented only `IAsyncDisposable`. The startup seed scope in
`Program.cs` is disposed **synchronously** (`using (var scope = …)`), and the DI container refuses to
sync-dispose a service that is async-disposable only.

**Fix.** Implement `IDisposable` on the context too (dispose the transaction + connection synchronously)
and add it to the interface, so both sync and async scope disposal work.

---

## 3. In-memory SQLite died under per-request connections

**Symptom / pitfall.** The EF integration harness kept a single in-memory SQLite connection alive for the
whole test. With Dapper, each request opens its **own** connection from a scope and disposes it at the end
— and an in-memory SQLite database vanishes the moment its last connection closes. Naively reusing the
in-memory pattern would destroy the schema after the first request.

**Fix.** Point the integration `WebApplicationFactory` at a **private, file-backed SQLite database** (a
unique temp file per factory instance) via config override, instead of a shared in-memory connection.
The file survives connections opening and closing across requests, the app's own startup builds the
schema, and the temp file is deleted on dispose. (The unit harness still uses one shared in-memory
connection, but there a single `IDbConnectionContext` is reused, so the connection stays open.)

**Steps taken** in `tests/…IntegrationTests/CustomWebApplicationFactory.cs`:

1. **Removed the EF-era service swap.** Deleted the old code that pulled
   `DbContextOptions<ApplicationDbContext>` / `ApplicationDbContext` out of the service collection and
   re-registered a DbContext bound to a shared in-memory `SqliteConnection` — that whole approach goes
   away with EF Core.
2. **Gave each factory instance its own database file**, so parallel test classes stay isolated:

   ```csharp
   private readonly string _databasePath =
       Path.Combine(Path.GetTempPath(), $"todoapp-it-{Guid.NewGuid():N}.db");
   ```

3. **Pointed the real wiring at it via config override** — no service swapping, so the production
   `DbConnectionFactory` is exercised as-is:

   ```csharp
   protected override void ConfigureWebHost(IWebHostBuilder builder) =>
       builder.ConfigureAppConfiguration((_, config) =>
           config.AddInMemoryCollection(new Dictionary<string, string?>
           {
               ["Database:Provider"] = "Sqlite",
               ["ConnectionStrings:DefaultConnection"] = $"Data Source={_databasePath}",
           }));
   ```

   The app's own startup path (`DbInitializer` → `SchemaInitializer`) then builds the schema and seeds
   the demo data on the file DB — real code, not a test double.
4. **Cleaned up on dispose** — release pooled handles first, then delete the file (best-effort):

   ```csharp
   protected override void Dispose(bool disposing)
   {
       base.Dispose(disposing);
       if (disposing)
       {
           SqliteConnection.ClearAllPools();
           try { File.Delete(_databasePath); } catch { /* temp file — harmless if it lingers */ }
       }
   }
   ```

5. **Re-ran the suite** — all 17 integration tests passed against the file-backed database.

---

## 4. SQLite foreign keys were off — `ON DELETE SET NULL` didn't fire

**Symptom / pitfall.** Deleting a category is supposed to leave its todos uncategorized via
`FK … ON DELETE SET NULL`. Microsoft.Data.Sqlite has **foreign-key enforcement off by default**, so the
cascade would silently not happen (EF Core had turned it on).

**Fix.** The connection factory enables it for every SQLite connection:

```csharp
_connectionString = new SqliteConnectionStringBuilder(raw) { ForeignKeys = true }.ConnectionString;
```

Verified by the smoke test's *"todo left uncategorized (FK ON DELETE SET NULL)"* assertion.

---

## 5. Smoke test: root redirect reported a false failure

**Symptom.** The first smoke run flagged `GET /` (which 302-redirects to `/swagger`) as a failure with
status `-1`, even though the endpoint worked.

**Cause.** In **Windows PowerShell 5.1**, `Invoke-WebRequest -MaximumRedirection 0` on a 3xx throws an
`InvalidOperationException` that doesn't expose the response's status code — so the script recorded `-1`.

**Fix.** Detect redirects with a raw `HttpWebRequest` (`AllowAutoRedirect = $false`) that reads the `302`
directly, instead of `Invoke-WebRequest -MaximumRedirection 0`. (A test-harness bug, not an API bug —
`curl -I /` confirmed the real `302`.)

**How the redirect is detected** — a small helper opens the request without following redirects and reads
the status straight off the response:

```powershell
function Get-RawStatus {
    param([string]$Url)
    $req = [System.Net.HttpWebRequest]::Create($Url)
    $req.AllowAutoRedirect = $false          # don't follow the 302 — we want to SEE it
    $req.Method = 'GET'
    try {
        $resp = $req.GetResponse()           # for a 3xx this returns normally (no throw)
        $code = [int]$resp.StatusCode
        $resp.Close()
    }
    catch [System.Net.WebException] {
        # 4xx/5xx land here — and, unlike Invoke-WebRequest in 5.1, the response IS available
        if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode } else { $code = -1 }
    }
    return $code
}

# usage: returns 302 directly
Get-RawStatus "$BaseUrl/"
```

With `AllowAutoRedirect = $false`, `HttpWebRequest.GetResponse()` treats a 3xx as a completed response
and returns it, so the `302` is read directly from `$resp.StatusCode` — no exception, no lost status. The
`catch` is only for genuine 4xx/5xx errors, where the `WebException` still exposes `.Response.StatusCode`.

---

## 6. Smoke test: Google sign-in returned 500 instead of 401

**Symptom.** `POST /api/auth/google` with a bad token returned **500**, not the expected `401`.

**Cause.** With no `Authentication:Google:ClientId` configured locally, the real `GoogleTokenValidator`
**throws** `InvalidOperationException("Google sign-in is not configured…")` before it ever inspects the
token — so an invalid token surfaced as a 500. Pre-existing behavior, unrelated to Dapper, but it made
the Google endpoint untestable locally.

**Fix.** Add a **Development-only** `FakeGoogleTokenValidator` (wired in only when
`ASPNETCORE_ENVIRONMENT=Development` **and** `Authentication:Google:UseFake=true`). It accepts
`fake:{email}` as a verified identity and rejects everything else with `401`, letting the smoke test
cover **both** the success and reject paths without a real Google account. See
[testing.md](testing.md#the-fake-google-identity).

---

## Not bugs — things that "just worked"

Worth noting, because they were the load-bearing assumptions of the refactor:

- **Direct entity materialization** — Dapper maps rows straight onto the domain entities (private
  parameterless ctor + private setters, convention column names), so no persistence POCOs were needed.
- **Enum ↔ int and bool mapping** — handled by Dapper with no custom code.
- **Optimistic concurrency** — capturing the expected token *before* mutating the entity, then
  `UPDATE … WHERE ConcurrencyToken = @expected`, reproduced EF's behavior exactly (`409` on conflict).
