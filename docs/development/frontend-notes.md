# Frontend & UI engineering notes

App-side engineering lessons from building the React (Vite) client — the ones about
UI behavior and component design rather than deployment. For the deployment, Azure, CI/CD,
database, and config gotchas, see **[Lessons learned](../lessons.md)**.

---

## Frontend (React + Vite)

- **The "post back" when moving a card was a full reload, not a real page post.** Dragging a card between lanes called the API and then re-fetched the whole board, so the UI visibly flashed/reset — it *looked* like a postback. The fix is **optimistic UI**: `useTodos.moveCard` updates local state immediately, then reconciles just the affected card from the server response, and only falls back to a full `reload()` **on error**. The board never flashes on the happy path, and a failed move rolls back cleanly.
- **Keep the optimistic update and the server reconcile in one place.** All todo mutations (move, create, update, delete) live in the `useTodos` hook, not in the view. The view (`KanbanBoard`) just renders and calls hook methods — so the optimistic/rollback logic is written once and is unit-testable.
- **On error, reload *before* setting the error message.** `reload()` calls `setError('')` internally, so if you set the error first and reload after, the reload wipes your message. Order matters: `await reload(); setError(err.message);` — otherwise the user never sees why the action failed. (This is exactly the bug the `useTodos` "reverts by reloading when a move fails" test now guards.)
- **`VITE_*` env vars are build-time**, so anything the SPA needs from config (`VITE_API_URL`, `VITE_GOOGLE_CLIENT_ID`) must be set as GitHub **repository Variables** and baked in at build — see the production-500 triage in [lessons.md](../lessons.md#diagnosing-a-500--failed-request-in-production) for how a wrong `VITE_API_URL` shows up.

## Mobile drag-and-drop — the native HTML5 DnD API is touch-blind

**Symptom:** dragging a card between lanes works fine in a desktop browser but does nothing on a phone browser — the cards can't be picked up at all.

**Root cause:** the board was built entirely on the **native HTML5 Drag and Drop API** — `draggable` cards with `onDragStart` + `e.dataTransfer`, and `onDrop` lanes — with no touch fallback. Those `dragstart` / `dragover` / `drop` events are **mouse-only**; mobile browsers don't synthesize them from touch gestures, so on a phone nothing fires. It's a known limitation of the API, not a device or deploy problem.

**Options considered:**

1. **Switch to a touch-aware drag library** (`@dnd-kit`, or `react-dnd` with a touch backend). Built on Pointer Events, so real drag-and-drop works with mouse, touch, *and* keyboard, and it's accessible. The proper long-term fix, but a larger rewrite of the board's drag wiring plus a new dependency.
2. **Add a tap-to-move control** — a small button on each card that opens the other lanes as tap targets and calls the existing `moveCard(id, status)`. Works on touch and mouse, tiny change, no new dependency. **[chosen]**
3. **A touch polyfill** (`mobile-drag-drop`) that shims HTML5 DnD onto touch events. Smallest code change, but janky and less reliable.

**Decision — option 2 (tap-to-move), because:**

- It's the smallest, lowest-risk change: two component files, no new dependency, `package.json` untouched.
- It **reuses `moveCard()`**, so a tap-move keeps the same optimistic update + concurrency handling as a drag — one code path for every device and every method.
- It works **identically on phone and desktop**, because the control is a plain button and a button's `click` fires the same on a finger tap as a mouse click — which is exactly why it sidesteps the mouse-only DnD limitation.
- Dragging on a small screen is fiddly even when it works, so tap-to-pick-a-lane is arguably *better* mobile UX than real drag.
- Native drag on desktop is left untouched, so desktop users get the tap control *in addition to* dragging.

**Implementation:** `TaskCard.jsx` gained a ⇄ "move" button that reveals the other lanes (excluding the card's current one, each calling `onMove(todo.id, status)`); `Lane.jsx` passes `onMove={onDropCard}` (i.e. `moveCard`) down to each card. No change needed in `KanbanBoard`.

**Trade-off accepted:** actual *dragging* still doesn't work on touch — only the tap control does. If true drag-on-touch is ever wanted, revisit option 1 (`@dnd-kit`).

## Date input — native segments can't backspace across; a typed mask fixes it

**Symptom:** editing a due date, Backspace only cleared the segment the caret was on (month, day, or year) and wouldn't carry across — from the year you couldn't backspace back into the month, and clearing the field entirely was awkward.

**Root cause:** the native `<input type="date">` is **segmented** (`mm | dd | yyyy`), and each segment is its own edit context — the browser owns that behavior, so Backspace/Delete are per-segment by design. A controlled React binding made partial edits worse: clearing a segment left the value momentarily "incomplete", the input reported empty, `onChange` fired with `''`, and React reset the field. There's no way to get continuous editing out of the native control.

**Fix:** a small reusable **`DateField`** component that replaces the native input with a masked **text** field:

- Typing digits auto-inserts the `/` separators (`07192026` → `07/19/2026`), and the caret is preserved across reformatting so **Backspace and Delete flow across the whole field** (year → month, through the slashes) and it can be cleared entirely.
- It validates on completion — an impossible date like `02/30` is treated as empty.
- A **mini-calendar button sits inside the bar**; clicking it opens the browser's native picker via `HTMLInputElement.showPicker()` (with a focus fallback), and picking a day fills the bar.
- It takes and emits a plain `yyyy-mm-dd` string, so the surrounding form/save logic is unchanged.

**Lesson:** native `<input type="date">` is great for free validation and the calendar, but its segmented editing can't be made to behave like free text. When continuous typed editing matters, own the input as a masked text field (and re-add the calendar via `showPicker()`) rather than fighting the native control. Used in `TodoForm` and `TaskCard`.

## Dark/light mode — mobile browsers force-darken a light-only page

**Symptom:** on a phone browser, choosing **light** mode on the login page didn't produce a
light page — it stayed dark. Desktop browsers were fine, and the in-app toggle worked there.

**Root cause:** the app declared **`color-scheme: light`** (light-only) on `:root`. Chrome for
Android's *Auto dark theme* and Samsung Internet's dark mode treat a light-only page as "not
dark-aware" and **algorithmically invert it at paint time** — on top of the app's own theme — so
the app's "light" choice could never win. The signal those engines look for (the `color-scheme`
value, and a `<meta name="color-scheme">`) was missing or wrong: the only `color-scheme` lived in
`index.css`, which is imported by JS (`main.jsx`) and so arrives *after* first paint, and it said
`light`. The toggle logic itself was never at fault.

**How it was confirmed:** reproduced in headless Chromium at a 390px phone viewport with
`prefers-color-scheme: dark` and Chrome's real auto-dark emulation
(`Emulation.setAutoDarkModeOverride`). With auto-dark **on**, tapping "light" rendered dark pixels
(`rgb(34,35,36)`) even though the computed CSS background was the light value (`rgb(244,245,247)`) —
the inversion happens in the compositor, after computed style. With auto-dark **off**, light
rendered correctly, proving the toggle was fine. A first attempt at a fix (a `<meta name="color-scheme">`
alone) did **not** work, because the CSS `:root { color-scheme: light }` overrides the meta — a good
reminder to reproduce before shipping a fix.

![Login page at a phone viewport, OS dark mode + browser auto-dark on. Before: tapping "light" stays dark. After: light renders.](images/login-dark-mode-before-after.png)

**Fix:** declare the page **dark-capable** so the browser stops force-darkening it:

- `src/index.css` — change the base `:root` from `color-scheme: light` to **`color-scheme: light dark`**. This is the load-bearing change; it tells the browser the page manages its own dark mode.
- `index.html` — add **`<meta name="color-scheme" content="light dark" />`** in `<head>`, so the signal exists *before* the JS-injected stylesheet loads (also reduces the load flash).

The toggle logic, the dark palette, and the system-dark media query were unchanged.

**Trade-off accepted:** with `color-scheme: light dark`, if the OS is in dark mode but the user
picks the app's *light* theme, native UA-drawn controls (scrollbars, autofill highlight, caret, the
date picker) may follow the system's dark preference. The app's own surfaces (background, card,
text) stay light because they come from CSS custom properties. Negligible on the login page; only
worth watching on the board's date field.

