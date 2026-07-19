import { useEffect, useRef } from 'react';

// helpers: convert between the display mask (MM/DD/YYYY) and the ISO value (yyyy-mm-dd)
const isoToMask = (iso) => {
  if (!iso) return '';
  const [y, m, d] = iso.split('-');
  return y && m && d ? `${m}/${d}/${y}` : '';
};
const formatDigits = (d) => {
  d = d.slice(0, 8);
  if (d.length > 4) return `${d.slice(0, 2)}/${d.slice(2, 4)}/${d.slice(4)}`;
  if (d.length > 2) return `${d.slice(0, 2)}/${d.slice(2)}`;
  return d;
};
const maskToIso = (digits) => {
  if (digits.length !== 8) return '';
  const mm = digits.slice(0, 2), dd = digits.slice(2, 4), yyyy = digits.slice(4);
  const dt = new Date(Number(yyyy), Number(mm) - 1, Number(dd));
  const ok =
    Number(mm) >= 1 && Number(mm) <= 12 &&
    dt.getMonth() === Number(mm) - 1 &&
    dt.getDate() === Number(dd) &&
    dt.getFullYear() === Number(yyyy);
  return ok ? `${yyyy}-${mm}-${dd}` : '';
};

/**
 * A typed date bar: enter MM/DD/YYYY with slashes auto-inserted, backspace/delete flow across
 * the whole field, and a mini-calendar button *inside* the bar opens the browser picker.
 * Takes and emits a plain `yyyy-mm-dd` string (or '') so parent save logic is unchanged.
 */
export default function DateField({ value = '', onChange, ariaLabel = 'Date' }) {
  const inputRef = useRef(null);
  const nativeRef = useRef(null);

  // Reflect external value changes (form reset, calendar pick) into the uncontrolled bar.
  useEffect(() => {
    const el = inputRef.current;
    if (!el) return;
    if (maskToIso(el.value.replace(/\D/g, '')) !== value) {
      el.value = isoToMask(value);
    }
    if (nativeRef.current) nativeRef.current.value = value || '';
  }, [value]);

  const handleInput = () => {
    const el = inputRef.current;
    if (!el) return;
    const caret = el.selectionStart == null ? el.value.length : el.selectionStart;
    const digitsBefore = el.value.slice(0, caret).replace(/\D/g, '').length;
    const digits = el.value.replace(/\D/g, '').slice(0, 8);
    const out = formatDigits(digits);
    el.value = out;
    // restore the caret after the same number of digits (keeps backspace/mid-edits sane)
    let pos = 0, seen = 0;
    while (pos < out.length && seen < digitsBefore) { if (/\d/.test(out[pos])) seen += 1; pos += 1; }
    try { el.setSelectionRange(pos, pos); } catch { /* ignore */ }
    const iso = maskToIso(digits);
    if (nativeRef.current) nativeRef.current.value = iso;
    onChange(iso);
  };

  const openPicker = () => {
    const el = nativeRef.current;
    if (!el) return;
    try { el.showPicker(); } catch { el.focus(); }
  };
  const handlePick = (e) => {
    const iso = e.target.value;
    if (inputRef.current) inputRef.current.value = isoToMask(iso);
    onChange(iso);
  };

  return (
    <div className="date-field">
      <input
        ref={inputRef}
        className="date-mask"
        type="text"
        inputMode="numeric"
        placeholder="MM/DD/YYYY"
        autoComplete="off"
        defaultValue={isoToMask(value)}
        onChange={handleInput}
        aria-label={ariaLabel}
      />
      <button type="button" className="cal-btn" onClick={openPicker} aria-label="Open calendar" title="Open calendar">
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.9" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
          <rect x="3" y="4.5" width="18" height="17" rx="2.5" />
          <path d="M3 9.5h18" /><path d="M8 3v3" /><path d="M16 3v3" />
        </svg>
      </button>
      <input ref={nativeRef} className="date-native" type="date" tabIndex={-1} aria-hidden="true" defaultValue={value} onChange={handlePick} />
    </div>
  );
}
