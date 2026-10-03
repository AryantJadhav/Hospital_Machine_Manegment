import { useEffect, useState } from 'react';
import type { FormEvent } from 'react';
import { Link, useLocation, useNavigate, useSearchParams } from 'react-router-dom';
import { api } from '../api/client';
import { EquipmentPicker } from '../EquipmentPicker';
import { DEFAULT_PURPOSE, describeMachine } from '../gatePassTypes';
import type { GatePassDetail } from '../gatePassTypes';
import { usePageTitle } from '../pageTitle';
import { todayAtHospital } from '../time';
import { GatePassItemsGrid } from './GatePassItemsGrid';
import { blankRow, isBlank, itemsFrom, machineRow, problemWith, rowsFrom } from './gatePassRows';
import type { ItemRow } from './gatePassRows';

/** What the machine's own record says, for the words that fill in once it is chosen. */
type MachineRecord = {
  id: number;
  assetTag: string;
  equipmentTypeName: string | null;
  manufacturer: string | null;
  model: string | null;
  serialNumber: string | null;
};

type RequestRecord = { id: number; number: string; equipmentId: number };

/**
 * Writing a gate pass, or correcting one that is still out.
 *
 * It follows the paper: who the goods are going to, what for, when they are due back, and a table of the
 * items. A machine is added by its number and its words are read from its own record; a cable or a probe
 * is typed on a line of its own.
 */
