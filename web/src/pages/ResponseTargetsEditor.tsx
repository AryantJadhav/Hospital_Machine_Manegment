import { useEffect, useState } from 'react';
import { api } from '../api/client';
import { formatMinutes } from '../age';
import { PRIORITY_LABEL } from '../statusTones';
import { useToast } from '../toast';

type Targets = {
  minMinutes: number;
  maxMinutes: number;
  targets: { priority: number; minutes: number }[];
};

/**
 * How quickly an engineer is expected to respond to a fault, by priority: from the fault being raised until an
 * engineer has reached the machine and pressed In progress. Not the time to fix it.
 *
 * Everyone who works on faults can read the times, folded away until wanted. Only the head of the department can change
 * them: for anyone else the boxes are plain text and there is no Save. The times run all day and night; a fault raised at
 * 2 a.m. is judged at 2 a.m.
 */
export function ResponseTargetsEditor({ canEdit }: { canEdit: boolean }) {
  const toast = useToast();
  const [data, setData] = useState<Targets | null>(null);
  const [draft, setDraft] = useState<Record<number, string>>({});
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const t = await api.get<Targets>('/api/work-orders/response-targets');
        if (cancelled) return;
        setData(t);
        setDraft(Object.fromEntries(t.targets.map((x) => [x.priority, String(x.minutes)])));
      } catch {
        // The list works without it; the section simply does not appear.
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  if (!data) return null;

  function valid(text: string): number | null {
    const n = Number(text);
    return Number.isInteger(n) && n >= data!.minMinutes && n <= data!.maxMinutes ? n : null;
  }

  const allValid = data.targets.every((t) => valid(draft[t.priority] ?? '') !== null);
  const changed = data.targets.some((t) => String(t.minutes) !== (draft[t.priority] ?? ''));

  async function save() {
    setBusy(true);
    setError(null);
    try {
      const saved = await api.put<Targets>('/api/work-orders/response-targets', {
        targets: data!.targets.map((t) => ({ priority: t.priority, minutes: valid(draft[t.priority] ?? '') })),
      });
      setData(saved);
      setDraft(Object.fromEntries(saved.targets.map((x) => [x.priority, String(x.minutes)])));
      toast.show('Response times saved.');
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not save the response times.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <details className="card">
      <summary>
        <strong>Response times</strong>{' '}
        <span className="muted">
          How soon an engineer should start a fault:{' '}
          {data.targets.map((t) => `${PRIORITY_LABEL[t.priority]} ${formatMinutes(t.minutes)}`).join(', ')}
        </span>
      </summary>

      <div className="stack">
        <p className="muted">
          The time from a fault being raised until an engineer reaches the machine and presses <strong>In progress</strong>.
          It is not the time to fix the fault. The clock runs day and night.
          {!canEdit && ' Only the head of the department can change these times.'}
        </p>

        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th>Priority</th>
                <th>{canEdit ? 'Allowed, in minutes' : 'Allowed'}</th>
                {canEdit && <th>That is</th>}
              </tr>
            </thead>
            <tbody>
              {data.targets.map((t) => {
                const n = valid(draft[t.priority] ?? '');
                return (
                  <tr key={t.priority}>
                    <td>{PRIORITY_LABEL[t.priority]}</td>
                    <td>
                      {canEdit ? (
                        <input
                          className="field mono"
                          type="number"
                          min={data.minMinutes}
                          max={data.maxMinutes}
                          step={1}
                          inputMode="numeric"
                          aria-label={`Minutes allowed for ${PRIORITY_LABEL[t.priority]} priority`}
                          value={draft[t.priority] ?? ''}
                          onChange={(e) => setDraft((d) => ({ ...d, [t.priority]: e.target.value }))}
                        />
                      ) : (
                        formatMinutes(t.minutes)
                      )}
                    </td>
                    {canEdit && (
                      <td>{n === null ? <span className="muted">Use 1 to {data.maxMinutes} minutes</span> : formatMinutes(n)}</td>
                    )}
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>

        {error && <p className="alert alert-error" role="alert">{error}</p>}

        {canEdit && (
          <div className="row">
            <button className="btn btn-primary" disabled={busy || !allValid || !changed} onClick={() => void save()}>
              {busy ? 'Saving…' : 'Save response times'}
            </button>
          </div>
        )}
      </div>
    </details>
  );
}
