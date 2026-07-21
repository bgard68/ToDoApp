# LinkedIn — Projects section entry

> Fill these into: Profile → Add profile section → Recommended (or Additional) → Add projects.
> Note: the Projects section takes a LINK, not a video upload. Put the MP4 in your Featured
> section (or a feed post). This entry links to the GitHub repo.

## Project name
TaskBoard — Full-Stack Kanban App (.NET 10 + React + Azure)

_(alt: Multi-User Kanban Board — Clean Architecture + JWT)_

## Project URL
Primary (recommended): https://github.com/bgard68/ToDoApp
   - Always works, shows the code — the safest single link for the one URL field.
Live demo: https://salmon-field-054249810.7.azurestaticapps.net/
   - Works. If it's been idle a while, the Azure SQL serverless DB may take ~20-30s to
     resume on the first login (auto-pause to save cost), then it's instant.

## Dates
Start month → end month, or tick "I'm currently working on this project" if ongoing.

## Contributors
Add only people who actually contributed. Solo → leave empty.

## Description (plain text — fits LinkedIn's 2,000-char limit)

A full-stack, multi-user Kanban board built to production standards — the focus wasn't the feature list, but the engineering underneath it.

Tasks move across To Do / In Progress / Done lanes as drag-and-drop, category-colored notes. React + Vite single-page frontend with dark mode, Google sign-in, and a mobile layout.

Backend:
• Clean Architecture + CQRS (MediatR) — dependencies point inward, so the same handlers run on SQLite locally and Azure SQL in production, unchanged.
• JWT auth with rotating, single-use refresh tokens and reuse detection. Security-stamp revocation powers "sign out everywhere," killing every session instantly.
• Per-user authorization (cross-user access returns 404) and optimistic concurrency (conflicting edits return 409, not silent overwrites).
• Secrets handled right — signing key from user-secrets locally, Azure Key Vault in production; the app fails fast without it.

Quality & delivery:
• Tested end-to-end: xUnit + WebApplicationFactory over the real HTTP pipeline; Vitest + React Testing Library on the frontend.
• Deployed on Azure (App Service + Azure SQL + Static Web Apps + Key Vault) via GitHub Actions using OIDC — zero stored cloud credentials.

Tech: .NET 10 · ASP.NET Core · EF Core 10 · MediatR · FluentValidation · React 18 · Vite · Azure · GitHub Actions

Live demo: https://salmon-field-054249810.7.azurestaticapps.net/ (demo login shown on the page; first load may take ~20-30s while the server wakes)
Code: https://github.com/bgard68/ToDoApp

## Skills to attach (also add these to your profile's Skills section)
C# · .NET · ASP.NET Core · Clean Architecture · CQRS · Entity Framework Core · Authentication (JWT) · React.js · JavaScript · Microsoft Azure · CI/CD · GitHub Actions · Unit Testing · Software Design Patterns

## Where the video goes
The 9:16 demo (demo/todoapp-demo-vertical.mp4) can't attach to the Projects section.
Put it in: Profile → Featured → + → Add media → upload the MP4.
(Optional: also embed a GIF in the repo README so it plays on the repo page you linked.)
