import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// Every test re-imports the module so the in-memory access token, the shared in-flight
// refresh, and the registered callbacks all start clean.
async function loadClient() {
  vi.resetModules();
  return import('./apiClient.js');
}

/** A fetch response stub. `body` is serialised unless it is already a string. */
function respond(status, body = null, { text } = {}) {
  return {
    ok: status >= 200 && status < 300,
    status,
    text: async () => (text !== undefined ? text : body === null ? '' : JSON.stringify(body)),
  };
}

let fetchMock;

beforeEach(() => {
  fetchMock = vi.fn();
  vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

describe('request plumbing', () => {
  it('sends JSON and credentials, and returns the parsed body', async () => {
    const { TodoApi } = await loadClient();
    fetchMock.mockResolvedValue(respond(200, [{ id: 1 }]));

    const todos = await TodoApi.list();

    expect(todos).toEqual([{ id: 1 }]);
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe('/api/todos');
    expect(init.credentials).toBe('include');
    expect(init.headers['Content-Type']).toBe('application/json');
  });

  it('omits the Authorization header until a session is set', async () => {
    const { TodoApi, setSession } = await loadClient();
    fetchMock.mockResolvedValue(respond(200, []));

    await TodoApi.list();
    expect(fetchMock.mock.calls[0][1].headers.Authorization).toBeUndefined();

    setSession({ accessToken: 'token-abc' });
    await TodoApi.list();
    expect(fetchMock.mock.calls[1][1].headers.Authorization).toBe('Bearer token-abc');
  });

  it('drops the Authorization header again once the session is cleared', async () => {
    const { TodoApi, setSession, clearSession } = await loadClient();
    fetchMock.mockResolvedValue(respond(200, []));

    setSession({ accessToken: 'token-abc' });
    clearSession();
    await TodoApi.list();

    expect(fetchMock.mock.calls[0][1].headers.Authorization).toBeUndefined();
  });

  it('treats 204 as no content', async () => {
    const { TodoApi } = await loadClient();
    fetchMock.mockResolvedValue(respond(204));

    await expect(TodoApi.remove(1)).resolves.toBeNull();
  });

  it('treats an empty body as no content', async () => {
    const { TodoApi } = await loadClient();
    fetchMock.mockResolvedValue(respond(200, null, { text: '' }));

    await expect(TodoApi.list()).resolves.toBeNull();
  });

  it('hasSession is always true — the refresh cookie is httpOnly and cannot be inspected', async () => {
    const { hasSession } = await loadClient();

    expect(hasSession()).toBe(true);
  });
});

describe('error mapping', () => {
  it('prefers the problem title', async () => {
    const { TodoApi } = await loadClient();
    fetchMock.mockResolvedValue(respond(400, { title: 'Bad input', detail: 'ignored' }));

    await expect(TodoApi.list()).rejects.toThrow('Bad input');
  });

  it('falls back to the problem detail', async () => {
    const { TodoApi } = await loadClient();
    fetchMock.mockResolvedValue(respond(409, { detail: 'Already exists' }));

    await expect(TodoApi.list()).rejects.toThrow('Already exists');
  });

  it('falls back to the status code when the body carries neither', async () => {
    const { TodoApi } = await loadClient();
    fetchMock.mockResolvedValue(respond(500, {}));

    await expect(TodoApi.list()).rejects.toThrow('Request failed (500)');
  });

  it('falls back to the status code when there is no body at all', async () => {
    const { TodoApi } = await loadClient();
    fetchMock.mockResolvedValue(respond(500, null, { text: '' }));

    await expect(TodoApi.list()).rejects.toThrow('Request failed (500)');
  });

  it('carries the status and the problem document on the error', async () => {
    const { TodoApi } = await loadClient();
    const problem = { title: 'Conflict', current: { id: 1 } };
    fetchMock.mockResolvedValue(respond(409, problem));

    await expect(TodoApi.list()).rejects.toMatchObject({ status: 409, problem });
  });
});

describe('401 refresh-and-retry', () => {
  it('refreshes once and replays the original request', async () => {
    const { TodoApi } = await loadClient();
    fetchMock
      .mockResolvedValueOnce(respond(401, { title: 'Expired' }))
      .mockResolvedValueOnce(respond(200, { accessToken: 'fresh' }))   // the refresh
      .mockResolvedValueOnce(respond(200, [{ id: 7 }]));               // the replay

    await expect(TodoApi.list()).resolves.toEqual([{ id: 7 }]);

    const [, refreshInit] = fetchMock.mock.calls[1];
    expect(refreshInit.method).toBe('POST');
    // The header's presence is the CSRF proof; its value is irrelevant.
    expect(refreshInit.headers['X-Refresh-CSRF']).toBeDefined();
    // The replay carries the token the refresh returned.
    expect(fetchMock.mock.calls[2][1].headers.Authorization).toBe('Bearer fresh');
  });

  it('gives up and notifies when the refresh itself fails', async () => {
    const { TodoApi, setOnUnauthorized, setSession } = await loadClient();
    const onUnauthorized = vi.fn();
    setOnUnauthorized(onUnauthorized);
    setSession({ accessToken: 'stale' });

    fetchMock
      .mockResolvedValueOnce(respond(401, { title: 'Expired' }))
      .mockResolvedValueOnce(respond(401, { title: 'No cookie' }));    // the refresh

    await expect(TodoApi.list()).rejects.toThrow('Expired');

    expect(onUnauthorized).toHaveBeenCalledTimes(1);
    expect(fetchMock).toHaveBeenCalledTimes(2); // no replay after a failed refresh
  });

  it('does not require an unauthorized handler to be registered', async () => {
    const { TodoApi } = await loadClient();
    fetchMock
      .mockResolvedValueOnce(respond(401, { title: 'Expired' }))
      .mockResolvedValueOnce(respond(401, { title: 'No cookie' }));

    await expect(TodoApi.list()).rejects.toThrow('Expired');
  });

  it('shares one refresh between requests that 401 at the same instant', async () => {
    const { TodoApi } = await loadClient();
    let releaseRefresh;
    const refreshGate = new Promise((resolve) => { releaseRefresh = resolve; });

    fetchMock.mockImplementation(async (url) => {
      if (url.endsWith('/api/auth/refresh')) {
        await refreshGate;
        return respond(200, { accessToken: 'fresh' });
      }
      // First call from each of the two requests 401s; the replays succeed.
      return fetchMock.mock.calls.filter((c) => !c[0].endsWith('/api/auth/refresh')).length <= 2
        ? respond(401, { title: 'Expired' })
        : respond(200, []);
    });

    const both = Promise.all([TodoApi.list(), TodoApi.list()]);
    await Promise.resolve();
    releaseRefresh();
    await both;

    // Two POSTs of the same rotating refresh token would look like reuse to the backend
    // and revoke every session.
    const refreshCalls = fetchMock.mock.calls.filter((c) => c[0].endsWith('/api/auth/refresh'));
    expect(refreshCalls).toHaveLength(1);
  });

  it('allows a fresh refresh after the in-flight one settles', async () => {
    const { AuthApi } = await loadClient();
    fetchMock.mockResolvedValue(respond(200, { accessToken: 'fresh' }));

    await expect(AuthApi.refresh()).resolves.toBe(true);
    await expect(AuthApi.refresh()).resolves.toBe(true);

    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('reports a failed refresh as not signed in', async () => {
    const { AuthApi } = await loadClient();
    fetchMock.mockResolvedValue(respond(401, { title: 'No cookie' }));

    await expect(AuthApi.refresh()).resolves.toBe(false);
  });
});

describe('cold-start resilience', () => {
  // Azure's Free tier unloads the app after idle, so the first request after a quiet spell
  // can take a minute. These paths are the difference between that and "Failed to fetch".
  beforeEach(() => {
    vi.useFakeTimers();
  });

  /** Runs `promise` to completion, flushing the backoff sleeps as they are scheduled. */
  async function withBackoffFlushed(promise) {
    const settled = promise.then(
      (value) => ({ value }),
      (error) => ({ error }),
    );

    let done = false;
    settled.then(() => { done = true; });

    // Each iteration lets pending microtasks run, then fires whatever sleep they queued.
    for (let i = 0; i < 40 && !done; i++) {
      await vi.advanceTimersByTimeAsync(20000);
    }

    const outcome = await settled;
    if (outcome.error) throw outcome.error;
    return outcome.value;
  }

  it.each([502, 503, 504])('retries a %i while the instance is starting up', async (status) => {
    const { TodoApi, setOnServerWaking } = await loadClient();
    const onWaking = vi.fn();
    setOnServerWaking(onWaking);

    fetchMock
      .mockResolvedValueOnce(respond(status))
      .mockResolvedValueOnce(respond(200, []));

    await expect(withBackoffFlushed(TodoApi.list())).resolves.toEqual([]);

    expect(fetchMock).toHaveBeenCalledTimes(2);
    // The UI is told once that it is waiting, and once that the wait is over.
    expect(onWaking.mock.calls).toEqual([[true], [false]]);
  });

  it('retries a network error until the server answers', async () => {
    const { TodoApi, setOnServerWaking } = await loadClient();
    const onWaking = vi.fn();
    setOnServerWaking(onWaking);

    fetchMock
      .mockRejectedValueOnce(new TypeError('Failed to fetch'))
      .mockRejectedValueOnce(new TypeError('Failed to fetch'))
      .mockResolvedValueOnce(respond(200, [{ id: 1 }]));

    await expect(withBackoffFlushed(TodoApi.list())).resolves.toEqual([{ id: 1 }]);

    expect(onWaking.mock.calls).toEqual([[true], [false]]);
  });

  it('gives up on a network error once the retry budget is spent', async () => {
    const { TodoApi, setOnServerWaking } = await loadClient();
    const onWaking = vi.fn();
    setOnServerWaking(onWaking);

    fetchMock.mockRejectedValue(new TypeError('Failed to fetch'));

    await expect(withBackoffFlushed(TodoApi.list())).rejects.toThrow('Failed to fetch');

    // The first attempt plus WAKE_MAX_RETRIES more.
    expect(fetchMock).toHaveBeenCalledTimes(7);
    expect(onWaking).toHaveBeenLastCalledWith(false);
  });

  it('surfaces a persistent 503 as an error rather than retrying forever', async () => {
    const { TodoApi } = await loadClient();
    fetchMock.mockResolvedValue(respond(503, { title: 'Service Unavailable' }));

    await expect(withBackoffFlushed(TodoApi.list())).rejects.toThrow('Service Unavailable');

    expect(fetchMock).toHaveBeenCalledTimes(7);
  });

  it('does not signal the UI when the very first attempt succeeds', async () => {
    const { TodoApi, setOnServerWaking } = await loadClient();
    const onWaking = vi.fn();
    setOnServerWaking(onWaking);

    fetchMock.mockResolvedValue(respond(200, []));

    await withBackoffFlushed(TodoApi.list());

    expect(onWaking).not.toHaveBeenCalled();
  });

  it('works with no waking handler registered', async () => {
    const { TodoApi } = await loadClient();
    fetchMock
      .mockResolvedValueOnce(respond(503))
      .mockResolvedValueOnce(respond(200, []));

    await expect(withBackoffFlushed(TodoApi.list())).resolves.toEqual([]);
  });
});

describe('AuthApi', () => {
  it.each([
    ['register', ['a@b.com', 'pw'], '/api/auth/register', { email: 'a@b.com', password: 'pw' }],
    ['login', ['a@b.com', 'pw'], '/api/auth/login', { email: 'a@b.com', password: 'pw' }],
    ['google', ['id-token'], '/api/auth/google', { idToken: 'id-token' }],
  ])('%s posts to %s and adopts the returned session', async (method, args, path, body) => {
    const { AuthApi, TodoApi } = await loadClient();
    fetchMock.mockResolvedValue(respond(200, { accessToken: 'new-token', user: { id: 1 } }));

    const auth = await AuthApi[method](...args);

    expect(auth.user).toEqual({ id: 1 });
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe(path);
    expect(JSON.parse(init.body)).toEqual(body);
    // credentials: 'include' so the browser stores the refresh cookie the server sets.
    expect(init.credentials).toBe('include');
    // No Authorization header on an anonymous endpoint.
    expect(init.headers.Authorization).toBeUndefined();

    fetchMock.mockClear();
    fetchMock.mockResolvedValue(respond(200, []));
    await TodoApi.list();
    expect(fetchMock.mock.calls[0][1].headers.Authorization).toBe('Bearer new-token');
  });

  it('surfaces a rejected sign-in', async () => {
    const { AuthApi } = await loadClient();
    fetchMock.mockResolvedValue(respond(401, { title: 'Invalid email or password.' }));

    await expect(AuthApi.login('a@b.com', 'wrong')).rejects.toThrow('Invalid email or password.');
  });

  it('me() reads the current profile', async () => {
    const { AuthApi } = await loadClient();
    fetchMock.mockResolvedValue(respond(200, { id: 1, email: 'a@b.com' }));

    await expect(AuthApi.me()).resolves.toEqual({ id: 1, email: 'a@b.com' });
    expect(fetchMock.mock.calls[0][0]).toBe('/api/auth/me');
  });

  it.each(['logout', 'revokeAll'])('%s clears the local session even when the call fails', async (method) => {
    const { AuthApi, TodoApi, setSession } = await loadClient();
    setSession({ accessToken: 'token-abc' });
    fetchMock.mockResolvedValue(respond(500, { title: 'Boom' }));

    await expect(AuthApi[method]()).rejects.toThrow('Boom');

    // The user asked to be signed out; a server error must not leave the token in memory.
    fetchMock.mockClear();
    fetchMock.mockResolvedValue(respond(200, []));
    await TodoApi.list();
    expect(fetchMock.mock.calls[0][1].headers.Authorization).toBeUndefined();
  });

  it.each([
    ['logout', '/api/auth/logout'],
    ['revokeAll', '/api/auth/revoke-all'],
  ])('%s posts to %s', async (method, path) => {
    const { AuthApi } = await loadClient();
    fetchMock.mockResolvedValue(respond(204));

    await AuthApi[method]();

    expect(fetchMock.mock.calls[0][0]).toBe(path);
    expect(fetchMock.mock.calls[0][1].method).toBe('POST');
  });
});

describe('CategoryApi', () => {
  it.each([
    ['list', [], 'GET', '/api/categories', undefined],
    ['create', [{ name: 'Work' }], 'POST', '/api/categories', { name: 'Work' }],
    ['update', [3, { name: 'Study' }], 'PUT', '/api/categories/3', { name: 'Study' }],
    ['remove', [3], 'DELETE', '/api/categories/3', undefined],
  ])('%s issues %s %s', async (method, args, verb, path, body) => {
    const { CategoryApi } = await loadClient();
    fetchMock.mockResolvedValue(respond(204));

    await CategoryApi[method](...args);

    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe(path);
    expect(init.method ?? 'GET').toBe(verb);
    expect(init.body ? JSON.parse(init.body) : undefined).toEqual(body);
  });
});

describe('TodoApi', () => {
  it.each([
    ['no filter or search', [], '/api/todos'],
    ['the default All filter dropped', ['All'], '/api/todos'],
    ['a filter', ['Active'], '/api/todos?filter=Active'],
    ['a search term', ['All', 'milk'], '/api/todos?search=milk'],
    ['both', ['Completed', 'milk'], '/api/todos?filter=Completed&search=milk'],
    ['an empty filter', ['', 'milk'], '/api/todos?search=milk'],
  ])('list() with %s requests %s', async (_label, args, expected) => {
    const { TodoApi } = await loadClient();
    fetchMock.mockResolvedValue(respond(200, []));

    await TodoApi.list(...args);

    expect(fetchMock.mock.calls[0][0]).toBe(expected);
  });

  it.each([
    ['create', [{ title: 'A' }], 'POST', '/api/todos', { title: 'A' }],
    ['update', [5, { title: 'B' }], 'PUT', '/api/todos/5', { title: 'B' }],
    ['remove', [5], 'DELETE', '/api/todos/5', undefined],
  ])('%s issues %s %s', async (method, args, verb, path, body) => {
    const { TodoApi } = await loadClient();
    fetchMock.mockResolvedValue(respond(204));

    await TodoApi[method](...args);

    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe(path);
    expect(init.method).toBe(verb);
    expect(init.body ? JSON.parse(init.body) : undefined).toEqual(body);
  });

  it('changeStatus sends the concurrency token so a stale move is rejected', async () => {
    const { TodoApi } = await loadClient();
    fetchMock.mockResolvedValue(respond(200, { id: 5 }));

    await TodoApi.changeStatus(5, 2, 'token-xyz');

    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe('/api/todos/5/status');
    expect(init.method).toBe('PATCH');
    expect(JSON.parse(init.body)).toEqual({ status: 2, concurrencyToken: 'token-xyz' });
  });
});
