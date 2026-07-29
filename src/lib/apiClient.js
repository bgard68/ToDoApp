// API client for the Todo backend with JWT auth.
//
// - Access token: in memory only. Lost on reload, which is fine — the refresh cookie restores it.
// - Refresh token: NEVER touched by this code. It lives in an httpOnly cookie the browser holds
//   and attaches automatically, so no script (including an injected one) can read it.
//   This closes review finding H2; it used to sit in localStorage, where any XSS could lift a
//   7-day, silently-renewing session in a single line.
//
// Because the cookie is httpOnly we cannot tell whether one exists. `credentials: 'include'` on
// every call lets the browser decide, and a failed refresh simply means "not signed in".
const BASE = (import.meta.env.VITE_API_URL || '').replace(/\/$/, '');

// CSRF proof for the refresh endpoint. A cross-site form post or image tag cannot set a custom
// header — the browser must first pass a CORS preflight the API only grants to this origin — so
// sending the header at all is the proof. Its value is irrelevant.
//
// An earlier version used a double-submit cookie whose value had to be echoed here. That cannot
// work across domains: the cookie is set by the API's host, and document.cookie only ever exposes
// cookies for the page's own host. Every refresh would have 401'd.
const CSRF_HEADER = 'X-Refresh-CSRF';

let accessToken = null;
let onUnauthorized = null;

export function setOnUnauthorized(fn) {
  onUnauthorized = fn;
}

export function setSession(auth) {
  accessToken = auth.accessToken;
  // Nothing else to do: the server set the refresh cookie on this response.
}

export function clearSession() {
  accessToken = null;
  // The refresh cookie is cleared server-side by /api/auth/logout.
}

/**
 * Whether a silent sign-in is worth attempting. The refresh cookie is httpOnly and cross-site, so
 * there is nothing readable to test — just try the refresh and treat 401 as "not signed in".
 */
export function hasSession() {
  return true;
}

async function parse(res) {
  if (res.status === 204) return null;
  const text = await res.text();
  return text ? JSON.parse(text) : null;
}

function toError(res, data) {
  const message = (data && (data.title || data.detail)) || `Request failed (${res.status})`;
  const error = new Error(message);
  error.status = res.status;
  error.problem = data;
  return error;
}

// --- Cold-start resilience --------------------------------------------------
// Azure's Free (F1) App Service unloads the app after idle and the serverless SQL
// database auto-pauses, so the FIRST request after a quiet spell can take ~30-60s
// to wake. Rather than fail with "Failed to fetch", retry transient warm-up
// failures with backoff and let the UI show a "Waking the server up..." note
// (register a handler via setOnServerWaking).
let onServerWaking = null;
export function setOnServerWaking(fn) {
  onServerWaking = fn;
}

const WAKE_MAX_RETRIES = 6; // extra attempts after the first
const WAKE_BASE_MS = 2000; // backoff base
const WAKE_MAX_MS = 12000; // backoff cap
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// 502/503/504 are what Azure returns while an instance is still starting up.
const isWarmingUp = (res) => res.status === 502 || res.status === 503 || res.status === 504;

// fetch() wrapper that transparently retries cold-start failures (a network error,
// or a 502/503/504) with exponential backoff, signalling the UI while it waits and
// clearing the signal once the server responds.
async function wakeFetch(url, init) {
  let signalled = false;
  for (let attempt = 0; ; attempt++) {
    try {
      const res = await fetch(url, init);
      if (attempt >= WAKE_MAX_RETRIES || !isWarmingUp(res)) {
        if (signalled) onServerWaking?.(false);
        return res;
      }
    } catch (err) {
      // Server unreachable (still waking, or genuinely down) — retry until budget.
      if (attempt >= WAKE_MAX_RETRIES) {
        if (signalled) onServerWaking?.(false);
        throw err;
      }
    }
    if (!signalled) {
      signalled = true;
      onServerWaking?.(true);
    }
    await sleep(Math.min(WAKE_BASE_MS * 2 ** attempt, WAKE_MAX_MS));
  }
}

