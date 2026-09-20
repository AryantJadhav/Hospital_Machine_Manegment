import { useEffect, useMemo, useState } from 'react';
import { api } from '../api/client';
import { presetPeriods } from '../compliancePeriods';
import { formatDate, todayAtHospital } from '../time';

type Totals = {
  due: number;
  onTime: number;
  late: number;
  skipped: number;
  overdue: number;
  withinGrace: number;
  withFindings: number;
  completed: number;
  onSchedulePercent: number | null;
  completionPercent: number | null;
};

type Group = { name: string; totals: Totals };

type Summary = {
  from: string;
  to: string;
  countedThrough: string;
  scope: string;
  notYetDue: number;
  machines: number;
  totals: Totals;
  byDepartment: Group[];
  byType: Group[];
  exceptions: number;
};

type Place = { id: number; name: string; depth: number };

const CUSTOM = 'custom';

const pct = (p: number | null) => (p === null ? '—' : `${p.toFixed(1)}%`);

/**
 * The PM compliance report an accreditation assessor asks for.
 *
 * Pick a period and, if wanted, a department; see the headline here, and take
 * away the PDF to hand over or the CSV with a row for every PM.
 */
export function CompliancePage() {
  const presets = useMemo(() => presetPeriods(todayAtHospital()), []);

  // Last month by default: the period an assessor most often asks about is one
  // that has finished, and its figures no longer move.
  const initial = presets.find((p) => p.key === 'last-month') ?? presets[0];

  const [choice, setChoice] = useState(initial.key);
  const [from, setFrom] = useState(initial.from);
  const [to, setTo] = useState(initial.to);
  const [locationId, setLocationId] = useState('');
  const [places, setPlaces] = useState<Place[]>([]);
  const [summary, setSummary] = useState<Summary | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState<'pdf' | 'csv' | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const list = await api.get<Place[]>('/api/lookups/locations');
        if (!cancelled) setPlaces(list);
      } catch {
        // The report works for the whole hospital without the list.
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  const query = useMemo(() => {
    const q = new URLSearchParams({ from, to });
    if (locationId) q.set('locationId', locationId);
    return q.toString();
  }, [from, to, locationId]);

  const valid = from !== '' && to !== '' && from <= to;

  useEffect(() => {
    if (!valid) return;
    let cancelled = false;
    (async () => {
      setError(null);
      try {
        const s = await api.get<Summary>(`/api/reports/pm-compliance?${query}`);
        if (!cancelled) setSummary(s);
      } catch (e) {
        if (!cancelled) {
          setSummary(null);
          setError(e instanceof Error ? e.message : 'Could not work out the report.');
        }
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [query, valid]);

  function pick(key: string) {
    setChoice(key);
    const p = presets.find((x) => x.key === key);
    if (p) {
      setFrom(p.from);
      setTo(p.to);
    }
  }

  async function download(kind: 'pdf' | 'csv') {
    setBusy(kind);
    setError(null);
    try {
      await api.download(
        `/api/reports/pm-compliance/report.${kind}?${query}`,
        `PM-compliance-${from.replaceAll('-', '')}-${to.replaceAll('-', '')}.${kind}`,
      );
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not download the report.');
    } finally {
      setBusy(null);
    }
  }

  const t = summary?.totals;

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <h1>Compliance report</h1>
          <p className="muted">
            Of the preventive maintenance that fell due, how much was done on time, and every PM that was not.
          </p>
        </div>
        <div className="row">
          <button className="btn" disabled={!valid || busy !== null} onClick={() => void download('csv')}>
            {busy === 'csv' ? 'Preparing…' : 'Download CSV'}
          </button>
          <button className="btn btn-primary" disabled={!valid || busy !== null} onClick={() => void download('pdf')}>
            {busy === 'pdf' ? 'Preparing…' : 'Download PDF'}
          </button>
        </div>
      </header>

      <div className="filters card">
        <label className="field">
          <span>Period</span>
          <select aria-label="Period" value={choice} onChange={(e) => pick(e.target.value)}>
            {presets.map((p) => (
              <option key={p.key} value={p.key}>{p.label}</option>
            ))}
            <option value={CUSTOM}>Choose dates…</option>
          </select>
        </label>

        <label className="field">
          <span>From</span>
          <input
            type="date"
            aria-label="From"
            value={from}
            onChange={(e) => {
              setFrom(e.target.value);
              setChoice(CUSTOM);
            }}
          />
        </label>

        <label className="field">
          <span>To</span>
          <input
            type="date"
            aria-label="To"
            value={to}
            onChange={(e) => {
              setTo(e.target.value);
              setChoice(CUSTOM);
            }}
          />
        </label>

        <label className="field grow">
          <span>Where</span>
          <select aria-label="Where" value={locationId} onChange={(e) => setLocationId(e.target.value)}>
            <option value="">Whole hospital</option>
            {places.map((l) => (
              <option key={l.id} value={l.id}>
                {' '.repeat(l.depth * 3)}
                {l.name}
              </option>
            ))}
          </select>
        </label>
      </div>

      {!valid && from !== '' && to !== '' && (
        <p className="alert alert-error" role="alert">The end of the period is before its start.</p>
      )}

      {error && <p className="alert alert-error" role="alert">{error}</p>}

      {summary && t && (
        <>
          <p className="muted">
            {summary.scope} · {formatDate(summary.from)} to {formatDate(summary.to)}
            {summary.countedThrough < summary.to && (
              <> · counted through {formatDate(summary.countedThrough)}, as the period has not ended</>
            )}
          </p>

          {t.due === 0 ? (
            <p className="alert alert-info">No PM fell due in this period{locationId ? ' in this place' : ''}.</p>
          ) : (
            <>
              <div className="tiles">
                <div className={`tile ${tone(t.onSchedulePercent)}`}>
                  <span className="tile-value">{pct(t.onSchedulePercent)}</span>
                  <span className="tile-label">Done on schedule</span>
                  <span className="tile-hint">{t.onTime} of {t.due} PMs that fell due</span>
                </div>
                <div className="tile">
                  <span className="tile-value">{pct(t.completionPercent)}</span>
                  <span className="tile-label">Done at all</span>
                  <span className="tile-hint">{t.completed} of {t.due}, including late</span>
                </div>
                <div className={`tile ${t.late > 0 ? 'tile-warn' : 'tile-ok'}`}>
                  <span className="tile-value">{t.late}</span>
                  <span className="tile-label">Done late</span>
                </div>
                <div className={`tile ${t.overdue > 0 ? 'tile-danger' : 'tile-ok'}`}>
                  <span className="tile-value">{t.overdue}</span>
                  <span className="tile-label">Overdue, not done</span>
                </div>
                <div className={`tile ${t.skipped > 0 ? 'tile-warn' : 'tile-ok'}`}>
                  <span className="tile-value">{t.skipped}</span>
                  <span className="tile-label">Skipped</span>
                </div>
                <div className="tile">
                  <span className="tile-value">{t.withFindings}</span>
                  <span className="tile-label">With findings</span>
                  <span className="tile-hint">out of spec or a failed check</span>
                </div>
              </div>

              <p className="muted">
                The PDF lists all {summary.exceptions} late, overdue and skipped PM{summary.exceptions === 1 ? '' : 's'}
                {' '}with the reason for each skip, across {summary.machines} machine{summary.machines === 1 ? '' : 's'}.
                {summary.notYetDue > 0 && <> {summary.notYetDue} more have not fallen due yet and are not counted.</>}
              </p>

              <Breakdown title="By department" groups={summary.byDepartment} />
              <Breakdown title="By equipment type" groups={summary.byType} />
            </>
          )}
        </>
      )}
    </div>
  );
}

function tone(percent: number | null): string {
  if (percent === null) return '';
  if (percent >= 95) return 'tile-ok';
  if (percent >= 80) return 'tile-warn';
  return 'tile-danger';
}

function Breakdown({ title, groups }: { title: string; groups: Group[] }) {
  if (groups.length === 0) return null;

  return (
    <section className="card table-wrap">
      <table className="table">
        <caption className="table-caption">{title}, worst first</caption>
        <thead>
          <tr>
            <th>{title.replace('By ', '')}</th>
            <th className="num">Due</th>
            <th className="num">On time</th>
            <th className="num">Late</th>
            <th className="num">Skipped</th>
            <th className="num">Overdue</th>
            <th className="num">On schedule</th>
          </tr>
        </thead>
        <tbody>
          {groups.map((g) => (
            <tr key={g.name}>
              <td>{g.name}</td>
              <td className="num">{g.totals.due}</td>
              <td className="num">{g.totals.onTime}</td>
              <td className="num">{g.totals.late}</td>
              <td className="num">{g.totals.skipped}</td>
              <td className="num">{g.totals.overdue}</td>
              <td className="num"><strong>{pct(g.totals.onSchedulePercent)}</strong></td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}
