import { useCallback, useEffect, useState } from 'react';
import { useSearchParams } from 'react-router-dom';
import { api } from '../api/client';
import { StatusPill } from '../StatusPill';
import type { Tone } from '../statusTones';
import { formatDate, formatDateTime, todayAtHospital } from '../time';

type Change = { field: string; from: string | null; to: string | null };

type Entry = {
  id: number;
  at: string;
  by: { id: number; name: string; userName: string } | null;
  /** False for what the system did itself, and for everything written before the log recorded who. */
  byRecorded: boolean;
  table: string;
  tableLabel: string;
  recordId: string;
  record: string | null;
  action: 'Created' | 'Changed' | 'Removed';
  changes: Change[];
};

type Page = { from: string; to: string; items: Entry[]; total: number; page: number; pageSize: number };
type TableOption = { table: string; label: string };
type Person = { id: number; fullName: string };

const PAGE_SIZE = 50;

const PERIODS = [
  { key: '7', label: 'Last 7 days', days: 7 },
  { key: '30', label: 'Last 30 days', days: 30 },
  { key: '90', label: 'Last 3 months', days: 90 },
];

const ACTION_TONE: Record<Entry['action'], Tone> = {
  Created: 'success',
  Changed: 'info',
  Removed: 'danger',
};

/** The hospital's date a number of days ago, as YYYY-MM-DD. */
function daysAgo(days: number): string {
  const [y, m, d] = todayAtHospital().split('-').map(Number);
  return new Date(Date.UTC(y, m - 1, d - days)).toISOString().slice(0, 10);
}

/** How much of a long list of changes is shown before the rest is folded away. */
const SHOWN = 4;

/**
 * The audit log: who changed what, and when.
 *
 * It is written by the database itself, so it cannot be edited or deleted from here or anywhere. Rows from
 * before the log began to record who made each change, and work the system did itself, say "Not recorded".
 */
