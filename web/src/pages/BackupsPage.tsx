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

type Status = {
  runs: Run[];
  directory: string;
  retainCount: number;
  encryption: Encryption;
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
                    <button
                      className="btn btn-quiet"
                      disabled={restoring !== null}
                      onClick={() => void restore(r)}
                    >
                      {restoring === r.id ? 'Starting…' : 'Restore'}
                    </button>
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
