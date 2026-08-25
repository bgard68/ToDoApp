import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import Lane from './Lane.jsx';

const categories = [{ id: 1, name: 'Work', color: '#7fb2e6' }];

const todo = (id, overrides = {}) => ({
  id,
  title: `Task ${id}`,
  description: '',
  status: 0,
  priority: 1,
  priorityName: 'Medium',
  categoryId: 1,
  dueDate: null,
  isCompleted: false,
  concurrencyToken: `token-${id}`,
  ...overrides,
});

function renderLane({ todos = [], status = 0 } = {}) {
  const handlers = {
    onDropCard: vi.fn(),
    onDragStart: vi.fn(),
    onDragEnd: vi.fn(),
    onUpdate: vi.fn(),
    onDelete: vi.fn(),
  };

  const { container } = render(
    <Lane status={status} label="To Do" todos={todos} categories={categories} {...handlers} />
  );

  return { ...handlers, lane: container.querySelector('.lane') };
}

/** jsdom implements no drag-and-drop, so the transfer object is supplied explicitly. */
function dataTransfer(payload = '') {
  return {
    dropEffect: '',
    getData: vi.fn(() => payload),
    setData: vi.fn(),
  };
}

describe('Lane', () => {
  it('shows the label and the card count', () => {
    renderLane({ todos: [todo(1), todo(2)] });

    expect(screen.getByRole('heading', { name: 'To Do' })).toBeInTheDocument();
    expect(screen.getByText('2')).toBeInTheDocument();
  });

  it('prompts when empty', () => {
    renderLane({ todos: [] });

    expect(screen.getByText('Drop tasks here')).toBeInTheDocument();
    expect(screen.getByText('0')).toBeInTheDocument();
  });

  it('renders a card per todo', () => {
    renderLane({ todos: [todo(1), todo(2)] });

    expect(screen.getByText('Task 1')).toBeInTheDocument();
    expect(screen.getByText('Task 2')).toBeInTheDocument();
    expect(screen.queryByText('Drop tasks here')).not.toBeInTheDocument();
  });

  it('highlights while a card is dragged over it', () => {
    const { lane } = renderLane();

    fireEvent.dragOver(lane, { dataTransfer: dataTransfer() });

    expect(lane.className).toContain('is-over');
  });

  it('stays highlighted across repeated dragover events', () => {
    const { lane } = renderLane();
    const transfer = dataTransfer();

    fireEvent.dragOver(lane, { dataTransfer: transfer });
    fireEvent.dragOver(lane, { dataTransfer: transfer });

    expect(lane.className).toContain('is-over');
  });

  it('drops the highlight when the card leaves', () => {
    const { lane } = renderLane();

    fireEvent.dragOver(lane, { dataTransfer: dataTransfer() });
    fireEvent.dragLeave(lane);

    expect(lane.className).not.toContain('is-over');
  });

  it('moves the dropped card into this lane', () => {
    const { lane, onDropCard } = renderLane({ status: 2 });

    fireEvent.dragOver(lane, { dataTransfer: dataTransfer() });
    fireEvent.drop(lane, { dataTransfer: dataTransfer('7') });

    expect(onDropCard).toHaveBeenCalledWith(7, 2);
    expect(lane.className).not.toContain('is-over');
  });

  it('ignores a drop that carries no card id', () => {
    const { lane, onDropCard } = renderLane();

    fireEvent.drop(lane, { dataTransfer: dataTransfer('') });

    expect(onDropCard).not.toHaveBeenCalled();
  });

  it('ignores a drop carrying something that is not a card id', () => {
    const { lane, onDropCard } = renderLane();

    fireEvent.drop(lane, { dataTransfer: dataTransfer('not-a-number') });

    expect(onDropCard).not.toHaveBeenCalled();
  });

  it('passes the move handler down so a tap-move works too', async () => {
    const userEvent = (await import('@testing-library/user-event')).default;
    const user = userEvent.setup();
    const { onDropCard } = renderLane({ todos: [todo(3)], status: 0 });

    await user.click(screen.getByLabelText('Move to another lane'));
    await user.click(screen.getByRole('button', { name: '→ Done' }));

    expect(onDropCard).toHaveBeenCalledWith(3, 2);
  });
});
