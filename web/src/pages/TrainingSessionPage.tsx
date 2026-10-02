import { useCallback, useEffect, useState } from 'react';
import { Link, Navigate, useNavigate, useParams } from 'react-router-dom';
import { api, ApiError } from '../api/client';
import { useAuth } from '../auth/useAuth';
import { PERMISSIONS } from '../auth/context';
import { HandoffNotice } from '../HandoffNotice';
import { useHandoff } from '../handoff';
import { StatusPill } from '../StatusPill';
import { usePageTitle } from '../pageTitle';
import { formatDate, formatDateTime } from '../time';
import { formatMinutes, TRAINER_TYPE_LABEL } from '../trainingTypes';
import type { TrainingDetail } from '../trainingTypes';
import { TrainingForm } from './TrainingForm';

/**
 * One training session on its own page: /training/:id. What it was about, when, who ran it, and
 * the people who attended. An Administrator can change or remove it.
 */
export function TrainingSessionPage() {
  const { id: idParam } = useParams();
  const id = Number(idParam);
  const valid = Number.isInteger(id) && id > 0;

  const { may } = useAuth();
  const canEdit = may(PERMISSIONS.trainingEdit);
  const navigate = useNavigate();
  const [handoff, setHandoff] = useHandoff();

  const [session, setSession] = useState<TrainingDetail | null>(null);
  const [missing, setMissing] = useState(false);
  const [editing, setEditing] = useState(false);
  const [error, setError] = useState<string | null>(null);

  usePageTitle(session ? session.title : undefined);

  const load = useCallback(async () => {
    try {
      setSession(await api.get<TrainingDetail>(`/api/training/${id}`));
      setMissing(false);
    } catch (e) {
      if (e instanceof ApiError && e.status === 404) setMissing(true);
      else setError(e instanceof Error ? e.message : 'Could not open that session.');
    }
  }, [id]);

  useEffect(() => {
    if (valid) void load();
  }, [valid, load]);

  async function remove() {
    if (!session) return;
    if (!window.confirm(`Remove "${session.title}" and its list of attendees? The removal is recorded in the audit log.`)) {
      return;
    }
    setError(null);
    try {
      await api.del(`/api/training/${session.id}`);
      navigate('/training', { replace: true });
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not remove the session.');
    }
  }

  if (!valid) return <Navigate to="/training" replace />;

  if (missing) {
    return (
      <div className="page stack">
        <p className="alert alert-error" role="alert">That training session does not exist.</p>
        <div><Link className="btn" to="/training">Back to Training</Link></div>
      </div>
    );
  }

  if (!session) {
    return (
      <div className="page">
        {error ? <p className="alert alert-error" role="alert">{error}</p> : <p className="muted">Loading…</p>}
      </div>
    );
  }

  if (editing) {
    return (
      <div className="page">
        <TrainingForm
          editing={session}
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

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <p className="crumb"><Link to="/training">← Back to Training</Link></p>
          <h1>{session.title}</h1>
          <p className="muted">
            {session.reference} · {formatDate(session.sessionDate)}
            {session.venue && ` · ${session.venue}`}
          </p>
        </div>

        <div className="row">
          {/* A preview in a tab of its own first; downloading is a choice made there. */}
          <a className="btn btn-primary" href={`/training/${session.id}/report`} target="_blank" rel="noopener">
            View training report
          </a>
          {canEdit && <button className="btn" onClick={() => setEditing(true)}>Edit</button>}
          {canEdit && <button className="btn btn-quiet" onClick={() => void remove()}>Remove</button>}
        </div>
      </header>

      <HandoffNotice handoff={handoff} />
      {error && <p className="alert alert-error" role="alert">{error}</p>}

      <div className="row">
        {session.isPlanned && <StatusPill tone="info">Planned</StatusPill>}
      </div>

      <section className="card stack">
        <h2 className="section-h">Machine</h2>
        {session.machine ? (
          <dl className="detail">
            <dt>Machine number</dt>
            <dd className="mono">
              <Link to={`/equipment/${session.machine.id}`}>{session.machine.assetTag}</Link>
            </dd>
            <dt>Machine</dt>
            <dd>{session.machine.machineName ?? <span className="muted">—</span>}</dd>
            <dt>Company</dt>
            <dd>{session.machine.manufacturer ?? <span className="muted">—</span>}</dd>
            <dt>Model</dt>
            <dd>{session.machine.model ?? <span className="muted">—</span>}</dd>
            <dt>Serial number</dt>
            <dd>{session.machine.serialNumber ?? <span className="muted">—</span>}</dd>
            <dt>Location</dt>
            <dd>{session.machine.locationName ?? <span className="muted">—</span>}</dd>
          </dl>
        ) : (
          <p className="muted" style={{ margin: 0 }}>This session was not about one machine.</p>
        )}
      </section>

      <section className="card stack">
        <h2 className="section-h">Details</h2>
        <dl className="detail">
          <dt>Date</dt>
          <dd>{formatDate(session.sessionDate)}</dd>
          <dt>Trainer from</dt>
          <dd>{session.trainerType ? TRAINER_TYPE_LABEL[session.trainerType] : <span className="muted">—</span>}</dd>
          <dt>Trainer</dt>
          <dd>{session.trainer ?? <span className="muted">—</span>}</dd>
          <dt>Where</dt>
          <dd>{session.venue ?? <span className="muted">—</span>}</dd>
          <dt>Length</dt>
          <dd>{formatMinutes(session.durationMinutes) ?? <span className="muted">—</span>}</dd>
          <dt>Recorded</dt>
          <dd>
            {formatDateTime(session.createdAtUtc)}
            {session.createdByName && ` by ${session.createdByName}`}
          </dd>
        </dl>
        {session.notes && <p style={{ margin: 0 }}>{session.notes}</p>}
      </section>

      <section className="card table-wrap">
        <table className="table">
          <caption className="table-caption">
            {session.isPlanned ? 'Expected' : 'Attended'} ({session.attendees.length})
          </caption>
          <thead>
            <tr>
              <th>Name</th>
              <th>Job</th>
              <th>Account</th>
            </tr>
          </thead>
          <tbody>
            {session.attendees.length === 0 && (
              <tr>
                <td colSpan={3} className="empty">
                  {session.isPlanned ? 'No one has been listed yet.' : 'No one was listed for this session.'}
                </td>
              </tr>
            )}
            {session.attendees.map((a) => (
              <tr key={a.id}>
                <td>{a.name}</td>
                <td>{a.designation ?? <span className="muted">—</span>}</td>
                <td>{a.userId !== null ? 'Has an account' : <span className="muted">—</span>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>
    </div>
  );
}
