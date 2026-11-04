import { useCallback, useEffect, useState } from 'react';
import type { FormEvent } from 'react';
import { Link, Navigate, useLocation, useParams } from 'react-router-dom';
import { api, ApiError } from '../api/client';
import { useDialogs } from '../dialogs';
import { PERMISSIONS } from '../auth/context';
import { useAuth } from '../auth/useAuth';
import { HandoffNotice } from '../HandoffNotice';
import { useHandoff } from '../handoff';
import { LOSS_STATUS_LABEL, LOSS_STATUS_LOOK } from '../lossTypes';
import type { LossDetail } from '../lossTypes';
import { formatRupees } from '../money';
import { usePageTitle } from '../pageTitle';
import { StatusPill } from '../StatusPill';
import { formatDate, formatDateTime, todayAtHospital } from '../time';
import { LossForm } from './LossForm';

/**
 * One loss on its own page: /losses/:id. Which machine, how and when it went, who was told, what it was worth
 * and where it stands on its way up:
 *
 *   1. the department (or an engineer, or the head) writes it up;
 *   2. a biomedical engineer looks into it and approves it, or rejects it as not lost;
 *   3. the head of Biomedical makes the final decision: write it off, or send it back to the engineer.
 *
 * Each person sees the step that is theirs, and the rest read-only. A loss that has ended (found, written off or
 * cancelled) is final. While it is open, an engineer or the head can mark the machine found.
 */
