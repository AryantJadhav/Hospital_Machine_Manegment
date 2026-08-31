import { useCallback, useEffect, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { api, ApiError } from '../api/client';
import { useAuth } from '../auth/useAuth';
import { ROLES } from '../auth/context';

type History = {
  equipment: {
    id: number;
    assetTag: string;
    serialNumber: string | null;
    manufacturer: string | null;
    model: string | null;
    status: number;
    purchaseDate: string | null;
    installationDate: string | null;
    warrantyExpiryDate: string | null;
    notes: string | null;
    equipmentTypeName: string;
    locationId: number;
    locationName: string;
  };
  breadcrumb: { id: number; name: string; level: number }[];
  openPm: { id: number; dueDate: string; status: number; checklistName: string; daysLate: number }[];
  completedPm: {
    id: number;
    dueDate: string;
    status: number;
    completedAtUtc: string | null;
    skipReason: string | null;
    checklistName: string;
    completedBy: string | null;
    outOfRange: number;
    hasCertificate: boolean;
  }[];
  workOrders: {
    id: number;
    number: string;
    status: number;
    priority: number;
    faultDescription: string;
    reportedAtUtc: string;
    resolvedAtUtc: string | null;
    resolutionNotes: string | null;
  }[];
  summary: {
    openPmCount: number;
    overduePmCount: number;
    completedPmCount: number;
    openWorkOrderCount: number;
    totalWorkOrderCount: number;
    totalDowntimeMinutes: number;
    currentlyDown: boolean;
  };
};

const EQUIPMENT_STATUS: Record<number, string> = {
  10: 'In store', 20: 'In service', 30: 'Under repair', 40: 'Condemned', 50: 'Disposed',
};

const PM_STATUS: Record<number, string> = {
  10: 'Scheduled', 20: 'Due', 30: 'Overdue', 40: 'Completed', 50: 'Skipped',
};

const WO_STATUS: Record<number, string> = {
  10: 'Reported', 20: 'Assigned', 30: 'In progress', 40: 'On hold',
  50: 'Resolved', 60: 'Closed', 70: 'Cancelled',
};

const PRIORITY: Record<number, string> = { 10: 'Low', 20: 'Medium', 30: 'High', 40: 'Critical' };

export function EquipmentDetailPage() {
  const { id } = useParams();
  const navigate = useNavigate();
  const { can } = useAuth();
  const canPrint = can(ROLES.admin, ROLES.biomedicalHead, ROLES.seniorEngineer);

  const [data, setData] = useState<History | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      setData(await api.get<History>(`/api/equipment/${id}/history`));
    } catch (e) {
      setError(
        e instanceof ApiError && e.status === 404
          ? 'No equipment with that id.'
          : e instanceof Error ? e.message : 'Could not load this machine.',
      );
    } finally {
      setLoading(false);
    }
  }, [id]);

  useEffect(() => {
    void load();
  }, [load]);

  if (loading) return <div className="page"><p className="muted">Loading…</p></div>;
  if (error) return <div className="page"><p className="alert alert-error">{error}</p></div>;
  if (!data) return null;

  const e = data.equipment;
  const warranty = e.warrantyExpiryDate ? new Date(e.warrantyExpiryDate) : null;
  const inWarranty = warranty !== null && warranty.getTime() >= Date.now();

  return (
    <div className="page">
      <header className="page-head">
        <div>
          {/* Where it sits, whole chain. "Ward 3" alone does not say which
              building, and a group can have one in every hospital. */}
          <p className="muted crumb">
            {data.breadcrumb.map((b) => b.name).join(' › ')}
          </p>
          <h1 className="mono">{e.assetTag}</h1>
          <p className="muted">{e.equipmentTypeName}</p>
        </div>

        <div className="row">
          {canPrint && (
            <button
              className="btn"
              onClick={() =>
                api.downloadPost('/api/labels/sheet', { equipmentIds: [e.id] }, `${e.assetTag}.pdf`)
              }
            >
              Print label
            </button>
          )}
          <Link className="btn" to="/work-orders">Report a fault</Link>
        </div>
      </header>

      <div className="row">
        <span className={`pill pill-${e.status}`}>{EQUIPMENT_STATUS[e.status] ?? '—'}</span>
        {data.summary.currentlyDown && <span className="pill wo-10">Currently down</span>}
        {data.summary.overduePmCount > 0 && (
          <span className="pill pm-30">{data.summary.overduePmCount} PM overdue</span>
        )}
        {warranty && (
          <span className={`pill ${inWarranty ? 'pm-40' : 'prio-30'}`}>
            {inWarranty ? 'In warranty' : 'Warranty expired'} {formatDate(e.warrantyExpiryDate)}
          </span>
        )}
      </div>

      <div className="detail-grid">
        <div className="card">
          <h2 className="section-h">Identification</h2>
          <dl className="detail">
            <Row label="Serial number" value={e.serialNumber} mono />
            <Row label="Manufacturer" value={e.manufacturer} />
            <Row label="Model" value={e.model} />
            <Row label="Location" value={e.locationName} strong />
            <Row label="Purchased" value={formatDate(e.purchaseDate)} />
            <Row label="Installed" value={formatDate(e.installationDate)} />
          </dl>
          {e.notes && <p className="muted" style={{ marginBottom: 0 }}>{e.notes}</p>}
        </div>

        <div className="card">
          <h2 className="section-h">Record</h2>
          <div className="tiles tiles-plain">
            <Stat label="PMs completed" value={data.summary.completedPmCount} />
            <Stat
              label="PMs open"
              value={data.summary.openPmCount}
              tone={data.summary.overduePmCount > 0 ? 'danger' : undefined}
            />
            <Stat label="Faults logged" value={data.summary.totalWorkOrderCount} />
            <Stat
              label="Recorded downtime"
              value={formatDuration(data.summary.totalDowntimeMinutes)}
            />
          </div>
        </div>
      </div>

      <Section title="Maintenance due" empty="Nothing due on this machine.">
        {data.openPm.map((t) => (
          <div key={t.id} className="hist-row">
            <div className="grow">
              <div>{t.checklistName}</div>
              <div className="muted hist-meta">
                Due {t.dueDate}
                {t.daysLate > 0 && <span className="late"> · {t.daysLate} days late</span>}
              </div>
            </div>
            <span className={`pill pm-${t.status}`}>{PM_STATUS[t.status]}</span>
          </div>
        ))}
      </Section>

      <Section title="Maintenance history" empty="No PM has been completed on this machine yet.">
        {data.completedPm.map((t) => (
          <div key={t.id} className="hist-row">
            <div className="grow">
              <div>
                {t.checklistName}
                {t.outOfRange > 0 && (
                  <span className="pill prio-30 hist-flag">
                    {t.outOfRange} out of spec
                  </span>
                )}
              </div>
              <div className="muted hist-meta">
                {t.status === 50
                  ? `Skipped — ${t.skipReason ?? 'no reason given'}`
                  : `${formatDateTime(t.completedAtUtc)} · ${t.completedBy ?? 'unknown'}`}
                {' · due '}{t.dueDate}
              </div>
            </div>

            {t.hasCertificate && (
              <button
                className="btn btn-quiet"
                onClick={() =>
                  api.download(
                    `/api/reports/pm/${t.id}/certificate.pdf`,
                    `PM-${e.assetTag}-${t.dueDate}.pdf`,
                  )
                }
              >
                Certificate
              </button>
            )}
          </div>
        ))}
      </Section>

      <Section title="Fault history" empty="No faults have been reported on this machine.">
        {data.workOrders.map((w) => (
          <div key={w.id} className="hist-row">
            <div className="grow">
              <div>
                <span className="mono">{w.number}</span>
                <span className={`pill prio-${w.priority} hist-flag`}>{PRIORITY[w.priority]}</span>
              </div>
              <div className="hist-fault">{w.faultDescription}</div>
              {w.resolutionNotes && (
                <div className="muted hist-meta">Fixed: {w.resolutionNotes}</div>
              )}
              <div className="muted hist-meta">
                Reported {formatDateTime(w.reportedAtUtc)}
                {w.resolvedAtUtc && ` · resolved ${formatDateTime(w.resolvedAtUtc)}`}
              </div>
            </div>

            <div className="stack" style={{ gap: '0.35rem', alignItems: 'flex-end' }}>
              <span className={`pill wo-${w.status}`}>{WO_STATUS[w.status]}</span>
              <button
                className="btn btn-quiet"
                onClick={() =>
                  api.download(`/api/reports/work-orders/${w.id}/report.pdf`, `${w.number}.pdf`)
                }
              >
                Report
              </button>
            </div>
          </div>
        ))}
      </Section>

      <button className="btn" onClick={() => navigate('/equipment')}>Back to register</button>
    </div>
  );
}

