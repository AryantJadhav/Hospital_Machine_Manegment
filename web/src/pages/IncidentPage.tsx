import { useCallback, useEffect, useState } from 'react';
import type { FormEvent } from 'react';
import { Link, Navigate, useLocation, useParams } from 'react-router-dom';
import { api, ApiError } from '../api/client';
import { PERMISSIONS } from '../auth/context';
import { useAuth } from '../auth/useAuth';
import { HandoffNotice } from '../HandoffNotice';
import { useHandoff } from '../handoff';
import { DAMAGE_LEVELS, DAMAGE_LOOK, INCIDENT_STATUS_LABEL, INCIDENT_STATUS_LOOK } from '../incidentTypes';
import type { IncidentDetail } from '../incidentTypes';
import { usePageTitle } from '../pageTitle';
import { StatusPill } from '../StatusPill';
import { formatDate, formatDateTime } from '../time';
import { IncidentForm } from './IncidentForm';

/**
 * One incident on its own page: /incidents/:id. What happened, to which machine, how it was left, and, once
 * the biomedical team has looked into it, what caused it and what is being done about it.
 *
 * The department that reported it reads all of it. The biomedical team also corrects what was written up,
 * writes the findings over one visit or several, and closes it. A closed incident is final.
 */
export function IncidentPage() {
  const { id: idParam } = useParams();
  const id = Number(idParam);
  const valid = Number.isInteger(id) && id > 0;

  const { may } = useAuth();
  const canManage = may(PERMISSIONS.incidentsManage);
  // The machine's own page is for those who work on the equipment; a person from another department
  // sees its number and nothing opens.
  const canOpenMachine = may(PERMISSIONS.registerView);
  const location = useLocation();
  const [handoff, setHandoff] = useHandoff();

  const [incident, setIncident] = useState<IncidentDetail | null>(null);
  const [missing, setMissing] = useState(false);
  const [editing, setEditing] = useState(false);
  const [error, setError] = useState<string | null>(null);

  usePageTitle(incident ? incident.reference : undefined);

  const load = useCallback(async () => {
    try {
      setIncident(await api.get<IncidentDetail>(`/api/incidents/${id}`));
      setMissing(false);
    } catch (e) {
      if (e instanceof ApiError && e.status === 404) setMissing(true);
      else setError(e instanceof Error ? e.message : 'Could not open that incident.');
    }
  }, [id]);

  useEffect(() => {
    if (valid) void load();
  }, [valid, load]);

  if (!valid) return <Navigate to="/incidents" replace />;

  if (missing) {
    return (
      <div className="page stack">
        <p className="alert alert-error" role="alert">That incident does not exist.</p>
        <div><Link className="btn" to="/incidents">Back to Incidents</Link></div>
      </div>
    );
  }

  if (!incident) {
    return (
      <div className="page">
        {error ? <p className="alert alert-error" role="alert">{error}</p> : <p className="muted">Loading…</p>}
      </div>
    );
  }

  if (editing) {
    return (
      <div className="page">
        <IncidentForm
          editing={incident}
          onCancel={() => setEditing(false)}
          onSaved={async (_id, message) => {
            setEditing(false);
            setHandoff({ notice: message, recorded: null });
            await load();
          }}
        />
      </div>
    );
  }

  const closed = incident.status === 'Closed';
  const back = (location.state as { from?: string } | null)?.from ?? '/incidents';

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <p className="crumb"><Link to={back}>← Back to Incidents</Link></p>
          <h1 className="mono">{incident.reference}</h1>
          <p className="muted">
            {incident.typeLabel} · {formatDate(incident.occurredOn)}
            {incident.occurredAt && ` at ${incident.occurredAt}`}
          </p>
        </div>

        <div className="row">
          {/* A preview in a tab of its own first; printing or downloading is a choice made there. */}
          <a className="btn btn-primary" href={`/incidents/${incident.id}/report`} target="_blank" rel="noopener">
            View incident report
          </a>
          {canManage && !closed && <button className="btn" onClick={() => setEditing(true)}>Correct</button>}
        </div>
      </header>

      <HandoffNotice handoff={handoff} />
      {error && <p className="alert alert-error" role="alert">{error}</p>}

      <div className="row">
        <StatusPill look={INCIDENT_STATUS_LOOK[incident.status]}>{INCIDENT_STATUS_LABEL[incident.status]}</StatusPill>
        <StatusPill look={DAMAGE_LOOK[incident.damage]}>{incident.damageLabel}</StatusPill>
        {incident.takenOutOfUse && <StatusPill tone="warning">Taken out of use</StatusPill>}
      </div>

      <section className="card stack">
        <h2 className="section-h">Machine</h2>
        {incident.machine ? (
          <dl className="detail">
            <dt>Machine number</dt>
            <dd className="mono">
              {canOpenMachine ? (
                <Link to={`/equipment/${incident.machine.id}`} state={{ from: `/incidents/${incident.id}` }}>
                  {incident.machine.assetTag}
                </Link>
              ) : (
                incident.machine.assetTag
              )}
            </dd>
            <dt>Machine</dt>
            <dd>{incident.machine.machineName ?? <span className="muted">—</span>}</dd>
            <dt>Company and model</dt>
            <dd>{[incident.machine.manufacturer, incident.machine.model].filter(Boolean).join(' ') || <span className="muted">—</span>}</dd>
            <dt>Serial number</dt>
            <dd>{incident.machine.serialNumber ?? <span className="muted">—</span>}</dd>
            <dt>Place when reported</dt>
            <dd>{incident.locationName ?? <span className="muted">—</span>}</dd>
          </dl>
        ) : (
          <p className="muted" style={{ margin: 0 }}>The machine is not on the register you can see.</p>
        )}
      </section>

      <section className="card stack">
        <h2 className="section-h">What happened</h2>
        <dl className="detail">
          <dt>Kind</dt>
          <dd>{incident.typeLabel}</dd>
          <dt>Day and time</dt>
          <dd>
            {formatDate(incident.occurredOn)}
            {incident.occurredAt && ` at ${incident.occurredAt}`}
          </dd>
          <dt>Where exactly</dt>
          <dd>{incident.place ?? <span className="muted">—</span>}</dd>
          <dt>Staff handling it</dt>
          <dd>{incident.involvedPerson ?? <span className="muted">—</span>}</dd>
          <dt>Reported</dt>
          <dd>
            {formatDateTime(incident.reportedAtUtc)}
            {incident.reportedByName && ` by ${incident.reportedByName}`}
          </dd>
        </dl>
        <p style={{ margin: 0, whiteSpace: 'pre-wrap' }}>{incident.description}</p>
        {incident.immediateAction && (
          <p style={{ margin: 0, whiteSpace: 'pre-wrap' }}>
            <strong>Done straight away: </strong>
            {incident.immediateAction}
          </p>
        )}
      </section>

      {closed || !canManage ? (
        <section className="card stack">
          <h2 className="section-h">Review by the biomedical team</h2>
          {incident.findings || incident.correctiveAction ? (
            <>
              <p style={{ margin: 0, whiteSpace: 'pre-wrap' }}>
                <strong>What caused it: </strong>
                {incident.findings ?? '—'}
              </p>
              <p style={{ margin: 0, whiteSpace: 'pre-wrap' }}>
                <strong>What is being done so it does not happen again: </strong>
                {incident.correctiveAction ?? '—'}
              </p>
            </>
          ) : (
            <p className="muted" style={{ margin: 0 }}>Not reviewed yet. The biomedical team will look into it.</p>
          )}
          {closed && incident.closedAtUtc && (
            <p className="muted" style={{ margin: 0 }}>
              Closed {formatDateTime(incident.closedAtUtc)}
              {incident.closedByName && ` by ${incident.closedByName}`}.
            </p>
          )}
        </section>
      ) : (
        <ReviewForm
          incident={incident}
          onDone={async (message) => {
            setHandoff({ notice: message, recorded: null });
            await load();
          }}
          onError={setError}
        />
      )}
    </div>
  );
}

