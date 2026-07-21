import { useState } from 'react';
import GoogleButton from './GoogleButton.jsx';
import ThemeToggle from './ThemeToggle.jsx';

export default function AuthForm({ onLogin, onRegister, onGoogle, waking }) {
  const [mode, setMode] = useState('login'); // 'login' | 'register'
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [showPassword, setShowPassword] = useState(false);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  const isRegister = mode === 'register';

  async function handleGoogle(idToken) {
    setError('');
    setBusy(true);
    try {
      await onGoogle(idToken);
    } catch (err) {
      setError(err.message);
    } finally {
      setBusy(false);
    }
  }

  async function handleSubmit(e) {
    e.preventDefault();
    setError('');
    setBusy(true);
    try {
      if (isRegister) {
        await onRegister(email.trim(), password);
      } else {
        await onLogin(email.trim(), password);
      }
    } catch (err) {
      // Surface field-level validation messages when present.
      const problem = err.problem;
      if (problem?.errors) {
        const first = Object.values(problem.errors)[0];
        setError(Array.isArray(first) ? first[0] : String(first));
      } else {
        setError(err.message);
      }
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="auth">
      <div className="card auth__card">
        <div className="auth__theme"><ThemeToggle /></div>
        <h1 className="auth__title">{isRegister ? 'Create account' : 'Sign in'}</h1>
        <p className="auth__subtitle">
          {isRegister ? 'Register to start your list.' : 'Welcome back.'}
        </p>

        {waking && (
          <div className="banner" style={{ background: 'rgba(37, 99, 235, 0.12)', color: 'var(--primary)' }}>
            Waking the server up… the first request after it's been idle can take up to a minute.
          </div>
        )}

        <form onSubmit={handleSubmit} className="auth__form">
          <label className="auth__label">
            Email
            <input
              type="email"
              autoComplete="email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              required
            />
          </label>

          <label className="auth__label">
            Password
            <div className="pw-field">
              <input
                type={showPassword ? 'text' : 'password'}
                autoComplete={isRegister ? 'new-password' : 'current-password'}
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                required
              />
              <button
                type="button"
                className="pw-toggle"
                onClick={() => setShowPassword((v) => !v)}
                aria-label={showPassword ? 'Hide password' : 'Show password'}
                aria-pressed={showPassword}
                title={showPassword ? 'Hide password' : 'Show password'}
              >
                <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
                  <path d="M2 12s3.8-6.5 10-6.5S22 12 22 12s-3.8 6.5-10 6.5S2 12 2 12Z" />
                  <circle cx="12" cy="12" r="2.3" fill="currentColor" stroke="none" />
                  {showPassword && <line x1="4.5" y1="4.5" x2="19.5" y2="19.5" />}
                </svg>
              </button>
            </div>
          </label>

          {isRegister && (
            <p className="auth__hint">At least 8 characters, including a letter and a number.</p>
          )}

          {error && <div className="banner banner--error">{error}</div>}

          <button className="btn btn--primary auth__submit" type="submit" disabled={busy}>
            {busy ? 'Please wait…' : isRegister ? 'Create account' : 'Sign in'}
          </button>
        </form>

        <div className="auth__divider"><span>or</span></div>

        <div className="auth__providers">
          <GoogleButton onCredential={handleGoogle} />
        </div>

        <p className="auth__switch">
          {isRegister ? 'Already have an account?' : "Don't have an account?"}{' '}
          <button
            type="button"
            className="auth__link"
            onClick={() => {
              setError('');
              setMode(isRegister ? 'login' : 'register');
            }}
          >
            {isRegister ? 'Sign in' : 'Create one'}
          </button>
        </p>

        <p className="auth__demo">
          Demo login: <code>demo@todoapp.local</code> / <code>Password123!</code>
        </p>
      </div>
    </div>
  );
}
