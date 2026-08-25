import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, act, waitFor } from '@testing-library/react';

const SCRIPT_SRC = 'https://accounts.google.com/gsi/client';

/**
 * The client id is read once at module load, so each test re-imports the component with the
 * environment it needs.
 */
async function loadButton(clientId) {
  vi.resetModules();
  if (clientId === undefined) {
    vi.stubEnv('VITE_GOOGLE_CLIENT_ID', '');
  } else {
    vi.stubEnv('VITE_GOOGLE_CLIENT_ID', clientId);
  }
  return (await import('./GoogleButton.jsx')).default;
}

/** Google Identity Services, reduced to the two calls this component makes. */
function stubGoogleIdentity() {
  const identity = {
    initialize: vi.fn(),
    renderButton: vi.fn(),
  };
  window.google = { accounts: { id: identity } };
  return identity;
}

/** A matchMedia whose change listeners the test can fire. */
function stubMatchMedia({ matches = false } = {}) {
  const listeners = new Set();
  const media = {
    matches,
    addEventListener: vi.fn((_event, fn) => listeners.add(fn)),
    removeEventListener: vi.fn((_event, fn) => listeners.delete(fn)),
  };
  window.matchMedia = vi.fn(() => media);
  return { media, fireChange: () => listeners.forEach((fn) => fn({ matches: !matches })) };
}

const originalMatchMedia = window.matchMedia;

beforeEach(() => {
  document.documentElement.removeAttribute('data-theme');
  document.querySelectorAll(`script[src="${SCRIPT_SRC}"]`).forEach((s) => s.remove());
  delete window.google;
});

afterEach(() => {
  vi.unstubAllEnvs();
  window.matchMedia = originalMatchMedia;
  delete window.google;
});

describe('GoogleButton without a client id', () => {
  it('explains what to configure instead of rendering a dead button', async () => {
    const GoogleButton = await loadButton('');

    render(<GoogleButton onCredential={vi.fn()} />);

    expect(screen.getByText(/VITE_GOOGLE_CLIENT_ID/)).toBeInTheDocument();
  });

  it('loads nothing from Google', async () => {
    const GoogleButton = await loadButton('');

    render(<GoogleButton onCredential={vi.fn()} />);

    expect(document.querySelector(`script[src="${SCRIPT_SRC}"]`)).toBeNull();
  });
});

describe('GoogleButton with a client id', () => {
  it('injects the Google script once and initializes when it loads', async () => {
    const GoogleButton = await loadButton('client-123.apps.googleusercontent.com');
    render(<GoogleButton onCredential={vi.fn()} />);

    const script = document.querySelector(`script[src="${SCRIPT_SRC}"]`);
    expect(script).not.toBeNull();
    expect(script.async).toBe(true);
    expect(script.defer).toBe(true);

    const identity = stubGoogleIdentity();
    await act(async () => { script.onload(); });

    expect(identity.initialize).toHaveBeenCalledWith(expect.objectContaining({
      client_id: 'client-123.apps.googleusercontent.com',
    }));
    expect(identity.renderButton).toHaveBeenCalled();
  });

  it('initializes straight away when the script is already on the page', async () => {
    const script = document.createElement('script');
    script.src = SCRIPT_SRC;
    document.body.appendChild(script);
    const identity = stubGoogleIdentity();

    const GoogleButton = await loadButton('client-123');
    await act(async () => { render(<GoogleButton onCredential={vi.fn()} />); });

    expect(identity.initialize).toHaveBeenCalled();
    // No second copy of the script.
    expect(document.querySelectorAll(`script[src="${SCRIPT_SRC}"]`)).toHaveLength(1);
  });

  it('hands the returned credential to the caller', async () => {
    const onCredential = vi.fn();
    const identity = stubGoogleIdentity();
    const script = document.createElement('script');
    script.src = SCRIPT_SRC;
    document.body.appendChild(script);

    const GoogleButton = await loadButton('client-123');
    await act(async () => { render(<GoogleButton onCredential={onCredential} />); });

    const { callback } = identity.initialize.mock.calls[0][0];
    callback({ credential: 'id-token-abc' });

    expect(onCredential).toHaveBeenCalledWith('id-token-abc');
  });

  it('survives a credential arriving with no handler attached', async () => {
    const identity = stubGoogleIdentity();
    const script = document.createElement('script');
    script.src = SCRIPT_SRC;
    document.body.appendChild(script);

    const GoogleButton = await loadButton('client-123');
    await act(async () => { render(<GoogleButton onCredential={undefined} />); });

    const { callback } = identity.initialize.mock.calls[0][0];
    expect(() => callback({ credential: 'id-token-abc' })).not.toThrow();
  });

  it('does nothing when the script loads but Google never appears', async () => {
    const GoogleButton = await loadButton('client-123');
    render(<GoogleButton onCredential={vi.fn()} />);

    const script = document.querySelector(`script[src="${SCRIPT_SRC}"]`);

    // No window.google — the load handler must not throw.
    await act(async () => { expect(() => script.onload()).not.toThrow(); });
  });
});

