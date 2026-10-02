import { useCallback, useEffect, useState } from 'react';
import { Link, useLocation, useSearchParams } from 'react-router-dom';
import { api, ApiError } from '../api/client';
import { useHandoff } from '../handoff';
import { HandoffNotice } from '../HandoffNotice';
import { formatDate } from '../time';
import { StatusPill } from '../StatusPill';
import { PM_LABEL, PM_LOOK } from '../statusTones';

type PmTask = {
  id: number;
  pmScheduleId: number;
  equipmentId: number;
  assetTag: string;
  equipmentTypeName: string;
  locationName: string;
  checklistName: string;
  dueDate: string;
  status: number;
  daysLate: number;
  // 10 our own team, 20 the maintenance contract vendor.
  performedBy: number;
  // False for a PM that is only scheduled and recorded as done.
  hasChecklist: boolean;
};

type Paged<T> = { items: T[]; total: number; page: number; pageSize: number };
type Lookup = { id: number; code: string; name: string; depth: number };

const PAGE_SIZE = 25;

export function PmTasksPage() {
  const [params, setParams] = useSearchParams();
  const status = params.get('status') ?? '';
  const locationId = params.get('locationId') ?? '';
  const search = params.get('q') ?? '';
  const location = useLocation();

  const [data, setData] = useState<Paged<PmTask> | null>(null);
  const [locations, setLocations] = useState<Lookup[]>([]);
  const [page, setPage] = useState(1);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  // What the form we were sent to just did, and its certificate. A hospital's
  // next question after "recorded" is "where is the paper".
  const [handoff, setHandoff] = useHandoff();
  // What is typed in the search box, kept apart from the URL so typing is not
  // one history entry per key and the list is asked once the typing pauses.
  const [typed, setTyped] = useState(search);
  // Whether anything has ever been completed or skipped. Only asked when the
  // open list is empty, to tell "all caught up" from "nothing scheduled yet".
  const [hasHistory, setHasHistory] = useState(false);

  useEffect(() => {
    (async () => {
      try {
        setLocations(await api.get<Lookup[]>('/api/lookups/locations'));
      } catch {
        /* filter degrades to none */
      }
    })();
  }, []);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const q = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE) });
      if (status) q.set('status', status);
      if (locationId) q.set('locationId', locationId);
      if (search) q.set('q', search);
      const result = await api.get<Paged<PmTask>>(`/api/pm/tasks?${q}`);
      setData(result);

      if (result.total === 0 && !status && !locationId && !search) {
        for (const finished of [40, 50]) {
          const past = await api.get<Paged<PmTask>>(`/api/pm/tasks?status=${finished}&pageSize=1`);
          if (past.total > 0) {
            setHasHistory(true);
            return;
          }
        }
        setHasHistory(false);
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not load the PM list.');
    } finally {
      setLoading(false);
    }
  }, [status, locationId, search, page]);

  useEffect(() => {
    void load();
  }, [load]);

  function setFilter(key: string, value: string) {
    const next = new URLSearchParams(params);
    if (value) next.set(key, value);
    else next.delete(key);
    setParams(next, { replace: true });
    setPage(1);
    setHandoff(null);
  }

  // The list is asked for what was typed once the typing pauses.
  useEffect(() => {
    if (typed.trim() === search) return;
    const handle = window.setTimeout(() => setFilter('q', typed.trim()), 250);
    return () => window.clearTimeout(handle);
    // setFilter only reads the current URL parameters.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [typed]);

  async function certificate(task: PmTask) {
    try {
      await api.download(
        `/api/reports/pm/${task.id}/certificate.pdf`,
        `PM-${task.assetTag}-${task.dueDate}.pdf`,
      );
    } catch (e) {
      setError(
        e instanceof ApiError && e.status === 404
          ? 'That PM has not been completed yet, so there is no certificate.'
          : 'Could not generate the certificate.',
      );
    }
  }

  const totalPages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <h1>Preventive maintenance</h1>
          <p className="muted">
            {data ? `${data.total.toLocaleString('en-IN')} task${data.total === 1 ? '' : 's'}` : ' '}
          </p>
        </div>
      </header>

      {error && <p className="alert alert-error" role="alert">{error}</p>}
      <HandoffNotice handoff={handoff} />

      <div className="filters card">
        <input
          className="grow"
          type="search"
          placeholder="Find a machine: asset tag, serial, make, model, type or place"
          aria-label="Search the PM list"
          value={typed}
          onChange={(e) => setTyped(e.target.value)}
        />
        <select aria-label="Filter by status" value={status} onChange={(e) => setFilter('status', e.target.value)}>
          <option value="">Open (scheduled, due, overdue)</option>
          {Object.entries(PM_LABEL).map(([v, label]) => (
            <option key={v} value={v}>{label}</option>
          ))}
        </select>

        <select aria-label="Filter by location" value={locationId} onChange={(e) => setFilter('locationId', e.target.value)}>
          <option value="">All locations</option>
          {locations.map((l) => (
            <option key={l.id} value={l.id}>
              {' '.repeat(l.depth * 3)}
              {l.name}
            </option>
          ))}
        </select>
      </div>

      <div className="card table-wrap">
        <table className="table">
          <thead>
            <tr>
              <th>Due</th>
              <th>Asset tag</th>
              <th>Equipment</th>
              <th>Location</th>
              <th>Checklist</th>
              <th>Status</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {loading && <tr><td colSpan={7} className="empty">Loading…</td></tr>}

            {!loading && data?.items.length === 0 && (
              <tr>
                <td colSpan={7} className="empty">
                  {status || locationId || search
                    ? 'No PMs match that.'
                    : hasHistory
                      ? 'All caught up. No PM is open right now. Finished ones are under Completed.'
                      : 'Nothing here yet. Schedule a checklist from the Checklists page to generate PM tasks.'}
                </td>
              </tr>
            )}

            {!loading && data?.items.map((t) => (
              <tr key={t.id}>
                <td className="mono">
                  {formatDate(t.dueDate)}
                  {t.daysLate > 0 && (
                    <span className="late"> {t.daysLate}d late</span>
                  )}
                </td>
                <td className="mono">{t.assetTag}</td>
                <td>{t.equipmentTypeName}</td>
                <td>{t.locationName}</td>
                <td>
                  {t.checklistName}
                  {t.performedBy === 20 && <StatusPill tone="info" className="hist-flag">Vendor</StatusPill>}
                </td>
                <td><StatusPill look={PM_LOOK[t.status]}>{PM_LABEL[t.status] ?? '—'}</StatusPill></td>
                <td style={{ whiteSpace: 'nowrap' }}>
                  {/* Scheduled, Due and Overdue are open work. Completed and
                      Skipped are finished records — a completion is immutable
                      by database trigger, so offering to reopen one would be
                      offering something the database will refuse. */}
                  {(t.status === 10 || t.status === 20 || t.status === 30) && (
                    <Link
                      className="btn btn-quiet"
                      to={`/pm/${t.id}/do`}
                      // Back here afterwards, with the same filters and search.
                      state={{ from: `${location.pathname}${location.search}` }}
                    >
                      {t.performedBy === 20 ? 'Record vendor PM' : t.hasChecklist ? 'Do PM' : 'Mark done'}
                    </Link>
                  )}
                  {t.status === 40 && (t.performedBy === 20 || !t.hasChecklist) && (
                    // Who did it and the report are the record; there is no certificate.
                    <Link
                      className="btn btn-quiet"
                      to={`/pm/${t.id}/do`}
                      state={{ from: `${location.pathname}${location.search}` }}
                    >
                      Report
                    </Link>
                  )}
                  {t.status === 40 && t.performedBy !== 20 && t.hasChecklist && (
                    <button className="btn btn-quiet" onClick={() => void certificate(t)}>
                      Certificate
                    </button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {data && data.total > data.pageSize && (
        <div className="pager">
          <button className="btn" disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>Previous</button>
          <span className="muted">Page {page} of {totalPages}</span>
          <button className="btn" disabled={page >= totalPages} onClick={() => setPage((p) => p + 1)}>Next</button>
        </div>
      )}
    </div>
  );
}
