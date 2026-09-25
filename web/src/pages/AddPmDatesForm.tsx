import { useEffect, useState } from 'react';
import type { FormEvent } from 'react';
import { Link } from 'react-router-dom';
import { api } from '../api/client';
import { PERFORMED_BY } from '../pmSchedule';
import { PmDatePicker } from './PmScheduleFields';

/**
 * Adding PM dates to a machine that is already on the register.
 *
 * The administrator selects a date and adds it as a PM, then selects another and adds that, and
 * saves them together. Coming back later to add more puts them on the same schedule, and a date
 * that is already a PM is left as it is. This is the same choosing of dates as on the Add a
 * machine form, for a machine that was added before.
 */

type Checklist = { id: number; name: string; isActive: boolean; publishedVersionNo: number | null };

/** The PM checklist kind, as the server numbers it. */
const PM_CHECKLIST_KIND = 10;

export function AddPmDatesForm({
  equipmentId,
  equipmentTypeId,
  hasContract,
  onCancel,
  onSaved,
}: {
  equipmentId: number;
  equipmentTypeId: number;
  /** Whether the machine has an AMC or a CMC, which is what lets the vendor be chosen. */
  hasContract: boolean;
  onCancel: () => void;
  onSaved: (message: string) => void | Promise<void>;
}) {
  const [checklists, setChecklists] = useState<Checklist[] | null>(null);
  const [checklistId, setChecklistId] = useState<number | null>(null);
  const [by, setBy] = useState<number>(PERFORMED_BY.inHouse);
  const [grace, setGrace] = useState('7');
  const [dates, setDates] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Only checklists that are switched on and published: a PM on one nobody can fill in would put
  // work on the list that cannot be done.
  useEffect(() => {
    let current = true;
    void (async () => {
      try {
        const all = await api.get<Checklist[]>(
          `/api/checklists?equipmentTypeId=${equipmentTypeId}&kind=${PM_CHECKLIST_KIND}`,
        );
        if (current) setChecklists(all.filter((c) => c.isActive && c.publishedVersionNo !== null));
      } catch {
        if (current) setChecklists([]);
      }
    })();
    return () => {
      current = false;
    };
  }, [equipmentTypeId]);

  // The one on offer is chosen for the person when there is only one.
  const choice = checklistId ?? (checklists?.length === 1 ? checklists[0].id : null);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError(null);

    if (choice === null) {
      setError('Choose the checklist for the PM.');
      return;
    }
    if (dates.length === 0) {
      setError('Add at least one PM date.');
      return;
    }

    setBusy(true);
    try {
      const r = await api.post<{ added: number; alreadyThere: number }>('/api/pm/schedules/dates', {
        equipmentId,
        checklistTemplateId: choice,
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

      {checklists === null && <p className="muted">Loading the PM checklists…</p>}

      {checklists !== null && checklists.length === 0 && (
        <p className="alert alert-info">
          This equipment type has no published PM checklist yet, so there is nothing to schedule. Write
          and publish one on the <Link to="/checklists">Checklists</Link> page first.
        </p>
      )}

      {checklists !== null && checklists.length > 0 && (
        <>
          <div style={{ display: 'grid', gap: '0.75rem', gridTemplateColumns: 'repeat(auto-fit, minmax(12rem, 1fr))' }}>
            <label className="stack">
              <span>PM checklist</span>
              <select
                className="field"
                required
                value={choice ?? ''}
                onChange={(e) => setChecklistId(e.target.value ? Number(e.target.value) : null)}
              >
                <option value="">Choose…</option>
                {checklists.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
              </select>
            </label>

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
            Dates added now go with any already added for this checklist on this machine. Everyone is
            reminded 7 days before each one.
          </span>
        </>
      )}

      <div style={{ display: 'flex', gap: '0.5rem' }}>
        <button className="btn btn-primary" disabled={busy || checklists === null || checklists.length === 0}>
          {busy ? 'Saving…' : 'Save the PM dates'}
        </button>
        <button type="button" className="btn" onClick={onCancel} disabled={busy}>Cancel</button>
      </div>
    </form>
  );
}
