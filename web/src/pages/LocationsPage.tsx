import { useCallback, useEffect, useMemo, useState } from 'react';
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

const COLLAPSED_KEY = 'hospitalpm.collapsedLocations';

export function LocationsPage() {
  const { can } = useAuth();
  const canEdit = can(ROLES.admin);

  const [items, setItems] = useState<Location[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [editing, setEditing] = useState<Location | null>(null);
  const [creatingUnder, setCreatingUnder] = useState<Location | null | undefined>(undefined);
  const [query, setQuery] = useState('');

  // Persisted so a hospital with two hundred rooms is not re-collapsing the
  // same six wards on every visit.
  const [collapsed, setCollapsed] = useState<Set<number>>(() => {
    try {
      const raw = localStorage.getItem(COLLAPSED_KEY);
      return new Set<number>(raw ? (JSON.parse(raw) as number[]) : []);
    } catch {
      return new Set();
    }
  });

  useEffect(() => {
    try {
      localStorage.setItem(COLLAPSED_KEY, JSON.stringify([...collapsed]));
    } catch {
      /* private window; collapse state is a convenience, not data */
    }
  }, [collapsed]);

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

  const childrenOf = useMemo(() => {
    const map = new Map<number | null, Location[]>();
    for (const l of items) {
      const list = map.get(l.parentId) ?? [];
      list.push(l);
      map.set(l.parentId, list);
    }
    return map;
  }, [items]);

  /** Assets in this location and everything under it. */
  const subtreeCounts = useMemo(() => {
    const totals = new Map<number, number>();

    const walk = (id: number): number => {
      if (totals.has(id)) return totals.get(id)!;
      const self = items.find((l) => l.id === id)?.equipmentCount ?? 0;
      const kids = (childrenOf.get(id) ?? []).reduce((sum, c) => sum + walk(c.id), 0);
      const total = self + kids;
      totals.set(id, total);
      return total;
    };

    for (const l of items) walk(l.id);
    return totals;
  }, [items, childrenOf]);

  /** Ids matching the search, plus every ancestor so matches stay reachable. */
  const matching = useMemo(() => {
    const term = query.trim().toLowerCase();
    if (!term) return null;

    const byId = new Map(items.map((l) => [l.id, l]));
    const keep = new Set<number>();

    for (const l of items) {
      if (l.name.toLowerCase().includes(term) || l.code.toLowerCase().includes(term)) {
        keep.add(l.id);
        // Walk up: a match five levels deep is useless if its parents are
        // filtered out and it never renders.
        let parent = l.parentId;
        while (parent !== null && !keep.has(parent)) {
          keep.add(parent);
          parent = byId.get(parent)?.parentId ?? null;
        }
      }
    }

    return keep;
  }, [items, query]);

  /** Depth-first order, honouring collapse — unless a search is active. */
  const visible = useMemo(() => {
    const out: Location[] = [];

    const walk = (parentId: number | null) => {
      for (const node of childrenOf.get(parentId) ?? []) {
        if (matching && !matching.has(node.id)) continue;
        out.push(node);
        // Searching expands everything; hiding a match behind a collapsed
        // parent would make the search look broken.
        if (matching || !collapsed.has(node.id)) walk(node.id);
      }
    };

    walk(null);
    return out;
  }, [childrenOf, collapsed, matching]);

  function toggle(id: number) {
    setCollapsed((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  async function remove(loc: Location) {
    if (!confirm(`Delete "${loc.name}"? This cannot be undone.`)) return;
    try {
      await api.del(`/api/locations/${loc.id}`);
      await load();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not delete the location.');
    }
  }

  const formOpen = editing !== null || creatingUnder !== undefined;

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
            <button
              className="btn btn-primary"
              onClick={() => { setCreatingUnder(null); setEditing(null); }}
            >
              Add top level
            </button>
          </div>
        )}
      </header>

      {error && <p className="alert alert-error" role="alert">{error}</p>}

      {formOpen && canEdit && (
        <LocationForm
          all={items}
          childrenOf={childrenOf}
          editing={editing}
          parent={creatingUnder ?? null}
          onCancel={() => { setCreatingUnder(undefined); setEditing(null); }}
          onSaved={async () => { setCreatingUnder(undefined); setEditing(null); await load(); }}
          onError={setError}
        />
      )}

      <div className="filters card">
        <input
          className="grow"
          placeholder="Search by name or code…"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
        />
        <button className="btn" onClick={() => setCollapsed(new Set())}>Expand all</button>
        <button
          className="btn"
          onClick={() => setCollapsed(new Set(items.filter((l) => l.childCount > 0).map((l) => l.id)))}
        >
          Collapse all
        </button>
      </div>

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

            {!loading && items.length > 0 && visible.length === 0 && (
              <tr><td colSpan={5} className="empty">Nothing matches “{query}”.</td></tr>
            )}

            {!loading && visible.map((l) => {
              const subtree = subtreeCounts.get(l.id) ?? 0;
              const hasChildren = l.childCount > 0;
              const isCollapsed = collapsed.has(l.id) && !matching;

              return (
                <tr key={l.id}>
                  <td style={{ paddingLeft: `${0.9 + l.depth * 1.35}rem` }}>
                    {hasChildren ? (
                      <button
                        className="tree-toggle"
                        aria-expanded={!isCollapsed}
                        aria-label={isCollapsed ? `Expand ${l.name}` : `Collapse ${l.name}`}
                        onClick={() => toggle(l.id)}
                      >
                        {isCollapsed ? '▸' : '▾'}
                      </button>
                    ) : (
                      <span className="tree-spacer" />
                    )}
                    {l.name}
                    {isCollapsed && hasChildren && (
                      <span className="muted"> ({l.childCount})</span>
                    )}
                  </td>
                  <td className="mono">{l.code}</td>
                  <td>
                    <span className="pill">{LEVEL_LABEL[l.level] ?? l.level}</span>
                    {l.level < EQUIPMENT_LEVEL && (
                      <span className="muted tree-note">no assets here</span>
                    )}
                  </td>
                  <td>
                    {/* Own count, then the subtree total, because "how much is
                        under this ward" is the question actually being asked. */}
                    {l.equipmentCount || <span className="muted">—</span>}
                    {subtree !== l.equipmentCount && (
                      <span className="muted tree-note">{subtree} below</span>
                    )}
                  </td>
                  {canEdit && (
                    <td>
                      <div className="row">
                        <button
                          className="btn btn-quiet"
                          onClick={() => { setCreatingUnder(l); setEditing(null); }}
                        >
                          Add inside
                        </button>
                        <button
                          className="btn btn-quiet"
                          onClick={() => { setEditing(l); setCreatingUnder(undefined); }}
                        >
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
              );
            })}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function LocationForm({
  all,
  childrenOf,
  editing,
  parent,
  onCancel,
  onSaved,
  onError,
}: {
  all: Location[];
  childrenOf: Map<number | null, Location[]>;
  editing: Location | null;
  parent: Location | null;
  onCancel: () => void;
  onSaved: () => void | Promise<void>;
  onError: (msg: string | null) => void;
}) {
  const initialParentId = editing ? editing.parentId : parent?.id ?? null;

  const [code, setCode] = useState(editing?.code ?? '');
  const [name, setName] = useState(editing?.name ?? '');
  const [parentId, setParentId] = useState<string>(initialParentId?.toString() ?? '');
  const [busy, setBusy] = useState(false);

  const parentLevel = useMemo(() => {
    const id = parentId ? Number(parentId) : null;
    return id === null ? null : all.find((l) => l.id === id)?.level ?? null;
  }, [all, parentId]);

  // Only levels deeper than the parent. The database refuses anything else,
  // so offering it would be offering a choice that can only fail.
  const levelOptions = useMemo(
    () => (parentLevel === null ? LEVELS : LEVELS.filter((l) => l.value > parentLevel)),
    [parentLevel],
  );

  const [level, setLevel] = useState<number>(
    editing?.level ?? (parent ? LEVELS.find((l) => l.value > parent.level)?.value ?? 60 : 20),
  );

  useEffect(() => {
    if (!levelOptions.some((l) => l.value === level) && levelOptions.length > 0) {
      setLevel(levelOptions[0].value);
    }
  }, [levelOptions, level]);

  /** Self and everything beneath it — moving into any of these makes a cycle. */
  const forbidden = useMemo(() => {
    if (!editing) return new Set<number>();

    const out = new Set<number>([editing.id]);
    const walk = (id: number) => {
      for (const child of childrenOf.get(id) ?? []) {
        out.add(child.id);
        walk(child.id);
      }
    };
    walk(editing.id);
    return out;
  }, [editing, childrenOf]);

  const parentOptions = all.filter((l) => !forbidden.has(l.id));

  async function submit(e: FormEvent) {
    e.preventDefault();
    onError(null);
    setBusy(true);
    try {
      const body = { code, name, level, parentId: parentId ? Number(parentId) : null };
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
        {editing
          ? `Edit ${editing.name}`
          : parent
            ? `Add inside ${parent.name}`
            : 'Add location'}
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
            {levelOptions.map((l) => (
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

      {editing && forbidden.size > 1 && (
        <p className="muted" style={{ margin: 0, fontSize: '0.85rem' }}>
          {forbidden.size - 1} location{forbidden.size === 2 ? '' : 's'} beneath this one are hidden
          from “Inside” — a location cannot be moved into its own subtree.
        </p>
      )}

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
