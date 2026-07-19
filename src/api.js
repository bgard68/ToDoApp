// Barrel re-export. Implementation now lives in focused modules under ./lib:
//   apiClient (HTTP + auth), constants, colors, categories.
// Prefer importing from those modules directly; this keeps existing imports working.
export * from './lib/apiClient.js';
export * from './lib/constants.js';
export * from './lib/colors.js';
export * from './lib/categories.js';
