import { DEFAULT_CATEGORY_COLOR } from './constants.js';

// Parse a #RRGGBB hex string to [r, g, b]. Returns null if it isn't a valid 6-digit hex.
function hexToRgb(hex) {
  const match = /^#?([0-9a-f]{6})$/i.exec((hex || '').trim());
  if (!match) return null;
  const int = parseInt(match[1], 16);
  return [(int >> 16) & 255, (int >> 8) & 255, int & 255];
}

// Mix a color toward white by `amount` (0 = original, 1 = white). Used for the soft
// post-it fill so the category color stays legible behind dark text.
export function tint(hex, amount = 0.7) {
  const rgb = hexToRgb(hex);
  if (!rgb) return tint(DEFAULT_CATEGORY_COLOR, amount);
  const mixed = rgb.map((c) => Math.round(c + (255 - c) * amount));
  return `rgb(${mixed[0]}, ${mixed[1]}, ${mixed[2]})`;
}
