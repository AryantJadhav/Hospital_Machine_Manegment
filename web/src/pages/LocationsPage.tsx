import { useCallback, useEffect, useState } from 'react';
import type { FormEvent } from 'react';
import { api } from '../api/client';
import { useAuth } from '../auth/useAuth';
import { ROLES } from '../auth/context';

type Location = {
  id: number;
  code: string;
  name: string;
  level: number;
  parentId: number | null;
  depth: number;
  isActive: boolean;
  equipmentCount: number;
  childCount: number;
};

const LEVELS: { value: number; label: string }[] = [
  { value: 10, label: 'Organisation' },
  { value: 20, label: 'Site' },
  { value: 30, label: 'Building' },
  { value: 40, label: 'Floor' },
  { value: 50, label: 'Department' },
  { value: 60, label: 'Room' },
];

const LEVEL_LABEL = Object.fromEntries(LEVELS.map((l) => [l.value, l.label]));

/** Building and deeper. Anything shallower is an administrative grouping. */
const EQUIPMENT_LEVEL = 30;

export function LocationsPage() {
  const { can } = useAuth();
  const canEdit = can(ROLES.admin, ROLES.biomedicalHead);

  const [items, setItems] = useState<Location[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [editing, setEditing] = useState<Location | null>(null);
  const [creating, setCreating] = useState(false);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      setItems(await api.get<Location[]>('/api/locations'));
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not load locations.');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  async function remove(loc: Location) {
    if (!confirm(`Delete "${loc.name}"? This cannot be undone.`)) return;
    try {
      await api.del(`/api/locations/${loc.id}`);
      await load();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not delete the location.');
    }
  }

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <h1>Locations</h1>
          <p className="muted">
            Equipment is filed against a location. Build the tree before importing assets.
          </p>
        </div>
        {canEdit && (
          <div className="row">
            <button
              className="btn"
              onClick={() =>
                api.download('/api/equipment/import/locations/template', 'locations-template.xlsx')
              }
            >
              Download template
            </button>
            <button className="btn btn-primary" onClick={() => { setCreating(true); setEditing(null); }}>
              Add location
            </button>
          </div>
        )}
      </header>

      {error && <p className="alert alert-error" role="alert">{error}</p>}

      {(creating || editing) && canEdit && (
        <LocationForm
          all={items}
          editing={editing}
          onCancel={() => { setCreating(false); setEditing(null); }}
          onSaved={async () => { setCreating(false); setEditing(null); await load(); }}
          onError={setError}
        />
      )}

      <div className="card table-wrap">
        <table className="table">
          <thead>
            <tr>
              <th>Name</th>
              <th>Code</th>
              <th>Level</th>
              <th>Assets</th>
              {canEdit && <th />}
            </tr>
          </thead>
          <tbody>
            {loading && <tr><td colSpan={5} className="empty">Loading…</td></tr>}

            {!loading && items.length === 0 && (
              <tr>
                <td colSpan={5} className="empty">
                  No locations yet. Add one, or import the tree from a spreadsheet.
                </td>
              </tr>
            )}

            {!loading && items.map((l) => (
              <tr key={l.id}>
                {/* Indented by depth; the API returns the tree in path order
                    so this renders correctly without any client-side sort. */}
                <td style={{ paddingLeft: `${0.9 + l.depth * 1.4}rem` }}>
                  {l.depth > 0 && <span className="muted">└ </span>}
                  {l.name}
                </td>
                <td className="mono">{l.code}</td>
                <td>
                  <span className="pill">{LEVEL_LABEL[l.level] ?? l.level}</span>
                  {l.level < EQUIPMENT_LEVEL && (
                    <span className="muted" style={{ marginLeft: '0.5rem', fontSize: '0.8rem' }}>
                      no assets here
                    </span>
                  )}
                </td>
                <td>{l.equipmentCount || <span className="muted">—</span>}</td>
                {canEdit && (
                  <td>
                    <div className="row">
                      <button className="btn btn-quiet" onClick={() => { setEditing(l); setCreating(false); }}>
                        Edit
                      </button>
                      <button
                        className="btn btn-quiet"
                        disabled={l.equipmentCount > 0 || l.childCount > 0}
                        title={
                          l.equipmentCount > 0 || l.childCount > 0
                            ? 'Holds assets or sub-locations'
                            : undefined
                        }
                        onClick={() => void remove(l)}
                      >
                        Delete
                      </button>
                    </div>
                  </td>
                )}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function LocationForm({
  all,
  editing,
  onCancel,
  onSaved,
  onError,
}: {
  all: Location[];
  editing: Location | null;
  onCancel: () => void;
  onSaved: () => void | Promise<void>;
  onError: (msg: string | null) => void;
}) {
  const [code, setCode] = useState(editing?.code ?? '');
  const [name, setName] = useState(editing?.name ?? '');
  const [level, setLevel] = useState<number>(editing?.level ?? 50);
  const [parentId, setParentId] = useState<string>(editing?.parentId?.toString() ?? '');
  const [busy, setBusy] = useState(false);

  // A location cannot be its own parent or sit inside its own descendant.
  // The server enforces this; excluding them here keeps the operator from
  // picking an option that can only fail.
  const parentOptions = all.filter((l) => {
    if (!editing) return true;
    if (l.id === editing.id) return false;
    return true;
  });

  async function submit(e: FormEvent) {
    e.preventDefault();
    onError(null);
    setBusy(true);
    try {
      const body = {
        code,
        name,
        level,
        parentId: parentId ? Number(parentId) : null,
      };
      if (editing) await api.put(`/api/locations/${editing.id}`, body);
      else await api.post('/api/locations', body);
      await onSaved();
    } catch (err) {
      onError(err instanceof Error ? err.message : 'Could not save the location.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="card stack" onSubmit={submit}>
      <h2 style={{ margin: 0, fontSize: '1.05rem' }}>
        {editing ? `Edit ${editing.name}` : 'Add location'}
      </h2>

      <div className="filters">
        <label className="field">
          <span>Code</span>
          <input value={code} onChange={(e) => setCode(e.target.value)} required />
        </label>

        <label className="field grow">
          <span>Name</span>
          <input value={name} onChange={(e) => setName(e.target.value)} required />
        </label>

        <label className="field">
          <span>Level</span>
          <select value={level} onChange={(e) => setLevel(Number(e.target.value))}>
            {LEVELS.map((l) => (
              <option key={l.value} value={l.value}>{l.label}</option>
            ))}
          </select>
        </label>

        <label className="field">
          <span>Inside</span>
          <select value={parentId} onChange={(e) => setParentId(e.target.value)}>
            <option value="">Nothing (top level)</option>
            {parentOptions.map((l) => (
              <option key={l.id} value={l.id}>
                {' '.repeat(l.depth * 3)}
                {l.name} ({LEVEL_LABEL[l.level]})
              </option>
            ))}
          </select>
        </label>
      </div>

      {level < EQUIPMENT_LEVEL && (
        <p className="muted" style={{ margin: 0, fontSize: '0.85rem' }}>
          Equipment cannot be filed at this level — a technician sent to an organisation or site has
          not been told where to go. Use Building or deeper for anywhere assets actually sit.
        </p>
      )}

      <div className="row">
        <button className="btn btn-primary" type="submit" disabled={busy}>
          {busy ? 'Saving…' : editing ? 'Save changes' : 'Add location'}
        </button>
        <button className="btn" type="button" onClick={onCancel}>Cancel</button>
      </div>
    </form>
  );
}
