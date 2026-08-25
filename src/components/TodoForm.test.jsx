import { describe, it, expect, vi } from 'vitest';
import { render, screen, act } from '@testing-library/react';
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

  it('sends every field and then clears the form', async () => {
    const onCreate = vi.fn().mockResolvedValue(undefined);
    render(<TodoForm onCreate={onCreate} categories={[{ id: 1, name: 'Work' }]} />);

    await userEvent.type(screen.getByLabelText('Title'), 'Buy milk');
    await userEvent.type(screen.getByLabelText('Description'), '  two litres  ');
    await userEvent.selectOptions(screen.getByLabelText('Category'), '1');
    await userEvent.selectOptions(screen.getByLabelText('Priority'), '2');
    await userEvent.type(screen.getByLabelText('Due date'), '07192026');
    await userEvent.click(screen.getByRole('button', { name: /^add$/i }));

    expect(onCreate).toHaveBeenCalledWith({
      title: 'Buy milk',
      description: 'two litres',
      priority: 2,
      categoryId: 1,
      dueDate: new Date('2026-07-19').toISOString(),
    });
    expect(screen.getByLabelText('Title')).toHaveValue('');
  });

  it('sends nulls for the optional fields left blank', async () => {
    const onCreate = vi.fn().mockResolvedValue(undefined);
    render(<TodoForm onCreate={onCreate} categories={[]} />);

    await userEvent.type(screen.getByLabelText('Title'), 'Bare task');
    await userEvent.click(screen.getByRole('button', { name: /^add$/i }));

    expect(onCreate).toHaveBeenCalledWith(expect.objectContaining({
      description: null,
      categoryId: null,
      dueDate: null,
    }));
  });

  it('shows the failure and keeps what was typed', async () => {
    const onCreate = vi.fn().mockRejectedValue(new Error('Title must be under 200 characters.'));
    render(<TodoForm onCreate={onCreate} categories={[]} />);

    await userEvent.type(screen.getByLabelText('Title'), 'Too long');
    await userEvent.click(screen.getByRole('button', { name: /^add$/i }));

    expect(await screen.findByText('Title must be under 200 characters.')).toBeInTheDocument();
    expect(screen.getByLabelText('Title')).toHaveValue('Too long');
  });

  it('disables the button while the create is in flight', async () => {
    let finish;
    const onCreate = vi.fn(() => new Promise((resolve) => { finish = resolve; }));
    render(<TodoForm onCreate={onCreate} categories={[]} />);

    await userEvent.type(screen.getByLabelText('Title'), 'Slow one');
    await userEvent.click(screen.getByRole('button', { name: /^add$/i }));

    expect(screen.getByRole('button', { name: /adding/i })).toBeDisabled();

    await act(async () => { finish(); });

    expect(screen.getByRole('button', { name: /^add$/i })).toBeEnabled();
  });
});
