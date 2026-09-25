import { useEffect, useState } from 'react';
import { api } from '../api/client';
import { formatDate } from '../time';
import { CUSTOM_FREQUENCY, MAX_MANUAL_DATES } from '../pmSchedule';

/** The preview of the dates a PM schedule choice means, on the Add a machine form. */

/**
 * The dates a choice means, in the first year, worked out by the server with the same
 * code that generates the real ones. "Quarterly" is shown as the four dates it makes
 * rather than trusted as a word.
 */
export function PmDatePreview({ frequency, anchor }: { frequency: number | null; anchor: string }) {
  const usable = frequency !== null && frequency !== CUSTOM_FREQUENCY && Boolean(anchor);
  const key = `${frequency}|${anchor}`;
  const [result, setResult] = useState<{ key: string; dates: string[] } | null>(null);

  useEffect(() => {
    if (!usable) return;

    let current = true;
    void (async () => {
      try {
        const r = await api.get<{ dates: string[] }>(
          `/api/pm/preview?frequency=${frequency}&anchorDate=${anchor}`,
        );
        if (current) setResult({ key, dates: r.dates });
      } catch {
        if (current) setResult({ key, dates: [] });
      }
    })();

    return () => {
      current = false;
    };
  }, [usable, key, frequency, anchor]);

  // Only the answer to the question currently asked, never the last one.
  const dates = usable && result?.key === key ? result.dates : [];
  if (dates.length === 0) return null;

  return (
    <div className="stack" style={{ gap: '0.25rem' }}>
      <span>Due dates in the first year</span>
      <ol className="row" style={{ flexWrap: 'wrap', gap: '0.5rem', listStyle: 'none', margin: 0, padding: 0 }}>
        {dates.map((d) => (
          <li key={d}>
            <span className="pill tone-neutral">{formatDate(d)}</span>
          </li>
        ))}
      </ol>
      <span className="muted">After that the dates carry on the same way.</span>
    </div>
  );
}

/**
 * Picking PM dates one at a time: select a date, click Add, then select the next one.
 *
 * Each date added is a PM of its own, listed as PM 1, PM 2 and so on in date order, and any of
 * them can be taken out again before saving. Enter adds the date too, and does not submit the
 * form it is in, which would save a machine that is half filled in.
 */
export function PmDatePicker({ dates, onChange }: { dates: string[]; onChange: (next: string[]) => void }) {
  const [pending, setPending] = useState('');
  const [note, setNote] = useState<string | null>(null);

  function add() {
    if (!pending) {
      setNote('Select a date first.');
      return;
    }
    if (dates.includes(pending)) {
      setNote(`${formatDate(pending)} is already in the list.`);
      return;
    }
    if (dates.length >= MAX_MANUAL_DATES) {
      setNote(`At most ${MAX_MANUAL_DATES} dates can be added at once.`);
      return;
    }

    onChange([...dates, pending].sort());
    setPending('');
    setNote(null);
  }

  return (
    <div className="stack">
      <div style={{ display: 'flex', gap: '0.5rem', alignItems: 'flex-end', flexWrap: 'wrap' }}>
        <label className="stack">
          <span>PM date</span>
          <input
            className="field"
            type="date"
            value={pending}
            onChange={(e) => {
              setPending(e.target.value);
              setNote(null);
            }}
            onKeyDown={(e) => {
              if (e.key === 'Enter') {
                e.preventDefault();
                add();
              }
            }}
          />
        </label>
        <button type="button" className="btn" onClick={add}>Add this date</button>
      </div>

      <span className="muted">
        Select a date and click Add. That date is one PM. Then select the next date and add it, and
        so on for each PM.
      </span>

      {note && <p className="alert alert-info" role="status" style={{ margin: 0 }}>{note}</p>}

      {dates.length > 0 && (
        <ol className="stack" style={{ margin: 0, paddingLeft: 0, listStyle: 'none', gap: '0.25rem' }}>
          {dates.map((d, i) => (
            <li key={d} className="row" style={{ gap: '0.75rem', alignItems: 'center' }}>
              <span className="pill tone-neutral">PM {i + 1}</span>
              <span className="mono">{formatDate(d)}</span>
              <button
                type="button"
                className="btn btn-quiet"
                aria-label={`Remove the PM on ${formatDate(d)}`}
                onClick={() => onChange(dates.filter((x) => x !== d))}
              >
                Remove
              </button>
            </li>
          ))}
        </ol>
      )}
    </div>
  );
}
