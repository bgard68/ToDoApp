import { describe, it, expect } from 'vitest';
import { findCategory } from './categories.js';

const cats = [{ id: 1, name: 'Work' }, { id: 2, name: 'Home' }];

describe('findCategory()', () => {
  it('finds a category by id', () => {
    expect(findCategory(cats, 2)).toEqual({ id: 2, name: 'Home' });
  });
  it('returns null for a null or undefined id', () => {
    expect(findCategory(cats, null)).toBeNull();
    expect(findCategory(cats, undefined)).toBeNull();
  });
  it('returns null for an unknown id', () => {
    expect(findCategory(cats, 999)).toBeNull();
  });
});
