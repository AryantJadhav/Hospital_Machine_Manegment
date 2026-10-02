import { useCallback, useEffect, useState } from 'react';
import type { FormEvent, MouseEvent } from 'react';
import { Link, useLocation, useNavigate, useSearchParams } from 'react-router-dom';
import { api } from '../api/client';
import { EquipmentPicker } from '../EquipmentPicker';
import { HandoffNotice } from '../HandoffNotice';
import { useHandoff } from '../handoff';
import { StatusPill } from '../StatusPill';
import { PRIORITY_LABEL, PRIORITY_LOOK, WORK_ORDER_LABEL, WORK_ORDER_LOOK } from '../statusTones';
import { formatAge, formatDateTime } from '../time';
import type { WorkOrderRow } from '../workOrderTypes';

type Paged<T> = { items: T[]; total: number; page: number; pageSize: number };

const PAGE_SIZE = 25;

/**
 * The work orders, as a list. Opening one goes to its own page (/work-orders/:id),
 * which is told where it came from so Back returns here with the filters and the
 * page as they were left.
 */
export function WorkOrdersPage() {
  const [params, setParams] = useSearchParams();
  const status = params.get('status') ?? '';
  const priority = params.get('priority') ?? '';
  const q = params.get('q') ?? '';
  // Only the faults that have a machine out of service: what the dashboard's "Machines down" counts.
  const down = params.get('down') === '1';
  const page = Math.max(1, Number(params.get('page')) || 1);
  // "me" is worked out by the server from who is signed in.
  const mine = params.get('assignee') === 'me';

  const [data, setData] = useState<Paged<WorkOrderRow> | null>(null);
  const [search, setSearch] = useState(q);
  const [reporting, setReporting] = useState(false);
  // The machine to report against when we were sent here from its own page, and
  // the page to go back to afterwards. Both are absent when someone simply
  // pressed Report a fault on this list.
  const [reportFor, setReportFor] = useState<{ id: number; label: string } | null>(null);
  const location = useLocation();
  const navigate = useNavigate();
  const from = (location.state as { from?: string } | null)?.from ?? null;
  const reportParam = params.get('report');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  // What the page we were sent back from just did, like a work order that was resolved.
  const [handoff] = useHandoff();

  // Where a work order's page sends you back to: this list, as it is now.
  const here = `${location.pathname}${location.search}`;

  // One place to change a filter, so changing any of them goes back to the first page.
  const setParam = useCallback(
    (key: string, value: string) => {
      const next = new URLSearchParams(params);
      if (value) next.set(key, value);
      else next.delete(key);
      if (key !== 'page') next.delete('page');
      setParams(next, { replace: true });
    },
    [params, setParams],
  );

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const query = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE) });
      if (status) query.set('status', status);
      if (priority) query.set('priority', priority);
      if (q) query.set('q', q);
      if (down) query.set('down', 'true');
      if (mine) query.set('assignee', 'me');
      setData(await api.get<Paged<WorkOrderRow>>(`/api/work-orders?${query}`));
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not load work orders.');
    } finally {
      setLoading(false);
    }
  }, [page, status, priority, q, down, mine]);

  useEffect(() => {
    void load();
  }, [load]);

  // The search box is typed into freely and applied a moment after the typing stops, so each
  // keystroke is not a request.
  useEffect(() => {
    if (search.trim() === q) return;
    const t = window.setTimeout(() => setParam('q', search.trim()), 300);
    return () => window.clearTimeout(t);
  }, [search, q, setParam]);

  // Arriving from a machine's page with ?report=<id>: open the form with that
  // machine already in it. It used to land on the plain list and leave the
  // technician to press Report a fault and search for the machine they were
  // standing at.
  useEffect(() => {
    if (!reportParam) return;
    let cancelled = false;
    (async () => {
      try {
        const m = await api.get<{ id: number; assetTag: string; equipmentTypeName: string }>(
          `/api/equipment/${reportParam}`,
        );
        if (cancelled) return;
        setReportFor({ id: m.id, label: `${m.assetTag} — ${m.equipmentTypeName}` });
        setReporting(true);
      } catch {
        // Unknown machine: fall back to the plain form rather than a dead end.
        if (!cancelled) setReporting(true);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [reportParam]);

  function closeReport() {
    setReporting(false);
    setReportFor(null);
    if (reportParam) {
      const next = new URLSearchParams(params);
      next.delete('report');
      setParams(next, { replace: true });
    }
  }

  function open(id: number) {
    navigate(`/work-orders/${id}`, { state: { from: here } });
  }

  // The number and the asset tag are real links, so a click on them is the link's own. A click
  // anywhere else on the row opens the work order, which is the larger target for a finger.
  function rowClick(e: MouseEvent<HTMLTableRowElement>, id: number) {
    if ((e.target as HTMLElement).closest('a')) return;
    open(id);
  }

  const totalPages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;
  const hasFilters = Boolean(status || priority || q || down);
  const columns = mine ? 6 : 7;

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <h1>{mine ? 'My work orders' : 'Work orders'}</h1>
          <p className="muted">
            {mine
              ? 'Faults assigned to you that are still to be done.'
              : 'Breakdowns and unscheduled repairs.'}
            {data && ` ${data.total.toLocaleString('en-IN')} ${data.total === 1 ? 'work order' : 'work orders'}${hasFilters ? ' match.' : '.'}`}
          </p>
        </div>
        <button className="btn btn-primary" onClick={() => setReporting(true)}>Report a fault</button>
      </header>

      <HandoffNotice handoff={handoff} />

      {error && <p className="alert alert-error" role="alert">{error}</p>}

      {down && (
        <p className="alert alert-info" role="status">
          Showing only the faults that have a machine out of service.{' '}
          <button className="btn btn-quiet" onClick={() => setParam('down', '')}>Show all work orders</button>
        </p>
      )}

      {reporting && (
        <ReportForm
          initial={reportFor}
          onCancel={() => (from ? navigate(from) : closeReport())}
          onDone={async (created) => {
            // Back where they were standing, with the fault said there. From the list, straight
            // into the new work order, which is where the next thing to do is.
            if (from) {
              navigate(from, { replace: true, state: { handoff: { notice: 'Fault reported.', recorded: null } } });
              return;
            }
            navigate(`/work-orders/${created.id}`, {
              replace: true,
              state: { from: '/work-orders', handoff: { notice: `Fault reported as ${created.number}.`, recorded: null } },
            });
          }}
          onError={setError}
        />
      )}

      <div className="filters card">
        <input
          type="search"
          className="grow"
          aria-label="Search work orders"
          placeholder="Find a work order: number, asset tag or what was wrong"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />
        <select
          aria-label="Whose work orders"
          value={mine ? 'me' : ''}
          onChange={(e) => setParam('assignee', e.target.value)}
        >
          <option value="">Everyone's</option>
          <option value="me">Assigned to me</option>
        </select>
        <select aria-label="Filter by status" value={status} onChange={(e) => setParam('status', e.target.value)}>
          <option value="">{mine ? 'Still to do' : 'Open only'}</option>
          {Object.entries(WORK_ORDER_LABEL).map(([v, label]) => (
            <option key={v} value={v}>{label}</option>
          ))}
        </select>
        <select aria-label="Filter by priority" value={priority} onChange={(e) => setParam('priority', e.target.value)}>
          <option value="">Any priority</option>
          {Object.entries(PRIORITY_LABEL).map(([v, label]) => (
            <option key={v} value={v}>{label}</option>
          ))}
        </select>
      </div>

      <div className="card table-wrap">
        <table className="table">
          <thead>
            <tr>
              <th>Number</th>
              <th>Priority</th>
              <th>Asset</th>
              <th>Fault</th>
              <th>Status</th>
              <th>Reported</th>
              {!mine && <th>Assigned to</th>}
            </tr>
          </thead>
          <tbody>
            {loading && !data && <tr><td colSpan={columns} className="empty">Loading…</td></tr>}

            {data && data.items.length === 0 && (
              <tr>
                <td colSpan={columns} className="empty">
                  {mine && !hasFilters ? 'Nothing is assigned to you right now.' : 'No work orders match.'}
                </td>
              </tr>
            )}

            {data?.items.map((w) => (
              <tr key={w.id} className="row-clickable" onClick={(e) => rowClick(e, w.id)}>
                <td className="mono">
                  <Link to={`/work-orders/${w.id}`} state={{ from: here }}>{w.number}</Link>
                </td>
                <td><StatusPill look={PRIORITY_LOOK[w.priority]}>{PRIORITY_LABEL[w.priority]}</StatusPill></td>
                <td className="mono">
                  <Link to={`/equipment/${w.equipmentId}`} state={{ from: here }}>{w.assetTag}</Link>
                </td>
                <td className="truncate">{w.faultDescription}</td>
                <td><StatusPill look={WORK_ORDER_LOOK[w.status]}>{WORK_ORDER_LABEL[w.status]}</StatusPill></td>
                <td title={formatDateTime(w.reportedAtUtc)}>{formatAge(w.reportedAtUtc)}</td>
                {/* On "my work" every row is the reader's own: a column saying so is noise. */}
                {!mine && <td>{w.assignedToName ?? <span className="muted">Unassigned</span>}</td>}
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {data && data.total > data.pageSize && (
        <div className="pager">
          <button className="btn" disabled={page <= 1} onClick={() => setParam('page', String(page - 1))}>
            Previous
          </button>
          <span className="muted">Page {page} of {totalPages}</span>
          <button className="btn" disabled={page >= totalPages} onClick={() => setParam('page', String(page + 1))}>
            Next
          </button>
        </div>
      )}
    </div>
  );
}

function ReportForm({
  initial,
  onCancel,
  onDone,
  onError,
}: {
  /** A machine already chosen, when the form was opened from its own page. */
  initial: { id: number; label: string } | null;
  onCancel: () => void;
  onDone: (created: { id: number; number: string }) => void | Promise<void>;
  onError: (msg: string | null) => void;
}) {
  const [equipmentId, setEquipmentId] = useState<number | null>(initial?.id ?? null);
  const [fault, setFault] = useState('');
  const [priority, setPriority] = useState(20);
  const [busy, setBusy] = useState(false);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    onError(null);
    try {
      const created = await api.post<{ id: number; number: string }>('/api/work-orders', {
        equipmentId,
        faultDescription: fault,
        priority,
      });
      await onDone(created);
    } catch (err) {
      onError(err instanceof Error ? err.message : 'Could not report the fault.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="card stack" onSubmit={submit}>
      <h2 className="section-h" style={{ margin: 0 }}>Report a fault</h2>

      <div className="filters">
        <EquipmentPicker value={equipmentId} onChange={setEquipmentId} initialLabel={initial?.label} />

        <label className="field">
          <span>Priority</span>
          <select value={priority} onChange={(e) => setPriority(Number(e.target.value))}>
            {Object.entries(PRIORITY_LABEL).map(([v, label]) => (
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

      {/* Reporting a fault is saying the machine is not working: its downtime is counted from this
          moment, not from when an engineer arrives, until it is resolved and back in use. */}
      <p className="muted" style={{ margin: 0 }}>
        The machine is counted as down from the moment you report it, until it is fixed and back in use.
      </p>

      <div className="row">
        <button className="btn btn-primary" type="submit" disabled={busy || equipmentId === null || !fault.trim()}>
          {busy ? 'Reporting…' : 'Report fault'}
        </button>
        <button className="btn" type="button" onClick={onCancel}>Cancel</button>
      </div>
    </form>
  );
}
