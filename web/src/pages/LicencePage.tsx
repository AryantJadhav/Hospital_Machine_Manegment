import { useCallback, useEffect, useState } from 'react';
import { api } from '../api/client';
import { formatDate } from '../time';

type LicenceDetail = {
  id: string;
  hospitalName: string;
  issuedOn: string;
  expiresOn: string | null;
  durationDays: number | null;
  modules: string[];
  maxEquipment: number | null;
  notes: string | null;
};

type Status = {
  state: number;
  message: string;
  effectiveExpiry: string | null;
  readOnlyFrom: string | null;
  path: string;
  licence: LicenceDetail | null;
};

const VALID = 10;
const MISSING = 20;
const EXPIRED = 30;
const READ_ONLY = 35;
const INVALID = 40;

const STATE_LABEL: Record<number, string> = {
  [VALID]: 'Licensed',
  [MISSING]: 'Unlicensed',
  [EXPIRED]: 'Expired',
  [READ_ONLY]: 'Read-only',
  [INVALID]: 'Not valid',
};

export function LicencePage() {
  const [data, setData] = useState<Status | null>(null);
  const [loading, setLoading] = useState(true);
  const [text, setText] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      setData(await api.get<Status>('/api/admin/licence'));
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not read the licence.');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  async function install() {
    setSaving(true);
    setError(null);
    setSaved(false);
    try {
      setData(await api.post<Status>('/api/admin/licence', { licence: text }));
      setText('');
      setSaved(true);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'The licence could not be installed.');
    } finally {
      setSaving(false);
    }
  }

  async function onFile(file: File) {
    setText(await file.text());
    setError(null);
    setSaved(false);
  }

  if (loading && !data) return <div className="page"><p className="muted">Loading…</p></div>;

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <h1>Licence</h1>
          <p className="muted">
            Checked on this machine against a key built into the software. No internet connection
            is needed, now or ever.
          </p>
        </div>
      </header>

      {data && (
        <p className={`alert ${data.state === VALID ? 'alert-ok' : 'alert-error'}`}>
          <strong>{STATE_LABEL[data.state] ?? 'Unknown'}.</strong> {data.message}
        </p>
      )}

      {/* Said plainly, because the fear this answers is "will it stop working". */}
      {data && data.state !== VALID && (
        <p className="muted" style={{ marginTop: '-0.4rem' }}>
          {data.state === READ_ONLY
            ? 'Every record can still be opened and printed. Nothing new can be recorded until a renewal key is installed below.'
            : 'Everything keeps working. Hospital PM does not lock you out of your own maintenance records over a licence.'}
        </p>
      )}

      {data?.licence && (
        <div className="card">
          <h2 className="section-h">This licence</h2>
          <dl className="detail">
            <dt>Hospital</dt>
            <dd className="strong">{data.licence.hospitalName}</dd>
            <dt>Issued</dt>
            <dd>{formatDate(data.licence.issuedOn)}</dd>
            <dt>Runs until</dt>
            <dd>
              {data.effectiveExpiry ? formatDate(data.effectiveExpiry) : 'Never'}
              {data.licence.durationDays !== null && (
                <span className="muted"> ({data.licence.durationDays} days from installation)</span>
              )}
            </dd>
            {data.readOnlyFrom && (
              <>
                <dt>Read-only from</dt>
                <dd>{formatDate(data.readOnlyFrom)}</dd>
              </>
            )}
            <dt>Modules</dt>
            <dd>{data.licence.modules.length > 0 ? data.licence.modules.join(', ') : 'Core only'}</dd>
            <dt>Equipment limit</dt>
            <dd>{data.licence.maxEquipment ?? 'No limit'}</dd>
            <dt>Reference</dt>
            <dd className="mono">{data.licence.id}</dd>
            {data.licence.notes && (
              <>
                <dt>Notes</dt>
                <dd>{data.licence.notes}</dd>
              </>
            )}
          </dl>
        </div>
      )}

      <div className="card stack">
        <h2 className="section-h" style={{ marginBottom: 0 }}>Install a licence</h2>
        <p className="muted" style={{ margin: 0 }}>
          Choose the <span className="mono">.licence</span> file you were sent, or paste its
          contents. It is checked before anything is saved, so a damaged file cannot replace a
          working licence.
        </p>

        {error && <p className="alert alert-error" role="alert">{error}</p>}
        {saved && <p className="alert alert-ok" role="status">Licence installed.</p>}

        <input
          type="file"
          aria-label="Licence file"
          accept=".licence,.txt"
          onChange={(e) => {
            const file = e.target.files?.[0];
            if (file) void onFile(file);
          }}
        />

        <textarea
          className="licence-box mono"
          rows={8}
          spellCheck={false}
          placeholder={'-----BEGIN HOSPITALPM LICENCE-----'}
          aria-label="Licence text"
          value={text}
          onChange={(e) => setText(e.target.value)}
        />

        <div className="row">
          <button
            className="btn btn-primary"
            disabled={saving || text.trim().length === 0}
            onClick={() => void install()}
          >
            {saving ? 'Checking…' : 'Install licence'}
          </button>
        </div>
      </div>

      <div className="card">
        <h2 className="section-h">Where it is kept</h2>
        <p className="mono" style={{ margin: 0, wordBreak: 'break-all' }}>{data?.path}</p>
      </div>
    </div>
  );
}

