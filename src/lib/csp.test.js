import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * Review finding H2 — the response security headers.
 *
 * The CSP allows the inline theme script in index.html by SHA-256 hash, because static hosting
 * can't issue a per-response nonce. That makes the two files silently coupled: editing the inline
 * script by one character invalidates the hash and the browser quietly refuses to run it — no
 * crash, just the theme flash coming back.
 *
 * These tests recompute the hash and fail loudly when they drift, and assert that both delivery
 * paths (Azure Static Web Apps and the nginx container) actually carry the headers.
 */

// Read from the project root. import.meta.url isn't a file: URL under the jsdom environment,
// and vitest always runs with cwd at the project root.
const fromRoot = (name) => readFileSync(resolve(process.cwd(), name), 'utf8');

const swaConfig = JSON.parse(fromRoot('staticwebapp.config.json'));
const nginxConf = fromRoot('nginx.conf');
const indexHtml = fromRoot('index.html');

const swaHeaders = swaConfig.globalHeaders ?? {};
const swaCsp = swaHeaders['Content-Security-Policy'] ?? '';

function inlineScriptHash() {
  const match = indexHtml.match(/<script>([\s\S]*?)<\/script>/);
  expect(match, 'index.html should contain the inline theme script').not.toBeNull();

  // Hash the LF form. A browser hashes the exact bytes it receives, and the deployed artifact is
  // built on Linux, so LF is the authoritative newline. A Windows working copy would otherwise
  // produce a different (and wrong) hash purely from CRLF — .gitattributes pins index.html to LF
  // so a local Docker build agrees with production, and this normalisation makes the test agree
  // regardless of how the file was checked out.
  const script = match[1].replace(/\r\n/g, '\n');
  const digest = createHash('sha256').update(script, 'utf8').digest('base64');
  return `sha256-${digest}`;
}

describe('Content-Security-Policy', () => {
  it('allows the current inline theme script by hash', () => {
    const expected = inlineScriptHash();

    expect(
      swaCsp,
      `The inline <script> in index.html changed, so its CSP hash is stale and the browser will ` +
        `refuse to run it. Replace the sha256-... value in staticwebapp.config.json AND nginx.conf ` +
        `with:\n\n    '${expected}'\n`,
    ).toContain(`'${expected}'`);

    expect(nginxConf, 'nginx.conf must carry the same hash').toContain(`'${expected}'`);
  });

  it('denies everything by default and forbids framing', () => {
    expect(swaCsp).toContain("default-src 'self'");
    expect(swaCsp).toContain("frame-ancestors 'none'");
    expect(swaCsp).toContain("object-src 'none'");
    expect(swaCsp).toContain("base-uri 'self'");
  });

  it('permits Google Identity Services, which GoogleButton.jsx injects at runtime', () => {
    // If this is ever tightened, the "Sign in with Google" button silently stops rendering.
    expect(swaCsp).toContain('https://accounts.google.com');
    expect(swaCsp).toMatch(/frame-src[^;]*https:\/\/accounts\.google\.com/);
  });

  it('does not allow arbitrary inline or eval-ed script', () => {
    const scriptSrc = swaCsp.split(';').find((d) => d.trim().startsWith('script-src')) ?? '';
    expect(scriptSrc).not.toContain("'unsafe-inline'");
    expect(scriptSrc).not.toContain("'unsafe-eval'");
  });
});

describe('baseline security headers', () => {
  it.each([
    ['X-Content-Type-Options', 'nosniff'],
    ['X-Frame-Options', 'DENY'],
    ['Referrer-Policy', 'strict-origin-when-cross-origin'],
  ])('%s is set on the Static Web App', (header, value) => {
    expect(swaHeaders[header]).toBe(value);
  });

  it('sets HSTS on the Static Web App', () => {
    expect(swaHeaders['Strict-Transport-Security']).toMatch(/max-age=\d+/);
  });

  it('sets the same baseline headers in the nginx container', () => {
    for (const header of [
      'Content-Security-Policy',
      'X-Content-Type-Options',
      'X-Frame-Options',
      'Referrer-Policy',
      'Permissions-Policy',
    ]) {
      expect(nginxConf, `${header} missing from nginx.conf`).toContain(`add_header ${header}`);
    }
  });

  it('emits nginx headers on error responses too', () => {
    // Without `always`, nginx omits add_header on 4xx/5xx — exactly the responses an attacker
    // is most likely to be probing.
    const headerLines = nginxConf.split('\n').filter((l) => l.trim().startsWith('add_header'));
    expect(headerLines.length).toBeGreaterThan(0);
    for (const line of headerLines) {
      expect(line.trim(), `missing "always": ${line.trim()}`).toMatch(/always;$/);
    }
  });
});
