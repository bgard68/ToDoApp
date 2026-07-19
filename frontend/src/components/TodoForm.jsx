import { useState } from 'react';
import { PRIORITIES } from '../lib/constants.js';
import DateField from './DateField.jsx';

const empty = { title: '', description: '', priority: 1, categoryId: '', dueDate: '' };

export default function TodoForm({ onCreate, categories }) {
  const [form, setForm] = useState(empty);
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState('');

  const update = (field) => (e) => setForm((f) => ({ ...f, [field]: e.target.value }));

  async function handleSubmit(e) {
    e.preventDefault();
    if (!form.title.trim()) {
      setError('Title is required.');
      return;
    }
    setError('');
    setSubmitting(true);
    try {
      await onCreate({
        title: form.title.trim(),
        description: form.description.trim() || null,
        priority: Number(form.priority),
        categoryId: form.categoryId === '' ? null : Number(form.categoryId),
        dueDate: form.dueDate ? new Date(form.dueDate).toISOString() : null,
      });
      setForm(empty);
    } catch (err) {
      setError(err.message);
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <form className="todo-form" onSubmit={handleSubmit}>
      <div className="todo-form__row">
        <input
          className="todo-form__title"
          type="text"
          placeholder="Add a task to the To Do lane…"
          value={form.title}
          onChange={update('title')}
          maxLength={200}
          aria-label="Title"
        />
        <button className="btn btn--primary" type="submit" disabled={submitting}>
          {submitting ? 'Adding…' : 'Add'}
        </button>
      </div>

      <div className="todo-form__row todo-form__row--meta">
        <input
          className="todo-form__desc"
          type="text"
          placeholder="Notes (optional)"
          value={form.description}
          onChange={update('description')}
          maxLength={2000}
          aria-label="Description"
        />
        <select value={form.categoryId} onChange={update('categoryId')} aria-label="Category">
          <option value="">No category</option>
          {categories.map((c) => (
            <option key={c.id} value={c.id}>{c.name}</option>
          ))}
        </select>
        <select value={form.priority} onChange={update('priority')} aria-label="Priority">
          {PRIORITIES.map((p) => (
            <option key={p.value} value={p.value}>{p.label}</option>
          ))}
        </select>
        <DateField value={form.dueDate} onChange={(v) => setForm((f) => ({ ...f, dueDate: v }))} ariaLabel="Due date" />
      </div>

      {error && <p className="todo-form__error">{error}</p>}
    </form>
  );
}
