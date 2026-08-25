import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, within, fireEvent } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

vi.mock('../lib/apiClient.js', () => ({
  TodoApi: {
    list: vi.fn(),
    create: vi.fn(),
    update: vi.fn(),
    changeStatus: vi.fn(),
    remove: vi.fn(),
  },
  CategoryApi: {
    list: vi.fn(),
    create: vi.fn(),
    update: vi.fn(),
    remove: vi.fn(),
  },
}));

import { TodoApi, CategoryApi } from '../lib/apiClient.js';
import KanbanBoard from './KanbanBoard.jsx';

const categories = [
  { id: 1, name: 'Work', color: '#7fb2e6' },
  { id: 2, name: 'Personal', color: '#ef9db4' },
];

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

/** The lane section with the given heading, so a card can be located by column. */
function lane(label) {
  return screen.getByRole('heading', { name: label }).closest('.lane');
}

// The board toolbar and the category panel both carry a "Category" control and an "Add"/"Close"
// button, so every query below is scoped to the one it means.
const toolbar = () => within(document.querySelector('.board-filter'));
const panel = () => within(document.querySelector('.cat-manager'));
const categoryFilter = () => toolbar().getByLabelText('Category');

beforeEach(() => {
  vi.clearAllMocks();
  TodoApi.list.mockResolvedValue([]);
  CategoryApi.list.mockResolvedValue(categories);
});

async function renderBoard() {
  render(<KanbanBoard />);
  await waitFor(() => expect(screen.queryByText('Loading…')).not.toBeInTheDocument());
}