export function AuditPage() {
  const [params, setParams] = useSearchParams();
  const period = params.get('period') ?? '30';
  const custom = period === 'custom';
  const from = params.get('from') ?? '';
  const to = params.get('to') ?? '';
  const table = params.get('table') ?? '';
  const action = params.get('action') ?? '';
  const by = params.get('by') ?? '';
  const q = params.get('q') ?? '';
  const page = Math.max(1, Number(params.get('page')) || 1);

  const [search, setSearch] = useState(q);
  const [data, setData] = useState<Page | null>(null);
  const [tables, setTables] = useState<TableOption[]>([]);
  const [people, setPeople] = useState<Person[]>([]);
  const [error, setError] = useState<string | null>(null);

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

  // The tables to choose from and the people to choose between: both for the filters only.
  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const [t, p] = await Promise.all([
          api.get<TableOption[]>('/api/admin/audit/tables'),
          api.get<Person[]>('/api/people'),
        ]);
        if (cancelled) return;
        setTables(t);
        setPeople(p);
      } catch {
        // The log is still readable without the filters' choices.
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  useEffect(() => {
    if (custom && !(from && to)) return; // A chosen period waits for both dates.
    let cancelled = false;
    void (async () => {
      setError(null);
      const days = PERIODS.find((p) => p.key === period)?.days ?? 30;
      const query = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE) });
      query.set('from', custom ? from : daysAgo(days));
      query.set('to', custom ? to : todayAtHospital());
      if (table) query.set('table', table);
      if (action) query.set('action', action);
      if (by) query.set('by', by);
      if (q) query.set('q', q);
      try {
        const result = await api.get<Page>(`/api/admin/audit?${query}`);
        if (!cancelled) setData(result);
      } catch (e) {
        if (!cancelled) setError(e instanceof Error ? e.message : 'Could not load the audit log.');
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [period, custom, from, to, table, action, by, q, page]);

  // The search box is applied a moment after the typing stops.
  useEffect(() => {
    if (search.trim() === q) return;
    const t = window.setTimeout(() => setParam('q', search.trim()), 300);
    return () => window.clearTimeout(t);
  }, [search, q, setParam]);

  const totalPages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;
  const filtered = Boolean(table || action || by || q);

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <h1>Audit log</h1>
          <p className="muted">
            Who changed what, and when. The database writes this itself, so it cannot be edited or deleted.
            Passwords are never shown.
          </p>
        </div>
      </header>

      {error && <p className="alert alert-error" role="alert">{error}</p>}

      <div className="filters card">
        <input
          type="search"
          className="grow"
          aria-label="Search the audit log"
          placeholder="Find a change: a machine number, a name, a request number or any word that was written"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />

        <label className="field">
          <span>What</span>
          <select aria-label="What was changed" value={table} onChange={(e) => setParam('table', e.target.value)}>
            <option value="">Anything</option>
            {tables.map((t) => <option key={t.table} value={t.table}>{t.label}</option>)}
          </select>
        </label>

        <label className="field">
          <span>Action</span>
          <select aria-label="What was done" value={action} onChange={(e) => setParam('action', e.target.value)}>
            <option value="">Any</option>
            <option value="created">Created</option>
            <option value="changed">Changed</option>
            <option value="removed">Removed</option>
          </select>
        </label>

        <label className="field">
          <span>By</span>
          <select aria-label="Who made the change" value={by} onChange={(e) => setParam('by', e.target.value)}>
            <option value="">Anyone</option>
            <option value="none">Not recorded</option>
            {people.map((p) => <option key={p.id} value={String(p.id)}>{p.fullName}</option>)}
          </select>
        </label>

        <label className="field">
          <span>Period</span>
          <select aria-label="Period" value={period} onChange={(e) => setParam('period', e.target.value === '30' ? '' : e.target.value)}>
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
      </div>

      {custom && !(from && to) && <p className="muted">Choose both dates to see that period.</p>}

      {data && (
        <p className="muted" role="status">
          {data.total.toLocaleString('en-IN')} {data.total === 1 ? 'entry' : 'entries'} between {formatDate(data.from)} and{' '}
          {formatDate(data.to)}{filtered ? ' that match.' : '.'}
        </p>
      )}

      <div className="card table-wrap">
        <table className="table">
          <thead>
            <tr>
              <th>When</th>
              <th>By</th>
              <th>What</th>
              <th>Action</th>
              <th>Details</th>
            </tr>
          </thead>
          <tbody>
            {!data && !error && <tr><td colSpan={5} className="empty">Loading…</td></tr>}
            {data && data.items.length === 0 && (
              <tr><td colSpan={5} className="empty">{filtered ? 'Nothing matches that.' : 'Nothing was changed in this period.'}</td></tr>
            )}
            {data?.items.map((e) => (
              <tr key={e.id}>
                <td style={{ whiteSpace: 'nowrap' }}>{formatDateTime(e.at)}</td>
                <td>
                  {e.by ? (
                    <>
                      {e.by.name}
                      <div className="muted mono">{e.by.userName}</div>
                    </>
                  ) : (
                    <span
                      className="muted"
                      title="Done by the system itself, or made before the log recorded who made each change."
                    >
                      Not recorded
                    </span>
                  )}
                </td>
                <td>
                  {e.tableLabel}
                  <div className="muted">{e.record ? `${e.record} ` : ''}<span className="mono">#{e.recordId}</span></div>
                </td>
                <td><StatusPill tone={ACTION_TONE[e.action]}>{e.action}</StatusPill></td>
                <td><Changes entry={e} /></td>
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

/** What changed: a few lines shown, the rest folded away. */
function Changes({ entry }: { entry: Entry }) {
  if (entry.changes.length === 0) return <span className="muted">—</span>;

  const line = (c: Change) => {
    if (entry.action === 'Created') return <><strong>{c.field}:</strong> {c.to ?? '—'}</>;
    if (entry.action === 'Removed') return <><strong>{c.field}:</strong> {c.from ?? '—'}</>;
    return <><strong>{c.field}:</strong> {c.from ?? '—'} → {c.to ?? '—'}</>;
  };

  const first = entry.changes.slice(0, SHOWN);
  const rest = entry.changes.slice(SHOWN);

  return (
    <div className="stack" style={{ gap: '0.15rem' }}>
      {first.map((c) => <div key={c.field}>{line(c)}</div>)}
      {rest.length > 0 && (
        <details>
          <summary>and {rest.length} more</summary>
          {rest.map((c) => <div key={c.field}>{line(c)}</div>)}
        </details>
      )}
    </div>
  );
}
