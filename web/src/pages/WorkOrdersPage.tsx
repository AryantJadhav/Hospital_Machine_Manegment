import { useCallback, useEffect, useState } from 'react';
import type { FormEvent, MouseEvent } from 'react';
import { Link, useLocation, useNavigate, useSearchParams } from 'react-router-dom';
import { api } from '../api/client';
import { PERMISSIONS } from '../auth/context';
import { useAuth } from '../auth/useAuth';
import { BREAKDOWN_TYPES, breakdownLabel } from '../breakdownTypes';
import { EquipmentPicker } from '../EquipmentPicker';
import { HandoffNotice } from '../HandoffNotice';
import { useHandoff } from '../handoff';
import { useToast } from '../toast';
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
  const breakdownType = params.get('breakdownType') ?? '';
  const q = params.get('q') ?? '';
  // Only the faults that have a machine out of service: what the dashboard's "Machines down" counts.
  const down = params.get('down') === '1';
  const page = Math.max(1, Number(params.get('page')) || 1);
  // "me" is worked out by the server from who is signed in.
  const mine = params.get('assignee') === 'me';

  // A person from another department starts on the requests they raised, which is what they come
  // here for; "all in my departments" is one choice away. Everyone else starts on everyone's.
  const { may } = useAuth();
  const toast = useToast();
  const fromAnotherDepartment = !may(PERMISSIONS.registerView);
  const by = params.get('by');
  const requestedByMe = fromAnotherDepartment ? by !== 'all' : by === 'me';

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
      if (breakdownType) query.set('breakdownType', breakdownType);
      if (q) query.set('q', q);
      if (down) query.set('down', 'true');
      if (mine) query.set('assignee', 'me');
      if (requestedByMe) query.set('requestedBy', 'me');
      setData(await api.get<Paged<WorkOrderRow>>(`/api/work-orders?${query}`));
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not load service requests.');
    } finally {
      setLoading(false);
    }
  }, [page, status, priority, breakdownType, q, down, mine, requestedByMe]);

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

  // A person from another department reads the row and opens nothing: the page of a request is the
  // biomedical team's to work. What they are given is the printed report, from the row itself.
  const opens = !fromAnotherDepartment;

  const totalPages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;
  const hasFilters = Boolean(status || priority || breakdownType || q || down || (requestedByMe && !fromAnotherDepartment));
  const columns = (mine ? 8 : 9) + (opens ? 0 : 1);

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <h1>{mine ? 'My service requests' : 'Request Service'}</h1>
          <p className="muted">
            {mine
              ? 'Faults assigned to you that are still to be done.'
              : 'Breakdowns and unscheduled repairs.'}
            {data && ` ${data.total.toLocaleString('en-IN')} ${data.total === 1 ? 'service request' : 'service requests'}${hasFilters ? ' match.' : '.'}`}
          </p>
        </div>
        <button className="btn btn-primary" onClick={() => setReporting(true)}>Report a fault</button>
      </header>

      <HandoffNotice handoff={handoff} />

      {error && <p className="alert alert-error" role="alert">{error}</p>}

      {down && (
        <p className="alert alert-info" role="status">
          Showing only the faults that have a machine out of service.{' '}
          <button className="btn btn-quiet" onClick={() => setParam('down', '')}>Show all service requests</button>
        </p>
      )}

      {reporting && (
        <ReportForm
          initial={reportFor}
          onCancel={() => (from ? navigate(from) : closeReport())}
          onDone={async (created) => {
            // A person from another department has nothing to do on the request's own page: it is
            // the biomedical team's to work. They are told it went in, in a message at the side, and
            // left where they were: on their list, with the new request on it, or back at the machine.
            if (fromAnotherDepartment) {
              toast.show(`Request submitted successfully. Your request number is ${created.number}.`);
              if (from) {
                navigate(from, { replace: true });
              } else {
                closeReport();
                await load();
              }
              return;
            }

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
          aria-label="Search service requests"
          placeholder="Find a service request: number, asset tag or what was wrong"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />
        {fromAnotherDepartment ? (
          <select
            aria-label="Whose service requests"
            value={requestedByMe ? 'me' : 'all'}
            onChange={(e) => setParam('by', e.target.value === 'all' ? 'all' : '')}
          >
            <option value="me">Requested by me</option>
            <option value="all">All in my departments</option>
          </select>
        ) : (
          <select
            aria-label="Whose service requests"
            value={mine ? 'me' : requestedByMe ? 'requested' : ''}
            onChange={(e) => {
              const v = e.target.value;
              const next = new URLSearchParams(params);
              next.delete('page');
              next.delete('assignee');
              next.delete('by');
              if (v === 'me') next.set('assignee', 'me');
              if (v === 'requested') next.set('by', 'me');
              setParams(next, { replace: true });
            }}
          >
            <option value="">Everyone&apos;s</option>
            <option value="me">Assigned to me</option>
            <option value="requested">Requested by me</option>
          </select>
        )}
        <select aria-label="Filter by status" value={status} onChange={(e) => setParam('status', e.target.value)}>
          <option value="">
            {mine ? 'Still to do' : fromAnotherDepartment ? 'Open, and done this week' : 'Open only'}
          </option>
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
        <select aria-label="Filter by breakdown type" value={breakdownType} onChange={(e) => setParam('breakdownType', e.target.value)}>
          <option value="">Any breakdown type</option>
          {BREAKDOWN_TYPES.map((t) => (
            <option key={t.value} value={t.value}>{t.label}</option>
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
              <th>Breakdown</th>
              <th>Status</th>
              <th>Reported</th>
              <th>Requested by</th>
              {!mine && <th>{fromAnotherDepartment ? 'Looked after by' : 'Assigned to'}</th>}
              {!opens && <th>Report</th>}
            </tr>
          </thead>
          <tbody>
            {loading && !data && <tr><td colSpan={columns} className="empty">Loading…</td></tr>}

            {data && data.items.length === 0 && (
              <tr>
                <td colSpan={columns} className="empty">
                  {mine && !hasFilters
                    ? 'Nothing is assigned to you right now.'
                    : fromAnotherDepartment && requestedByMe && !hasFilters
                      ? 'You have not requested any service yet. Open one of your machines and press Report a fault.'
                      : 'No service requests match.'}
                </td>
              </tr>
            )}

            {data?.items.map((w) => (
              <tr
                key={w.id}
                className={opens ? 'row-clickable' : undefined}
                onClick={opens ? (e) => rowClick(e, w.id) : undefined}
              >
                <td className="mono">
                  {opens
                    ? <Link to={`/work-orders/${w.id}`} state={{ from: here }}>{w.number}</Link>
                    : w.number}
                </td>
                <td><StatusPill look={PRIORITY_LOOK[w.priority]}>{PRIORITY_LABEL[w.priority]}</StatusPill></td>
                <td className="mono">
                  {opens
                    ? <Link to={`/equipment/${w.equipmentId}`} state={{ from: here }}>{w.assetTag}</Link>
                    : w.assetTag}
                </td>
                <td className="truncate">{w.faultDescription}</td>
                <td>{breakdownLabel(w.breakdownType) ?? <span className="muted">—</span>}</td>
                <td>
                  <StatusPill look={WORK_ORDER_LOOK[w.status]}>{WORK_ORDER_LABEL[w.status]}</StatusPill>
                  {/* For the department that asked: when the repair was done, so the answer is easy to find. */}
                  {!opens && w.resolvedAtUtc && (
                    <div className="muted" style={{ fontSize: '0.8rem' }} title={formatDateTime(w.resolvedAtUtc)}>
                      Repair done {formatAge(w.resolvedAtUtc)}
                    </div>
                  )}
                </td>
                <td title={formatDateTime(w.reportedAtUtc)}>{formatAge(w.reportedAtUtc)}</td>
                <td>{w.reportedByName ?? <span className="muted">—</span>}</td>
                {/* On "my work" every row is the reader's own: a column saying so is noise. */}
                {!mine && <td>{w.assignedToName ?? <span className="muted">Unassigned</span>}</td>}
                {!opens && (
                  <td>
                    <a href={`/work-orders/${w.id}/report`} target="_blank" rel="noopener">View report</a>
                  </td>
                )}
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
  const [breakdown, setBreakdown] = useState('');
  const [busy, setBusy] = useState(false);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    onError(null);
    try {
      // No priority is asked for here: the server gives a new fault the usual one.
      const created = await api.post<{ id: number; number: string }>('/api/work-orders', {
        equipmentId,
        faultDescription: fault,
        // Only if the reporter knows; the engineer can say once they have looked.
        breakdownType: breakdown ? Number(breakdown) : null,
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

      <label className="field">
        <span>Breakdown type (optional)</span>
        <select value={breakdown} onChange={(e) => setBreakdown(e.target.value)}>
          <option value="">Not sure yet</option>
          {BREAKDOWN_TYPES.map((t) => <option key={t.value} value={t.value}>{t.label}</option>)}
        </select>
        <span className="muted">If you are not sure, leave it. The engineer will say once they have looked at the machine.</span>
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
