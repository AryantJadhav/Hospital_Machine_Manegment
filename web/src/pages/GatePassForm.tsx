import { useEffect, useState } from 'react';
import type { FormEvent } from 'react';
import { Link, useLocation, useNavigate, useSearchParams } from 'react-router-dom';
import { api } from '../api/client';
import { EquipmentPicker } from '../EquipmentPicker';
import { DEFAULT_NON_RETURNABLE_PURPOSE, DEFAULT_PURPOSE, describeMachine } from '../gatePassTypes';
import type { GatePassDetail } from '../gatePassTypes';
import { usePageTitle } from '../pageTitle';
import { todayAtHospital } from '../time';
import { GatePassItemsGrid } from './GatePassItemsGrid';
import { blankRow, isBlank, itemsFrom, machineRow, partRow, problemWith, rowsFrom } from './gatePassRows';
import type { ItemRow } from './gatePassRows';

/** What a part sent back from the spares register is for, until the engineer says otherwise. */
const PART_REPLACEMENT_PURPOSE = 'Part returned to the vendor for replacement';

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

/** What the spare part's own record says, for the line and for the list to pick from. */
type PartRecord = {
  id: number;
  partNumber: string;
  name: string;
  specification: string | null;
  manufacturer: string | null;
  compatibleModels: string | null;
  isReturnable: boolean;
};

