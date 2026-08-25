import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, within, act, fireEvent } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import TaskCard from './TaskCard.jsx';

const categories = [
  { id: 1, name: 'Work', color: '#7fb2e6' },
  { id: 2, name: 'Personal', color: null },
];

const baseTodo = {
  id: 10,
  title: 'Write the thing',
  description: '',
  status: 0,
  priority: 1,
  priorityName: 'Medium',
  categoryId: 1,
  dueDate: null,
  isCompleted: false,
  concurrencyToken: 'token-1',
};

function renderCard(todo = {}, props = {}) {
  const handlers = {
    onUpdate: vi.fn().mockResolvedValue(undefined),
    onDelete: vi.fn(),
    onMove: vi.fn(),
    onDragStart: vi.fn(),
    onDragEnd: vi.fn(),
    ...props,
  };

  render(<TaskCard todo={{ ...baseTodo, ...todo }} categories={categories} {...handlers} />);
  return handlers;
}

describe('TaskCard display', () => {
  it('shows the title and its category', () => {
    renderCard();

    expect(screen.getByText('Write the thing')).toBeInTheDocument();
    expect(screen.getByText('Work')).toBeInTheDocument();
  });

  it('falls back to "Uncategorized" when the category is missing', () => {
    renderCard({ categoryId: null });

    expect(screen.getByText('Uncategorized')).toBeInTheDocument();
  });

  it('falls back to "Uncategorized" when the category was deleted', () => {
    renderCard({ categoryId: 999 });

    expect(screen.getByText('Uncategorized')).toBeInTheDocument();
  });

  it('hides the notes line when there are none', () => {
    renderCard({ description: '' });

    expect(screen.queryByText(/notes go here/i)).not.toBeInTheDocument();
  });

  it('shows the notes when there are some', () => {
    renderCard({ description: 'notes go here' });

    expect(screen.getByText('notes go here')).toBeInTheDocument();
  });

  it('marks a completed task with a check', () => {
    renderCard({ isCompleted: true, status: 2 });

    expect(screen.getByTitle('Done')).toBeInTheDocument();
  });

  it('leaves an open task unchecked', () => {
    renderCard();

    expect(screen.queryByTitle('Done')).not.toBeInTheDocument();
  });

  it('shows no due date when the task has none', () => {
    renderCard({ dueDate: null });

    expect(screen.queryByText(/overdue/)).not.toBeInTheDocument();
  });
});

describe('TaskCard due dates', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-06-15T12:00:00Z'));
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('flags a past due date as overdue', () => {
    renderCard({ dueDate: '2026-06-01T00:00:00Z' });

    expect(screen.getByText(/overdue/)).toBeInTheDocument();
  });

  it('does not flag a future due date', () => {
    renderCard({ dueDate: '2026-07-01T00:00:00Z' });

    expect(screen.queryByText(/overdue/)).not.toBeInTheDocument();
  });

  it('does not call a completed task overdue', () => {
    renderCard({ dueDate: '2026-06-01T00:00:00Z', isCompleted: true });

    expect(screen.queryByText(/overdue/)).not.toBeInTheDocument();
  });
});

describe('TaskCard actions', () => {
  it('deletes on the delete control', async () => {
    const user = userEvent.setup();
    const { onDelete } = renderCard();

    await user.click(screen.getByLabelText('Delete'));

    expect(onDelete).toHaveBeenCalledWith(10);
  });

  it('reports the drag so the board can highlight the lanes', () => {
    const { onDragStart } = renderCard();
    const note = screen.getByText('Write the thing').closest('.note');

    // jsdom has no drag support, so the handler is invoked the way React would.
    const dataTransfer = { setData: vi.fn(), effectAllowed: '' };
    fireEvent.dragStart(note, { dataTransfer });

    expect(onDragStart).toHaveBeenCalled();
  });

  it('reports the end of a drag', () => {
    const { onDragEnd } = renderCard();
    const note = screen.getByText('Write the thing').closest('.note');

    fireEvent.dragEnd(note);

    expect(onDragEnd).toHaveBeenCalled();
  });
});

describe('TaskCard tap-to-move', () => {
  // Native HTML5 drag events are mouse-only, so touch devices need this control.
  it('offers the other lanes and not the current one', async () => {
    const user = userEvent.setup();
    renderCard({ status: 0 });

    await user.click(screen.getByLabelText('Move to another lane'));

    const group = screen.getByRole('group', { name: 'Move this task to' });
    expect(within(group).getByRole('button', { name: '→ In Progress' })).toBeInTheDocument();
    expect(within(group).getByRole('button', { name: '→ Done' })).toBeInTheDocument();
    expect(within(group).queryByRole('button', { name: '→ To Do' })).not.toBeInTheDocument();
  });

  it('moves the task and closes the control', async () => {
    const user = userEvent.setup();
    const { onMove } = renderCard({ status: 0 });

    await user.click(screen.getByLabelText('Move to another lane'));
    await user.click(screen.getByRole('button', { name: '→ Done' }));

    expect(onMove).toHaveBeenCalledWith(10, 2);
    expect(screen.queryByRole('group', { name: 'Move this task to' })).not.toBeInTheDocument();
  });

  it('toggles closed again', async () => {
    const user = userEvent.setup();
    renderCard();

    await user.click(screen.getByLabelText('Move to another lane'));
    await user.click(screen.getByLabelText('Move to another lane'));

    expect(screen.queryByRole('group', { name: 'Move this task to' })).not.toBeInTheDocument();
  });
});

