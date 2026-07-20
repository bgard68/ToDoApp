import { useEffect, useRef } from 'react';

const CLIENT_ID = import.meta.env.VITE_GOOGLE_CLIENT_ID;
const SCRIPT_SRC = 'https://accounts.google.com/gsi/client';

/**
 * Current theme, mirroring ThemeToggle: an explicit choice on `<html data-theme>`
 * wins, otherwise fall back to the OS setting.
 */
function isDarkTheme() {
  const t = document.documentElement.getAttribute('data-theme');
  if (t === 'dark') return true;
  if (t === 'light') return false;
  try {
    return window.matchMedia('(prefers-color-scheme: dark)').matches;
  } catch {
    return false;
  }
}

/**
 * Renders Google's "Sign in with Google" button using Google Identity Services.
 * On success it hands the returned ID token (credential) to `onCredential`.
 * Renders nothing actionable until VITE_GOOGLE_CLIENT_ID is configured.
 */
export default function GoogleButton({ onCredential }) {
  const containerRef = useRef(null);
  const callbackRef = useRef(onCredential);
  callbackRef.current = onCredential;
  const widthRef = useRef(0); // stable button width, so theme re-renders don't jitter its size

  useEffect(() => {
    if (!CLIENT_ID) return;

    let initialized = false;

    // (Re)draw the button in the current theme. Google won't restyle an already
    // rendered button, so we clear the container and render again each time.
    function renderButton() {
      const el = containerRef.current;
      if (!window.google?.accounts?.id || !el) return;
      // Measure a STABLE width from the surrounding box (not the button's own
      // container, which is empty mid-redraw) and cache it, so re-rendering on a
      // theme toggle always produces the same size (200–400) instead of jittering.
      const box =
        el.parentElement?.getBoundingClientRect().width ||
        el.getBoundingClientRect().width ||
        0;
      if (box) widthRef.current = Math.max(240, Math.min(400, Math.round(box)));
      const width = widthRef.current || 320;
      el.innerHTML = '';
      window.google.accounts.id.renderButton(el, {
        theme: isDarkTheme() ? 'filled_black' : 'outline',
        size: 'large',
        width,
        text: 'continue_with',
      });
    }

    function init() {
      if (!window.google?.accounts?.id || !containerRef.current) return;
      if (!initialized) {
        window.google.accounts.id.initialize({
          client_id: CLIENT_ID,
          callback: (response) => callbackRef.current?.(response.credential),
        });
        initialized = true;
      }
      renderButton();
    }

    const existing = document.querySelector(`script[src="${SCRIPT_SRC}"]`);
    if (existing) {
      init();
    } else {
      const script = document.createElement('script');
      script.src = SCRIPT_SRC;
      script.async = true;
      script.defer = true;
      script.onload = init;
      document.body.appendChild(script);
    }

    // Re-render on theme change: the toggle flips `<html data-theme>`, and the OS
    // preference can change when the user hasn't made an explicit choice.
    const observer = new MutationObserver(renderButton);
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });

    let media = null;
    const onMedia = () => renderButton();
    try {
      media = window.matchMedia('(prefers-color-scheme: dark)');
      media.addEventListener('change', onMedia);
    } catch {
      media = null;
    }

    return () => {
      observer.disconnect();
      if (media) media.removeEventListener('change', onMedia);
    };
  }, []);

  if (!CLIENT_ID) {
    return (
      <p className="auth__demo">
        Set <code>VITE_GOOGLE_CLIENT_ID</code> to enable Google sign-in.
      </p>
    );
  }

  return <div className="auth__google" ref={containerRef} />;
}
