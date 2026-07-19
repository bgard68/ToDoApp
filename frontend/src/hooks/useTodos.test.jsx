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
