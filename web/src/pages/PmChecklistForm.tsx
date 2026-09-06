import { useEffect, useMemo, useRef, useState } from 'react';
import { api, ApiError } from '../api/client';

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

type ItemType = 10 | 20 | 30 | 40 | 50;

type Item = {
  key: string;
  label: string;
  type: ItemType;
  required: boolean;
  guidance?: string | null;
  unit?: string | null;
  min?: number | null;
  max?: number | null;
  options?: string[] | null;
};

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
}: {
  taskId: number;
  canSkip: boolean;
  onClose: () => void;
  onDone: (message: string) => void | Promise<void>;
}) {
  const [form, setForm] = useState<PmForm | null>(null);
  const [answers, setAnswers] = useState<Answers>({});
  const [signedBy, setSignedBy] = useState('');
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

      const result = await api.post<{ outOfRangeCount: number }>(
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

      await onDone(
        result.outOfRangeCount > 0
          ? `PM recorded, with ${result.outOfRangeCount} reading${result.outOfRangeCount === 1 ? '' : 's'} outside the acceptable range.`
          : 'PM recorded.',
      );
    } catch (e) {
      // The server validates the whole checklist and names each offending
      // item. Showing those beside the questions beats one banner saying the
      // form is incomplete on a forty-item checklist.
      const body = e instanceof ApiError ? e.body as
        { problems?: { item: string; message: string }[] } | undefined : undefined;

      if (body?.problems?.length) {
        setProblems(Object.fromEntries(body.problems.map((p) => [p.item, p.message])));
        setError('Some answers need attention.');
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
      await onDone('PM skipped, with the reason recorded.');
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
        <button className="btn btn-quiet" onClick={onClose}>← Back to the PM list</button>
        <p className="alert alert-error">{error ?? 'Could not load this PM.'}</p>
      </div>
    );
  }

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <button className="btn btn-quiet" onClick={onClose}>← Back to the PM list</button>
          <h1 style={{ marginBottom: 0 }}>{form.checklistName}</h1>
          <p className="muted">
            <span className="mono">{form.assetTag}</span> · {form.equipmentTypeName} ·{' '}
            {form.locationName} · due {formatDate(form.dueDate)} · checklist v{form.versionNo}
          </p>
        </div>
      </header>

      {error && <p className="alert alert-error" role="alert">{error}</p>}

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

function AnswerControl({
  item,
  value,
  onChange,
}: {
  item: Item;
  value: string;
  onChange: (value: string) => void;
}) {
  // The same values the API validates and the Expo app sends. A third spelling
  // of "pass" would be a data problem, not a styling one.
  if (item.type === 10 || item.type === 20) {
    const options = item.type === 10
      ? [{ v: 'pass', label: 'Pass' }, { v: 'fail', label: 'Fail' }, { v: 'na', label: 'N/A' }]
      : [{ v: 'yes', label: 'Yes' }, { v: 'no', label: 'No' }, { v: 'na', label: 'N/A' }];

    return (
      <div style={{ display: 'flex', gap: '0.4rem', flexWrap: 'wrap' }}>
        {options.map((o) => (
          <button
            key={o.v}
            type="button"
            className={value === o.v ? 'btn btn-primary' : 'btn'}
            onClick={() => onChange(o.v)}
          >
            {o.label}
          </button>
        ))}
      </div>
    );
  }

  if (item.type === 50) {
    return (
      <div style={{ display: 'flex', gap: '0.4rem', flexWrap: 'wrap' }}>
        {(item.options ?? []).map((o) => (
          <button
            key={o}
            type="button"
            className={value === o ? 'btn btn-primary' : 'btn'}
            onClick={() => onChange(o)}
          >
            {o}
          </button>
        ))}
      </div>
    );
  }

  if (item.type === 30) {
    return (
      <div style={{ display: 'flex', gap: '0.4rem', alignItems: 'center' }}>
        <input
          className="field"
          type="number"
          step="any"
          inputMode="decimal"
          value={value}
          onChange={(e) => onChange(e.target.value)}
          style={{ maxWidth: '12rem' }}
        />
        {item.unit && <span className="muted">{item.unit}</span>}
        {(item.min != null || item.max != null) && (
          <span className="muted">
            acceptable {item.min ?? '−∞'} to {item.max ?? '∞'}
          </span>
        )}
      </div>
    );
  }

  return (
    <input
      className="field"
      value={value}
      maxLength={1000}
      onChange={(e) => onChange(e.target.value)}
    />
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

/** Day-first, as every date in an Indian hospital is written. */
function formatDate(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '—';
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${pad(d.getDate())}/${pad(d.getMonth() + 1)}/${d.getFullYear()}`;
}
