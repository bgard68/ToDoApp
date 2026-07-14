import { useCallback, useEffect, useMemo, useState } from 'react';
import { TodoApi, CategoryApi, STATUSES } from '../api.js';
import TodoForm from './TodoForm.jsx';
import Lane from './Lane.jsx';
import CategoryManager from './CategoryManager.jsx';

const DONE = 2;

export default function KanbanBoard() {
  const [todos, setTodos] = useState([]);
  const [categories, setCategories] = useState([]);
  const [category, setCategory] = useState('all');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [dragging, setDragging] = useState(false);
  const [managingCategories, setManagingCategories] = useState(false);

  const loadCategories = useCallback(async () => {
    setCategories(await CategoryApi.list());
  }, []);

  const load = useCallback(async () => {
    setLoading(true);
    setError('');
    try {
      const [items] = await Promise.all([TodoApi.list('All'), loadCategories()]);
      setTodos(items);
    } catch (err) {
      setError(err.message);
    } finally {
      setLoading(false);
    }
  }, [loadCategories]);

  useEffect(() => {
    load();
  }, [load]);

  // If the currently selected filter category is deleted, fall back to "all".
  useEffect(() => {
    if (category !== 'all' && !categories.some((c) => c.id === Number(category))) {
      setCategory('all');
    }
  }, [categories, category]);

  const visible = useMemo(
    () => (category === 'all' ? todos : todos.filter((t) => t.categoryId === Number(category))),
    [todos, category]
  );

  const byStatus = useMemo(() => {
    const map = { 0: [], 1: [], 2: [] };
    visible.forEach((t) => {
      (map[t.status] ?? (map[t.status] = [])).push(t);
    });
    return map;
  }, [visible]);

  function handleDragStart(e, todo) {
    setDragging(true);
    e.dataTransfer.setData('text/plain', String(todo.id));
    e.dataTransfer.effectAllowed = 'move';
  }

  function handleDragEnd() {
    setDragging(false);
  }

  async function handleDropCard(id, status) {
    const todo = todos.find((t) => t.id === id);
    if (!todo || todo.status === status) return;
    // Optimistic move for a snappy board.
    setTodos((prev) =>
      prev.map((t) => (t.id === id ? { ...t, status, isCompleted: status === DONE } : t))
    );
    try {
      // A drag-drop lane move is a deliberate single action, so it always applies
      // (the server still guards against lost updates via the tracked entity). The
      // edit form is where a stale-token conflict surfaces as a 409.
      await TodoApi.changeStatus(id, status);
      await load();
    } catch (err) {
      setError(err.message);
      await load();
    }
  }

  async function handleCreate(todo) {
    await TodoApi.create(todo);
    await load();
  }

  async function handleUpdate(id, data) {
    try {
      await TodoApi.update(id, data);
      await load();
    } catch (err) {
      if (err.status === 409) {
        setError('That task changed elsewhere. Reloaded with the latest — review and try again.');
        await load();
      } else {
        setError(err.message);
      }
    }
  }

  async function handleDelete(id) {
    setTodos((prev) => prev.filter((t) => t.id !== id));
    try {
      await TodoApi.remove(id);
    } catch (err) {
      setError(err.message);
      await load();
    }
  }

  const doneCount = useMemo(() => todos.filter((t) => t.isCompleted).length, [todos]);

  return (
    <>
      <div className="card board-toolbar">
        <TodoForm onCreate={handleCreate} categories={categories} />
        <div className="board-filter">
          <label htmlFor="cat-filter">Category</label>
          <select id="cat-filter" value={category} onChange={(e) => setCategory(e.target.value)}>
            <option value="all">All categories</option>
            {categories.map((c) => (
              <option key={c.id} value={c.id}>{c.name}</option>
            ))}
          </select>
          <button
            type="button"
            className="btn btn--ghost btn--sm"
            onClick={() => setManagingCategories((v) => !v)}
          >
            {managingCategories ? 'Close' : 'Manage categories'}
          </button>
        </div>
      </div>

      {managingCategories && (
        <CategoryManager
          categories={categories}
          onChanged={loadCategories}
          onClose={() => setManagingCategories(false)}
        />
      )}

      {error && <div className="banner banner--error">{error}</div>}

      {loading ? (
        <p className="empty">Loading…</p>
      ) : (
        <div className={`board ${dragging ? 'is-dragging' : ''}`}>
          {STATUSES.map((s) => (
            <Lane
              key={s.value}
              status={s.value}
              label={s.label}
              todos={byStatus[s.value] || []}
              categories={categories}
              onDropCard={handleDropCard}
              onDragStart={handleDragStart}
              onDragEnd={handleDragEnd}
              onUpdate={handleUpdate}
              onDelete={handleDelete}
            />
          ))}
        </div>
      )}

      <footer className="app__footer">
        <span>{todos.length} tasks · {doneCount} done</span>
      </footer>
    </>
  );
}
