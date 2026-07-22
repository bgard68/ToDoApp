import { describe, it, expect } from 'vitest';
import { tint, hsvToHex, hexToHsv, isValidHexColor } from './colors.js';

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

describe('isValidHexColor()', () => {
  it('accepts a 6-digit hex with or without a leading #', () => {
    expect(isValidHexColor('#4f46e5')).toBe(true);
    expect(isValidHexColor('4F46E5')).toBe(true);
  });

  it('rejects anything that is not a 6-digit hex', () => {
    expect(isValidHexColor('not-a-color')).toBe(false);
    expect(isValidHexColor('#fff')).toBe(false);
    expect(isValidHexColor('#12345g')).toBe(false);
    expect(isValidHexColor('')).toBe(false);
  });
});

describe('hsvToHex()', () => {
  it('maps the primaries and greyscale correctly', () => {
    expect(hsvToHex(0, 1, 1)).toBe('#ff0000');
    expect(hsvToHex(120, 1, 1)).toBe('#00ff00');
    expect(hsvToHex(240, 1, 1)).toBe('#0000ff');
    expect(hsvToHex(0, 0, 1)).toBe('#ffffff');
    expect(hsvToHex(0, 0, 0)).toBe('#000000');
  });

  it('always emits an API-valid #rrggbb string', () => {
    expect(hsvToHex(45, 0.6, 0.9)).toMatch(/^#[0-9a-f]{6}$/);
    // clamps out-of-range input rather than producing garbage
    expect(hsvToHex(400, 2, -1)).toMatch(/^#[0-9a-f]{6}$/);
  });
});

describe('hexToHsv()', () => {
  it('round-trips back through hsvToHex', () => {
    for (const hex of ['#4f46e5', '#7fb2e6', '#86c97b', '#000000', '#ffffff']) {
      const { h, s, v } = hexToHsv(hex);
      expect(hsvToHex(h, s, v)).toBe(hex);
    }
  });

  it('returns null for invalid input', () => {
    expect(hexToHsv('nope')).toBe(null);
  });
});
