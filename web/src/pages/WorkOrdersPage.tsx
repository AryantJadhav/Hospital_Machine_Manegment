import { useCallback, useEffect, useRef, useState } from 'react';
import type { FormEvent } from 'react';
import { useSearchParams } from 'react-router-dom';
import { api } from '../api/client';
import { useAuth } from '../auth/useAuth';
import { ROLES } from '../auth/context';
import { EquipmentPicker } from '../EquipmentPicker';

type WorkOrderRow = {
  id: number;
  number: string;
  status: number;
  priority: number;
  faultDescription: string;
  equipmentId: number;
  assetTag: string;
  equipmentTypeName: string;
  locationName: string;
  reportedAtUtc: string;
  assignedToUserId: number | null;
  outOfServiceAtUtc: string | null;
  backInServiceAtUtc: string | null;
};

type WorkOrderDetail = WorkOrderRow & {
  reportedByUserId: number;
  assignedAtUtc: string | null;
  startedAtUtc: string | null;
  resolutionNotes: string | null;
  resolvedAtUtc: string | null;
  closedAtUtc: string | null;
  downtimeMinutes: number | null;
  allowedTransitions: number[];
  notes: { id: number; body: string; statusAfter: number | null; authorUserId: number; createdAtUtc: string }[];
};

const STATUS: Record<number, string> = {
  10: 'Reported',
  20: 'Assigned',
  30: 'In progress',
  40: 'On hold',
  50: 'Resolved',
  60: 'Closed',
  70: 'Cancelled',
};

const PRIORITY: Record<number, string> = { 10: 'Low', 20: 'Medium', 30: 'High', 40: 'Critical' };

