import { useEffect, useState } from 'react';
import { api } from '../api/client';
import { formatDate } from '../time';
import { CUSTOM_FREQUENCY, PM_FREQUENCIES } from '../pmSchedule';
import type { ScheduleEdit } from '../pmSchedule';

/** The pieces of a PM schedule form that the Add a machine form and the Edit form share. */

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

/** One of the machine's existing PM schedules. */
export function PmExistingSchedule({
  value,
  onChange,
}: {
  value: ScheduleEdit;
  onChange: (next: ScheduleEdit) => void;
}) {
  const isCustom = value.original.frequency === CUSTOM_FREQUENCY;
  const selected = value.active ? String(value.frequency) : 'stop';
  const name = `pm-${value.id}`;

  return (
    <div className="card stack" style={{ margin: 0 }}>
      <div className="row" style={{ justifyContent: 'space-between', alignItems: 'baseline' }}>
        <strong>{value.checklistName}</strong>
        {!value.original.active && <span className="muted">Stopped</span>}
      </div>

      <label className="stack" htmlFor={`${name}-freq`}>
        <span>How often</span>
        <select
          id={`${name}-freq`}
          className="field"
          value={selected}
          onChange={(e) => {
            if (e.target.value === 'stop') {
              onChange({ ...value, active: false });
            } else {
              onChange({ ...value, active: true, frequency: Number(e.target.value) });
            }
          }}
        >
          {PM_FREQUENCIES.map((f) => <option key={f.value} value={f.value}>{f.label}</option>)}
          {isCustom && (
            <option value={CUSTOM_FREQUENCY}>Every {value.original.intervalDays} days (set on the PM pages)</option>
          )}
          <option value="stop">Stop this PM</option>
        </select>
      </label>

      {value.active ? (
        <>
          <div style={{ display: 'grid', gap: '0.75rem', gridTemplateColumns: 'repeat(auto-fit, minmax(12rem, 1fr))' }}>
            <label className="stack" htmlFor={`${name}-next`}>
              <span>{value.original.active ? 'Next PM due' : 'First PM due'}</span>
              <input
                id={`${name}-next`}
                className="field"
                type="date"
                required
                value={value.nextDue}
                onChange={(e) => onChange({ ...value, nextDue: e.target.value })}
              />
            </label>

            <label className="stack" htmlFor={`${name}-grace`}>
              <span>Days of grace</span>
              <input
                id={`${name}-grace`}
                className="field mono"
                type="number"
                min={0}
                max={90}
                value={value.graceDays}
                onChange={(e) => onChange({ ...value, graceDays: e.target.value })}
              />
            </label>
          </div>

          <PmDatePreview frequency={value.frequency} anchor={value.nextDue} />

          {(value.frequency !== value.original.frequency || value.nextDue !== value.original.nextDue) && (
            <span className="muted">
              Saving replaces the PMs that are not yet due with these dates. PMs already due, overdue
              or done are not touched.
            </span>
          )}
        </>
      ) : (
        value.original.active && (
          <span className="muted">
            Saving stops this PM. The PMs that are not yet due are removed; any that are already due
            or overdue stay on the work list until they are done or skipped with a reason.
          </span>
        )
      )}
    </div>
  );
}
