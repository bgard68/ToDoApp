import { DEFAULT_CATEGORY_COLOR } from './constants.js';

const HEX_RE = /^#?([0-9a-f]{6})$/i;

// Parse a #RRGGBB hex string to [r, g, b]. Returns null if it isn't a valid 6-digit hex.
function hexToRgb(hex) {
  const match = HEX_RE.exec((hex || '').trim());
  if (!match) return null;
  const int = parseInt(match[1], 16);
  return [(int >> 16) & 255, (int >> 8) & 255, int & 255];
}

// True when `hex` is a 6-digit hex color, with or without a leading '#'.
export function isValidHexColor(hex) {
  return HEX_RE.test((hex || '').trim());
}

// Convert HSV (h in [0, 360), s and v in [0, 1]) to a lowercase '#rrggbb' string —
// the exact shape the API's color validator (^#([0-9A-Fa-f]{6})$) expects.
export function hsvToHex(h, s, v) {
  h = ((h % 360) + 360) % 360;
  s = Math.min(1, Math.max(0, s));
  v = Math.min(1, Math.max(0, v));
  const c = v * s;
  const x = c * (1 - Math.abs(((h / 60) % 2) - 1));
  const m = v - c;
  let r = 0;
  let g = 0;
  let b = 0;
  if (h < 60) { r = c; g = x; }
  else if (h < 120) { r = x; g = c; }
  else if (h < 180) { g = c; b = x; }
  else if (h < 240) { g = x; b = c; }
  else if (h < 300) { r = x; b = c; }
  else { r = c; b = x; }
  const to = (n) => ('0' + Math.round((n + m) * 255).toString(16)).slice(-2);
  return `#${to(r)}${to(g)}${to(b)}`;
}

// Convert a '#rrggbb' string to { h, s, v }. Returns null for invalid input.
export function hexToHsv(hex) {
  const rgb = hexToRgb(hex);
  if (!rgb) return null;
  const [r, g, b] = rgb.map((c) => c / 255);
  const max = Math.max(r, g, b);
  const min = Math.min(r, g, b);
  const d = max - min;
  let h = 0;
  if (d) {
    if (max === r) h = 60 * (((g - b) / d) % 6);
    else if (max === g) h = 60 * ((b - r) / d + 2);
    else h = 60 * ((r - g) / d + 4);
  }
  if (h < 0) h += 360;
  return { h, s: max ? d / max : 0, v: max };
}

// Mix a color toward white by `amount` (0 = original, 1 = white). Used for the soft
// post-it fill so the category color stays legible behind dark text.
export function tint(hex, amount = 0.7) {
  const rgb = hexToRgb(hex);
  if (!rgb) return tint(DEFAULT_CATEGORY_COLOR, amount);
  const mixed = rgb.map((c) => Math.round(c + (255 - c) * amount));
  return `rgb(${mixed[0]}, ${mixed[1]}, ${mixed[2]})`;
}
