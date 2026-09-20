import { useEffect, useMemo, useRef, useState } from 'react';
import { api, ApiError } from '../api/client';
import { useAuth } from '../auth/useAuth';
import { formatDate } from '../time';
import { AnswerControl } from './ChecklistAnswerControl';
import type { Item } from './ChecklistAnswerControl';

/**
 * Filling in and recording a PM from the PC.
 *
 * This was the last thing a hospital could not do without a phone. The API and
 * the Expo app have had it since the PM slices were built; the web UI could
 * define an entire maintenance programme, generate ten thousand tasks, and
 * offer no way to say any of them had been done.
 *
 * Deliberately close to the Expo screen rather than a nicer desktop design:
 * the same answer values, the same advisory out-of-range handling, the same
 * idempotency key. A technician who learns one should recognise the other, and
 * two clients that disagree about what "pass" means is a data problem, not a
 * styling one.
 */

type Section = { title: string; items: Item[] };

type PmForm = {
  id: number;
  status: number;
  dueDate: string;
  equipmentId: number;
  assetTag: string;
  equipmentTypeName: string;
  locationName: string;
  checklistName: string;
  checklistTemplateVersionId: number;
  versionNo: number;
  definition: { sections: Section[] };
};

type Answers = Record<string, { value: string; note?: string }>;