/**
 * The biomedical team's part: how the machine was really left, what caused it, and what is being done about
 * it. Saved as often as needed; closing needs the cause and cannot be undone.
 */
function ReviewForm({
  incident,
  onDone,
  onError,
}: {
  incident: IncidentDetail;
  onDone: (message: string) => void | Promise<void>;
  onError: (message: string | null) => void;
}) {
  const [damage, setDamage] = useState<number>(incident.damage);
  const [findings, setFindings] = useState(incident.findings ?? '');
  const [action, setAction] = useState(incident.correctiveAction ?? '');
  const [busy, setBusy] = useState<'save' | 'close' | null>(null);

  const body = () => ({ findings, correctiveAction: action, damage });

  async function save(e: FormEvent) {
    e.preventDefault();
    onError(null);
    setBusy('save');
    try {
      await api.put(`/api/incidents/${incident.id}/review`, body());
      await onDone(`Saved the review of ${incident.reference}.`);
    } catch (err) {
      onError(err instanceof Error ? err.message : 'Could not save the review.');
    } finally {
      setBusy(null);
    }
  }

  async function close() {
    if (!window.confirm(`Close ${incident.reference}? A closed incident can no longer be changed.`)) return;
    onError(null);
    setBusy('close');
    try {
      await api.post(`/api/incidents/${incident.id}/close`, body());
      await onDone(`Closed ${incident.reference}.`);
    } catch (err) {
      onError(err instanceof Error ? err.message : 'Could not close the incident.');
    } finally {
      setBusy(null);
    }
  }

  return (
    <form className="card stack" onSubmit={save}>
      <h2 className="section-h" style={{ margin: 0 }}>Review by the biomedical team</h2>
      <p className="muted" style={{ margin: 0 }}>
        Look at the machine, say how it was really left and what caused it. Save as you go; close it when the
        cause is written down.
      </p>

      <label className="field">
        <span>The machine was left</span>
        <select value={damage} onChange={(e) => setDamage(Number(e.target.value))}>
          {DAMAGE_LEVELS.map((d) => <option key={d.value} value={d.value}>{d.label}</option>)}
        </select>
      </label>

      <label className="field">
        <span>What caused it</span>
        <textarea
          rows={3}
          value={findings}
          maxLength={4000}
          onChange={(e) => setFindings(e.target.value)}
          placeholder="e.g. The trolley brake was not locked while the machine was moved."
        />
      </label>

      <label className="field">
        <span>What is being done so it does not happen again (optional)</span>
        <textarea
          rows={3}
          value={action}
          maxLength={4000}
          onChange={(e) => setAction(e.target.value)}
          placeholder="e.g. Brake check added to the shift round; staff shown the stand."
        />
      </label>

      <div className="row">
        <button className="btn" type="submit" disabled={busy !== null}>
          {busy === 'save' ? 'Saving…' : incident.status === 'Reported' ? 'Start the review' : 'Save the review'}
        </button>
        <button
          className="btn btn-primary"
          type="button"
          disabled={busy !== null || !findings.trim()}
          title={findings.trim() ? undefined : 'Say what caused it first'}
          onClick={() => void close()}
        >
          {busy === 'close' ? 'Closing…' : 'Close the incident'}
        </button>
      </div>
    </form>
  );
}
