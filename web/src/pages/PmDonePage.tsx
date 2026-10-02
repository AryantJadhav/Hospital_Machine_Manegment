import { useRef, useState } from 'react';
import type { FormEvent } from 'react';
import { api } from '../api/client';
import { useAuth } from '../auth/useAuth';
import { formatDate, formatDateTime } from '../time';
import { StatusPill } from '../StatusPill';
import { formatBytes } from '../bytes';

/**
 * Recording that a PM was done, on its own address: /pm/:taskId/do.
 *
 * A PM is scheduled and then recorded as done: which day, by whom, any notes, and the report as a
 * PDF or photos if there is one. There is no checklist to fill in and no signature to draw. It is
 * the same for the hospital's own team and for the maintenance contract vendor (where a machine has
 * an AMC or a CMC), whose work somebody from the department records on their behalf.
 *
 * The report often arrives after the visit, so a PM can be recorded first and the file added
 * later, and it can be added to, as a report photographed in pieces arrives in pieces.
 */

export type ReportFile = {
  id: number;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  uploadedBy: string | null;
  uploadedAtUtc: string;
};

export type RecordPm = {
  simple: true;
  // 10 our own team, 20 the maintenance contract vendor.
  performedBy: number;
  taskId: number;
  status: number;
  dueDate: string;
  equipmentId: number;
  assetTag: string;
  equipmentTypeName: string;
  locationName: string;
  checklistName: string;
  vendorName: string | null;
  contractType: number | null;
  contractNumber: string | null;
  today: string;
  completion: {
    performedOn: string | null;
    doneBy: string | null;
    notes: string | null;
    recordedBy: string | null;
    completedAtUtc: string;
  } | null;
  files: ReportFile[];
};

const CONTRACT: Record<number, string> = { 10: 'AMC', 20: 'CMC' };

/** The same limits the server enforces, so a mistake is caught before a large upload. */
const MAX_BYTES = 10 * 1024 * 1024;
const MAX_FILES = 10;
const ACCEPT = 'application/pdf,image/jpeg,image/png,image/webp,.pdf,.jpg,.jpeg,.png,.webp';

/** Said once, wherever a file can be chosen: a report is a paper about a machine, never a person. */
function PatientNotice() {
  return (
    <p className="muted" style={{ margin: 0 }}>
      PDF, JPEG, PNG or WebP, up to 10 MB each. This system holds no patient information, so do not
      upload anything that shows a patient or a patient&apos;s details.
    </p>
  );
}

