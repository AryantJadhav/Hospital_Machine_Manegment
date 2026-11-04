import { useCallback, useEffect, useState } from 'react';
import { Link, useLocation, useNavigate, useSearchParams } from 'react-router-dom';
import { api } from '../api/client';
import { PERMISSIONS } from '../auth/context';
import { useAuth } from '../auth/useAuth';
import { LOSS_KINDS, LOSS_STATUS_LABEL, LOSS_STATUS_LOOK } from '../lossTypes';
import type { LossCounts, LossRow, LossSummary } from '../lossTypes';
import { formatRupees } from '../money';
import { StatusPill } from '../StatusPill';
import { formatDate, todayAtHospital } from '../time';
import { useSearchBox } from '../useSearchBox';

type Page = { items: LossRow[]; total: number; page: number; pageSize: number; counts: LossCounts };

const PAGE_SIZE = 25;

// The two desks a loss can wait at have a tab each: a person goes straight to what is waiting for them.
const TABS = [
  { key: 'open', label: 'Still missing' },
  { key: 'engineer', label: 'With an engineer' },
  { key: 'awaiting', label: 'Waiting for the head' },
  { key: 'recovered', label: 'Found' },
  { key: 'written-off', label: 'Written off' },
  { key: 'cancelled', label: 'Cancelled' },
  { key: 'all', label: 'All' },
];

const PERIODS = [
  { key: 'all', label: 'All time', days: null },
  { key: '30', label: 'Last 30 days', days: 30 },
  { key: '90', label: 'Last 3 months', days: 90 },
  { key: '365', label: 'Last 12 months', days: 365 },
];

/** The hospital's date a number of days ago, as YYYY-MM-DD. */
function daysAgo(days: number): string {
  const [y, m, d] = todayAtHospital().split('-').map(Number);
  return new Date(Date.UTC(y, m - 1, d - days)).toISOString().slice(0, 10);
}

/**
 * Loss of equipment: the machines that were lost, stolen or lost while away. A department or an engineer
 * writes one up; the biomedical team looks for the machine and into how it went, and it ends found or
 * written off. Opens on what is still missing, the longest gone first, with the figures and the printed
 * report for the period chosen.
 *
 * A person from another department sees the losses of their own departments' machines, and nothing else.
 */
