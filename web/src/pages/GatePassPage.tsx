import { useCallback, useEffect, useState } from 'react';
import type { FormEvent } from 'react';
import { Link, Navigate, useLocation, useParams } from 'react-router-dom';
import { api, ApiError } from '../api/client';
import { PERMISSIONS } from '../auth/context';
import { useAuth } from '../auth/useAuth';
import { describeDays, GATE_PASS_LABEL, GATE_PASS_TONE } from '../gatePassTypes';
import type { GatePassDetail } from '../gatePassTypes';
import { HandoffNotice } from '../HandoffNotice';
import { useHandoff } from '../handoff';
import { usePageTitle } from '../pageTitle';
import { StatusPill } from '../StatusPill';
import { formatDate, formatDateTime, todayAtHospital } from '../time';
import { GatePassForm } from './GatePassForm';

/**
 * One gate pass on its own page: /gate-passes/:id. Who it went to and why, what is on it, when it is due
 * back, and - once it is back - the day it came. Anyone who works on the equipment reads it; whoever
 * sent the machine records that it came back, corrects the pass while it is out, or cancels it.
 * A pass is never deleted.
 */
export function GatePassPage() {
  const { id: idParam } = useParams();
  const id = Number(idParam);
  const valid = Number.isInteger(id) && id > 0;

  const { may } = useAuth();
  const canEdit = may(PERMISSIONS.gatePassEdit);
  const location = useLocation();
  const [handoff, setHandoff] = useHandoff();

  const [pass, setPass] = useState<GatePassDetail | null>(null);
  const [missing, setMissing] = useState(false);
  const [editing, setEditing] = useState(false);
  const [closing, setClosing] = useState<'return' | 'cancel' | null>(null);
  const [error, setError] = useState<string | null>(null);

  usePageTitle(pass ? pass.reference : undefined);

  const load = useCallback(async () => {
    try {
      setPass(await api.get<GatePassDetail>(`/api/gate-passes/${id}`));
      setMissing(false);
    } catch (e) {
      if (e instanceof ApiError && e.status === 404) setMissing(true);
      else setError(e instanceof Error ? e.message : 'Could not open that gate pass.');
    }
  }, [id]);

  useEffect(() => {
    if (valid) void load();
  }, [valid, load]);

  if (!valid) return <Navigate to="/gate-passes" replace />;

  if (missing) {
    return (
      <div className="page stack">
        <p className="alert alert-error" role="alert">That gate pass does not exist.</p>
        <div><Link className="btn" to="/gate-passes">Back to Gate passes</Link></div>
      </div>
    );
  }

  if (!pass) {
    return (
      <div className="page">
        {error ? <p className="alert alert-error" role="alert">{error}</p> : <p className="muted">Loading…</p>}
      </div>
    );
  }

  if (editing) {
    return (
      <div className="page">
        <GatePassForm
          editing={pass}
          onCancel={() => setEditing(false)}
          onSaved={async (_id, message) => {
            setEditing(false);
            setHandoff({ notice: message, recorded: null });
            await load();
          }}
        />
      </div>
    );
  }

  const isOut = pass.status === 'Out';
  const back = (location.state as { from?: string } | null)?.from ?? '/gate-passes';

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <p className="crumb"><Link to={back}>← Back to Gate passes</Link></p>
          <h1 className="mono">{pass.reference}</h1>
          <p className="muted">
            {pass.vendorName} · written {formatDate(pass.passDate)}
          </p>
        </div>

        <div className="row">
          {/* A preview in a tab of its own first; printing or downloading is a choice made there. */}
          <a className="btn btn-primary" href={`/gate-passes/${pass.id}/pdf`} target="_blank" rel="noopener">
            View gate pass to print
          </a>
          {canEdit && isOut && closing === null && (
            <>
              <button className="btn" onClick={() => setClosing('return')}>It came back</button>
              <button className="btn" onClick={() => setEditing(true)}>Correct</button>
              <button className="btn btn-quiet" onClick={() => setClosing('cancel')}>Cancel the pass</button>
            </>
          )}
        </div>
      </header>

      <HandoffNotice handoff={handoff} />
      {error && <p className="alert alert-error" role="alert">{error}</p>}

      <div className="row">
        <StatusPill tone={GATE_PASS_TONE[pass.status]}>{GATE_PASS_LABEL[pass.status]}</StatusPill>
        {pass.isOverdue && pass.expectedReturnDate && (
          <StatusPill tone="danger">Overdue since {formatDate(pass.expectedReturnDate)}</StatusPill>
        )}
        {isOut && pass.daysOut !== null && (
          <span className="muted">Away {describeDays(pass.daysOut)}</span>
        )}
      </div>

      {closing === 'return' && (
        <CloseForm
          kind="return"
          pass={pass}
          onCancel={() => setClosing(null)}
          onDone={async (message) => {
            setClosing(null);
            setHandoff({ notice: message, recorded: null });
            await load();
          }}
          onError={setError}
        />
      )}

      {closing === 'cancel' && (
        <CloseForm
          kind="cancel"
          pass={pass}
          onCancel={() => setClosing(null)}
          onDone={async (message) => {
            setClosing(null);
            setHandoff({ notice: message, recorded: null });
            await load();
          }}
          onError={setError}
        />
      )}

      <section className="card stack">
        <h2 className="section-h">Details</h2>
        <dl className="detail">
          <dt>Name of company / person</dt>
          <dd>{pass.vendorName}</dd>
          <dt>Contact</dt>
          <dd>
            {pass.contactPerson || pass.contactPhone
              ? [pass.contactPerson, pass.contactPhone].filter(Boolean).join(' · ')
              : <span className="muted">—</span>}
          </dd>
          <dt>Purpose</dt>
          <dd>{pass.purpose}</dd>
          <dt>Date</dt>
          <dd>{formatDate(pass.passDate)}</dd>
          <dt>Expected date of return</dt>
          <dd>{pass.expectedReturnDate ? formatDate(pass.expectedReturnDate) : <span className="muted">—</span>}</dd>
          <dt>Actual date of return</dt>
          <dd>
            {pass.returnedOn
              ? `${formatDate(pass.returnedOn)}${pass.daysOut !== null ? ` (away ${describeDays(pass.daysOut)})` : ''}`
              : <span className="muted">—</span>}
          </dd>
          <dt>Request no.</dt>
          <dd>
            {pass.workOrder ? (
              <Link to={`/work-orders/${pass.workOrder.id}`} className="mono" state={{ from: `/gate-passes/${pass.id}` }}>
                {pass.workOrder.number}
              </Link>
            ) : (
              <span className="muted">—</span>
            )}
          </dd>
          <dt>Authorised by</dt>
          <dd>{pass.authorisedBy ?? <span className="muted">—</span>}</dd>
          <dt>Prepared</dt>
          <dd>
            {formatDateTime(pass.createdAtUtc)}
            {pass.createdByName && ` by ${pass.createdByName}`}
          </dd>
        </dl>
        {pass.notes && <p style={{ margin: 0 }}>{pass.notes}</p>}
        {pass.outcomeNotes && (
          <p style={{ margin: 0 }}>
            <strong>{pass.status === 'Cancelled' ? 'Why it was cancelled: ' : 'How it went: '}</strong>
            {pass.outcomeNotes}
          </p>
        )}
      </section>

      <section className="card table-wrap">
        <table className="table">
          <caption className="table-caption">Items ({pass.items.length})</caption>
          <thead>
            <tr>
              <th style={{ width: '3.5rem' }}>Sr. No.</th>
              <th>Description</th>
              <th>Asset code</th>
              <th>Quantity</th>
              <th>Remarks</th>
            </tr>
          </thead>
          <tbody>
            {pass.items.map((i, n) => (
              <tr key={i.id}>
                <td className="muted">{n + 1}</td>
                <td>
                  {i.description}
                  {i.machine?.locationName && <div className="muted">Taken from {i.machine.locationName}</div>}
                </td>
                <td className="mono">
                  {i.machine ? (
                    <Link to={`/equipment/${i.machine.id}`} state={{ from: `/gate-passes/${pass.id}` }}>{i.assetCode ?? i.machine.assetTag}</Link>
                  ) : (
                    i.assetCode ?? <span className="muted">NA</span>
                  )}
                </td>
                <td>{i.quantity.toLocaleString('en-IN')}</td>
                <td>{i.remarks ?? <span className="muted">—</span>}</td>
              </tr>
            ))}
          </tbody>
          <tfoot>
            <tr>
              <td colSpan={3} style={{ textAlign: 'right' }}><strong>Total</strong></td>
              <td><strong>{pass.totalQuantity.toLocaleString('en-IN')}</strong></td>
              <td />
            </tr>
          </tfoot>
        </table>
      </section>
    </div>
  );
}

