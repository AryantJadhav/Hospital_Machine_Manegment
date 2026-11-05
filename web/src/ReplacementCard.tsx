import { useCallback, useEffect, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import { api } from './api/client';
import { useDialogs } from './dialogs';
import { formatDateTime } from './time';
import type { ReplacementOption, ReplacementOptions, WorkOrderDetail, WorkOrderReplacement } from './workOrderTypes';

/** Runs an action on the work order and says whether it worked. */
type Act = (fn: () => Promise<unknown>) => Promise<boolean>;

const kindOf = (m: { manufacturer: string | null; model: string | null }) =>
  [m.manufacturer, m.model].filter(Boolean).join(' ');

/**
 * A repair can take days and the ward still has its work to do. The engineer picks a free machine from the store, puts it where
 * the faulty one stands, and takes it back when the repair is done. A department sees which machine is standing in for theirs.
 */
export function ReplacementCard({ order, act }: { order: WorkOrderDetail; act: Act }) {
  const dialogs = useDialogs();
  const active = order.replacements.find((r) => r.isActive) ?? null;
  const past = order.replacements.filter((r) => !r.isActive);
  const [choosing, setChoosing] = useState(false);

  // Nothing to show and nothing to do: do not take room on the page.
  if (!active && past.length === 0 && !order.canProvideReplacement) return null;

  async function takeBack(r: WorkOrderReplacement) {
    const note = await dialogs.ask({
      title: `Take ${r.assetTag} back?`,
      message: `${r.assetTag} goes back to ${r.fromLocationName}, and the repaired machine is used again.`,
      label: 'Anything to note? (optional)',
      placeholder: 'e.g. Checked and cleaned before it went back',
      confirmLabel: 'Yes, take it back',
      cancelLabel: 'Not yet',
      minLength: 0,
      multiline: true,
    });
    if (note === null) return;
    void act(() => api.post(`/api/work-orders/${order.id}/replacement/return`, { note: note.trim() || null }));
  }

  return (
    <section className="card stack" aria-labelledby="replacement-title">
      <h2 className="section-h" id="replacement-title">Replacement machine</h2>

      {active ? (
        <div className="stack replacement-active" style={{ gap: '0.35rem' }}>
          <p style={{ margin: 0 }}>
            <Link to={`/equipment/${active.equipmentId}`} className="mono"><strong>{active.assetTag}</strong></Link>
            {kindOf(active) && <> — {kindOf(active)}</>}
            <span className="muted"> ({active.typeName})</span> is standing in while this machine is repaired.
          </p>
          <p className="muted" style={{ margin: 0 }}>
            Put at {active.placedAt} on {formatDateTime(active.providedAtUtc)}
            {active.providedByName && ` by ${active.providedByName}`}. It came from {active.fromLocationName}.
          </p>
          {active.note && <p style={{ margin: 0 }}>{active.note}</p>}
          {order.canTakeBackReplacement && (
            <div>
              <button className="btn" onClick={() => void takeBack(active)}>Take the replacement back</button>
            </div>
          )}
        </div>
      ) : (
        <>
          <p className="muted" style={{ margin: 0 }}>
            The repair needs time. Put a free machine from the store in its place so the work can carry on.
          </p>
          {!choosing && (
            <div>
              <button className="btn" onClick={() => setChoosing(true)}>Provide a replacement machine</button>
            </div>
          )}
        </>
      )}

      {choosing && !active && (
        <ReplacementPicker order={order} act={act} onClose={() => setChoosing(false)} />
      )}

      {past.length > 0 && (
        <ul className="timeline" aria-label="Earlier replacement machines">
          {past.map((r) => (
            <li key={r.id}>
              <span className="mono">{r.assetTag}</span>
              {kindOf(r) && <span className="muted"> ({kindOf(r)})</span>} stood in from {formatDateTime(r.providedAtUtc)}
              {r.returnedAtUtc && <> until {formatDateTime(r.returnedAtUtc)}</>}
              {r.returnedByName && <span className="muted"> · taken back by {r.returnedByName}</span>}
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

/** The list of machines that are free, same kind first, to choose from. */
function ReplacementPicker({ order, act, onClose }: { order: WorkOrderDetail; act: Act; onClose: () => void }) {
  const dialogs = useDialogs();
  const [text, setText] = useState('');
  const [otherTypes, setOtherTypes] = useState(false);
  const [data, setData] = useState<ReplacementOptions | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  // Only the answer to the latest question is shown, however slowly an earlier one comes back.
  const latest = useRef(0);

  const load = useCallback(async () => {
    const ticket = ++latest.current;
    try {
      const params = new URLSearchParams();
      if (text.trim()) params.set('q', text.trim());
      if (otherTypes) params.set('otherTypes', 'true');
      const result = await api.get<ReplacementOptions>(`/api/work-orders/${order.id}/replacement-options?${params}`);
      if (ticket !== latest.current) return;
      setData(result);
      setError(null);
    } catch (e) {
      if (ticket !== latest.current) return;
      setError(e instanceof Error ? e.message : 'Could not load the machines that are free.');
    }
  }, [order.id, text, otherTypes]);

  useEffect(() => {
    const wait = window.setTimeout(() => void load(), text ? 250 : 0);
    return () => window.clearTimeout(wait);
  }, [load, text]);

  async function use(option: ReplacementOption) {
    const note = await dialogs.ask({
      title: `Put ${option.assetTag} in place of ${order.assetTag}?`,
      message: (
        <>
          <p style={{ margin: 0 }}>
            {option.assetTag}{kindOf(option) && ` (${kindOf(option)})`} is moved from {option.locationName} to{' '}
            {data?.forMachine.locationName ?? 'where the faulty machine stands'} and is marked in use. It is taken back
            when the repair is done.
          </p>
        </>
      ),
      label: 'Anything to note? (optional)',
      placeholder: 'e.g. Same model, checked this week',
      confirmLabel: 'Yes, put it there',
      cancelLabel: 'Choose another',
      minLength: 0,
      multiline: true,
    });
    if (note === null) return;
    setBusy(true);
    const ok = await act(() =>
      api.post(`/api/work-orders/${order.id}/replacement`, { equipmentId: option.id, note: note.trim() || null }),
    );
    setBusy(false);
    if (ok) onClose();
    else void load();
  }

  const none = data !== null && data.items.length === 0;

  return (
    <div className="stack replacement-picker" style={{ gap: '0.6rem' }}>
      <div className="row" style={{ alignItems: 'center', justifyContent: 'space-between' }}>
        <strong>Machines that are free</strong>
        <button className="btn btn-quiet" onClick={onClose}>Close</button>
      </div>

      <div className="row" style={{ alignItems: 'center' }}>
        <input
          aria-label="Search the free machines"
          className="field grow"
          type="search"
          placeholder="Search by number, make, model or serial"
          value={text}
          onChange={(e) => setText(e.target.value)}
        />
        <label className="row" style={{ alignItems: 'center', gap: '0.4rem' }}>
          <input type="checkbox" checked={otherTypes} onChange={(e) => setOtherTypes(e.target.checked)} />
          Show other kinds of machine too
        </label>
      </div>

      {error && <p className="alert alert-error" role="alert">{error}</p>}
      {data === null && !error && <p className="muted" style={{ margin: 0 }}>Looking for free machines…</p>}

      {none && (
        <p className="muted" style={{ margin: 0 }}>
          {text.trim()
            ? 'No free machine matches that.'
            : otherTypes
              ? 'No machine is free in the store.'
              : `No ${data.forMachine.typeName ?? 'machine'} of this kind is free in the store. Tick “Show other kinds of machine too” to see the rest.`}
        </p>
      )}

      {data !== null && data.items.length > 0 && (
        <>
          <ul className="replacement-list" aria-label="Free machines">
            {data.items.map((o) => (
              <li key={o.id} className="replacement-option">
                <span className="replacement-main">
                  <span className="mono"><strong>{o.assetTag}</strong></span>
                  <span>{kindOf(o) || o.typeName}</span>
                  <span className="muted">
                    {kindOf(o) ? `${o.typeName} · ` : ''}in {o.locationName}
                    {o.serialNumber && ` · serial ${o.serialNumber}`}
                  </span>
                </span>
                <span className="replacement-side">
                  {o.sameModel ? (
                    <span className="pill tone-success">Same model</span>
                  ) : o.sameType ? (
                    <span className="pill">Same kind</span>
                  ) : (
                    <span className="pill tone-warning">Different kind</span>
                  )}
                  <button className="btn" disabled={busy} onClick={() => void use(o)}>Use this one</button>
                </span>
              </li>
            ))}
          </ul>
          {data.total > data.items.length && (
            <p className="muted" style={{ margin: 0 }}>
              Showing {data.items.length} of {data.total}. Search to narrow the list.
            </p>
          )}
        </>
      )}
    </div>
  );
}
