# Testing Guide — Frontend (Vitest) & API (xUnit)

_[← Back to the main README](../../README.md)_

How the automated tests are set up on both sides of the app, how to run them, and — most
importantly — **step-by-step instructions for adding a new test**. It also documents the
from-scratch standup of the frontend test stack (Vitest + React Testing Library, including how
JSX is transformed via Babel) and the **deploy gate** that stops a release when tests fail.

For how the CI pipeline catches build/publish failures and how to test the pipeline itself, see
the companion [CI/CD pipeline testing guide](../deployment/pipeline-testing.md).

---

## 1. What tests exist today

| Layer | Project / folder | Framework | What it covers |
| ----- | ---------------- | --------- | -------------- |
| Frontend | `frontend/src/**/*.test.{js,jsx}` | **Vitest** + React Testing Library | Pure helpers, a component, and the `useTodos` hook (optimistic move + error handling). |
| API — unit | `tests/TodoApp.UnitTests` | **xUnit** + FluentAssertions | Domain invariants and CQRS handlers (ownership, concurrency, auth/refresh) against real EF Core over in-memory SQLite. |
| API — integration | `tests/TodoApp.IntegrationTests` | **xUnit** + `WebApplicationFactory<Program>` | The whole API in-process, hit over HTTP end-to-end (register → authorize → call endpoints). |

The guiding rule on both sides: **tests use the real thing wherever it's cheap.** The API tests run
against a genuine EF Core context (in-memory SQLite, not the EF in-memory provider) so query
translation is actually exercised; the frontend hook tests run the real hook with only the network
layer mocked.

---

## 2. Frontend testing (Vitest + React Testing Library)

### 2.1 The stack — and where Babel fits

The frontend has **no separate Babel config file** — and that's intentional. JSX still has to be
compiled to JavaScript for a test to run, and here's the chain that makes `.jsx` test files work:

- **Vitest reuses Vite's transform pipeline.** It reads the same `vite.config.js` the app uses, so
  a test file is transformed exactly the way the app is built — no second, drifting config.
- **`@vitejs/plugin-react` does the JSX → JS transform, and it uses Babel under the hood**
  (`@babel/plugin-transform-react-jsx` plus Fast Refresh). Because that plugin is already in
  `vite.config.js` for the app, Vitest picks it up automatically. That's why there's nothing to
  configure and no `.babelrc` — Babel is present, just wrapped inside the Vite React plugin.

The rest of the stack:

- **Vitest** — the test runner (Vite-native; Jest-compatible API: `describe`/`it`/`expect`/`vi`).
- **jsdom** — a headless DOM so component tests run in Node without a browser.
- **@testing-library/react** — renders components and hooks the way a user sees them (`render`,
  `screen`, `renderHook`).
- **@testing-library/jest-dom** — extra matchers like `toBeInTheDocument()`.
- **@testing-library/user-event** — realistic user interactions (typing, clicking).

