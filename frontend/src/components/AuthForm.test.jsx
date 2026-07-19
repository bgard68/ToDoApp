import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
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
});
