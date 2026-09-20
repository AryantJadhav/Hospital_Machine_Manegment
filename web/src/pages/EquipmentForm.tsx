import { useEffect, useState } from 'react';
import type { FormEvent } from 'react';
import { api } from '../api/client';

/**
 * Adding or correcting one machine.
 *
 * The register could only ever be loaded from a spreadsheet. That is the right
 * way to bring in two thousand assets on day one and the wrong way to record
 * the single infusion pump that arrived this morning — a hospital had to build
 * a one-row file and re-import it, which nobody does, so the register drifts
 * out of date and the PM programme drifts with it.
 *
 * Editing matters for the same reason: a machine moves ward, comes back from
 * the vendor, or is finally scrapped, and none of that reached the register
 * without a spreadsheet round-trip.
 */

export type EquipmentDraft = {
  id?: number;
  assetTag: string;
  serialNumber: string | null;
  equipmentTypeId: number;
  locationId: number;
  manufacturer: string | null;
  model: string | null;
  status: number;
  purchaseDate: string | null;
  installationDate: string | null;
  warrantyExpiryDate: string | null;
  notes: string | null;
};

type Lookup = { id: number; code: string; name: string };
type LocationLookup = Lookup & { depth: number; level: number };

/** Building is the shallowest level a machine may stand in; the server refuses the two above it. */
const FIRST_PLACEABLE_LEVEL = 30;

// InStore and InService are the ones anyone picks by hand. Condemned and
// Disposed are deliberately absent: retiring a machine stops its PM programme,
// so it is its own confirmed action rather than an option in a dropdown.
const STATUSES = [
  { value: 10, label: 'In store — received, not yet commissioned' },
  { value: 20, label: 'In service' },
  { value: 30, label: 'Under repair' },
];

