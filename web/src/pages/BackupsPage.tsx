import { useCallback, useEffect, useState } from 'react';
import { api } from '../api/client';

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

type Status = {
  runs: Run[];
  directory: string;
  retainCount: number;
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

  if (loading) return <div className="page"><p className="muted">Loading…</p></div>;
  if (!data) {
    return (
      <div className="page">
        <p className="alert alert-error">{error ?? 'Could not load backup status.'}</p>
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
            The database is dumped nightly at 02:30 UTC and each dump is read back to confirm it
            opens.
          </p>
        </div>
        <button className="btn btn-primary" onClick={() => void runNow()} disabled={running}>
          {running ? 'Backing up…' : 'Back up now'}
        </button>
      </header>

      {error && <p className="alert alert-error" role="alert">{error}</p>}

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
        <p className="alert alert-error">
          <strong>pg_dump was not found.</strong> {data.tool.problem} Until this is fixed, no
          backup can run.
        </p>
      )}

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
            </tr>
          </thead>
          <tbody>
            {data.runs.length === 0 && (
              <tr><td colSpan={6} className="empty">No backup has run yet.</td></tr>
            )}

            {data.runs.map((r) => (
              <tr key={r.id}>
                <td>{formatDateTime(r.startedAtUtc)}</td>
                <td>
                  <span className={`pill bk-${r.status}`}>{STATUS[r.status] ?? '—'}</span>
                  {r.error && <div className="hist-fault">{r.error}</div>}
                </td>
                <td>{TRIGGER[r.trigger] ?? '—'}</td>
                <td className="mono">{r.fileName ?? <span className="muted">—</span>}</td>
                <td>{r.sizeBytes !== null ? formatSize(r.sizeBytes) : <span className="muted">—</span>}</td>
                <td>{r.durationMs !== null ? formatDuration(r.durationMs) : <span className="muted">—</span>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

/** Day-first, as every date in an Indian hospital is written. */
function formatDateTime(iso: string | null): string {
  if (!iso) return '—';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '—';
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${pad(d.getDate())}/${pad(d.getMonth() + 1)}/${d.getFullYear()} ${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

function formatAge(hours: number): string {
  if (hours < 1) return plural(Math.max(1, Math.round(hours * 60)), 'minute');
  if (hours < 48) return plural(Math.round(hours), 'hour');
  return plural(Math.round(hours / 24), 'day');
}

function plural(n: number, unit: string): string {
  return `${n} ${unit}${n === 1 ? '' : 's'}`;
}

function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

function formatDuration(ms: number): string {
  if (ms < 1000) return `${ms} ms`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(1)} s`;
  return `${Math.round(ms / 60_000)} min`;
}
