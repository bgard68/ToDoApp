import { useState } from 'react';
import { CategoryApi } from '../lib/apiClient.js';
import { DEFAULT_CATEGORY_COLOR } from '../lib/constants.js';
import ColorPicker from './ColorPicker.jsx';

const emptyDraft = { name: '', color: '#7fb2e6' };

/**
 * A small panel for creating, renaming, recoloring, and deleting the signed-in
 * user's categories. Changes are pushed to the API and the parent is told to
 * reload so the board, form, and cards all pick up the new list.
 */
export default function CategoryManager({ categories, onChanged, onClose }) {
  const [draft, setDraft] = useState(emptyDraft);
  const [editingId, setEditingId] = useState(null);
  const [editDraft, setEditDraft] = useState(emptyDraft);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  async function run(action) {
    setBusy(true);
    setError('');
    try {
      await action();
      await onChanged();
      return true;
    } catch (err) {
      setError(err.message);
      return false;
    } finally {
      setBusy(false);
    }
  }

  async function handleCreate(e) {
    e.preventDefault();
    if (!draft.name.trim()) {
      setError('Name is required.');
      return;
    }
    const ok = await run(() =>
      CategoryApi.create({ name: draft.name.trim(), color: draft.color })
    );
    if (ok) setDraft(emptyDraft);
  }

  function startEdit(category) {
    setEditingId(category.id);
    setEditDraft({ name: category.name, color: category.color || DEFAULT_CATEGORY_COLOR });
    setError('');
  }

  async function saveEdit(id) {
    if (!editDraft.name.trim()) {
      setError('Name is required.');
      return;
    }
    const ok = await run(() =>
      CategoryApi.update(id, { name: editDraft.name.trim(), color: editDraft.color })
    );
    if (ok) setEditingId(null);
  }

  async function handleDelete(category) {
    const ok = window.confirm(
      `Delete “${category.name}”? Tasks in this category will become uncategorized.`
    );
    if (!ok) return;
    await run(() => CategoryApi.remove(category.id));
  }

  return (
    <div className="cat-manager card">
      <div className="cat-manager__head">
        <h2>Categories</h2>
        <button className="btn btn--ghost btn--sm" onClick={onClose} aria-label="Close">Done</button>
      </div>

      {error && <p className="cat-manager__error">{error}</p>}

      <ul className="cat-manager__list">
        {categories.length === 0 && <li className="cat-manager__empty">No categories yet.</li>}
        {categories.map((c) =>
          editingId === c.id ? (
            <li key={c.id} className="cat-manager__row cat-manager__row--edit">
              <ColorPicker
                value={editDraft.color}
                onChange={(color) => setEditDraft({ ...editDraft, color })}
                label="Category color"
              />
              <input
                type="text"
                value={editDraft.name}
                onChange={(e) => setEditDraft({ ...editDraft, name: e.target.value })}
                maxLength={50}
                aria-label="Category name"
              />
              <button className="btn btn--primary btn--sm" onClick={() => saveEdit(c.id)} disabled={busy}>Save</button>
              <button className="btn btn--sm" onClick={() => setEditingId(null)} disabled={busy}>Cancel</button>
            </li>
          ) : (
            <li key={c.id} className="cat-manager__row">
              <span className="cat-manager__swatch" style={{ background: c.color || DEFAULT_CATEGORY_COLOR }} />
              <span className="cat-manager__name">{c.name}</span>
              <button className="btn btn--ghost btn--sm" onClick={() => startEdit(c)} disabled={busy}>Edit</button>
              <button className="btn btn--ghost btn--sm" onClick={() => handleDelete(c)} disabled={busy}>Delete</button>
            </li>
          )
        )}
      </ul>

      <form className="cat-manager__add" onSubmit={handleCreate}>
        <ColorPicker
          value={draft.color}
          onChange={(color) => setDraft({ ...draft, color })}
          label="New category color"
        />
        <input
          type="text"
          placeholder="New category…"
          value={draft.name}
          onChange={(e) => setDraft({ ...draft, name: e.target.value })}
          maxLength={50}
          aria-label="New category name"
        />
        <button className="btn btn--primary btn--sm" type="submit" disabled={busy}>Add</button>
      </form>
    </div>
  );
}
