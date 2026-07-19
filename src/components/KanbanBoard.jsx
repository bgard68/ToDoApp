import { useEffect, useMemo, useState } from 'react';
import { STATUSES } from '../lib/constants.js';
import { useTodos } from '../hooks/useTodos.js';
import { useCategories } from '../hooks/useCategories.js';
import TodoForm from './TodoForm.jsx';
import Lane from './Lane.jsx';
import CategoryManager from './CategoryManager.jsx';

export default function KanbanBoard() {
  const { todos, loading, error, moveCard, createTodo, updateTodo, deleteTodo } = useTodos();
  const { categories, reload: reloadCategories } = useCategories();
  const [category, setCategory] = useState('all');
  const [dragging, setDragging] = useState(false);
  const [managingCategories, setManagingCategories] = useState(false);

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

  const doneCount = useMemo(() => todos.filter((t) => t.isCompleted).length, [todos]);

  return (
    <>
      <div className="card board-toolbar">
        <TodoForm onCreate={createTodo} categories={categories} />
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
          onChanged={reloadCategories}
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
              onDropCard={moveCard}
              onDragStart={handleDragStart}
              onDragEnd={handleDragEnd}
              onUpdate={updateTodo}
              onDelete={deleteTodo}
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
