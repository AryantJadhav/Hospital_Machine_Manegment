import { useCallback, useEffect, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { api, ApiError } from '../api/client';
import { useAuth } from '../auth/useAuth';
import { ROLES } from '../auth/context';
import { EquipmentForm } from './EquipmentForm';
import { formatDate, formatDateTime, todayAtHospital } from '../time';
import { useHandoff } from '../handoff';
import { HandoffNotice } from '../HandoffNotice';
import { StatusPill } from '../StatusPill';
import { EQUIPMENT_LOOK, PM_LOOK, PRIORITY_LOOK, WORK_ORDER_LOOK } from '../statusTones';

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
    // Returned by the history endpoint and simply not declared here until the
    // edit form needed it to prefill the type.
    equipmentTypeId: number;
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
    failedChecks: number;
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
  const canPrint = can(ROLES.admin);

  const canEdit = can(ROLES.admin);
  const canCondemn = can(ROLES.admin);

  const [data, setData] = useState<History | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [editing, setEditing] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);
  // What the PM or fault form we sent them to just did, said here, where the
  // new entry appears in the history below.
  const [handoff] = useHandoff();

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

  async function condemn() {
    // Typed, not clicked. Condemning ends this machine's PM programme: the
    // generator stops producing tasks for it from the next run. Reversible by
    // correcting the status afterwards, but a technician should not discover
    // it by finding the work gone.
    const typed = prompt(
      [
        `Condemn ${data?.equipment.assetTag}?`,
        '',
        'The machine stays on the register so its PM certificates and work-order',
        'history remain readable — nothing is deleted.',
        '',
        'But its preventive maintenance stops: no further PM tasks will be',
        'generated for it.',
        '',
        'Type CONDEMN to continue:',
      ].join('\n'),
    );
    if (typed !== 'CONDEMN') return;

    setActionError(null);
    try {
      await api.post(`/api/equipment/${id}/condemn`, {});
      await load();
    } catch (e) {
      setActionError(e instanceof Error ? e.message : 'Could not condemn this machine.');
    }
  }

  if (loading) return <div className="page"><p className="muted">Loading…</p></div>;
  if (error) return <div className="page"><p className="alert alert-error">{error}</p></div>;
  if (!data) return null;

  const e = data.equipment;
  // Calendar days compared as yyyy-mm-dd, which sort correctly as text. A
  // warranty runs to the end of its last day; comparing its midnight to the
  // current moment called it expired from the morning of that day.
  const warranty = e.warrantyExpiryDate;
  const inWarranty = warranty !== null && warranty >= todayAtHospital();

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
          {canEdit && !editing && (
            <button className="btn" onClick={() => setEditing(true)}>Edit</button>
          )}
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
          {/* With this machine already chosen, and back here afterwards. It used
              to open the general work-order list and leave the technician to
              press Report a fault and find the machine they were standing at. */}
          <Link className="btn" to={`/work-orders?report=${e.id}`} state={{ from: `/equipment/${e.id}` }}>
            Report a fault
          </Link>
          {/* Condemning is its own action, not a status in a dropdown. It ends
              the machine's PM programme - the generator stops producing tasks
              for it - and that is not something to do by mis-clicking a
              select. Admin and biomedical head only. */}
          {canCondemn && e.status !== 40 && e.status !== 50 && (
            <button className="btn btn-quiet" onClick={() => void condemn()}>
              Condemn…
            </button>
          )}
        </div>
      </header>

      <HandoffNotice handoff={handoff} />

      {actionError && <p className="alert alert-error" role="alert">{actionError}</p>}

      {editing && (
        <EquipmentForm
          editing={{
            id: e.id,
            assetTag: e.assetTag,
            serialNumber: e.serialNumber ?? null,
            equipmentTypeId: e.equipmentTypeId,
            locationId: e.locationId,
            manufacturer: e.manufacturer ?? null,
            model: e.model ?? null,
            status: e.status,
            purchaseDate: e.purchaseDate ?? null,
            installationDate: e.installationDate ?? null,
            warrantyExpiryDate: e.warrantyExpiryDate ?? null,
            notes: e.notes ?? null,
          }}
          onCancel={() => setEditing(false)}
          onSaved={async () => {
            setEditing(false);
            await load();
          }}
        />
      )}

      <div className="row">
        <StatusPill look={EQUIPMENT_LOOK[e.status]}>{EQUIPMENT_STATUS[e.status] ?? '—'}</StatusPill>
        {data.summary.currentlyDown && <StatusPill tone="danger">Currently down</StatusPill>}
        {data.summary.overduePmCount > 0 && (
          <StatusPill tone="danger">{data.summary.overduePmCount} PM overdue</StatusPill>
        )}
        {warranty && (
          // Out of warranty is a fact, not an alarm: neutral. In warranty is good news.
          <StatusPill tone={inWarranty ? 'success' : 'neutral'}>
            {inWarranty ? 'In warranty' : 'Warranty expired'} {formatDate(e.warrantyExpiryDate)}
          </StatusPill>
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
                Due {formatDate(t.dueDate)}
                {t.daysLate > 0 && <span className="late"> · {t.daysLate} days late</span>}
              </div>
            </div>
            <StatusPill look={PM_LOOK[t.status]}>{PM_STATUS[t.status]}</StatusPill>
            {/* The point of a machine's page: it says what is due, and now it lets
                the person standing at the machine do it. */}
            <Link className="btn" to={`/pm/${t.id}/do`} state={{ from: `/equipment/${e.id}` }}>
              Do PM
            </Link>
          </div>
        ))}
      </Section>

      <Section title="Maintenance history" empty="No PM has been completed on this machine yet.">
        {data.completedPm.map((t) => (
          <div key={t.id} className="hist-row">
            <div className="grow">
              <div>
                {t.checklistName}
                {t.failedChecks > 0 && (
                  <StatusPill tone="danger" className="hist-flag">
                    {t.failedChecks} failed
                  </StatusPill>
                )}
                {t.outOfRange > 0 && (
                  <StatusPill tone="warning" className="hist-flag">
                    {t.outOfRange} out of spec
                  </StatusPill>
                )}
              </div>
              <div className="muted hist-meta">
                {t.status === 50
                  ? `Skipped — ${t.skipReason ?? 'no reason given'}`
                  : `${formatDateTime(t.completedAtUtc)} · ${t.completedBy ?? 'unknown'}`}
                {' · due '}{formatDate(t.dueDate)}
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
                <StatusPill look={PRIORITY_LOOK[w.priority]} className="hist-flag">{PRIORITY[w.priority]}</StatusPill>
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
              <StatusPill look={WORK_ORDER_LOOK[w.status]}>{WO_STATUS[w.status]}</StatusPill>
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

/** Hours and days: "4380 minutes" means nothing to a reader. */
function formatDuration(minutes: number): string {
  if (minutes === 0) return '—';
  if (minutes < 60) return `${minutes} min`;
  const days = Math.floor(minutes / 1440);
  const hours = Math.floor((minutes % 1440) / 60);
  return days > 0 ? `${days}d ${hours}h` : `${hours}h`;
}
