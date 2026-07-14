import { useEffect, useState } from 'react';
import { AuthApi, getRefreshToken, setOnUnauthorized } from './api.js';
import AuthForm from './components/AuthForm.jsx';
import KanbanBoard from './components/KanbanBoard.jsx';

export default function App() {
  const [user, setUser] = useState(null);
  const [initializing, setInitializing] = useState(true);

  useEffect(() => {
    // If a session (access token / stamp) is revoked mid-use, drop back to sign-in.
    setOnUnauthorized(() => setUser(null));

    (async () => {
      if (getRefreshToken()) {
        const ok = await AuthApi.refresh();
        if (ok) {
          try {
            setUser(await AuthApi.me());
          } catch {
            setUser(null);
          }
        }
      }
      setInitializing(false);
    })();
  }, []);

  async function handleLogin(email, password) {
    const auth = await AuthApi.login(email, password);
    setUser(auth.user);
  }

  async function handleRegister(email, password) {
    const auth = await AuthApi.register(email, password);
    setUser(auth.user);
  }

  async function handleGoogle(idToken) {
    const auth = await AuthApi.google(idToken);
    setUser(auth.user);
  }

  async function handleLogout() {
    await AuthApi.logout();
    setUser(null);
  }

  async function handleRevokeAll() {
    await AuthApi.revokeAll();
    setUser(null);
  }

  if (initializing) {
    return (
      <div className="app">
        <p className="empty">Loading…</p>
      </div>
    );
  }

  if (!user) {
    return (
      <AuthForm
        onLogin={handleLogin}
        onRegister={handleRegister}
        onGoogle={handleGoogle}
      />
    );
  }

  return (
    <div className="app app--wide">
      <header className="app__header app__header--row">
        <div>
          <h1>Board</h1>
          <p className="app__subtitle">Signed in as {user.email}</p>
        </div>
        <div className="app__account">
          <button className="btn btn--ghost" onClick={handleRevokeAll} title="Revoke all sessions everywhere">
            Sign out everywhere
          </button>
          <button className="btn" onClick={handleLogout}>
            Sign out
          </button>
        </div>
      </header>

      <KanbanBoard />
    </div>
  );
}
