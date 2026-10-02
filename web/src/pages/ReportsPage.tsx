import { Fragment, useEffect, useMemo, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { api } from '../api/client';
import { presetPeriods } from '../compliancePeriods';
import { formatHours } from '../hours';
import { formatRupees, formatRupeesCompact } from '../money';
import { formatDate, todayAtHospital } from '../time';
import { StatusPill } from '../StatusPill';
import { Tile } from '../Tile';

type Kind = 'downtime' | 'cost';

type DowntimeMachine = {
  equipmentId: number;
  assetTag: string;
  equipmentType: string;
  location: string;
  incidents: number;
  downtimeHours: number;
  availabilityPercent: number;
  stillDown: boolean;
};

type DowntimeSummary = {
  from: string;
  to: string;
  countedThrough: string;
  scope: string;
  machinesAffected: number;
  incidents: number;
  totalDowntimeHours: number;
  stillDown: number;
  machines: DowntimeMachine[];
};

type CostPolicy = {
  provider: string;
  policyNumber: string | null;
  expiryDate: string;
  cost: number | null;
  isCurrent: boolean;
};

type CostPart = { partNumber: string; name: string; quantity: number; cost: number; costMissing: boolean };

type CostMachine = {
  equipmentId: number;
  assetTag: string;
  equipmentType: string;
  location: string;
  purchaseCost: number | null;
  insuranceCost: number | null;
  contractCost: number | null;
  partsCost: number;
  total: number;
  policies: CostPolicy[];
  parts: CostPart[];
};

type CostSummary = {
  from: string;
  to: string;
  countedThrough: string;
  scope: string;
  machines: CostMachine[];
  machinesWithoutCost: number;
  partsWithoutCost: number;
  totals: { purchase: number; insurance: number; contract: number; parts: number; total: number };
};

type Place = { id: number; name: string; depth: number };

const CUSTOM = 'custom';

const TITLES: Record<Kind, string> = { downtime: 'Downtime', cost: 'Cost' };

/**
 * The fleet-wide reports: which machines were down and for how long, and what has been spent on
 * each and on what.
 *
 * Pick a period and, if wanted, a department; see the headline and the machines behind it, and
 * take away the CSV for a spreadsheet. The period and place are shared by both reports, so
 * switching between them keeps the question the same.
 */
export function ReportsPage() {
  const presets = useMemo(() => presetPeriods(todayAtHospital()), []);
  const [params, setParams] = useSearchParams();
  const kind: Kind = params.get('report') === 'cost' ? 'cost' : 'downtime';

  // This month by default: a period still running is the one a head asks about while it matters.
  const initial = presets.find((p) => p.key === 'this-month') ?? presets[0];

  const [choice, setChoice] = useState(initial.key);
  const [from, setFrom] = useState(initial.from);
  const [to, setTo] = useState(initial.to);
  const [locationId, setLocationId] = useState('');
  const [places, setPlaces] = useState<Place[]>([]);
  const [downtime, setDowntime] = useState<DowntimeSummary | null>(null);
  const [cost, setCost] = useState<CostSummary | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const list = await api.get<Place[]>('/api/lookups/locations');
        if (!cancelled) setPlaces(list);
      } catch {
        // The reports work for the whole hospital without the list.
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
        if (kind === 'downtime') {
          const s = await api.get<DowntimeSummary>(`/api/reports/downtime?${query}`);
          if (!cancelled) setDowntime(s);
        } else {
          const s = await api.get<CostSummary>(`/api/reports/cost?${query}`);
          if (!cancelled) setCost(s);
        }
      } catch (e) {
        if (!cancelled) {
          setDowntime(null);
          setCost(null);
          setError(e instanceof Error ? e.message : 'Could not work out the report.');
        }
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [kind, query, valid]);

  function pick(key: string) {
    setChoice(key);
    const p = presets.find((x) => x.key === key);
    if (p) {
      setFrom(p.from);
      setTo(p.to);
    }
  }

  function show(next: Kind) {
    const q = new URLSearchParams(params);
    q.set('report', next);
    setParams(q, { replace: true });
  }

  async function download() {
    setBusy(true);
    setError(null);
    try {
      await api.download(
        `/api/reports/${kind}/report.csv?${query}`,
        `${kind}-${from.replaceAll('-', '')}-${to.replaceAll('-', '')}.csv`,
      );
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not download the report.');
    } finally {
      setBusy(false);
    }
  }

  const summary = kind === 'downtime' ? downtime : cost;

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <h1>Reports</h1>
          <p className="muted">
            {kind === 'downtime'
              ? 'Which machines were down, for how long, and how often.'
              : 'What has been spent on each machine, and on what.'}
          </p>
        </div>
        <button className="btn btn-primary" disabled={!valid || busy} onClick={() => void download()}>
          {busy ? 'Preparing…' : 'Download CSV'}
        </button>
      </header>

      <div className="row" role="tablist" aria-label="Report">
        {(Object.keys(TITLES) as Kind[]).map((k) => (
          <button
            key={k}
            role="tab"
            aria-selected={kind === k}
            className={kind === k ? 'btn btn-primary' : 'btn'}
            onClick={() => show(k)}
          >
            {TITLES[k]}
          </button>
        ))}
      </div>

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
                {' '.repeat(l.depth * 3)}
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

      {summary && (
        <p className="muted">
          {summary.scope} · {formatDate(summary.from)} to {formatDate(summary.to)}
          {summary.countedThrough < summary.to && (
            <> · counted through {formatDate(summary.countedThrough)}, as the period has not ended</>
          )}
        </p>
      )}

      {kind === 'downtime' && downtime && <DowntimeView report={downtime} />}
      {kind === 'cost' && cost && <CostView report={cost} />}
    </div>
  );
}

