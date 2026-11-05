import { useState } from 'react';
import { api } from './api/client';
import { useDialogs } from './dialogs';
import { formatBytes } from './bytes';
import { formatDateTime } from './time';
import { PhotoPicker } from './PhotoPicker';
import { photoForm } from './photoForm';
import type { GatePassPhoto } from './gatePassTypes';

/**
 * The photos on a gate pass: what went out, in what condition, what came back. Always optional. Anyone who can read the pass
 * sees them; whoever writes passes adds more at any time, and an administrator can remove one.
 */
export function GatePassPhotos({
  passId,
  photos,
  canAdd,
  canRemove,
  onChanged,
}: {
  passId: number;
  photos: GatePassPhoto[];
  canAdd: boolean;
  canRemove: boolean;
  onChanged: () => Promise<void>;
}) {
  const dialogs = useDialogs();
  const [files, setFiles] = useState<File[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Nothing on it and nothing to add: no card at all, since photos are optional.
  if (photos.length === 0 && !canAdd) return null;

  async function save() {
    if (files.length === 0) return;
    setBusy(true);
    setError(null);
    try {
      await api.postForm(`/api/gate-passes/${passId}/photos`, photoForm(files));
      setFiles([]);
      await onChanged();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not save the photos.');
    } finally {
      setBusy(false);
    }
  }

  async function remove(photo: GatePassPhoto) {
    const ok = await dialogs.confirm({
      title: `Remove "${photo.fileName}"?`,
      message: 'The photo is deleted from the system. The removal is recorded in the audit log, with who did it and when.',
      confirmLabel: 'Yes, remove it',
      cancelLabel: 'No, keep it',
      danger: true,
    });
    if (!ok) return;
    try {
      setError(null);
      await api.del(`/api/gate-passes/photos/${photo.id}`);
      await onChanged();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not remove the photo.');
    }
  }

  return (
    <section className="card stack" aria-labelledby="gp-photos-title">
      <h2 className="section-h" id="gp-photos-title">Photos <span className="muted">(optional)</span></h2>

      {photos.length === 0 && <p className="muted" style={{ margin: 0 }}>No photos yet.</p>}

      {photos.length > 0 && (
        <ul className="stack" style={{ listStyle: 'none', margin: 0, padding: 0, gap: '0.35rem' }}>
          {photos.map((p) => (
            <li key={p.id} className="row" style={{ justifyContent: 'space-between' }}>
              <span>
                {p.fileName}
                <span className="muted"> · {formatBytes(p.sizeBytes)} · {p.uploadedBy ?? 'unknown'} · {formatDateTime(p.uploadedAtUtc)}</span>
              </span>
              <span className="row" style={{ gap: '0.4rem' }}>
                <button
                  className="btn btn-quiet"
                  aria-label={`View ${p.fileName}`}
                  onClick={() =>
                    void api.view(`/api/gate-passes/photos/${p.id}`)
                      .catch((err: unknown) => setError(err instanceof Error ? err.message : 'Could not open the photo.'))
                  }
                >
                  View
                </button>
                {canRemove && (
                  <button className="btn btn-quiet" aria-label={`Remove ${p.fileName}`} onClick={() => void remove(p)}>
                    Remove
                  </button>
                )}
              </span>
            </li>
          ))}
        </ul>
      )}

      {canAdd && (
        <>
          <PhotoPicker files={files} onChange={setFiles} alreadySaved={photos.length} />
          {files.length > 0 && (
            <div>
              <button className="btn" disabled={busy} onClick={() => void save()}>
                {busy ? 'Saving…' : 'Save photos'}
              </button>
            </div>
          )}
        </>
      )}
      {error && <p className="alert alert-error" role="alert">{error}</p>}
    </section>
  );
}
