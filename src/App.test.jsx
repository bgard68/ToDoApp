import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, act } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

vi.mock('./lib/apiClient.js', () => ({
  AuthApi: {
    refresh: vi.fn(),
    me: vi.fn(),
    login: vi.fn(),
    register: vi.fn(),
    google: vi.fn(),
    logout: vi.fn(),
    revokeAll: vi.fn(),
  },
  TodoApi: { list: vi.fn(), create: vi.fn(), update: vi.fn(), changeStatus: vi.fn(), remove: vi.fn() },
  CategoryApi: { list: vi.fn(), create: vi.fn(), update: vi.fn(), remove: vi.fn() },
  hasSession: vi.fn(),
  setOnUnauthorized: vi.fn(),
  setOnServerWaking: vi.fn(),
}));

import {
  AuthApi, TodoApi, CategoryApi, hasSession, setOnUnauthorized, setOnServerWaking,
} from './lib/apiClient.js';
// The real widget needs Google Identity Services; it has its own tests. Here it stands in as a
// plain button that hands back a credential.
vi.mock('./components/GoogleButton.jsx', () => ({
  default: ({ onCredential }) => (
    <button type="button" onClick={() => onCredential('google-id-token')}>
      Continue with Google
    </button>
  ),
}));

import App from './App.jsx';

const user = { id: 1, email: 'me@example.com', role: 'User' };

beforeEach(() => {
  vi.clearAllMocks();
  hasSession.mockReturnValue(true);
  AuthApi.refresh.mockResolvedValue(false);
  TodoApi.list.mockResolvedValue([]);
  CategoryApi.list.mockResolvedValue([]);
});

/** Waits out the silent sign-in attempt that runs on mount. */
async function settle() {
  await waitFor(() => expect(screen.queryByText('Loading…')).not.toBeInTheDocument());
}

describe('App startup', () => {
  it('shows a loading note while the silent sign-in runs', async () => {
    let release;
    AuthApi.refresh.mockReturnValue(new Promise((resolve) => { release = resolve; }));
    render(<App />);

    expect(screen.getByText('Loading…')).toBeInTheDocument();

    await act(async () => { release(false); });
    await settle();
  });

  it('says it is waking the server when a cold start is being waited out', async () => {
    let signalWaking;
    setOnServerWaking.mockImplementation((fn) => { signalWaking = fn; });
    let release;
    AuthApi.refresh.mockReturnValue(new Promise((resolve) => { release = resolve; }));
    render(<App />);

    await act(async () => { signalWaking(true); });

    expect(screen.getByText(/waking the server up/i)).toBeInTheDocument();

    await act(async () => { release(false); });
    await settle();
  });

  it('restores the session when the refresh cookie is still good', async () => {
    AuthApi.refresh.mockResolvedValue(true);
    AuthApi.me.mockResolvedValue(user);
    render(<App />);

    await waitFor(() => expect(screen.getByText('Signed in as me@example.com')).toBeInTheDocument());
  });

  it('falls back to the sign-in form when there is no usable cookie', async () => {
    AuthApi.refresh.mockResolvedValue(false);
    render(<App />);

    await settle();
    expect(AuthApi.me).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: /sign in/i })).toBeInTheDocument();
  });

  it('falls back to the sign-in form when the profile call fails', async () => {
    AuthApi.refresh.mockResolvedValue(true);
    AuthApi.me.mockRejectedValue(new Error('revoked'));
    render(<App />);

    await settle();
    expect(screen.getByRole('button', { name: /sign in/i })).toBeInTheDocument();
  });

  it('skips the refresh entirely when there is no session to restore', async () => {
    hasSession.mockReturnValue(false);
    render(<App />);

    await settle();
    expect(AuthApi.refresh).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: /sign in/i })).toBeInTheDocument();
  });
});

describe('App authentication', () => {
  it('signs in with a password', async () => {
    const ui = userEvent.setup();
    AuthApi.login.mockResolvedValue({ user });
    render(<App />);
    await settle();

    await ui.type(screen.getByLabelText(/email/i), 'me@example.com');
    await ui.type(screen.getByLabelText(/password/i, { selector: 'input' }), 'Password1');
    await ui.click(screen.getByRole('button', { name: /sign in/i }));

    await waitFor(() => expect(screen.getByText('Signed in as me@example.com')).toBeInTheDocument());
    expect(AuthApi.login).toHaveBeenCalledWith('me@example.com', 'Password1');
  });

  it('registers a new account', async () => {
    const ui = userEvent.setup();
    AuthApi.register.mockResolvedValue({ user });
    render(<App />);
    await settle();

    await ui.click(screen.getByRole('button', { name: /create one/i }));
    await ui.type(screen.getByLabelText(/email/i), 'me@example.com');
    await ui.type(screen.getByLabelText(/password/i, { selector: 'input' }), 'Password1');
    await ui.click(screen.getByRole('button', { name: /create account/i }));

    await waitFor(() => expect(screen.getByText('Signed in as me@example.com')).toBeInTheDocument());
    expect(AuthApi.register).toHaveBeenCalledWith('me@example.com', 'Password1');
  });

  it('drops back to sign-in when the session is revoked mid-use', async () => {
    let signalUnauthorized;
    setOnUnauthorized.mockImplementation((fn) => { signalUnauthorized = fn; });
    AuthApi.refresh.mockResolvedValue(true);
    AuthApi.me.mockResolvedValue(user);
    render(<App />);
    await waitFor(() => expect(screen.getByText('Signed in as me@example.com')).toBeInTheDocument());

    await act(async () => { signalUnauthorized(); });

    expect(screen.getByRole('button', { name: /sign in/i })).toBeInTheDocument();
  });
});

describe('App signed in', () => {
  beforeEach(() => {
    AuthApi.refresh.mockResolvedValue(true);
    AuthApi.me.mockResolvedValue(user);
  });

  async function renderSignedIn() {
    render(<App />);
    await waitFor(() => expect(screen.getByText('Signed in as me@example.com')).toBeInTheDocument());
  }

  it('shows the board', async () => {
    await renderSignedIn();

    expect(screen.getByRole('heading', { name: 'Board' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'To Do' })).toBeInTheDocument();
  });

  it('signs out', async () => {
    const ui = userEvent.setup();
    AuthApi.logout.mockResolvedValue(undefined);
    await renderSignedIn();

    await ui.click(screen.getByRole('button', { name: 'Sign out' }));

    await waitFor(() => expect(screen.getByRole('button', { name: /sign in/i })).toBeInTheDocument());
    expect(AuthApi.logout).toHaveBeenCalled();
  });

  it('signs out everywhere', async () => {
    const ui = userEvent.setup();
    AuthApi.revokeAll.mockResolvedValue(undefined);
    await renderSignedIn();

    await ui.click(screen.getByRole('button', { name: 'Sign out everywhere' }));

    await waitFor(() => expect(screen.getByRole('button', { name: /sign in/i })).toBeInTheDocument());
    expect(AuthApi.revokeAll).toHaveBeenCalled();
  });
});

describe('App Google sign-in', () => {
  it('signs in with a Google credential', async () => {
    const ui = userEvent.setup();
    AuthApi.google.mockResolvedValue({ user });
    render(<App />);
    await settle();

    await ui.click(screen.getByRole('button', { name: /continue with google/i }));

    await waitFor(() => expect(screen.getByText('Signed in as me@example.com')).toBeInTheDocument());
    expect(AuthApi.google).toHaveBeenCalledWith('google-id-token');
  });
});