function doFetch(path, options = {}) {
  const headers = { 'Content-Type': 'application/json', ...(options.headers || {}) };
  if (accessToken) headers.Authorization = `Bearer ${accessToken}`;
  // credentials: 'include' — the refresh cookie is cross-site (SPA and API are different hosts),
  // so the browser only attaches it when asked. The API's CORS policy names this origin
  // explicitly and allows credentials.
  return wakeFetch(`${BASE}${path}`, { ...options, headers, credentials: 'include' });
}

// A single in-flight refresh shared by all callers. Without this, two requests that
// 401 at the same instant would each POST the same refresh token; the backend rotates
// tokens on refresh and treats a second use of the now-rotated token as reuse/compromise,
// revoking every session and signing the user out everywhere. De-duplicating avoids that.
let refreshInFlight = null;

function refreshSession() {
  if (!refreshInFlight) {
    refreshInFlight = performRefresh().finally(() => {
      refreshInFlight = null;
    });
  }
  return refreshInFlight;
}

async function performRefresh() {
  const res = await wakeFetch(`${BASE}/api/auth/refresh`, {
    method: 'POST',
    // The header's presence is the CSRF proof; '1' is as good as any value.
    headers: { 'Content-Type': 'application/json', [CSRF_HEADER]: '1' },
    credentials: 'include',   // the httpOnly cookie carries the actual token
    body: '{}',
  });

  if (!res.ok) {
    clearSession();
    return false;
  }

  setSession(await parse(res));
  return true;
}

// Authenticated request with a single transparent refresh-and-retry on 401.
async function request(path, options = {}) {
  let res = await doFetch(path, options);

  if (res.status === 401 && hasSession()) {
    const refreshed = await refreshSession();
    if (refreshed) res = await doFetch(path, options);
  }

  const data = await parse(res);

  if (!res.ok) {
    if (res.status === 401) {
      clearSession();
      onUnauthorized?.();
    }
    throw toError(res, data);
  }

  return data;
}

async function publicPost(path, body) {
  const res = await wakeFetch(`${BASE}${path}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    credentials: 'include',   // so the browser stores the refresh cookie the server sets
    body: JSON.stringify(body),
  });
  const data = await parse(res);
  if (!res.ok) throw toError(res, data);
  return data;
}

export const AuthApi = {
  async register(email, password) {
    const auth = await publicPost('/api/auth/register', { email, password });
    setSession(auth);
    return auth;
  },
  async login(email, password) {
    const auth = await publicPost('/api/auth/login', { email, password });
    setSession(auth);
    return auth;
  },
  async google(idToken) {
    const auth = await publicPost('/api/auth/google', { idToken });
    setSession(auth);
    return auth;
  },
  refresh() {
    return refreshSession();
  },
  me() {
    return request('/api/auth/me');
  },
  async logout() {
    try {
      // The server reads the refresh token from the cookie and clears it on the way out.
      await request('/api/auth/logout', { method: 'POST', body: '{}' });
    } finally {
      clearSession();
    }
  },
  async revokeAll() {
    try {
      await request('/api/auth/revoke-all', { method: 'POST', body: JSON.stringify({}) });
    } finally {
      clearSession();
    }
  },
};

export const CategoryApi = {
  list() {
    return request('/api/categories');
  },
  create(category) {
    return request('/api/categories', { method: 'POST', body: JSON.stringify(category) });
  },
  update(id, category) {
    return request(`/api/categories/${id}`, { method: 'PUT', body: JSON.stringify(category) });
  },
  remove(id) {
    return request(`/api/categories/${id}`, { method: 'DELETE' });
  },
};

export const TodoApi = {
  list(filter = 'All', search = '') {
    const params = new URLSearchParams();
    if (filter && filter !== 'All') params.set('filter', filter);
    if (search) params.set('search', search);
    const qs = params.toString();
    return request(`/api/todos${qs ? `?${qs}` : ''}`);
  },
  create(todo) {
    return request('/api/todos', { method: 'POST', body: JSON.stringify(todo) });
  },
  update(id, todo) {
    return request(`/api/todos/${id}`, { method: 'PUT', body: JSON.stringify(todo) });
  },
  changeStatus(id, status, concurrencyToken) {
    return request(`/api/todos/${id}/status`, {
      method: 'PATCH',
      body: JSON.stringify({ status, concurrencyToken }),
    });
  },
  remove(id) {
    return request(`/api/todos/${id}`, { method: 'DELETE' });
  },
};