export function WorkOrdersPage() {
  const { can } = useAuth();
  const canAssign = can(ROLES.admin);

  const [params, setParams] = useSearchParams();
  const status = params.get('status') ?? '';

  const [rows, setRows] = useState<WorkOrderRow[]>([]);
  const [selected, setSelected] = useState<WorkOrderDetail | null>(null);
  const [reporting, setReporting] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const q = new URLSearchParams({ pageSize: '100' });
      if (status) q.set('status', status);
      const data = await api.get<{ items: WorkOrderRow[] }>(`/api/work-orders?${q}`);
      setRows(data.items);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not load work orders.');
    } finally {
      setLoading(false);
    }
  }, [status]);

  useEffect(() => {
    void load();
  }, [load]);

  async function open(id: number) {
    try {
      setSelected(await api.get<WorkOrderDetail>(`/api/work-orders/${id}`));
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not open that work order.');
    }
  }

  async function act(fn: () => Promise<unknown>) {
    setError(null);
    try {
      await fn();
      if (selected) await open(selected.id);
      await load();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'That action failed.');
    }
  }

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <h1>Work orders</h1>
          <p className="muted">Breakdowns and unscheduled repairs.</p>
        </div>
        <button className="btn btn-primary" onClick={() => setReporting(true)}>Report a fault</button>
      </header>

      {error && <p className="alert alert-error" role="alert">{error}</p>}

      {reporting && (
        <ReportForm
          onCancel={() => setReporting(false)}
          onDone={async () => { setReporting(false); await load(); }}
          onError={setError}
        />
      )}

      <div className="filters card">
        <select
          aria-label="Filter by status"
          value={status}
          onChange={(e) => {
            const next = new URLSearchParams(params);
            if (e.target.value) next.set('status', e.target.value);
            else next.delete('status');
            setParams(next, { replace: true });
          }}
        >
          <option value="">Open only</option>
          {Object.entries(STATUS).map(([v, label]) => (
            <option key={v} value={v}>{label}</option>
          ))}
        </select>
      </div>

      <div className="wo-grid">
        <div className="card table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th>Number</th>
                <th>Priority</th>
                <th>Asset</th>
                <th>Fault</th>
                <th>Status</th>
              </tr>
            </thead>
            <tbody>
              {loading && <tr><td colSpan={5} className="empty">Loading…</td></tr>}

              {!loading && rows.length === 0 && (
                <tr><td colSpan={5} className="empty">No work orders match.</td></tr>
              )}

              {!loading && rows.map((w) => (
                <tr
                  key={w.id}
                  onClick={() => void open(w.id)}
                  className={selected?.id === w.id ? 'row-selected' : 'row-clickable'}
                >
                  <td className="mono">{w.number}</td>
                  <td><span className={`pill prio-${w.priority}`}>{PRIORITY[w.priority]}</span></td>
                  <td className="mono">{w.assetTag}</td>
                  <td className="truncate">{w.faultDescription}</td>
                  <td><span className={`pill wo-${w.status}`}>{STATUS[w.status]}</span></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>

        <div className="card">
          {selected ? (
            <Detail order={selected} canAssign={canAssign} act={act} />
          ) : (
            <p className="muted" style={{ margin: 0 }}>Select a work order to see its history.</p>
          )}
        </div>
      </div>
    </div>
  );
}

function Detail({
  order,
  canAssign,
  act,
}: {
  order: WorkOrderDetail;
  canAssign: boolean;
  act: (fn: () => Promise<unknown>) => Promise<void>;
}) {
  const [note, setNote] = useState('');
  const [resolution, setResolution] = useState('');
  const [resolveProblem, setResolveProblem] = useState<string | null>(null);
  const resolutionBox = useRef<HTMLTextAreaElement>(null);
  const [staff, setStaff] = useState<{ id: number; fullName: string }[]>([]);

  // Only the people who can actually be sent to a machine. Loaded here rather
  // than with the list because it is only needed once a work order is open,
  // and only for someone who may assign - the staff list is an Admin route
  // now, so asking for it as an Employee is a guaranteed 403.
  useEffect(() => {
    if (!canAssign) return;
    void (async () => {
      try {
        setStaff(await api.get<{ id: number; fullName: string }[]>('/api/users'));
      } catch {
        // Assignment degrades to unavailable rather than breaking the page.
      }
    })();
  }, [canAssign]);

  // The button stays clickable when the box is empty and says what is missing.
  // A greyed-out button explained only by a placeholder reads as broken: the
  // engineer clicks it, nothing happens, and nothing says why.
  function resolve() {
    if (!resolution.trim()) {
      setResolveProblem(
        'Say what was wrong and what you did before resolving. It goes on the service report.',
      );
      resolutionBox.current?.focus();
      return;
    }
    void act(() =>
      api.post(`/api/work-orders/${order.id}/resolve`, {
        resolutionNotes: resolution,
        returnToService: true,
      }),
    );
  }

  // Only what the server will actually accept. Offering a button that
  // returns 409 teaches people to distrust the buttons.
  const transitions = order.allowedTransitions.filter((s) => s !== 50);
  const canResolve = order.allowedTransitions.includes(50);

  return (
    <div className="stack">
      <div>
        <h2 style={{ margin: 0, fontSize: '1.2rem' }}>{order.number}</h2>
        <p className="muted" style={{ margin: '0.2rem 0 0' }}>
          {order.assetTag} · {order.equipmentTypeName} · {order.locationName}
        </p>
      </div>

      <div className="row">
        <span className={`pill wo-${order.status}`}>{STATUS[order.status]}</span>
        <span className={`pill prio-${order.priority}`}>{PRIORITY[order.priority]}</span>
        {order.downtimeMinutes !== null && (
          <span className="pill">Down {formatDuration(order.downtimeMinutes)}</span>
        )}
      </div>

      <div>
        <div className="muted" style={{ fontSize: '0.8rem' }}>Reported fault</div>
        <p style={{ margin: '0.2rem 0 0' }}>{order.faultDescription}</p>
      </div>

      {order.resolutionNotes && (
        <div>
          <div className="muted" style={{ fontSize: '0.8rem' }}>Work carried out</div>
          <p style={{ margin: '0.2rem 0 0' }}>{order.resolutionNotes}</p>
        </div>
      )}

      {order.notes.length > 0 && (
        <div>
          <div className="muted" style={{ fontSize: '0.8rem', marginBottom: '0.35rem' }}>Timeline</div>
          <ul className="timeline">
            {order.notes.map((n) => (
              <li key={n.id}>
                <span className="muted mono">{n.createdAtUtc.slice(0, 16).replace('T', ' ')}</span>
                {n.statusAfter !== null && (
                  <span className="pill" style={{ marginLeft: '0.4rem' }}>{STATUS[n.statusAfter]}</span>
                )}
                <div>{n.body}</div>
              </li>
            ))}
          </ul>
        </div>
      )}

      {transitions.length > 0 && (
        <div className="row">
          {transitions.map((s) => (
            <button
              key={s}
              className="btn"
              onClick={() =>
                void act(() => api.post(`/api/work-orders/${order.id}/status`, { status: s, note: note || undefined }))
              }
            >
              {STATUS[s]}
            </button>
          ))}

          {/* Was hardcoded to user id 1 — not "me", just whoever happened to
              be first — because until staff accounts existed there was nobody
              else to assign to. Now it offers the actual engineers. */}
          {canAssign && order.assignedToUserId === null && staff.length > 0 && (
            <select
              aria-label="Assign to"
              className="field"
              style={{ maxWidth: '14rem' }}
              defaultValue=""
              onChange={(e) => {
                const id = Number(e.target.value);
                if (id) void act(() => api.post(`/api/work-orders/${order.id}/assign`, { assignedToUserId: id }));
              }}
            >
              <option value="">Assign to…</option>
              {staff.map((p) => (
                <option key={p.id} value={p.id}>{p.fullName}</option>
              ))}
            </select>
          )}
        </div>
      )}

      {canResolve && (
        <div className="stack">
          <textarea
            ref={resolutionBox}
            className="grow"
            rows={3}
            placeholder="What was wrong and what you did (required to resolve)"
            aria-label="What was wrong and what you did"
            aria-invalid={resolveProblem !== null}
            value={resolution}
            onChange={(e) => {
              setResolution(e.target.value);
              if (e.target.value.trim()) setResolveProblem(null);
            }}
          />
          {resolveProblem && <p className="alert alert-error" role="alert">{resolveProblem}</p>}
          <button className="btn btn-primary" onClick={resolve}>
            Resolve and return to service
          </button>
        </div>
      )}

      <div className="row">
        <input
          className="grow"
          placeholder="Add a note"
          aria-label="Add a note"
          value={note}
          onChange={(e) => setNote(e.target.value)}
        />
        <button
          className="btn"
          disabled={!note.trim()}
          onClick={() =>
            void act(async () => {
              await api.post(`/api/work-orders/${order.id}/notes`, { body: note });
              setNote('');
            })
          }
        >
          Add
        </button>
      </div>

      <button
        className="btn"
        onClick={() => api.download(`/api/reports/work-orders/${order.id}/report.pdf`, `${order.number}.pdf`)}
      >
        Download service report
      </button>
    </div>
  );
}

function ReportForm({
  onCancel,
  onDone,
  onError,
}: {
  onCancel: () => void;
  onDone: () => void | Promise<void>;
  onError: (msg: string | null) => void;
}) {
  const [equipmentId, setEquipmentId] = useState<number | null>(null);
  const [fault, setFault] = useState('');
  const [priority, setPriority] = useState(20);
  const [outOfService, setOutOfService] = useState(false);
  const [busy, setBusy] = useState(false);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    onError(null);
    try {
      await api.post('/api/work-orders', {
        equipmentId,
        faultDescription: fault,
        priority,
        outOfService,
      });
      await onDone();
    } catch (err) {
      onError(err instanceof Error ? err.message : 'Could not report the fault.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="card stack" onSubmit={submit}>
      <h2 style={{ margin: 0, fontSize: '1.05rem' }}>Report a fault</h2>

      <div className="filters">
        <EquipmentPicker value={equipmentId} onChange={setEquipmentId} />

        <label className="field">
          <span>Priority</span>
          <select value={priority} onChange={(e) => setPriority(Number(e.target.value))}>
            {Object.entries(PRIORITY).map(([v, label]) => (
              <option key={v} value={v}>{label}</option>
            ))}
          </select>
        </label>
      </div>

      <label className="field">
        <span>What is wrong</span>
        <textarea
          rows={3}
          value={fault}
          onChange={(e) => setFault(e.target.value)}
          placeholder="Describe the fault as the ward reported it"
          required
        />
      </label>

      <label className="row" style={{ alignItems: 'center', gap: '0.5rem' }}>
        <input
          type="checkbox"
          checked={outOfService}
          onChange={(e) => setOutOfService(e.target.checked)}
        />
        {/* Downtime is measured from here, not from when an engineer
            arrives, so this checkbox is what makes uptime reporting honest. */}
        <span>The machine cannot be used (starts downtime now)</span>
      </label>

      <div className="row">
        <button className="btn btn-primary" type="submit" disabled={busy || equipmentId === null || !fault.trim()}>
          {busy ? 'Reporting…' : 'Report fault'}
        </button>
        <button className="btn" type="button" onClick={onCancel}>Cancel</button>
      </div>
    </form>
  );
}

function formatDuration(minutes: number): string {
  if (minutes < 60) return `${minutes} min`;
  const days = Math.floor(minutes / 1440);
  const hours = Math.floor((minutes % 1440) / 60);
  return days > 0 ? `${days}d ${hours}h` : `${hours}h ${minutes % 60}m`;
}