function MachineCell({ id, tag, type }: { id: number; tag: string; type: string }) {
  return (
    <td>
      <Link to={`/equipment/${id}`} className="mono">{tag}</Link>
      <div className="muted">{type}</div>
    </td>
  );
}

function DowntimeView({ report }: { report: DowntimeSummary }) {
  if (report.machines.length === 0) {
    return <p className="alert alert-info">No machine was reported down in this period.</p>;
  }

  return (
    <>
      <div className="tiles">
        <Tile label="Machines affected" value={report.machinesAffected} />
        <Tile label="Downtime reports" value={report.incidents} />
        <Tile label="Total downtime" value={formatHours(report.totalDowntimeHours)} />
        <Tile
          label="Still down now"
          value={report.stillDown}
          tone={report.stillDown > 0 ? 'danger' : undefined}
        />
      </div>

      <section className="card table-wrap">
        <table className="table">
          <caption className="table-caption">Machines, longest down first</caption>
          <thead>
            <tr>
              <th>Machine</th>
              <th>Where</th>
              <th className="num">Reports</th>
              <th className="num">Downtime</th>
              <th className="num">Up</th>
            </tr>
          </thead>
          <tbody>
            {report.machines.map((m) => (
              <tr key={m.equipmentId}>
                <MachineCell id={m.equipmentId} tag={m.assetTag} type={m.equipmentType} />
                <td>
                  {m.location}
                  {m.stillDown && <StatusPill tone="danger" className="hist-flag">Still down</StatusPill>}
                </td>
                <td className="num">{m.incidents}</td>
                <td className="num"><strong>{formatHours(m.downtimeHours)}</strong></td>
                <td className="num">{m.availabilityPercent.toFixed(1)}%</td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>
    </>
  );
}

/** The insurance policies and spare parts behind one machine's figures. */
function CostDetail({ m }: { m: CostMachine }) {
  const label = { fontSize: '0.8rem', marginBottom: '0.25rem' } as const;
  const lines = { listStyle: 'none', margin: 0, padding: 0, gap: '0.25rem' } as const;

  return (
    <div className="stack" style={{ gap: '0.75rem' }}>
      {m.policies.length > 0 && (
        <div>
          <div className="muted" style={label}>Insurance policies</div>
          <ul className="stack" style={lines}>
            {m.policies.map((p) => (
              <li key={`${p.provider}-${p.expiryDate}`} className="row" style={{ justifyContent: 'space-between' }}>
                <span>
                  {p.provider}
                  {p.isCurrent && <StatusPill tone="success" className="hist-flag">Current</StatusPill>}
                  <span className="muted">
                    {' · '}{p.policyNumber ? `${p.policyNumber} · ` : ''}covered until {formatDate(p.expiryDate)}
                  </span>
                </span>
                <span>{formatRupees(p.cost)}</span>
              </li>
            ))}
          </ul>
        </div>
      )}

      {m.parts.length > 0 && (
        <div>
          <div className="muted" style={label}>Spare parts used in this period</div>
          <ul className="stack" style={lines}>
            {m.parts.map((p) => (
              <li key={p.partNumber} className="row" style={{ justifyContent: 'space-between' }}>
                <span>
                  <span className="mono">{p.quantity}× {p.partNumber}</span> — {p.name}
                  {p.costMissing && <StatusPill tone="warning" className="hist-flag">Some cost not recorded</StatusPill>}
                </span>
                <span>{formatRupees(p.cost)}</span>
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  );
}

function CostView({ report }: { report: CostSummary }) {
  const t = report.totals;
  const [open, setOpen] = useState<Set<number>>(new Set());

  function toggle(id: number) {
    setOpen((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  if (report.machines.length === 0) {
    return (
      <p className="alert alert-info">
        No cost is recorded for any machine here. Add a purchase cost, insurance or maintenance contract
        to a machine's record, or use a spare part on a work order.
      </p>
    );
  }

  return (
    <>
      <div className="tiles">
        <Tile label="Total spend" value={formatRupeesCompact(t.total)} />
        <Tile label="Purchase" value={formatRupeesCompact(t.purchase)} />
        <Tile label="Insurance" value={formatRupeesCompact(t.insurance)} />
        <Tile label="Maintenance contracts" value={formatRupeesCompact(t.contract)} />
        <Tile label="Spare parts" value={formatRupeesCompact(t.parts)} hint="used in this period" />
      </div>

      <p className="muted">
        Purchase, insurance and contract costs are shown as recorded on each machine, whatever the period.
        Spare parts are counted only for the period chosen.
        {report.machinesWithoutCost > 0 && (
          <> {report.machinesWithoutCost} machine{report.machinesWithoutCost === 1 ? ' has' : 's have'} no cost recorded and {report.machinesWithoutCost === 1 ? 'is' : 'are'} not listed.</>
        )}
        {report.partsWithoutCost > 0 && (
          <> {report.partsWithoutCost} part{report.partsWithoutCost === 1 ? '' : 's'} used had no cost recorded and {report.partsWithoutCost === 1 ? 'is' : 'are'} not counted.</>
        )}
      </p>

      <section className="card table-wrap">
        <table className="table">
          <caption className="table-caption">Machines, dearest first</caption>
          <thead>
            <tr>
              <th>Machine</th>
              <th>Where</th>
              <th className="num">Purchase</th>
              <th className="num">Insurance</th>
              <th className="num">Contract</th>
              <th className="num">Spare parts</th>
              <th className="num">Total</th>
            </tr>
          </thead>
          <tbody>
            {report.machines.map((m) => {
              const expanded = open.has(m.equipmentId);
              const hasDetail = m.policies.length > 0 || m.parts.length > 0;

              return (
                <Fragment key={m.equipmentId}>
                  <tr>
                    <MachineCell id={m.equipmentId} tag={m.assetTag} type={m.equipmentType} />
                    <td>{m.location}</td>
                    <td className="num">{formatRupees(m.purchaseCost)}</td>
                    <td className="num">
                      {formatRupees(m.insuranceCost)}
                      {m.policies.length > 1 && <div className="muted">{m.policies.length} policies</div>}
                    </td>
                    <td className="num">{formatRupees(m.contractCost)}</td>
                    <td className="num">{m.partsCost > 0 ? formatRupees(m.partsCost) : '—'}</td>
                    <td className="num">
                      <strong>{formatRupees(m.total)}</strong>
                      {hasDetail && (
                        <div>
                          <button
                            className="btn btn-quiet"
                            aria-expanded={expanded}
                            aria-label={`${expanded ? 'Hide' : 'Show'} what makes up ${m.assetTag}`}
                            onClick={() => toggle(m.equipmentId)}
                          >
                            {expanded ? 'Hide' : 'Details'}
                          </button>
                        </div>
                      )}
                    </td>
                  </tr>
                  {expanded && (
                    <tr>
                      <td colSpan={7}><CostDetail m={m} /></td>
                    </tr>
                  )}
                </Fragment>
              );
            })}
          </tbody>
        </table>
      </section>
    </>
  );
}
