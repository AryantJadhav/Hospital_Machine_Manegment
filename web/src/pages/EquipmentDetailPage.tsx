import { useCallback, useEffect, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { api, ApiError } from '../api/client';
import { useAuth } from '../auth/useAuth';
import { ROLES } from '../auth/context';
import { EquipmentForm } from './EquipmentForm';
import { AddPmDatesForm } from './AddPmDatesForm';
import { MoveMachineForm } from './MoveMachineForm';
import { RenewInsuranceForm } from './RenewInsuranceForm';
import { formatDate, formatDateTime, todayAtHospital } from '../time';
import { formatRupees } from '../money';
import { useHandoff } from '../handoff';
import { HandoffNotice } from '../HandoffNotice';
import { StatusPill } from '../StatusPill';
import { Tile } from '../Tile';
import {
  CONTRACT_LABEL,
  CRITICALITY_LABEL,
  CRITICALITY_LOOK,
  EQUIPMENT_LABEL,
  EQUIPMENT_LOOK,
  PM_LABEL,
  PM_LOOK,
  PRIORITY_LABEL,
  PRIORITY_LOOK,
  WORK_ORDER_LABEL,
  WORK_ORDER_LOOK,
} from '../statusTones';
import { formatHours } from '../hours';

type History = {
  equipment: {
    id: number;
    assetTag: string;
    serialNumber: string | null;
    manufacturer: string | null;
    model: string | null;
    status: number;
    criticality: number | null;
    purchaseDate: string | null;
    purchaseCost: number | null;
    installationDate: string | null;
    warrantyExpiryDate: string | null;
    isInsured: boolean;
    insuranceProvider: string | null;
    insurancePolicyNumber: string | null;
    insuranceExpiryDate: string | null;
    insuranceCost: number | null;
    maintenanceContractType: number | null;
    maintenanceVendor: string | null;
    maintenanceContractNumber: string | null;
    maintenanceStartDate: string | null;
    maintenanceEndDate: string | null;
    maintenanceCost: number | null;
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
    // 10 our own team, 20 the maintenance contract vendor, whose report is the record.
    performedBy: number;
    vendorName: string | null;
    reportFiles: number;
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
    downtimeHours: number | null;
    stillDown: boolean;
  }[];
  summary: {
    openPmCount: number;
    overduePmCount: number;
    completedPmCount: number;
    openWorkOrderCount: number;
    totalWorkOrderCount: number;
    totalDowntimeHours: number;
    downtimeHoursLast30Days: number;
    uptimeHoursLast30Days: number;
    availabilityPercentLast30Days: number | null;
    currentlyDown: boolean;
  };
};

type Move = {
  id: number;
  movedAtUtc: string;
  reason: string | null;
  from: string | null;
  to: string | null;
  movedBy: string | null;
};

/** What the machine has cost over its whole life, as the Cost report works it out. */
type Spend = {
  spend: {
    purchaseCost: number | null;
    insuranceCost: number | null;
    contractCost: number | null;
    partsCost: number;
    total: number;
    policies: { provider: string; policyNumber: string | null; expiryDate: string; cost: number | null; isCurrent: boolean }[];
    parts: { partNumber: string; name: string; quantity: number; cost: number; costMissing: boolean }[];
  };
  partsWithoutCost: number;
};

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
  const [moving, setMoving] = useState(false);
  const [addingDates, setAddingDates] = useState(false);
  const [moves, setMoves] = useState<Move[]>([]);
  const [spend, setSpend] = useState<Spend | null>(null);
  const [renewing, setRenewing] = useState(false);
  const [movedNote, setMovedNote] = useState<string | null>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  // What the PM or fault form we sent them to just did, said here, where the
  // new entry appears in the history below.
  const [handoff] = useHandoff();

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      setData(await api.get<History>(`/api/equipment/${id}/history`));
      setMoves(await api.get<Move[]>(`/api/equipment/${id}/moves`));

      // Its own try: the rest of the page is still worth showing if the costs cannot be worked out.
      try {
        setSpend(await api.get<Spend>(`/api/equipment/${id}/spend`));
      } catch {
        setSpend(null);
      }
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
  if (error) return <div className="page"><p className="alert alert-error" role="alert">{error}</p></div>;
  if (!data) return null;

  const e = data.equipment;
  // Calendar days compared as yyyy-mm-dd, which sort correctly as text. A
  // warranty runs to the end of its last day; comparing its midnight to the
  // current moment called it expired from the morning of that day.
  const warranty = e.warrantyExpiryDate;
  const inWarranty = warranty !== null && warranty >= todayAtHospital();
  // Same reading as the warranty: the policy covers its last day in full.
  const insuredUntil = e.isInsured ? e.insuranceExpiryDate : null;
  const insuranceCurrent = insuredUntil !== null && insuredUntil >= todayAtHospital();
  // A contract may not have started yet, may be running, or may have lapsed.
  const contract = e.maintenanceContractType != null ? CONTRACT_LABEL[e.maintenanceContractType] ?? 'Contract' : null;
  const contractEnds = e.maintenanceEndDate;
  const contractNotStarted = e.maintenanceStartDate != null && e.maintenanceStartDate > todayAtHospital();
  const contractCurrent = contractEnds !== null && contractEnds >= todayAtHospital() && !contractNotStarted;

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
          {/* A renewal is a second policy that was paid for, so the old one is kept; Edit is for
              correcting a mistake and overwrites. */}
          {canEdit && e.isInsured && !renewing && (
            <button className="btn" onClick={() => { setRenewing(true); setMovedNote(null); }}>Renew insurance</button>
          )}
          {e.status !== 40 && e.status !== 50 && !moving && (
            <button className="btn" onClick={() => { setMoving(true); setMovedNote(null); }}>Move</button>
          )}
          {canEdit && e.status !== 40 && e.status !== 50 && !addingDates && (
            <button className="btn" onClick={() => { setAddingDates(true); setMovedNote(null); }}>Add PM dates</button>
          )}
          {canPrint && (
            <button
              className="btn"
              onClick={() =>
                void api
                  .downloadPost('/api/labels/sheet', { equipmentIds: [e.id] }, `${e.assetTag}.pdf`)
                  .catch((err: unknown) => setActionError(err instanceof Error ? err.message : 'Could not make the label.'))
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

      {movedNote && <p className="alert alert-ok" role="status">{movedNote}</p>}

      {addingDates && (
        <AddPmDatesForm
          equipmentId={e.id}
          hasContract={e.maintenanceContractType != null}
          onCancel={() => setAddingDates(false)}
          onSaved={async (message) => {
            setAddingDates(false);
            setMovedNote(message);
            await load();
          }}
        />
      )}

      {renewing && (
        <RenewInsuranceForm
          equipmentId={e.id}
          currentProvider={e.insuranceProvider}
          currentExpiry={e.insuranceExpiryDate}
          onCancel={() => setRenewing(false)}
          onRenewed={async (message) => {
            setRenewing(false);
            setMovedNote(message);
            await load();
          }}
        />
      )}

      {moving && (
        <MoveMachineForm
          equipmentId={e.id}
          currentLocationId={e.locationId}
          onCancel={() => setMoving(false)}
          onMoved={async (message) => {
            setMoving(false);
            setMovedNote(message);
            await load();
          }}
        />
      )}

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
            criticality: e.criticality ?? null,
            purchaseDate: e.purchaseDate ?? null,
            purchaseCost: e.purchaseCost ?? null,
            installationDate: e.installationDate ?? null,
            warrantyExpiryDate: e.warrantyExpiryDate ?? null,
            isInsured: e.isInsured,
            insuranceProvider: e.insuranceProvider ?? null,
            insurancePolicyNumber: e.insurancePolicyNumber ?? null,
            insuranceExpiryDate: e.insuranceExpiryDate ?? null,
            insuranceCost: e.insuranceCost ?? null,
            maintenanceContractType: e.maintenanceContractType ?? null,
            maintenanceVendor: e.maintenanceVendor ?? null,
            maintenanceContractNumber: e.maintenanceContractNumber ?? null,
            maintenanceStartDate: e.maintenanceStartDate ?? null,
            maintenanceEndDate: e.maintenanceEndDate ?? null,
            maintenanceCost: e.maintenanceCost ?? null,
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
        <StatusPill look={EQUIPMENT_LOOK[e.status]}>{EQUIPMENT_LABEL[e.status] ?? '—'}</StatusPill>
        {e.criticality != null && (
          <StatusPill look={CRITICALITY_LOOK[e.criticality]}>{CRITICALITY_LABEL[e.criticality] ?? '—'}</StatusPill>
        )}
        {insuredUntil && (
          // A lapsed policy is something to renew, so it is a warning; a current one is good news.
          <StatusPill tone={insuranceCurrent ? 'success' : 'warning'}>
            {insuranceCurrent ? 'Insured until' : 'Insurance expired'} {formatDate(insuredUntil)}
          </StatusPill>
        )}
        {contract && contractEnds && (
          // Lapsed is something to renew, so a warning. Not started yet is neither.
          <StatusPill tone={contractNotStarted ? 'neutral' : contractCurrent ? 'success' : 'warning'}>
            {contractNotStarted
              ? `${contract} starts ${formatDate(e.maintenanceStartDate)}`
              : contractCurrent
                ? `${contract} until ${formatDate(contractEnds)}`
                : `${contract} expired ${formatDate(contractEnds)}`}
          </StatusPill>
        )}
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
            <Row label="Cost of the machine" value={e.purchaseCost == null ? null : formatRupees(e.purchaseCost)} />
            <Row label="Installed" value={formatDate(e.installationDate)} />
            {e.isInsured && (
              <>
                <Row label="Insurer" value={e.insuranceProvider} />
                <Row label="Policy number" value={e.insurancePolicyNumber} mono />
                <Row label="Insurance expires" value={formatDate(e.insuranceExpiryDate)} />
                <Row label="Cost of the insurance" value={e.insuranceCost == null ? null : formatRupees(e.insuranceCost)} />
              </>
            )}
            {contract && (
              <>
                <Row label="Maintenance contract" value={contract} />
                <Row label="Contract vendor" value={e.maintenanceVendor} />
                <Row label="Contract number" value={e.maintenanceContractNumber} mono />
                <Row label="Contract period" value={`${formatDate(e.maintenanceStartDate)} to ${formatDate(e.maintenanceEndDate)}`} />
                <Row label="Cost of the contract" value={e.maintenanceCost == null ? null : formatRupees(e.maintenanceCost)} />
              </>
            )}
          </dl>
          {e.notes && <p className="muted" style={{ marginBottom: 0 }}>{e.notes}</p>}
        </div>

        <div className="card">
          <h2 className="section-h">Record</h2>
          <div className="tiles tiles-plain">
            <Tile label="PMs completed" value={data.summary.completedPmCount} />
            <Tile
              label="PMs open"
              value={data.summary.openPmCount}
              tone={data.summary.overduePmCount > 0 ? 'danger' : undefined}
            />
            <Tile label="Faults logged" value={data.summary.totalWorkOrderCount} />
            <Tile
              label={data.summary.currentlyDown ? 'Down now · total downtime' : 'Total downtime'}
              value={formatHours(data.summary.totalDowntimeHours)}
              tone={data.summary.currentlyDown ? 'danger' : undefined}
            />
            <Tile label="Uptime, last 30 days" value={formatHours(data.summary.uptimeHoursLast30Days)} />
            <Tile label="Downtime, last 30 days" value={formatHours(data.summary.downtimeHoursLast30Days)} />
            <Tile
              label="Availability, last 30 days"
              value={data.summary.availabilityPercentLast30Days === null ? '—' : `${data.summary.availabilityPercentLast30Days}%`}
            />
          </div>
        </div>
      </div>

      {spend && (
        <div className="card stack">
          <h2 className="section-h" style={{ margin: 0 }}>What it has cost</h2>

          {spend.spend.total <= 0 && spend.spend.policies.length === 0 && spend.spend.parts.length === 0 ? (
            <p className="muted" style={{ margin: 0 }}>
              No cost is recorded for this machine yet. Add its cost, insurance or maintenance contract with
              Edit, or record a spare part on one of its work orders.
            </p>
          ) : (
            <>
              <div className="tiles tiles-plain">
                <Tile label="Total" value={formatRupees(spend.spend.total)} />
                <Tile label="Purchase" value={formatRupees(spend.spend.purchaseCost)} />
                <Tile
                  label="Insurance"
                  value={formatRupees(spend.spend.insuranceCost)}
                  hint={spend.spend.policies.length > 1 ? `${spend.spend.policies.length} policies` : undefined}
                />
                <Tile label="Maintenance contract" value={formatRupees(spend.spend.contractCost)} />
                <Tile label="Spare parts used" value={formatRupees(spend.spend.partsCost)} />
              </div>

              {spend.spend.policies.length > 0 && (
                <div>
                  <div className="muted" style={{ fontSize: '0.8rem', marginBottom: '0.35rem' }}>Insurance policies</div>
                  {spend.spend.policies.map((p) => (
                    <div key={`${p.provider}-${p.expiryDate}`} className="hist-row">
                      <div className="grow">
                        <div>
                          {p.provider}
                          {p.isCurrent && <StatusPill tone="success" className="hist-flag">Current</StatusPill>}
                        </div>
                        <div className="muted hist-meta">
                          {p.policyNumber ? `${p.policyNumber} · ` : ''}covered until {formatDate(p.expiryDate)}
                        </div>
                      </div>
                      <strong>{formatRupees(p.cost)}</strong>
                    </div>
                  ))}
                </div>
              )}

              {spend.spend.parts.length > 0 && (
                <div>
                  <div className="muted" style={{ fontSize: '0.8rem', marginBottom: '0.35rem' }}>Spare parts used</div>
                  {spend.spend.parts.map((p) => (
                    <div key={p.partNumber} className="hist-row">
                      <div className="grow">
                        <div>
                          <span className="mono">{p.quantity}× {p.partNumber}</span> — {p.name}
                          {p.costMissing && (
                            <StatusPill tone="warning" className="hist-flag">Some cost not recorded</StatusPill>
                          )}
                        </div>
                      </div>
                      <strong>{formatRupees(p.cost)}</strong>
                    </div>
                  ))}
                </div>
              )}
            </>
          )}
        </div>
      )}

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
            <StatusPill look={PM_LOOK[t.status]}>{PM_LABEL[t.status]}</StatusPill>
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
                {t.performedBy === 20 && (
                  <StatusPill tone="info" className="hist-flag">Vendor</StatusPill>
                )}
                {t.performedBy === 20 && t.reportFiles === 0 && (
                  <StatusPill tone="warning" className="hist-flag">No report yet</StatusPill>
                )}
              </div>
              <div className="muted hist-meta">
                {t.status === 50
                  ? `Skipped — ${t.skipReason ?? 'no reason given'}`
                  : t.performedBy === 20
                    ? `${formatDateTime(t.completedAtUtc)} · done by ${t.vendorName ?? 'the vendor'}, recorded by ${t.completedBy ?? 'unknown'}`
                    : `${formatDateTime(t.completedAtUtc)} · ${t.completedBy ?? 'unknown'}`}
                {' · due '}{formatDate(t.dueDate)}
              </div>
            </div>

            {t.status === 40 && !t.hasCertificate && (
              // Nothing to certify: who did it and the report are the record.
              <Link className="btn btn-quiet" to={`/pm/${t.id}/do`} state={{ from: `/equipment/${e.id}` }}>
                Report{t.reportFiles > 0 ? ` (${t.reportFiles})` : ''}
              </Link>
            )}

            {t.hasCertificate && (
              <button
                className="btn btn-quiet"
                onClick={() =>
                  void api
                    .download(`/api/reports/pm/${t.id}/certificate.pdf`, `PM-${e.assetTag}-${t.dueDate}.pdf`)
                    .catch((err: unknown) => setActionError(err instanceof Error ? err.message : 'Could not make the certificate.'))
                }
              >
                Certificate
              </button>
            )}
          </div>
        ))}
      </Section>

      <Section title="Where it has been" empty="It has not been moved since it was put on the register.">
        {moves.map((m) => (
          <div key={m.id} className="hist-row">
            <div className="grow">
              <div>{m.from ?? 'Not placed'} → <strong>{m.to ?? '—'}</strong></div>
              <div className="muted hist-meta">
                {formatDateTime(m.movedAtUtc)} · {m.movedBy ?? 'unknown'}
                {m.reason && ` · ${m.reason}`}
              </div>
            </div>
          </div>
        ))}
      </Section>

      <Section title="Fault history" empty="No faults have been reported on this machine.">
        {data.workOrders.map((w) => (
          <div key={w.id} className="hist-row">
            <div className="grow">
              <div>
                <Link className="mono" to={`/work-orders/${w.id}`} state={{ from: `/equipment/${e.id}` }}>{w.number}</Link>
                <StatusPill look={PRIORITY_LOOK[w.priority]} className="hist-flag">{PRIORITY_LABEL[w.priority]}</StatusPill>
              </div>
              <div className="hist-fault">{w.faultDescription}</div>
              {w.resolutionNotes && (
                <div className="muted hist-meta">Fixed: {w.resolutionNotes}</div>
              )}
              <div className="muted hist-meta">
                Reported {formatDateTime(w.reportedAtUtc)}
                {w.resolvedAtUtc && ` · resolved ${formatDateTime(w.resolvedAtUtc)}`}
              </div>
              {w.downtimeHours !== null && (
                <div className="muted hist-meta">
                  {w.stillDown ? `Down for ${formatHours(w.downtimeHours)} so far` : `Was down ${formatHours(w.downtimeHours)}`}
                </div>
              )}
            </div>

            <div className="stack" style={{ gap: '0.35rem', alignItems: 'flex-end' }}>
              <StatusPill look={WORK_ORDER_LOOK[w.status]}>{WORK_ORDER_LABEL[w.status]}</StatusPill>
              <button
                className="btn btn-quiet"
                onClick={() =>
                  void api
                    .download(`/api/reports/work-orders/${w.id}/report.pdf`, `${w.number}.pdf`)
                    .catch((err: unknown) => setActionError(err instanceof Error ? err.message : 'Could not make the service report.'))
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