**Lesson:** a page that supports both themes must advertise `color-scheme: light dark` (and must not
be overridden to light-only anywhere), or mobile auto-dark will invert it regardless of the app's
own toggle. A `<meta name="color-scheme">` won't help on its own if a CSS `:root { color-scheme: light }`
overrides it — the CSS value wins.

## Google sign-in button didn't follow the app theme

**Symptom:** in dark mode the "Sign in with Google" button stayed a bright **white** pill, clashing
with the dark card.

**Root cause:** the button is a **Google-rendered widget** (Google Identity Services). The app's CSS
variables can't restyle it — the only lever is the GIS `theme` option, which was **hardcoded to
`outline`** (the light variant) and read once at mount, so it never tracked the app theme. GIS
offers `outline`, `filled_blue`, and `filled_black`; none of them follow `prefers-color-scheme`.

**Fix (contained to `GoogleButton.jsx`):**

- Derive the theme from the current mode — `theme: dark ? 'filled_black' : 'outline'` — computing
  `dark` the same way `ThemeToggle` does (`<html data-theme>`, falling back to `matchMedia`).
- Because GIS won't restyle an already-rendered button, a **`MutationObserver`** on
  `document.documentElement`'s `data-theme` clears the container and calls `renderButton` again when
  the theme flips (plus a `matchMedia` listener for the system-preference case). Clear the container
  first, or you get a stacked/duplicate button.

**Lesson:** third-party rendered widgets don't inherit your theme via CSS — pass their own theme
option and re-render them when your theme changes. (Verifiable only with a real `VITE_GOOGLE_CLIENT_ID`
against a running app, since the button is Google-rendered.)