/** "SpO2 sensor, adult, 3 m - Philips MX450": the same words the server writes when a line is left as it is filled in. */
function describePart(p: PartRecord): string {
  const text = p.specification ? `${p.name}, ${p.specification}` : p.name;
  const fits = [p.manufacturer, p.compatibleModels].filter((x): x is string => Boolean(x && x.trim())).join(' ');
  return fits ? `${text} - ${fits}` : text;
}

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
  prefill?: { equipmentId: number | null; workOrderId: number | null; sparePartId?: number | null };
  onCancel: () => void;
  onSaved: (id: number, message: string) => void | Promise<void>;
}) {
  const today = todayAtHospital();

  // A pass is returnable (it comes back) or not (the goods leave for good). Chosen when it is written, and then fixed.
  const [returnable, setReturnable] = useState(editing?.isReturnable ?? true);
  // What is going out: a machine from the register, or spare parts. Chosen when it is written, and then fixed. Either may come
  // back (repair, a warranty swap) or leave for good.
  const [goods, setGoods] = useState<'machine' | 'parts'>(() =>
    (!editing && prefill?.sparePartId) ||
    editing &&
    editing.items.every((i) => i.equipmentId === null) &&
    (!editing.isReturnable || editing.items.some((i) => i.sparePartId !== null))
      ? 'parts'
      : 'machine',
  );
  const [parts, setParts] = useState<PartRecord[]>([]);
  const [partPickerKey, setPartPickerKey] = useState(0);

  const [vendorName, setVendorName] = useState(editing?.vendorName ?? '');
  const [contactPerson, setContactPerson] = useState(editing?.contactPerson ?? '');
  const [contactPhone, setContactPhone] = useState(editing?.contactPhone ?? '');
  const [purpose, setPurpose] = useState(editing?.purpose ?? (prefill?.sparePartId ? PART_REPLACEMENT_PURPOSE : DEFAULT_PURPOSE));
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
  const prefillPart = prefill?.sparePartId ?? null;

  useEffect(() => {
    if (editing || (prefillMachine === null && prefillRequest === null && prefillPart === null)) return;
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

        if (machineId !== null && prefillPart === null) {
          const m = await api.get<MachineRecord>(`/api/equipment/${machineId}`);
          if (cancelled) return;
          setRows((current) => placeMachine(current, m));
        }

        // Sent back from the spares register: the part is the first line, and its supplier is who it goes to.
        if (prefillPart !== null) {
          const part = await api.get<PartRecord & { supplier: string | null }>(`/api/spare-parts/${prefillPart}`);
          if (cancelled) return;
          setRows((current) => {
            if (current.some((r) => r.sparePartId === part.id)) return current;
            const line = partRow(part.id, part.partNumber, describePart(part));
            const empty = current.findIndex(isBlank);
            if (empty < 0) return [...current, line, blankRow()];
            const next = [...current];
            next[empty] = line;
            return next;
          });
          setVendorName((name) => name || part.supplier || '');
        }
      } catch {
        if (!cancelled) setError('Could not read that machine\'s details. Add it from the register below.');
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [editing, prefillMachine, prefillRequest, prefillPart]);

  // The spares register, to add a part to the sheet from. Not needed to write a pass: a part can be typed on a line.
  useEffect(() => {
    api.get<{ items: PartRecord[] }>('/api/spare-parts?pageSize=200')
      .then((r) => setParts(r.items))
      .catch(() => undefined);
  }, []);

  function addPart(id: string) {
    const part = parts.find((p) => String(p.id) === id);
    if (!part) return;
    setError(null);
    setRows((current) => {
      const line = partRow(part.id, part.partNumber, describePart(part));
      const empty = current.findIndex(isBlank);
      if (empty >= 0) {
        const next = [...current];
        next[empty] = line;
        return next;
      }
      return [...current, line, blankRow()];
    });
    // A fresh box, so the next part is picked from the top.
    setPartPickerKey((k) => k + 1);
  }

  // What stays on the sheet when what is going out changes: the lines that suit the new choice, and always at least one line.
  const keep = (suits: (r: ItemRow) => boolean) => (current: ItemRow[]) => {
    const kept = current.filter(suits);
    return kept.length > 0 ? kept : [blankRow(), blankRow(), blankRow()];
  };

  // Changing what is going out: a machine, or spare parts. What only suits the other is cleared, not carried over.
  function chooseGoods(next: 'machine' | 'parts') {
    if (next === goods) return;
    setGoods(next);
    setError(null);
    setRows(keep(next === 'machine' ? (r) => r.sparePartId === null : (r) => r.equipmentId === null));
  }

  // Changing the kind of a new pass: the choices that only make sense for the other kind are cleared.
  function chooseKind(next: boolean) {
    if (next === returnable) return;
    setReturnable(next);
    setError(null);
    if (!next) {
      setExpected('');
      if (purpose === DEFAULT_PURPOSE) setPurpose(DEFAULT_NON_RETURNABLE_PURPOSE);
    } else if (purpose === DEFAULT_NON_RETURNABLE_PURPOSE) {
      setPurpose(DEFAULT_PURPOSE);
    }
  }

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
        expectedReturnDate: returnable ? expected || null : null,
        isReturnable: returnable,
        authorisedBy: authorisedBy.trim() || null,
        notes: notes.trim() || null,
        items: itemsFrom(rows),
      };

      if (editing) {
        await api.put(`/api/gate-passes/${editing.id}`, body);
        await onSaved(editing.id, `Saved ${editing.reference}.`);
      } else {
        const created = await api.post<{ id: number; reference: string }>('/api/gate-passes', body);
        const machines = rows.some((r) => r.equipmentId !== null);
        await onSaved(
          created.id,
          returnable
            ? `Written ${created.reference}. Print it for the gate and the vendor.${machines ? ' Its machines are now marked Under repair.' : ''}`
            : `Written ${created.reference}, a non-returnable pass. Print it for the gate and the vendor. Nothing is expected back.${machines ? ' Its machines are now marked Disposed.' : ''}`,
        );
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
        {editing
          ? `Correct ${editing.reference}`
          : `New ${returnable ? 'returnable' : 'non-returnable'} gate pass for ${goods === 'machine' ? 'a machine' : 'spare parts'}`}
      </h2>

      {!editing && (
        <fieldset className="stack" style={{ border: 'none', padding: 0, margin: 0 }}>
          <legend style={{ fontWeight: 600, padding: 0 }}>What is going out?</legend>
          <label className="row" style={{ gap: '0.5rem', alignItems: 'flex-start' }}>
            <input type="radio" name="pass-goods" checked={goods === 'machine'} onChange={() => chooseGoods('machine')} />
            <span>
              <strong>A machine.</strong> A machine from the equipment register goes to the vendor, for repair or calibration
              or for good.
            </span>
          </label>
          <label className="row" style={{ gap: '0.5rem', alignItems: 'flex-start' }}>
            <input type="radio" name="pass-goods" checked={goods === 'parts'} onChange={() => chooseGoods('parts')} />
            <span>
              <strong>Spare parts.</strong> Parts from the spares register: sent for a warranty swap or repair, or handed to
              the vendor for good.
            </span>
          </label>
        </fieldset>
      )}

      {editing && (
        <p className="muted" style={{ margin: 0 }}>
          Going out: <strong>{goods === 'machine' ? 'a machine' : 'spare parts'}</strong>
        </p>
      )}

      {!editing && (
        <fieldset className="stack" style={{ border: 'none', padding: 0, margin: 0 }}>
          <legend style={{ fontWeight: 600, padding: 0 }}>Will it come back?</legend>
          <label className="row" style={{ gap: '0.5rem', alignItems: 'flex-start' }}>
            <input type="radio" name="pass-kind" checked={returnable} onChange={() => chooseKind(true)} />
            <span>
              <strong>Returnable.</strong> {goods === 'machine'
                ? 'It goes for repair or calibration and comes back. The register marks it Under repair while it is away.'
                : 'It goes for a warranty swap or repair and comes back.'} The pass waits for the day it does.
            </span>
          </label>
          <label className="row" style={{ gap: '0.5rem', alignItems: 'flex-start' }}>
            <input type="radio" name="pass-kind" checked={!returnable} onChange={() => chooseKind(false)} />
            <span>
              <strong>Not returnable.</strong> {goods === 'machine'
                ? 'It leaves for good: sold, scrapped or given back to its owner. The register marks it Disposed.'
                : 'It leaves for good: a faulty part handed to the vendor, or scrap.'}
            </span>
          </label>
        </fieldset>
      )}

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

        {returnable && (
          <label className="field">
            <span>Expected date of return</span>
            <input type="date" value={expected} min={passDate} onChange={(e) => setExpected(e.target.value)} />
          </label>
        )}
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
        <legend style={{ fontWeight: 600, padding: 0 }}>{goods === 'machine' ? 'The machine' : 'The spare parts'}</legend>

        {goods === 'machine' && (
          <>
            <EquipmentPicker
              key={pickerKey}
              value={null}
              onChange={(id) => void addMachine(id)}
              label="Add a machine from the register"
            />
            <span className="muted">
              Type the machine&apos;s number, or its make or model. Its description and number fill in from its
              record, and the register marks it {returnable ? 'Under repair while it is away' : 'Disposed, as it does not come back'}.
              For something that is not on the register (a probe, a cable), type on a line below.
            </span>
          </>
        )}

        {goods === 'parts' && parts.length > 0 && (
          <label className="field" key={partPickerKey}>
            <span>Add a spare part from the register</span>
            <select value="" onChange={(e) => addPart(e.target.value)}>
              <option value="">Choose a part…</option>
              {[
                { label: 'Returnable parts (faulty or under warranty)', list: parts.filter((p) => p.isReturnable) },
                { label: 'Other parts', list: parts.filter((p) => !p.isReturnable) },
              ]
                .filter((group) => group.list.length > 0)
                .map((group) => (
                  <optgroup key={group.label} label={group.label}>
                    {group.list.map((p) => (
                      <option key={p.id} value={p.id}>
                        {p.partNumber} — {p.name}
                        {p.specification ? `, ${p.specification}` : ''}
                      </option>
                    ))}
                  </optgroup>
                ))}
            </select>
            <span className="muted">
              Its name, specification and part number fill in. Set the quantity on the line. This does not change the
              stock count.
            </span>
          </label>
        )}
        {!returnable && (
          <span className="muted">
            Nothing here is expected back, so there is no date of return.
            {goods === 'parts' && ' Type on a line below for anything that is not on the spares register.'}
          </span>
        )}

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
  const sparePartId = Number(params.get('sparePartId')) || null;
  const from = (location.state as { from?: string } | null)?.from ?? '/gate-passes';

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <p className="crumb"><Link to={from}>← Back</Link></p>
          <h1>New gate pass</h1>
          <p className="muted">For a machine that cannot be repaired here and has to go to the vendor, or for parts that leave for good.</p>
        </div>
      </header>

      <GatePassForm
        prefill={{ equipmentId, workOrderId, sparePartId }}
        onCancel={() => navigate(from)}
        onSaved={(id, message) =>
          navigate(`/gate-passes/${id}`, { replace: true, state: { handoff: { notice: message, recorded: null } } })
        }
      />
    </div>
  );
}
