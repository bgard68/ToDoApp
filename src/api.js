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

async function refreshSession() {
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

export const PRIORITIES = [
  { value: 0, label: 'Low' },
  { value: 1, label: 'Medium' },
  { value: 2, label: 'High' },
];

// Kanban lanes (must match the backend TodoStatus enum).
export const STATUSES = [
  { value: 0, label: 'To Do' },
  { value: 1, label: 'In Progress' },
  { value: 2, label: 'Done' },
];

// A neutral fallback used when a task has no category (or its category was deleted).
export const DEFAULT_CATEGORY_COLOR = '#64748b';

// Parse a #RRGGBB hex string to [r, g, b]. Returns null if it isn't a valid 6-digit hex.
function hexToRgb(hex) {
  const match = /^#?([0-9a-f]{6})$/i.exec((hex || '').trim());
  if (!match) return null;
  const int = parseInt(match[1], 16);
  return [(int >> 16) & 255, (int >> 8) & 255, int & 255];
}

// Mix a color toward white by `amount` (0 = original, 1 = white). Used for the soft
// post-it fill so the category color stays legible behind dark text.
export function tint(hex, amount = 0.7) {
  const rgb = hexToRgb(hex);
  if (!rgb) return tint(DEFAULT_CATEGORY_COLOR, amount);
  const mixed = rgb.map((c) => Math.round(c + (255 - c) * amount));
  return `rgb(${mixed[0]}, ${mixed[1]}, ${mixed[2]})`;
}

// Look up a category object by id from a fetched list; null id or unknown id → null.
export function findCategory(categories, id) {
  if (id === null || id === undefined) return null;
  return categories.find((c) => c.id === id) || null;
}
