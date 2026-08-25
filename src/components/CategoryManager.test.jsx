import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

vi.mock('../lib/apiClient.js', () => ({
  CategoryApi: {
    create: vi.fn(),
    update: vi.fn(),
    remove: vi.fn(),
  },
}));

import { CategoryApi } from '../lib/apiClient.js';
import CategoryManager from './CategoryManager.jsx';

const categories = [
  { id: 1, name: 'Work', color: '#7fb2e6' },
  { id: 2, name: 'Personal', color: null },
];

function renderManager(list = categories) {
  const onChanged = vi.fn().mockResolvedValue(undefined);
  const onClose = vi.fn();
  render(<CategoryManager categories={list} onChanged={onChanged} onClose={onClose} />);
  return { onChanged, onClose };
}

/** The row for a named category, so a control can be found without ambiguity. */
function rowFor(name) {
  return screen.getByText(name).closest('li');
}

beforeEach(() => {
  vi.clearAllMocks();
  CategoryApi.create.mockResolvedValue({ id: 3 });
  CategoryApi.update.mockResolvedValue({ id: 1 });
  CategoryApi.remove.mockResolvedValue(null);
});

describe('CategoryManager listing', () => {
  it('lists the categories', () => {
    renderManager();

    expect(screen.getByText('Work')).toBeInTheDocument();
    expect(screen.getByText('Personal')).toBeInTheDocument();
  });

  it('says so when there are none', () => {
    renderManager([]);

    expect(screen.getByText('No categories yet.')).toBeInTheDocument();
  });

  it('closes on Done', async () => {
    const user = userEvent.setup();
    const { onClose } = renderManager();

    await user.click(screen.getByLabelText('Close'));

    expect(onClose).toHaveBeenCalled();
  });
});

describe('CategoryManager create', () => {
  it('creates a category and clears the form', async () => {
    const user = userEvent.setup();
    const { onChanged } = renderManager();

    await user.type(screen.getByLabelText('New category name'), 'Errands');
    await user.click(screen.getByRole('button', { name: 'Add' }));

    await waitFor(() => expect(CategoryApi.create).toHaveBeenCalledWith({
      name: 'Errands',
      color: '#7fb2e6',
    }));
    expect(onChanged).toHaveBeenCalled();
    expect(screen.getByLabelText('New category name')).toHaveValue('');
  });

  it('trims the name', async () => {
    const user = userEvent.setup();
    renderManager();

    await user.type(screen.getByLabelText('New category name'), '   Errands   ');
    await user.click(screen.getByRole('button', { name: 'Add' }));

    await waitFor(() => expect(CategoryApi.create)
      .toHaveBeenCalledWith(expect.objectContaining({ name: 'Errands' })));
  });

  it('refuses a blank name without calling the API', async () => {
    const user = userEvent.setup();
    renderManager();

    await user.type(screen.getByLabelText('New category name'), '   ');
    await user.click(screen.getByRole('button', { name: 'Add' }));

    expect(await screen.findByText('Name is required.')).toBeInTheDocument();
    expect(CategoryApi.create).not.toHaveBeenCalled();
  });

  it('surfaces a rejected create and keeps the typed name', async () => {
    const user = userEvent.setup();
    CategoryApi.create.mockRejectedValue(new Error('A category with this name already exists.'));
    renderManager();

    await user.type(screen.getByLabelText('New category name'), 'Work');
    await user.click(screen.getByRole('button', { name: 'Add' }));

    expect(await screen.findByText('A category with this name already exists.')).toBeInTheDocument();
    expect(screen.getByLabelText('New category name')).toHaveValue('Work');
  });
});