export function PmChecklistForm({
  taskId,
  canSkip,
  onClose,
  onDone,
  backLabel = 'the PM list',
}: {
  taskId: number;
  canSkip: boolean;
  onClose: () => void;
  /**
   * `completed` is false for a skip: only a completed PM has a certificate.
   * `task` names what was recorded, so the page it returns to can offer the
   * certificate without having had the row to hand.
   */
  onDone: (
    message: string,
    completed: boolean,
    task: { id: number; assetTag: string; dueDate: string },
  ) => void | Promise<void>;
  /** Where "back" goes, said in words: the PM list, or the machine. */
  backLabel?: string;
}) {
  const [form, setForm] = useState<PmForm | null>(null);
  const [answers, setAnswers] = useState<Answers>({});
  // The name on the certificate. Whoever is signed in is almost always the one
  // who did the work, so it starts as their name; it stays editable for a
  // supervisor recording a PM on someone else's behalf.
  const { user } = useAuth();
  const [signedBy, setSignedBy] = useState(user?.fullName ?? '');
  const [notes, setNotes] = useState('');
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [problems, setProblems] = useState<Record<string, string>>({});

  // Generated once per attempt, not per click. The server treats a repeat as
  // the same submission and returns the original result, so a double-click or
  // a retry after a dropped response cannot record two PMs.
  const submissionId = useRef(crypto.randomUUID());
  const signatureRef = useRef<SignaturePadHandle | null>(null);

  useEffect(() => {
    (async () => {
      setLoading(true);
      try {
        setForm(await api.get<PmForm>(`/api/pm/tasks/${taskId}/form`));
      } catch (e) {
        setError(
          e instanceof ApiError && e.status === 409
            ? 'This checklist has no published version, so the PM cannot be recorded yet.'
            : e instanceof ApiError && e.status === 404
              ? 'That PM could not be found. It may have been removed, or the address is wrong.'
              : e instanceof Error ? e.message : 'Could not load this PM.',
        );
      } finally {
        setLoading(false);
      }
    })();
  }, [taskId]);

  const items = useMemo(
    () => form?.definition.sections.flatMap((s) => s.items) ?? [],
    [form],
  );

  const requiredTotal = items.filter((i) => i.required).length;
  const requiredDone = items.filter(
    (i) => i.required && (answers[i.key]?.value ?? '').trim().length > 0,
  ).length;

  function set(key: string, value: string) {
    setAnswers((prev) => ({ ...prev, [key]: { ...prev[key], value } }));
    setProblems((prev) => {
      if (!prev[key]) return prev;
      const next = { ...prev };
      delete next[key];
      return next;
    });
  }

  function setNote(key: string, note: string) {
    setAnswers((prev) => ({ ...prev, [key]: { value: prev[key]?.value ?? '', note } }));
  }

  /**
   * A reading outside its range is flagged, never rejected.
   *
   * The measurement IS the finding. Refusing to store an out-of-spec value is
   * how real data quietly gets rounded back into range, so this is advisory
   * here and recorded as out-of-range by the server.
   */
  function outOfRange(item: Item): boolean {
    if (item.type !== 30) return false;
    const raw = answers[item.key]?.value;
    if (!raw) return false;
    const n = Number(raw);
    if (Number.isNaN(n)) return false;
    return (item.min != null && n < item.min) || (item.max != null && n > item.max);
  }

  async function submit() {
    if (!form) return;
    setBusy(true);
    setError(null);
    setProblems({});

    try {
      const signature = signatureRef.current?.toPngBase64() ?? null;

      const result = await api.post<{ outOfRangeCount: number; failedCheckCount: number }>(
        `/api/pm/tasks/${form.id}/complete`,
        {
          checklistTemplateVersionId: form.checklistTemplateVersionId,
          answers,
          signatureBase64: signature ?? undefined,
          signatureFormat: signature ? 'png' : undefined,
          signedByName: signedBy.trim() || undefined,
          performedAtUtc: new Date().toISOString(),
          clientSubmissionId: submissionId.current,
          notes: notes.trim() || undefined,
        },
      );

      // Said in the words of the finding. The message used to mention only a
      // reading out of range, so a PM whose alarm test failed was confirmed as
      // if it had gone perfectly.
      const findings: string[] = [];
      if (result.failedCheckCount > 0) {
        findings.push(`${result.failedCheckCount} failed check${result.failedCheckCount === 1 ? '' : 's'}`);
      }
      if (result.outOfRangeCount > 0) {
        findings.push(
          `${result.outOfRangeCount} reading${result.outOfRangeCount === 1 ? '' : 's'} outside the acceptable range`,
        );
      }

      await onDone(
        findings.length > 0
          ? `PM recorded, with ${findings.join(' and ')}. ${result.failedCheckCount > 0 ? 'Report a fault so the machine is followed up. ' : ''}`.trim()
          : 'PM recorded.',
        true,
        { id: form.id, assetTag: form.assetTag, dueDate: form.dueDate },
      );
    } catch (e) {
      // The server validates the whole checklist and names each offending
      // item. Showing those beside the questions beats one banner saying the
      // form is incomplete on a forty-item checklist.
      const body = e instanceof ApiError ? e.body as
        { problems?: { item: string; message: string }[] } | undefined : undefined;

      if (body?.problems?.length) {
        // Said by the banner below, which counts what is still wrong. It was set
        // here as fixed text, so it stayed on screen, red, after the last problem
        // had been corrected.
        setProblems(Object.fromEntries(body.problems.map((p) => [p.item, p.message])));
      } else {
        setError(e instanceof Error ? e.message : 'Could not record this PM.');
      }
    } finally {
      setBusy(false);
    }
  }

  async function skip() {
    const reason = prompt(
      [
        'Why is this PM not being done?',
        '',
        'A skip is a permanent gap in the maintenance record and cannot be',
        'undone. It is kept, with this reason, for audit.',
      ].join('\n'),
    );
    if (reason === null) return;
    if (reason.trim().length === 0) {
      setError('A skip needs a reason.');
      return;
    }

    setBusy(true);
    setError(null);
    try {
      await api.post(`/api/pm/tasks/${taskId}/skip`, {
        reason: reason.trim(),
        clientSubmissionId: submissionId.current,
      });
      await onDone('PM skipped, with the reason recorded.', false, {
        id: form!.id,
        assetTag: form!.assetTag,
        dueDate: form!.dueDate,
      });
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not skip this PM.');
    } finally {
      setBusy(false);
    }
  }

  if (loading) return <div className="page"><p className="muted">Loading…</p></div>;

  if (!form) {
    return (
      <div className="page">
        <button className="btn btn-quiet" onClick={onClose}>← Back to {backLabel}</button>
        <p className="alert alert-error">{error ?? 'Could not load this PM.'}</p>
      </div>
    );
  }

  // Reachable by address now, not only from a row that offers it for open work.
  // A PM someone has already recorded (or skipped) would otherwise open as a
  // blank form, and the technician would fill in every check before the server
  // refused the second completion.
  if (form.status === 40 || form.status === 50) {
    return (
      <div className="page">
        <button className="btn btn-quiet" onClick={onClose}>← Back to {backLabel}</button>
        <p className="alert alert-info" role="status">
          {form.status === 40
            ? `This PM on ${form.assetTag} has already been recorded, so there is nothing left to fill in.`
            : `This PM on ${form.assetTag} was skipped, so it is not open for recording.`}
          {' '}Its certificate and history are on the machine&apos;s page.
        </p>
      </div>
    );
  }

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <button className="btn btn-quiet" onClick={onClose}>← Back to {backLabel}</button>
          <h1 style={{ marginBottom: 0 }}>{form.checklistName}</h1>
          <p className="muted">
            <span className="mono">{form.assetTag}</span> · {form.equipmentTypeName} ·{' '}
            {form.locationName} · due {formatDate(form.dueDate)} · checklist v{form.versionNo}
          </p>
        </div>
      </header>

      {error && <p className="alert alert-error" role="alert">{error}</p>}

      {Object.keys(problems).length > 0 && (
        <p className="alert alert-error" role="alert">
          {Object.keys(problems).length === 1
            ? '1 answer needs attention.'
            : `${Object.keys(problems).length} answers need attention.`}
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

              <AnswerControl
                item={item}
                value={answers[item.key]?.value ?? ''}
                onChange={(v) => set(item.key, v)}
              />

              {answers[item.key]?.value === 'fail' && (
                <div className="hist-flag">
                  Recorded as a failed check, and the certificate will say so. Add a note saying what
                  failed, and report a fault if the machine should not stay in use.
                </div>
              )}

              {outOfRange(item) && (
                <div className="hist-flag">
                  Outside the acceptable range
                  {item.min != null && item.max != null ? ` (${item.min}–${item.max}${item.unit ? ' ' + item.unit : ''})` : ''}.
                  Recorded as a finding — do not adjust the reading to fit.
                </div>
              )}

              {problems[item.key] && <div className="hist-fault">{problems[item.key]}</div>}

              <input
                className="field"
                placeholder="Note (optional)"
                aria-label={`Note for: ${item.label}`}
                value={answers[item.key]?.note ?? ''}
                onChange={(e) => setNote(item.key, e.target.value)}
              />
            </div>
          ))}
        </div>
      ))}

      <div className="card stack">
        <h2 className="section-h" style={{ marginTop: 0 }}>Sign off</h2>

        <label className="stack">
          <span>Signed by</span>
          <input
            className="field"
            placeholder="Name of the engineer performing this PM"
            maxLength={120}
            value={signedBy}
            onChange={(e) => setSignedBy(e.target.value)}
          />
          {user?.fullName && signedBy === user.fullName && (
            <span className="muted">From your account. Change it if someone else did the work.</span>
          )}
        </label>

        <div className="stack">
          <span>Signature (optional)</span>
          <SignaturePad ref={signatureRef} />
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

        <div style={{ display: 'flex', gap: '0.5rem', flexWrap: 'wrap' }}>
          <button className="btn btn-primary" onClick={() => void submit()} disabled={busy}>
            {busy ? 'Recording…' : 'Record this PM'}
          </button>
          {canSkip && (
            <button className="btn" onClick={() => void skip()} disabled={busy}>
              Skip…
            </button>
          )}
          <button className="btn" onClick={onClose} disabled={busy}>Cancel</button>
        </div>

        {/* Said before submitting, not after. A completion is immutable by
            database trigger — the remedy for a wrong one is to repeat the PM,
            which is a second visit to the machine. */}
        <p className="muted" style={{ margin: 0 }}>
          Once recorded, a PM cannot be edited or deleted. Correcting one means performing
          it again.
        </p>
      </div>
    </div>
  );
}

