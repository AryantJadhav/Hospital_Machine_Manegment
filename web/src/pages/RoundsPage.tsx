import { useEffect, useMemo, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { api } from '../api/client';
import { formatDateTime } from '../time';
import { StatusPill } from '../StatusPill';
import { DIAGNOSIS_LOOK, DIAGNOSIS_WORDS } from '../statusTones';
import { useHandoff } from '../handoff';
import { HandoffNotice } from '../HandoffNotice';

type Place = { id: number; name: string; depth: number };

type Row = {
  id: number;
  assetTag: string;
  typeName: string;
  location: string;
  hasChecklist: boolean;
  checkedToday: { id: number; outcome: number; performedAtUtc: string; by: string | null } | null;
};

type Round = { date: string; total: number; shown: number; items: Row[] };

/**
 * The daily round: pick a place, walk it, check each machine.
 *
 * Every machine in the place (and inside it) is listed with whether it has been
 * checked today and how that went, so the engineer can see what is left and the
 * head can see what was missed.
 */
export function RoundsPage() {
  const [params, setParams] = useSearchParams();
  const place = params.get('place') ?? '';

  const [places, setPlaces] = useState<Place[]>([]);
  const [round, setRound] = useState<Round | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [handoff] = useHandoff();

  useEffect(() => {
    (async () => {
      try {
        setPlaces(await api.get<Place[]>('/api/lookups/locations'));
      } catch {
        // The round still works for the whole hospital without the list.
      }
    })();
  }, []);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      setError(null);
      try {
        const r = await api.get<Round>(`/api/diagnosis/rounds${place ? `?locationId=${place}` : ''}`);
        if (!cancelled) setRound(r);
      } catch (e) {
        if (!cancelled) setError(e instanceof Error ? e.message : 'Could not load the round.');
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [place]);

  const summary = useMemo(() => {
    const items = round?.items ?? [];
    const toCheck = items.filter((r) => r.hasChecklist);
    const done = toCheck.filter((r) => r.checkedToday);
    return {
      toCheck: toCheck.length,
      done: done.length,
      attention: done.filter((r) => r.checkedToday!.outcome !== 10).length,
      noChecklist: items.length - toCheck.length,
    };
  }, [round]);

  const here = `/rounds${place ? `?place=${place}` : ''}`;

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <h1>Daily round</h1>
          <p className="muted">Check each machine in a place, and see what has been checked today.</p>
        </div>
      </header>

      <HandoffNotice handoff={handoff} />
      {error && <p className="alert alert-error" role="alert">{error}</p>}

      <div className="filters card">
        <label className="field grow">
          <span>Where</span>
          <select
            aria-label="Where"
            value={place}
            onChange={(e) => setParams(e.target.value ? { place: e.target.value } : {})}
          >
            <option value="">Whole hospital</option>
            {places.map((p) => (
              <option key={p.id} value={p.id}>{' '.repeat(p.depth * 2)}{p.name}</option>
            ))}
          </select>
        </label>
      </div>

      {round && (
        <>
          <p
            className={summary.done === summary.toCheck && summary.toCheck > 0 ? 'alert alert-ok' : 'alert alert-info'}
            role="status"
          >
            {summary.done} of {summary.toCheck} checked today
            {summary.attention > 0 && ` · ${summary.attention} need${summary.attention === 1 ? 's' : ''} attention`}
            {summary.noChecklist > 0 && ` · ${summary.noChecklist} with no daily check set up for their type`}
          </p>

          {round.total > round.shown && (
            <p className="alert alert-warn">
              Showing the first {round.shown} of {round.total} machines. Choose a smaller place to see the rest.
            </p>
          )}

          <div className="card table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th>Machine</th>
                  <th>Type</th>
                  <th>Where</th>
                  <th>Today</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {round.items.length === 0 && (
                  <tr><td colSpan={5} className="empty">No machines in use here.</td></tr>
                )}

                {round.items.map((r) => (
                  <tr key={r.id}>
                    <td><Link className="mono" to={`/equipment/${r.id}`}>{r.assetTag}</Link></td>
                    <td>{r.typeName}</td>
                    <td>{r.location}</td>
                    <td>
                      {r.checkedToday ? (
                        <>
                          <Link to={`/diagnoses/${r.checkedToday.id}`}>
                            <StatusPill look={DIAGNOSIS_LOOK[r.checkedToday.outcome]}>
                              {DIAGNOSIS_WORDS[r.checkedToday.outcome]}
                            </StatusPill>
                          </Link>
                          <div className="muted hist-meta">
                            {formatDateTime(r.checkedToday.performedAtUtc)} · {r.checkedToday.by ?? 'unknown'}
                          </div>
                        </>
                      ) : r.hasChecklist ? (
                        <StatusPill tone="neutral">Not checked</StatusPill>
                      ) : (
                        <span className="muted">No daily check for this type</span>
                      )}
                    </td>
                    <td>
                      {r.hasChecklist && (
                        <Link className="btn" to={`/equipment/${r.id}/diagnose`} state={{ from: here }}>
                          {r.checkedToday ? 'Check again' : 'Diagnose'}
                        </Link>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}
    </div>
  );
}
