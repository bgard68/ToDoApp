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

// Frozen fixtures. Built fresh per test so an accidental in-place mutation in the hook
// cannot leak between cases and mask a bug.
const todoToDo = () => ({ id: 1, title: 'Write tests', status: 0, isCompleted: false, concurrencyToken: 'tok-1' });
const todoInProgress = () => ({ id: 2, title: 'Review PR', status: 1, isCompleted: false, concurrencyToken: 'tok-2' });
const board = () => [todoToDo(), todoInProgress()];

/** Renders the hook and waits out the mount-time load. Returns the hook result handle. */
async function renderLoadedBoard() {
  const handle = renderHook(() => useTodos());
  await waitFor(() => expect(handle.result.current.loading).toBe(false));
  return handle.result;
}

beforeEach(() => {
  vi.clearAllMocks();
  TodoApi.list.mockResolvedValue(board());
});

describe('useTodos / reload', () => {
  it('reload_onMount_requestsAllLanesAndExposesExactServerPayload', async () => {
    // Arrange — mount-time load is stubbed in beforeEach.

    // Act
    const result = await renderLoadedBoard();

    // Assert — the 'All' argument is the contract with the API; asserting only the call
    // count would let a mutation to 'Done' survive.
    expect(TodoApi.list).toHaveBeenCalledTimes(1);
    expect(TodoApi.list).toHaveBeenCalledWith('All');
    expect(result.current.todos).toEqual(board());
    expect(result.current.error).toBe('');
  });

  it('reload_whenListRejects_surfacesMessageAndLeavesBoardEmpty', async () => {
    // Arrange
    TodoApi.list.mockRejectedValue(new Error('Network is down'));

    // Act
    const result = await renderLoadedBoard();

    // Assert
    expect(result.current.error).toBe('Network is down');
    expect(result.current.todos).toEqual([]);
    expect(result.current.loading).toBe(false);
  });

  it('reload_afterAPriorFailure_clearsStaleErrorBeforeRefetching', async () => {
    // Arrange
    TodoApi.list.mockRejectedValueOnce(new Error('Transient blip'));
    const result = await renderLoadedBoard();
    expect(result.current.error).toBe('Transient blip');
    TodoApi.list.mockResolvedValue(board());

    // Act
    await act(async () => { await result.current.reload(); });

    // Assert
    expect(result.current.error).toBe('');
    expect(result.current.todos).toEqual(board());
  });

  it('reload_whenARefetchFailsAfterASuccessfulLoad_keepsThePreviouslyLoadedBoard', async () => {
    // Arrange
    const result = await renderLoadedBoard();
    TodoApi.list.mockRejectedValue(new Error('Gateway timeout'));

    // Act
    await act(async () => { await result.current.reload(); });

    // Assert - a failed refetch must not blank a board the user is looking at.
    expect(result.current.todos).toEqual(board());
    expect(result.current.error).toBe('Gateway timeout');
    expect(result.current.loading).toBe(false);
  });
});

describe('useTodos / moveCard', () => {
  it('moveCard_toDoneLane_reconcilesFromServerWithoutRefetchingBoard', async () => {
    // Arrange
    const serverCard = { id: 1, title: 'Write tests', status: 2, isCompleted: true, concurrencyToken: 'tok-1b' };
    TodoApi.changeStatus.mockResolvedValue(serverCard);
    const result = await renderLoadedBoard();

    // Act
    await act(async () => { await result.current.moveCard(1, 2); });

    // Assert — full board shape, so a mutation that drops or reorders the untouched card fails.
    expect(TodoApi.changeStatus).toHaveBeenCalledWith(1, 2);
    expect(result.current.todos).toEqual([serverCard, todoInProgress()]);
    expect(TodoApi.list).toHaveBeenCalledTimes(1);
  });

  it('moveCard_whenServerReturnsNull_keepsOptimisticIsCompletedDerivedFromStatus', async () => {
    // Arrange — null response means "no reconciliation payload"; optimistic state must stand.
    TodoApi.changeStatus.mockResolvedValue(null);
    const result = await renderLoadedBoard();

    // Act
    await act(async () => { await result.current.moveCard(1, 2); });

    // Assert — isCompleted is derived (status === Done), not echoed by the server here.
    expect(result.current.todos).toEqual([
      { ...todoToDo(), status: 2, isCompleted: true },
      todoInProgress(),
    ]);
    expect(TodoApi.list).toHaveBeenCalledTimes(1);
  });

  it('moveCard_toSameLane_shortCircuitsWithoutCallingTheApi', async () => {
    // Arrange
    const result = await renderLoadedBoard();

    // Act
    await act(async () => { await result.current.moveCard(1, 0); });

    // Assert
    expect(TodoApi.changeStatus).not.toHaveBeenCalled();
    expect(result.current.todos).toEqual(board());
  });

  it('moveCard_withUnknownId_shortCircuitsWithoutCallingTheApi', async () => {
    // Arrange
    const result = await renderLoadedBoard();

    // Act
    await act(async () => { await result.current.moveCard(9999, 2); });

    // Assert
    expect(TodoApi.changeStatus).not.toHaveBeenCalled();
    expect(result.current.todos).toEqual(board());
  });

  it('moveCard_whenChangeStatusRejects_revertsToServerTruthAndReportsFailure', async () => {
    // Arrange
    TodoApi.changeStatus.mockRejectedValue(new Error('boom'));
    const result = await renderLoadedBoard();

    // Act
    await act(async () => { await result.current.moveCard(1, 2); });

    // Assert — reload restores the original lane and the error survives the reload's error reset.
    expect(TodoApi.list).toHaveBeenCalledTimes(2);
    expect(result.current.todos).toEqual(board());
    expect(result.current.error).toBe('boom');
  });
});

