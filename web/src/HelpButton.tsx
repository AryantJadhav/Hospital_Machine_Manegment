import { useEffect, useRef, useState } from 'react';
import { Link, useLocation } from 'react-router-dom';
import { helpForPath } from './helpText';

/**
 * A Help button in the top bar of every page. It opens a short box that says what the page in front of the person is
 * for and what they can do on it, in plain words, and points to the Help page for the everyday jobs and the words used.
 * It is a button, not a manual: three or four lines, so it is read.
 */
export function HelpButton() {
  const { pathname } = useLocation();
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDialogElement>(null);
  const help = helpForPath(pathname);

  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    if (open && !el.open) el.showModal();
    if (!open && el.open) el.close();
  }, [open]);

  // Moving to another page closes it, so it never describes a page that is no longer there.
  useEffect(() => {
    setOpen(false);
  }, [pathname]);

  return (
    <>
      <button
        className="btn btn-quiet help-button"
        title="What is this page for?"
        aria-haspopup="dialog"
        onClick={() => setOpen(true)}
      >
        <span aria-hidden="true" className="help-mark">?</span> Help
      </button>

      <dialog
        ref={ref}
        className="dialog"
        aria-labelledby="help-title"
        onClose={() => setOpen(false)}
        onClick={(e) => {
          if (e.target === ref.current) setOpen(false);
        }}
      >
        <div className="dialog-body">
          <h2 id="help-title" className="dialog-title">{help.title}</h2>
          <div className="dialog-message">
            <p>{help.purpose}</p>
            {help.can.length > 0 && (
              <ul>
                {help.can.map((line) => <li key={line}>{line}</li>)}
              </ul>
            )}
          </div>
          <div className="dialog-actions">
            <Link className="btn" to="/help" onClick={() => setOpen(false)}>Everyday jobs and the words we use</Link>
            <button className="btn btn-primary" autoFocus onClick={() => setOpen(false)}>Got it</button>
          </div>
        </div>
      </dialog>
    </>
  );
}
