import { Fragment, useEffect, useMemo, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { api } from '../api/client';
import { presetPeriods, recentPeriods } from '../compliancePeriods';
import { formatHours } from '../hours';
import { formatRupees, formatRupeesCompact } from '../money';
import { formatDate, formatDateTime, todayAtHospital } from '../time';
import { PRIORITY_LOOK } from '../statusTones';
import { StatusPill } from '../StatusPill';
import { Tile } from '../Tile';

type Kind = 'downtime' | 'cost' | 'work' | 'stock';

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

type WorkDonePart = { partNumber: string; name: string; quantity: number; unitCost: number | null };

type WorkDoneItem = {
  workOrderId: number;
  number: string;
  equipmentId: number;
  assetTag: string;
  equipmentType: string;
  location: string;
  priority: string;
  fault: string;
  solution: string | null;
  doneBy: string;
  reportedAtUtc: string;
  resolvedAtUtc: string;
  hoursToFix: number;
  downtimeHours: number | null;
  parts: WorkDonePart[];
  partsCost: number;
  partsCostMissing: boolean;
};

type WorkDonePerson = { userId: number | null; name: string; done: number; averageHoursToFix: number; partsCost: number };

type WorkDoneSummary = {
  from: string;
  to: string;
  countedThrough: string;
  scope: string;
  done: number;
  people: number;
  averageHoursToFix: number;
  partsCost: number;
  byPerson: WorkDonePerson[];
  items: WorkDoneItem[];
};

type StockLine = {
  id: number;
  partNumber: string;
  name: string;
  quantityOnHand: number;
  unit: string;
  equipmentType: string | null;
  supplier: string | null;
  storageLocation: string | null;
  unitCost: number | null;
};

type StockSummary = {
  outOfStockCount: number;
  lowStockCount: number;
  outOfStock: StockLine[];
  lowStock: StockLine[];
};

type Place = { id: number; name: string; depth: number };
type Person = { id: number; fullName: string };

const CUSTOM = 'custom';

const TITLES: Record<Kind, string> = { downtime: 'Downtime', cost: 'Cost', work: 'Work done', stock: 'Spare part stock' };

/** Where each report lives on the server. */
const PATH: Record<Kind, string> = { downtime: 'downtime', cost: 'cost', work: 'work-done', stock: 'stock' };

/** The priority names the server sends, as the look each one wears. */
const PRIORITY_BY_NAME: Record<string, number> = { Low: 10, Medium: 20, High: 30, Critical: 40 };

/**
 * The fleet-wide reports: which machines were down and for how long, what has been spent on each
 * and on what, and what work was done and by whom.
 *
 * Pick a period and, if wanted, a department; see the headline and the detail behind it, and
 * take away the CSV for a spreadsheet. The period and place are shared by the reports, so
 * switching between them keeps the question the same.
 */
export function ReportsPage() {
  const today = useMemo(() => todayAtHospital(), []);
  const monthly = useMemo(() => presetPeriods(today), [today]);
  const recent = useMemo(() => recentPeriods(today), [today]);
  const [params, setParams] = useSearchParams();
  const reportParam = params.get('report');
  const kind: Kind =
    reportParam === 'cost' ? 'cost' : reportParam === 'work' ? 'work' : reportParam === 'stock' ? 'stock' : 'downtime';

  // What was done is mostly asked about for a day or a week, so that report offers those first.
  const presets = useMemo(() => (kind === 'work' ? [...recent, ...monthly] : monthly), [kind, recent, monthly]);

  // Today for the work report, and this month for the others: a period still running is the one
  // a head asks about while it matters.
  const initial =
    (kind === 'work' ? recent.find((p) => p.key === 'today') : monthly.find((p) => p.key === 'this-month')) ??
    monthly[0];

  const [choice, setChoice] = useState(initial.key);
  const [from, setFrom] = useState(initial.from);
  const [to, setTo] = useState(initial.to);
  const [locationId, setLocationId] = useState('');
  const [userId, setUserId] = useState('');
  const [places, setPlaces] = useState<Place[]>([]);
  const [people, setPeople] = useState<Person[]>([]);
  const [downtime, setDowntime] = useState<DowntimeSummary | null>(null);
  const [cost, setCost] = useState<CostSummary | null>(null);
  const [work, setWork] = useState<WorkDoneSummary | null>(null);
  const [stock, setStock] = useState<StockSummary | null>(null);
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

  // The people a fault can be credited to, for the work report's filter. An Admin route, like this page.
  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const list = await api.get<Person[]>('/api/people');
        if (!cancelled) setPeople(list);
      } catch {
        // The work report still lists everyone without the filter.
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  const query = useMemo(() => {
    const q = new URLSearchParams({ from, to });
    if (locationId) q.set('locationId', locationId);
    if (kind === 'work' && userId) q.set('userId', userId);
    return q.toString();
  }, [from, to, locationId, userId, kind]);

  const valid = from !== '' && to !== '' && from <= to;

  // The stock report is the shelf as it is now, so it has no period to be valid or not.
  const ready = kind === 'stock' || valid;

  useEffect(() => {
    if (!ready) return;
    let cancelled = false;
    (async () => {
      setError(null);
      try {
        if (kind === 'stock') {
          const s = await api.get<StockSummary>('/api/reports/stock');
          if (!cancelled) setStock(s);
        } else if (kind === 'downtime') {
          const s = await api.get<DowntimeSummary>(`/api/reports/downtime?${query}`);
          if (!cancelled) setDowntime(s);
        } else if (kind === 'cost') {
          const s = await api.get<CostSummary>(`/api/reports/cost?${query}`);
          if (!cancelled) setCost(s);
        } else {
          const s = await api.get<WorkDoneSummary>(`/api/reports/work-done?${query}`);
          if (!cancelled) setWork(s);
        }
      } catch (e) {
        if (!cancelled) {
          setDowntime(null);
          setCost(null);
          setWork(null);
          setStock(null);
          setError(e instanceof Error ? e.message : 'Could not work out the report.');
        }
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [kind, query, ready]);

  function pick(key: string, among = presets) {
    setChoice(key);
    const p = among.find((x) => x.key === key);
    if (p) {
      setFrom(p.from);
      setTo(p.to);
    }
  }

  function show(next: Kind) {
    const q = new URLSearchParams(params);
    q.set('report', next);
    setParams(q, { replace: true });

    // A day or a week only exists for the work report; leaving it for another, go back to the month.
    // And arriving at it from the untouched default, start with today, which is what it is mostly asked.
    if (next === 'work' && choice === 'this-month') pick('today', [...recent, ...monthly]);
    if (next !== 'work' && recent.some((p) => p.key === choice)) pick('this-month', monthly);
  }

  async function download() {
    setBusy(true);
    setError(null);
    try {
      await (kind === 'stock'
        ? api.download('/api/reports/stock/report.csv', `spare-part-stock-${today.replaceAll('-', '')}.csv`)
        : api.download(
            `/api/reports/${PATH[kind]}/report.csv?${query}`,
            `${PATH[kind]}-${from.replaceAll('-', '')}-${to.replaceAll('-', '')}.csv`,
          ));
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not download the report.');
    } finally {
      setBusy(false);
    }
  }

  // The ones with a period to say; the stock report is simply as of now.
  const summary = kind === 'downtime' ? downtime : kind === 'cost' ? cost : kind === 'work' ? work : null;

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <h1>Reports</h1>
          <p className="muted">
            {kind === 'downtime'
              ? 'Which machines were down, for how long, and how often.'
              : kind === 'cost'
                ? 'What has been spent on each machine, and on what.'
                : kind === 'work'
                  ? 'The faults that were fixed, what was done, and who did it.'
                  : 'The spare parts that are out of stock, and the ones running low, as they stand now.'}
          </p>
        </div>
        <button className="btn btn-primary" disabled={!ready || busy} onClick={() => void download()}>
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

      {/* The shelf as it is now: there is no period or place to choose. */}
      {kind !== 'stock' && (
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

        {kind === 'work' && (
          <label className="field">
            <span>Done by</span>
            <select aria-label="Done by" value={userId} onChange={(e) => setUserId(e.target.value)}>
              <option value="">Everyone</option>
              {people.map((p) => (
                <option key={p.id} value={p.id}>{p.fullName}</option>
              ))}
            </select>
          </label>
        )}
      </div>
      )}

      {kind !== 'stock' && !valid && from !== '' && to !== '' && (
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
      {kind === 'work' && work && <WorkDoneView report={work} />}
      {kind === 'stock' && stock && <StockView report={stock} />}
    </div>
  );
}

function StockView({ report }: { report: StockSummary }) {
  if (report.outOfStockCount === 0 && report.lowStockCount === 0) {
    return <p className="alert alert-ok" role="status">Every spare part has more than 5 on the shelf.</p>;
  }

  return (
    <>
      <div className="tiles">
        <Tile
          label="Out of stock"
          value={report.outOfStockCount}
          tone={report.outOfStockCount > 0 ? 'danger' : undefined}
          hint="None left"
        />
        <Tile
          label="Low stock"
          value={report.lowStockCount}
          tone={report.lowStockCount > 0 ? 'warn' : undefined}
          hint="1 to 5 left"
        />
      </div>

      <StockTable title="Out of stock" empty="No spare part is out of stock." lines={report.outOfStock} />
      <StockTable title="Low stock" empty="No spare part is running low." lines={report.lowStock} />
    </>
  );
}

function StockTable({ title, empty, lines }: { title: string; empty: string; lines: StockLine[] }) {
  return (
    <section className="card table-wrap">
      <table className="table">
        <caption className="table-caption">{title}</caption>
        <thead>
          <tr>
            <th>Part</th>
            <th className="num">In stock</th>
            <th>Used in</th>
            <th>Supplier</th>
            <th>Where it&rsquo;s kept</th>
          </tr>
        </thead>
        <tbody>
          {lines.length === 0 && (
            <tr><td colSpan={5} className="empty">{empty}</td></tr>
          )}
          {lines.map((l) => (
            <tr key={l.id}>
              <td>
                <strong>{l.name}</strong>
                <div className="mono muted">{l.partNumber}</div>
              </td>
              <td className="num">{l.quantityOnHand} {l.unit}</td>
              <td>{l.equipmentType ?? <span className="muted">Not specific to one type</span>}</td>
              <td>
                {l.supplier ?? <span className="muted">—</span>}
                {l.unitCost != null && <div className="muted">{formatRupees(l.unitCost)} / {l.unit}</div>}
              </td>
              <td>{l.storageLocation ?? <span className="muted">—</span>}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}

function WorkDoneView({ report }: { report: WorkDoneSummary }) {
  if (report.items.length === 0) {
    return <p className="alert alert-info">No work was finished in this period.</p>;
  }

  return (
    <>
      <div className="tiles">
        <Tile label="Faults fixed" value={report.done} />
        <Tile label="People" value={report.people} />
        <Tile label="Average time to fix" value={formatHours(report.averageHoursToFix)} hint="From reported to resolved" />
        <Tile label="Parts used" value={formatRupees(report.partsCost)} />
      </div>

      <section className="card table-wrap">
        <table className="table">
          <caption className="table-caption">Who did the work</caption>
          <thead>
            <tr>
              <th>Person</th>
              <th className="num">Faults fixed</th>
              <th className="num">Average time to fix</th>
              <th className="num">Parts used</th>
            </tr>
          </thead>
          <tbody>
            {report.byPerson.map((p) => (
              <tr key={p.name}>
                <td>{p.name}</td>
                <td className="num">{p.done}</td>
                <td className="num">{formatHours(p.averageHoursToFix)}</td>
                <td className="num">{formatRupees(p.partsCost)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>

      <section className="card table-wrap">
        <table className="table">
          <caption className="table-caption">Work done, latest first</caption>
          <thead>
            <tr>
              <th>Service request</th>
              <th>Machine</th>
              <th>Fault</th>
              <th>What was done</th>
              <th>Done by</th>
              <th>Resolved</th>
              <th className="num">Took</th>
              <th>Parts</th>
            </tr>
          </thead>
          <tbody>
            {report.items.map((i) => (
              <tr key={i.workOrderId}>
                <td>
                  <Link
                    to={`/work-orders/${i.workOrderId}`}
                    state={{ from: '/reports?report=work' }}
                    className="mono"
                  >
                    {i.number}
                  </Link>
                  <div>
                    <StatusPill look={PRIORITY_LOOK[PRIORITY_BY_NAME[i.priority] ?? 20]}>{i.priority}</StatusPill>
                  </div>
                </td>
                <MachineCell id={i.equipmentId} tag={i.assetTag} type={i.equipmentType} />
                <td>{i.fault}</td>
                <td>{i.solution ?? <span className="muted">—</span>}</td>
                <td>{i.doneBy}</td>
                <td>{formatDateTime(i.resolvedAtUtc)}</td>
                <td className="num">{formatHours(i.hoursToFix)}</td>
                <td>
                  {i.parts.length === 0 ? (
                    <span className="muted">None</span>
                  ) : (
                    <>
                      {i.parts.map((p) => (
                        <div key={`${p.partNumber}-${p.quantity}`}>
                          <span className="mono">{p.quantity}× {p.partNumber}</span>
                        </div>
                      ))}
                      <div className="muted">
                        {formatRupees(i.partsCost)}
                        {i.partsCostMissing && ' · some cost not recorded'}
                      </div>
                    </>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>
    </>
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
        to a machine's record, or use a spare part on a service request.
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
