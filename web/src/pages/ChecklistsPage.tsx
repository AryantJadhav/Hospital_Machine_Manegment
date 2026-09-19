import { useCallback, useEffect, useMemo, useState } from 'react';
import type { FormEvent } from 'react';
import { api } from '../api/client';
import { useAuth } from '../auth/useAuth';
import { ROLES } from '../auth/context';
import { formatDate, todayAtHospital } from '../time';

/**
 * Authoring the checklists a PM round is worked from.
 *
 * Until this page existed the API could do all of it and nothing could reach
 * it: a hospital could hold an equipment register and raise work orders, and
 * had no way to define a single preventive-maintenance check without someone
 * calling the API by hand. That is the loop the product is named for.
 *
 * Two properties of the domain drive the whole design here, and both are
 * enforced by database triggers rather than by this page:
 *
 *   1. A published version is frozen forever, because completions recorded
 *      under it must stay readable years later. Editing means a NEW version.
 *   2. An item's key is the identity of a measurement across versions.
 *      "flow_rate" in v1 and v7 are the same reading, which is what lets a
 *      hospital trend it. So keys are typed deliberately and never generated
 *      from the label.
 */

type Template = {
  id: number;
  equipmentTypeId: number;
  equipmentTypeName: string;
  code: string;
  name: string;
  description: string | null;
  isActive: boolean;
  publishedVersionNo: number | null;
  hasDraft: boolean;
  versionCount: number;
};

type ItemType = 10 | 20 | 30 | 40 | 50;

type Item = {
  key: string;
  label: string;
  type: ItemType;
  required: boolean;
  guidance?: string | null;
  unit?: string | null;
  min?: number | null;
  max?: number | null;
  options?: string[] | null;
};

type Section = { title: string; items: Item[] };
type Definition = { sections: Section[] };

/**
 * Gives every question without a key one, made from its wording.
 *
 * The key is what a reading is trended by across versions, so it matters to the
 * software and means nothing to the engineer typing "Casing intact". Publishing
 * used to refuse a blank one with a message about `sections[0].items[0].key`.
 * A key an author typed is never changed, and a generated one never collides
 * with another in the checklist, because answers are stored by key.
 */
function withKeys(definition: Definition): Definition {
  const used = new Set<string>();
  for (const section of definition.sections) {
    for (const item of section.items) {
      if (item.key.trim()) used.add(item.key.trim());
    }
  }

  return {
    ...definition,
    sections: definition.sections.map((section) => ({
      ...section,
      items: section.items.map((item) => {
        if (item.key.trim()) return item;

        let base = item.label
          .toLowerCase()
          .replace(/[^a-z0-9]+/g, '_')
          .replace(/^_+|_+$/g, '')
          .slice(0, 50)
          .replace(/_+$/, '');
        if (!/^[a-z]/.test(base)) base = base ? `check_${base}` : 'check';

        let key = base;
        for (let n = 2; used.has(key); n++) key = `${base}_${n}`;
        used.add(key);
        return { ...item, key };
      }),
    })),
  };
}

type Version = {
  id: number;
  versionNo: number;
  status: 10 | 20 | 30;
  definition: Definition;
  changeNote: string | null;
  publishedAtUtc: string | null;
  itemCount: number;
};

type Problem = { path: string; message: string };
type Lookup = { id: number; code: string; name: string };
type LocationLookup = Lookup & { depth: number };

type BulkResult = {
  created: number;
  alreadyScheduled: number;
  skippedRetired: number;
  skippedNotYetInService: number;
  considered: number;
};

const FREQUENCIES = [
  { value: 10, label: 'Monthly' },
  { value: 20, label: 'Quarterly' },
  { value: 30, label: 'Half-yearly' },
  { value: 40, label: 'Yearly' },
  { value: 90, label: 'Custom interval' },
];

