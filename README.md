# TaskBoard — Frontend (React + Vite)

The single-page web client for **TaskBoard**, a multi-user Kanban board — sign in, add tasks,
drag them across To Do / In Progress / Done lanes, and organize them with color-coded categories.

> ### 🔀 This is the `frontend` branch
> The React/Vite client lives here on its **own standalone branch** (app at the branch root) and is
> **shared by both backend editions** of the same repo — [`main`](https://github.com/bgard68/ToDoApp/tree/main)
> (EF Core) and [`dapper`](https://github.com/bgard68/ToDoApp/tree/dapper) (Dapper). Keeping it on a
> single branch means one source of truth for the UI: frontend changes are committed once, here.

## Tech stack

- **React 18** + **Vite** (dev server, build) — single-page app, custom hooks, `fetch`-based API client
- **Vitest** + React Testing Library + jsdom — unit/component tests
- **Google Identity Services** — Google sign-in
- Deploys to **Azure Static Web Apps**

## Prerequisites

- **Node** `^20.19.0 || >=22.12.0` (CI uses Node 22)
- A running TaskBoard **backend API** (from the `main` or `dapper` branch) for the app to talk to

## Getting started

```bash
npm install

# point the app at your backend API, then start the dev server (http://localhost:5173)
# (create .env.local — see "Configuration" below)
npm run dev
```

Run a backend in a separate checkout (e.g. `dotnet run --project src/TodoApp.WebApi` from a `main` or
`dapper` working tree). A convenient way to have both at once is a git worktree from a backend clone:

```bash
git worktree add ../todoapp-frontend frontend
```

## Configuration

Vite env vars are **build-time**. For local dev, create `.env.local` at the root:

```
VITE_API_URL=http://localhost:5080        # the backend API base URL (include the scheme)
VITE_GOOGLE_CLIENT_ID=<your-google-web-client-id>   # optional; only needed for Google sign-in
```

In CI/deploy these come from **GitHub repository Variables** (`VITE_API_URL`, `VITE_GOOGLE_CLIENT_ID`),
injected by the deploy workflow.

## Scripts

| Command | What it does |
| ------- | ------------ |
| `npm run dev` | Start the Vite dev server with HMR |
| `npm run build` | Production build to `dist/` |
| `npm run preview` | Serve the built `dist/` locally |
| `npm test` | Run the Vitest suite once (CI mode) |
| `npm run test:watch` | Vitest in watch mode |

## Testing

```bash
npm test
```

Vitest reuses Vite's transform pipeline, so `.jsx` tests compile exactly like the app. Tests live next
to the code they cover as `*.test.{js,jsx}` under `src/`.

## Deployment

Pushing to this `frontend` branch triggers [`.github/workflows/deploy.yml`](.github/workflows/deploy.yml),
which builds, runs the test gate, and deploys to **Azure Static Web Apps** — routed to the production
environment via `production_branch: frontend`. The same SWA serves whichever backend is currently
deployed; the backends deploy independently from their own branches.

## Relationship to the backends

TaskBoard is one app with two interchangeable backend implementations, each on its own branch:

- [`main`](https://github.com/bgard68/ToDoApp/tree/main) — ASP.NET Core + **EF Core**
- [`dapper`](https://github.com/bgard68/ToDoApp/tree/dapper) — ASP.NET Core + **Dapper**

Both expose the same HTTP API, so this frontend runs unchanged against either.
