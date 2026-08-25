import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, act, waitFor } from '@testing-library/react';

vi.mock('../lib/apiClient.js', () => ({
  CategoryApi: { list: vi.fn() },
}));

import { CategoryApi } from '../lib/apiClient.js';
import { useCategories } from './useCategories.js';

beforeEach(() => {
  vi.clearAllMocks();
});

describe('useCategories()', () => {
  it('starts empty and loads on mount', async () => {
    CategoryApi.list.mockResolvedValue([{ id: 1, name: 'Work' }]);

    const { result } = renderHook(() => useCategories());

    expect(result.current.categories).toEqual([]);
    await waitFor(() => expect(result.current.categories).toHaveLength(1));
    expect(CategoryApi.list).toHaveBeenCalledTimes(1);
  });

  it('refetches on reload', async () => {
    CategoryApi.list.mockResolvedValue([{ id: 1, name: 'Work' }]);
    const { result } = renderHook(() => useCategories());
    await waitFor(() => expect(result.current.categories).toHaveLength(1));

    CategoryApi.list.mockResolvedValue([{ id: 1, name: 'Work' }, { id: 2, name: 'Personal' }]);
    await act(async () => { await result.current.reload(); });

    expect(result.current.categories).toHaveLength(2);
    expect(CategoryApi.list).toHaveBeenCalledTimes(2);
  });

  it('keeps a stable reload identity so effects do not re-run', async () => {
    CategoryApi.list.mockResolvedValue([]);
    const { result, rerender } = renderHook(() => useCategories());
    await waitFor(() => expect(CategoryApi.list).toHaveBeenCalled());

    const first = result.current.reload;
    rerender();

    expect(result.current.reload).toBe(first);
    expect(CategoryApi.list).toHaveBeenCalledTimes(1);
  });
});
