import { useEffect, useState } from 'react';
import type { FormEvent } from 'react';
import { api } from '../api/client';
import { EquipmentPicker } from '../EquipmentPicker';
import { todayAtHospital } from '../time';
import type { TrainingDetail, TrainingMachine } from '../trainingTypes';

/** What the machine's own record says, for the details that fill in once it is chosen. */
type MachineRecord = {
  id: number;
  assetTag: string;
  equipmentTypeName: string | null;
  manufacturer: string | null;
  model: string | null;
  serialNumber: string | null;
  locationName: string | null;
};

type Staff = { id: number; fullName: string };

/** One line of "Name, designation" for a person without an account. */
function otherLine(name: string, designation: string | null): string {
  return designation ? `${name}, ${designation}` : name;
}

/**
 * Adding or changing a training session and the people who came.
 *
 * People with an account are ticked from the staff list; everyone else - most of the nurses and
 * technicians trained on a ward's equipment have no login - is typed one to a line.
 */
export function TrainingForm({
  editing,
  onCancel,
  onSaved,
}: {
  editing?: TrainingDetail;
  onCancel: () => void;
  onSaved: (id: number, message: string) => void | Promise<void>;
}) {
  // Not asked for any more, but a session that already has a name or a kind of machine keeps it
  // when it is edited, rather than losing it on save.
  const title = editing?.title ?? '';
  const equipmentTypeId = editing?.equipmentTypeId ?? null;

  // The machine is found by its number (or make, or model) and everything else about it is read from
  // its own record, so ten machines of one model differ only by the number the user picks.
  const [machineId, setMachineId] = useState<number | null>(editing?.equipmentId ?? null);
  const [machine, setMachine] = useState<TrainingMachine | null>(editing?.machine ?? null);

  const [sessionDate, setSessionDate] = useState(editing?.sessionDate ?? todayAtHospital());
  const [trainer, setTrainer] = useState(editing?.trainer ?? '');
  const [venue, setVenue] = useState(editing?.venue ?? '');
  const [minutes, setMinutes] = useState(editing?.durationMinutes?.toString() ?? '');
  const [notes, setNotes] = useState(editing?.notes ?? '');

  const [ticked, setTicked] = useState<Set<number>>(
    () => new Set((editing?.attendees ?? []).filter((a) => a.userId !== null).map((a) => a.userId as number)),
  );
  const [others, setOthers] = useState(
    (editing?.attendees ?? [])
      .filter((a) => a.userId === null)
      .map((a) => otherLine(a.name, a.designation))
      .join('\n'),
  );

  const [staff, setStaff] = useState<Staff[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const s = await api.get<Staff[]>('/api/users');
        if (cancelled) return;
        setStaff(s);
      } catch {
        // The form still works for a session with names typed in.
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  // Choosing a machine fills in what is known about it; clearing the box clears it.
  useEffect(() => {
    if (machineId === null) {
      setMachine(null);
      return;
    }
    if (machine?.id === machineId) return;

    let cancelled = false;
    (async () => {
      try {
        const m = await api.get<MachineRecord>(`/api/equipment/${machineId}`);
        if (cancelled) return;
        setMachine({
          id: m.id,
          assetTag: m.assetTag,
          machineName: m.equipmentTypeName,
          manufacturer: m.manufacturer,
          model: m.model,
          serialNumber: m.serialNumber,
          locationName: m.locationName,
        });
      } catch {
        if (!cancelled) setError('Could not read that machine\'s details.');
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [machineId, machine?.id]);

  function toggle(id: number) {
    setTicked((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError(null);

    const typed = others
      .split('\n')
      .map((line) => line.trim())
      .filter((line) => line.length > 0)
      .map((line) => {
        const comma = line.indexOf(',');
        return comma < 0
          ? { name: line, designation: null }
          : { name: line.slice(0, comma).trim(), designation: line.slice(comma + 1).trim() || null };
      });

    setBusy(true);
    try {
      const body = {
        title: title || null,
        sessionDate,
        equipmentId: machineId,
        // With a machine chosen the server uses the machine's own kind.
        equipmentTypeId,
        trainer: trainer.trim() || null,
        venue: venue.trim() || null,
        durationMinutes: minutes.trim() ? Number(minutes) : null,
        notes: notes.trim() || null,
        attendees: [...[...ticked].map((userId) => ({ userId })), ...typed],
      };

      if (editing) {
        await api.put(`/api/training/${editing.id}`, body);
        await onSaved(editing.id, 'Saved the session.');
      } else {
        const created = await api.post<{ id: number }>('/api/training', body);
        await onSaved(created.id, 'Added the session.');
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not save the session.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="card stack" onSubmit={submit}>
      <h2 className="section-h" style={{ margin: 0 }}>{editing ? 'Edit the training session' : 'Add a training session'}</h2>

      {error && <p className="alert alert-error" role="alert">{error}</p>}

      <fieldset className="stack" style={{ border: 'none', padding: 0, margin: 0 }}>
        <legend style={{ fontWeight: 600, padding: 0 }}>Machine</legend>

        <EquipmentPicker
          value={machineId}
          onChange={setMachineId}
          label="Machine number"
          initialLabel={editing?.machine ? `${editing.machine.assetTag} — ${editing.machine.machineName ?? ''}` : ''}
        />
        <span className="muted">
          Type the machine&apos;s number, or its make or model. Its details fill in from its own record.
        </span>

        {machine && (
          <dl className="detail card" aria-label="Machine details">
            <dt>Machine number</dt>
            <dd className="mono">{machine.assetTag}</dd>
            <dt>Machine</dt>
            <dd>{machine.machineName ?? '—'}</dd>
            <dt>Company</dt>
            <dd>{machine.manufacturer ?? '—'}</dd>
            <dt>Model</dt>
            <dd>{machine.model ?? '—'}</dd>
            <dt>Serial number</dt>
            <dd>{machine.serialNumber ?? '—'}</dd>
            <dt>Location</dt>
            <dd>{machine.locationName ?? '—'}</dd>
          </dl>
        )}
      </fieldset>

      <div className="filters">
        <label className="field">
          <span>Date</span>
          <input type="date" value={sessionDate} onChange={(e) => setSessionDate(e.target.value)} required />
          <span className="muted">A date still to come is a session that is planned.</span>
        </label>

        <label className="field">
          <span>Length, minutes (optional)</span>
          <input
            type="number"
            min={1}
            max={1440}
            value={minutes}
            onChange={(e) => setMinutes(e.target.value)}
            placeholder="e.g. 90"
          />
        </label>
      </div>

      <div className="filters">
        <label className="field grow">
          <span>Trainer (optional)</span>
          <input
            value={trainer}
            onChange={(e) => setTrainer(e.target.value)}
            placeholder="Who ran it, or the manufacturer's trainer"
            maxLength={200}
          />
        </label>

        <label className="field grow">
          <span>Where (optional)</span>
          <input value={venue} onChange={(e) => setVenue(e.target.value)} placeholder="e.g. ICU seminar room" maxLength={200} />
        </label>
      </div>

      <fieldset className="stack" style={{ border: 'none', padding: 0, margin: 0 }}>
        <legend style={{ fontWeight: 600, padding: 0 }}>Who attended</legend>

        {staff.length > 0 && (
          <div className="stack" style={{ gap: '0.3rem' }}>
            <span className="muted">People with an account</span>
            <div
              style={{
                display: 'grid',
                gap: '0.25rem 1rem',
                gridTemplateColumns: 'repeat(auto-fill, minmax(14rem, 1fr))',
                maxHeight: '14rem',
                overflow: 'auto',
              }}
            >
              {staff.map((s) => (
                <label key={s.id} className="row" style={{ gap: '0.5rem', alignItems: 'center' }}>
                  <input type="checkbox" checked={ticked.has(s.id)} onChange={() => toggle(s.id)} />
                  <span>{s.fullName}</span>
                </label>
              ))}
            </div>
          </div>
        )}

        <label className="field">
          <span>Everyone else, one to a line</span>
          <textarea
            rows={4}
            value={others}
            onChange={(e) => setOthers(e.target.value)}
            placeholder={'Meera Nair, Staff nurse, ICU\nRavi Patil, Technician'}
          />
          <span className="muted">
            A name, then a comma and their job if you like. Staff only: this system holds no patient information,
            so do not enter a patient&apos;s name.
          </span>
        </label>
      </fieldset>

      <label className="field">
        <span>Notes (optional)</span>
        <textarea rows={3} value={notes} onChange={(e) => setNotes(e.target.value)} maxLength={4000} />
      </label>

      <div className="row">
        <button className="btn btn-primary" type="submit" disabled={busy || !sessionDate}>
          {busy ? 'Saving…' : editing ? 'Save' : 'Add session'}
        </button>
        <button className="btn" type="button" onClick={onCancel} disabled={busy}>Cancel</button>
      </div>
    </form>
  );
}