type SignaturePadHandle = { toPngBase64: () => string | null };

/**
 * A signature drawn with a mouse or a finger.
 *
 * PNG rather than the SVG the Expo app sends, because a browser canvas
 * produces one directly and the API accepts either. Returns null when nothing
 * was drawn, so an untouched pad records no signature rather than a blank
 * image that looks like one.
 */
function SignaturePad({ ref }: { ref: React.RefObject<SignaturePadHandle | null> }) {
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const drawing = useRef(false);
  const [dirty, setDirty] = useState(false);

  useEffect(() => {
    ref.current = {
      toPngBase64: () => {
        if (!dirty || !canvasRef.current) return null;
        return canvasRef.current.toDataURL('image/png').split(',')[1] ?? null;
      },
    };
  }, [ref, dirty]);

  function position(e: React.PointerEvent<HTMLCanvasElement>) {
    const rect = e.currentTarget.getBoundingClientRect();
    return { x: e.clientX - rect.left, y: e.clientY - rect.top };
  }

  function start(e: React.PointerEvent<HTMLCanvasElement>) {
    const ctx = canvasRef.current?.getContext('2d');
    if (!ctx) return;
    e.currentTarget.setPointerCapture(e.pointerId);
    drawing.current = true;
    setDirty(true);
    const { x, y } = position(e);
    ctx.beginPath();
    ctx.moveTo(x, y);
  }

  function move(e: React.PointerEvent<HTMLCanvasElement>) {
    if (!drawing.current) return;
    const ctx = canvasRef.current?.getContext('2d');
    if (!ctx) return;
    const { x, y } = position(e);
    ctx.lineWidth = 2;
    ctx.lineCap = 'round';
    ctx.strokeStyle = '#111';
    ctx.lineTo(x, y);
    ctx.stroke();
  }

  function end() {
    drawing.current = false;
  }

  function clear() {
    const canvas = canvasRef.current;
    const ctx = canvas?.getContext('2d');
    if (!canvas || !ctx) return;
    ctx.clearRect(0, 0, canvas.width, canvas.height);
    setDirty(false);
  }

  return (
    <div className="stack" style={{ gap: '0.35rem' }}>
      <canvas
        ref={canvasRef}
        width={480}
        height={140}
        onPointerDown={start}
        onPointerMove={move}
        onPointerUp={end}
        onPointerLeave={end}
        style={{
          background: '#fff',
          borderRadius: '6px',
          maxWidth: '100%',
          touchAction: 'none',
          cursor: 'crosshair',
        }}
      />
      <div>
        <button type="button" className="btn btn-quiet" onClick={clear}>Clear signature</button>
      </div>
    </div>
  );
}

