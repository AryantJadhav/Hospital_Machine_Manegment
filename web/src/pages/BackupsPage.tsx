import { useState } from 'react';
import { api } from '../api/client';
import { formatBytes } from '../bytes';

type RunResult = {
  status: number;
  fileName: string | null;
  sizeBytes: number | null;
  error: string | null;
  /** How sending it to Google Drive went. Null when Drive is switched off. */
  drive: { ran: boolean; sent: number; error: string | null; skipped: string | null } | null;
};

const SUCCEEDED = 20;

/** One button: take a backup now. */
export function BackupsPage() {
  const [running, setRunning] = useState(false);
  const [result, setResult] = useState<RunResult | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function runNow() {
    setRunning(true);
    setError(null);
    setResult(null);
    try {
      setResult(await api.post<RunResult>('/api/admin/backups/run', {}));
    } catch (e) {
      setError(e instanceof Error ? e.message : 'The backup could not be started.');
    } finally {
      setRunning(false);
    }
  }

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <h1>Backups</h1>
        </div>
        <button className="btn btn-primary" onClick={() => void runNow()} disabled={running}>
          {running ? 'Backing up…' : 'Back up now'}
        </button>
      </header>

      {error && <p className="alert alert-error" role="alert">{error}</p>}

      {result && result.status === SUCCEEDED && (
        <p className="alert alert-ok" role="status">
          Backup finished{result.fileName ? `: ${result.fileName}` : ''}
          {result.sizeBytes !== null ? ` (${formatBytes(result.sizeBytes)})` : ''}.
        </p>
      )}

      {result && result.status === SUCCEEDED && result.drive && (
        result.drive.error || result.drive.skipped ? (
          <p className="alert alert-error" role="alert">
            Not sent to Google Drive: {result.drive.error ?? result.drive.skipped}
          </p>
        ) : (
          <p className="alert alert-ok" role="status">
            {result.drive.sent > 0
              ? 'Sent to Google Drive.'
              : 'Already on Google Drive.'}
          </p>
        )
      )}

      {result && result.status !== SUCCEEDED && (
        <p className="alert alert-error" role="alert">
          The backup did not finish{result.error ? `: ${result.error}` : '.'}
        </p>
      )}
    </div>
  );
}
