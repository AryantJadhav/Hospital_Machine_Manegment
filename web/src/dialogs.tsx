import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react';
import type { ReactNode } from 'react';

/** A question with two answers. Resolves true for the confirm button and false for anything else (Cancel, Esc, a click outside). */
export type ConfirmOptions = {
  title: string;
  /** What will happen, in plain words. A string, or a few paragraphs and a list. */
  message?: ReactNode;
  confirmLabel?: string;
  cancelLabel?: string;
  /** For a step that cannot be undone: the confirm button is red. */
  danger?: boolean;
  /** For a step that cannot be undone: a tick box that must be ticked first, with these words beside it. */
  acknowledge?: string;
};

/** A question that needs words back. Resolves the text typed, or null when it is cancelled. */
export type AskOptions = {
  title: string;
  message?: ReactNode;
  label: string;
  placeholder?: string;
  confirmLabel?: string;
  cancelLabel?: string;
  danger?: boolean;
  /** Several lines, for a reason or a note. */
  multiline?: boolean;
  /** The least characters, after trimming. Default 1: an empty answer is not an answer. */
  minLength?: number;
  initial?: string;
  /** A button beside the box that fills it in, for a password someone would rather not invent. */
  suggest?: { label: string; make: () => string };
};

/** Something to read, with one button. */
export type TellOptions = {
  title: string;
  message?: ReactNode;
  /** A value to read out or copy: a new password, a code. Shown large, with a Copy button. */
  copyable?: string;
  okLabel?: string;
};

type Active = { id: number } & (
  | { kind: 'confirm'; options: ConfirmOptions; done: (answer: boolean) => void }
  | { kind: 'ask'; options: AskOptions; done: (answer: string | null) => void }
  | { kind: 'tell'; options: TellOptions; done: () => void }
);

export type DialogApi = {
  confirm: (options: ConfirmOptions) => Promise<boolean>;
  ask: (options: AskOptions) => Promise<string | null>;
  tell: (options: TellOptions) => Promise<void>;
};

const DialogContext = createContext<DialogApi | null>(null);

type DistributiveOmit<T, K extends keyof T> = T extends unknown ? Omit<T, K> : never;

/**
 * In-app replacements for the browser's own pop-up boxes, which look foreign, cannot be styled or explained, and
 * show a password in plain text. Each is a promise, so a call reads the way the pop-up did:
 *
 *     if (!(await dialogs.confirm({ title: 'Remove this photo?', danger: true }))) return;
 *
 * One at a time: a second request waits for the first to be answered.
 */
export function DialogProvider({ children }: { children: ReactNode }) {
  const [queue, setQueue] = useState<Active[]>([]);
  const nextId = useRef(1);

  const push = useCallback((item: DistributiveOmit<Active, 'id'>) => {
    setQueue((q) => [...q, { ...item, id: nextId.current++ } as Active]);
  }, []);
  const pop = useCallback(() => setQueue((q) => q.slice(1)), []);

  const api = useMemo<DialogApi>(
    () => ({
      confirm: (options) => new Promise((resolve) => push({ kind: 'confirm', options, done: resolve })),
      ask: (options) => new Promise((resolve) => push({ kind: 'ask', options, done: resolve })),
      tell: (options) => new Promise((resolve) => push({ kind: 'tell', options, done: () => resolve() })),
    }),
    [push],
  );

  const active = queue[0];

  return (
    <DialogContext.Provider value={api}>
      {children}
      {active && <Dialog key={active.id} active={active} onClose={pop} />}
    </DialogContext.Provider>
  );
}

// eslint-disable-next-line react-refresh/only-export-components
export function useDialogs(): DialogApi {
  const ctx = useContext(DialogContext);
  if (!ctx) throw new Error('useDialogs must be used inside a DialogProvider.');
  return ctx;
}

