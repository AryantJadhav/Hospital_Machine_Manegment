import { useCallback, useEffect, useState } from 'react';
import { api, ApiError } from '../api/client';
import { formatDateTime } from '../time';
import { StatusPill } from '../StatusPill';
import { formatBytes } from '../bytes';
import { BACKUP_LOOK } from '../statusTones';

type Run = {
  id: number;
  startedAtUtc: string;
  finishedAtUtc: string | null;
  status: number;
  trigger: number;
  fileName: string | null;
  sizeBytes: number | null;
  error: string | null;
  durationMs: number | null;
};

type Encryption = {
  /** Whether new backups are encrypted. */
  enabled: boolean;
  keyPresent: boolean;
  recoveryKeyCreatedAtUtc: string | null;
  /** Whether an administrator has said the recovery key is written down. */
  recoveryKeySaved: boolean;
};

type Drive = {
  enabled: boolean;
  /** Everything needed to send is in place. */
  ready: boolean;
  /** Why it is not, in words. */
  problem: string | null;
  folder: string | null;
  lastUploadAtUtc: string | null;
  lastAttemptAtUtc: string | null;
  lastError: string | null;
  uploaded: number;
  pending: number;
};

type Status = {
  runs: Run[];
  directory: string;
  retainCount: number;
  encryption: Encryption;
  drive: Drive;
  tool: { found: boolean; path: string | null; version: string | null; problem: string | null };
  lastSuccessAtUtc: string | null;
};

const STATUS: Record<number, string> = { 10: 'Running', 20: 'Succeeded', 30: 'Failed' };
const TRIGGER: Record<number, string> = { 10: 'Nightly', 20: 'Manual' };

/** Anything older than this and the hospital is not really backed up. */
const STALE_HOURS = 48;

