import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, act, waitFor } from '@testing-library/react';

vi.mock('../lib/apiClient.js', () => ({
  TodoApi: {
    list: vi.fn(),
    create: vi.fn(),
    update: vi.fn(),
    changeStatus: vi.fn(),
    remove: vi.fn(),
  },
}));

import { TodoApi } from '../lib/apiClient.js';
import { useTodos } from './useTodos.js';

const sample = () => [
  { id: 1, title: 'A', status: 0, isCompleted: false },
  { id: 2, title: 'B', status: 1, isCompleted: false },
];

beforeEach(() => {
  vi.clearAllMocks();
  TodoApi.list.mockResolvedValue(sample());
});

describe('useTodos()', () => {
  it('loads todos on mount', async () => {
    const { result } = renderHook(() => useTodos());
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.todos).toHaveLength(2);
  });

  it('optimistically moves a card, reconciles from the server, and does NOT refetch the board', async () => {
    TodoApi.changeStatus.mockResolvedValue({ id: 1, title: 'A', status: 2, isCompleted: true, concurrencyToken: 'x' });
    const { result } = renderHook(() => useTodos());
    await waitFor(() => expect(result.current.loading).toBe(false));

    await act(async () => { await result.current.moveCard(1, 2); });

    expect(TodoApi.changeStatus).toHaveBeenCalledWith(1, 2);
    const moved = result.current.todos.find((t) => t.id === 1);
    expect(moved.status).toBe(2);
    expect(moved.isCompleted).toBe(true);
    expect(TodoApi.list).toHaveBeenCalledTimes(1);
  });

  it('reverts by reloading when a move fails', async () => {
    TodoApi.changeStatus.mockRejectedValue(new Error('boom'));
    const { result } = renderHook(() => useTodos());
    await waitFor(() => expect(result.current.loading).toBe(false));

    await act(async () => { await result.current.moveCard(1, 2); });

    // A failed move triggers a fresh reload to snap back to server truth...
    expect(TodoApi.list).toHaveBeenCalledTimes(2); // mount + reload after the failure
    // ...and the optimistic change is undone (card back in its original lane).
    expect(result.current.todos.find((t) => t.id === 1).status).toBe(0);
    // the failure message now persists (set after the reload, not cleared by it).
    expect(result.current.error).toBe('boom');
  });
});