> Aside: during development we also used the standalone `@babel/parser` to **syntax-check** the
> refactored source without a full `vite build` (the sandbox couldn't run the bundler). That's a
> one-off diagnostic, not part of the test setup — the tests themselves rely only on the Vite React
> plugin above.

### 2.2 Standing it up from scratch (one time)

If you were wiring this into a bare Vite React app, these are the exact steps that produced the
current setup.

**Step 1 — install the dev dependencies:**

```bash
cd frontend
npm install -D vitest jsdom \
  @testing-library/react @testing-library/dom \
  @testing-library/jest-dom @testing-library/user-event
```

(`@testing-library/react` 16 requires `@testing-library/dom` 10 as a peer, so install both.)

**Step 2 — add the `test` block to `vite.config.js`** (co-locating test config with build config):

```js
export default defineConfig({
  plugins: [react()],           // <- the React plugin (Babel/JSX) the tests reuse
  // ...server config...
  test: {
    environment: 'jsdom',       // DOM for component tests
    globals: true,              // describe/it/expect without imports
    setupFiles: './src/test/setup.js',
    css: false,                 // don't process CSS in tests — faster, no styling asserts
  },
});
```

**Step 3 — create the setup file** `frontend/src/test/setup.js` so the jest-dom matchers load
before every test:

```js
import '@testing-library/jest-dom';
```

**Step 4 — add the scripts** to `frontend/package.json`:

```json
"scripts": {
  "test": "vitest run",
  "test:watch": "vitest"
}
```

`vitest run` runs once and exits (what CI uses); `vitest` stays in watch mode for local dev.

> **Important for CI:** after installing the dev deps, commit the updated **`package-lock.json`**.
> The pipeline runs `npm ci`, which fails if the lockfile is out of sync with `package.json`.

### 2.3 Running the tests

```bash
cd frontend
npm test            # one-shot run (CI mode)
npm run test:watch  # re-run on file changes while developing
```

### 2.4 How to ADD a frontend test

Put the test **next to the file it covers**, named `<name>.test.js` (or `.test.jsx` for anything
that renders JSX). Pick the pattern that matches what you're testing.

**Pattern A — a pure function** (e.g. something in `src/lib/`). No DOM, no mocks:

```js
// src/lib/colors.test.js
import { describe, it, expect } from 'vitest';
import { tint } from './colors.js';

describe('tint', () => {
  it('lightens a hex color toward white', () => {
    expect(tint('#000000', 0.5)).toBe('rgb(128, 128, 128)');
  });
});
```

**Pattern B — a component** (`.test.jsx`). Render it, then assert on what the user sees:

```jsx
// src/components/TodoForm.test.jsx
import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { TodoForm } from './TodoForm.jsx';

it('submits the entered title', async () => {
  const onSubmit = vi.fn();
  render(<TodoForm categories={[]} onSubmit={onSubmit} />);

  await userEvent.type(screen.getByLabelText(/title/i), 'Buy milk');
  await userEvent.click(screen.getByRole('button', { name: /add/i }));

  expect(onSubmit).toHaveBeenCalledWith(expect.objectContaining({ title: 'Buy milk' }));
});
```

**Pattern C — a hook** (`.test.jsx`). Use `renderHook`, and mock the network layer with `vi.mock`
so nothing real is called:

```jsx
// src/hooks/useTodos.test.jsx
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, act, waitFor } from '@testing-library/react';
import { useTodos } from './useTodos.js';
import { TodoApi } from '../lib/apiClient.js';

vi.mock('../lib/apiClient.js', () => ({
  TodoApi: { list: vi.fn(), changeStatus: vi.fn(), /* ...other methods... */ },
}));

beforeEach(() => vi.clearAllMocks());

it('reverts by reloading when a move fails', async () => {
  TodoApi.list.mockResolvedValueOnce([{ id: 1, status: 0 }]);   // initial load
  TodoApi.changeStatus.mockRejectedValueOnce(new Error('boom')); // move fails
  TodoApi.list.mockResolvedValueOnce([{ id: 1, status: 0 }]);   // reload after failure

  const { result } = renderHook(() => useTodos());
  await waitFor(() => expect(result.current.todos).toHaveLength(1));

  await act(async () => { await result.current.moveCard(1, 2); });

  expect(TodoApi.list).toHaveBeenCalledTimes(2); // reloaded to recover
  expect(result.current.todos[0].status).toBe(0); // rolled back
  expect(result.current.error).toBe('boom');      // error surfaced to the user
});
```

That's the whole loop: write the file, `npm test`, see it green. No config changes are needed to add
a test — Vitest auto-discovers any `*.test.*` file under `src/`.

---

## 3. API testing (xUnit)

There are two projects, and the split matters: **unit tests** exercise one handler or entity in
isolation; **integration tests** boot the real API and talk to it over HTTP.

### 3.1 Unit tests — `tests/TodoApp.UnitTests`

Stack: **xUnit** + **FluentAssertions**, with two small support types you'll reuse constantly:

- **`TestDatabase`** (`TestSupport/TestDatabase.cs`) — a real `ApplicationDbContext` backed by an
  **in-memory SQLite** connection (kept alive for the test's lifetime). Call `db.NewContext()` to
  read back with a fresh context so you're asserting on persisted state, not the EF change-tracker.
- **`Fakes.cs`** — hand-written test doubles for the ports: `FakeDateTimeProvider` (a controllable
  clock), `FakeCurrentUserService` (set `UserId`), `FakeJwtTokenService`, `FakeGoogleTokenValidator`.
  These exist precisely *because* the app depends on abstractions — no mocking framework needed.

**How to add a unit test for a handler** — construct the handler with the fakes and the test DB, call
`Handle`, assert:

```csharp
// tests/TodoApp.UnitTests/Todos/MyNewHandlerTests.cs
using FluentAssertions;
using TodoApp.Application.Todos.Commands.CreateTodo;
using TodoApp.UnitTests.TestSupport;
using Xunit;

public class MyNewHandlerTests
{
    private readonly FakeDateTimeProvider _clock = new();

    [Fact]
    public async Task Create_AssignsTodoToCurrentUser()
    {
        using var db = new TestDatabase();
        var user = new TodoApp.Domain.Entities.User("u@x.com", "hash", _clock.UtcNow);
        db.Context.Users.Add(user);
        db.Context.SaveChanges();

        var current = new FakeCurrentUserService { UserId = user.Id };
        var handler = new CreateTodoCommandHandler(db.Context, current, _clock);

        var result = await handler.Handle(new CreateTodoCommand { Title = "Mine" }, CancellationToken.None);

        result.Title.Should().Be("Mine");
        using var read = db.NewContext();                 // fresh context to verify persistence
        read.TodoItems.Single().UserId.Should().Be(user.Id);
    }
}
```

To assert a handler **throws**, use FluentAssertions' async form:

```csharp
var act = () => handler.Handle(command, CancellationToken.None);
await act.Should().ThrowAsync<NotFoundException>();
```

Domain-only tests are even simpler — new up the entity and assert its behavior (see
`Domain/TodoItemTests.cs` for the pattern).

### 3.2 Integration tests — `tests/TodoApp.IntegrationTests`

Stack: **xUnit** + **`Microsoft.AspNetCore.Mvc.Testing`**. The whole API runs in-process and you hit
it with a real `HttpClient`.

- **`CustomWebApplicationFactory`** boots the app (`WebApplicationFactory<Program>`), swaps the
  file-based DbContext for a private in-memory SQLite connection via `ConfigureTestServices`, and
  supplies a throwaway `Jwt__Key` so auth works. (This is why `Program.cs` ends with
  `public partial class Program { }` — it makes the entry point referenceable by the factory.)
- **`ApiHelpers`** provides extension methods so tests read cleanly: `client.RegisterAsync()`,
  `client.LoginAsync(...)`, `client.Authorize(accessToken)`.

**How to add an integration test** — implement `IClassFixture<CustomWebApplicationFactory>`, create a
client, authenticate, and assert on real HTTP responses:

```csharp
// tests/TodoApp.IntegrationTests/MyEndpointTests.cs
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Xunit;

public class MyEndpointTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    public MyEndpointTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Create_ThenList_ReturnsTheTodo()
    {
        var client = _factory.CreateClient();
        var auth = await client.RegisterAsync();   // helper: registers a unique user
        client.Authorize(auth.AccessToken);

        var create = await client.PostAsJsonAsync("/api/todos", new { title = "Buy milk", priority = 2 });
        create.StatusCode.Should().Be(HttpStatusCode.Created);

        var list = await client.GetFromJsonAsync<List<TodoResult>>("/api/todos");
        list.Should().ContainSingle(t => t.Title == "Buy milk");
    }
}
```

Because each factory instance owns its own in-memory database, test classes are isolated from one
another and can run in parallel.

### 3.3 Running the API tests

```bash
dotnet test                                   # everything
dotnet test tests/TodoApp.UnitTests           # unit only
dotnet test tests/TodoApp.IntegrationTests    # integration only
```

---

## 4. The deploy gate — tests must pass before shipping

Tests aren't advisory here; a red test **blocks the deploy**.

**Frontend.** `frontend-ci-cd.yml` installs deps and runs the suite **before** the build-and-deploy
step:

```yaml
      - name: Run frontend tests (gate - deploy is skipped if these fail)
        working-directory: frontend
        run: npm test
```

Because every `run:` step executes under `bash -e`, a non-zero exit from `vitest run` (any failing
test) fails the step, which fails the job, which means the `Azure/static-web-apps-deploy` step that
follows **never runs**. A broken test can't reach production.

**API.** `api-ci-cd.yml` runs `dotnet test` between build and publish; a failing test returns a
non-zero exit and stops the job before the deploy for the same reason. The pipeline's fail-fast and
publish-guard behavior is documented in detail in the
[CI/CD pipeline testing guide](../deployment/pipeline-testing.md).

The net guarantee across both apps: **if a test fails, nothing ships, and GitHub emails the person
who pushed the commit.**

---

## 5. Dependencies — `package.json` & `package-lock.json`

Two files describe the frontend's npm dependencies, and **both belong in git**. They do different jobs:

- **`package.json`** — the file *you* (and npm) edit. It declares the project's direct dependencies,
  usually as version *ranges* (e.g. `"vitest": "^2.1.8"` means "2.1.8 or any newer 2.x"), plus the
  `scripts`, name, and other metadata. It's the human-facing statement of intent: "the app needs
  roughly these packages."
- **`package-lock.json`** — a file npm *generates* and maintains automatically (you never hand-edit
  it). It records the **exact** resolved version of every package **and every nested sub-dependency**,
  each with an integrity hash. It's the machine-precise snapshot: "here is the exact tree that was
  actually installed, bit-for-bit."

The gap between them is the whole point. `package.json` says `^2.1.8`, so one `npm install` might
resolve to 2.1.8 and another next month to 2.1.15 — same manifest, different code. The lockfile pins
the answer so everyone ends up with an identical `node_modules`.

**How they're generated.** `package.json` starts from `npm init` and changes whenever you add a
dependency: `npm install <pkg>` (runtime) or `npm install -D <pkg>` (dev) writes the new entry into
`package.json` **and** rewrites `package-lock.json` to match. A plain `npm install` also refreshes the
lockfile when it's out of date. You don't edit the lockfile by hand — you change `package.json` (or run
an install) and let npm regenerate it.

**Why both are committed.**

- **Reproducible builds** — with the lockfile committed, every developer, machine, and CI runner
  installs the exact same versions top to bottom. This is what prevents "works on my machine, breaks in
  the build."
- **Security** — the integrity hashes verify each package wasn't tampered with in transit, and pinned
  versions mean a compromised *newer* release of some deep sub-dependency can't silently slip in.
- **CI depends on it** — the pipeline runs **`npm ci`** (not `npm install`), the strict, lockfile-only
  mode. `npm ci` **refuses to run** if `package-lock.json` is missing or out of sync with
  `package.json`. So after adding a dependency you must commit the updated lockfile alongside
  `package.json`, or CI fails immediately (exactly what we hit when adding the Vitest/Testing-Library
  dev deps).

**Rule of thumb:** change `package.json` (or `npm install <pkg>`), let npm rewrite `package-lock.json`,
and **commit both together in the same change**. The installed `node_modules/` folder itself is *not*
committed — it's rebuilt from these two files and stays in `.gitignore`.

---

_See also: [CI/CD pipeline testing](../deployment/pipeline-testing.md) · [Architecture & practices assessment](../architecture/assessment.md) · [Lessons learned](../lessons.md)._

> **← Back to the main [README](../../README.md).**
