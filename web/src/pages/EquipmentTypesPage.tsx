import { useCallback, useEffect, useMemo, useState } from 'react';
import type { FormEvent } from 'react';
import { api } from '../api/client';
import { StatusPill } from '../StatusPill';

/**
 * The kinds of machine the hospital keeps, added and edited by an Administrator.
 *
 * The starter set covers most of what a hospital owns, but every hospital has a kind
 * of machine that is not in it, and a name that the biomedical team uses for one that
 * is. Without this a missing type meant a request to us, or a wrong type on the
 * machine and the wrong PM checklist with it.
 *
 * A type is never deleted: machines, schedules and checklists hang off it and must
 * stay readable for years. One nobody uses is switched off and leaves the pickers.
 */

type TypeRow = {
  id: number;
  code: string;
  name: string;
  description: string | null;
  isSeeded: boolean;
  isActive: boolean;
  machineCount: number;
  categories: { categoryId: number; name: string; isPrimary: boolean }[];
};

type Category = { id: number; code: string; name: string };

export function EquipmentTypesPage() {
  const [types, setTypes] = useState<TypeRow[]>([]);
  const [categories, setCategories] = useState<Category[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState<string | null>(null);
  const [editing, setEditing] = useState<TypeRow | 'new' | null>(null);
  const [query, setQuery] = useState('');
  const [showOff, setShowOff] = useState(false);

  const load = useCallback(async () => {
    setError(null);
    try {
      const [t, c] = await Promise.all([
        api.get<TypeRow[]>('/api/equipment-types'),
        api.get<Category[]>('/api/lookups/categories'),
      ]);
      setTypes(t);
      setCategories(c);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not load the equipment types.');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const shown = useMemo(() => {
    const q = query.trim().toLowerCase();
    return types.filter(
      (t) =>
        (showOff || t.isActive) &&
        (!q ||
          t.name.toLowerCase().includes(q) ||
          t.code.toLowerCase().includes(q) ||
          t.categories.some((c) => c.name.toLowerCase().includes(q))),
    );
  }, [types, query, showOff]);

  const switchedOff = types.filter((t) => !t.isActive).length;

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <h1>Equipment types</h1>
          <p className="muted">
            {types.length.toLocaleString('en-IN')} types
            {switchedOff > 0 && `, ${switchedOff} switched off`}
          </p>
        </div>
        {editing === null && (
          <button
            className="btn btn-primary"
            onClick={() => {
              setSaved(null);
              setEditing('new');
            }}
          >
            Add an equipment type
          </button>
        )}
      </header>

      {saved && <p className="alert alert-ok" role="status">{saved}</p>}
      {error && <p className="alert alert-error" role="alert">{error}</p>}

      {editing !== null && (
        <TypeForm
          // A new key per type, so opening a different one starts from its own values.
          key={editing === 'new' ? 'new' : editing.id}
          editing={editing === 'new' ? undefined : editing}
          categories={categories}
          onCancel={() => setEditing(null)}
          onSaved={async (message) => {
            setEditing(null);
            setSaved(message);
            await load();
          }}
        />
      )}

      <div className="card row" style={{ flexWrap: 'wrap', gap: '0.75rem', alignItems: 'center' }}>
        <input
          className="field"
          style={{ flex: '1 1 16rem' }}
          type="search"
          aria-label="Search equipment types"
          placeholder="Search by name, code or category"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
        />
        <label className="row" style={{ gap: '0.5rem', alignItems: 'center' }}>
          <input type="checkbox" checked={showOff} onChange={(e) => setShowOff(e.target.checked)} />
          <span>Show switched-off types</span>
        </label>
      </div>

      <div className="card table-wrap">
        <table className="table">
          <thead>
            <tr>
              <th>Name</th>
              <th>Code</th>
              <th>Categories</th>
              <th>Machines</th>
              <th>Status</th>
              <th aria-label="Actions" />
            </tr>
          </thead>
          <tbody>
            {loading && <tr><td colSpan={6} className="empty">Loading…</td></tr>}
            {!loading && shown.length === 0 && (
              <tr>
                <td colSpan={6} className="empty">
                  {types.length === 0
                    ? 'There are no equipment types yet.'
                    : 'No equipment type matches that.'}
                </td>
              </tr>
            )}
            {shown.map((t) => (
              <tr key={t.id}>
                <td>
                  <strong>{t.name}</strong>
                  {t.description && <div className="muted">{t.description}</div>}
                </td>
                <td className="mono">{t.code}</td>
                <td>
                  {t.categories.map((c, i) => (
                    <span key={c.categoryId}>
                      {i > 0 && ', '}
                      {c.isPrimary ? <strong title="Primary category">{c.name}</strong> : c.name}
                    </span>
                  ))}
                </td>
                <td>
                  {t.machineCount > 0 ? t.machineCount.toLocaleString('en-IN') : <span className="muted">0</span>}
                </td>
                <td>
                  <StatusPill tone={t.isActive ? 'success' : 'neutral'}>
                    {t.isActive ? 'Active' : 'Switched off'}
                  </StatusPill>
                  {!t.isSeeded && <div className="muted">Added by you</div>}
                </td>
                <td>
                  <button
                    className="btn"
                    aria-label={`Edit ${t.name}`}
                    onClick={() => {
                      setSaved(null);
                      setEditing(t);
                    }}
                  >
                    Edit
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function TypeForm({
  editing,
  categories,
  onCancel,
  onSaved,
}: {
  editing?: TypeRow;
  categories: Category[];
  onCancel: () => void;
  onSaved: (message: string) => void | Promise<void>;
}) {
  const [name, setName] = useState(editing?.name ?? '');
  const [description, setDescription] = useState(editing?.description ?? '');
  const [chosen, setChosen] = useState<Set<number>>(
    new Set(editing?.categories.map((c) => c.categoryId) ?? []),
  );
  const [primary, setPrimary] = useState<number | null>(
    editing?.categories.find((c) => c.isPrimary)?.categoryId ?? null,
  );
  const [isActive, setIsActive] = useState(editing?.isActive ?? true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  function toggle(id: number) {
    const next = new Set(chosen);
    if (next.has(id)) {
      next.delete(id);
      // The primary has to be one of the chosen categories; if it was just
      // unticked, the first one still ticked takes over rather than leaving none.
      if (primary === id) setPrimary(next.size > 0 ? [...next][0] : null);
    } else {
      next.add(id);
      // The first category ticked is the primary until someone says otherwise.
      if (primary === null) setPrimary(id);
    }
    setChosen(next);
  }

  async function submit(e: FormEvent) {
    e.preventDefault();
    setError(null);

    if (chosen.size === 0 || primary === null) {
      setError('Choose at least one category, and say which one is the primary.');
      return;
    }

    setBusy(true);
    try {
      const body = {
        name: name.trim(),
        description: description.trim() || null,
        categoryIds: [...chosen],
        primaryCategoryId: primary,
        isActive: editing ? isActive : true,
      };

      if (editing) {
        await api.put(`/api/equipment-types/${editing.id}`, body);
        await onSaved(`Saved ${body.name}.`);
      } else {
        await api.post('/api/equipment-types', body);
        await onSaved(`Added ${body.name}. It is in the list when you add a machine.`);
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not save this equipment type.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="card stack" onSubmit={submit}>
      <h2 style={{ margin: 0, fontSize: '1.05rem' }}>
        {editing ? `Edit ${editing.name}` : 'Add an equipment type'}
      </h2>

      {error && <p className="alert alert-error" role="alert">{error}</p>}

      <label className="stack">
        <span>Name</span>
        <input
          className="field"
          required
          maxLength={200}
          placeholder="Bedside monitor"
          value={name}
          onChange={(e) => setName(e.target.value)}
        />
        {editing && (
          <span className="muted">
            Renaming is safe. Its code, <span className="mono">{editing.code}</span>, stays the same,
            so a spreadsheet that uses it still imports.
          </span>
        )}
      </label>

      <label className="stack">
        <span>Description</span>
        <input
          className="field"
          maxLength={1000}
          placeholder="Optional. What it is, in a few words."
          value={description}
          onChange={(e) => setDescription(e.target.value)}
        />
      </label>

      <fieldset className="stack" style={{ border: 0, padding: 0, margin: 0 }}>
        <legend>Categories</legend>
        <span className="muted">
          A machine can belong to more than one, for example an ultrasound is both diagnostic and
          imaging. Tick all that apply, then choose the primary one, which is what reports group by.
        </span>
        <div className="stack" style={{ gap: '0.35rem' }}>
          {categories.map((c) => {
            const on = chosen.has(c.id);
            return (
              <div key={c.id} className="row" style={{ gap: '1rem', alignItems: 'center' }}>
                <label className="row" style={{ gap: '0.5rem', alignItems: 'center', flex: '1 1 14rem' }}>
                  <input type="checkbox" checked={on} onChange={() => toggle(c.id)} />
                  <span>{c.name}</span>
                </label>
                <label
                  className="row"
                  style={{ gap: '0.4rem', alignItems: 'center', visibility: on ? 'visible' : 'hidden' }}
                >
                  <input
                    type="radio"
                    name="primary-category"
                    disabled={!on}
                    checked={primary === c.id}
                    onChange={() => setPrimary(c.id)}
                  />
                  <span className="muted">Primary</span>
                </label>
              </div>
            );
          })}
        </div>
      </fieldset>

      {editing && (
        <label className="row" style={{ gap: '0.5rem', alignItems: 'center' }}>
          <input type="checkbox" checked={isActive} onChange={(e) => setIsActive(e.target.checked)} />
          <span>Active (untick to remove it from the list when adding a machine)</span>
        </label>
      )}

      <div style={{ display: 'flex', gap: '0.5rem' }}>
        <button className="btn btn-primary" disabled={busy}>
          {busy ? 'Saving…' : editing ? 'Save changes' : 'Add equipment type'}
        </button>
        <button type="button" className="btn" onClick={onCancel} disabled={busy}>Cancel</button>
      </div>
    </form>
  );
}
