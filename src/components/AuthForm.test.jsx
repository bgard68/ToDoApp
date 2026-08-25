import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

// The real widget needs Google Identity Services and a configured client id; it has its own
// tests. Here it stands in as a plain button that hands back a credential.
vi.mock('./GoogleButton.jsx', () => ({
  default: ({ onCredential }) => (
    <button type="button" onClick={() => onCredential('google-id-token')}>
      Continue with Google
    </button>
  ),
}));

import AuthForm from './AuthForm.jsx';

describe('<AuthForm />', () => {
  it('calls onLogin with the entered credentials', async () => {
    const onLogin = vi.fn().mockResolvedValue(undefined);
    render(<AuthForm onLogin={onLogin} onRegister={vi.fn()} onGoogle={vi.fn()} />);
    await userEvent.type(screen.getByLabelText(/email/i), 'demo@todoapp.local');
    await userEvent.type(screen.getByLabelText(/^password$/i), 'Password123!');
    await userEvent.click(screen.getByRole('button', { name: /^sign in$/i }));
    expect(onLogin).toHaveBeenCalledWith('demo@todoapp.local', 'Password123!');
  });

  it('switches to register mode', async () => {
    render(<AuthForm onLogin={vi.fn()} onRegister={vi.fn()} onGoogle={vi.fn()} />);
    await userEvent.click(screen.getByRole('button', { name: /create one/i }));
    expect(screen.getByRole('heading', { name: /create account/i })).toBeInTheDocument();
  });

  it('toggles password visibility with the show/hide control', async () => {
    render(<AuthForm onLogin={vi.fn()} onRegister={vi.fn()} onGoogle={vi.fn()} />);
    const pw = screen.getByLabelText(/^password$/i);
    expect(pw).toHaveAttribute('type', 'password');

    // masked by default → button reads "Show password"
    await userEvent.click(screen.getByRole('button', { name: /show password/i }));
    expect(pw).toHaveAttribute('type', 'text');

    // now revealed → button reads "Hide password"; clicking masks it again
    await userEvent.click(screen.getByRole('button', { name: /hide password/i }));
    expect(pw).toHaveAttribute('type', 'password');
  });

  it('registers with the entered credentials', async () => {
    const onRegister = vi.fn().mockResolvedValue(undefined);
    render(<AuthForm onLogin={vi.fn()} onRegister={onRegister} onGoogle={vi.fn()} />);

    await userEvent.click(screen.getByRole('button', { name: /create one/i }));
    await userEvent.type(screen.getByLabelText(/email/i), '  demo@todoapp.local  ');
    await userEvent.type(screen.getByLabelText(/^password$/i), 'Password123!');
    await userEvent.click(screen.getByRole('button', { name: /^create account$/i }));

    expect(onRegister).toHaveBeenCalledWith('demo@todoapp.local', 'Password123!');
    expect(screen.getByText(/at least 8 characters/i)).toBeInTheDocument();
  });

  it('switches back to sign-in and clears the error', async () => {
    const onLogin = vi.fn().mockRejectedValue(new Error('Invalid email or password.'));
    render(<AuthForm onLogin={onLogin} onRegister={vi.fn()} onGoogle={vi.fn()} />);
    await userEvent.type(screen.getByLabelText(/email/i), 'demo@todoapp.local');
    await userEvent.type(screen.getByLabelText(/^password$/i), 'wrong');
    await userEvent.click(screen.getByRole('button', { name: /^sign in$/i }));
    expect(await screen.findByText('Invalid email or password.')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: /create one/i }));

    expect(screen.queryByText('Invalid email or password.')).not.toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: /^sign in$/i, selector: '.auth__link' }));
    expect(screen.getByRole('heading', { name: 'Sign in' })).toBeInTheDocument();
  });

  it('shows the message from a failed sign-in', async () => {
    const onLogin = vi.fn().mockRejectedValue(new Error('This account has been disabled.'));
    render(<AuthForm onLogin={onLogin} onRegister={vi.fn()} onGoogle={vi.fn()} />);

    await userEvent.type(screen.getByLabelText(/email/i), 'demo@todoapp.local');
    await userEvent.type(screen.getByLabelText(/^password$/i), 'Password123!');
    await userEvent.click(screen.getByRole('button', { name: /^sign in$/i }));

    expect(await screen.findByText('This account has been disabled.')).toBeInTheDocument();
  });

  it('prefers a field-level validation message over the generic one', async () => {
    const err = new Error('One or more validation errors occurred.');
    err.problem = { errors: { Password: ['Password must contain a letter and a number.'] } };
    const onRegister = vi.fn().mockRejectedValue(err);
    render(<AuthForm onLogin={vi.fn()} onRegister={onRegister} onGoogle={vi.fn()} />);

    await userEvent.click(screen.getByRole('button', { name: /create one/i }));
    await userEvent.type(screen.getByLabelText(/email/i), 'demo@todoapp.local');
    await userEvent.type(screen.getByLabelText(/^password$/i), 'password');
    await userEvent.click(screen.getByRole('button', { name: /^create account$/i }));

    expect(await screen.findByText('Password must contain a letter and a number.'))
      .toBeInTheDocument();
  });

  it('copes with a validation payload that is not an array', async () => {
    const err = new Error('One or more validation errors occurred.');
    err.problem = { errors: { Email: 'Email is required.' } };
    const onLogin = vi.fn().mockRejectedValue(err);
    render(<AuthForm onLogin={onLogin} onRegister={vi.fn()} onGoogle={vi.fn()} />);

    await userEvent.type(screen.getByLabelText(/email/i), 'demo@todoapp.local');
    await userEvent.type(screen.getByLabelText(/^password$/i), 'Password123!');
    await userEvent.click(screen.getByRole('button', { name: /^sign in$/i }));

    expect(await screen.findByText('Email is required.')).toBeInTheDocument();
  });

  it('signs in with a Google credential', async () => {
    const onGoogle = vi.fn().mockResolvedValue(undefined);
    render(<AuthForm onLogin={vi.fn()} onRegister={vi.fn()} onGoogle={onGoogle} />);

    await userEvent.click(screen.getByRole('button', { name: /continue with google/i }));

    expect(onGoogle).toHaveBeenCalledWith('google-id-token');
  });

  it('shows the message from a failed Google sign-in', async () => {
    const onGoogle = vi.fn().mockRejectedValue(new Error('Your Google email is not verified.'));
    render(<AuthForm onLogin={vi.fn()} onRegister={vi.fn()} onGoogle={onGoogle} />);

    await userEvent.click(screen.getByRole('button', { name: /continue with google/i }));

    expect(await screen.findByText('Your Google email is not verified.')).toBeInTheDocument();
  });

  it('shows the cold-start note when the server is waking', () => {
    render(<AuthForm onLogin={vi.fn()} onRegister={vi.fn()} onGoogle={vi.fn()} waking />);

    expect(screen.getByText(/waking the server up/i)).toBeInTheDocument();
  });

  it('hides the cold-start note otherwise', () => {
    render(<AuthForm onLogin={vi.fn()} onRegister={vi.fn()} onGoogle={vi.fn()} />);

    expect(screen.queryByText(/waking the server up/i)).not.toBeInTheDocument();
  });
});
