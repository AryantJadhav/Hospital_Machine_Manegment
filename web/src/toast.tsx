import { createContext, useCallback, useContext, useMemo, useRef, useState } from 'react';
import type { ReactNode } from 'react';

type Tone = 'ok' | 'info' | 'error';
type Toast = { id: number; message: string; tone: Tone };

type ToastApi = {
  /** Say something short at the top right of the screen. It goes away by itself. */
  show: (message: string, tone?: Tone) => void;
};

const ToastContext = createContext<ToastApi | null>(null);

/** How long a message stays. Long enough to read a sentence, short enough not to need closing. */
const SHOW_FOR_MS = 6000;

/**
 * A small message at the right of the screen, for what a person has just done: "Request submitted".
 * It stays above whatever page they land on, so it can be shown and the page changed in one step.
 */
export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<Toast[]>([]);
  const next = useRef(1);

  const dismiss = useCallback((id: number) => {
    setToasts((all) => all.filter((t) => t.id !== id));
  }, []);

  const show = useCallback(
    (message: string, tone: Tone = 'ok') => {
      const id = next.current++;
      setToasts((all) => [...all, { id, message, tone }]);
      window.setTimeout(() => dismiss(id), SHOW_FOR_MS);
    },
    [dismiss],
  );

  const api = useMemo(() => ({ show }), [show]);

  return (
    <ToastContext.Provider value={api}>
      {children}
      {/* Announced politely: a screen reader says it without cutting off what it was reading. */}
      <div className="toast-stack" role="status" aria-live="polite">
        {toasts.map((t) => (
          <div key={t.id} className={`toast toast-${t.tone}`}>
            <span>{t.message}</span>
            <button className="toast-close" aria-label="Dismiss" onClick={() => dismiss(t.id)}>×</button>
          </div>
        ))}
      </div>
    </ToastContext.Provider>
  );
}

// eslint-disable-next-line react-refresh/only-export-components
export function useToast(): ToastApi {
  const ctx = useContext(ToastContext);
  if (!ctx) throw new Error('useToast must be used inside a ToastProvider.');
  return ctx;
}
