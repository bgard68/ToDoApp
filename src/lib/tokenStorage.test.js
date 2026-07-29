import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * Review finding H2 — the refresh token must never be readable by script.
 *
 * It now lives in an httpOnly cookie the browser attaches automatically. That property is
 * invisible to a normal unit test (the client can't see the cookie, which is the point), so this
 * guards the thing that CAN regress: someone reintroducing browser storage for the token because
 * "reload loses the session".
 */

const fromRoot = (name) => readFileSync(resolve(process.cwd(), name), 'utf8');

/**
 * Strip comments before asserting. The source deliberately *mentions* localStorage when
 * explaining why it is no longer used; a naive substring check flags that prose and would push
 * the next person to delete the explanation to get green — the opposite of useful.
 */
const code = (source) =>
  source.replace(/\/\*[\s\S]*?\*\//g, '').replace(/(^|[^:])\/\/.*$/gm, '$1');

const apiClient = fromRoot('src/lib/apiClient.js');
const appJsx = fromRoot('src/App.jsx');

describe('refresh token storage', () => {
  it('the api client never touches localStorage or sessionStorage', () => {
    // A single localStorage.setItem here re-opens the exact XSS-to-account-takeover path H2 closed.
    expect(code(apiClient)).not.toMatch(/\blocalStorage\s*\./);
    expect(code(apiClient)).not.toMatch(/\bsessionStorage\s*\./);
  });

  it('no module stores anything named like a refresh token in browser storage', () => {
    for (const [name, source] of [
      ['apiClient.js', apiClient],
      ['App.jsx', appJsx],
    ]) {
      expect(code(source), `${name} must not persist a refresh token`).not.toMatch(
        /(local|session)Storage\.setItem\([^)]*refresh/i,
      );
    }
  });

  it('sends credentials so the browser can attach the httpOnly cookie', () => {
    // Cross-site by necessity: the SPA and API are on different hosts, so without this the
    // cookie is silently omitted and every refresh fails with no visible cause.
    expect(code(apiClient)).toMatch(/credentials:\s*'include'/);
  });

  it('sends the custom CSRF header on refresh', () => {
    expect(code(apiClient)).toMatch(/X-Refresh-CSRF/);
  });

  it('does not try to read a cookie set by the API host', () => {
    // document.cookie only ever exposes cookies for the PAGE's host. The SPA and API are on
    // different domains, so any attempt to read an API-set cookie silently yields null and
    // breaks refresh for everyone — which is exactly the bug this replaced.
    expect(code(apiClient)).not.toMatch(/document\.cookie/);
  });

  it('does not send the refresh token in a request body', () => {
    // The whole point is that the client no longer possesses it.
    expect(code(apiClient)).not.toMatch(/JSON\.stringify\(\{\s*refreshToken/);
  });
});
