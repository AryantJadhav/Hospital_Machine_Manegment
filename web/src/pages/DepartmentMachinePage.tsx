import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { api, ApiError } from '../api/client';
import { StatusPill } from '../StatusPill';
import { CRITICALITY_LABEL, CRITICALITY_LOOK, EQUIPMENT_LABEL, EQUIPMENT_LOOK, PRIORITY_LABEL, PRIORITY_LOOK, WORK_ORDER_LABEL, WORK_ORDER_LOOK } from '../statusTones';
import { formatAge, formatDate, formatDateTime } from '../time';

type Machine = {
  id: number;
  assetTag: string;
  serialNumber: string | null;
  equipmentTypeName: string;
  locationName: string;
  manufacturer: string | null;
  model: string | null;
  status: number;
  criticality: number | null;
  installationDate: string | null;
  warrantyExpiryDate: string | null;
};

type Request = {
  id: number;
  number: string;
  status: number;
  priority: number;
  faultDescription: string;
  reportedAtUtc: string;
};

/**
 * One machine as a person from its own department sees it: what it is, where it is and what state it
 * is in, and the service requests on it with a button to report a fault. Not its history, costs, PM
 * schedule or insurance: those are the biomedical department's.
 */
export function DepartmentMachinePage() {
  const { id } = useParams();
  const [machine, setMachine] = useState<Machine | null>(null);
  const [requests, setRequests] = useState<Request[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [missing, setMissing] = useState(false);

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const m = await api.get<Machine>(`/api/equipment/${id}`);
        const r = await api.get<{ items: Request[] }>(`/api/work-orders?equipmentId=${id}&openOnly=false&pageSize=50`);
        if (cancelled) return;
        setMachine(m);
        setRequests(r.items);
      } catch (e) {
        if (cancelled) return;
        if (e instanceof ApiError && e.status === 404) setMissing(true);
        else setError(e instanceof Error ? e.message : 'Could not open that machine.');
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [id]);

  if (missing) {
    return (
      <div className="page stack">
        <p className="alert alert-error" role="alert">That machine is not one of your departments&apos;.</p>
        <div><Link className="btn" to="/equipment">Back to equipment</Link></div>
      </div>
    );
  }

  if (!machine) {
    return (
      <div className="page">
        {error ? <p className="alert alert-error" role="alert">{error}</p> : <p className="muted">Loading…</p>}
      </div>
    );
  }

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <p className="crumb"><Link to="/equipment">← Back to equipment</Link></p>
          <h1 className="mono">{machine.assetTag}</h1>
          <p className="muted">{machine.equipmentTypeName} · {machine.locationName}</p>
        </div>
        <Link className="btn btn-primary" to={`/work-orders?report=${machine.id}`}>Report a fault</Link>
      </header>

      <div className="row">
        <StatusPill look={EQUIPMENT_LOOK[machine.status]}>{EQUIPMENT_LABEL[machine.status]}</StatusPill>
        {machine.criticality !== null && (
          <StatusPill look={CRITICALITY_LOOK[machine.criticality]}>{CRITICALITY_LABEL[machine.criticality]}</StatusPill>
        )}
      </div>

      <section className="card stack">
        <h2 className="section-h">The machine</h2>
        <dl className="detail">
          <dt>Machine</dt>
          <dd>{machine.equipmentTypeName}</dd>
          <dt>Company</dt>
          <dd>{machine.manufacturer ?? <span className="muted">—</span>}</dd>
          <dt>Model</dt>
          <dd>{machine.model ?? <span className="muted">—</span>}</dd>
          <dt>Serial number</dt>
          <dd>{machine.serialNumber ?? <span className="muted">—</span>}</dd>
          <dt>Where</dt>
          <dd>{machine.locationName}</dd>
          <dt>Installed</dt>
          <dd>{formatDate(machine.installationDate) ?? <span className="muted">—</span>}</dd>
          <dt>Warranty until</dt>
          <dd>{formatDate(machine.warrantyExpiryDate) ?? <span className="muted">—</span>}</dd>
        </dl>
      </section>

      <section className="card table-wrap">
        <table className="table">
          <caption className="table-caption">Service requests on this machine ({requests.length})</caption>
          <thead>
            <tr>
              <th>Request</th>
              <th>What was wrong</th>
              <th>Status</th>
              <th>Reported</th>
            </tr>
          </thead>
          <tbody>
            {requests.length === 0 && (
              <tr><td colSpan={4} className="empty">No service request has been raised on this machine.</td></tr>
            )}
            {requests.map((r) => (
              <tr key={r.id}>
                <td><Link to={`/work-orders/${r.id}`} className="mono">{r.number}</Link></td>
                <td>
                  {r.faultDescription}
                  <div><StatusPill look={PRIORITY_LOOK[r.priority]}>{PRIORITY_LABEL[r.priority]}</StatusPill></div>
                </td>
                <td><StatusPill look={WORK_ORDER_LOOK[r.status]}>{WORK_ORDER_LABEL[r.status]}</StatusPill></td>
                <td title={formatDateTime(r.reportedAtUtc)}>{formatAge(r.reportedAtUtc)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>
    </div>
  );
}