describe('CategoryManager edit', () => {
  it('renames a category', async () => {
    const user = userEvent.setup();
    const { onChanged } = renderManager();

    await user.click(within(rowFor('Work')).getByRole('button', { name: 'Edit' }));
    const field = screen.getByLabelText('Category name');
    await user.clear(field);
    await user.type(field, 'Job');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(CategoryApi.update)
      .toHaveBeenCalledWith(1, { name: 'Job', color: '#7fb2e6' }));
    expect(onChanged).toHaveBeenCalled();
  });

  it('falls back to the default color for a category that has none', async () => {
    const user = userEvent.setup();
    renderManager();

    await user.click(within(rowFor('Personal')).getByRole('button', { name: 'Edit' }));
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(CategoryApi.update)
      .toHaveBeenCalledWith(2, { name: 'Personal', color: '#64748b' }));
  });

  it('refuses a blank name', async () => {
    const user = userEvent.setup();
    renderManager();

    await user.click(within(rowFor('Work')).getByRole('button', { name: 'Edit' }));
    await user.clear(screen.getByLabelText('Category name'));
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByText('Name is required.')).toBeInTheDocument();
    expect(CategoryApi.update).not.toHaveBeenCalled();
  });

  it('stays open when the save is rejected', async () => {
    const user = userEvent.setup();
    CategoryApi.update.mockRejectedValue(new Error('Conflict'));
    renderManager();

    await user.click(within(rowFor('Work')).getByRole('button', { name: 'Edit' }));
    await user.click(screen.getByRole('button', { name: 'Save' }));

    expect(await screen.findByText('Conflict')).toBeInTheDocument();
    expect(screen.getByLabelText('Category name')).toBeInTheDocument();
  });

  it('abandons the edit on cancel', async () => {
    const user = userEvent.setup();
    renderManager();

    await user.click(within(rowFor('Work')).getByRole('button', { name: 'Edit' }));
    await user.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(screen.queryByLabelText('Category name')).not.toBeInTheDocument();
    expect(CategoryApi.update).not.toHaveBeenCalled();
  });

  it('clears a previous error when a new edit starts', async () => {
    const user = userEvent.setup();
    renderManager();

    await user.type(screen.getByLabelText('New category name'), '  ');
    await user.click(screen.getByRole('button', { name: 'Add' }));
    expect(await screen.findByText('Name is required.')).toBeInTheDocument();

    await user.click(within(rowFor('Work')).getByRole('button', { name: 'Edit' }));

    expect(screen.queryByText('Name is required.')).not.toBeInTheDocument();
  });
});

describe('CategoryManager colors', () => {
  it('sends the color chosen for a new category', async () => {
    const user = userEvent.setup();
    renderManager();

    await user.click(screen.getByRole('button', { name: /new category color/i }));
    const hex = screen.getByLabelText(/new category color hex value/i);
    await user.clear(hex);
    await user.type(hex, '00ff00');
    await user.type(screen.getByLabelText('New category name'), 'Errands');
    await user.click(screen.getByRole('button', { name: 'Add' }));

    await waitFor(() => expect(CategoryApi.create)
      .toHaveBeenCalledWith({ name: 'Errands', color: '#00ff00' }));
  });

  it('sends the recolored value when a category is edited', async () => {
    const user = userEvent.setup();
    renderManager();

    await user.click(within(rowFor('Work')).getByRole('button', { name: 'Edit' }));
    // The add form carries its own picker, so scope to the row being edited.
    const editRow = within(document.querySelector('.cat-manager__row--edit'));
    await user.click(editRow.getByRole('button', { name: /^category color:/i }));
    const hex = screen.getByLabelText(/^category color hex value$/i);
    await user.clear(hex);
    await user.type(hex, '123456');
    await user.click(screen.getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(CategoryApi.update)
      .toHaveBeenCalledWith(1, { name: 'Work', color: '#123456' }));
  });
});

describe('CategoryManager delete', () => {
  it('deletes after the confirmation is accepted', async () => {
    const user = userEvent.setup();
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    const { onChanged } = renderManager();

    await user.click(within(rowFor('Work')).getByRole('button', { name: 'Delete' }));

    await waitFor(() => expect(CategoryApi.remove).toHaveBeenCalledWith(1));
    expect(onChanged).toHaveBeenCalled();
    expect(window.confirm).toHaveBeenCalledWith(expect.stringContaining('Work'));
  });

  it('does nothing when the confirmation is declined', async () => {
    const user = userEvent.setup();
    vi.spyOn(window, 'confirm').mockReturnValue(false);
    renderManager();

    await user.click(within(rowFor('Work')).getByRole('button', { name: 'Delete' }));

    expect(CategoryApi.remove).not.toHaveBeenCalled();
  });

  it('surfaces a rejected delete', async () => {
    const user = userEvent.setup();
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    CategoryApi.remove.mockRejectedValue(new Error('Still in use'));
    renderManager();

    await user.click(within(rowFor('Work')).getByRole('button', { name: 'Delete' }));

    expect(await screen.findByText('Still in use')).toBeInTheDocument();
  });
});