/** The files chosen so far, with a button to add more and to drop one. */
function FilePicker({
  files,
  onChange,
  onError,
  room,
}: {
  files: File[];
  onChange: (next: File[]) => void;
  onError: (message: string | null) => void;
  room: number;
}) {
  const input = useRef<HTMLInputElement>(null);

  function add(list: FileList | null) {
    if (!list || list.length === 0) return;
    const next = [...files];

    for (const f of Array.from(list)) {
      if (f.size === 0) {
        onError(`"${f.name}" is empty.`);
        continue;
      }
      if (f.size > MAX_BYTES) {
        onError(`"${f.name}" is larger than 10 MB.`);
        continue;
      }
      if (next.length >= room) {
        onError(`A PM can have at most ${MAX_FILES} report files.`);
        break;
      }
      onError(null);
      next.push(f);
    }

    onChange(next);
    // So the same file can be chosen again after it has been removed.
    if (input.current) input.current.value = '';
  }

  return (
    <div className="stack">
      <input
        ref={input}
        type="file"
        className="field"
        multiple
        accept={ACCEPT}
        aria-label="Choose report files"
        onChange={(e) => add(e.target.files)}
      />
      {files.length > 0 && (
        <ul className="stack" style={{ listStyle: 'none', margin: 0, padding: 0, gap: '0.25rem' }}>
          {files.map((f, i) => (
            <li key={`${f.name}-${i}`} className="row" style={{ justifyContent: 'space-between', gap: '0.75rem' }}>
              <span>{f.name} <span className="muted">· {formatBytes(f.size)}</span></span>
              <button
                type="button"
                className="btn btn-quiet"
                aria-label={`Remove ${f.name}`}
                onClick={() => onChange(files.filter((_, j) => j !== i))}
              >
                Remove
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

export function PmDonePage({
  data,
  canAdminister,
  canRemoveFiles,
  backLabel,
  onClose,
  onDone,
  onChanged,
}: {
  data: RecordPm;
  /** May record that this PM will not happen. */
  canAdminister: boolean;
  /** May remove an uploaded report file. */
  canRemoveFiles: boolean;
  backLabel: string;
  onClose: () => void;
  onDone: (message: string, completed: boolean, task: { id: number; assetTag: string; dueDate: string }) => void;
  /** The page's data changed (a file was added or removed) and should be read again. */
  onChanged: () => Promise<void>;
}) {
  const open = data.status === 10 || data.status === 20 || data.status === 30;
  const done = data.status === 40;

  const [performedOn, setPerformedOn] = useState(data.today);
  const { user } = useAuth();
  const byVendor = data.performedBy === 20;
  // Whoever is signed in is usually the one who did the work, so it starts as their name; it stays
  // editable for someone recording it on another's behalf. The vendor's engineer starts empty.
  const [doneBy, setDoneBy] = useState(byVendor ? '' : user?.fullName ?? '');
  const [notes, setNotes] = useState('');
  const [picked, setPicked] = useState<File[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [skipping, setSkipping] = useState(false);
  const [skipReason, setSkipReason] = useState('');

  const contract = data.contractType ? CONTRACT[data.contractType] ?? 'Contract' : null;
  const task = { id: data.taskId, assetTag: data.assetTag, dueDate: data.dueDate };

  async function record(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setBusy(true);
    try {
      const form = new FormData();
      form.append('performedOn', performedOn);
      form.append('doneBy', doneBy.trim());
      form.append('notes', notes.trim());
      picked.forEach((f) => form.append('files', f, f.name));

      await api.postForm(`/api/pm/tasks/${data.taskId}/done`, form);
      onDone(
        `Recorded that the PM on ${data.assetTag} was done${byVendor ? ` by ${data.vendorName ?? 'the vendor'}` : ''}. ${
          picked.length > 0 ? 'The report is saved.' : byVendor ? 'The report has not been saved yet.' : ''
        }`.trim(),
        // Nothing to certify: there was no checklist. The record is who did it, when, and the report.
        false,
        task,
      );
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not record this PM.');
    } finally {
      setBusy(false);
    }
  }

  async function addFiles() {
    if (picked.length === 0) return;
    setError(null);
    setBusy(true);
    try {
      const form = new FormData();
      picked.forEach((f) => form.append('files', f, f.name));
      await api.postForm(`/api/pm/tasks/${data.taskId}/attachments`, form);
      setPicked([]);
      await onChanged();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not save the files.');
    } finally {
      setBusy(false);
    }
  }

  async function removeFile(f: ReportFile) {
    if (!window.confirm(`Remove "${f.fileName}"? It is deleted from the system. The removal is recorded in the audit log.`)) {
      return;
    }
    setError(null);
    try {
      await api.del(`/api/pm/attachments/${f.id}`);
      await onChanged();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not remove the file.');
    }
  }

  async function skip() {
    setError(null);
    setBusy(true);
    try {
      await api.post(`/api/pm/tasks/${data.taskId}/skip`, { reason: skipReason.trim() });
      onDone(`Skipped the PM on ${data.assetTag}.`, false, task);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not skip this PM.');
      setBusy(false);
    }
  }

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <h1>{byVendor ? 'PM done by the maintenance contract vendor' : 'Mark this PM as done'}</h1>
          <p className="muted">
            <span className="mono">{data.assetTag}</span> · {data.equipmentTypeName} · {data.locationName}
            {' · due '}{formatDate(data.dueDate)}
          </p>
        </div>
        <button className="btn" onClick={onClose}>Back to {backLabel}</button>
      </header>

      {error && <p className="alert alert-error" role="alert">{error}</p>}

      <div className="card stack">
        <dl className="detail">
          <dt>Done by</dt>
          <dd>
            {byVendor
              ? `${data.vendorName ?? 'The vendor'}${contract ? ` (${contract}${data.contractNumber ? ` ${data.contractNumber}` : ''})` : ''}`
              : 'Our own team'}
          </dd>
          {data.status === 50 && (
            <>
              <dt>Status</dt>
              <dd><StatusPill tone="neutral">Skipped</StatusPill></dd>
            </>
          )}
        </dl>
      </div>

      {open && (
        <form className="card stack" onSubmit={record}>
          <h2 style={{ margin: 0, fontSize: '1.05rem' }}>{byVendor ? 'Record that the vendor did it' : 'Record that it was done'}</h2>

          <div style={{ display: 'grid', gap: '0.75rem', gridTemplateColumns: 'repeat(auto-fit, minmax(12rem, 1fr))' }}>
            <label className="stack">
              <span>Day it was done</span>
              <input
                className="field"
                type="date"
                required
                max={data.today}
                value={performedOn}
                onChange={(e) => setPerformedOn(e.target.value)}
              />
            </label>

            <label className="stack">
              <span>{byVendor ? "Vendor's engineer" : 'Done by'}</span>
              <input
                className="field"
                maxLength={200}
                placeholder={byVendor ? 'Name, if it is on the report' : 'Who did the PM'}
                value={doneBy}
                onChange={(e) => setDoneBy(e.target.value)}
              />
            </label>
          </div>

          <label className="stack">
            <span>Notes</span>
            <input
              className="field"
              maxLength={2000}
              placeholder={byVendor ? 'Anything the vendor said that is not on the report' : 'Anything worth remembering about this PM'}
              value={notes}
              onChange={(e) => setNotes(e.target.value)}
            />
          </label>

          <div className="stack">
            <span>Report or photos (optional)</span>
            <FilePicker files={picked} onChange={setPicked} onError={setError} room={MAX_FILES} />
            <PatientNotice />
            <span className="muted">
              A report can also be added afterwards, if it has not arrived yet.
            </span>
          </div>

          <div style={{ display: 'flex', gap: '0.5rem' }}>
            <button className="btn btn-primary" disabled={busy}>
              {busy ? 'Saving…' : picked.length > 0 ? 'Record and save the report' : 'Mark as done'}
            </button>
            <button type="button" className="btn" onClick={onClose} disabled={busy}>Cancel</button>
          </div>
        </form>
      )}

      {done && data.completion && (
        <div className="card stack">
          <h2 style={{ margin: 0, fontSize: '1.05rem' }}>
            <StatusPill tone="success">{byVendor ? 'Done by the vendor' : 'Done'}</StatusPill>
          </h2>
          <dl className="detail">
            <dt>Done on</dt>
            <dd>{data.completion.performedOn ? formatDate(data.completion.performedOn) : '—'}</dd>
            <dt>{byVendor ? "Vendor's engineer" : 'Done by'}</dt>
            <dd>{data.completion.doneBy ?? '—'}</dd>
            <dt>Recorded by</dt>
            <dd>{data.completion.recordedBy ?? '—'} · {formatDateTime(data.completion.completedAtUtc)}</dd>
            {data.completion.notes && (
              <>
                <dt>Notes</dt>
                <dd>{data.completion.notes}</dd>
              </>
            )}
          </dl>
        </div>
      )}

      {done && (
        <div className="card stack">
          <h2 style={{ margin: 0, fontSize: '1.05rem' }}>Report</h2>

          {data.files.length === 0 && (
            <p className="alert alert-info">No report has been saved for this PM. Add one below if there is one.</p>
          )}

          {data.files.length > 0 && (
            <ul className="stack" style={{ listStyle: 'none', margin: 0, padding: 0, gap: '0.5rem' }}>
              {data.files.map((f) => (
                <li key={f.id} className="row" style={{ justifyContent: 'space-between', gap: '0.75rem', flexWrap: 'wrap' }}>
                  <span>
                    <strong>{f.fileName}</strong>
                    <span className="muted"> · {formatBytes(f.sizeBytes)} ·{f.uploadedBy ?? 'unknown'}, {formatDateTime(f.uploadedAtUtc)}</span>
                  </span>
                  <span className="row" style={{ gap: '0.5rem' }}>
                    <button className="btn" onClick={() => void api.view(`/api/pm/attachments/${f.id}`).catch((err: unknown) => setError(err instanceof Error ? err.message : 'Could not open the file.'))}>
                      Open
                    </button>
                    <button
                      className="btn btn-quiet"
                      onClick={() => void api.download(`/api/pm/attachments/${f.id}`, f.fileName).catch((err: unknown) => setError(err instanceof Error ? err.message : 'Could not download the file.'))}
                    >
                      Download
                    </button>
                    {canRemoveFiles && (
                      <button className="btn btn-quiet" aria-label={`Remove ${f.fileName}`} onClick={() => void removeFile(f)}>
                        Remove
                      </button>
                    )}
                  </span>
                </li>
              ))}
            </ul>
          )}

          {data.files.length < MAX_FILES && (
            <div className="stack">
              <span>Add to the report</span>
              <FilePicker files={picked} onChange={setPicked} onError={setError} room={MAX_FILES - data.files.length} />
              <PatientNotice />
              <div>
                <button className="btn btn-primary" disabled={busy || picked.length === 0} onClick={() => void addFiles()}>
                  {busy ? 'Saving…' : 'Save the files'}
                </button>
              </div>
            </div>
          )}
        </div>
      )}

      {open && canAdminister && (
        <div className="card stack">
          {!skipping ? (
            <button className="btn btn-quiet" onClick={() => setSkipping(true)}>This PM will not happen…</button>
          ) : (
            <>
              <label className="stack">
                <span>Why will it not happen?</span>
                <input
                  className="field"
                  maxLength={500}
                  value={skipReason}
                  onChange={(e) => setSkipReason(e.target.value)}
                />
                <span className="muted">Skipping is a permanent gap in the maintenance record, so it needs a reason.</span>
              </label>
              <div style={{ display: 'flex', gap: '0.5rem' }}>
                <button className="btn btn-primary" disabled={busy || skipReason.trim().length === 0} onClick={() => void skip()}>
                  Skip this PM
                </button>
                <button className="btn" onClick={() => setSkipping(false)} disabled={busy}>Cancel</button>
              </div>
            </>
          )}
        </div>
      )}
    </div>
  );
}
