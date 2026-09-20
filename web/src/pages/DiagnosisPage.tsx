import { useEffect, useMemo, useRef, useState } from 'react';
import { Link, Navigate, useLocation, useNavigate, useParams } from 'react-router-dom';
import { api, ApiError } from '../api/client';
import { formatDateTime } from '../time';
import { StatusPill } from '../StatusPill';
import { DIAGNOSIS_LOOK, DIAGNOSIS_WORDS } from '../statusTones';
import { AnswerControl } from './ChecklistAnswerControl';
import type { Item } from './ChecklistAnswerControl';

type Section = { title: string; items: Item[] };
type Answers = Record<string, { value: string; note?: string }>;

type Form = {
  id: number;
  assetTag: string;
  equipmentTypeName: string;
  locationName: string;
  checklistName: string;
  checklistTemplateVersionId: number;
  versionNo: number;
  definition: { sections: Section[] };
  available: { id: number; name: string }[];
};

const OUTCOMES = [10, 20, 30];

/** A reading outside its range, or a failed check: what the engineer should look at. */
function findings(items: Item[], answers: Answers): { failed: number; outOfRange: number } {
  let failed = 0;
  let outOfRange = 0;
  for (const item of items) {
    const value = answers[item.key]?.value;
    if (!value) continue;
    if (value === 'fail') failed += 1;
    if (item.type === 30) {
      const n = Number(value);
      if (!Number.isNaN(n) && ((item.min != null && n < item.min) || (item.max != null && n > item.max))) {
        outOfRange += 1;
      }
    }
  }
  return { failed, outOfRange };
}

/**
 * Checking one machine: the everyday diagnosis, at /equipment/:id/diagnose.
 *
 * What is asked comes from the hospital's own daily-check checklist for this kind of
 * machine. The engineer answers, says whether the machine is working, and it is kept
 * against the exact version of the questions they were shown.
 */