export function BackupsPage() {
  const [data, setData] = useState<Status | null>(null);
  const [loading, setLoading] = useState(true);
  const [running, setRunning] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [restoring, setRestoring] = useState<number | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  // The recovery key, while it is on screen. Shown once and never fetched again, so it lives only here.
  const [shownKey, setShownKey] = useState<string | null>(null);
  const [writtenDown, setWrittenDown] = useState(false);
  const [keyBusy, setKeyBusy] = useState(false);
  // A backup file brought in from elsewhere, and the recovery key typed for it.
  const [chosen, setChosen] = useState<File | null>(null);
  const [fileKey, setFileKey] = useState('');
  const [needsKey, setNeedsKey] = useState(false);
  const [bringing, setBringing] = useState(false);
  const [downloading, setDownloading] = useState<number | null>(null);
  const [sendingToDrive, setSendingToDrive] = useState(false);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      setData(await api.get<Status>('/api/admin/backups'));
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not load backup status.');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  async function runNow() {
    setRunning(true);
    setError(null);
    try {
      await api.post('/api/admin/backups/run', {});
      await load();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'The backup could not be started.');
    } finally {
      setRunning(false);
    }
  }

  async function restore(run: Run) {
    // Typed, not clicked. This replaces every record in the system with
    // whatever is in that file, and a confirm dialog is one stray Enter away
    // from being dismissed.
    const typed = prompt(
      [
        `Restore from ${run.fileName}?`,
        '',
        'This REPLACES the current database with the contents of that backup.',
        'Anything recorded since it was taken will be lost.',
        '',
        'A copy of the current database is saved first, so this can be undone.',
        '',
        'Type RESTORE to continue:',
      ].join('\n'),
    );

    if (typed !== 'RESTORE') return;

    setRestoring(run.id);
    setError(null);
    setNotice(null);
    try {
      setNotice(await startRestore(run.id, null));
    } catch (e) {
      // Made on another machine, or with a key that is gone from this one: the recovery key opens it.
      if (e instanceof ApiError && (e.body as { needsRecoveryKey?: boolean } | undefined)?.needsRecoveryKey) {
        const typedKey = prompt(
          [
            'This backup cannot be opened with this machine\'s own key.',
            'It was made on another machine, or the key here has been replaced.',
            '',
            'Enter the recovery key written down for this installation:',
          ].join('\n'),
        );

        if (typedKey) {
          try {
            setNotice(await startRestore(run.id, typedKey));
          } catch (again) {
            setError(again instanceof Error ? again.message : 'The restore could not be started.');
          }
        }
      } else {
        setError(e instanceof Error ? e.message : 'The restore could not be started.');
      }
    } finally {
      setRestoring(null);
    }
  }

  /** Sends the backups that have not gone to the drive yet, and says what happened. */
  async function sendToDrive() {
    setSendingToDrive(true);
    setError(null);
    setNotice(null);
    try {
      const res = await api.post<{ ran: boolean; sent: number; error: string | null; skipped: string | null }>(
        '/api/admin/backups/drive/sync', {});
      if (res.error) setError(`The upload to Google Drive did not finish: ${res.error}`);
      else if (res.skipped) setNotice(res.skipped);
      else setNotice(res.sent === 0 ? 'Everything is already on Google Drive.' : `Sent ${res.sent} backup${res.sent === 1 ? '' : 's'} to Google Drive.`);
      await load();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not send to Google Drive.');
    } finally {
      setSendingToDrive(false);
    }
  }

  /** Saves one backup file. It stays encrypted: the file is no use to anyone without the keys. */
  async function download(run: Run) {
    setDownloading(run.id);
    setError(null);
    setNotice(null);
    try {
      // The browser saves the file itself, straight to disk, from a one-use link: a backup can be far too large
      // to be held in the page and handed back.
      const made = await api.post<{ url: string; fileName: string }>(`/api/admin/backups/${run.id}/download-link`, {});
      const link = document.createElement('a');
      link.href = made.url;
      link.download = made.fileName;
      link.click();
      setNotice(`Saving ${made.fileName}. It is encrypted: keep it with the recovery key, but not in the same place.`);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not start the download.');
    } finally {
      setDownloading(null);
    }
  }

  /**
   * Restores from a file chosen on this computer: the way back after the machine is lost. The file is sent here,
   * checked (it must be a whole, untouched backup that opens with this machine's key or the recovery key), and then
   * restored exactly like one of this machine's own.
   */
  async function restoreFromFile() {
    if (!chosen) return;

    const typed = prompt(
      [
        `Restore from ${chosen.name}?`,
        '',
        'This REPLACES the current database with the contents of that file.',
        'Anything recorded here since it was taken will be lost.',
        '',
        'A copy of the current database is saved first, so this can be undone.',
        '',
        'Type RESTORE to continue:',
      ].join('\n'),
    );

    if (typed !== 'RESTORE') return;

    setBringing(true);
    setError(null);
    setNotice(null);
    try {
      const key = fileKey.trim();
      const kept = await api.uploadFile<{ id: number }>('/api/admin/backups/upload', chosen, {
        'X-File-Name': encodeURIComponent(chosen.name),
        ...(key ? { 'X-Recovery-Key': key } : {}),
      });
      setNotice(await startRestore(kept.id, key || null));
      setChosen(null);
      setFileKey('');
      setNeedsKey(false);
    } catch (e) {
      if (e instanceof ApiError && (e.body as { needsRecoveryKey?: boolean } | undefined)?.needsRecoveryKey) {
        setNeedsKey(true);
        setError('This file was made on another machine. Type the recovery key written down for it below, then try again.');
      } else {
        setError(e instanceof Error ? e.message : 'The file could not be restored.');
      }
    } finally {
      setBringing(false);
    }
  }

  async function startRestore(id: number, recoveryKey: string | null): Promise<string> {
    const result = await api.post<{ message: string }>(
      `/api/admin/backups/${id}/restore`, { confirm: 'RESTORE', recoveryKey });
    return result.message;
  }

  /** Makes a new recovery key. It comes back once, here, and is never kept anywhere that can be read. */
  async function makeRecoveryKey() {
    setKeyBusy(true);
    setError(null);
    setNotice(null);
    try {
      const made = await api.post<{ recoveryKey: string }>('/api/admin/backups/encryption/recovery-key', {});
      setShownKey(made.recoveryKey);
      setWrittenDown(false);
      await load();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not make a recovery key.');
    } finally {
      setKeyBusy(false);
    }
  }

  async function confirmWrittenDown() {
    setKeyBusy(true);
    setError(null);
    try {
      await api.post('/api/admin/backups/encryption/recovery-key/saved', {});
      setShownKey(null);
      setNotice('Recorded that the recovery key is written down. It will not be shown again; if it is lost, make a new one.');
      await load();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not record that.');
    } finally {
      setKeyBusy(false);
    }
  }

  function downloadKey(key: string) {
    const text = [
      'Hospital PM - backup recovery key',
      '',
      key,
      '',
      `Made: ${new Date().toISOString()}`,
      '',
      'This key opens the encrypted backups of this installation if the machine is lost.',
      'Keep it away from the machine and away from the backups: a printed copy in a safe, or a password manager.',
      'Anyone who has it and a backup file can read the hospital\'s records. It is shown once; to replace it, make a new one on the Backups page.',
      '',
    ].join('\r\n');
    const url = URL.createObjectURL(new Blob([text], { type: 'text/plain' }));
    const link = document.createElement('a');
    link.href = url;
    link.download = 'hospitalpm-recovery-key.txt';
    link.click();
    URL.revokeObjectURL(url);
  }

  if (loading) return <div className="page"><p className="muted">Loading…</p></div>;
  if (!data) {
    return (
      <div className="page">
        <p className="alert alert-error" role="alert">{error ?? 'Could not load backup status.'}</p>
      </div>
    );
  }

  const lastSuccess = data.lastSuccessAtUtc ? new Date(data.lastSuccessAtUtc) : null;
  const hoursSince = lastSuccess
    ? (Date.now() - lastSuccess.getTime()) / 3_600_000
    : null;
  const stale = hoursSince === null || hoursSince > STALE_HOURS;

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <h1>Backups</h1>
          <p className="muted">
            The database is dumped every night at 02:30 IST and each dump is read back to confirm it
            opens.
          </p>
        </div>
        <button className="btn btn-primary" onClick={() => void runNow()} disabled={running}>
          {running ? 'Backing up…' : 'Back up now'}
        </button>
      </header>

      {error && <p className="alert alert-error" role="alert">{error}</p>}
      {notice && <p className="alert alert-ok" role="status">{notice}</p>}

      {/* The banner a hospital actually needs. Silence is the failure mode:
          nobody notices backups stopped until the day they are wanted. */}
      <p className={`alert ${stale ? 'alert-error' : 'alert-ok'}`}>
        {lastSuccess
          ? stale
            ? `Last successful backup was ${formatAge(hoursSince!)} ago. That is longer than it should be — check the most recent run below.`
            : `Last successful backup ${formatAge(hoursSince!)} ago, on ${formatDateTime(data.lastSuccessAtUtc)}.`
          : 'No backup has ever succeeded on this installation.'}
      </p>

      {!data.tool.found && (
        <p className="alert alert-error" role="alert">
          <strong>pg_dump was not found.</strong> {data.tool.problem} Until this is fixed, no
          backup can run.
        </p>
      )}

      <EncryptionCard
        encryption={data.encryption}
        busy={keyBusy}
        shownKey={shownKey}
        writtenDown={writtenDown}
        onWrittenDown={setWrittenDown}
        onMake={() => void makeRecoveryKey()}
        onConfirm={() => void confirmWrittenDown()}
        onDownload={downloadKey}
        onCopied={() => setNotice('Copied. Paste it somewhere safe, away from this machine.')}
      />

      <DriveCard drive={data.drive} busy={sendingToDrive} onSend={() => void sendToDrive()} />

      <div className="card">
        <h2 className="section-h">Where backups are kept</h2>
        <dl className="detail">
          <dt>Folder</dt>
          <dd className="mono">{data.directory}</dd>
          <dt>Kept</dt>
          <dd>{data.retainCount} most recent</dd>
          <dt>pg_dump</dt>
          <dd className="mono">
            {data.tool.found
              ? `${data.tool.path}${data.tool.version ? ` (v${data.tool.version})` : ''}`
              : <span className="muted">not found</span>}
          </dd>
        </dl>
        <p className="muted" style={{ marginBottom: 0 }}>
          Copy this folder somewhere off this machine. A backup that lives only on the server it
          came from does not survive the server.
          {data.encryption.enabled && ' The files are encrypted, so a copy is useless to anyone without the recovery key.'}
        </p>
      </div>

      <div className="card">
        <h2 className="section-h">Restore from a file</h2>
        <p className="muted">
          For when this machine is new, or its data is gone. Choose a backup file you saved earlier (the ones named
          <span className="mono"> hospitalpm-…dump.enc</span>). Every record comes back, with its photos and uploaded
          reports, each still attached to the same machine, work order or PM. Reports and printouts are not in the
          backup: the program makes them again from the records.
        </p>
        <div style={{ display: 'grid', gap: '0.75rem', maxWidth: '32rem', marginBottom: '0.75rem' }}>
          <label className="field" htmlFor="backup-file">
            <span>Backup file</span>
            <input
              id="backup-file"
              type="file"
              accept=".enc"
              disabled={bringing}
              onChange={(e) => {
                setChosen(e.target.files?.[0] ?? null);
                setNeedsKey(false);
              }}
            />
          </label>
          <label className="field" htmlFor="backup-file-key">
            <span>Recovery key{needsKey ? '' : ' (only if the backup was made on another machine)'}</span>
            <input
              id="backup-file-key"
              type="text"
              autoComplete="off"
              spellCheck={false}
              placeholder="XXXX-XXXX-XXXX-…"
              value={fileKey}
              disabled={bringing}
              aria-invalid={needsKey && !fileKey.trim()}
              onChange={(e) => setFileKey(e.target.value)}
            />
          </label>
        </div>
        <button
          className="btn btn-primary"
          disabled={!chosen || bringing || restoring !== null}
          onClick={() => void restoreFromFile()}
        >
          {bringing ? 'Sending the file and restoring…' : 'Restore from this file'}
        </button>
      </div>

      <div className="card table-wrap">
        <h2 className="section-h">Recent runs</h2>
        <table className="table">
          <thead>
            <tr>
              <th>Started</th>
              <th>Result</th>
              <th>Trigger</th>
              <th>File</th>
              <th>Size</th>
              <th>Took</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {data.runs.length === 0 && (
              <tr><td colSpan={7} className="empty">No backup has run yet.</td></tr>
            )}

            {data.runs.map((r) => (
              <tr key={r.id}>
                <td>{formatDateTime(r.startedAtUtc)}</td>
                <td>
                  <StatusPill look={BACKUP_LOOK[r.status]}>{STATUS[r.status] ?? '—'}</StatusPill>
                  {r.error && <div className="hist-fault">{r.error}</div>}
                </td>
                <td>{TRIGGER[r.trigger] ?? '—'}</td>
                <td className="mono">
                  {r.fileName ?? <span className="muted">—</span>}
                  {r.fileName?.endsWith('.enc') && <div className="muted" style={{ fontFamily: 'inherit' }}>Encrypted</div>}
                </td>
                <td>{r.sizeBytes !== null ? formatBytes(r.sizeBytes) : <span className="muted">—</span>}</td>
                <td>{r.durationMs !== null ? formatDuration(r.durationMs) : <span className="muted">—</span>}</td>
                <td>
                  {r.status === 20 && r.fileName && (
                    <>
                      {r.fileName.endsWith('.enc') && (
                        <button
                          className="btn btn-quiet"
                          disabled={downloading !== null}
                          onClick={() => void download(r)}
                        >
                          {downloading === r.id ? 'Starting…' : 'Download'}
                        </button>
                      )}
                      <button
                        className="btn btn-quiet"
                        disabled={restoring !== null}
                        onClick={() => void restore(r)}
                      >
                        {restoring === r.id ? 'Starting…' : 'Restore'}
                      </button>
                    </>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

/**
 * Copies of the backups on Google Drive. Off unless it has been switched on, and always optional: a hospital with no
 * internet simply never sees a problem here. Only the encrypted files are sent, so what is on the drive is useless
 * without the keys; the page says what has gone, what is waiting and what the last error was.
 */
function DriveCard({ drive, busy, onSend }: { drive: Drive; busy: boolean; onSend: () => void }) {
  if (!drive.enabled) {
    return (
      <div className="card">
        <h2 className="section-h">Google Drive</h2>
        <p className="muted" style={{ margin: 0 }}>
          Off. Backups stay on this machine. Switch it on with <span className="mono">Backup:Drive:Enabled</span> and an
          internet connection, and the encrypted backups are copied to Google Drive each night.
        </p>
      </div>
    );
  }

  return (
    <div className="card stack">
      <h2 className="section-h" style={{ marginBottom: 0 }}>Google Drive</h2>

      {!drive.ready && drive.problem && <p className="alert alert-error" role="alert">{drive.problem}</p>}
      {drive.lastError && (
        <p className="alert alert-error" role="alert">
          The last upload did not finish: {drive.lastError} It tries again after the next backup, or press Upload now.
        </p>
      )}

      <dl className="detail">
        <dt>Sent</dt>
        <dd>{drive.uploaded} of the backups kept here{drive.pending > 0 ? `, ${drive.pending} waiting` : ''}</dd>
        <dt>Last upload</dt>
        <dd>{drive.lastUploadAtUtc ? formatDateTime(drive.lastUploadAtUtc) : <span className="muted">None yet</span>}</dd>
        {drive.folder && (
          <>
            <dt>Folder</dt>
            <dd className="mono">{drive.folder}</dd>
          </>
        )}
      </dl>

      <div className="row">
        <button className="btn btn-primary" disabled={!drive.ready || busy} onClick={onSend}>
          {busy ? 'Uploading…' : 'Upload now'}
        </button>
      </div>
    </div>
  );
}

/**
 * Keeps the backup files private, and makes sure the way back in is written down.
 *
 * Backups are encrypted with a key held on this machine, so this machine restores its own without anyone typing
 * anything. The recovery key is what opens them if the machine is gone. It is shown once, when it is made, and
 * the page does not let it be forgotten: until an administrator says it is written down, this is a warning.
 */
function EncryptionCard({
  encryption,
  busy,
  shownKey,
  writtenDown,
  onWrittenDown,
  onMake,
  onConfirm,
  onDownload,
  onCopied,
}: {
  encryption: Encryption;
  busy: boolean;
  shownKey: string | null;
  writtenDown: boolean;
  onWrittenDown: (value: boolean) => void;
  onMake: () => void;
  onConfirm: () => void;
  onDownload: (key: string) => void;
  onCopied: () => void;
}) {
  if (!encryption.enabled) {
    return (
      <div className="card">
        <h2 className="section-h">Keeping backups private</h2>
        <p className="alert alert-error" role="alert" style={{ marginBottom: 0 }}>
          <strong>Backups are not encrypted.</strong> Encryption has been switched off in this installation&apos;s
          settings, so each backup file holds the hospital&apos;s records in the clear. Anyone who copies one can read it.
        </p>
      </div>
    );
  }

  const keyDate = encryption.recoveryKeyCreatedAtUtc ? formatDateTime(encryption.recoveryKeyCreatedAtUtc) : null;

  return (
    <div className="card stack">
      <h2 className="section-h" style={{ margin: 0 }}>Keeping backups private</h2>

      {shownKey ? (
        <div className="stack" role="region" aria-label="The recovery key">
          <p className="alert alert-warn" style={{ margin: 0 }}>
            <strong>Write this down now. It is shown once.</strong> Keep it away from this machine and away from the
            backups: printed and in a safe, or in a password manager. With it, and a backup file, the backup can be
            opened if this machine is lost. Anyone else who has both can read the hospital&apos;s records.
          </p>

          <p className="mono" style={{ fontSize: '1.25rem', letterSpacing: '0.05em', margin: 0, overflowWrap: 'anywhere' }} aria-label="Recovery key">
            {shownKey}
          </p>

          <div className="row">
            <button
              className="btn"
              onClick={() => {
                void navigator.clipboard?.writeText(shownKey).then(onCopied, () => undefined);
              }}
            >
              Copy
            </button>
            <button className="btn" onClick={() => onDownload(shownKey)}>Download as a text file</button>
            <button className="btn" onClick={() => window.print()}>Print</button>
          </div>

          <label className="row" style={{ gap: '0.5rem', alignItems: 'center' }}>
            <input type="checkbox" checked={writtenDown} onChange={(e) => onWrittenDown(e.target.checked)} />
            I have written it down and keep it away from this machine
          </label>

          <div>
            <button className="btn btn-primary" disabled={!writtenDown || busy} onClick={onConfirm}>
              {busy ? 'Saving…' : 'I have saved it'}
            </button>
          </div>
        </div>
      ) : encryption.recoveryKeySaved ? (
        <>
          <p className="alert alert-ok" style={{ margin: 0 }}>
            <strong>Backups are encrypted.</strong> The recovery key{keyDate ? ` made ${keyDate}` : ''} is written down.
          </p>
          <p className="muted" style={{ margin: 0 }}>
            If it is lost, or someone who should not have it has seen it, make a new one. Every backup on this machine
            is updated to the new key, and the old one stops working.
          </p>
          <div>
            <button className="btn btn-quiet" disabled={busy} onClick={onMake}>
              {busy ? 'Making…' : 'Make a new recovery key'}
            </button>
          </div>
        </>
      ) : (
        <>
          <p className="alert alert-warn" role="alert" style={{ margin: 0 }}>
            <strong>Backups are encrypted, but the recovery key has not been written down.</strong> Without it, a backup
            cannot be opened if this machine is lost, and a backup that cannot be opened is not a backup.
          </p>
          <div>
            <button className="btn btn-primary" disabled={busy} onClick={onMake}>
              {busy ? 'Making…' : 'Create the recovery key'}
            </button>
          </div>
        </>
      )}
    </div>
  );
}

function formatAge(hours: number): string {
  if (hours < 1) return plural(Math.max(1, Math.round(hours * 60)), 'minute');
  if (hours < 48) return plural(Math.round(hours), 'hour');
  return plural(Math.round(hours / 24), 'day');
}

function plural(n: number, unit: string): string {
  return `${n} ${unit}${n === 1 ? '' : 's'}`;
}

function formatDuration(ms: number): string {
  if (ms < 1000) return `${ms} ms`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(1)} s`;
  return `${Math.round(ms / 60_000)} min`;
}