function Section({
  title,
  empty,
  children,
}: {
  title: string;
  empty: string;
  children: React.ReactNode;
}) {
  const isEmpty = Array.isArray(children) && children.length === 0;

  return (
    <div className="card">
      <h2 className="section-h">{title}</h2>
      {isEmpty ? <p className="muted" style={{ margin: 0 }}>{empty}</p> : children}
    </div>
  );
}

function Row({
  label,
  value,
  mono,
  strong,
}: {
  label: string;
  value: string | null;
  mono?: boolean;
  strong?: boolean;
}) {
  return (
    <>
      <dt>{label}</dt>
      <dd className={[mono ? 'mono' : '', strong ? 'strong' : ''].join(' ').trim()}>
        {value || <span className="muted">—</span>}
      </dd>
    </>
  );
}

function Stat({ label, value, tone }: { label: string; value: number | string; tone?: 'danger' }) {
  return (
    <div className={['tile', tone ? `tile-${tone}` : ''].join(' ').trim()}>
      <span className="tile-value">{value}</span>
      <span className="tile-label">{label}</span>
    </div>
  );
}

/** Day-first, as every date in an Indian hospital is written. */
function formatDate(iso: string | null): string | null {
  if (!iso) return null;
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return null;
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${pad(d.getDate())}/${pad(d.getMonth() + 1)}/${d.getFullYear()}`;
}

function formatDateTime(iso: string | null): string {
  if (!iso) return '—';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '—';
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${pad(d.getDate())}/${pad(d.getMonth() + 1)}/${d.getFullYear()} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

/** Hours and days: "4380 minutes" means nothing to a reader. */
function formatDuration(minutes: number): string {
  if (minutes === 0) return '—';
  if (minutes < 60) return `${minutes} min`;
  const days = Math.floor(minutes / 1440);
  const hours = Math.floor((minutes % 1440) / 60);
  return days > 0 ? `${days}d ${hours}h` : `${hours}h`;
}