describe('TaskCard editing', () => {
  it('saves the edited fields with the concurrency token the card was rendered with', async () => {
    const user = userEvent.setup();
    const { onUpdate } = renderCard({ description: 'old notes' });

    await user.click(screen.getByLabelText('Edit'));
    const title = screen.getByLabelText('Edit title');
    await user.clear(title);
    await user.type(title, 'New title');
    await user.selectOptions(screen.getByLabelText('Edit category'), '2');
    await user.selectOptions(screen.getByLabelText('Edit priority'), '2');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(onUpdate).toHaveBeenCalledWith(10, expect.objectContaining({
      title: 'New title',
      description: 'old notes',
      priority: 2,
      categoryId: 2,
      dueDate: null,
      concurrencyToken: 'token-1',
    }));
    // Back to the read view: the card re-renders from props, which the parent owns.
    expect(screen.queryByLabelText('Edit title')).not.toBeInTheDocument();
  });

  it('sends null for cleared notes and no category', async () => {
    const user = userEvent.setup();
    const { onUpdate } = renderCard({ description: 'old notes' });

    await user.click(screen.getByLabelText('Edit'));
    await user.clear(screen.getByLabelText('Edit notes'));
    await user.selectOptions(screen.getByLabelText('Edit category'), '');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(onUpdate).toHaveBeenCalledWith(10, expect.objectContaining({
      description: null,
      categoryId: null,
    }));
  });

  it('trims whitespace off the title', async () => {
    const user = userEvent.setup();
    const { onUpdate } = renderCard();

    await user.click(screen.getByLabelText('Edit'));
    const title = screen.getByLabelText('Edit title');
    await user.clear(title);
    await user.type(title, '   Trimmed   ');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(onUpdate).toHaveBeenCalledWith(10, expect.objectContaining({ title: 'Trimmed' }));
  });

  it('refuses to save a blank title', async () => {
    const user = userEvent.setup();
    const { onUpdate } = renderCard();

    await user.click(screen.getByLabelText('Edit'));
    await user.clear(screen.getByLabelText('Edit title'));
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(onUpdate).not.toHaveBeenCalled();
    expect(screen.getByLabelText('Edit title')).toBeInTheDocument(); // still editing
  });

  it('discards the draft on cancel', async () => {
    const user = userEvent.setup();
    const { onUpdate } = renderCard();

    await user.click(screen.getByLabelText('Edit'));
    await user.type(screen.getByLabelText('Edit title'), ' extra');
    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(onUpdate).not.toHaveBeenCalled();
    expect(screen.getByText('Write the thing')).toBeInTheDocument();
  });

  it('disables both buttons while the save is in flight, then closes the editor', async () => {
    const user = userEvent.setup();
    let finishSave;
    const onUpdate = vi.fn(() => new Promise((resolve) => { finishSave = resolve; }));
    renderCard({}, { onUpdate });

    await user.click(screen.getByLabelText('Edit'));
    await user.click(screen.getByRole('button', { name: 'Save' }));

    // A second click would send a second update with the same concurrency token.
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Cancel' })).toBeDisabled();

    await act(async () => { finishSave(); });

    expect(screen.queryByLabelText('Edit title')).not.toBeInTheDocument();
  });

  it('seeds the date field from an existing due date', async () => {
    const user = userEvent.setup();
    renderCard({ dueDate: '2026-07-19T00:00:00Z' });

    await user.click(screen.getByLabelText('Edit'));

    expect(screen.getByLabelText('Edit due date')).toHaveValue('07/19/2026');
  });

  it('sends a due date typed into the date bar', async () => {
    const user = userEvent.setup();
    const { onUpdate } = renderCard();

    await user.click(screen.getByLabelText('Edit'));
    await user.type(screen.getByLabelText('Edit due date'), '07192026');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(onUpdate).toHaveBeenCalledWith(10, expect.objectContaining({
      dueDate: new Date('2026-07-19').toISOString(),
    }));
  });

  it('clears a due date that is emptied out', async () => {
    const user = userEvent.setup();
    const { onUpdate } = renderCard({ dueDate: '2026-07-19T00:00:00Z' });

    await user.click(screen.getByLabelText('Edit'));
    await user.clear(screen.getByLabelText('Edit due date'));
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(onUpdate).toHaveBeenCalledWith(10, expect.objectContaining({ dueDate: null }));
  });
});
