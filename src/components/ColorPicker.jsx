import { useState, useRef, useEffect } from 'react';
import { hsvToHex, hexToHsv, isValidHexColor } from '../lib/colors.js';

// Wheel is 156px; the handle geometry works in the same 0..156 coordinate space.
const WHEEL_RADIUS = 78;

/**
 * An accessible color picker: a swatch button that opens a popover holding a
 * hue/saturation wheel, a brightness slider, and a hex field. `value` is a
 * '#rrggbb' string and `onChange` is called with a new '#rrggbb' string.
 *
 * The wheel is a pointer/touch enhancement; keyboard and assistive-tech users
 * are fully covered by the labeled hex input and the native brightness range.
 * Escape closes the popover and restores focus to the trigger, and a click
 * outside closes it too.
 */
export default function ColorPicker({ value, onChange, label = 'Color' }) {
  const [open, setOpen] = useState(false);
  const [hsv, setHsv] = useState(() => hexToHsv(value) || { h: 210, s: 0.45, v: 0.9 });
  const [hexText, setHexText] = useState(value);

  const rootRef = useRef(null);
  const triggerRef = useRef(null);
  const wheelRef = useRef(null);
  const hexRef = useRef(null);
  const dragging = useRef(false);

  // Keep internal state in sync when `value` changes from outside (e.g. the parent
  // starts editing a different category). Skip the hex field while it's being typed.
  useEffect(() => {
    const parsed = hexToHsv(value);
    if (parsed && hsvToHex(hsv.h, hsv.s, hsv.v) !== value) setHsv(parsed);
    if (document.activeElement !== hexRef.current) setHexText(value);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [value]);

  // While open: Escape / outside-click close it, and focus lands in the popover.
  useEffect(() => {
    if (!open) return undefined;
    function onKey(e) {
      if (e.key === 'Escape') {
        setOpen(false);
        triggerRef.current?.focus();
      }
    }
    function onDown(e) {
      if (rootRef.current && !rootRef.current.contains(e.target)) setOpen(false);
    }
    document.addEventListener('keydown', onKey);
    document.addEventListener('mousedown', onDown);
    hexRef.current?.focus();
    return () => {
      document.removeEventListener('keydown', onKey);
      document.removeEventListener('mousedown', onDown);
    };
  }, [open]);

  function commit(next) {
    setHsv(next);
    onChange(hsvToHex(next.h, next.s, next.v));
  }

  function pickFromEvent(e) {
    const rect = wheelRef.current.getBoundingClientRect();
    const dx = e.clientX - rect.left - rect.width / 2;
    const dy = e.clientY - rect.top - rect.height / 2;
    const dist = Math.min(Math.hypot(dx, dy), WHEEL_RADIUS);
    const h = ((Math.atan2(dx, -dy) * 180) / Math.PI + 360) % 360;
    commit({ ...hsv, h, s: WHEEL_RADIUS ? dist / WHEEL_RADIUS : 0 });
  }

  function onWheelDown(e) {
    dragging.current = true;
    wheelRef.current.setPointerCapture?.(e.pointerId);
    pickFromEvent(e);
  }
  function onWheelMove(e) {
    if (dragging.current) pickFromEvent(e);
  }
  function onWheelUp() {
    dragging.current = false;
  }

  function onHexChange(e) {
    const text = e.target.value;
    setHexText(text);
    if (isValidHexColor(text)) {
      const hex = (text.startsWith('#') ? text : `#${text}`).toLowerCase();
      const parsed = hexToHsv(hex);
      if (parsed) {
        setHsv(parsed);
        onChange(hex);
      }
    }
  }

  const hex = hsvToHex(hsv.h, hsv.s, hsv.v);
  const angle = (hsv.h * Math.PI) / 180;
  const handleX = WHEEL_RADIUS + Math.sin(angle) * hsv.s * WHEEL_RADIUS;
  const handleY = WHEEL_RADIUS - Math.cos(angle) * hsv.s * WHEEL_RADIUS;

  return (
    <div className="color-picker" ref={rootRef}>
      <button
        type="button"
        ref={triggerRef}
        className="color-picker__trigger"
        aria-haspopup="dialog"
        aria-expanded={open}
        aria-label={`${label}: ${value}`}
        onClick={() => setOpen((o) => !o)}
      >
        <span className="color-picker__swatch" style={{ background: value }} />
        <span className="color-picker__caret" aria-hidden="true">▾</span>
      </button>

      {open && (
        <div className="color-picker__pop" role="dialog" aria-label={`Choose ${label.toLowerCase()}`}>
          <div
            className="color-picker__wheel"
            ref={wheelRef}
            onPointerDown={onWheelDown}
            onPointerMove={onWheelMove}
            onPointerUp={onWheelUp}
            aria-hidden="true"
          >
            <span className="color-picker__sat" />
            <span className="color-picker__val" style={{ opacity: 1 - hsv.v }} />
            <span className="color-picker__handle" style={{ left: handleX, top: handleY }} />
          </div>

          <label className="color-picker__bright">
            <span className="color-picker__bright-label">Brightness</span>
            <input
              type="range"
              min="0"
              max="100"
              step="1"
              value={Math.round(hsv.v * 100)}
              onChange={(e) => commit({ ...hsv, v: Number(e.target.value) / 100 })}
            />
          </label>

          <div className="color-picker__hexrow">
            <span className="color-picker__swatch" style={{ background: hex }} />
            <input
              ref={hexRef}
              type="text"
              className="color-picker__hex"
              value={hexText}
              maxLength={7}
              spellCheck={false}
              autoComplete="off"
              aria-label={`${label} hex value`}
              onChange={onHexChange}
            />
          </div>
        </div>
      )}
    </div>
  );
}
