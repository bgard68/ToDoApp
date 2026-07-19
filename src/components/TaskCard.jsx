import { useState } from 'react';
import { PRIORITIES, DEFAULT_CATEGORY_COLOR } from '../lib/constants.js';
import { findCategory } from '../lib/categories.js';
import { tint } from '../lib/colors.js';

const priorityClass = { 0: 'low', 1: 'medium', 2: 'high' };

function formatDate(value) {
  if (!value) return null;
  return new Date(value).toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
}

/**
 * A single task rendered as a colored post-it note. Draggable between lanes;
 * shows a check mark when it's in the Done lane. The note's color comes from its
 * category (resolved from the fetched list), tinted toward white for legibility.
 */
export default function TaskCard({ todo, categories, onUpdate, onDelete, onDragStart, onDragEnd }) {
  const [editing, setEditing] = useState(false);
  const [busy, setBusy] = useState(false);
  const [draft, setDraft] = useState({
    title: todo.title,
    description: todo.description || '',
    priority: todo.priority,
    categoryId: todo.categoryId ?? '',
    dueDate: todo.dueDate ? todo.dueDate.substring(0, 10) : '',
  });

  const category = findCategory(categories, todo.categoryId);
  const baseColor = category?.color || DEFAULT_CATEGORY_COLOR;
  const noteStyle = { '--note-bg': tint(baseColor, 0.72), '--note-edge': baseColor };

  const overdue =
    todo.dueDate && !todo.isCompleted && new Date(todo.dueDate) < new Date(new Date().toDateString());

  async function save() {
    if (!draft.title.trim()) return;
    setBusy(true);
    try {
      await onUpdate(todo.id, {
        title: draft.title.trim(),
        description: draft.description.trim() || null,
        priority: Number(draft.priority),
        categoryId: draft.categoryId === '' ? null : Number(draft.categoryId),
        dueDate: draft.dueDate ? new Date(draft.dueDate).toISOString() : null,
        concurrencyToken: todo.concurrencyToken,
      });
      setEditing(false);
    } finally {
      setBusy(false);
    }
  }

  if (editing) {
    return (
      <div className="note note--editing" style={noteStyle}>
        <input
          type="text"
          value={draft.title}
          onChange={(e) => setDraft({ ...draft, title: e.target.value })}
          maxLength={200}
          aria-label="Edit title"
        />
        <input
          type="text"
          placeholder="Notes"
          value={draft.description}
          onChange={(e) => setDraft({ ...draft, description: e.target.value })}
          maxLength={2000}
          aria-label="Edit notes"
        />
        <div className="note__edit-meta">
          <select
            value={draft.categoryId}
            onChange={(e) => setDraft({ ...draft, categoryId: e.target.value })}
            aria-label="Edit category"
          >
            <option value="">No category</option>
            {categories.map((c) => (
              <option key={c.id} value={c.id}>{c.name}</option>
            ))}
          </select>
          <select
            value={draft.priority}
            onChange={(e) => setDraft({ ...draft, priority: e.target.value })}
            aria-label="Edit priority"
          >
            {PRIORITIES.map((p) => (
              <option key={p.value} value={p.value}>{p.label}</option>
            ))}
          </select>
          <input
            type="date"
            value={draft.dueDate}
            onChange={(e) => setDraft({ ...draft, dueDate: e.target.value })}
            aria-label="Edit due date"
          />
        </div>
        <div className="note__actions">
          <button className="btn btn--primary btn--sm" onClick={save} disabled={busy}>Save</button>
          <button className="btn btn--sm" onClick={() => setEditing(false)} disabled={busy}>Cancel</button>
        </div>
      </div>
    );
  }

  return (
    <div
      className={`note ${todo.isCompleted ? 'is-done' : ''}`}
      style={noteStyle}
      draggable
      onDragStart={(e) => onDragStart(e, todo)}
      onDragEnd={onDragEnd}
    >
      <div className="note__top">
        <span className="note__category">{category?.name || 'Uncategorized'}</span>
        {todo.isCompleted && <span className="note__check" title="Done">✓</span>}
      </div>

      <p className="note__title">{todo.title}</p>
      {todo.description && <p className="note__desc">{todo.description}</p>}

      <div className="note__meta">
        <span className={`dot dot--${priorityClass[todo.priority]}`} title={`${todo.priorityName} priority`} />
        {todo.dueDate && (
          <span className={`note__due ${overdue ? 'is-overdue' : ''}`}>
            {formatDate(todo.dueDate)}{overdue ? ' · overdue' : ''}
          </span>
        )}
        <span className="note__spacer" />
        <button className="note__icon" onClick={() => setEditing(true)} aria-label="Edit" title="Edit">✎</button>
        <button className="note__icon note__icon--danger" onClick={() => onDelete(todo.id)} aria-label="Delete" title="Delete">✕</button>
      </div>
    </div>
  );
}