function Dialog({ active, onClose }: { active: Active; onClose: () => void }) {
  const ref = useRef<HTMLDialogElement>(null);
  const [text, setText] = useState(active.kind === 'ask' ? (active.options.initial ?? '') : '');
  const [ticked, setTicked] = useState(false);
  const [copied, setCopied] = useState(false);
  const options = active.options;
  const titleId = 'dialog-title';

  // The browser's own modal: it keeps the page behind it still, traps the keyboard inside, and closes on Esc.
  useEffect(() => {
    const el = ref.current;
    if (el && !el.open) el.showModal();
  }, []);

  function finish(answer: boolean | string | null) {
    if (active.kind === 'confirm') active.done(answer === true);
    else if (active.kind === 'ask') active.done(typeof answer === 'string' ? answer : null);
    else active.done();
    onClose();
  }

  const danger = 'danger' in options && options.danger;
  const needsTick = active.kind === 'confirm' && !!active.options.acknowledge && !ticked;
  const minLength = active.kind === 'ask' ? (active.options.minLength ?? 1) : 0;
  const tooShort = active.kind === 'ask' && text.trim().length < minLength;

  function submit() {
    if (active.kind === 'confirm') {
      if (!needsTick) finish(true);
    } else if (active.kind === 'ask') {
      if (!tooShort) finish(text.trim());
    } else {
      finish(true);
    }
  }

  async function copy() {
    if (active.kind !== 'tell' || !active.options.copyable) return;
    try {
      await navigator.clipboard.writeText(active.options.copyable);
      setCopied(true);
    } catch {
      // Some browsers refuse on a plain-HTTP address: the value is on screen, to be selected by hand.
      setCopied(false);
    }
  }

  const confirmLabel =
    'confirmLabel' in options && options.confirmLabel
      ? options.confirmLabel
      : active.kind === 'tell'
        ? ((options as TellOptions).okLabel ?? 'OK')
        : active.kind === 'ask'
          ? 'Save'
          : 'Yes';
  const cancelLabel = 'cancelLabel' in options && options.cancelLabel ? options.cancelLabel : 'Cancel';

  return (
    <dialog
      ref={ref}
      className="dialog"
      aria-labelledby={titleId}
      onCancel={(e) => {
        // Esc: the same as Cancel, answered, not left hanging.
        e.preventDefault();
        finish(active.kind === 'confirm' ? false : null);
      }}
      onClick={(e) => {
        // A click on the dimmed area outside the box is a cancel; a click inside is not.
        if (e.target === ref.current && active.kind !== 'tell') finish(active.kind === 'confirm' ? false : null);
      }}
    >
      <form
        className="dialog-body"
        onSubmit={(e) => {
          e.preventDefault();
          submit();
        }}
      >
        <h2 id={titleId} className="dialog-title">{options.title}</h2>

        {options.message && <div className="dialog-message">{options.message}</div>}

        {active.kind === 'confirm' && active.options.acknowledge && (
          <label className="dialog-tick">
            <input type="checkbox" checked={ticked} onChange={(e) => setTicked(e.target.checked)} />
            <span>{active.options.acknowledge}</span>
          </label>
        )}

        {active.kind === 'ask' && (
          <label className="field">
            <span>{active.options.label}</span>
            {active.options.multiline ? (
              <textarea
                className="field"
                rows={3}
                autoFocus
                maxLength={500}
                placeholder={active.options.placeholder}
                value={text}
                onChange={(e) => setText(e.target.value)}
              />
            ) : (
              <input
                className="field"
                autoFocus
                autoComplete="off"
                placeholder={active.options.placeholder}
                value={text}
                onChange={(e) => setText(e.target.value)}
              />
            )}
          </label>
        )}

        {active.kind === 'ask' && active.options.suggest && (
          <div>
            <button type="button" className="btn btn-quiet" onClick={() => setText(active.options.suggest!.make())}>
              {active.options.suggest.label}
            </button>
          </div>
        )}

        {active.kind === 'tell' && active.options.copyable && (
          <div className="dialog-copy">
            <code className="dialog-code">{active.options.copyable}</code>
            <button type="button" className="btn" onClick={() => void copy()}>{copied ? 'Copied' : 'Copy'}</button>
          </div>
        )}

        <div className="dialog-actions">
          {active.kind !== 'tell' && (
            <button type="button" className="btn" onClick={() => finish(active.kind === 'confirm' ? false : null)}>
              {cancelLabel}
            </button>
          )}
          <button
            type="submit"
            className={danger ? 'btn btn-danger' : 'btn btn-primary'}
            disabled={needsTick || tooShort}
            // The safe answer has the focus on a question about something that cannot be undone.
            autoFocus={active.kind === 'tell' || (active.kind === 'confirm' && !danger)}
          >
            {confirmLabel}
          </button>
        </div>
      </form>
    </dialog>
  );
}

/** A readable temporary password that meets the rules (upper case, lower case, a number): "Blue-Tiger-4827". */
export function suggestPassword(): string {
  const words = ['Blue', 'Green', 'Red', 'Silver', 'Golden', 'Brave', 'Calm', 'Swift', 'Tiger', 'Eagle', 'River', 'Mango', 'Lotus', 'Cedar'];
  const pick = () => words[crypto.getRandomValues(new Uint32Array(1))[0] % words.length];
  const number = 1000 + (crypto.getRandomValues(new Uint32Array(1))[0] % 9000);
  return `${pick()}-${pick()}-${number}`;
}
