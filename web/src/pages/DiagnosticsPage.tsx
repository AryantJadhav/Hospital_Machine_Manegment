import { useCallback, useEffect, useState } from 'react';
import { api } from '../api/client';
import { ServerQrCode } from './ServerQrCode';

type Check = {
  name: string;
  state: number;
  detail: string;
  advice: string | null;
};

type Diagnostics = {
  hospitalName: string | null;
  overall: number;
  version: string;
  uptimeSeconds: number;
  utcNow: string;
  addresses: string[];
  checks: Check[];
};

const OK = 10;
const WARNING = 20;
const PROBLEM = 30;

const STATE_LABEL: Record<number, string> = {
  [OK]: 'OK',
  [WARNING]: 'Check',
  [PROBLEM]: 'Problem',
};

export function DiagnosticsPage() {
  const [data, setData] = useState<Diagnostics | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      setData(await api.get<Diagnostics>('/api/admin/diagnostics'));
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not run diagnostics.');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  if (loading && !data) return <div className="page"><p className="muted">Checking…</p></div>;

  if (!data) {
    return (
      <div className="page">
        <p className="alert alert-error">{error ?? 'Could not run diagnostics.'}</p>
      </div>
    );
  }

  const problems = data.checks.filter((c) => c.state === PROBLEM).length;
  const warnings = data.checks.filter((c) => c.state === WARNING).length;

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <h1>Diagnostics</h1>
          <p className="muted">
            Everything this installation depends on, checked now. Read this first when something
            is wrong.
          </p>
        </div>
        <button className="btn" onClick={() => void load()} disabled={loading}>
          {loading ? 'Checking…' : 'Run again'}
        </button>
      </header>

      {error && <p className="alert alert-error" role="alert">{error}</p>}

      {/* The one-line verdict, so nobody has to interpret six rows. */}
      <p className={`alert ${data.overall === OK ? 'alert-ok' : 'alert-error'}`}>
        {data.overall === OK
          ? 'Everything checks out.'
          : problems > 0
            ? `${problems} problem${problems === 1 ? ' needs' : 's need'} attention${warnings > 0 ? `, and ${warnings} more worth a look` : ''}.`
            : `${warnings} thing${warnings === 1 ? '' : 's'} worth a look.`}
      </p>

      <div className="card">
        {data.checks.map((c) => (
          <div key={c.name} className="hist-row">
            <div className="grow">
              <div>
                <strong>{c.name}</strong>
                <span className={`pill dg-${c.state} hist-flag`}>{STATE_LABEL[c.state]}</span>
              </div>
              <div className="muted hist-meta">{c.detail}</div>
              {c.advice && <div className="hist-fault">{c.advice}</div>}
            </div>
          </div>
        ))}
      </div>

      <div className="card">
        <h2 className="section-h">Reaching this server</h2>
        {data.addresses.length === 0 ? (
          <p className="muted" style={{ margin: 0 }}>
            No network address could be determined.
          </p>
        ) : (
          <>
            <p className="muted" style={{ marginTop: 0 }}>
              Type one of these into a browser on another machine on the same network.
            </p>
            <ul className="addr-list">
              {data.addresses.map((a) => <li key={a} className="mono">{a}</li>)}
            </ul>
          </>
        )}
      </div>

      {/* Mobile-config QR — the permanent place to onboard a new phone.
          Uses the first address the server reports, which is the most likely
          to work from the hospital's Wi-Fi network. */}
      {data.addresses.length > 0 && (
        <div className="card">
          <h2 className="section-h">Connect a phone</h2>
          <p className="muted" style={{ marginTop: 0 }}>
            Open the Hospital PM app on a phone and scan this code to connect it to this server.
          </p>
          <ServerQrCode url={data.addresses[0]} />
        </div>
      )}

      <div className="card">
        <h2 className="section-h">This installation</h2>
        <dl className="detail">
          {data.hospitalName && (
            <>
              <dt>Hospital</dt>
              <dd className="strong">{data.hospitalName}</dd>
            </>
          )}
          <dt>Version</dt>
          <dd className="mono">{data.version}</dd>
          <dt>Running for</dt>
          <dd>{formatUptime(data.uptimeSeconds)}</dd>
          <dt>Server time</dt>
          <dd className="mono">{data.utcNow.replace('T', ' ').replace(/\..*$/, '')} UTC</dd>
        </dl>
      </div>
    </div>
  );
}

function formatUptime(seconds: number): string {
  if (seconds < 60) return `${seconds} seconds`;
  if (seconds < 3600) return `${Math.round(seconds / 60)} minutes`;
  if (seconds < 172_800) return `${Math.round(seconds / 3600)} hours`;
  return `${Math.round(seconds / 86_400)} days`;
}
