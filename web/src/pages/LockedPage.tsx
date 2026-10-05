import { useCallback, useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import { api, LOCKED_EVENT } from '../api/client';
import { usePageTitle } from '../pageTitle';

type LockInfo = {
  locked: boolean;
  hospitalName: string | null;
  licenceId: string | null;
  message: string | null;
};

/**
 * Stands in front of the whole program. When the installation has been locked by its supplier it shows the lock
 * screen and nothing else, because every other request is refused anyway; otherwise it is invisible.
 *
 * It asks once on opening, and again whenever any request is answered "locked" (a lock entered while someone was
 * mid-task). If it cannot ask, it lets the program through: a lock that nobody can read about is not a reason to
 * show a blank page.
 */
export function LockGuard({ children }: { children: ReactNode }) {
  const [lock, setLock] = useState<LockInfo | null | undefined>(undefined);

  const check = useCallback(async () => {
    try {
      setLock(await api.get<LockInfo>('/api/licence/lock'));
    } catch {
      setLock(null);
    }
  }, []);

  useEffect(() => {
    void check();
    window.addEventListener(LOCKED_EVENT, check);
    return () => window.removeEventListener(LOCKED_EVENT, check);
  }, [check]);

  if (lock === undefined) return <div className="boot">Loading…</div>;
  if (lock?.locked) return <LockedPage info={lock} />;
  return <>{children}</>;
}

/**
 * What everyone sees while the installation is locked. Nobody can sign in, so the one thing it offers is the box
 * for the unlock code, and enough to quote on the phone: who this is, and which licence.
 */
function LockedPage({ info }: { info: LockInfo }) {
  usePageTitle('Locked');
  const [code, setCode] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function unlock() {
    setBusy(true);
    setError(null);
    try {
      await api.post('/api/licence/code', { code });
      // Whatever was open is stale: start again from the sign-in.
      window.location.assign('/');
    } catch (e) {
      setError(e instanceof Error ? e.message : 'The code could not be entered.');
      setBusy(false);
    }
  }

  return (
    <div className="boot" style={{ alignItems: 'flex-start', padding: '3rem 1rem' }}>
      <div className="card stack" style={{ maxWidth: '40rem', margin: '0 auto', width: '100%' }}>
        <h1 style={{ margin: 0 }}>This installation is locked</h1>
        <p style={{ margin: 0 }}>{info.message}</p>

        <dl className="detail">
          {info.hospitalName && (
            <>
              <dt>Hospital</dt>
              <dd className="strong">{info.hospitalName}</dd>
            </>
          )}
          <dt>Licence</dt>
          <dd className="mono" style={{ wordBreak: 'break-all' }}>{info.licenceId}</dd>
        </dl>

        <label className="field" htmlFor="unlock-code">
          <span>Unlock code</span>
          <textarea
            id="unlock-code"
            className="licence-box mono"
            rows={8}
            spellCheck={false}
            autoComplete="off"
            placeholder={'-----BEGIN HOSPITALPM CODE-----'}
            value={code}
            onChange={(e) => setCode(e.target.value)}
          />
        </label>

        {error && <p className="alert alert-error" role="alert">{error}</p>}

        <div className="row">
          <button className="btn btn-primary" disabled={busy || code.trim().length === 0} onClick={() => void unlock()}>
            {busy ? 'Checking…' : 'Unlock'}
          </button>
        </div>
      </div>
    </div>
  );
}