export function LossesPage() {
  const { may } = useAuth();
  const canReport = may(PERMISSIONS.lossesReport);
  const canOpenMachine = may(PERMISSIONS.registerView);
  const navigate = useNavigate();
  const location = useLocation();

  const [params, setParams] = useSearchParams();
  const tab = TABS.some((t) => t.key === params.get('status')) ? params.get('status')! : 'open';
  const period = params.get('period') ?? 'all';
  const custom = period === 'custom';
  const from = params.get('from') ?? '';
  const to = params.get('to') ?? '';
  const kind = params.get('kind') ?? '';
  const mine = params.get('mine') === 'true';
  const q = params.get('q') ?? '';
  const page = Math.max(1, Number(params.get('page')) || 1);

  const [data, setData] = useState<Page | null>(null);
  const [summary, setSummary] = useState<LossSummary | null>(null);
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
  const [search, setSearch] = useSearchBox(q, (typed) => setParam('q', typed));

  // The period as dates, or nothing for all time. A chosen period waits for both of its dates.
  const days = PERIODS.find((p) => p.key === period)?.days ?? null;
  const periodFrom = custom ? from : days !== null ? daysAgo(days) : '';
  const periodTo = custom ? to : days !== null ? todayAtHospital() : '';
  const waiting = custom && !(from && to);

  useEffect(() => {
    if (waiting) return;
    let cancelled = false;
    (async () => {
      setError(null);
      const query = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE) });
      if (tab !== 'all') query.set('status', tab);
      if (q) query.set('q', q);
      if (kind) query.set('kind', kind);
      if (mine) query.set('mine', 'true');
      if (periodFrom) query.set('from', periodFrom);
      if (periodTo) query.set('to', periodTo);

      const figures = new URLSearchParams();
      if (periodFrom) figures.set('from', periodFrom);
      if (periodTo) figures.set('to', periodTo);

      try {
        const [list, sum] = await Promise.all([
          api.get<Page>(`/api/losses?${query}`),
          api.get<LossSummary>(`/api/losses/summary?${figures}`),
        ]);
        if (cancelled) return;
        setData(list);
        setSummary(sum);
      } catch (e) {
        if (!cancelled) setError(e instanceof Error ? e.message : 'Could not load the losses.');
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [tab, q, kind, mine, periodFrom, periodTo, waiting, page]);

  const totalPages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;
  const filtered = Boolean(q || kind || mine);
  const here = `${location.pathname}${location.search}`;

  // The report is of the period and nothing narrower: it is the summary the department files.
  const reportQuery = new URLSearchParams();
  if (periodFrom) reportQuery.set('from', periodFrom);
  if (periodTo) reportQuery.set('to', periodTo);

  const counts = (key: string): number | null => {
    if (!data) return null;
    const c = data.counts;
    switch (key) {
      case 'open': return c.open;
      case 'engineer': return c.withEngineer;
      case 'awaiting': return c.awaitingDecision;
      case 'recovered': return c.recovered;
      case 'written-off': return c.writtenOff;
      case 'cancelled': return c.cancelled;
      default: return c.open + c.recovered + c.writtenOff + c.cancelled;
    }
  };

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <h1>Loss of equipment</h1>
          <p className="muted">
            Machines that were lost, stolen or went missing while away: what was done to find them, and how it ended.
          </p>
        </div>
        <div className="row">
          {/* A preview in a tab of its own first; printing or downloading is a choice made there. */}
          <a
            className="btn"
            href={`/losses/report${reportQuery.toString() ? `?${reportQuery}` : ''}`}
            target="_blank"
            rel="noopener"
          >
            Print the loss report
          </a>
          {canReport && (
            <button className="btn btn-primary" onClick={() => navigate('/losses/new', { state: { from: here } })}>
              Report a loss
            </button>
          )}
        </div>
      </header>

      {error && <p className="alert alert-error" role="alert">{error}</p>}

      {summary && summary.total > 0 && (
        <section className="card stack" aria-label="The period at a glance">
          <div className="row" style={{ gap: '2rem', flexWrap: 'wrap' }}>
            <Figure label="Machines lost" value={summary.total.toLocaleString('en-IN')} />
            <Figure label="Still missing" value={summary.open.toLocaleString('en-IN')} />
            <Figure label="Found again" value={summary.recovered.toLocaleString('en-IN')} />
            <Figure label="Written off" value={summary.writtenOff.toLocaleString('en-IN')} />
            <Figure label="Worth, not recovered" value={formatRupees(summary.valueLost)} />
          </div>
          <p className="muted" style={{ margin: 0 }}>
            {summary.byKind.map((k) => `${k.label}: ${k.value}`).join(' · ')}
            {summary.withoutValue > 0 && ` · ${summary.withoutValue} with no value recorded`}
          </p>
          {summary.repeatMachines.length > 0 && (
            <div className="alert alert-warn row" style={{ margin: 0, alignItems: 'center', flexWrap: 'wrap', gap: '0.25rem 0.5rem' }} role="status">
              <span>Lost more than once:</span>
              {summary.repeatMachines.slice(0, 4).map((m) => (
                <span key={m.equipmentId}>
                  <button
                    className="btn btn-quiet"
                    onClick={() => {
                      const next = new URLSearchParams(params);
                      next.set('status', 'all');
                      next.set('q', m.assetTag);
                      next.delete('page');
                      setSearch(m.assetTag);
                      setParams(next, { replace: true });
                    }}
                  >
                    {m.assetTag} ({m.losses})
                  </button>
                </span>
              ))}
            </div>
          )}
        </section>
      )}

      <div className="row" role="tablist" aria-label="Losses">
        {TABS.map((t) => {
          const n = counts(t.key);
          return (
            <button
              key={t.key}
              role="tab"
              aria-selected={tab === t.key}
              className={tab === t.key ? 'btn btn-primary' : 'btn'}
              onClick={() => setParam('status', t.key === 'open' ? '' : t.key)}
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
          aria-label="Search losses"
          placeholder="Find a loss: its number, a machine number, a place, a police complaint or what was said"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />

        <label className="field">
          <span>How</span>
          <select aria-label="How it was lost" value={kind} onChange={(e) => setParam('kind', e.target.value)}>
            <option value="">Any</option>
            {LOSS_KINDS.map((k) => <option key={k.value} value={k.value}>{k.label}</option>)}
          </select>
        </label>

        <label className="field">
          <span>Period</span>
          <select
            aria-label="Period"
            value={period}
            onChange={(e) => setParam('period', e.target.value === 'all' ? '' : e.target.value)}
          >
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
          <input type="checkbox" checked={mine} onChange={(e) => setParam('mine', e.target.checked ? 'true' : '')} />
          Only ones I reported
        </label>
      </div>

      {waiting && <p className="muted">Choose both dates to see that period.</p>}

      <div className="card table-wrap">
        <table className="table">
          <thead>
            <tr>
              <th>Loss</th>
              <th>Machine</th>
              <th>What happened</th>
              <th className="num">Worth</th>
              <th>Status</th>
            </tr>
          </thead>
          <tbody>
            {!data && !error && <tr><td colSpan={5} className="empty">Loading…</td></tr>}
            {data && data.items.length === 0 && (
              <tr>
                <td colSpan={5} className="empty">
                  {filtered
                    ? 'No loss matches that.'
                    : tab === 'open'
                      ? 'No machine is missing. Every loss has been closed.'
                      : tab === 'engineer'
                        ? 'Nothing is waiting for an engineer.'
                        : tab === 'awaiting'
                          ? 'Nothing is waiting for the head’s decision.'
                          : 'Nothing here yet.'}
                </td>
              </tr>
            )}
            {data?.items.map((l) => (
              <tr key={l.id}>
                <td>
                  <Link to={`/losses/${l.id}`} state={{ from: here }} className="mono">{l.reference}</Link>
                  <div className="muted">{formatDate(l.noticedOn)}</div>
                </td>
                <td>
                  {/* A person from another department sees the number and nothing opens. */}
                  {canOpenMachine ? (
                    <Link to={`/equipment/${l.equipmentId}`} state={{ from: here }} className="mono">{l.assetTag}</Link>
                  ) : (
                    <span className="mono">{l.assetTag}</span>
                  )}
                  <div className="muted">{[l.machineName, l.locationName].filter(Boolean).join(' · ')}</div>
                </td>
                {/* Cells do not wrap by default; what was said is a sentence and has to. */}
                <td style={{ whiteSpace: 'normal', minWidth: '16rem', maxWidth: '26rem' }}>
                  <strong>{l.kindLabel}</strong>
                  <div className="muted">{l.summary}</div>
                  {l.hasPoliceReport && <div className="muted">Police complaint made</div>}
                </td>
                <td className="num">{l.estimatedValue !== null ? formatRupees(l.estimatedValue) : <span className="muted">—</span>}</td>
                <td>
                  <StatusPill look={LOSS_STATUS_LOOK[l.status]}>{LOSS_STATUS_LABEL[l.status]}</StatusPill>
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

function Figure({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <div style={{ fontSize: '1.6rem', fontWeight: 700, lineHeight: 1.1 }}>{value}</div>
      <div className="muted">{label}</div>
    </div>
  );
}