describe('useTodos() mutations', () => {
  it('surfaces a failed initial load', async () => {
    TodoApi.list.mockRejectedValue(new Error('Network is down'));
    const { result } = renderHook(() => useTodos());

    await waitFor(() => expect(result.current.loading).toBe(false));

    expect(result.current.error).toBe('Network is down');
    expect(result.current.todos).toEqual([]);
  });

  it('ignores a move to the lane the card is already in', async () => {
    const { result } = renderHook(() => useTodos());
    await waitFor(() => expect(result.current.loading).toBe(false));

    await act(async () => { await result.current.moveCard(1, 0); });

    expect(TodoApi.changeStatus).not.toHaveBeenCalled();
  });

  it('ignores a move for a card that is not on the board', async () => {
    const { result } = renderHook(() => useTodos());
    await waitFor(() => expect(result.current.loading).toBe(false));

    await act(async () => { await result.current.moveCard(999, 2); });

    expect(TodoApi.changeStatus).not.toHaveBeenCalled();
  });

  it('keeps the optimistic move when the server returns nothing to reconcile', async () => {
    TodoApi.changeStatus.mockResolvedValue(null);
    const { result } = renderHook(() => useTodos());
    await waitFor(() => expect(result.current.loading).toBe(false));

    await act(async () => { await result.current.moveCard(1, 2); });

    expect(result.current.todos.find((t) => t.id === 1).status).toBe(2);
    expect(TodoApi.list).toHaveBeenCalledTimes(1); // no reload
  });

  it('appends a created todo without refetching the board', async () => {
    TodoApi.create.mockResolvedValue({ id: 3, title: 'C', status: 0 });
    const { result } = renderHook(() => useTodos());
    await waitFor(() => expect(result.current.loading).toBe(false));

    await act(async () => { await result.current.createTodo({ title: 'C' }); });

    expect(result.current.todos).toHaveLength(3);
    expect(TodoApi.list).toHaveBeenCalledTimes(1);
  });

  it('reloads when a create returns nothing to append', async () => {
    TodoApi.create.mockResolvedValue(null);
    const { result } = renderHook(() => useTodos());
    await waitFor(() => expect(result.current.loading).toBe(false));

    await act(async () => { await result.current.createTodo({ title: 'C' }); });

    expect(TodoApi.list).toHaveBeenCalledTimes(2);
  });

  it('lets a failed create reach the caller so the form can show it', async () => {
    TodoApi.create.mockRejectedValue(new Error('Title is required.'));
    const { result } = renderHook(() => useTodos());
    await waitFor(() => expect(result.current.loading).toBe(false));

    await expect(result.current.createTodo({ title: '' })).rejects.toThrow('Title is required.');
  });

  it('merges an updated todo into place', async () => {
    TodoApi.update.mockResolvedValue({ id: 1, title: 'Renamed' });
    const { result } = renderHook(() => useTodos());
    await waitFor(() => expect(result.current.loading).toBe(false));

    await act(async () => { await result.current.updateTodo(1, { title: 'Renamed' }); });

    expect(result.current.todos.find((t) => t.id === 1).title).toBe('Renamed');
    expect(TodoApi.list).toHaveBeenCalledTimes(1);
  });

  it('reloads when an update returns nothing to merge', async () => {
    TodoApi.update.mockResolvedValue(null);
    const { result } = renderHook(() => useTodos());
    await waitFor(() => expect(result.current.loading).toBe(false));

    await act(async () => { await result.current.updateTodo(1, { title: 'Renamed' }); });

    expect(TodoApi.list).toHaveBeenCalledTimes(2);
  });

  it('reloads and explains a 409 rather than showing the raw message', async () => {
    const conflict = new Error('The resource was modified by someone else.');
    conflict.status = 409;
    TodoApi.update.mockRejectedValue(conflict);
    const { result } = renderHook(() => useTodos());
    await waitFor(() => expect(result.current.loading).toBe(false));

    await act(async () => { await result.current.updateTodo(1, { title: 'Renamed' }); });

    expect(TodoApi.list).toHaveBeenCalledTimes(2);
    expect(result.current.error).toMatch(/changed elsewhere/i);
  });

  it('shows any other update failure as-is without reloading', async () => {
    TodoApi.update.mockRejectedValue(new Error('Title is required.'));
    const { result } = renderHook(() => useTodos());
    await waitFor(() => expect(result.current.loading).toBe(false));

    await act(async () => { await result.current.updateTodo(1, { title: '' }); });

    expect(result.current.error).toBe('Title is required.');
    expect(TodoApi.list).toHaveBeenCalledTimes(1);
  });

  it('removes a deleted todo immediately', async () => {
    TodoApi.remove.mockResolvedValue(null);
    const { result } = renderHook(() => useTodos());
    await waitFor(() => expect(result.current.loading).toBe(false));

    await act(async () => { await result.current.deleteTodo(1); });

    expect(result.current.todos.map((t) => t.id)).toEqual([2]);
    expect(TodoApi.list).toHaveBeenCalledTimes(1);
  });

  it('puts a failed delete back by reloading', async () => {
    TodoApi.remove.mockRejectedValue(new Error('Gone already'));
    const { result } = renderHook(() => useTodos());
    await waitFor(() => expect(result.current.loading).toBe(false));

    await act(async () => { await result.current.deleteTodo(1); });

    expect(TodoApi.list).toHaveBeenCalledTimes(2);
    expect(result.current.todos).toHaveLength(2);
    expect(result.current.error).toBe('Gone already');
  });
});