export function DiagnosisPage() {
  const { id } = useParams();
  const navigate = useNavigate();
  const location = useLocation();
  const machineId = Number(id);

  const from = (location.state as { from?: string } | null)?.from ?? `/equipment/${machineId}`;

  const [templateId, setTemplateId] = useState<number | null>(null);
  const [form, setForm] = useState<Form | null>(null);
  const [answers, setAnswers] = useState<Answers>({});
  const [choice, setChoice] = useState<number | null>(null);
  const [notes, setNotes] = useState('');
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [problems, setProblems] = useState<Record<string, string>>({});

  // One key per attempt, so a double-click or a retry after a lost reply is one diagnosis.
  const submissionId = useRef(crypto.randomUUID());

  useEffect(() => {
    if (!Number.isInteger(machineId) || machineId <= 0) return;
    let cancelled = false;
    (async () => {
      setLoading(true);
      setError(null);
      try {
        const q = templateId ? `?templateId=${templateId}` : '';
        const f = await api.get<Form>(`/api/equipment/${machineId}/diagnosis/form${q}`);
        if (!cancelled) {
          setForm(f);
          setAnswers({});
        }
      } catch (e) {
        if (!cancelled) {
          setForm(null);
          setError(
            e instanceof ApiError && e.status === 404
              ? 'That machine could not be found.'
              : e instanceof Error ? e.message : 'Could not load the check.',
          );
        }
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [machineId, templateId]);

  const items = useMemo(() => form?.definition.sections.flatMap((s) => s.items) ?? [], [form]);
  const found = useMemo(() => findings(items, answers), [items, answers]);
  const suggested = found.failed + found.outOfRange > 0 ? 20 : 10;
  const outcome = choice ?? suggested;

  const requiredTotal = items.filter((i) => i.required).length;
  const requiredDone = items.filter((i) => i.required && (answers[i.key]?.value ?? '').trim() !== '').length;

  if (!Number.isInteger(machineId) || machineId <= 0) return <Navigate to="/equipment" replace />;

  function set(key: string, value: string) {
    setAnswers((prev) => ({ ...prev, [key]: { ...prev[key], value } }));
    setProblems((prev) => {
      if (!prev[key]) return prev;
      const next = { ...prev };
      delete next[key];
      return next;
    });
  }

  async function submit() {
    if (!form) return;
    setBusy(true);
    setError(null);
    setProblems({});
    try {
      await api.post(`/api/equipment/${machineId}/diagnoses`, {
        checklistTemplateVersionId: form.checklistTemplateVersionId,
        answers,
        outcome,
        notes: notes.trim() || undefined,
        clientSubmissionId: submissionId.current,
      });

      const message = outcome === 10
        ? `${form.assetTag} checked: working.`
        : `${form.assetTag} checked: ${DIAGNOSIS_WORDS[outcome].toLowerCase()}. Report a fault so it is followed up.`;
      navigate(from, { replace: true, state: { handoff: { notice: message, recorded: null } } });
    } catch (e) {
      const body = e instanceof ApiError ? e.body as { problems?: { item: string; message: string }[] } | undefined : undefined;
      if (body?.problems?.length) {
        setProblems(Object.fromEntries(body.problems.map((p) => [p.item, p.message])));
      } else {
        setError(e instanceof Error ? e.message : 'Could not record this check.');
      }
    } finally {
      setBusy(false);
    }
  }

  if (loading && !form) return <div className="page"><p className="muted">Loading…</p></div>;

  if (!form) {
    return (
      <div className="page">
        <button className="btn btn-quiet" onClick={() => navigate(from)}>← Back</button>
        <p className="alert alert-info" role="alert">{error ?? 'Could not load the check.'}</p>
      </div>
    );
  }

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <button className="btn btn-quiet" onClick={() => navigate(from)}>← Back</button>
          <h1 style={{ marginBottom: 0 }}>Check {form.assetTag}</h1>
          <p className="muted">
            {form.equipmentTypeName} · {form.locationName} · {form.checklistName} v{form.versionNo}
          </p>
        </div>
      </header>

      {form.available.length > 1 && (
        <label className="field">
          <span>Which check</span>
          <select
            aria-label="Which check"
            value={templateId ?? form.available[0].id}
            onChange={(e) => setTemplateId(Number(e.target.value))}
          >
            {form.available.map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}
          </select>
        </label>
      )}

      {error && <p className="alert alert-error" role="alert">{error}</p>}
      {Object.keys(problems).length > 0 && (
        <p className="alert alert-error" role="alert">
          {Object.keys(problems).length === 1 ? '1 answer needs attention.' : `${Object.keys(problems).length} answers need attention.`}
        </p>
      )}

      <p className="alert alert-ok">
        {requiredDone} of {requiredTotal} required check{requiredTotal === 1 ? '' : 's'} answered.
      </p>

      {form.definition.sections.map((section, si) => (
        <div className="card stack" key={si}>
          <h2 className="section-h" style={{ marginTop: 0 }}>{section.title}</h2>

          {section.items.map((item) => (
            <div key={item.key} className="stack" style={{ gap: '0.35rem' }}>
              <div>
                <strong>{item.label}</strong>
                {item.required && <span className="muted"> *</span>}
                {item.guidance && <div className="muted">{item.guidance}</div>}
              </div>

              <AnswerControl item={item} value={answers[item.key]?.value ?? ''} onChange={(v) => set(item.key, v)} />

              {problems[item.key] && <div className="hist-fault">{problems[item.key]}</div>}

              <input
                className="field"
                placeholder="Note (optional)"
                aria-label={`Note for: ${item.label}`}
                value={answers[item.key]?.note ?? ''}
                onChange={(e) =>
                  setAnswers((prev) => ({ ...prev, [item.key]: { value: prev[item.key]?.value ?? '', note: e.target.value } }))
                }
              />
            </div>
          ))}
        </div>
      ))}

      <div className="card stack">
        <h2 className="section-h" style={{ marginTop: 0 }}>Verdict</h2>

        {found.failed + found.outOfRange > 0 && (
          <p className="muted" style={{ margin: 0 }}>
            {found.failed > 0 && `${found.failed} failed check${found.failed === 1 ? '' : 's'}. `}
            {found.outOfRange > 0 && `${found.outOfRange} reading${found.outOfRange === 1 ? '' : 's'} outside the acceptable range. `}
            Recorded as findings; do not adjust a reading to fit.
          </p>
        )}

        <div role="group" aria-label="Is the machine working" className="row">
          {OUTCOMES.map((o) => (
            <button
              key={o}
              type="button"
              aria-pressed={outcome === o}
              className={outcome === o ? 'btn btn-primary' : 'btn'}
              onClick={() => setChoice(o)}
            >
              {DIAGNOSIS_WORDS[o]}
            </button>
          ))}
        </div>

        <label className="stack">
          <span>Notes (optional)</span>
          <input
            className="field"
            placeholder="Anything the next engineer should know"
            maxLength={1000}
            value={notes}
            onChange={(e) => setNotes(e.target.value)}
          />
        </label>

        <div className="row">
          <button className="btn btn-primary" onClick={() => void submit()} disabled={busy}>
            {busy ? 'Recording…' : 'Record this check'}
          </button>
          <button className="btn" onClick={() => navigate(from)} disabled={busy}>Cancel</button>
        </div>

        <p className="muted" style={{ margin: 0 }}>
          Once recorded, a check cannot be edited or deleted. If it was wrong, check the machine again.
        </p>
      </div>
    </div>
  );
}

type Recorded = {
  id: number;
  equipmentId: number;
  assetTag: string;
  equipmentTypeName: string;
  checklistName: string;
  location: string | null;
  performedBy: string | null;
  performedAtUtc: string;
  outcome: number;
  notes: string | null;
  versionNo: number;
  definition: { sections: Section[] };
  answers: Answers;
};

/** One check that was recorded, shown with the questions as they were then. */
export function DiagnosisViewPage() {
  const { id } = useParams();
  const [d, setD] = useState<Recorded | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    (async () => {
      try {
        setD(await api.get<Recorded>(`/api/diagnoses/${id}`));
      } catch (e) {
        setError(e instanceof ApiError && e.status === 404 ? 'No check with that number.' : 'Could not load this check.');
      }
    })();
  }, [id]);

  if (error) return <div className="page"><p className="alert alert-error">{error}</p></div>;
  if (!d) return <div className="page"><p className="muted">Loading…</p></div>;

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <Link className="btn btn-quiet" to={`/equipment/${d.equipmentId}`}>← Back to {d.assetTag}</Link>
          <h1 style={{ marginBottom: 0 }}>{d.checklistName} <span className="muted">v{d.versionNo}</span></h1>
          <p className="muted">
            <span className="mono">{d.assetTag}</span> · {d.equipmentTypeName} · {d.location ?? '—'} ·{' '}
            {formatDateTime(d.performedAtUtc)} · {d.performedBy ?? 'unknown'}
          </p>
        </div>
        <StatusPill look={DIAGNOSIS_LOOK[d.outcome]}>{DIAGNOSIS_WORDS[d.outcome]}</StatusPill>
      </header>

      {d.definition.sections.map((section, si) => (
        <div className="card stack" key={si}>
          <h2 className="section-h" style={{ marginTop: 0 }}>{section.title}</h2>
          {section.items.map((item) => {
            const a = d.answers[item.key];
            return (
              <div key={item.key} className="hist-row">
                <div className="grow">
                  <div>{item.label}</div>
                  {a?.note && <div className="muted hist-meta">{a.note}</div>}
                </div>
                <strong className={a?.value === 'fail' ? 'hist-fault' : undefined}>
                  {a?.value ? `${a.value}${item.unit && item.type === 30 ? ` ${item.unit}` : ''}` : '—'}
                </strong>
              </div>
            );
          })}
        </div>
      ))}

      {d.notes && <p className="card">{d.notes}</p>}
    </div>
  );
}
