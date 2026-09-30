# Presentations

Two slideshows explaining how TaskBoard handles sign-in, access, revocation and rate limiting —
one for a general audience, one for developers. Each is a single self-contained HTML file.

| Deck | Audience | Slides | What it covers |
| ---- | -------- | ------ | -------------- |
| **[Security walkthrough](security-walkthrough.html)** | Non-technical | 10 | The whole flow told through everyday things: a hotel key card, a driver's license hologram, changing your locks after losing your keys, a phone passcode lockout. No jargon on the slides; the last slide maps each analogy to its technical term. |
| **[Auth internals](auth-internals.html)** | Developers | 12 | Middleware order, PBKDF2 hashing, the access/refresh token model, JWT anatomy, `TokenValidationParameters` + the `OnTokenValidated` security-stamp check, endpoint/row/role authorization, refresh rotation with reuse detection, revocation, the per-IP rate limiter and `ClientAddress`, the three lessons-learned bugs, and a status-code/file reference. |

## Viewing and presenting

GitHub shows `.html` files as source, so open them in a browser instead: clone the repo (or use
**Download raw file** on GitHub) and double-click the file. No build step or server is needed.

- **Next / previous slide:** → or Space / ←
- **First / last slide:** Home / End
- **Full screen:** F11
- **Link to a slide:** each slide has an anchor, e.g. `auth-internals.html#refresh`

The pages follow the viewer's light/dark setting and work at phone width.

## Keeping them accurate

The developer deck quotes code from `src/`. If you change any of the files below, check the
matching slide:

| Slide | Source |
| ----- | ------ |
| Pipeline order | `src/TodoApp.WebApi/Program.cs` |
| Password hashing | `src/TodoApp.Infrastructure/Authentication/PasswordHasher.cs` |
| Token validation / security stamp | `src/TodoApp.WebApi/Authentication/AuthenticationSetup.cs` |
| Refresh cookie + CSRF header | `src/TodoApp.WebApi/Authentication/RefreshTokenCookie.cs` |
| Rotation / reuse detection | `src/TodoApp.Application/Auth/Commands/RefreshToken/RefreshTokenCommandHandler.cs` |
| Revoke all | `src/TodoApp.Application/Auth/Commands/RevokeAllTokens/RevokeAllTokensCommandHandler.cs` |
| Rate limiter + partition key | `src/TodoApp.WebApi/Program.cs`, `src/TodoApp.WebApi/ClientAddress.cs` |