export function LossPage() {
  const { id: idParam } = useParams();
  const id = Number(idParam);
  const valid = Number.isInteger(id) && id > 0;

  const { may } = useAuth();
  const canReview = may(PERMISSIONS.lossesReview);
  const canDecide = may(PERMISSIONS.lossesDecide);
  // The machine's own page is for those who work on the equipment; a person from another department
  // sees its number and nothing opens.
  const canOpenMachine = may(PERMISSIONS.registerView);
  const location = useLocation();
  const [handoff, setHandoff] = useHandoff();

  const [loss, setLoss] = useState<LossDetail | null>(null);
  const [missing, setMissing] = useState(false);
  const [editing, setEditing] = useState(false);
  const [error, setError] = useState<string | null>(null);

  usePageTitle(loss ? loss.reference : undefined);

  const load = useCallback(async () => {
    try {
      setLoss(await api.get<LossDetail>(`/api/losses/${id}`));
      setMissing(false);
    } catch (e) {
      if (e instanceof ApiError && e.status === 404) setMissing(true);
      else setError(e instanceof Error ? e.message : 'Could not open that loss.');
    }
  }, [id]);

  useEffect(() => {
    if (valid) void load();
  }, [valid, load]);

  if (!valid) return <Navigate to="/losses" replace />;

  if (missing) {
    return (
      <div className="page stack">
        <p className="alert alert-error" role="alert">That loss does not exist.</p>
        <div><Link className="btn" to="/losses">Back to Loss of equipment</Link></div>
      </div>
    );
  }

  if (!loss) {
    return (
      <div className="page">
        {error ? <p className="alert alert-error" role="alert">{error}</p> : <p className="muted">Loading…</p>}
      </div>
    );
  }

  if (editing) {
    return (
      <div className="page">
        <LossForm
          editing={loss}
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

  const back = (location.state as { from?: string } | null)?.from ?? '/losses';
  const withEngineer = loss.status === 'Reported' || loss.status === 'Investigating';
  const awaiting = loss.status === 'AwaitingDecision';

  async function done(message: string) {
    setHandoff({ notice: message, recorded: null });
    await load();
  }

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <p className="crumb"><Link to={back}>← Back to Loss of equipment</Link></p>
          <h1 className="mono">{loss.reference}</h1>
          <p className="muted">
            {loss.kindLabel} · found missing {formatDate(loss.noticedOn)}
          </p>
        </div>

        <div className="row">
          {/* A preview in a tab of its own first; printing or downloading is a choice made there. */}
          <a className="btn btn-primary" href={`/losses/${loss.id}/report`} target="_blank" rel="noopener">
            View loss report
          </a>
          {canReview && !loss.isFinal && <button className="btn" onClick={() => setEditing(true)}>Correct</button>}
        </div>
      </header>

      <HandoffNotice handoff={handoff} />
      {error && <p className="alert alert-error" role="alert">{error}</p>}

      <div className="row">
        <StatusPill look={LOSS_STATUS_LOOK[loss.status]}>{LOSS_STATUS_LABEL[loss.status]}</StatusPill>
        {loss.estimatedValue !== null && <StatusPill tone="neutral">Worth {formatRupees(loss.estimatedValue)}</StatusPill>}
        {loss.policeReportNumber && <StatusPill tone="info">Police complaint made</StatusPill>}
      </div>

      <Steps loss={loss} />

      {/* Whose turn it is, said to the person it is waiting for. */}
      {withEngineer && canReview && (
        <p className="alert alert-warn" role="status" style={{ margin: 0 }}>
          This is waiting for you. Look into it, say what you found out, and approve it for the head, or reject it if the machine was not lost.
        </p>
      )}
      {awaiting && canDecide && (
        <p className="alert alert-warn" role="status" style={{ margin: 0 }}>
          An engineer has approved this and it is waiting for your final decision.
        </p>
      )}

      <section className="card stack">
        <h2 className="section-h">Machine</h2>
        {loss.machine ? (
          <dl className="detail">
            <dt>Machine number</dt>
            <dd className="mono">
              {canOpenMachine ? (
                <Link to={`/equipment/${loss.machine.id}`} state={{ from: `/losses/${loss.id}` }}>
                  {loss.machine.assetTag}
                </Link>
              ) : (
                loss.machine.assetTag
              )}
            </dd>
            <dt>Machine</dt>
            <dd>{loss.machine.machineName ?? <span className="muted">—</span>}</dd>
            <dt>Company and model</dt>
            <dd>{[loss.machine.manufacturer, loss.machine.model].filter(Boolean).join(' ') || <span className="muted">—</span>}</dd>
            <dt>Serial number</dt>
            <dd>{loss.machine.serialNumber ?? <span className="muted">—</span>}</dd>
            <dt>Meant to be in</dt>
            <dd>{loss.locationName ?? <span className="muted">—</span>}</dd>
          </dl>
        ) : (
          <p className="muted" style={{ margin: 0 }}>The machine is not on the register you can see.</p>
        )}
      </section>

      <section className="card stack">
        <h2 className="section-h">What happened</h2>
        <dl className="detail">
          <dt>How</dt>
          <dd>{loss.kindLabel}</dd>
          <dt>Found missing</dt>
          <dd>{formatDate(loss.noticedOn)}</dd>
          <dt>Last seen</dt>
          <dd>
            {loss.lastSeenOn || loss.lastSeenPlace
              ? [loss.lastSeenOn && formatDate(loss.lastSeenOn), loss.lastSeenPlace].filter(Boolean).join(' · ')
              : <span className="muted">—</span>}
          </dd>
          <dt>Last handled by</dt>
          <dd>{loss.lastHandledBy ?? <span className="muted">—</span>}</dd>
          <dt>Worth</dt>
          <dd>{loss.estimatedValue !== null ? formatRupees(loss.estimatedValue) : <span className="muted">Not recorded</span>}</dd>
          <dt>Police complaint</dt>
          <dd>
            {loss.policeReportNumber || loss.policeReportedOn
              ? [loss.policeReportNumber, loss.policeReportedOn && `told on ${formatDate(loss.policeReportedOn)}`].filter(Boolean).join(' · ')
              : <span className="muted">None recorded</span>}
          </dd>
          <dt>Reported</dt>
          <dd>
            {formatDateTime(loss.reportedAtUtc)}
            {loss.reportedByName && ` by ${loss.reportedByName}`}
          </dd>
        </dl>
        <p style={{ margin: 0, whiteSpace: 'pre-wrap' }}>{loss.description}</p>
      </section>

      {/* The step that is this person's. Everyone else reads what has been written so far. */}
      {withEngineer && canReview ? (
        <EngineerForm loss={loss} onDone={done} onError={setError} />
      ) : awaiting && canDecide ? (
        <HeadForm loss={loss} onDone={done} onError={setError} />
      ) : (
        <ReadOnlyReview loss={loss} onDone={done} onError={setError} canMarkFound={awaiting && canReview} />
      )}
    </div>
  );
}

/** The three steps, and where this loss has got to. Words and a mark, not colour alone. */
function Steps({ loss }: { loss: LossDetail }) {
  const withEngineer = loss.status === 'Reported' || loss.status === 'Investigating';
  const awaiting = loss.status === 'AwaitingDecision';
  const ended = loss.isFinal;

  const steps = [
    {
      title: 'Reported',
      detail: `${loss.reportedByName ?? 'Someone'} · ${formatDateTime(loss.reportedAtUtc)}`,
      state: 'done' as const,
    },
    {
      title: 'Biomedical engineer',
      detail: loss.approvedAtUtc
        ? `Approved by ${loss.approvedByName ?? 'an engineer'} · ${formatDateTime(loss.approvedAtUtc)}`
        : withEngineer
          ? loss.status === 'Investigating' ? 'Looking into it' : 'Waiting'
          : ended && loss.status === 'Cancelled' ? 'Rejected or cancelled' : '—',
      state: loss.approvedAtUtc ? ('done' as const) : withEngineer ? ('now' as const) : ('skipped' as const),
    },
    {
      title: 'Head of Biomedical',
      detail: awaiting
        ? 'Waiting for the final decision'
        : ended
          ? `${LOSS_STATUS_LABEL[loss.status]}${loss.closedByName ? ` by ${loss.closedByName}` : ''}`
          : 'Not yet',
      state: awaiting ? ('now' as const) : ended ? ('done' as const) : ('later' as const),
    },
  ];

  const mark = { done: '✓', now: '●', later: '○', skipped: '–' } as const;

  return (
    <ol className="row" style={{ listStyle: 'none', padding: 0, margin: 0, flexWrap: 'wrap', gap: '1rem' }} aria-label="Where it stands">
      {steps.map((s, i) => (
        <li key={s.title} className="card" style={{ flex: '1 1 14rem', opacity: s.state === 'later' ? 0.7 : 1 }}>
          <strong>
            <span aria-hidden="true">{mark[s.state]} </span>
            Step {i + 1}: {s.title}
          </strong>
          <div className="muted">{s.detail}</div>
        </li>
      ))}
    </ol>
  );
}

/** Marking the machine found: the day, and what was found out. An engineer's or the head's, at any point before it ends. */
function useFoundAction(loss: LossDetail, onDone: (m: string) => Promise<void>, onError: (m: string | null) => void) {
  const dialogs = useDialogs();
  const [busy, setBusy] = useState(false);

  async function found(body: { recoveredOn: string; findings?: string; correctiveAction?: string }) {
    const ok = await dialogs.confirm({
      title: `Mark ${loss.reference} as found?`,
      message: 'The machine goes back on the register as it was before it went missing. This record is then closed and cannot be changed.',
      confirmLabel: 'Yes, it was found',
      cancelLabel: 'Not yet',
    });
    if (!ok) return;
    onError(null);
    setBusy(true);
    try {
      await api.post(`/api/losses/${loss.id}/recover`, body);
      await onDone(`${loss.reference} is closed: the machine was found and is back on the register.`);
    } catch (err) {
      onError(err instanceof Error ? err.message : 'Could not save that.');
    } finally {
      setBusy(false);
    }
  }

  return { found, busy };
}

/** Rejecting a loss as not a loss (cancelling it), with the reason. The machine goes back on the register. */
function useCancelAction(loss: LossDetail, onDone: (m: string) => Promise<void>, onError: (m: string | null) => void) {
  const dialogs = useDialogs();
  const [busy, setBusy] = useState(false);

  async function cancel() {
    const reason = await dialogs.ask({
      title: `Reject ${loss.reference}?`,
      message: 'Use this when the machine was never lost. The machine goes back on the register as it was, and the record stays on the list as cancelled.',
      label: 'Why is it being rejected?',
      placeholder: 'e.g. It was found in the store; written up by mistake.',
      confirmLabel: 'Reject it',
      cancelLabel: 'Not now',
      multiline: true,
      minLength: 3,
      danger: true,
    });
    if (reason === null) return;
    onError(null);
    setBusy(true);
    try {
      await api.post(`/api/losses/${loss.id}/cancel`, { findings: reason });
      await onDone(`Rejected ${loss.reference}. The machine is back on the register as it was.`);
    } catch (err) {
      onError(err instanceof Error ? err.message : 'Could not save that.');
    } finally {
      setBusy(false);
    }
  }

  return { cancel, busy };
}

/**
 * Step 2, the biomedical engineer's: what is being done to find it, what was found out, and then approve it for the head,
 * mark it found, or reject it as not lost. Saved as often as needed.
 */
function EngineerForm({
  loss,
  onDone,
  onError,
}: {
  loss: LossDetail;
  onDone: (message: string) => Promise<void>;
  onError: (message: string | null) => void;
}) {
  const today = todayAtHospital();
  const [findings, setFindings] = useState(loss.findings ?? '');
  const [action, setAction] = useState(loss.correctiveAction ?? '');
  const [policeNumber, setPoliceNumber] = useState(loss.policeReportNumber ?? '');
  const [policeOn, setPoliceOn] = useState(loss.policeReportedOn ?? '');
  const [value, setValue] = useState(loss.estimatedValue?.toString() ?? '');
  const [foundOn, setFoundOn] = useState(today);
  const [busy, setBusy] = useState<'save' | 'approve' | null>(null);
  const dialogs = useDialogs();
  const foundAction = useFoundAction(loss, onDone, onError);
  const cancelAction = useCancelAction(loss, onDone, onError);

  const reviewBody = () => ({
    findings,
    correctiveAction: action,
    policeReportNumber: policeNumber,
    policeReportedOn: policeOn || null,
    estimatedValue: value.trim() ? Number(value) : null,
  });

  async function run(kind: 'save' | 'approve', call: () => Promise<unknown>, message: string) {
    onError(null);
    setBusy(kind);
    try {
      await call();
      await onDone(message);
    } catch (err) {
      onError(err instanceof Error ? err.message : 'Could not save that.');
    } finally {
      setBusy(null);
    }
  }

  function save(e: FormEvent) {
    e.preventDefault();
    void run('save', () => api.put(`/api/losses/${loss.id}/review`, reviewBody()), `Saved the review of ${loss.reference}.`);
  }

  async function approve() {
    const ok = await dialogs.confirm({
      title: `Approve ${loss.reference}?`,
      message: 'It goes to the head of Biomedical for the final decision. Until then you can still correct it.',
      confirmLabel: 'Yes, send it to the head',
      cancelLabel: 'Not yet',
    });
    if (!ok) return;
    void run('approve', () => api.post(`/api/losses/${loss.id}/approve`, reviewBody()), `Approved ${loss.reference}. It is now with the head for the final decision.`);
  }

  const any = busy !== null || foundAction.busy || cancelAction.busy;

  return (
    <form className="card stack" onSubmit={save}>
      <h2 className="section-h" style={{ margin: 0 }}>Step 2: your review</h2>

      {loss.decisionNotes && (
        <p className="alert alert-warn" role="status" style={{ margin: 0, whiteSpace: 'pre-wrap' }}>
          <strong>The head sent this back: </strong>
          {loss.decisionNotes}
        </p>
      )}

      <p className="muted" style={{ margin: 0 }}>
        Look for the machine and into how it went. Save as you go. When you have found out what happened, approve it for the head&apos;s
        final decision. If the machine was never lost, reject it.
      </p>

      <label className="field">
        <span>What was found out</span>
        <textarea
          rows={3}
          value={findings}
          maxLength={4000}
          onChange={(e) => setFindings(e.target.value)}
          placeholder="e.g. Checked every ward and the store. The sign-out book shows it left with a vendor engineer on Tuesday."
        />
      </label>

      <label className="field">
        <span>What is being done so it does not happen again (optional)</span>
        <textarea
          rows={3}
          value={action}
          maxLength={4000}
          onChange={(e) => setAction(e.target.value)}
          placeholder="e.g. Machines are signed in and out; the store is locked after the round."
        />
      </label>

      <div className="filters">
        <label className="field grow">
          <span>Police complaint (FIR) number</span>
          <input value={policeNumber} maxLength={100} onChange={(e) => setPoliceNumber(e.target.value)} />
        </label>
        <label className="field">
          <span>Day the police were told</span>
          <input type="date" value={policeOn} max={today} onChange={(e) => setPoliceOn(e.target.value)} />
        </label>
        <label className="field">
          <span>What it was worth, ₹</span>
          <input type="number" min={0} step="0.01" value={value} onChange={(e) => setValue(e.target.value)} />
        </label>
      </div>

      <div className="row">
        <button className="btn" type="submit" disabled={any}>
          {busy === 'save' ? 'Saving…' : loss.status === 'Reported' ? 'Start looking into it' : 'Save the review'}
        </button>
        <button
          className="btn btn-primary"
          type="button"
          disabled={any || !findings.trim()}
          title={findings.trim() ? undefined : 'Say what was found out first'}
          onClick={() => void approve()}
        >
          {busy === 'approve' ? 'Sending…' : 'Approve and send to the head'}
        </button>
      </div>

      <hr style={{ width: '100%', border: 0, borderTop: '1px solid var(--line, #d0d7de)' }} />

      <h3 className="section-h" style={{ margin: 0, fontSize: '1rem' }}>Or</h3>

      <div className="row" style={{ alignItems: 'flex-end', flexWrap: 'wrap' }}>
        <label className="field">
          <span>Day it was found</span>
          <input type="date" value={foundOn} min={loss.noticedOn} max={today} onChange={(e) => setFoundOn(e.target.value)} />
        </label>
        <button
          className="btn"
          type="button"
          disabled={any || !foundOn}
          onClick={() => void foundAction.found({ recoveredOn: foundOn, findings, correctiveAction: action })}
        >
          {foundAction.busy ? 'Saving…' : 'It was found'}
        </button>
        <button className="btn btn-quiet" type="button" disabled={any} onClick={() => void cancelAction.cancel()}>
          {cancelAction.busy ? 'Saving…' : 'Reject, not lost'}
        </button>
      </div>
    </form>
  );
}

/**
 * Step 3, the head of Biomedical's: the final decision on what an engineer has approved. Write it off (the machine is
 * not coming back, and it stays on the register as Lost), or send it back to the engineer with what is wanted. They can
 * also mark it found, or reject it as not lost.
 */
function HeadForm({
  loss,
  onDone,
  onError,
}: {
  loss: LossDetail;
  onDone: (message: string) => Promise<void>;
  onError: (message: string | null) => void;
}) {
  const today = todayAtHospital();
  const [notes, setNotes] = useState('');
  const [foundOn, setFoundOn] = useState(today);
  const [busy, setBusy] = useState<'writeoff' | 'back' | null>(null);
  const dialogs = useDialogs();
  const foundAction = useFoundAction(loss, onDone, onError);
  const cancelAction = useCancelAction(loss, onDone, onError);

  async function writeOff() {
    const ok = await dialogs.confirm({
      title: `Write off ${loss.reference}?`,
      message:
        'This is the final decision. The hospital accepts that the machine is not coming back. It stays on the register as Lost, with its history, and this record is closed for good.',
      confirmLabel: 'Yes, write it off',
      cancelLabel: 'Not yet',
      danger: true,
      acknowledge: 'I understand this cannot be undone.',
    });
    if (!ok) return;
    onError(null);
    setBusy('writeoff');
    try {
      await api.post(`/api/losses/${loss.id}/write-off`, { decisionNotes: notes });
      await onDone(`${loss.reference} is written off.`);
    } catch (err) {
      onError(err instanceof Error ? err.message : 'Could not save that.');
    } finally {
      setBusy(null);
    }
  }

  async function sendBack() {
    const reason = await dialogs.ask({
      title: `Send ${loss.reference} back to the engineer?`,
      message: 'It goes back to a biomedical engineer, who has to look into it again and approve it again before it comes to you.',
      label: 'What should the engineer do?',
      placeholder: 'e.g. Check the vendor’s gate pass records and the CCTV for Tuesday night.',
      confirmLabel: 'Send it back',
      cancelLabel: 'Not now',
      multiline: true,
      minLength: 3,
      initial: notes,
    });
    if (reason === null) return;
    onError(null);
    setBusy('back');
    try {
      await api.post(`/api/losses/${loss.id}/send-back`, { reason });
      await onDone(`Sent ${loss.reference} back to the engineer.`);
    } catch (err) {
      onError(err instanceof Error ? err.message : 'Could not save that.');
    } finally {
      setBusy(null);
    }
  }

  const any = busy !== null || foundAction.busy || cancelAction.busy;

  return (
    <section className="card stack">
      <h2 className="section-h" style={{ margin: 0 }}>Step 3: your final decision</h2>

      <div className="stack" style={{ gap: '0.5rem' }}>
        <p style={{ margin: 0, whiteSpace: 'pre-wrap' }}>
          <strong>What the engineer found out: </strong>
          {loss.findings ?? '—'}
        </p>
        <p style={{ margin: 0, whiteSpace: 'pre-wrap' }}>
          <strong>What is being done so it does not happen again: </strong>
          {loss.correctiveAction ?? '—'}
        </p>
        <p className="muted" style={{ margin: 0 }}>
          Approved by {loss.approvedByName ?? 'an engineer'}
          {loss.approvedAtUtc && ` on ${formatDateTime(loss.approvedAtUtc)}`}.
        </p>
      </div>

      <label className="field">
        <span>Your remarks (optional)</span>
        <textarea
          rows={2}
          value={notes}
          maxLength={4000}
          onChange={(e) => setNotes(e.target.value)}
          placeholder="Kept with the record and printed on the report."
        />
      </label>

      <div className="row" style={{ flexWrap: 'wrap' }}>
        <button className="btn btn-danger" type="button" disabled={any} onClick={() => void writeOff()}>
          {busy === 'writeoff' ? 'Saving…' : 'Write it off'}
        </button>
        <button className="btn" type="button" disabled={any} onClick={() => void sendBack()}>
          {busy === 'back' ? 'Saving…' : 'Send back to the engineer'}
        </button>
      </div>

      <hr style={{ width: '100%', border: 0, borderTop: '1px solid var(--line, #d0d7de)' }} />

      <div className="row" style={{ alignItems: 'flex-end', flexWrap: 'wrap' }}>
        <label className="field">
          <span>Day it was found</span>
          <input type="date" value={foundOn} min={loss.noticedOn} max={today} onChange={(e) => setFoundOn(e.target.value)} />
        </label>
        <button
          className="btn"
          type="button"
          disabled={any || !foundOn}
          onClick={() => void foundAction.found({ recoveredOn: foundOn })}
        >
          {foundAction.busy ? 'Saving…' : 'It was found'}
        </button>
        <button className="btn btn-quiet" type="button" disabled={any} onClick={() => void cancelAction.cancel()}>
          {cancelAction.busy ? 'Saving…' : 'Reject, not lost'}
        </button>
      </div>
    </section>
  );
}

/** What has been written so far, for everyone whose step it is not. An engineer can still mark it found while it waits for the head. */
function ReadOnlyReview({
  loss,
  onDone,
  onError,
  canMarkFound,
}: {
  loss: LossDetail;
  onDone: (message: string) => Promise<void>;
  onError: (message: string | null) => void;
  canMarkFound: boolean;
}) {
  const foundAction = useFoundAction(loss, onDone, onError);
  const [foundOn, setFoundOn] = useState(todayAtHospital());

  const waitingFor =
    loss.status === 'Reported' || loss.status === 'Investigating'
      ? 'a biomedical engineer, who will look into it'
      : loss.status === 'AwaitingDecision'
        ? 'the head of Biomedical, for the final decision'
        : null;

  return (
    <section className="card stack">
      <h2 className="section-h">Review</h2>

      {loss.decisionNotes && loss.status === 'Investigating' && (
        <p className="alert alert-warn" style={{ margin: 0, whiteSpace: 'pre-wrap' }}>
          <strong>The head sent this back to the engineer: </strong>
          {loss.decisionNotes}
        </p>
      )}

      {loss.findings || loss.correctiveAction || loss.recoveredOn ? (
        <>
          <p style={{ margin: 0, whiteSpace: 'pre-wrap' }}>
            <strong>{loss.status === 'Cancelled' ? 'Why it was rejected: ' : 'What was found out: '}</strong>
            {loss.findings ?? '—'}
          </p>
          {loss.status !== 'Cancelled' && (
            <p style={{ margin: 0, whiteSpace: 'pre-wrap' }}>
              <strong>What is being done so it does not happen again: </strong>
              {loss.correctiveAction ?? '—'}
            </p>
          )}
          {loss.decisionNotes && loss.status !== 'Investigating' && loss.status !== 'Cancelled' && (
            <p style={{ margin: 0, whiteSpace: 'pre-wrap' }}>
              <strong>The head’s remarks: </strong>
              {loss.decisionNotes}
            </p>
          )}
          {loss.recoveredOn && (
            <p style={{ margin: 0 }}>
              <strong>Found and recovered on: </strong>
              {formatDate(loss.recoveredOn)}
            </p>
          )}
        </>
      ) : (
        <p className="muted" style={{ margin: 0 }}>Not looked into yet.</p>
      )}

      {waitingFor && <p className="muted" style={{ margin: 0 }}>It is waiting for {waitingFor}.</p>}

      {loss.isFinal && loss.closedAtUtc && (
        <p className="muted" style={{ margin: 0 }}>
          {LOSS_STATUS_LABEL[loss.status]} {formatDateTime(loss.closedAtUtc)}
          {loss.closedByName && ` by ${loss.closedByName}`}.
        </p>
      )}

      {canMarkFound && (
        <div className="row" style={{ alignItems: 'flex-end', flexWrap: 'wrap' }}>
          <label className="field">
            <span>Day it was found</span>
            <input type="date" value={foundOn} min={loss.noticedOn} max={todayAtHospital()} onChange={(e) => setFoundOn(e.target.value)} />
          </label>
          <button className="btn" type="button" disabled={foundAction.busy || !foundOn} onClick={() => void foundAction.found({ recoveredOn: foundOn })}>
            {foundAction.busy ? 'Saving…' : 'It was found'}
          </button>
        </div>
      )}
    </section>
  );
}
