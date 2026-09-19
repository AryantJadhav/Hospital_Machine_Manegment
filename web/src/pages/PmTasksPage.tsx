import { useCallback, useEffect, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { api, ApiError } from '../api/client';
import { useAuth } from '../auth/useAuth';
import { ROLES } from '../auth/context';
import { PmChecklistForm } from './PmChecklistForm';

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
};

type Paged<T> = { items: T[]; total: number; page: number; pageSize: number };
type Lookup = { id: number; code: string; name: string; depth: number };

const STATUS: Record<number, string> = {
  10: 'Scheduled',
  20: 'Due',
  30: 'Overdue',
  40: 'Completed',
  50: 'Skipped',
};

const PAGE_SIZE = 25;

export function PmTasksPage() {
  const [params, setParams] = useSearchParams();
  const status = params.get('status') ?? '';
  const locationId = params.get('locationId') ?? '';

  const [data, setData] = useState<Paged<PmTask> | null>(null);
  const [locations, setLocations] = useState<Lookup[]>([]);
  const [page, setPage] = useState(1);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [working, setWorking] = useState<PmTask | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  // The PM just recorded, kept so the confirmation can offer its certificate.
  // A hospital's next question after "recorded" is "where is the paper".
  const [recorded, setRecorded] = useState<PmTask | null>(null);
  // Whether anything has ever been completed or skipped. Only asked when the
  // open list is empty, to tell "all caught up" from "nothing scheduled yet".
  const [hasHistory, setHasHistory] = useState(false);

  const { can } = useAuth();
  // Deciding a PM will not happen is a supervisory call, not a technician's,
  // because a skip is a permanent gap in the record. The server enforces the
  // same rule; this only keeps the button out of the way.
  const canSkip = can(ROLES.admin);

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
      const result = await api.get<Paged<PmTask>>(`/api/pm/tasks?${q}`);
      setData(result);

      if (result.total === 0 && !status && !locationId) {
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
  }, [status, locationId, page]);

  useEffect(() => {
    void load();
  }, [load]);

  function setFilter(key: string, value: string) {
    const next = new URLSearchParams(params);
    if (value) next.set(key, value);
    else next.delete(key);
    setParams(next, { replace: true });
    setPage(1);
    setNotice(null);
    setRecorded(null);
  }

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

  if (working) {
    return (
      <PmChecklistForm
        taskId={working.id}
        canSkip={canSkip}
        onClose={() => setWorking(null)}
        onDone={async (message, completed) => {
          const task = working;
          setWorking(null);
          setNotice(message);
          setRecorded(completed ? task : null);
          await load();
        }}
      />
    );
  }

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <h1>Preventive maintenance</h1>
          <p className="muted">
            {data ? `${data.total.toLocaleString()} task${data.total === 1 ? '' : 's'}` : ' '}
          </p>
        </div>
      </header>

      {error && <p className="alert alert-error" role="alert">{error}</p>}
      {notice && (
        <p className="alert alert-ok" role="status">
          {notice}
          {recorded && (
            <>
              {' '}
              <button className="btn btn-quiet" onClick={() => void certificate(recorded)}>
                Download the certificate
              </button>
            </>
          )}
        </p>
      )}

      <div className="filters card">
        <select value={status} onChange={(e) => setFilter('status', e.target.value)}>
          <option value="">Open (scheduled, due, overdue)</option>
          {Object.entries(STATUS).map(([v, label]) => (
            <option key={v} value={v}>{label}</option>
          ))}
        </select>

        <select value={locationId} onChange={(e) => setFilter('locationId', e.target.value)}>
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
                  {status || locationId
                    ? 'No PMs match these filters.'
                    : hasHistory
                      ? 'All caught up. No PM is open right now. Finished ones are under Completed.'
                      : 'Nothing here yet. Schedule a checklist from the Checklists page to generate PM tasks.'}
                </td>
              </tr>
            )}

            {!loading && data?.items.map((t) => (
              <tr key={t.id}>
                <td className="mono">
                  {t.dueDate}
                  {t.daysLate > 0 && (
                    <span className="late"> {t.daysLate}d late</span>
                  )}
                </td>
                <td className="mono">{t.assetTag}</td>
                <td>{t.equipmentTypeName}</td>
                <td>{t.locationName}</td>
                <td>{t.checklistName}</td>
                <td><span className={`pill pm-${t.status}`}>{STATUS[t.status] ?? '—'}</span></td>
                <td style={{ whiteSpace: 'nowrap' }}>
                  {/* Scheduled, Due and Overdue are open work. Completed and
                      Skipped are finished records — a completion is immutable
                      by database trigger, so offering to reopen one would be
                      offering something the database will refuse. */}
                  {(t.status === 10 || t.status === 20 || t.status === 30) && (
                    <button
                      className="btn btn-quiet"
                      onClick={() => {
                        setRecorded(null);
                        setWorking(t);
                      }}
                    >
                      Do PM
                    </button>
                  )}
                  {t.status === 40 && (
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
