import { useEffect, useRef } from 'react';

const CLIENT_ID = import.meta.env.VITE_GOOGLE_CLIENT_ID;
const SCRIPT_SRC = 'https://accounts.google.com/gsi/client';

/**
 * Renders Google's "Sign in with Google" button using Google Identity Services.
 * On success it hands the returned ID token (credential) to `onCredential`.
 * Renders nothing actionable until VITE_GOOGLE_CLIENT_ID is configured.
 */
export default function GoogleButton({ onCredential }) {
  const containerRef = useRef(null);
  const callbackRef = useRef(onCredential);
  callbackRef.current = onCredential;

  useEffect(() => {
    if (!CLIENT_ID) return;

    function init() {
      if (!window.google?.accounts?.id || !containerRef.current) return;
      window.google.accounts.id.initialize({
        client_id: CLIENT_ID,
        callback: (response) => callbackRef.current?.(response.credential),
      });
      window.google.accounts.id.renderButton(containerRef.current, {
        theme: 'outline',
        size: 'large',
        width: 320,
        text: 'continue_with',
      });
    }

    const existing = document.querySelector(`script[src="${SCRIPT_SRC}"]`);
    if (existing) {
      init();
      return;
    }

    const script = document.createElement('script');
    script.src = SCRIPT_SRC;
    script.async = true;
    script.defer = true;
    script.onload = init;
    document.body.appendChild(script);
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