export function EquipmentForm({
  editing,
  onCancel,
  onSaved,
}: {
  editing?: EquipmentDraft;
  onCancel: () => void;
  onSaved: (id: number) => void | Promise<void>;
}) {
  const [types, setTypes] = useState<Lookup[]>([]);
  const [locations, setLocations] = useState<LocationLookup[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [form, setForm] = useState<EquipmentDraft>(
    editing ?? {
      assetTag: '',
      serialNumber: null,
      equipmentTypeId: 0,
      locationId: 0,
      manufacturer: null,
      model: null,
      status: 20,
      purchaseDate: null,
      installationDate: null,
      warrantyExpiryDate: null,
      notes: null,
    },
  );

  useEffect(() => {
    void (async () => {
      try {
        const [t, l] = await Promise.all([
          api.get<Lookup[]>('/api/lookups/equipment-types'),
          api.get<LocationLookup[]>('/api/lookups/locations'),
        ]);
        setTypes(t);
        setLocations(l);
      } catch {
        setError('Could not load equipment types and locations.');
      }
    })();
  }, []);

  function set<K extends keyof EquipmentDraft>(key: K, value: EquipmentDraft[K]) {
    setForm((prev) => ({ ...prev, [key]: value }));
  }

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setBusy(true);
    try {
      const body = {
        assetTag: form.assetTag.trim(),
        serialNumber: form.serialNumber?.trim() || null,
        equipmentTypeId: Number(form.equipmentTypeId),
        locationId: Number(form.locationId),
        manufacturer: form.manufacturer?.trim() || null,
        model: form.model?.trim() || null,
        status: Number(form.status),
        purchaseDate: form.purchaseDate || null,
        installationDate: form.installationDate || null,
        warrantyExpiryDate: form.warrantyExpiryDate || null,
        notes: form.notes?.trim() || null,
      };

      if (editing?.id) {
        await api.put(`/api/equipment/${editing.id}`, body);
        await onSaved(editing.id);
      } else {
        const created = await api.post<{ id: number }>('/api/equipment', body);
        await onSaved(created.id);
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not save this machine.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="card stack" onSubmit={submit}>
      <h2 style={{ margin: 0, fontSize: '1.05rem' }}>
        {editing ? `Edit ${editing.assetTag}` : 'Add a machine'}
      </h2>

      {error && <p className="alert alert-error" role="alert">{error}</p>}

      <label className="stack">
        <span>Asset tag</span>
        <input
          className="field mono"
          required
          maxLength={40}
          placeholder="BME-00841"
          value={form.assetTag}
          onChange={(e) => set('assetTag', e.target.value)}
        />
        <span className="muted">
          The hospital's own number, as written on the sticker. It must be unique and it is
          what a QR label prints.
        </span>
      </label>

      <label className="stack">
        <span>Equipment type</span>
        <select
          className="field"
          required
          value={form.equipmentTypeId || ''}
          onChange={(e) => set('equipmentTypeId', Number(e.target.value))}
        >
          <option value="">Choose…</option>
          {types.map((t) => <option key={t.id} value={t.id}>{t.name}</option>)}
        </select>
        <span className="muted">
          Decides which PM checklist applies, so it is worth getting right.
        </span>
      </label>

      <label className="stack">
        <span>Location</span>
        <select
          className="field"
          required
          disabled={locations.length === 0}
          value={form.locationId || ''}
          onChange={(e) => set('locationId', Number(e.target.value))}
        >
          <option value="">Choose…</option>
          {locations.map((l) => (
            // The organisation and its sites stay in the list, so the tree still
            // reads as a tree, but cannot be chosen: the server refuses them, and
            // it used to be discovered only after filling in the rest of the form.
            <option key={l.id} value={l.id} disabled={l.level < FIRST_PLACEABLE_LEVEL}>
              {' '.repeat(l.depth * 2)}{l.name}
            </option>
          ))}
        </select>

        {/* A brand-new install has no locations, and every machine needs one.
            Without this, the first person to open this form on a fresh system
            meets an empty dropdown that explains nothing. Found by testing on
            a fresh install rather than the populated one. */}
        {locations.length === 0 && (
          <span className="muted">
            No locations yet, and every machine belongs to one. Add a ward or room on the
            Locations page first, or bring the whole tree in from a spreadsheet on the
            Import page.
          </span>
        )}
      </label>

      <label className="stack">
        <span>Status</span>
        <select
          className="field"
          value={form.status}
          onChange={(e) => set('status', Number(e.target.value))}
        >
          {STATUSES.map((s) => <option key={s.value} value={s.value}>{s.label}</option>)}
        </select>
      </label>

      <div style={{ display: 'grid', gap: '0.75rem', gridTemplateColumns: 'repeat(auto-fit, minmax(12rem, 1fr))' }}>
        <label className="stack">
          <span>Manufacturer</span>
          <input
            className="field"
            maxLength={120}
            placeholder="Mindray"
            value={form.manufacturer ?? ''}
            onChange={(e) => set('manufacturer', e.target.value || null)}
          />
        </label>

        <label className="stack">
          <span>Model</span>
          <input
            className="field"
            maxLength={120}
            placeholder="BeneHeart R3"
            value={form.model ?? ''}
            onChange={(e) => set('model', e.target.value || null)}
          />
        </label>

        <label className="stack">
          <span>Serial number</span>
          <input
            className="field mono"
            maxLength={120}
            value={form.serialNumber ?? ''}
            onChange={(e) => set('serialNumber', e.target.value || null)}
          />
          <span className="muted">Often blank on an older register. That is fine.</span>
        </label>
      </div>

      <div style={{ display: 'grid', gap: '0.75rem', gridTemplateColumns: 'repeat(auto-fit, minmax(11rem, 1fr))' }}>
        <label className="stack">
          <span>Purchased</span>
          <input
            className="field"
            type="date"
            value={form.purchaseDate ?? ''}
            onChange={(e) => set('purchaseDate', e.target.value || null)}
          />
        </label>

        <label className="stack">
          <span>Installed</span>
          <input
            className="field"
            type="date"
            value={form.installationDate ?? ''}
            onChange={(e) => set('installationDate', e.target.value || null)}
          />
        </label>

        <label className="stack">
          <span>Warranty expires</span>
          <input
            className="field"
            type="date"
            value={form.warrantyExpiryDate ?? ''}
            onChange={(e) => set('warrantyExpiryDate', e.target.value || null)}
          />
        </label>
      </div>

      <label className="stack">
        <span>Notes</span>
        <input
          className="field"
          maxLength={1000}
          placeholder="AMC with local vendor until March"
          value={form.notes ?? ''}
          onChange={(e) => set('notes', e.target.value || null)}
        />
      </label>

      <div style={{ display: 'flex', gap: '0.5rem' }}>
        <button className="btn btn-primary" disabled={busy}>
          {busy ? 'Saving…' : editing ? 'Save changes' : 'Add machine'}
        </button>
        <button type="button" className="btn" onClick={onCancel} disabled={busy}>Cancel</button>
      </div>
    </form>
  );
}
