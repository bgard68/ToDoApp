import { describe, it, expect } from 'vitest';
import { tint } from './colors.js';

describe('tint()', () => {
  it('returns the original color at amount 0 and white at amount 1', () => {
    expect(tint('#000000', 0)).toBe('rgb(0, 0, 0)');
    expect(tint('#000000', 1)).toBe('rgb(255, 255, 255)');
    expect(tint('#ffffff', 0.5)).toBe('rgb(255, 255, 255)');
  });

  it('falls back to the default category color for invalid input', () => {
    // #64748b => rgb(100, 116, 139)
    expect(tint('not-a-color', 0)).toBe('rgb(100, 116, 139)');
  });
});
