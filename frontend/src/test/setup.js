import '@testing-library/jest-dom';

// jsdom doesn't implement matchMedia; stub it so theme-aware components render in tests.
if (!window.matchMedia) {
  window.matchMedia = (query) => ({
    matches: false, media: query, onchange: null,
    addEventListener() {}, removeEventListener() {},
    addListener() {}, removeListener() {}, dispatchEvent() { return false; },
  });
}
