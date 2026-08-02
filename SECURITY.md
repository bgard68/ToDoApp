# Security

## Reporting a vulnerability

Use GitHub's private reporting:
[**Report a vulnerability**](https://github.com/bgard68/ToDoApp/security/advisories/new).
It opens a private advisory visible only to the maintainer, so a problem can be
fixed before it is described in public. Please do not open a public issue for
anything exploitable.

Expect a first response within a week. This is a personal project with one
maintainer, not a product with an on-call rotation — that is the honest
expectation to set rather than a service level nobody is paying for.

## What the threat model actually is

Unlike a static site, this one has real exposure and it is worth stating
plainly rather than implying there is nothing to find.

- **There is a server.** An ASP.NET Core API, publicly reachable, holding a
  database.
- **There are accounts.** Local registration with password login, refresh
  tokens with revocation, and Google sign-in.
- **There is user data.** Todo items belong to a user, and one user must never
  be able to read or change another's.
- **There are secrets.** The JWT signing key lives in Azure Key Vault and is
  read at runtime through managed identity. None is in this repository, and
  none should ever be.

So the realistic exposure is the ordinary one for an authenticated web API:
whether authentication can be bypassed, whether authorisation is enforced on
every path that touches data, and whether anything that crosses a trust
boundary is handled as untrusted.

### Worth reporting

- **Authentication or authorisation bypass.** Reading or modifying another
  user's todos or categories is the single highest-value finding here — every
  data-bearing endpoint is meant to scope by the authenticated user.
- **Token handling.** A refresh token that survives revocation, a JWT accepted
  with a wrong signature, audience or issuer, or one that outlives its expiry.
- **The Google sign-in path**, including an ID token accepted from the wrong
  audience, or account takeover by linking an unverified email.
- **Rate-limit bypass on the auth endpoints.** Login is throttled deliberately;
  a way around it turns a slow guessing attack into a fast one.
- **Injection or unsafe deserialisation** anywhere user input reaches the
  database or the log.
- **Cross-site scripting in the React frontend**, or a CORS or
  security-header configuration that lets another origin act on a user's
  behalf.
- **Anything that reveals whether an email address is registered** through
  timing or a differing response. Login deliberately hashes a dummy password
  for unknown users so the two cost the same.

### Not vulnerabilities here

- **The seeded demo account.** `demo@todoapp.local` exists on purpose so the
  app can be tried without registering. It holds nothing but sample todos.
  Reporting that it can be logged into is reporting the feature.
- **"The API is public."** It is meant to be. What matters is what it lets an
  unauthenticated caller do, which should be: register, log in, and nothing
  else.
- **"Dependency X has a CVE"** with no path to exploitation here. NuGet audit
  runs in `all` mode and fails the build on any advisory with a fix available;
  a report that adds nothing to that is noise.
- **Anything reachable only with the deployment credentials.** Those are
  GitHub secrets scoped to the `production` environment; if you have them, the
  compromise happened elsewhere.

## How the pipeline is protected

Every pull request must pass, and cannot be merged without: build and tests,
the container image build with a blocking Trivy scan, CodeQL, and a gitleaks
scan of the whole history. Every action is pinned to a commit SHA. Dependencies
install from a committed lock file, so what deploys is the tree that was
reviewed. The deploy authenticates to Azure with OIDC — there is no stored
publish profile or password.

A separate job, `gate-probes.yml`, shows each of those gates something it must
reject and fails if any of them does not. That exists because a check which
cannot fail looks exactly like a check with nothing to report: the container
scan ran for its entire life with `exit-code: 0`, reporting findings faithfully
and blocking nothing, until 28 had accumulated behind a passing job.
