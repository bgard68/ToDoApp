import { describe, it, expect, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import TodoForm from './TodoForm.jsx';

describe('<TodoForm />', () => {
  it('blocks an empty submit and shows a required-title error', async () => {
    const onCreate = vi.fn();
    render(<TodoForm onCreate={onCreate} categories={[]} />);
    await userEvent.click(screen.getByRole('button', { name: /^add$/i }));
    expect(onCreate).not.toHaveBeenCalled();
    expect(screen.getByText(/title is required/i)).toBeInTheDocument();
  });

  it('submits a trimmed title to onCreate', async () => {
    const onCreate = vi.fn().mockResolvedValue(undefined);
    render(<TodoForm onCreate={onCreate} categories={[]} />);
    await userEvent.type(screen.getByLabelText(/^title$/i), '  Buy milk  ');
    await userEvent.click(screen.getByRole('button', { name: /^add$/i }));
    expect(onCreate).toHaveBeenCalledTimes(1);
    expect(onCreate.mock.calls[0][0]).toMatchObject({ title: 'Buy milk' });
  });
});
