import { useCallback, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import type { FormEvent } from 'react';
import { api, ApiError } from '../api/client';
import { useQrScanner } from '../scan/useQrScanner';
import { extractAssetTag } from '../scan/assetTag';
import { formatDate } from '../time';
import { StatusPill } from '../StatusPill';
import { EQUIPMENT_LOOK } from '../statusTones';

type Equipment = {
  id: number;
  assetTag: string;
  serialNumber: string | null;
  equipmentTypeName: string;
  locationName: string;
  manufacturer: string | null;
  model: string | null;
  status: number;
  purchaseDate: string | null;
  installationDate: string | null;
  warrantyExpiryDate: string | null;
  notes: string | null;
};

const STATUS: Record<number, string> = {
  10: 'In store',
  20: 'In service',
  30: 'Under repair',
  40: 'Condemned',
  50: 'Disposed',
};

export function ScanPage() {
  const [found, setFound] = useState<Equipment | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [manual, setManual] = useState('');

  // A webcam decodes the same code many times a second. Without this guard
  // one label in frame becomes a stream of identical requests.
  const locked = useRef(false);

  const lookup = useCallback(async (raw: string) => {
    const tag = extractAssetTag(raw);
    if (!tag) return;

    setBusy(true);
    setError(null);
    try {
      setFound(await api.get<Equipment>(`/api/equipment/by-tag/${encodeURIComponent(tag)}`));
    } catch (e) {
      if (e instanceof ApiError && e.status === 404) {
        setError(`No equipment with tag "${tag}". It may not be on the register yet.`);
      } else {
        setError(e instanceof Error ? e.message : 'Lookup failed.');
      }
    } finally {
      setBusy(false);
      setTimeout(() => {
        locked.current = false;
      }, 1500);
    }
  }, []);

  const onDecoded = useCallback(
    (value: string) => {
      if (locked.current) return;
      locked.current = true;
      void lookup(value);
    },
    [lookup],
  );

  const { videoRef, canvasRef, state, cameras, start, stop } = useQrScanner(onDecoded);

  function submitManual(e: FormEvent) {
    e.preventDefault();
    if (manual.trim()) void lookup(manual.trim());
  }

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <h1>Scan</h1>
          <p className="muted">Hold an asset tag up to the webcam, or type it in.</p>
        </div>
      </header>

      {error && <p className="alert alert-error" role="alert">{error}</p>}

      <div className="scan-grid">
        <div className="card stack">
          <div className="scan-view">
            {/* Kept mounted so the ref exists before start() runs. */}
            <video ref={videoRef} className="scan-video" muted playsInline />
            <canvas ref={canvasRef} style={{ display: 'none' }} />

            {state.kind !== 'scanning' && (
              <div className="scan-cover">
                <ScannerMessage state={state} onStart={() => void start()} />
              </div>
            )}
          </div>

          {state.kind === 'scanning' && (
            <div className="row">
              {cameras.length > 1 && (
                <select
                  aria-label="Camera"
                  onChange={(e) => {
                    stop();
                    void start(e.target.value);
                  }}
                >
                  {cameras.map((c) => (
                    <option key={c.deviceId} value={c.deviceId}>{c.label}</option>
                  ))}
                </select>
              )}
              <button className="btn" onClick={stop}>Stop camera</button>
              {busy && <span className="muted" style={{ alignSelf: 'center' }}>Looking up…</span>}
            </div>
          )}

          <form className="row" onSubmit={submitManual}>
            <input
              className="grow"
              value={manual}
              onChange={(e) => setManual(e.target.value)}
              placeholder="Or type an asset tag, e.g. BME-0001"
              aria-label="Asset tag"
            />
            <button className="btn" type="submit" disabled={!manual.trim() || busy}>
              Find
            </button>
          </form>
        </div>

        <div className="card">
          {found ? <Detail equipment={found} /> : (
            <p className="muted" style={{ margin: 0 }}>
              Scanned equipment appears here.
            </p>
          )}
        </div>
      </div>
    </div>
  );
}

function ScannerMessage({
  state,
  onStart,
}: {
  state: ReturnType<typeof useQrScanner>['state'];
  onStart: () => void;
}) {
  switch (state.kind) {
    case 'idle':
      return <button className="btn btn-primary" onClick={onStart}>Start camera</button>;

    case 'starting':
      return <p className="muted">Starting camera…</p>;

    case 'insecure':
      // The single most likely reason this feature appears broken in a real
      // hospital, and the message has to say so plainly.
      return (
        <div className="scan-note">
          <strong>Camera unavailable over plain HTTP.</strong>
          <p>
            Browsers only allow camera access on a secure page. This works when you open the app on
            the server itself (<code>localhost</code>), but not from another PC over{' '}
            <code>http://</code>. Serving over HTTPS fixes it for everyone.
          </p>
          <p>You can still type an asset tag below.</p>
        </div>
      );

    case 'denied':
      return (
        <div className="scan-note">
          <strong>Camera permission denied.</strong>
          <p>Allow camera access for this site in your browser settings, then start again.</p>
        </div>
      );

    case 'no-camera':
      return (
        <div className="scan-note">
          <strong>No camera found.</strong>
          <p>Connect a webcam, or type the asset tag below.</p>
        </div>
      );

    case 'unsupported':
      return (
        <div className="scan-note">
          <strong>This browser cannot use the camera.</strong>
          <p>Type the asset tag below instead.</p>
        </div>
      );

    case 'error':
      return (
        <div className="scan-note">
          <strong>Camera error.</strong>
          <p>{state.message}</p>
        </div>
      );

    default:
      return null;
  }
}

function Detail({ equipment }: { equipment: Equipment }) {
  return (
    <div className="stack">
      <div>
        <h2 style={{ margin: 0, fontSize: '1.5rem' }}>{equipment.assetTag}</h2>
        <p className="muted" style={{ margin: '0.15rem 0 0' }}>{equipment.equipmentTypeName}</p>
      </div>

      <div>
        <StatusPill look={EQUIPMENT_LOOK[equipment.status]}>
          {STATUS[equipment.status] ?? 'Unknown'}
        </StatusPill>
      </div>

      <Link className="btn" to={`/equipment/${equipment.id}`}>
        Open full record
      </Link>

      <dl className="detail">
        <Row label="Location" value={equipment.locationName} strong />
        <Row label="Serial number" value={equipment.serialNumber} mono />
        <Row label="Manufacturer" value={equipment.manufacturer} />
        <Row label="Model" value={equipment.model} />
        <Row label="Installed" value={formatDate(equipment.installationDate)} />
        <Row label="Warranty expiry" value={formatDate(equipment.warrantyExpiryDate)} />
      </dl>

      {equipment.notes && <p style={{ margin: 0 }}>{equipment.notes}</p>}
    </div>
  );
}

function Row({
  label,
  value,
  mono,
  strong,
}: {
  label: string;
  value: string | null;
  mono?: boolean;
  strong?: boolean;
}) {
  return (
    <>
      <dt>{label}</dt>
      <dd className={[mono ? 'mono' : '', strong ? 'strong' : ''].join(' ').trim()}>
        {value || <span className="muted">—</span>}
      </dd>
    </>
  );
}