/**
 * Closing a pass that is out: it came back (the day, and how it went), or it was never used (and why).
 * Neither can be undone, so each says so and asks.
 */
function CloseForm({
  kind,
  pass,
  onCancel,
  onDone,
  onError,
}: {
  kind: 'return' | 'cancel';
  pass: GatePassDetail;
  onCancel: () => void;
  onDone: (message: string) => void | Promise<void>;
  onError: (message: string | null) => void;
}) {
  const today = todayAtHospital();
  const [day, setDay] = useState(today);
  const [notes, setNotes] = useState('');
  const [busy, setBusy] = useState(false);

  const returning = kind === 'return';
  const hasMachines = pass.items.some((i) => i.equipmentId !== null);

  async function submit(e: FormEvent) {
    e.preventDefault();
    onError(null);
    setBusy(true);
    try {
      if (returning) {
        await api.post(`/api/gate-passes/${pass.id}/return`, { returnedOn: day, notes: notes.trim() || null });
        await onDone(`Recorded that ${pass.reference} came back on ${formatDate(day)}.${hasMachines ? ' Its machines are back on the register as in use.' : ''}`);
      } else {
        await api.post(`/api/gate-passes/${pass.id}/cancel`, { notes: notes.trim() || null });
        await onDone(`Cancelled ${pass.reference}. Its number stays on the list.${hasMachines ? ' Its machines are back on the register as they were.' : ''}`);
      }
    } catch (err) {
      onError(err instanceof Error ? err.message : 'Could not save that.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="card stack" onSubmit={submit} aria-label={returning ? 'Record that it came back' : 'Cancel the pass'}>
      <h2 className="section-h" style={{ margin: 0 }}>{returning ? 'It came back' : 'Cancel this gate pass'}</h2>

      {returning ? (
        <label className="field">
          <span>Day it came back</span>
          <input type="date" value={day} min={pass.passDate} max={today} onChange={(e) => setDay(e.target.value)} required />
        </label>
      ) : (
        <p className="muted" style={{ margin: 0 }}>
          Use this when the machine never left. The number stays on the list as cancelled, the register puts the
          machine back as it was, and it can be sent out on a new pass.
        </p>
      )}

      <label className="field">
        <span>{returning ? 'How it went (optional)' : 'Why (optional)'}</span>
        <textarea
          rows={3}
          value={notes}
          maxLength={4000}
          onChange={(e) => setNotes(e.target.value)}
          placeholder={returning ? 'e.g. Repaired and tested OK, or not repairable and replaced' : 'e.g. Vendor collected it another day'}
        />
      </label>

      <span className="muted">
        This cannot be undone: a pass that is back or cancelled can no longer be changed.
        {hasMachines && returning && ' Its machines go back to in use on the register, unless they have been condemned or changed by hand since.'}
      </span>

      <div className="row">
        <button className="btn btn-primary" type="submit" disabled={busy || (returning && !day)}>
          {busy ? 'Saving…' : returning ? 'Record that it came back' : 'Cancel the pass'}
        </button>
        <button className="btn" type="button" onClick={onCancel} disabled={busy}>Not now</button>
      </div>
    </form>
  );
}
