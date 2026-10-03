import { useCallback, useEffect, useState } from 'react';
import { Link, useLocation, useNavigate, useSearchParams } from 'react-router-dom';
import { api } from '../api/client';
import { PERMISSIONS } from '../auth/context';
import { useAuth } from '../auth/useAuth';
import { describeDays, GATE_PASS_LABEL, GATE_PASS_TONE } from '../gatePassTypes';
import type { GatePassCounts, GatePassRow } from '../gatePassTypes';
import { StatusPill } from '../StatusPill';
import { formatDate } from '../time';

type Page = { items: GatePassRow[]; total: number; page: number; pageSize: number; counts: GatePassCounts };

const PAGE_SIZE = 25;

/** The tabs, in the order a person asks the questions: what is out, what is late, what has come back. */
const TABS: { key: string; label: string; count: (c: GatePassCounts) => number | null }[] = [
  { key: 'out', label: 'Out now', count: (c) => c.out },
  { key: 'overdue', label: 'Overdue', count: (c) => c.overdue },
  { key: 'returned', label: 'Returned', count: (c) => c.returned },
  { key: 'cancelled', label: 'Cancelled', count: (c) => c.cancelled },
  { key: 'all', label: 'All', count: () => null },
];

/**
 * Gate passes: the machines that left the hospital for a vendor to repair, who has them, and when they
 * are due back. Opens on what is out now, the longest overdue first.
 */
export function GatePassesPage() {
  const { may } = useAuth();
  const canEdit = may(PERMISSIONS.gatePassEdit);
  const navigate = useNavigate();
  const location = useLocation();

  const [params, setParams] = useSearchParams();
  const tab = TABS.some((t) => t.key === params.get('status')) ? params.get('status')! : 'out';
  const q = params.get('q') ?? '';
  const page = Math.max(1, Number(params.get('page')) || 1);

  const [search, setSearch] = useState(q);
  const [data, setData] = useState<Page | null>(null);
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

  // The search box is applied a moment after the typing stops, so each keystroke is not a request.
  useEffect(() => {
    if (search.trim() === q) return;
    const t = window.setTimeout(() => setParam('q', search.trim()), 300);
    return () => window.clearTimeout(t);
  }, [search, q, setParam]);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      setError(null);
      const query = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE) });
      if (tab !== 'all') query.set('status', tab);
      if (q) query.set('q', q);
      try {
        const result = await api.get<Page>(`/api/gate-passes?${query}`);
        if (!cancelled) setData(result);
      } catch (e) {
        if (!cancelled) setError(e instanceof Error ? e.message : 'Could not load the gate passes.');
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [tab, q, page]);

  const totalPages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;
  // Where Back from a pass returns to: this list as it is now, with its tab and search.
  const here = `${location.pathname}${location.search}`;

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <h1>Gate passes</h1>
          <p className="muted">
            Machines sent out of the hospital to a vendor for repair: where they are and when they are due back.
          </p>
        </div>
        {canEdit && (
          <button className="btn btn-primary" onClick={() => navigate('/gate-passes/new', { state: { from: here } })}>
            New gate pass
          </button>
        )}
      </header>

      {error && <p className="alert alert-error" role="alert">{error}</p>}

      {data && data.counts.overdue > 0 && tab !== 'overdue' && (
        <p className="alert alert-warn" role="status">
          {data.counts.overdue === 1 ? '1 machine is' : `${data.counts.overdue} gate passes are`} overdue to come back.{' '}
          <button className="btn btn-quiet" onClick={() => setParam('status', 'overdue')}>Show {data.counts.overdue === 1 ? 'it' : 'them'}</button>
        </p>
      )}

      <div className="row" role="tablist" aria-label="Gate passes">
        {TABS.map((t) => {
          const n = data ? t.count(data.counts) : null;
          return (
            <button
              key={t.key}
              role="tab"
              aria-selected={tab === t.key}
              className={tab === t.key ? 'btn btn-primary' : 'btn'}
              onClick={() => setParam('status', t.key === 'out' ? '' : t.key)}
            >
              {t.label}{n !== null && ` (${n.toLocaleString('en-IN')})`}
            </button>
          );
        })}
      </div>

      <div className="filters card">
        <input
          type="search"
          className="grow"
          aria-label="Search gate passes"
          placeholder="Find a pass: its number, the company, a machine number, or what was sent"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />
      </div>

      <div className="card table-wrap">
        <table className="table">
          <thead>
            <tr>
              <th>Gate pass</th>
              <th>Company</th>
              <th>Items</th>
              <th>Expected back</th>
              <th>Status</th>
            </tr>
          </thead>
          <tbody>
            {!data && !error && <tr><td colSpan={5} className="empty">Loading…</td></tr>}
            {data && data.items.length === 0 && (
              <tr>
                <td colSpan={5} className="empty">
                  {q
                    ? 'No gate pass matches that.'
                    : tab === 'out'
                      ? 'Nothing is out of the hospital right now.'
                      : tab === 'overdue'
                        ? 'Nothing is overdue.'
                        : 'No gate passes here yet.'}
                </td>
              </tr>
            )}
            {data?.items.map((p) => (
              <tr key={p.id}>
                <td>
                  <Link to={`/gate-passes/${p.id}`} state={{ from: here }} className="mono">{p.reference}</Link>
                  <div className="muted">{formatDate(p.passDate)}</div>
                </td>
                <td>
                  {p.vendorName}
                  {p.contactPerson && <div className="muted">{p.contactPerson}</div>}
                  {p.workOrderNumber && (
                    <div className="muted">
                      Request <Link to={`/work-orders/${p.workOrderId}`} state={{ from: here }} className="mono">{p.workOrderNumber}</Link>
                    </div>
                  )}
                </td>
                <td style={{ maxWidth: '22rem' }}>
                  {p.someItems[0]}
                  <div className="muted">
                    {p.itemCount > 1 ? `and ${p.itemCount - 1} more · ` : ''}quantity {p.totalQuantity.toLocaleString('en-IN')}
                  </div>
                </td>
                <td>
                  {p.status === 'Returned' && p.returnedOn
                    ? <>Back {formatDate(p.returnedOn)}</>
                    : p.expectedReturnDate
                      ? formatDate(p.expectedReturnDate)
                      : <span className="muted">—</span>}
                </td>
                <td>
                  <StatusPill tone={GATE_PASS_TONE[p.status]}>{GATE_PASS_LABEL[p.status]}</StatusPill>
                  {p.isOverdue && <div><StatusPill tone="danger">Overdue</StatusPill></div>}
                  {p.status === 'Out' && p.daysOut !== null && (
                    <div className="muted">Away {describeDays(p.daysOut)}</div>
                  )}
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
