// API client for the Todo backend with JWT auth.
// - Access token is held in memory only.
// - Refresh token is persisted so a page reload can silently re-authenticate.
//   (For production, prefer an httpOnly cookie for the refresh token; see README.)
const BASE = (import.meta.env.VITE_API_URL || '').replace(/\/$/, '');
const REFRESH_KEY = 'todo.refreshToken';

let accessToken = null;
let onUnauthorized = null;

export function setOnUnauthorized(fn) {
  onUnauthorized = fn;
}

export function setSession(auth) {
  accessToken = auth.accessToken;
  localStorage.setItem(REFRESH_KEY, auth.refreshToken);
}

export function clearSession() {
  accessToken = null;
  localStorage.removeItem(REFRESH_KEY);
}

export function getRefreshToken() {
  return localStorage.getItem(REFRESH_KEY);
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

function doFetch(path, options = {}) {
  const headers = { 'Content-Type': 'application/json', ...(options.headers || {}) };
  if (accessToken) headers.Authorization = `Bearer ${accessToken}`;
  return fetch(`${BASE}${path}`, { ...options, headers });
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
  const refreshToken = getRefreshToken();
  if (!refreshToken) return false;

  const res = await fetch(`${BASE}/api/auth/refresh`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ refreshToken }),
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

  if (res.status === 401 && getRefreshToken()) {
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
  const res = await fetch(`${BASE}${path}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
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
    const refreshToken = getRefreshToken();
    try {
      if (refreshToken) {
        await request('/api/auth/logout', {
          method: 'POST',
          body: JSON.stringify({ refreshToken }),
        });
      }
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