const ITEM_TYPES: { value: ItemType; label: string; hint: string }[] = [
  { value: 10, label: 'Pass / Fail', hint: 'Pass, Fail or Not applicable.' },
  { value: 20, label: 'Yes / No', hint: 'A plain yes or no.' },
  { value: 30, label: 'Measurement', hint: 'A number, with a unit and an acceptable range.' },
  { value: 40, label: 'Text', hint: 'Free text, for an observation.' },
  { value: 50, label: 'Choice', hint: 'One of a fixed list.' },
];

const STATUS: Record<number, string> = { 10: 'Draft', 20: 'Published', 30: 'Archived' };

export function ChecklistsPage() {
  const { can } = useAuth();
  const canAuthor = can(ROLES.admin);

  const [templates, setTemplates] = useState<Template[]>([]);
  const [types, setTypes] = useState<Lookup[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const [editing, setEditing] = useState<Template | null>(null);
  const [scheduling, setScheduling] = useState<Template | null>(null);
  const [query, setQuery] = useState('');

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const [list, lookups] = await Promise.all([
        api.get<Template[]>('/api/checklists'),
        api.get<Lookup[]>('/api/lookups/equipment-types'),
      ]);
      setTemplates(list);
      setTypes(lookups);
      return list;
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not load checklists.');
      return [];
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const shown = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return templates;
    return templates.filter(
      (t) =>
        t.name.toLowerCase().includes(q) ||
        t.code.toLowerCase().includes(q) ||
        t.equipmentTypeName.toLowerCase().includes(q),
    );
  }, [templates, query]);

  if (editing) {
    return (
      <DraftEditor
        template={editing}
        onClose={async () => {
          setEditing(null);
          await load();
        }}
      />
    );
  }

  if (loading) return <div className="page"><p className="muted">Loading…</p></div>;

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <h1>Checklists</h1>
          <p className="muted">
            What a technician is asked to check on a PM round. One checklist per equipment
            type; publishing freezes it so past records stay readable.
          </p>
        </div>
        {canAuthor && (
          <button className="btn btn-primary" onClick={() => setCreating(true)}>
            New checklist
          </button>
        )}
      </header>

      {error && <p className="alert alert-error" role="alert">{error}</p>}

      {scheduling && (
        <ScheduleForm
          template={scheduling}
          onClose={() => setScheduling(null)}
          onError={setError}
        />
      )}

      {creating && (
        <TemplateForm
          types={types}
          existing={templates}
          onCancel={() => setCreating(false)}
          onSaved={async (createdId) => {
            setCreating(false);
            // Reloaded and looked up by id, because POST /api/checklists
            // returns only { id } - not the projected template. Opening the
            // editor on the response directly gave a header with an undefined
            // name and code, which is what testing it against the real API
            // rather than against my memory of it caught.
            const list = await load();
            const created = list.find((t) => t.id === createdId);
            // Straight into the editor when we have it. A checklist with no
            // questions is not a thing anyone wants to stop and admire.
            if (created) setEditing(created);
          }}
          onError={setError}
        />
      )}

      <div className="filters">
        <input
          className="field"
          placeholder="Search by name, code or equipment type"
          aria-label="Search checklists"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
        />
      </div>

      <div className="card table-wrap">
        <table className="table">
          <thead>
            <tr>
              <th>Equipment type</th>
              <th>Checklist</th>
              <th>Published</th>
              <th>Draft</th>
              <th>Versions</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {shown.length === 0 && (
              <tr>
                <td colSpan={6} className="empty">
                  {templates.length === 0
                    ? 'No checklists yet. A PM cannot be scheduled until its equipment type has one.'
                    : 'Nothing matches that search.'}
                </td>
              </tr>
            )}

            {shown.map((t) => (
              <tr key={t.id}>
                <td>{t.equipmentTypeName}</td>
                <td>
                  <div>{t.name}</div>
                  <div className="mono muted">{t.code}</div>
                </td>
                <td>
                  {t.publishedVersionNo !== null
                    ? <span className="pill dg-10">v{t.publishedVersionNo}</span>
                    : <span className="muted">not published</span>}
                </td>
                <td>
                  {t.hasDraft
                    ? <span className="pill dg-20">unpublished changes</span>
                    : <span className="muted">—</span>}
                </td>
                <td>{t.versionCount}</td>
                <td style={{ whiteSpace: 'nowrap' }}>
                  {/* Offered here rather than on a separate setup screen: the
                      checklist decides which machines get scheduled, so the
                      action belongs beside the thing it acts on. Only once
                      published — scheduling a draft would promise a technician
                      work with no questions in it. */}
                  {canAuthor && t.publishedVersionNo !== null && (
                    <button className="btn btn-quiet" onClick={() => setScheduling(t)}>
                      Schedule…
                    </button>
                  )}
                  <button className="btn btn-quiet" onClick={() => setEditing(t)}>
                    {canAuthor ? 'Edit' : 'View'}
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

/**
 * Puts one published checklist onto every machine of its type, in one action.
 *
 * The reason this exists: schedules were created one machine at a time, so a
 * hospital with 2,000 assets faced 2,000 operations to set up preventive
 * maintenance. That is not a slow path — it is a path nobody walks, which is
 * why the product could hold an equipment register and never schedule a PM.
 *
 * The result is reported rather than assumed. "Created 47" on its own leaves
 * an operator wondering about the other nine, so what was skipped is named
 * along with why.
 */
function ScheduleForm({
  template,
  onClose,
  onError,
}: {
  template: Template;
  onClose: () => void;
  onError: (msg: string | null) => void;
}) {
  const [locations, setLocations] = useState<LocationLookup[]>([]);
  const [frequency, setFrequency] = useState(20);
  const [intervalDays, setIntervalDays] = useState('90');
  const [anchorDate, setAnchorDate] = useState(todayAtHospital);
  const [graceDays, setGraceDays] = useState('7');
  const [locationId, setLocationId] = useState('');
  const [includeInStore, setIncludeInStore] = useState(false);
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<BulkResult | null>(null);

  useEffect(() => {
    void (async () => {
      try {
        setLocations(await api.get<LocationLookup[]>('/api/lookups/locations'));
      } catch {
        // The whole-hospital default still works without the list, so a
        // failed lookup should not block scheduling.
      }
    })();
  }, []);

  async function submit(e: FormEvent) {
    e.preventDefault();
    onError(null);
    setBusy(true);
    try {
      setResult(await api.post<BulkResult>('/api/pm/schedules/bulk', {
        checklistTemplateId: template.id,
        frequency,
        intervalDays: frequency === 90 ? Number(intervalDays) : 0,
        anchorDate,
        graceDays: Number(graceDays),
        locationId: locationId ? Number(locationId) : null,
        includeInStore,
      }));
    } catch (err) {
      onError(err instanceof Error ? err.message : 'Could not schedule this checklist.');
    } finally {
      setBusy(false);
    }
  }

  if (result) {
    return (
      <div className="card stack">
        <h2 style={{ margin: 0, fontSize: '1.05rem' }}>Scheduled {template.name}</h2>

        <p className={result.created > 0 ? 'alert alert-ok' : 'alert'}>
          {result.created > 0
            ? `${result.created} machine${result.created === 1 ? '' : 's'} now on this schedule.`
            : 'Nothing new to schedule.'}
        </p>

        <dl className="detail">
          <dt>Machines of this type</dt>
          <dd>{result.considered}</dd>
          <dt>Newly scheduled</dt>
          <dd>{result.created}</dd>
          {result.alreadyScheduled > 0 && (
            <>
              <dt>Already scheduled</dt>
              <dd>{result.alreadyScheduled} — left alone</dd>
            </>
          )}
          {result.skippedNotYetInService > 0 && (
            <>
              <dt>Still in store</dt>
              <dd>{result.skippedNotYetInService} — not yet in service</dd>
            </>
          )}
          {result.skippedRetired > 0 && (
            <>
              <dt>Condemned or disposed</dt>
              <dd>{result.skippedRetired} — never maintained again</dd>
            </>
          )}
        </dl>

        <button className="btn btn-primary" onClick={onClose}>Done</button>
      </div>
    );
  }

  return (
    <form className="card stack" onSubmit={submit}>
      <h2 style={{ margin: 0, fontSize: '1.05rem' }}>Schedule {template.name}</h2>
      <p className="muted" style={{ margin: 0 }}>
        Puts this checklist on every {template.equipmentTypeName.toLowerCase()} in the
        hospital. Machines already on it are left alone, so this is safe to run again
        after commissioning more.
      </p>

      <label className="stack">
        <span>How often</span>
        <select
          className="field"
          value={frequency}
          onChange={(e) => setFrequency(Number(e.target.value))}
        >
          {FREQUENCIES.map((f) => (
            <option key={f.value} value={f.value}>{f.label}</option>
          ))}
        </select>
      </label>

      {frequency === 90 && (
        <label className="stack">
          <span>Interval in days</span>
          <input
            className="field"
            type="number"
            min={1}
            required
            value={intervalDays}
            onChange={(e) => setIntervalDays(e.target.value)}
          />
        </label>
      )}

      <label className="stack">
        <span>First due</span>
        <input
          className="field"
          type="date"
          required
          value={anchorDate}
          onChange={(e) => setAnchorDate(e.target.value)}
        />
        <span className="muted">
          Occurrences are counted from this date. Set it in the past and the PMs that
          were missed appear as overdue, which is what an auditor expects to see.
        </span>
      </label>

      <label className="stack">
        <span>Grace period in days</span>
        <input
          className="field"
          type="number"
          min={0}
          required
          value={graceDays}
          onChange={(e) => setGraceDays(e.target.value)}
        />
        <span className="muted">How long after the due date a PM stays merely due, not overdue.</span>
      </label>

      <label className="stack">
        <span>Limit to part of the hospital (optional)</span>
        <select className="field" value={locationId} onChange={(e) => setLocationId(e.target.value)}>
          <option value="">Everywhere</option>
          {locations.map((l) => (
            <option key={l.id} value={l.id}>
              {' '.repeat(l.depth * 2)}{l.name}
            </option>
          ))}
        </select>
        <span className="muted">Includes everything beneath the place you choose.</span>
      </label>

      <label style={{ display: 'flex', gap: '0.4rem', alignItems: 'center' }}>
        <input
          type="checkbox"
          checked={includeInStore}
          onChange={(e) => setIncludeInStore(e.target.checked)}
        />
        <span>Also schedule machines still in store</span>
      </label>

      <div style={{ display: 'flex', gap: '0.5rem' }}>
        <button className="btn btn-primary" disabled={busy}>
          {busy ? 'Scheduling…' : 'Schedule'}
        </button>
        <button type="button" className="btn" onClick={onClose} disabled={busy}>
          Cancel
        </button>
      </div>
    </form>
  );
}

function TemplateForm({
  types,
  existing,
  onCancel,
  onSaved,
  onError,
}: {
  types: Lookup[];
  existing: Template[];
  onCancel: () => void;
  onSaved: (createdId: number) => void | Promise<void>;
  onError: (msg: string | null) => void;
}) {
  const [equipmentTypeId, setEquipmentTypeId] = useState('');
  const [code, setCode] = useState('');
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [busy, setBusy] = useState(false);

  // A type that already has a checklist is not offered again. The server has
  // its own rule; this stops the operator picking something that can only be
  // rejected.
  const taken = useMemo(() => new Set(existing.map((t) => t.equipmentTypeId)), [existing]);
  const available = types.filter((t) => !taken.has(t.id));

  async function submit(e: FormEvent) {
    e.preventDefault();
    onError(null);
    setBusy(true);
    try {
      const created = await api.post<{ id: number }>("/api/checklists", {
        equipmentTypeId: Number(equipmentTypeId),
        code: code.trim(),
        name: name.trim(),
        description: description.trim() || null,
      });
      await onSaved(created.id);
    } catch (err) {
      onError(err instanceof Error ? err.message : 'Could not create the checklist.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="card stack" onSubmit={submit}>
      <h2 style={{ margin: 0, fontSize: '1.05rem' }}>New checklist</h2>

      <label className="stack">
        <span>Equipment type</span>
        <select
          className="field"
          required
          value={equipmentTypeId}
          onChange={(e) => setEquipmentTypeId(e.target.value)}
        >
          <option value="">Choose…</option>
          {available.map((t) => (
            <option key={t.id} value={t.id}>{t.name}</option>
          ))}
        </select>
        {available.length === 0 && (
          <span className="muted">Every equipment type already has a checklist.</span>
        )}
      </label>

      <label className="stack">
        <span>Code</span>
        <input
          className="field mono"
          required
          maxLength={40}
          placeholder="pm-defib-annual"
          value={code}
          onChange={(e) => setCode(e.target.value)}
        />
        <span className="muted">Short, stable, and yours. It never changes.</span>
      </label>

      <label className="stack">
        <span>Name</span>
        <input
          className="field"
          required
          maxLength={120}
          placeholder="Defibrillator — annual PM"
          value={name}
          onChange={(e) => setName(e.target.value)}
        />
      </label>

      <label className="stack">
        <span>Description (optional)</span>
        <input
          className="field"
          maxLength={400}
          value={description}
          onChange={(e) => setDescription(e.target.value)}
        />
      </label>

      <div style={{ display: 'flex', gap: '0.5rem' }}>
        <button className="btn btn-primary" disabled={busy || !equipmentTypeId}>
          {busy ? 'Creating…' : 'Create and add questions'}
        </button>
        <button type="button" className="btn" onClick={onCancel} disabled={busy}>
          Cancel
        </button>
      </div>
    </form>
  );
}

/**
 * Edits the draft version of one checklist.
 *
 * The whole definition is held locally and saved as a unit, because that is
 * how the API stores it — a version's definition is one jsonb document, not a
 * table of rows. It also means an operator can restructure a checklist and
 * abandon it without leaving half a change behind.
 */
function DraftEditor({
  template,
  onClose,
}: {
  template: Template;
  onClose: () => void | Promise<void>;
}) {
  const { can } = useAuth();
  const canAuthor = can(ROLES.admin);

  const [versions, setVersions] = useState<Version[]>([]);
  const [definition, setDefinition] = useState<Definition>({ sections: [] });
  const [changeNote, setChangeNote] = useState('');
  const [problems, setProblems] = useState<Problem[]>([]);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [dirty, setDirty] = useState(false);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const list = await api.get<Version[]>(`/api/checklists/${template.id}/versions`);
      setVersions(list);

      const draft = list.find((v) => v.status === 10);
      const published = list.find((v) => v.status === 20);

      // A template with no draft opens on a copy of what is published, so
      // "change one line of the annual PM" is one edit rather than retyping
      // forty questions. Nothing is saved until Save is pressed.
      const source = draft ?? published;
      setDefinition(source ? structuredClone(source.definition) : { sections: [] });
      setChangeNote(draft?.changeNote ?? '');
      setDirty(!draft && !!published);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not load this checklist.');
    } finally {
      setLoading(false);
    }
  }, [template.id]);

  useEffect(() => {
    void load();
  }, [load]);

  const published = versions.find((v) => v.status === 20);
  const itemCount = definition.sections.reduce((n, s) => n + s.items.length, 0);

  function update(next: Definition) {
    setDefinition(next);
    setDirty(true);
    setNotice(null);
  }

  async function save() {
    setBusy('save');
    setError(null);
    try {
      await api.put(`/api/checklists/${template.id}/draft`, {
        definition: withKeys(definition),
        changeNote: changeNote.trim() || null,
      });
      setDirty(false);
      setNotice('Draft saved.');
      await load();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not save the draft.');
    } finally {
      setBusy(null);
    }
  }

  async function check() {
    setBusy('check');
    setError(null);
    try {
      const result = await api.post<{ valid: boolean; problems: Problem[] }>(
        `/api/checklists/${template.id}/validate`, {});
      setProblems(result.problems);
      setNotice(result.valid ? 'This checklist is ready to publish.' : null);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not check the draft.');
    } finally {
      setBusy(null);
    }
  }

  async function publish() {
    // Typed, not clicked. Publishing is irreversible: the version freezes and
    // every PM recorded from now on is tied to it.
    const typed = prompt(
      [
        `Publish ${template.name}?`,
        '',
        'A published checklist can never be edited. Changing it later means a',
        'new version, and past records stay tied to the version they were',
        'filled under.',
        '',
        'Type PUBLISH to continue:',
      ].join('\n'),
    );
    if (typed !== 'PUBLISH') return;

    setBusy('publish');
    setError(null);
    try {
      await api.post(`/api/checklists/${template.id}/publish`, {});
      setProblems([]);
      setNotice('Published. PM schedules can now use this checklist.');
      await load();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not publish.');
    } finally {
      setBusy(null);
    }
  }

  if (loading) return <div className="page"><p className="muted">Loading…</p></div>;

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <button className="btn btn-quiet" onClick={() => void onClose()}>← All checklists</button>
          <h1 style={{ marginBottom: 0 }}>{template.name}</h1>
          <p className="muted">
            {template.equipmentTypeName} · <span className="mono">{template.code}</span>
            {published ? ` · published v${published.versionNo}` : ' · never published'}
          </p>
        </div>
      </header>

      {error && <p className="alert alert-error" role="alert">{error}</p>}
      {notice && <p className="alert alert-ok" role="status">{notice}</p>}

      {problems.length > 0 && (
        <div className="alert alert-error">
          <strong>This checklist cannot be published yet.</strong>
          <ul style={{ margin: '0.5rem 0 0', paddingLeft: '1.2rem' }}>
            {problems.map((p, i) => (
              <li key={i}>
                <span className="mono">{p.path}</span> — {p.message}
              </li>
            ))}
          </ul>
        </div>
      )}

      {published && (
        <p className="alert alert-ok">
          Version {published.versionNo} is live with {published.itemCount} check
          {published.itemCount === 1 ? '' : 's'}. Editing here builds the next version; the
          live one is untouched until you publish.
        </p>
      )}

      {definition.sections.map((section, si) => (
        <SectionCard
          key={si}
          section={section}
          readOnly={!canAuthor}
          onChange={(next) => {
            const sections = [...definition.sections];
            sections[si] = next;
            update({ sections });
          }}
          onRemove={() => {
            const sections = definition.sections.filter((_, i) => i !== si);
            update({ sections });
          }}
          onMove={(delta) => {
            const target = si + delta;
            if (target < 0 || target >= definition.sections.length) return;
            const sections = [...definition.sections];
            [sections[si], sections[target]] = [sections[target], sections[si]];
            update({ sections });
          }}
        />
      ))}

      {canAuthor && (
        <div className="card stack">
          <button
            className="btn"
            onClick={() =>
              update({
                sections: [...definition.sections, { title: 'New section', items: [] }],
              })
            }
          >
            Add a section
          </button>

          <label className="stack">
            <span>What changed (optional)</span>
            <input
              className="field"
              maxLength={400}
              placeholder="Added leakage-current check after the 2026 audit"
              value={changeNote}
              onChange={(e) => { setChangeNote(e.target.value); setDirty(true); }}
            />
            <span className="muted">
              Recorded against the version. Whoever reads this in three years will not
              remember why.
            </span>
          </label>

          <div style={{ display: 'flex', gap: '0.5rem', flexWrap: 'wrap' }}>
            <button className="btn btn-primary" onClick={() => void save()} disabled={busy !== null}>
              {busy === 'save' ? 'Saving…' : 'Save draft'}
            </button>
            <button className="btn" onClick={() => void check()} disabled={busy !== null || dirty}>
              {busy === 'check' ? 'Checking…' : 'Check'}
            </button>
            <button
              className="btn"
              onClick={() => void publish()}
              disabled={busy !== null || dirty || itemCount === 0}
            >
              {busy === 'publish' ? 'Publishing…' : 'Publish'}
            </button>
            <span className="muted" style={{ alignSelf: 'center' }}>
              {dirty
                ? 'Unsaved changes — save before checking or publishing.'
                : `${itemCount} check${itemCount === 1 ? '' : 's'} in ${definition.sections.length} section${definition.sections.length === 1 ? '' : 's'}.`}
            </span>
          </div>
        </div>
      )}

      {versions.length > 0 && (
        <div className="card table-wrap">
          <h2 className="section-h">History</h2>
          <table className="table">
            <thead>
              <tr><th>Version</th><th>Status</th><th>Checks</th><th>Published</th><th>What changed</th></tr>
            </thead>
            <tbody>
              {versions.map((v) => (
                <tr key={v.id}>
                  <td>{v.versionNo === 0 ? <span className="muted">draft</span> : `v${v.versionNo}`}</td>
                  <td>{STATUS[v.status] ?? '—'}</td>
                  <td>{v.itemCount}</td>
                  <td>{v.publishedAtUtc ? formatDate(v.publishedAtUtc) : <span className="muted">—</span>}</td>
                  <td>{v.changeNote ?? <span className="muted">—</span>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

function SectionCard({
  section,
  readOnly,
  onChange,
  onRemove,
  onMove,
}: {
  section: Section;
  readOnly: boolean;
  onChange: (next: Section) => void;
  onRemove: () => void;
  onMove: (delta: number) => void;
}) {
  return (
    <div className="card stack">
      <div style={{ display: 'flex', gap: '0.5rem', alignItems: 'center' }}>
        <input
          className="field"
          style={{ fontWeight: 600 }}
          aria-label="Section title"
          value={section.title}
          readOnly={readOnly}
          maxLength={120}
          onChange={(e) => onChange({ ...section, title: e.target.value })}
        />
        {!readOnly && (
          <>
            <button className="btn btn-quiet" onClick={() => onMove(-1)} title="Move up">↑</button>
            <button className="btn btn-quiet" onClick={() => onMove(1)} title="Move down">↓</button>
            <button className="btn btn-quiet" onClick={onRemove}>Remove</button>
          </>
        )}
      </div>

      {section.items.map((item, ii) => (
        <ItemRow
          key={ii}
          item={item}
          readOnly={readOnly}
          onChange={(next) => {
            const items = [...section.items];
            items[ii] = next;
            onChange({ ...section, items });
          }}
          onRemove={() => onChange({ ...section, items: section.items.filter((_, i) => i !== ii) })}
        />
      ))}

      {section.items.length === 0 && (
        <p className="muted" style={{ margin: 0 }}>
          No checks yet. A section with no checks cannot be published.
        </p>
      )}

      {!readOnly && (
        <button
          className="btn btn-quiet"
          onClick={() =>
            onChange({
              ...section,
              items: [
                ...section.items,
                { key: '', label: '', type: 10, required: true },
              ],
            })
          }
        >
          Add a check
        </button>
      )}
    </div>
  );
}

function ItemRow({
  item,
  readOnly,
  onChange,
  onRemove,
}: {
  item: Item;
  readOnly: boolean;
  onChange: (next: Item) => void;
  onRemove: () => void;
}) {
  const typeHint = ITEM_TYPES.find((t) => t.value === item.type)?.hint ?? '';

  return (
    <div className="card stack" style={{ background: 'transparent' }}>
      <div style={{ display: 'grid', gap: '0.5rem', gridTemplateColumns: 'minmax(0,2fr) minmax(0,1fr) auto' }}>
        <input
          className="field"
          placeholder="What the technician reads"
          aria-label="What the technician reads"
          value={item.label}
          readOnly={readOnly}
          maxLength={200}
          onChange={(e) => onChange({ ...item, label: e.target.value })}
        />
        <select
          className="field"
          aria-label="Type of answer"
          value={item.type}
          disabled={readOnly}
          onChange={(e) => onChange({ ...item, type: Number(e.target.value) as ItemType })}
        >
          {ITEM_TYPES.map((t) => (
            <option key={t.value} value={t.value}>{t.label}</option>
          ))}
        </select>
        {!readOnly && <button className="btn btn-quiet" onClick={onRemove}>Remove</button>}
      </div>

      <div style={{ display: 'grid', gap: '0.5rem', gridTemplateColumns: 'minmax(0,1fr) auto' }}>
        <input
          className="field mono"
          placeholder="Key — made from the question if left blank"
          aria-label="Key"
          value={item.key}
          readOnly={readOnly}
          maxLength={60}
          onChange={(e) => onChange({ ...item, key: e.target.value })}
        />
        <label style={{ display: 'flex', gap: '0.35rem', alignItems: 'center' }}>
          <input
            type="checkbox"
            checked={item.required}
            disabled={readOnly}
            onChange={(e) => onChange({ ...item, required: e.target.checked })}
          />
          <span>Required</span>
        </label>
      </div>

      {/* Said here rather than in documentation nobody opens. Getting this
          wrong is silent: the checklist still works, and four years of a
          reading stop lining up. */}
      <p className="muted" style={{ margin: 0 }}>
        The key identifies this reading across versions — keep it identical in the next
        version and the hospital can trend it. {typeHint}
      </p>

      {item.type === 30 && (
        <div style={{ display: 'grid', gap: '0.5rem', gridTemplateColumns: 'repeat(3, minmax(0,1fr))' }}>
          <input
            className="field"
            placeholder="Unit, e.g. mA"
            aria-label="Unit"
            value={item.unit ?? ''}
            readOnly={readOnly}
            onChange={(e) => onChange({ ...item, unit: e.target.value || null })}
          />
          <input
            className="field"
            type="number"
            placeholder="Min"
            aria-label="Lowest acceptable reading"
            value={item.min ?? ''}
            readOnly={readOnly}
            onChange={(e) => onChange({ ...item, min: e.target.value === '' ? null : Number(e.target.value) })}
          />
          <input
            className="field"
            type="number"
            placeholder="Max"
            aria-label="Highest acceptable reading"
            value={item.max ?? ''}
            readOnly={readOnly}
            onChange={(e) => onChange({ ...item, max: e.target.value === '' ? null : Number(e.target.value) })}
          />
        </div>
      )}

      {item.type === 50 && (
        <input
          className="field"
          placeholder="Options, separated by commas"
          value={(item.options ?? []).join(', ')}
          readOnly={readOnly}
          onChange={(e) =>
            onChange({
              ...item,
              options: e.target.value
                .split(',')
                .map((o) => o.trim())
                .filter((o) => o.length > 0),
            })
          }
        />
      )}

      <input
        className="field"
        placeholder="Guidance (optional) — the how, where it is not obvious"
        aria-label="Guidance"
        value={item.guidance ?? ''}
        readOnly={readOnly}
        maxLength={400}
        onChange={(e) => onChange({ ...item, guidance: e.target.value || null })}
      />
    </div>
  );
}

