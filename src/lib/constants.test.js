import { describe, it, expect } from 'vitest';
import { STATUSES, PRIORITIES, STATUS } from './constants.js';

describe('constants', () => {
  it('exposes three lanes in backend-enum order', () => {
    expect(STATUSES.map((s) => s.value)).toEqual([0, 1, 2]);
    expect(STATUS.ToDo).toBe(0);
    expect(STATUS.Done).toBe(2);
  });
  it('exposes three priorities', () => {
    expect(PRIORITIES.map((p) => p.value)).toEqual([0, 1, 2]);
  });
});