describe('KanbanBoard', () => {
  it('shows a loading note until the todos arrive', async () => {
    let release;
    TodoApi.list.mockReturnValue(new Promise((resolve) => { release = resolve; }));
    render(<KanbanBoard />);

    expect(screen.getByText('Loading…')).toBeInTheDocument();

    release([]);
    await waitFor(() => expect(screen.queryByText('Loading…')).not.toBeInTheDocument());
  });

  it('renders the three lanes', async () => {
    await renderBoard();

    expect(screen.getByRole('heading', { name: 'To Do' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'In Progress' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Done' })).toBeInTheDocument();
  });

  it('buckets each task into its own lane', async () => {
    TodoApi.list.mockResolvedValue([
      todo(1, { status: 0 }),
      todo(2, { status: 1 }),
      todo(3, { status: 2, isCompleted: true }),
    ]);
    await renderBoard();

    expect(within(lane('To Do')).getByText('Task 1')).toBeInTheDocument();
    expect(within(lane('In Progress')).getByText('Task 2')).toBeInTheDocument();
    expect(within(lane('Done')).getByText('Task 3')).toBeInTheDocument();
  });

  it('keeps a task with an unexpected status off the known lanes', async () => {
    TodoApi.list.mockResolvedValue([todo(1, { status: 9 })]);
    await renderBoard();

    // It must not silently land in "To Do" — that would misreport the board.
    expect(within(lane('To Do')).queryByText('Task 1')).not.toBeInTheDocument();
    expect(screen.getByText('1 tasks · 0 done')).toBeInTheDocument();
  });

  it('counts the tasks and the completed ones', async () => {
    TodoApi.list.mockResolvedValue([
      todo(1),
      todo(2, { status: 2, isCompleted: true }),
      todo(3, { status: 2, isCompleted: true }),
    ]);
    await renderBoard();

    expect(screen.getByText('3 tasks · 2 done')).toBeInTheDocument();
  });

  it('shows the load error', async () => {
    TodoApi.list.mockRejectedValue(new Error('Network is down'));
    await renderBoard();

    expect(screen.getByText('Network is down')).toBeInTheDocument();
  });
});

describe('KanbanBoard category filter', () => {
  it('offers every category plus "All"', async () => {
    await renderBoard();

    const filter = categoryFilter();
    expect(within(filter).getByRole('option', { name: 'All categories' })).toBeInTheDocument();
    expect(within(filter).getByRole('option', { name: 'Work' })).toBeInTheDocument();
    expect(within(filter).getByRole('option', { name: 'Personal' })).toBeInTheDocument();
  });

  it('hides tasks outside the chosen category', async () => {
    const user = userEvent.setup();
    TodoApi.list.mockResolvedValue([
      todo(1, { categoryId: 1 }),
      todo(2, { categoryId: 2 }),
    ]);
    await renderBoard();

    await user.selectOptions(categoryFilter(), '2');

    expect(screen.queryByText('Task 1')).not.toBeInTheDocument();
    expect(screen.getByText('Task 2')).toBeInTheDocument();
    // The footer still counts the whole board, not the filtered view.
    expect(screen.getByText('2 tasks · 0 done')).toBeInTheDocument();
  });

  it('falls back to "All" when the selected category disappears', async () => {
    const user = userEvent.setup();
    TodoApi.list.mockResolvedValue([todo(1, { categoryId: 1 }), todo(2, { categoryId: 2 })]);
    await renderBoard();

    await user.selectOptions(categoryFilter(), '2');
    expect(screen.queryByText('Task 1')).not.toBeInTheDocument();

    // Someone deletes that category in the manager panel; the board reloads the list.
    CategoryApi.list.mockResolvedValue([categories[0]]);
    CategoryApi.remove.mockResolvedValue(null);
    vi.spyOn(window, 'confirm').mockReturnValue(true);

    await user.click(toolbar().getByRole('button', { name: 'Manage categories' }));
    const row = panel().getByText('Personal').closest('li');
    await user.click(within(row).getByRole('button', { name: 'Delete' }));

    // Without the fallback the board would filter on a category that no longer exists
    // and show nothing at all.
    await waitFor(() => expect(categoryFilter()).toHaveValue('all'));
    expect(screen.getByText('Task 1')).toBeInTheDocument();
  });
});

describe('KanbanBoard category manager panel', () => {
  it('opens and closes from the toolbar', async () => {
    const user = userEvent.setup();
    await renderBoard();

    await user.click(toolbar().getByRole('button', { name: 'Manage categories' }));
    expect(screen.getByRole('heading', { name: 'Categories' })).toBeInTheDocument();

    await user.click(toolbar().getByRole('button', { name: 'Close' }));
    expect(screen.queryByRole('heading', { name: 'Categories' })).not.toBeInTheDocument();
  });

  it('closes from the panel itself', async () => {
    const user = userEvent.setup();
    await renderBoard();

    await user.click(toolbar().getByRole('button', { name: 'Manage categories' }));
    await user.click(panel().getByLabelText('Close'));

    expect(screen.queryByRole('heading', { name: 'Categories' })).not.toBeInTheDocument();
  });

  it('picks up a newly created category', async () => {
    const user = userEvent.setup();
    await renderBoard();

    await user.click(toolbar().getByRole('button', { name: 'Manage categories' }));
    CategoryApi.create.mockResolvedValue({ id: 3, name: 'Errands', color: '#86c97b' });
    CategoryApi.list.mockResolvedValue([...categories, { id: 3, name: 'Errands', color: '#86c97b' }]);

    await user.type(panel().getByLabelText('New category name'), 'Errands');
    await user.click(panel().getByRole('button', { name: 'Add' }));

    await waitFor(() => expect(
      within(categoryFilter()).getByRole('option', { name: 'Errands' })
    ).toBeInTheDocument());
  });
});

describe('KanbanBoard drag state', () => {
  it('marks the board while a card is being dragged, and clears it afterwards', async () => {
    TodoApi.list.mockResolvedValue([todo(1)]);
    const { container } = render(<KanbanBoard />);
    await waitFor(() => expect(screen.queryByText('Loading…')).not.toBeInTheDocument());

    const note = screen.getByText('Task 1').closest('.note');
    const dataTransfer = { setData: vi.fn(), effectAllowed: '' };

    fireEvent.dragStart(note, { dataTransfer });
    expect(container.querySelector('.board').className).toContain('is-dragging');
    expect(dataTransfer.setData).toHaveBeenCalledWith('text/plain', '1');

    fireEvent.dragEnd(note);
    expect(container.querySelector('.board').className).not.toContain('is-dragging');
  });

  it('moves a card when it is dropped on another lane', async () => {
    TodoApi.list.mockResolvedValue([todo(1, { status: 0 })]);
    TodoApi.changeStatus.mockResolvedValue(todo(1, { status: 2, isCompleted: true }));
    await renderBoard();

    fireEvent.drop(lane('Done'), {
      dataTransfer: { getData: () => '1', dropEffect: '' },
    });

    await waitFor(() => expect(TodoApi.changeStatus).toHaveBeenCalledWith(1, 2));
    await waitFor(() => expect(within(lane('Done')).getByText('Task 1')).toBeInTheDocument());
  });
});

describe('KanbanBoard task lifecycle', () => {
  it('adds a created task to the board', async () => {
    const user = userEvent.setup();
    TodoApi.create.mockResolvedValue(todo(9, { title: 'Brand new' }));
    await renderBoard();

    await user.type(screen.getByPlaceholderText(/add a task/i), 'Brand new');
    await user.click(screen.getByRole('button', { name: /add/i }));

    await waitFor(() => expect(screen.getByText('Brand new')).toBeInTheDocument());
  });

  it('removes a deleted task from the board', async () => {
    const user = userEvent.setup();
    TodoApi.list.mockResolvedValue([todo(1)]);
    TodoApi.remove.mockResolvedValue(null);
    await renderBoard();

    await user.click(screen.getByLabelText('Delete'));

    await waitFor(() => expect(screen.queryByText('Task 1')).not.toBeInTheDocument());
    expect(TodoApi.remove).toHaveBeenCalledWith(1);
  });

  it('applies an edit to the card', async () => {
    const user = userEvent.setup();
    TodoApi.list.mockResolvedValue([todo(1)]);
    TodoApi.update.mockResolvedValue(todo(1, { title: 'Renamed' }));
    await renderBoard();

    await user.click(screen.getByLabelText('Edit'));
    const title = screen.getByLabelText('Edit title');
    await user.clear(title);
    await user.type(title, 'Renamed');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(screen.getByText('Renamed')).toBeInTheDocument());
  });

  it('explains a concurrency conflict in plain language', async () => {
    const user = userEvent.setup();
    TodoApi.list.mockResolvedValue([todo(1)]);
    const conflict = new Error('The resource was modified by someone else.');
    conflict.status = 409;
    TodoApi.update.mockRejectedValue(conflict);
    await renderBoard();

    await user.click(screen.getByLabelText('Edit'));
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByText(/changed elsewhere/i)).toBeInTheDocument();
  });
});
