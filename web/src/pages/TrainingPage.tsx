import { useCallback, useEffect, useState } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import { api } from '../api/client';
import { useAuth } from '../auth/useAuth';
import { ROLES } from '../auth/context';
import { StatusPill } from '../StatusPill';
import { formatDate } from '../time';
import { describeTrainer, formatMinutes } from '../trainingTypes';
import type { TrainingPerson, TrainingRow } from '../trainingTypes';
import { TrainingForm } from './TrainingForm';

type Paged<T> = { items: T[]; total: number; page: number; pageSize: number };

const PAGE_SIZE = 25;

/**
 * Training: the sessions the department has held or planned, and who has been trained.
 *
 * Everyone reads it - an engineer finding out who was trained on a machine before leaving it with
 * them is the point of keeping it. Only an Administrator adds or changes a session.
 */
export function TrainingPage() {
  const { can } = useAuth();
  const canEdit = can(ROLES.admin);
  const navigate = useNavigate();

  const [params, setParams] = useSearchParams();
  const view = params.get('view') === 'people' ? 'people' : 'sessions';
  const q = params.get('q') ?? '';
  const from = params.get('from') ?? '';
  const to = params.get('to') ?? '';
  const page = Math.max(1, Number(params.get('page')) || 1);

  const [search, setSearch] = useState(q);
  const [sessions, setSessions] = useState<Paged<TrainingRow> | null>(null);
  const [people, setPeople] = useState<TrainingPerson[] | null>(null);
  const [adding, setAdding] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // One place to change a filter, so changing any of them goes back to the first page.
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

  useEffect(() => {
    let cancelled = false;
    (async () => {
      setError(null);
      try {
        if (view === 'people') {
          const query = new URLSearchParams();
          if (q) query.set('q', q);
          const list = await api.get<TrainingPerson[]>(`/api/training/people?${query}`);
          if (!cancelled) setPeople(list);
        } else {
          const query = new URLSearchParams({ page: String(page), pageSize: String(PAGE_SIZE) });
          if (q) query.set('q', q);
          if (from) query.set('from', from);
          if (to) query.set('to', to);
          const list = await api.get<Paged<TrainingRow>>(`/api/training?${query}`);
          if (!cancelled) setSessions(list);
        }
      } catch (e) {
        if (!cancelled) setError(e instanceof Error ? e.message : 'Could not load the training register.');
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [view, q, from, to, page]);

  const totalPages = sessions ? Math.max(1, Math.ceil(sessions.total / sessions.pageSize)) : 1;

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <h1>Training</h1>
          <p className="muted">
            Training sessions the department has held or planned, and who attended.
            {view === 'sessions' && sessions && ` ${sessions.total.toLocaleString('en-IN')} ${sessions.total === 1 ? 'session' : 'sessions'}.`}
            {view === 'people' && people && ` ${people.length.toLocaleString('en-IN')} ${people.length === 1 ? 'person' : 'people'}.`}
          </p>
        </div>
        {canEdit && !adding && (
          <button className="btn btn-primary" onClick={() => setAdding(true)}>Add a session</button>
        )}
      </header>

      {error && <p className="alert alert-error" role="alert">{error}</p>}

      {adding && (
        <TrainingForm
          onCancel={() => setAdding(false)}
          onSaved={(id, message) => navigate(`/training/${id}`, { state: { handoff: { notice: message, recorded: null } } })}
        />
      )}

      <div className="row" role="tablist" aria-label="Training">
        <button
          role="tab"
          aria-selected={view === 'sessions'}
          className={view === 'sessions' ? 'btn btn-primary' : 'btn'}
          onClick={() => setParam('view', '')}
        >
          Sessions
        </button>
        <button
          role="tab"
          aria-selected={view === 'people'}
          className={view === 'people' ? 'btn btn-primary' : 'btn'}
          onClick={() => setParam('view', 'people')}
        >
          People
        </button>
      </div>

      <div className="filters card">
        <input
          type="search"
          className="grow"
          aria-label="Search training"
          placeholder={
            view === 'people' ? 'Find a person' : 'Find a session: trainer, place or a person who attended'
          }
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />

        {view === 'sessions' && (
          <>
            <label className="field">
              <span>From</span>
              <input type="date" aria-label="From" value={from} onChange={(e) => setParam('from', e.target.value)} />
            </label>
            <label className="field">
              <span>To</span>
              <input type="date" aria-label="To" value={to} onChange={(e) => setParam('to', e.target.value)} />
            </label>
          </>
        )}
      </div>

      {view === 'sessions' && <SessionsTable data={sessions} filtered={Boolean(q || from || to)} />}
      {view === 'people' && <PeopleTable people={people} filtered={Boolean(q)} />}

      {view === 'sessions' && sessions && sessions.total > sessions.pageSize && (
        <div className="pager">
          <button className="btn" disabled={page <= 1} onClick={() => setParam('page', String(page - 1))}>Previous</button>
          <span className="muted">Page {page} of {totalPages}</span>
          <button className="btn" disabled={page >= totalPages} onClick={() => setParam('page', String(page + 1))}>Next</button>
        </div>
      )}
    </div>
  );
}

function SessionsTable({ data, filtered }: { data: Paged<TrainingRow> | null; filtered: boolean }) {
  return (
    <div className="card table-wrap">
      <table className="table">
        <thead>
          <tr>
            <th>Date</th>
            <th>Session</th>
            <th>Machine</th>
            <th>Trainer</th>
            <th>Attended</th>
          </tr>
        </thead>
        <tbody>
          {!data && <tr><td colSpan={5} className="empty">Loading…</td></tr>}
          {data && data.items.length === 0 && (
            <tr>
              <td colSpan={5} className="empty">
                {filtered ? 'No session matches that.' : 'No training has been recorded yet.'}
              </td>
            </tr>
          )}
          {data?.items.map((s) => (
            <tr key={s.id}>
              <td>
                {formatDate(s.sessionDate)}
                {s.isPlanned && (
                  <div><StatusPill tone="info">Planned</StatusPill></div>
                )}
              </td>
              <td>
                <Link to={`/training/${s.id}`}>{s.title}</Link>
                <div className="muted">
                  {[s.venue, formatMinutes(s.durationMinutes)].filter(Boolean).join(' · ')}
                </div>
              </td>
              <td>
                {s.assetTag ? (
                  <>
                    <Link to={`/equipment/${s.equipmentId}`} className="mono">{s.assetTag}</Link>
                    <div className="muted">
                      {[s.machineName, s.manufacturer, s.model].filter(Boolean).join(' · ')}
                    </div>
                  </>
                ) : (
                  <span className="muted">—</span>
                )}
              </td>
              <td>{describeTrainer(s.trainerType, s.trainer) ?? <span className="muted">—</span>}</td>
              <td>
                {s.attendeeCount === 0 ? (
                  <span className="muted">{s.isPlanned ? 'Not yet' : 'No one listed'}</span>
                ) : (
                  <>
                    {s.attendeeCount}
                    <div className="muted">
                      {s.someAttendees.join(', ')}
                      {s.attendeeCount > s.someAttendees.length && ` and ${s.attendeeCount - s.someAttendees.length} more`}
                    </div>
                  </>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function PeopleTable({ people, filtered }: { people: TrainingPerson[] | null; filtered: boolean }) {
  return (
    <div className="card table-wrap">
      <table className="table">
        <thead>
          <tr>
            <th>Person</th>
            <th className="num">Sessions</th>
            <th>Last session</th>
          </tr>
        </thead>
        <tbody>
          {!people && <tr><td colSpan={3} className="empty">Loading…</td></tr>}
          {people && people.length === 0 && (
            <tr>
              <td colSpan={3} className="empty">
                {filtered ? 'No one matches that.' : 'No one has been listed on a session yet.'}
              </td>
            </tr>
          )}
          {people?.map((p) => (
            <tr key={p.userId ?? p.name.toLowerCase()}>
              <td>
                {p.name}
                {p.designation && <div className="muted">{p.designation}</div>}
              </td>
              <td className="num">{p.sessions}</td>
              <td>{formatDate(p.lastSessionDate)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
