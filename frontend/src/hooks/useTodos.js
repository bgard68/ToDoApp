import { useCallback, useEffect, useRef, useState } from 'react';
import { TodoApi } from '../lib/apiClient.js';
import { STATUS } from '../lib/constants.js';

/**
 * Owns the board's todo collection and every server operation. Each mutation updates
 * local state optimistically and reconciles just the affected card from the server
 * response, falling back to a full reload only on error - so the board never flashes.
 */
export function useTodos() {
  const [todos, setTodos] = useState([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');

  // Latest todos, so callbacks can read current state without being re-created each render.
  const todosRef = useRef(todos);
  todosRef.current = todos;

  const reload = useCallback(async () => {
    setLoading(true);
    setError('');
    try {
      setTodos(await TodoApi.list('All'));
    } catch (err) {
      setError(err.message);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    reload();
  }, [reload]);

  const moveCard = useCallback(async (id, status) => {
    const todo = todosRef.current.find((t) => t.id === id);
    if (!todo || todo.status === status) return;
    // Optimistic move for a snappy board.
    setTodos((prev) =>
      prev.map((t) => (t.id === id ? { ...t, status, isCompleted: status === STATUS.Done } : t))
    );
    try {
      const updated = await TodoApi.changeStatus(id, status);
      if (updated) setTodos((prev) => prev.map((t) => (t.id === id ? { ...t, ...updated } : t)));
    } catch (err) {
      await reload();
      setError(err.message);
    }
  }, [reload]);

  const createTodo = useCallback(async (todo) => {
    const created = await TodoApi.create(todo);
    if (created) setTodos((prev) => [...prev, created]);
    else await reload();
  }, [reload]);

  const updateTodo = useCallback(async (id, data) => {
    try {
      const updated = await TodoApi.update(id, data);
      if (updated) setTodos((prev) => prev.map((t) => (t.id === id ? { ...t, ...updated } : t)));
      else await reload();
    } catch (err) {
      if (err.status === 409) {
        await reload();
        setError('That task changed elsewhere. Reloaded with the latest - review and try again.');
      } else {
        setError(err.message);
      }
    }
  }, [reload]);

  const deleteTodo = useCallback(async (id) => {
    setTodos((prev) => prev.filter((t) => t.id !== id));
    try {
      await TodoApi.remove(id);
    } catch (err) {
      await reload();
      setError(err.message);
    }
  }, [reload]);

  return { todos, loading, error, reload, moveCard, createTodo, updateTodo, deleteTodo };
}