export function GatePassForm({
  editing,
  prefill,
  onCancel,
  onSaved,
}: {
  editing?: GatePassDetail;
  /** From a machine's page or a service request: send this machine out, for that request. */
  prefill?: { equipmentId: number | null; workOrderId: number | null };
  onCancel: () => void;
  onSaved: (id: number, message: string) => void | Promise<void>;
}) {
  const today = todayAtHospital();

  const [vendorName, setVendorName] = useState(editing?.vendorName ?? '');
  const [contactPerson, setContactPerson] = useState(editing?.contactPerson ?? '');
  const [contactPhone, setContactPhone] = useState(editing?.contactPhone ?? '');
  const [purpose, setPurpose] = useState(editing?.purpose ?? DEFAULT_PURPOSE);
  const [passDate, setPassDate] = useState(editing?.passDate ?? today);
  const [expected, setExpected] = useState(editing?.expectedReturnDate ?? '');
  const [authorisedBy, setAuthorisedBy] = useState(editing?.authorisedBy ?? '');
  const [notes, setNotes] = useState(editing?.notes ?? '');
  const [request, setRequest] = useState<{ id: number; number: string } | null>(
    editing?.workOrder ? { id: editing.workOrder.id, number: editing.workOrder.number } : null,
  );

  // A few empty lines to start on, or the items already on the pass and one to add to.
  const [rows, setRows] = useState<ItemRow[]>(() =>
    rowsFrom(editing?.items ?? [], editing && editing.items.length > 0 ? editing.items.length + 1 : 3),
  );
  const [pickerKey, setPickerKey] = useState(0);

  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Arriving from a machine or from a service request: that machine is the first item, that request is the Request No.
  const prefillMachine = prefill?.equipmentId ?? null;
  const prefillRequest = prefill?.workOrderId ?? null;

  useEffect(() => {
    if (editing || (prefillMachine === null && prefillRequest === null)) return;
    let cancelled = false;
    (async () => {
      try {
        let machineId = prefillMachine;

        if (prefillRequest !== null) {
          const wo = await api.get<RequestRecord>(`/api/work-orders/${prefillRequest}`);
          if (cancelled) return;
          setRequest({ id: wo.id, number: wo.number });
          machineId ??= wo.equipmentId;
        }

        if (machineId !== null) {
          const m = await api.get<MachineRecord>(`/api/equipment/${machineId}`);
          if (cancelled) return;
          setRows((current) => placeMachine(current, m));
        }
      } catch {
        if (!cancelled) setError('Could not read that machine\'s details. Add it from the register below.');
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [editing, prefillMachine, prefillRequest]);

  async function addMachine(id: number | null) {
    if (id === null) return;
    setError(null);

    if (rows.some((r) => r.equipmentId === id)) {
      setError('That machine is already on the sheet.');
      setPickerKey((k) => k + 1);
      return;
    }

    try {
      const m = await api.get<MachineRecord>(`/api/equipment/${id}`);
      setRows((current) => placeMachine(current, m));
    } catch {
      setError('Could not read that machine\'s details.');
    }
    // A fresh box, so the next machine is looked for from nothing.
    setPickerKey((k) => k + 1);
  }

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError(null);

    const problem = problemWith(rows);
    if (problem) {
      setError(problem);
      return;
    }

    setBusy(true);
    try {
      const body = {
        passDate,
        vendorName: vendorName.trim(),
        contactPerson: contactPerson.trim() || null,
        contactPhone: contactPhone.trim() || null,
        purpose: purpose.trim() || null,
        workOrderId: request?.id ?? null,
        expectedReturnDate: expected || null,
        authorisedBy: authorisedBy.trim() || null,
        notes: notes.trim() || null,
        items: itemsFrom(rows),
      };

      if (editing) {
        await api.put(`/api/gate-passes/${editing.id}`, body);
        await onSaved(editing.id, `Saved ${editing.reference}.`);
      } else {
        const created = await api.post<{ id: number; reference: string }>('/api/gate-passes', body);
        await onSaved(created.id, `Written ${created.reference}. Print it for the gate and the vendor.`);
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not save the gate pass.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="card stack" onSubmit={submit}>
      <h2 className="section-h" style={{ margin: 0 }}>
        {editing ? `Correct ${editing.reference}` : 'New returnable gate pass'}
      </h2>

      {error && <p className="alert alert-error" role="alert">{error}</p>}

      <div className="filters">
        <label className="field grow">
          <span>Name of company / person</span>
          <input
            value={vendorName}
            onChange={(e) => setVendorName(e.target.value)}
            maxLength={200}
            required
            placeholder="e.g. Bet Medical"
          />
        </label>

        <label className="field">
          <span>Contact person (optional)</span>
          <input value={contactPerson} onChange={(e) => setContactPerson(e.target.value)} maxLength={200} placeholder="e.g. Rupesh" />
        </label>

        <label className="field">
          <span>Phone (optional)</span>
          <input value={contactPhone} onChange={(e) => setContactPhone(e.target.value)} maxLength={50} inputMode="tel" />
        </label>
      </div>

      <div className="filters">
        <label className="field grow">
          <span>Purpose</span>
          <input value={purpose} onChange={(e) => setPurpose(e.target.value)} maxLength={200} />
        </label>

        <label className="field">
          <span>Date</span>
          <input
            type="date"
            value={passDate}
            max={today}
            onChange={(e) => setPassDate(e.target.value)}
            required
          />
        </label>

        <label className="field">
          <span>Expected date of return</span>
          <input type="date" value={expected} min={passDate} onChange={(e) => setExpected(e.target.value)} />
        </label>
      </div>

      <div className="filters">
        <label className="field grow">
          <span>Authorised by (optional)</span>
          <input
            value={authorisedBy}
            onChange={(e) => setAuthorisedBy(e.target.value)}
            maxLength={200}
            placeholder="Who signs the pass"
          />
        </label>

        <div className="field">
          <span>Request no.</span>
          {request ? (
            <span className="row" style={{ alignItems: 'center' }}>
              <Link to={`/work-orders/${request.id}`} className="mono">{request.number}</Link>
              <button type="button" className="btn btn-quiet" onClick={() => setRequest(null)}>Remove</button>
            </span>
          ) : (
            <span className="muted">
              None. Use Send out for repair on a service request to tie the pass to it.
            </span>
          )}
        </div>
      </div>

      <fieldset className="stack" style={{ border: 'none', padding: 0, margin: 0 }}>
        <legend style={{ fontWeight: 600, padding: 0 }}>Items</legend>

        <EquipmentPicker
          key={pickerKey}
          value={null}
          onChange={(id) => void addMachine(id)}
          label="Add a machine from the register"
        />
        <span className="muted">
          Type the machine&apos;s number, or its make or model. Its description and number fill in from its
          record. For something that is not on the register (a probe, a cable), type on a line below.
        </span>

        <GatePassItemsGrid rows={rows} onChange={setRows} />
      </fieldset>

      <label className="field">
        <span>Notes (optional)</span>
        <textarea rows={3} value={notes} onChange={(e) => setNotes(e.target.value)} maxLength={4000} />
      </label>

      <div className="row">
        <button className="btn btn-primary" type="submit" disabled={busy || !vendorName.trim() || !passDate}>
          {busy ? 'Saving…' : editing ? 'Save' : 'Write the gate pass'}
        </button>
        <button className="btn" type="button" onClick={onCancel} disabled={busy}>Cancel</button>
      </div>
    </form>
  );
}

/** Puts a machine on the first empty line of the sheet, or on a new one. */
function placeMachine(rows: ItemRow[], m: MachineRecord): ItemRow[] {
  if (rows.some((r) => r.equipmentId === m.id)) return rows;

  const line = machineRow(m.id, m.assetTag, describeMachine(m));
  const empty = rows.findIndex(isBlank);
  if (empty >= 0) {
    const next = [...rows];
    next[empty] = line;
    return next;
  }
  return [...rows, line, blankRow()];
}

/**
 * /gate-passes/new, optionally ?equipmentId=12&workOrderId=3: the page for writing a pass, reached from the
 * list, from a machine, or from a service request.
 */
export function GatePassNewPage() {
  usePageTitle('New gate pass');
  const navigate = useNavigate();
  const location = useLocation();
  const [params] = useSearchParams();

  const equipmentId = Number(params.get('equipmentId')) || null;
  const workOrderId = Number(params.get('workOrderId')) || null;
  const from = (location.state as { from?: string } | null)?.from ?? '/gate-passes';

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <p className="crumb"><Link to={from}>← Back</Link></p>
          <h1>New gate pass</h1>
          <p className="muted">For a machine that cannot be repaired here and has to go to the vendor.</p>
        </div>
      </header>

      <GatePassForm
        prefill={{ equipmentId, workOrderId }}
        onCancel={() => navigate(from)}
        onSaved={(id, message) =>
          navigate(`/gate-passes/${id}`, { replace: true, state: { handoff: { notice: message, recorded: null } } })
        }
      />
    </div>
  );
}
