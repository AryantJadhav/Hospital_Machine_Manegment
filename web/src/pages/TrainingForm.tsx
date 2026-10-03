import { useEffect, useState } from 'react';
import type { FormEvent } from 'react';
import { api } from '../api/client';
import { EquipmentPicker } from '../EquipmentPicker';
import { todayAtHospital } from '../time';
import { AttendeeGrid } from './AttendeeGrid';
import { attendeesFrom, duplicateName, rowsFrom } from './attendeeRows';
import type { AttendeeRow } from './attendeeRows';
import { TRAINER_TYPE_LABEL } from '../trainingTypes';
import type { TrainerType, TrainingDetail, TrainingMachine } from '../trainingTypes';

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

/**
 * Adding or changing a training session and the people who came.
 *
 * The attendance is a sheet of rows (Name, Job): staff with an account can be picked from a list, and
 * everyone else - most of the nurses and technicians trained on a ward's equipment have no login - is
 * typed in, or pasted from Excel.
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
  const [trainerType, setTrainerType] = useState<TrainerType | ''>(editing?.trainerType ?? '');
  const [trainer, setTrainer] = useState(editing?.trainer ?? '');
  const [venue, setVenue] = useState(editing?.venue ?? '');
  const [minutes, setMinutes] = useState(editing?.durationMinutes?.toString() ?? '');
  const [notes, setNotes] = useState(editing?.notes ?? '');

  // The attendance sheet: a few empty rows to start on, or the people already listed and one to add to.
  const [rows, setRows] = useState<AttendeeRow[]>(() =>
    rowsFrom(editing?.attendees ?? [], editing && editing.attendees.length > 0 ? editing.attendees.length + 1 : 5),
  );

  const [staff, setStaff] = useState<Staff[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const s = await api.get<Staff[]>('/api/people');
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

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError(null);

    const twice = duplicateName(rows);
    if (twice) {
      setError(`${twice} is listed twice. Take one of the rows out.`);
      return;
    }

    setBusy(true);
    try {
      const body = {
        title: title || null,
        sessionDate,
        equipmentId: machineId,
        // With a machine chosen the server uses the machine's own kind.
        equipmentTypeId,
        trainerType: trainerType || null,
        trainer: trainer.trim() || null,
        venue: venue.trim() || null,
        durationMinutes: minutes.trim() ? Number(minutes) : null,
        notes: notes.trim() || null,
        attendees: attendeesFrom(rows),
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
        <label className="field">
          <span>Trainer (optional)</span>
          <select value={trainerType} onChange={(e) => setTrainerType(e.target.value as TrainerType | '')}>
            <option value="">Not said</option>
            <option value="Vendor">{TRAINER_TYPE_LABEL.Vendor}</option>
            <option value="InHouse">{TRAINER_TYPE_LABEL.InHouse}</option>
          </select>
        </label>

        <label className="field grow">
          <span>Trainer&apos;s name (optional)</span>
          <input
            value={trainer}
            onChange={(e) => setTrainer(e.target.value)}
            placeholder={trainerType === 'Vendor' ? 'e.g. the manufacturer\'s applications specialist' : 'Who ran it'}
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

        <AttendeeGrid rows={rows} onChange={setRows} staff={staff} />
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
