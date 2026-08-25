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

describe('hsvToHex() across the hue circle', () => {
  it('covers every sixth of the circle', () => {
    expect(hsvToHex(0, 1, 1)).toBe('#ff0000');    // 0-60
    expect(hsvToHex(90, 1, 1)).toBe('#80ff00');   // 60-120
    expect(hsvToHex(150, 1, 1)).toBe('#00ff80');  // 120-180
    expect(hsvToHex(210, 1, 1)).toBe('#0080ff');  // 180-240
    expect(hsvToHex(270, 1, 1)).toBe('#8000ff');  // 240-300
    expect(hsvToHex(330, 1, 1)).toBe('#ff0080');  // 300-360
  });

  it('wraps a hue outside 0-360', () => {
    expect(hsvToHex(360, 1, 1)).toBe(hsvToHex(0, 1, 1));
    expect(hsvToHex(-30, 1, 1)).toBe(hsvToHex(330, 1, 1));
  });

  it('clamps saturation and value into range', () => {
    expect(hsvToHex(0, 2, 2)).toBe(hsvToHex(0, 1, 1));
    expect(hsvToHex(0, -1, -1)).toBe('#000000');
  });

});

describe('hexToHsv() around the circle', () => {
  it('finds the hue whichever channel is brightest', () => {
    expect(hexToHsv('#ff0000').h).toBeCloseTo(0);     // red is max
    expect(hexToHsv('#00ff00').h).toBeCloseTo(120);   // green is max
    expect(hexToHsv('#0000ff').h).toBeCloseTo(240);   // blue is max
  });

  it('wraps a negative hue back into 0-360', () => {
    // Red is max and blue exceeds green, which computes a negative hue first.
    expect(hexToHsv('#ff00ff').h).toBeCloseTo(300);
  });

  it('reports no hue for greys', () => {
    expect(hexToHsv('#808080')).toMatchObject({ h: 0, s: 0 });
    expect(hexToHsv('#000000')).toMatchObject({ h: 0, s: 0, v: 0 });
  });

  it.each([null, undefined, ''])('treats %s as invalid rather than throwing', (input) => {
    expect(hexToHsv(input)).toBe(null);
    expect(isValidHexColor(input)).toBe(false);
    expect(tint(input)).toBe(tint('#64748b'));
  });
});
