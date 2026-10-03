import { useCallback, useEffect, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { api } from '../api/client';
import { PRIORITY_LABEL, PRIORITY_LOOK } from '../statusTones';
import { StatusPill } from '../StatusPill';
import { formatHours } from '../hours';
import { formatDate, formatDateTime, todayAtHospital } from '../time';

type Row = {
  id: number;
  number: string;
  priority: number;
  assetTag: string;
  equipmentTypeName: string;
  locationName: string;
  faultDescription: string;
  resolutionNotes: string | null;
  reportedAtUtc: string;
  reportedByName: string | null;
  resolvedAtUtc: string;
  resolvedByName: string | null;
  downtimeMinutes: number | null;
};

type History = {
  from: string;
  to: string;
  done: number;
  downtimeHours: number;
  items: Row[];
  total: number;
  page: number;
  pageSize: number;
};

const PAGE_SIZE = 25;

/** The hospital's date a number of days ago, as YYYY-MM-DD. */
function daysAgo(days: number): string {
  const [y, m, d] = todayAtHospital().split('-').map(Number);
  return new Date(Date.UTC(y, m - 1, d - days)).toISOString().slice(0, 10);
}

const PERIODS: { key: string; label: string; days: number }[] = [
  { key: '30', label: 'Last 30 days', days: 30 },
  { key: '90', label: 'Last 3 months', days: 90 },
  { key: '365', label: 'Last 12 months', days: 365 },
];

/**
 * Service history: the repairs that have been done on the machines of the person's own departments,
 * newest first, with what was wrong, what was done and who did it. For a person from another
 * department it is where they look back at the service their equipment has had.
 *
 * There is no request page to open from here: the row says it all, and the printed report is one click away.
 */
export function ServiceHistoryPage() {
  const [params, setParams] = useSearchParams();
  const period = params.get('period') ?? '90';
  const custom = period === 'custom';
  const from = params.get('from') ?? '';
  const to = params.get('to') ?? '';
  const q = params.get('q') ?? '';
  const mine = params.get('by') === 'me';
  const page = Math.max(1, Number(params.get('page')) || 1);

  const [search, setSearch] = useState(q);
  const [data, setData] = useState<History | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [downloading, setDownloading] = useState(false);

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

  // The period as dates: a ready-made one counts back from today, a custom one is what was typed.
  const range = useCallback((): { from: string; to: string } | null => {
    if (custom) return from && to ? { from, to } : null;
    const days = PERIODS.find((p) => p.key === period)?.days ?? 90;
    return { from: daysAgo(days), to: todayAtHospital() };
  }, [custom, from, to, period]);

  const query = useCallback(
    (extra: Record<string, string> = {}) => {
      const r = range();
      const p = new URLSearchParams(extra);
      if (r) {
        p.set('from', r.from);
        p.set('to', r.to);
      }
      if (q) p.set('q', q);
      if (mine) p.set('requestedBy', 'me');
      return p;
    },
    [range, q, mine],
  );

  useEffect(() => {
    if (!range()) return; // A custom period waits for both dates.
    let cancelled = false;
    void (async () => {
      setError(null);
      try {
        const result = await api.get<History>(
          `/api/work-orders/history?${query({ page: String(page), pageSize: String(PAGE_SIZE) })}`,
        );
        if (!cancelled) setData(result);
      } catch (e) {
        if (!cancelled) setError(e instanceof Error ? e.message : 'Could not load the service history.');
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [range, query, page]);

  // The search box is applied a moment after the typing stops.
  useEffect(() => {
    if (search.trim() === q) return;
    const t = window.setTimeout(() => setParam('q', search.trim()), 300);
    return () => window.clearTimeout(t);
  }, [search, q, setParam]);

  async function download() {
    setDownloading(true);
    try {
      const r = range();
      await api.download(
        `/api/work-orders/history/report.csv?${query()}`,
        `service-history-${r?.from ?? ''}-${r?.to ?? ''}.csv`,
      );
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not download the history.');
    } finally {
      setDownloading(false);
    }
  }

  const totalPages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;
  const hasFilters = Boolean(q || mine);

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <h1>Service history</h1>
          <p className="muted">
            The repairs done on your departments&apos; machines: what was wrong, what was done and who did it.
          </p>
        </div>
        <button className="btn" onClick={() => void download()} disabled={downloading || !data || data.total === 0}>
          {downloading ? 'Preparing…' : 'Download as a spreadsheet'}
        </button>
      </header>

      {error && <p className="alert alert-error" role="alert">{error}</p>}

      <div className="filters card">
        <input
          type="search"
          className="grow"
          aria-label="Search the service history"
          placeholder="Find a repair: request number, machine, what was wrong or what was done"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />

        <label className="field">
          <span>Period</span>
          <select aria-label="Period" value={period} onChange={(e) => setParam('period', e.target.value === '90' ? '' : e.target.value)}>
            {PERIODS.map((p) => <option key={p.key} value={p.key}>{p.label}</option>)}
            <option value="custom">Choose dates…</option>
          </select>
        </label>

        {custom && (
          <>
            <label className="field">
              <span>From</span>
              <input type="date" aria-label="From" value={from} max={to || todayAtHospital()} onChange={(e) => setParam('from', e.target.value)} />
            </label>
            <label className="field">
              <span>To</span>
              <input type="date" aria-label="To" value={to} min={from} max={todayAtHospital()} onChange={(e) => setParam('to', e.target.value)} />
            </label>
          </>
        )}

        <label className="row" style={{ gap: '0.4rem', alignItems: 'center' }}>
          <input type="checkbox" checked={mine} onChange={(e) => setParam('by', e.target.checked ? 'me' : '')} />
          <span>Only requests I raised</span>
        </label>
      </div>

      {custom && !range() && <p className="muted">Choose both dates to see that period.</p>}

      {data && (
        <p className="muted" role="status">
          {data.done.toLocaleString('en-IN')} {data.done === 1 ? 'repair' : 'repairs'} between {formatDate(data.from)} and{' '}
          {formatDate(data.to)}
          {data.done > 0 && `. Machines were out of service for ${formatHours(data.downtimeHours)} in all.`}
        </p>
      )}

      <div className="card table-wrap">
        <table className="table">
          <thead>
            <tr>
              <th>Repaired</th>
              <th>Machine</th>
              <th>What was wrong</th>
              <th>What was done</th>
              <th>Done by</th>
              <th>Machine down</th>
              <th>Report</th>
            </tr>
          </thead>
          <tbody>
            {!data && !error && <tr><td colSpan={7} className="empty">Loading…</td></tr>}
            {data && data.items.length === 0 && (
              <tr>
                <td colSpan={7} className="empty">
                  {hasFilters ? 'No repair matches that.' : 'No repair was done on your machines in this period.'}
                </td>
              </tr>
            )}
            {data?.items.map((r) => (
              <tr key={r.id}>
                <td title={formatDateTime(r.resolvedAtUtc)}>{formatDate(r.resolvedAtUtc)}</td>
                <td>
                  <span className="mono">{r.assetTag}</span>
                  <div className="muted">{r.equipmentTypeName} · {r.locationName}</div>
                  <div className="muted mono">{r.number}</div>
                </td>
                <td>
                  {r.faultDescription}
                  <div className="muted">
                    <StatusPill look={PRIORITY_LOOK[r.priority]}>{PRIORITY_LABEL[r.priority]}</StatusPill>{' '}
                    Reported {formatDate(r.reportedAtUtc)}{r.reportedByName && ` by ${r.reportedByName}`}
                  </div>
                </td>
                <td>{r.resolutionNotes ?? <span className="muted">—</span>}</td>
                <td>{r.resolvedByName ?? <span className="muted">—</span>}</td>
                <td>{r.downtimeMinutes !== null ? formatHours(r.downtimeMinutes / 60) : <span className="muted">—</span>}</td>
                <td>
                  <a href={`/work-orders/${r.id}/report`} target="_blank" rel="noopener">View report</a>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {data && data.total > data.pageSize && (
        <div className="pager">
          <button className="btn" disabled={page <= 1} onClick={() => setParam('page', String(page - 1))}>Previous</button>
          <span className="muted">Page {page} of {totalPages}</span>
          <button className="btn" disabled={page >= totalPages} onClick={() => setParam('page', String(page + 1))}>Next</button>
        </div>
      )}
    </div>
  );
}