describe('useTodos / createTodo', () => {
  it('createTodo_whenServerReturnsCreated_appendsItWithoutRefetching', async () => {
    // Arrange
    const created = { id: 3, title: 'Ship it', status: 0, isCompleted: false, concurrencyToken: 'tok-3' };
    TodoApi.create.mockResolvedValue(created);
    const result = await renderLoadedBoard();

    // Act
    await act(async () => { await result.current.createTodo({ title: 'Ship it' }); });

    // Assert — appended last; asserting the whole array pins ordering.
    expect(TodoApi.create).toHaveBeenCalledWith({ title: 'Ship it' });
    expect(result.current.todos).toEqual([todoToDo(), todoInProgress(), created]);
    expect(TodoApi.list).toHaveBeenCalledTimes(1);
  });

  it('createTodo_whenServerReturnsNull_fallsBackToAFullReload', async () => {
    // Arrange
    TodoApi.create.mockResolvedValue(null);
    const result = await renderLoadedBoard();

    // Act
    await act(async () => { await result.current.createTodo({ title: 'Ship it' }); });

    // Assert
    expect(TodoApi.list).toHaveBeenCalledTimes(2);
    expect(result.current.todos).toEqual(board());
  });

  it('createTodo_whenCreateRejects_propagatesToCallerAndLeavesBoardUnchanged', async () => {
    // Arrange — createTodo deliberately has no catch, unlike update/delete. The caller owns
    // the failure so the form can keep the user's draft. This asymmetry is load-bearing.
    TodoApi.create.mockRejectedValue(new Error('Validation failed'));
    const result = await renderLoadedBoard();

    // Act
    const attempt = act(async () => { await result.current.createTodo({ title: '' }); });

    // Assert
    await expect(attempt).rejects.toThrow('Validation failed');
    expect(result.current.todos).toEqual(board());
    expect(result.current.error).toBe('');
  });

  it('createTodo_whenNullResponseAndTheFallbackReloadAlsoFails_surfacesTheReloadError', async () => {
    // Arrange - compound failure: no created payload, and the recovery path fails too.
    TodoApi.create.mockResolvedValue(null);
    const result = await renderLoadedBoard();
    TodoApi.list.mockRejectedValue(new Error('Reload failed'));

    // Act
    await act(async () => { await result.current.createTodo({ title: 'Ship it' }); });

    // Assert
    expect(TodoApi.list).toHaveBeenCalledTimes(2);
    expect(result.current.error).toBe('Reload failed');
    expect(result.current.todos).toEqual(board());
  });
});

