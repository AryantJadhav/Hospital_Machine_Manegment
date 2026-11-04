import { useEffect, useState } from 'react';
import type { FormEvent } from 'react';
import { Link, useLocation, useNavigate, useSearchParams } from 'react-router-dom';
import { api } from '../api/client';
import { EquipmentPicker } from '../EquipmentPicker';
import { LOSS_KINDS, NO_PATIENTS_LOSS } from '../lossTypes';
import type { LossDetail } from '../lossTypes';
import { formatRupees } from '../money';
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
  purchaseCost: number | null;
  status: number;
};

const LOST = 60;
const DISPOSED = 50;

/**
 * Writing up a machine that was lost, or correcting one that has not been closed.
 *
 * Short on purpose: the person who found it gone should be able to put it down in a minute. The machine is found
 * by its number, and everything the register knows about it is read from its record. How it went, and what is being
 * done to find it, is for the biomedical team to write afterwards. Writing it up marks the machine Lost on the register.
 */
export function LossForm({
  editing,
  prefillMachine,
  onCancel,
  onSaved,
}: {
  editing?: LossDetail;
  /** From a machine's page: the machine this is about. */
  prefillMachine?: number | null;
  onCancel: () => void;
  onSaved: (id: number, message: string) => void | Promise<void>;
}) {
  const today = todayAtHospital();

  const [machineId, setMachineId] = useState<number | null>(editing?.equipmentId ?? prefillMachine ?? null);
  const [machine, setMachine] = useState<MachineRecord | null>(null);
  const [kind, setKind] = useState<number>(editing?.kind ?? 10);
  const [noticedOn, setNoticedOn] = useState(editing?.noticedOn ?? today);
  const [lastSeenOn, setLastSeenOn] = useState(editing?.lastSeenOn ?? '');
  const [lastSeenPlace, setLastSeenPlace] = useState(editing?.lastSeenPlace ?? '');
  const [description, setDescription] = useState(editing?.description ?? '');
  const [handledBy, setHandledBy] = useState(editing?.lastHandledBy ?? '');
  const [policeNumber, setPoliceNumber] = useState(editing?.policeReportNumber ?? '');
  const [policeOn, setPoliceOn] = useState(editing?.policeReportedOn ?? '');
  const [value, setValue] = useState(editing?.estimatedValue?.toString() ?? '');

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

  const alreadyLost = !editing && machine?.status === LOST;
  const disposed = !editing && machine?.status === DISPOSED;

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError(null);

    if (machineId === null) {
      setError('Choose the machine that was lost.');
      return;
    }

    setBusy(true);
    try {
      const body = {
        equipmentId: machineId,
        kind,
        noticedOn,
        lastSeenOn: lastSeenOn || null,
        lastSeenPlace: lastSeenPlace.trim() || null,
        description: description.trim(),
        lastHandledBy: handledBy.trim() || null,
        policeReportNumber: policeNumber.trim() || null,
        policeReportedOn: policeOn || null,
        estimatedValue: value.trim() ? Number(value) : null,
      };

      if (editing) {
        await api.put(`/api/losses/${editing.id}`, body);
        await onSaved(editing.id, `Saved ${editing.reference}.`);
      } else {
        const created = await api.post<{ id: number; reference: string; status: string }>('/api/losses', body);
        // A department's loss goes to a biomedical engineer. One an engineer or the head wrote has had that step, so it goes to the head.
        const next =
          created.status === 'AwaitingDecision'
            ? 'It is now with the head of Biomedical for the final decision.'
            : 'It is now with a biomedical engineer, who will look into it and approve it for the head.';
        await onSaved(created.id, `Reported as ${created.reference}. ${next} The machine is marked Lost on the register.`);
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not save the loss.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="card stack" onSubmit={submit}>
      <h2 className="section-h" style={{ margin: 0 }}>
        {editing ? `Correct ${editing.reference}` : 'Report a lost machine'}
      </h2>

      {error && <p className="alert alert-error" role="alert">{error}</p>}

      <p className="alert alert-info" style={{ margin: 0 }}>{NO_PATIENTS_LOSS}</p>

      <fieldset className="stack" style={{ border: 'none', padding: 0, margin: 0 }}>
        <legend style={{ fontWeight: 600, padding: 0 }}>Which machine</legend>

        {editing ? (
          <p style={{ margin: 0 }}>
            <span className="mono">{editing.machine?.assetTag}</span>
            {editing.machine?.machineName && ` — ${editing.machine.machineName}`}
            <span className="muted"> The machine cannot be changed. To fix a wrong one, cancel this record and write a new one.</span>
          </p>
        ) : (
          <>
            {/* From a machine's page the box starts with that machine in it, once its record has been read. */}
            <EquipmentPicker
              key={prefillMachine ? (machine ? 'prefilled' : 'waiting') : 'typed'}
              value={machineId}
              onChange={setMachineId}
              label="Machine number"
              initialLabel={prefillMachine && machine ? `${machine.assetTag} — ${machine.equipmentTypeName ?? ''}` : ''}
            />
            <span className="muted">Type the machine&apos;s number, or its make or model.</span>
          </>
        )}

        {machine && !editing && (
          <dl className="detail card" aria-label="Machine details">
            <dt>Machine</dt>
            <dd>{machine.equipmentTypeName ?? '—'}</dd>
            <dt>Company and model</dt>
            <dd>{[machine.manufacturer, machine.model].filter(Boolean).join(' ') || '—'}</dd>
            <dt>Meant to be in</dt>
            <dd>{machine.locationName ?? '—'}</dd>
            <dt>Cost on the register</dt>
            <dd>{machine.purchaseCost !== null ? formatRupees(machine.purchaseCost) : <span className="muted">Not recorded</span>}</dd>
          </dl>
        )}

        {alreadyLost && (
          <p className="alert alert-warn" role="alert" style={{ margin: 0 }}>
            This machine is already recorded as lost. Look for its record on the Loss of equipment page.
          </p>
        )}
        {disposed && (
          <p className="alert alert-warn" role="alert" style={{ margin: 0 }}>
            This machine has already been disposed of, so it cannot be recorded as lost.
          </p>
        )}
      </fieldset>

      <fieldset className="stack" style={{ border: 'none', padding: 0, margin: 0 }}>
        <legend style={{ fontWeight: 600, padding: 0 }}>How was it lost</legend>

        <div className="row" role="radiogroup" aria-label="How was it lost" style={{ flexWrap: 'wrap' }}>
          {LOSS_KINDS.map((k) => (
            <label key={k.value} className="row" style={{ gap: '0.4rem', alignItems: 'center' }} title={k.help}>
              <input type="radio" name="loss-kind" checked={kind === k.value} onChange={() => setKind(k.value)} />
              {k.label}
            </label>
          ))}
        </div>
        <span className="muted">{LOSS_KINDS.find((k) => k.value === kind)?.help}</span>
      </fieldset>

      <div className="filters">
        <label className="field">
          <span>Day it was found missing</span>
          <input type="date" value={noticedOn} max={today} onChange={(e) => setNoticedOn(e.target.value)} required />
        </label>

        <label className="field">
          <span>Last seen on (optional)</span>
          <input type="date" value={lastSeenOn} max={noticedOn || today} onChange={(e) => setLastSeenOn(e.target.value)} />
        </label>

        <label className="field grow">
          <span>Last seen at (optional)</span>
          <input
            value={lastSeenPlace}
            onChange={(e) => setLastSeenPlace(e.target.value)}
            maxLength={200}
            placeholder="e.g. Recovery bay, on the trolley by bed 4"
          />
        </label>
      </div>

      <label className="field">
        <span>Describe what happened</span>
        <textarea
          rows={4}
          value={description}
          onChange={(e) => setDescription(e.target.value)}
          maxLength={4000}
          required
          placeholder="e.g. The infusion pump was not on its stand at the morning round. It was last used on Tuesday night."
        />
      </label>

      <div className="filters">
        <label className="field grow">
          <span>Last handled by (optional)</span>
          <input
            value={handledBy}
            onChange={(e) => setHandledBy(e.target.value)}
            maxLength={200}
            placeholder="e.g. Staff nurse, ICU. Staff only, never a patient."
          />
        </label>

        <label className="field">
          <span>What it was worth, ₹ (optional)</span>
          <input type="number" min={0} step="0.01" value={value} onChange={(e) => setValue(e.target.value)} />
          <span className="muted">Left empty, the cost on the register is used.</span>
        </label>
      </div>

      <fieldset className="stack" style={{ border: 'none', padding: 0, margin: 0 }}>
        <legend style={{ fontWeight: 600, padding: 0 }}>Police complaint (if one was made)</legend>
        <div className="filters">
          <label className="field grow">
            <span>Complaint (FIR) number</span>
            <input value={policeNumber} onChange={(e) => setPoliceNumber(e.target.value)} maxLength={100} />
          </label>

          <label className="field">
            <span>Day the police were told</span>
            <input type="date" value={policeOn} max={today} onChange={(e) => setPoliceOn(e.target.value)} />
          </label>
        </div>
      </fieldset>

      <div className="row">
        <button
          className="btn btn-primary"
          type="submit"
          disabled={busy || machineId === null || !description.trim() || !noticedOn || alreadyLost || disposed}
        >
          {busy ? 'Saving…' : editing ? 'Save' : 'Report the loss'}
        </button>
        <button className="btn" type="button" onClick={onCancel} disabled={busy}>Cancel</button>
      </div>
    </form>
  );
}

/**
 * /losses/new, optionally ?equipmentId=12: the page for writing up a lost machine, reached from the list or from
 * a machine's own page.
 */
export function LossNewPage() {
  usePageTitle('Report a loss');
  const navigate = useNavigate();
  const location = useLocation();
  const [params] = useSearchParams();

  const equipmentId = Number(params.get('equipmentId')) || null;
  const from = (location.state as { from?: string } | null)?.from ?? '/losses';

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <p className="crumb"><Link to={from}>← Back</Link></p>
          <h1>Report a lost machine</h1>
          <p className="muted">A machine was lost, stolen, or went missing while it was away.</p>
        </div>
      </header>

      <LossForm
        prefillMachine={equipmentId}
        onCancel={() => navigate(from)}
        onSaved={(id, message) =>
          navigate(`/losses/${id}`, { replace: true, state: { handoff: { notice: message, recorded: null } } })
        }
      />
    </div>
  );
}
