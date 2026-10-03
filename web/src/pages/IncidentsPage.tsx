import { useCallback, useEffect, useState } from 'react';
import { Link, useLocation, useNavigate, useSearchParams } from 'react-router-dom';
import { api } from '../api/client';
import { PERMISSIONS } from '../auth/context';
import { useAuth } from '../auth/useAuth';
import {
  DAMAGE_LOOK,
  INCIDENT_STATUS_LABEL,
  INCIDENT_STATUS_LOOK,
  INCIDENT_TYPES,
} from '../incidentTypes';
import type { IncidentCounts, IncidentRow, IncidentSummary } from '../incidentTypes';
import { StatusPill } from '../StatusPill';
import { formatDate, todayAtHospital } from '../time';

type Page = { items: IncidentRow[]; total: number; page: number; pageSize: number; counts: IncidentCounts };

const PAGE_SIZE = 25;

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
 * Incidents: the times a machine was dropped, mishandled, knocked, soaked or lost. A department writes
 * them up; the biomedical team looks into each one and says what caused it. Opens on what is still open,
 * the oldest first, with the figures and the printed report for the period chosen.
 *
 * A person from another department sees the incidents on their own departments' machines, and nothing else.
 */
export function IncidentsPage() {
  const { may } = useAuth();
  const canReport = may(PERMISSIONS.incidentsReport);
  const canOpenMachine = may(PERMISSIONS.registerView);
  const navigate = useNavigate();
  const location = useLocation();

  const [params, setParams] = useSearchParams();
  const tab = ['open', 'closed', 'all'].includes(params.get('status') ?? '') ? params.get('status')! : 'open';
  const period = params.get('period') ?? 'all';
  const custom = period === 'custom';
  const from = params.get('from') ?? '';
  const to = params.get('to') ?? '';
  const type = params.get('type') ?? '';
  const mine = params.get('mine') === 'true';
  const q = params.get('q') ?? '';
  const page = Math.max(1, Number(params.get('page')) || 1);

  const [search, setSearch] = useState(q);
  const [data, setData] = useState<Page | null>(null);
  const [summary, setSummary] = useState<IncidentSummary | null>(null);
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
      if (type) query.set('type', type);
      if (mine) query.set('mine', 'true');
      if (periodFrom) query.set('from', periodFrom);
      if (periodTo) query.set('to', periodTo);

      const figures = new URLSearchParams();
      if (periodFrom) figures.set('from', periodFrom);
      if (periodTo) figures.set('to', periodTo);

      try {
        const [list, sum] = await Promise.all([
          api.get<Page>(`/api/incidents?${query}`),
          api.get<IncidentSummary>(`/api/incidents/summary?${figures}`),
        ]);
        if (cancelled) return;
        setData(list);
        setSummary(sum);
      } catch (e) {
        if (!cancelled) setError(e instanceof Error ? e.message : 'Could not load the incidents.');
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [tab, q, type, mine, periodFrom, periodTo, waiting, page]);

  const totalPages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;
  const filtered = Boolean(q || type || mine);
  const here = `${location.pathname}${location.search}`;
  const open = data ? data.counts.reported + data.counts.inReview : null;

  // The report is of the period and nothing narrower: it is the summary the department files.
  const reportQuery = new URLSearchParams();
  if (periodFrom) reportQuery.set('from', periodFrom);
  if (periodTo) reportQuery.set('to', periodTo);

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <h1>Incidents</h1>
          <p className="muted">
            Machines that were dropped, mishandled, knocked, soaked or lost, and what was found.
          </p>
        </div>
        <div className="row">
          {/* A preview in a tab of its own first; printing or downloading is a choice made there. */}
          <a
            className="btn"
            href={`/incidents/report${reportQuery.toString() ? `?${reportQuery}` : ''}`}
            target="_blank"
            rel="noopener"
          >
            Print the incident report
          </a>
          {canReport && (
            <button className="btn btn-primary" onClick={() => navigate('/incidents/new', { state: { from: here } })}>
              Report an incident
            </button>
          )}
        </div>
      </header>

      {error && <p className="alert alert-error" role="alert">{error}</p>}

      {summary && summary.total > 0 && (
        <section className="card stack" aria-label="The period at a glance">
          <div className="row" style={{ gap: '2rem', flexWrap: 'wrap' }}>
            <Figure label="Incidents" value={summary.total} />
            <Figure label="Still open" value={summary.open} />
            <Figure label="Closed" value={summary.closed} />
            <Figure label="Taken out of use" value={summary.takenOutOfUse} />
            <Figure label="Beyond repair" value={summary.byDamage.find((d) => d.number === 40)?.value ?? 0} />
          </div>
          <p className="muted" style={{ margin: 0 }}>
            {summary.byType.map((t) => `${t.label}: ${t.value}`).join(' · ')}
          </p>
          {summary.repeatMachines.length > 0 && (
            <div className="alert alert-warn row" style={{ margin: 0, alignItems: 'center', flexWrap: 'wrap', gap: '0.25rem 0.5rem' }} role="status">
              <span>More than one incident on the same machine:</span>
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
                    {m.assetTag} ({m.incidents})
                  </button>
                </span>
              ))}
            </div>
          )}
        </section>
      )}

      <div className="row" role="tablist" aria-label="Incidents">
        {[
          { key: 'open', label: 'Open', n: open },
          { key: 'closed', label: 'Closed', n: data ? data.counts.closed : null },
          { key: 'all', label: 'All', n: data ? data.counts.reported + data.counts.inReview + data.counts.closed : null },
        ].map((t) => (
          <button
            key={t.key}
            role="tab"
            aria-selected={tab === t.key}
            className={tab === t.key ? 'btn btn-primary' : 'btn'}
            onClick={() => setParam('status', t.key === 'open' ? '' : t.key)}
          >
            {t.label}{t.n !== null && ` (${t.n.toLocaleString('en-IN')})`}
          </button>
        ))}
      </div>

      <div className="filters card">
        <input
          type="search"
          className="grow"
          aria-label="Search incidents"
          placeholder="Find an incident: its number, a machine number, a place or what was said"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />

        <label className="field">
          <span>Kind</span>
          <select aria-label="Kind of incident" value={type} onChange={(e) => setParam('type', e.target.value)}>
            <option value="">Any</option>
            {INCIDENT_TYPES.map((t) => <option key={t.value} value={t.value}>{t.label}</option>)}
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
              <th>Incident</th>
              <th>Machine</th>
              <th>What happened</th>
              <th>Left</th>
              <th>Status</th>
            </tr>
          </thead>
          <tbody>
            {!data && !error && <tr><td colSpan={5} className="empty">Loading…</td></tr>}
            {data && data.items.length === 0 && (
              <tr>
                <td colSpan={5} className="empty">
                  {filtered
                    ? 'No incident matches that.'
                    : tab === 'open'
                      ? 'Nothing is open. Every incident has been looked into.'
                      : 'No incident has been written up yet.'}
                </td>
              </tr>
            )}
            {data?.items.map((i) => (
              <tr key={i.id}>
                <td>
                  <Link to={`/incidents/${i.id}`} state={{ from: here }} className="mono">{i.reference}</Link>
                  <div className="muted">
                    {formatDate(i.occurredOn)}
                    {i.occurredAt && ` ${i.occurredAt}`}
                  </div>
                </td>
                <td>
                  {/* A person from another department sees the number and nothing opens. */}
                  {canOpenMachine ? (
                    <Link to={`/equipment/${i.equipmentId}`} state={{ from: here }} className="mono">{i.assetTag}</Link>
                  ) : (
                    <span className="mono">{i.assetTag}</span>
                  )}
                  <div className="muted">{[i.machineName, i.locationName].filter(Boolean).join(' · ')}</div>
                </td>
                {/* Cells do not wrap by default; what was said is a sentence and has to. */}
                <td style={{ whiteSpace: 'normal', minWidth: '16rem', maxWidth: '26rem' }}>
                  <strong>{i.typeLabel}</strong>
                  <div className="muted">{i.summary}</div>
                </td>
                <td>
                  <StatusPill look={DAMAGE_LOOK[i.damage]}>{i.damageLabel}</StatusPill>
                  {i.takenOutOfUse && <div className="muted">Taken out of use</div>}
                </td>
                <td>
                  <StatusPill look={INCIDENT_STATUS_LOOK[i.status]}>{INCIDENT_STATUS_LABEL[i.status]}</StatusPill>
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

function Figure({ label, value }: { label: string; value: number }) {
  return (
    <div>
      <div style={{ fontSize: '1.6rem', fontWeight: 700, lineHeight: 1.1 }}>{value.toLocaleString('en-IN')}</div>
      <div className="muted">{label}</div>
    </div>
  );
}
