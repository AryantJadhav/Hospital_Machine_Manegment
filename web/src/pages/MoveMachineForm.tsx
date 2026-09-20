import { useEffect, useState } from 'react';
import type { FormEvent } from 'react';
import { api } from '../api/client';

type Place = { id: number; name: string; depth: number; level: number };

// Buildings and deeper. The organisation and its sites are shown so the list reads
// as a tree, but a machine is never placed in one.
const FIRST_PLACEABLE_LEVEL = 30;

/**
 * Shifting a machine to another room or ward.
 *
 * Open to whoever is signed in, because it is the engineer standing at the machine
 * who knows it has moved. The register changes at once, and the move is written
 * down: from where, to where, by whom, and why.
 */
export function MoveMachineForm({
  equipmentId,
  currentLocationId,
  onCancel,
  onMoved,
}: {
  equipmentId: number;
  currentLocationId: number;
  onCancel: () => void;
  onMoved: (message: string) => void | Promise<void>;
}) {
  const [places, setPlaces] = useState<Place[]>([]);
  const [to, setTo] = useState('');
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const list = await api.get<Place[]>('/api/lookups/locations');
        if (!cancelled) setPlaces(list);
      } catch {
        if (!cancelled) setError('Could not load the places.');
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setBusy(true);
    try {
      await api.post(`/api/equipment/${equipmentId}/move`, {
        toLocationId: Number(to),
        reason: reason.trim() || null,
      });
      const name = places.find((p) => p.id === Number(to))?.name ?? 'the new place';
      await onMoved(`Moved to ${name}.`);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not move the machine.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="card stack" onSubmit={submit}>
      <h2 className="section-h" style={{ margin: 0 }}>Move this machine</h2>

      {error && <p className="alert alert-error" role="alert">{error}</p>}

      <div className="filters">
        <label className="field grow">
          <span>Move to</span>
          <select aria-label="Move to" value={to} onChange={(e) => setTo(e.target.value)} required>
            <option value="">Choose…</option>
            {places.map((p) => (
              <option
                key={p.id}
                value={p.id}
                disabled={p.level < FIRST_PLACEABLE_LEVEL || p.id === currentLocationId}
              >
                {' '.repeat(p.depth * 2)}{p.name}{p.id === currentLocationId ? ' (here now)' : ''}
              </option>
            ))}
          </select>
        </label>

        <label className="field grow">
          <span>Why (optional)</span>
          <input
            aria-label="Why"
            value={reason}
            onChange={(e) => setReason(e.target.value)}
            maxLength={500}
            placeholder="For example: needed in ICU for the night"
            autoComplete="off"
          />
        </label>
      </div>

      <div className="row">
        <button className="btn btn-primary" type="submit" disabled={busy || !to}>
          {busy ? 'Moving…' : 'Move'}
        </button>
        <button className="btn" type="button" onClick={onCancel}>Cancel</button>
      </div>
    </form>
  );
}
