import { useState } from 'react';
import type { FormEvent } from 'react';
import { api } from '../api/client';
import { PERFORMED_BY } from '../pmSchedule';
import { PmDatePicker } from './PmScheduleFields';

/**
 * Adding PM dates to a machine that is already on the register.
 *
 * The administrator selects a date and adds it as a PM, then selects another and adds that, and
 * saves them together. Coming back later to add more puts them on the same schedule, and a date
 * that is already a PM is left as it is. This is the same choosing of dates as on the Add a
 * machine form, for a machine that was added before. A PM needs no checklist.
 */
export function AddPmDatesForm({
  equipmentId,
  hasContract,
  onCancel,
  onSaved,
}: {
  equipmentId: number;
  /** Whether the machine has an AMC or a CMC, which is what lets the vendor be chosen. */
  hasContract: boolean;
  onCancel: () => void;
  onSaved: (message: string) => void | Promise<void>;
}) {
  const [by, setBy] = useState<number>(PERFORMED_BY.inHouse);
  const [grace, setGrace] = useState('7');
  const [dates, setDates] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError(null);

    if (dates.length === 0) {
      setError('Add at least one PM date.');
      return;
    }

    setBusy(true);
    try {
      const r = await api.post<{ added: number; alreadyThere: number }>('/api/pm/schedules/dates', {
        equipmentId,
        dates,
        graceDays: Number(grace) || 0,
        performedBy: hasContract ? by : PERFORMED_BY.inHouse,
      });

      const already = r.alreadyThere > 0 ? ` ${r.alreadyThere} ${r.alreadyThere === 1 ? 'was' : 'were'} already a PM.` : '';
      await onSaved(
        r.added === 0
          ? `No new PM dates were added.${already}`
          : `Added ${r.added} PM ${r.added === 1 ? 'date' : 'dates'}.${already}`,
      );
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not add the PM dates.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="card stack" onSubmit={submit}>
      <h2 style={{ margin: 0, fontSize: '1.05rem' }}>Add PM dates</h2>

      {error && <p className="alert alert-error" role="alert">{error}</p>}

      <div style={{ display: 'grid', gap: '0.75rem', gridTemplateColumns: 'repeat(auto-fit, minmax(12rem, 1fr))' }}>
        {hasContract && (
          <label className="stack">
            <span>Who does these PMs</span>
            <select className="field" value={by} onChange={(e) => setBy(Number(e.target.value))}>
              <option value={PERFORMED_BY.inHouse}>Our own team</option>
              <option value={PERFORMED_BY.vendor}>The maintenance contract vendor</option>
            </select>
          </label>
        )}

        <label className="stack">
          <span>Days of grace</span>
          <input
            className="field mono"
            type="number"
            min={0}
            max={90}
            value={grace}
            onChange={(e) => setGrace(e.target.value)}
          />
        </label>
      </div>

      <PmDatePicker dates={dates} onChange={setDates} />

      <span className="muted">
        Dates added now go with any already added for this machine. Everyone is reminded 7 days before
        each one.
      </span>

      <div style={{ display: 'flex', gap: '0.5rem' }}>
        <button className="btn btn-primary" disabled={busy}>
          {busy ? 'Saving…' : 'Save the PM dates'}
        </button>
        <button type="button" className="btn" onClick={onCancel} disabled={busy}>Cancel</button>
      </div>
    </form>
  );
}
