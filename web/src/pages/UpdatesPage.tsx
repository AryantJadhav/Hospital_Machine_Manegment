import { useCallback, useEffect, useRef, useState } from 'react';
import { api } from '../api/client';

/**
 * Installing an update from a file.
 *
 * Written for a machine with no internet, because that is what a biomedical
 * department's PC usually is. Someone is handed two files, copies them into a
 * folder, and presses a button.
 *
 * The unusual part of this page is the end: the server it is talking to stops
 * existing halfway through. Nothing after "Install" is a normal request /
 * response — the page waits for the service to come back and reports which
 * version came back with it.
 */

type Candidate = {
  state: string;
  canInstall: boolean;
  manifestPath: string;
  fileName: string;
  message: string;
  version: string | null;
  releasedOn: string | null;
  notes: string | null;
  sizeBytes: number | null;
  installerFileName: string | null;
};

type Status = {
  enabled: boolean;
  runningVersion: string;
  defaultFolder: string;
  folder: string;
  available: Candidate[];
};

/** Green for the one that can be installed, red for the ones that must not be. */
const TONE: Record<string, string> = {
  Ready: 'pill-20',
  NotNewer: 'pill-10',
  Unreadable: 'pill-30',
  InstallerMissing: 'pill-30',
  InstallerAltered: 'pill-40',
  NotOurs: 'pill-40',
};

const STATE_LABEL: Record<string, string> = {
  Ready: 'Ready',
  NotNewer: 'Already installed',
  Unreadable: 'Not readable',
  InstallerMissing: 'Installer missing',
  InstallerAltered: 'Does not match',
  NotOurs: 'Not from us',
};