describe('useTodos / updateTodo', () => {
  it('updateTodo_whenServerReturnsUpdated_mergesOnlyThatCard', async () => {
    // Arrange
    const updated = { id: 2, title: 'Review PR (edited)', status: 1, isCompleted: false, concurrencyToken: 'tok-2b' };
    TodoApi.update.mockResolvedValue(updated);
    const result = await renderLoadedBoard();

    // Act
    await act(async () => { await result.current.updateTodo(2, { title: 'Review PR (edited)' }); });

    // Assert
    expect(TodoApi.update).toHaveBeenCalledWith(2, { title: 'Review PR (edited)' });
    expect(result.current.todos).toEqual([todoToDo(), updated]);
    expect(TodoApi.list).toHaveBeenCalledTimes(1);
  });

  it('updateTodo_whenServerReturns409_reloadsAndShowsTheConcurrencyNotice', async () => {
    // Arrange — the 409 branch has its own user-facing copy, distinct from err.message.
    const conflict = Object.assign(new Error('Conflict'), { status: 409 });
    TodoApi.update.mockRejectedValue(conflict);
    const result = await renderLoadedBoard();

    // Act
    await act(async () => { await result.current.updateTodo(2, { title: 'Stale edit' }); });

    // Assert
    expect(TodoApi.list).toHaveBeenCalledTimes(2);
    expect(result.current.error).toBe(
      'That task changed elsewhere. Reloaded with the latest - review and try again.'
    );
    expect(result.current.todos).toEqual(board());
  });

  it('updateTodo_whenServerReturnsNon409Error_reportsRawMessageWithoutReloading', async () => {
    // Arrange
    const failure = Object.assign(new Error('Title is required.'), { status: 400 });
    TodoApi.update.mockRejectedValue(failure);
    const result = await renderLoadedBoard();

    // Act
    await act(async () => { await result.current.updateTodo(2, { title: '' }); });

    // Assert — a 400 must NOT trigger the reload that 409 does.
    expect(result.current.error).toBe('Title is required.');
    expect(TodoApi.list).toHaveBeenCalledTimes(1);
  });

  it('updateTodo_whenServerReturnsNull_fallsBackToAFullReload', async () => {
    // Arrange
    TodoApi.update.mockResolvedValue(null);
    const result = await renderLoadedBoard();

    // Act
    await act(async () => { await result.current.updateTodo(2, { title: 'x' }); });

    // Assert
    expect(TodoApi.list).toHaveBeenCalledTimes(2);
    expect(result.current.todos).toEqual(board());
  });
});

describe('useTodos / deleteTodo', () => {
  it('deleteTodo_onSuccess_removesOnlyThatCardWithoutRefetching', async () => {
    // Arrange
    TodoApi.remove.mockResolvedValue(undefined);
    const result = await renderLoadedBoard();

    // Act
    await act(async () => { await result.current.deleteTodo(1); });

    // Assert
    expect(TodoApi.remove).toHaveBeenCalledWith(1);
    expect(result.current.todos).toEqual([todoInProgress()]);
    expect(TodoApi.list).toHaveBeenCalledTimes(1);
  });

  it('deleteTodo_whenRemoveRejects_restoresTheOptimisticallyRemovedCard', async () => {
    // Arrange
    TodoApi.remove.mockRejectedValue(new Error('Delete failed'));
    const result = await renderLoadedBoard();

    // Act
    await act(async () => { await result.current.deleteTodo(1); });

    // Assert — the card must come back, not stay optimistically gone.
    expect(TodoApi.list).toHaveBeenCalledTimes(2);
    expect(result.current.todos).toEqual(board());
    expect(result.current.error).toBe('Delete failed');
  });

  it('deleteTodo_withUnknownId_leavesBoardIntactAndStillCallsTheApi', async () => {
    // Arrange — the hook filters first and does not guard on existence, so the call goes out.
    TodoApi.remove.mockResolvedValue(undefined);
    const result = await renderLoadedBoard();

    // Act
    await act(async () => { await result.current.deleteTodo(9999); });

    // Assert
    expect(TodoApi.remove).toHaveBeenCalledWith(9999);
    expect(result.current.todos).toEqual(board());
  });

  it('deleteTodo_whenRemoveRejectsAndTheRevertReloadAlsoFails_reportsTheDeleteError', async () => {
    // Arrange - both the delete and its recovery reload fail; the delete's message must win,
    // because it is set after the reload rather than by it.
    TodoApi.remove.mockRejectedValue(new Error('Delete failed'));
    const result = await renderLoadedBoard();
    TodoApi.list.mockRejectedValue(new Error('Reload failed'));

    // Act
    await act(async () => { await result.current.deleteTodo(1); });

    // Assert
    expect(result.current.error).toBe('Delete failed');
    expect(result.current.loading).toBe(false);
  });
});
