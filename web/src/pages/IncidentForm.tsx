import { useEffect, useState } from 'react';
import type { FormEvent } from 'react';
import { Link, useLocation, useNavigate, useSearchParams } from 'react-router-dom';
import { api } from '../api/client';
import { EquipmentPicker } from '../EquipmentPicker';
import { DAMAGE_LEVELS, INCIDENT_TYPES, NO_PATIENTS } from '../incidentTypes';
import type { IncidentDetail } from '../incidentTypes';
import { usePageTitle } from '../pageTitle';
import { todayAtHospital } from '../time';

/** What the machine's own record says, for the details shown once it is chosen. */
type MachineRecord = {
  id: number;
  assetTag: string;
  equipmentTypeName: string | null;
  manufacturer: string | null;
  model: string | null;
  locationName: string | null;
};

/**
 * Writing up an incident with a machine, or correcting one that has not been closed.
 *
 * Short on purpose: the person who saw it should be able to put it down in a minute. The machine is
 * found by its number, and everything the register knows about it is read from its record. What
 * caused it is for the biomedical team to write afterwards.
 */
export function IncidentForm({
  editing,
  prefillMachine,
  onCancel,
  onSaved,
}: {
  editing?: IncidentDetail;
  /** From a machine's page: the machine this is about. */
  prefillMachine?: number | null;
  onCancel: () => void;
  onSaved: (id: number, message: string) => void | Promise<void>;
}) {
  const today = todayAtHospital();

  const [machineId, setMachineId] = useState<number | null>(editing?.equipmentId ?? prefillMachine ?? null);
  const [machine, setMachine] = useState<MachineRecord | null>(null);
  const [type, setType] = useState<number>(editing?.type ?? 10);
  const [occurredOn, setOccurredOn] = useState(editing?.occurredOn ?? today);
  const [occurredAt, setOccurredAt] = useState(editing?.occurredAt ?? '');
  const [place, setPlace] = useState(editing?.place ?? '');
  const [description, setDescription] = useState(editing?.description ?? '');
  const [involved, setInvolved] = useState(editing?.involvedPerson ?? '');
  const [action, setAction] = useState(editing?.immediateAction ?? '');
  const [outOfUse, setOutOfUse] = useState(editing?.takenOutOfUse ?? false);
  const [damage, setDamage] = useState<number>(editing?.damage ?? 10);

  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Choosing a machine shows what is known about it; clearing the box clears it.
  useEffect(() => {
    if (machineId === null) {
      setMachine(null);
      return;
    }
    let cancelled = false;
    (async () => {
      try {
        const m = await api.get<MachineRecord>(`/api/equipment/${machineId}`);
        if (!cancelled) setMachine(m);
      } catch {
        if (!cancelled) setMachine(null);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [machineId]);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError(null);

    if (machineId === null) {
      setError('Choose the machine this happened to.');
      return;
    }

    setBusy(true);
    try {
      const body = {
        equipmentId: machineId,
        type,
        occurredOn,
        occurredAt: occurredAt || null,
        place: place.trim() || null,
        description: description.trim(),
        involvedPerson: involved.trim() || null,
        immediateAction: action.trim() || null,
        takenOutOfUse: outOfUse,
        damage,
      };

      if (editing) {
        await api.put(`/api/incidents/${editing.id}`, body);
        await onSaved(editing.id, `Saved ${editing.reference}.`);
      } else {
        const created = await api.post<{ id: number; reference: string }>('/api/incidents', body);
        await onSaved(created.id, `Reported as ${created.reference}. The biomedical team will look into it.`);
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not save the incident.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="card stack" onSubmit={submit}>
      <h2 className="section-h" style={{ margin: 0 }}>
        {editing ? `Correct ${editing.reference}` : 'Report an incident with a machine'}
      </h2>

      {error && <p className="alert alert-error" role="alert">{error}</p>}

      <p className="alert alert-info" style={{ margin: 0 }}>{NO_PATIENTS}</p>

      <fieldset className="stack" style={{ border: 'none', padding: 0, margin: 0 }}>
        <legend style={{ fontWeight: 600, padding: 0 }}>Which machine</legend>

        {/* From a machine's page the box starts with that machine in it, once its record has been read. */}
        <EquipmentPicker
          key={prefillMachine && !editing ? (machine ? 'prefilled' : 'waiting') : 'typed'}
          value={machineId}
          onChange={setMachineId}
          label="Machine number"
          initialLabel={
            editing?.machine
              ? `${editing.machine.assetTag} — ${editing.machine.machineName ?? ''}`
              : prefillMachine && machine
                ? `${machine.assetTag} — ${machine.equipmentTypeName ?? ''}`
                : ''
          }
        />
        <span className="muted">Type the machine&apos;s number, or its make or model.</span>

        {machine && (
          <dl className="detail card" aria-label="Machine details">
            <dt>Machine</dt>
            <dd>{machine.equipmentTypeName ?? '—'}</dd>
            <dt>Company and model</dt>
            <dd>{[machine.manufacturer, machine.model].filter(Boolean).join(' ') || '—'}</dd>
            <dt>Where it is now</dt>
            <dd>{machine.locationName ?? '—'}</dd>
          </dl>
        )}
      </fieldset>

      <fieldset className="stack" style={{ border: 'none', padding: 0, margin: 0 }}>
        <legend style={{ fontWeight: 600, padding: 0 }}>What happened</legend>

        <div className="row" role="radiogroup" aria-label="What happened" style={{ flexWrap: 'wrap' }}>
          {INCIDENT_TYPES.map((t) => (
            <label key={t.value} className="row" style={{ gap: '0.4rem', alignItems: 'center' }} title={t.help}>
              <input
                type="radio"
                name="incident-type"
                checked={type === t.value}
                onChange={() => setType(t.value)}
              />
              {t.label}
            </label>
          ))}
        </div>
        <span className="muted">{INCIDENT_TYPES.find((t) => t.value === type)?.help}</span>
      </fieldset>

      <div className="filters">
        <label className="field">
          <span>Day it happened</span>
          <input type="date" value={occurredOn} max={today} onChange={(e) => setOccurredOn(e.target.value)} required />
        </label>

        <label className="field">
          <span>Time (optional)</span>
          <input type="time" value={occurredAt} onChange={(e) => setOccurredAt(e.target.value)} />
        </label>

        <label className="field grow">
          <span>Where exactly (optional)</span>
          <input
            value={place}
            onChange={(e) => setPlace(e.target.value)}
            maxLength={200}
            placeholder="e.g. Bay 4, fell off the bedside table"
          />
        </label>
      </div>

      <label className="field">
        <span>Describe what happened to the machine</span>
        <textarea
          rows={4}
          value={description}
          onChange={(e) => setDescription(e.target.value)}
          maxLength={4000}
          required
          placeholder="e.g. The monitor slipped off its stand while the bed was being moved and hit the floor."
        />
      </label>

      <div className="filters">
        <label className="field grow">
          <span>Staff handling it (optional)</span>
          <input
            value={involved}
            onChange={(e) => setInvolved(e.target.value)}
            maxLength={200}
            placeholder="e.g. Staff nurse, ICU. Staff only, never a patient."
          />
        </label>

        <label className="field">
          <span>The machine was left</span>
          <select value={damage} onChange={(e) => setDamage(Number(e.target.value))}>
            {DAMAGE_LEVELS.map((d) => <option key={d.value} value={d.value}>{d.label}</option>)}
          </select>
        </label>
      </div>

      <label className="field">
        <span>What was done straight away (optional)</span>
        <textarea
          rows={2}
          value={action}
          onChange={(e) => setAction(e.target.value)}
          maxLength={4000}
          placeholder="e.g. Switched off and sent to the biomedical store."
        />
      </label>

      <label className="row" style={{ gap: '0.5rem', alignItems: 'center' }}>
        <input type="checkbox" checked={outOfUse} onChange={(e) => setOutOfUse(e.target.checked)} />
        The machine was taken out of use because of it
      </label>

      <div className="row">
        <button className="btn btn-primary" type="submit" disabled={busy || machineId === null || !description.trim() || !occurredOn}>
          {busy ? 'Saving…' : editing ? 'Save' : 'Report the incident'}
        </button>
        <button className="btn" type="button" onClick={onCancel} disabled={busy}>Cancel</button>
      </div>
    </form>
  );
}

/**
 * /incidents/new, optionally ?equipmentId=12: the page for writing up an incident, reached from the list or
 * from a machine's own page.
 */
export function IncidentNewPage() {
  usePageTitle('Report an incident');
  const navigate = useNavigate();
  const location = useLocation();
  const [params] = useSearchParams();

  const equipmentId = Number(params.get('equipmentId')) || null;
  const from = (location.state as { from?: string } | null)?.from ?? '/incidents';

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <p className="crumb"><Link to={from}>← Back</Link></p>
          <h1>Report an incident</h1>
          <p className="muted">A machine was dropped, mishandled, knocked, soaked or lost.</p>
        </div>
      </header>

      <IncidentForm
        prefillMachine={equipmentId}
        onCancel={() => navigate(from)}
        onSaved={(id, message) =>
          navigate(`/incidents/${id}`, { replace: true, state: { handoff: { notice: message, recorded: null } } })
        }
      />
    </div>
  );
}
