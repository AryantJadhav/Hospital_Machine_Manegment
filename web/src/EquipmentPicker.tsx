import { useEffect, useId, useRef, useState } from 'react';
import type { KeyboardEvent } from 'react';
import { api } from './api/client';
import { EQUIPMENT_LABEL } from './statusTones';

type Machine = {
  id: number;
  assetTag: string;
  equipmentTypeName: string;
  locationName: string;
  status: number;
};

type Page = { items: Machine[]; total: number };

const SHOWN = 8;

function describe(m: Machine): string {
  return `${m.assetTag} — ${m.equipmentTypeName}`;
}

/**
 * Finds one machine by typing part of its tag, serial, make or model.
 *
 * Replaces a dropdown that loaded the first 200 machines and offered nothing
 * else. Past 200 the rest of the register could not be chosen at all, and even
 * below that nobody scrolls a list of hundreds to find a sticker's number. The
 * search runs on the server, so it is the same at 20 machines and at 20,000.
 *
 * A combobox in the ARIA sense: arrow keys move through the results, Enter
 * picks one, Escape closes the list, and a screen reader is told which is
 * current. Choosing sets the value; typing again clears it, so the form never
 * submits a machine that no longer matches what is on screen.
 */
export function EquipmentPicker({
  value,
  onChange,
  label = 'Machine',
  initialLabel = '',
}: {
  value: number | null;
  onChange: (id: number | null) => void;
  label?: string;
  /** What the box says on arrival, when the machine is already known. */
  initialLabel?: string;
}) {
  const id = useId();
  const listId = `${id}-list`;
  const root = useRef<HTMLDivElement>(null);
  const latest = useRef(0);

  const [text, setText] = useState(initialLabel);
  const [open, setOpen] = useState(false);
  const [results, setResults] = useState<Machine[]>([]);
  const [total, setTotal] = useState(0);
  const [active, setActive] = useState(0);
  const [state, setState] = useState<'idle' | 'loading' | 'failed'>('idle');

  // Search when the list is open and the text changes. Debounced so typing
  // "vent-0" is not six requests, and numbered so a slow early answer cannot
  // overwrite a quicker later one.
  useEffect(() => {
    if (!open) return;

    const handle = window.setTimeout(async () => {
      const mine = ++latest.current;
      setState('loading');
      try {
        const q = new URLSearchParams({ pageSize: String(SHOWN) });
        if (text.trim()) q.set('q', text.trim());
        const page = await api.get<Page>(`/api/equipment?${q}`);
        if (mine !== latest.current) return;
        setResults(page.items);
        setTotal(page.total);
        setActive(0);
        setState('idle');
      } catch {
        if (mine !== latest.current) return;
        setResults([]);
        setTotal(0);
        setState('failed');
      }
    }, text.trim() ? 200 : 0);

    return () => window.clearTimeout(handle);
  }, [text, open]);

  // Close when a click lands anywhere else.
  useEffect(() => {
    if (!open) return;
    const onPointer = (e: MouseEvent) => {
      if (root.current && !root.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener('mousedown', onPointer);
    return () => document.removeEventListener('mousedown', onPointer);
  }, [open]);

  function choose(m: Machine) {
    setText(describe(m));
    setOpen(false);
    onChange(m.id);
  }

  function onKeyDown(e: KeyboardEvent<HTMLInputElement>) {
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      if (!open) setOpen(true);
      else setActive((a) => Math.min(a + 1, Math.max(results.length - 1, 0)));
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      setActive((a) => Math.max(a - 1, 0));
    } else if (e.key === 'Enter' && open && results[active]) {
      // Picking, not submitting the form behind it.
      e.preventDefault();
      choose(results[active]);
    } else if (e.key === 'Escape' && open) {
      e.preventDefault();
      setOpen(false);
    }
  }

  const more = total > results.length;

  return (
    <div className="picker" ref={root}>
      <label htmlFor={id}>{label}</label>
      <input
        id={id}
        className="field"
        role="combobox"
        aria-expanded={open}
        aria-controls={listId}
        aria-autocomplete="list"
        aria-activedescendant={open && results[active] ? `${id}-${results[active].id}` : undefined}
        autoComplete="off"
        placeholder="Type an asset tag, serial number, make or model"
        value={text}
        onFocus={() => setOpen(true)}
        onChange={(e) => {
          setText(e.target.value);
          setOpen(true);
          if (value !== null) onChange(null);
        }}
        onKeyDown={onKeyDown}
      />

      {open && (
        <div className="picker-pop">
          <ul id={listId} role="listbox" aria-label={`${label} matches`} className="picker-list">
            {results.map((m, i) => (
              <li
                key={m.id}
                id={`${id}-${m.id}`}
                role="option"
                aria-selected={i === active}
                className={i === active ? 'picker-option active' : 'picker-option'}
                // mousedown, not click: the input would lose focus and the list
                // close before a click ever landed.
                onMouseDown={(e) => {
                  e.preventDefault();
                  choose(m);
                }}
                onMouseEnter={() => setActive(i)}
              >
                <span className="mono">{m.assetTag}</span>
                <span>{m.equipmentTypeName}</span>
                <span className="muted">
                  {m.locationName}
                  {/* Said only when it is not the ordinary case, and only when
                      it is a status we know: a machine the ward is reporting
                      as broken is usually In use, and "In use" on
                      every row would be noise. */}
                  {m.status !== 20 && EQUIPMENT_LABEL[m.status] && ` · ${EQUIPMENT_LABEL[m.status]}`}
                </span>
              </li>
            ))}
          </ul>

          <p className="picker-hint muted" role="status">
            {state === 'loading' && results.length === 0 && 'Searching…'}
            {state === 'failed' && 'Could not search the register. Try again.'}
            {state === 'idle' && results.length === 0 && 'No machine matches that.'}
            {state !== 'failed' && results.length > 0 && more &&
              `Showing ${results.length} of ${total.toLocaleString('en-IN')}. Keep typing to narrow it down.`}
          </p>
        </div>
      )}
    </div>
  );
}