describe('GoogleButton theming', () => {
  async function renderConfigured({ onCredential = vi.fn() } = {}) {
    const script = document.createElement('script');
    script.src = SCRIPT_SRC;
    document.body.appendChild(script);
    const identity = stubGoogleIdentity();

    const GoogleButton = await loadButton('client-123');
    const view = render(<GoogleButton onCredential={onCredential} />);
    await act(async () => {});
    return { identity, view };
  }

  const themeOf = (identity) =>
    identity.renderButton.mock.calls.at(-1)[1].theme;

  it('uses the dark treatment when the page is explicitly dark', async () => {
    document.documentElement.setAttribute('data-theme', 'dark');

    const { identity } = await renderConfigured();

    expect(themeOf(identity)).toBe('filled_black');
  });

  it('uses the light treatment when the page is explicitly light', async () => {
    document.documentElement.setAttribute('data-theme', 'light');
    stubMatchMedia({ matches: true }); // an explicit choice must win over the OS

    const { identity } = await renderConfigured();

    expect(themeOf(identity)).toBe('outline');
  });

  it('follows the OS preference when no explicit choice has been made', async () => {
    stubMatchMedia({ matches: true });

    const { identity } = await renderConfigured();

    expect(themeOf(identity)).toBe('filled_black');
  });

  it('treats an unavailable matchMedia as light rather than failing', async () => {
    window.matchMedia = vi.fn(() => { throw new Error('unsupported'); });

    const { identity } = await renderConfigured();

    expect(themeOf(identity)).toBe('outline');
  });

  it('redraws when the theme attribute flips', async () => {
    const { identity } = await renderConfigured();
    const before = identity.renderButton.mock.calls.length;

    await act(async () => {
      document.documentElement.setAttribute('data-theme', 'dark');
      // MutationObserver callbacks are delivered as microtasks.
      await Promise.resolve();
    });

    await waitFor(() => expect(identity.renderButton.mock.calls.length).toBeGreaterThan(before));
    expect(themeOf(identity)).toBe('filled_black');
  });

  it('redraws when the OS preference changes', async () => {
    const { fireChange } = stubMatchMedia({ matches: false });
    const { identity } = await renderConfigured();
    const before = identity.renderButton.mock.calls.length;

    await act(async () => { fireChange(); });

    expect(identity.renderButton.mock.calls.length).toBeGreaterThan(before);
  });

  it('stops listening once it unmounts', async () => {
    const { media } = stubMatchMedia();
    const { view } = await renderConfigured();

    view.unmount();

    expect(media.removeEventListener).toHaveBeenCalled();
  });

  it('unmounts cleanly when matchMedia was unavailable', async () => {
    window.matchMedia = vi.fn(() => { throw new Error('unsupported'); });
    const { view } = await renderConfigured();

    expect(() => view.unmount()).not.toThrow();
  });

  it('clamps the button width to the range Google accepts', async () => {
    // jsdom reports a zero-width box, so the component falls back to its default.
    const { identity } = await renderConfigured();

    const { width } = identity.renderButton.mock.calls.at(-1)[1];
    expect(width).toBeGreaterThanOrEqual(240);
    expect(width).toBeLessThanOrEqual(400);
  });

  it('measures the surrounding box when the layout reports one', async () => {
    const spy = vi
      .spyOn(Element.prototype, 'getBoundingClientRect')
      .mockReturnValue({ width: 1000, height: 40, top: 0, left: 0, right: 0, bottom: 0 });

    const { identity } = await renderConfigured();

    expect(identity.renderButton.mock.calls.at(-1)[1].width).toBe(400); // clamped down
    spy.mockRestore();
  });

  it('skips the redraw when Google is no longer available', async () => {
    const { identity } = await renderConfigured();
    const before = identity.renderButton.mock.calls.length;
    delete window.google;

    await act(async () => {
      document.documentElement.setAttribute('data-theme', 'dark');
      await Promise.resolve();
    });

    expect(identity.renderButton.mock.calls.length).toBe(before);
  });
});