function megabytes(bytes: number | null): string {
  if (!bytes) return '';
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

export function UpdatesPage() {
  const [data, setData] = useState<Status | null>(null);
  const [folder, setFolder] = useState('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  // Set once the installer has been handed control. From then on this page is
  // talking to a server that is shutting down, and the only useful thing it
  // can do is wait.
  const [installing, setInstalling] = useState<string | null>(null);
  const [progress, setProgress] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);

  const load = useCallback(async (target?: string) => {
    setLoading(true);
    setError(null);
    try {
      const query = target?.trim() ? `?folder=${encodeURIComponent(target.trim())}` : '';
      setData(await api.get<Status>(`/api/admin/update${query}`));
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not read the update folder.');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  if (loading && !data) return <div className="page"><p className="muted">Loading…</p></div>;

  return (
    <div className="page">
      <div className="page-head">
        <div>
          <h1>Updates</h1>
          <p className="muted">
            Version {data?.runningVersion ?? '—'} is installed. Updates are installed from a file,
            so this works with no internet.
          </p>
        </div>
      </div>

      {error && <p className="alert alert-error" role="alert">{error}</p>}

      {data && !data.enabled && (
        <p className="alert alert-error">
          This build cannot check whether an update was really issued by us, so it will not install
          one. That is deliberate — an update runs with full rights on this machine. Ask your
          supplier for a build that can.
        </p>
      )}

      {installing && (
        <div className="card stack">
          <h2 style={{ margin: 0, fontSize: '1.05rem' }}>Installing version {installing}</h2>
          <p className="muted" style={{ margin: 0 }}>
            {progress ?? 'The service is stopping. Do not turn the machine off.'}
          </p>
        </div>
      )}

      {done && <p className="alert alert-ok" role="status">{done}</p>}

      {data?.enabled && !installing && (
        <>
          <div className="card stack">
            <h2 style={{ margin: 0, fontSize: '1.05rem' }}>Where to look</h2>
            <p className="muted" style={{ margin: 0 }}>
              Copy both files — the installer and the small <code>.update</code> file that came with
              it — into the same folder, then look there.
            </p>
            <div className="row">
              <input
                style={{ flex: '1 1 22rem' }}
                value={folder}
                placeholder={data.defaultFolder}
                onChange={(e) => setFolder(e.target.value)}
                aria-label="Folder to look in"
              />
              <button className="btn" onClick={() => void load(folder)} disabled={loading}>
                {loading ? 'Looking…' : 'Look for updates'}
              </button>
              {folder && (
                <button
                  className="btn btn-quiet"
                  onClick={() => {
                    setFolder('');
                    void load();
                  }}
                >
                  Use the default folder
                </button>
              )}
            </div>
            <p className="muted" style={{ margin: 0, fontSize: '0.85rem' }}>
              Looking in <span className="mono">{data.folder}</span>. A USB stick works too — type
              its drive, such as <span className="mono">E:\</span>.
            </p>
          </div>

          {data.available.length === 0 && (
            <div className="card">
              <p className="muted" style={{ margin: 0 }}>
                Nothing in that folder. An update is two files that travel together: an installer
                ending in <span className="mono">.exe</span> and a small one ending in{' '}
                <span className="mono">.update</span>.
              </p>
            </div>
          )}

          {data.available.map((c) => (
            <UpdateCard
              key={c.manifestPath}
              candidate={c}
              onInstalling={(version) => {
                setInstalling(version);
                setError(null);
              }}
              onProgress={setProgress}
              onFinished={(message) => {
                setInstalling(null);
                setProgress(null);
                setDone(message);
                void load(folder);
              }}
              onFailed={(message) => {
                setInstalling(null);
                setProgress(null);
                setError(message);
              }}
            />
          ))}
        </>
      )}
    </div>
  );
}

function UpdateCard({
  candidate,
  onInstalling,
  onProgress,
  onFinished,
  onFailed,
}: {
  candidate: Candidate;
  onInstalling: (version: string) => void;
  onProgress: (message: string) => void;
  onFinished: (message: string) => void;
  onFailed: (message: string) => void;
}) {
  const [confirming, setConfirming] = useState(false);
  const [busy, setBusy] = useState(false);

  // Held in a ref so the polling loop below cannot outlive the component and
  // keep calling setState on something that has gone.
  const cancelled = useRef(false);
  useEffect(() => () => { cancelled.current = true; }, []);

  async function install() {
    setBusy(true);
    try {
      await api.post('/api/admin/update/install', { manifestPath: candidate.manifestPath });
    } catch (e) {
      setBusy(false);
      setConfirming(false);
      onFailed(e instanceof Error ? e.message : 'The update could not be started.');
      return;
    }

    onInstalling(candidate.version ?? '');
    await waitForTheServiceToComeBack(candidate.version, onProgress, onFinished, cancelled);
  }

  return (
    <div className="card stack">
      <div className="page-head">
        <div>
          <h2 style={{ margin: 0, fontSize: '1.05rem' }}>
            {candidate.version ? `Version ${candidate.version}` : candidate.fileName}
          </h2>
          <p className="muted" style={{ margin: '0.15rem 0 0' }}>
            {candidate.releasedOn && `Released ${candidate.releasedOn}. `}
            {candidate.installerFileName}
            {candidate.sizeBytes ? `, ${megabytes(candidate.sizeBytes)}` : ''}
          </p>
        </div>
        <span className={`pill ${TONE[candidate.state] ?? 'pill-10'}`}>
          {STATE_LABEL[candidate.state] ?? candidate.state}
        </span>
      </div>

      {candidate.notes && <p style={{ margin: 0 }}>{candidate.notes}</p>}

      <p className={candidate.canInstall ? 'muted' : 'alert alert-error'} style={{ margin: 0 }}>
        {candidate.message}
      </p>

      {candidate.canInstall && !confirming && (
        <div className="row">
          <button className="btn btn-primary" onClick={() => setConfirming(true)}>
            Install version {candidate.version}
          </button>
        </div>
      )}

      {candidate.canInstall && confirming && (
        <div className="stack">
          <p className="alert alert-info" style={{ margin: 0 }}>
            A backup is taken first, then the service stops, updates and starts again on its own.
            It usually takes a few minutes, and everyone is signed out while it happens. There is no
            undo other than restoring that backup — so do this when the department is quiet, not
            mid-round.
          </p>
          <div className="row">
            <button className="btn btn-primary" onClick={() => void install()} disabled={busy}>
              {busy ? 'Starting…' : 'Back up and install'}
            </button>
            <button className="btn btn-quiet" onClick={() => setConfirming(false)} disabled={busy}>
              Not now
            </button>
          </div>
        </div>
      )}
    </div>
  );
}

/**
 * Waits out the restart.
 *
 * There is no request that can report on this: the server that would answer it
 * is the one being replaced. So the page asks /health until something answers,
 * and then says which version answered — which is the only honest way to know
 * the update worked.
 *
 * Failures here are reported as "could not confirm" rather than "failed". The
 * update may well have succeeded while this browser was on the wrong side of a
 * network hiccup, and telling a hospital their update failed when it did not is
 * how someone ends up running an installer twice.
 */
async function waitForTheServiceToComeBack(
  expected: string | null,
  onProgress: (message: string) => void,
  onFinished: (message: string) => void,
  cancelled: { current: boolean },
) {
  const started = Date.now();
  const limitMs = 15 * 60 * 1000;

  // Long enough for the service to actually stop before the first look,
  // otherwise the old process answers and the page declares victory early.
  await new Promise((r) => setTimeout(r, 8000));

  let sawItGoDown = false;

  // If the service has not even begun to stop by now, the installer very
  // likely never took hold. Saying "waiting" for fifteen minutes in that
  // case is not patience, it is a page implying progress that is not
  // happening while someone stands over a machine they have been told not
  // to turn off.
  const shouldHaveStoppedBy = started + 90_000;

  while (!cancelled.current && Date.now() - started < limitMs) {
    try {
      const res = await fetch('/health', { cache: 'no-store' });
      const body = (await res.json()) as { version?: string };

      // A version that is not the one we asked for, before the service has
      // been seen to go down, is the old process still serving.
      if (sawItGoDown || (expected && body.version?.startsWith(expected))) {
        onFinished(
          `Version ${body.version ?? 'unknown'} is running. Sign in again if you are asked to.`,
        );
        return;
      }

      onProgress(
        Date.now() > shouldHaveStoppedBy
          ? 'The service has not stopped yet. If nothing changes in the next few minutes the '
            + 'installer probably did not start — there is a log in the updates folder, and '
            + 'nothing has been installed.'
          : 'Waiting for the service to stop…',
      );
    } catch {
      sawItGoDown = true;
      onProgress('The service is restarting. This is the slow part — do not turn the machine off.');
    }

    await new Promise((r) => setTimeout(r, 3000));
  }

  if (!cancelled.current) {
    onFinished(
      'The service has not come back yet. Give it a few more minutes, then reload this page. '
      + 'If it stays down, the installer wrote a log in the updates folder, and there is a backup '
      + 'from just before the update.',
    );
  }
}
