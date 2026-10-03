import { useCallback, useEffect, useMemo, useState } from 'react';
import { api } from '../api/client';
import { ROLE_LABEL } from '../auth/context';
import { groupCatalog } from '../accessTypes';
import type { AccessEffect, AccessView, CatalogItem } from '../accessTypes';
import { formatDate, todayAtHospital } from '../time';

type Person = {
  id: number;
  userName: string;
  fullName: string;
  role: string;
  isActive: boolean;
  accessGiven: number;
  accessTakenAway: number;
};

/** What is chosen for one section: nothing (the role decides), give it, or take it away. */
type Choice = { effect: AccessEffect; expiresOn: string; note: string } | null;

/**
 * Access: give one person a section their role does not, or take one away.
 *
 * Only the Developer sees this page. It is on show to the hospital in every other way: the Staff page
 * lists what each person has been given or had taken away, and each change is in the audit log.
 */
export function AccessPage() {
  const [people, setPeople] = useState<Person[] | null>(null);
  const [catalog, setCatalog] = useState<CatalogItem[]>([]);
  const [selected, setSelected] = useState<number | null>(null);
  const [view, setView] = useState<AccessView | null>(null);
  const [choices, setChoices] = useState<Record<string, Choice>>({});
  const [filter, setFilter] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const loadPeople = useCallback(async () => {
    try {
      setPeople(await api.get<Person[]>('/api/users'));
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not load the staff list.');
    }
  }, []);

  useEffect(() => {
    void loadPeople();
    void (async () => {
      try {
        setCatalog(await api.get<CatalogItem[]>('/api/access/catalog'));
      } catch (e) {
        setError(e instanceof Error ? e.message : 'Could not load the list of sections.');
      }
    })();
  }, [loadPeople]);

  function show(v: AccessView) {
    setView(v);
    const next: Record<string, Choice> = {};
    for (const g of v.grants) {
      next[g.permission] = { effect: g.effect, expiresOn: g.expiresOn ?? '', note: g.note ?? '' };
    }
    setChoices(next);
  }

  async function open(id: number) {
    setSelected(id);
    setView(null);
    setError(null);
    setNotice(null);
    try {
      show(await api.get<AccessView>(`/api/access/users/${id}`));
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not open that person.');
    }
  }

  async function save() {
    if (!view) return;
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      const grants = Object.entries(choices)
        .filter((entry): entry is [string, NonNullable<Choice>] => entry[1] !== null)
        .map(([permission, c]) => ({
          permission,
          effect: c.effect,
          expiresOn: c.expiresOn || null,
          note: c.note.trim() || null,
        }));
      show(await api.put<AccessView>(`/api/access/users/${view.user.id}`, { grants }));
      setNotice(`Saved. ${view.user.fullName}'s access has changed now: it takes effect on their next click.`);
      await loadPeople();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not save.');
    } finally {
      setBusy(false);
    }
  }

  function setChoice(permission: string, choice: Choice) {
    setChoices((prev) => ({ ...prev, [permission]: choice }));
  }

  const shown = useMemo(() => {
    const q = filter.trim().toLowerCase();
    return (people ?? [])
      .filter((p) => p.isActive)
      .filter((p) => !q || p.fullName.toLowerCase().includes(q) || p.userName.toLowerCase().includes(q));
  }, [people, filter]);

  const groups = useMemo(() => groupCatalog(catalog), [catalog]);
  const today = todayAtHospital();

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <h1>Access</h1>
          <p className="muted">
            Give one person a section their role does not, or take one away. The Head of Biomedical and the IT
            team can see everything given here on the Staff page, and every change is kept in the audit log.
          </p>
        </div>
      </header>

      {error && <p className="alert alert-error" role="alert">{error}</p>}
      {notice && <p className="alert alert-ok" role="status">{notice}</p>}

      <div style={{ display: 'grid', gap: '1rem', gridTemplateColumns: 'minmax(14rem, 18rem) 1fr', alignItems: 'start' }}>
        <section className="card stack" aria-label="People">
          <input
            type="search"
            aria-label="Find a person"
            placeholder="Find a person"
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
          />
          {!people && <p className="muted">Loading…</p>}
          <ul style={{ listStyle: 'none', margin: 0, padding: 0, display: 'grid', gap: '0.25rem' }}>
            {shown.map((p) => (
              <li key={p.id}>
                <button
                  type="button"
                  className={selected === p.id ? 'btn btn-primary' : 'btn'}
                  style={{ width: '100%', textAlign: 'left' }}
                  aria-pressed={selected === p.id}
                  onClick={() => void open(p.id)}
                >
                  {p.fullName}
                  <span style={{ display: 'block', fontSize: '0.8rem', opacity: 0.8 }}>
                    {ROLE_LABEL[p.role] ?? p.role}
                    {(p.accessGiven > 0 || p.accessTakenAway > 0) &&
                      ` · +${p.accessGiven} / −${p.accessTakenAway}`}
                  </span>
                </button>
              </li>
            ))}
          </ul>
        </section>

        <section className="stack" aria-label="Access for the chosen person">
          {!selected && <p className="muted">Choose a person to see what they can do.</p>}
          {selected && !view && !error && <p className="muted">Loading…</p>}

          {view && (
            <>
              <div className="card stack">
                <h2 className="section-h" style={{ margin: 0 }}>{view.user.fullName}</h2>
                <p className="muted" style={{ margin: 0 }}>
                  {ROLE_LABEL[view.user.role ?? ''] ?? view.user.role} · {view.user.userName}
                </p>
                {view.locked && <p className="alert alert-info" role="status">{view.locked}</p>}
              </div>

              {groups.map(({ group, items }) => (
                <div key={group} className="card table-wrap">
                  <table className="table">
                    <caption className="table-caption">{group}</caption>
                    <thead>
                      <tr>
                        <th>Section</th>
                        <th>Their role</th>
                        <th>Access</th>
                        <th>Until (optional)</th>
                        <th>Why (optional)</th>
                      </tr>
                    </thead>
                    <tbody>
                      {items.map((item) => {
                        const inRole = view.rolePermissions.includes(item.permission);
                        const choice = choices[item.permission] ?? null;
                        const existing = view.grants.find((g) => g.permission === item.permission);
                        return (
                          <tr key={item.permission}>
                            <td>
                              {item.label}
                              <div className="muted">{item.help}</div>
                              {existing?.expired && choice && (
                                <div className="muted">Ran out on {formatDate(existing.expiresOn)}.</div>
                              )}
                            </td>
                            <td>{inRole ? 'Yes' : <span className="muted">No</span>}</td>
                            <td>
                              <select
                                aria-label={`Access to ${item.label}`}
                                disabled={view.locked !== null}
                                value={choice ? choice.effect : ''}
                                onChange={(e) => {
                                  const v = e.target.value;
                                  setChoice(
                                    item.permission,
                                    v === '' ? null : { effect: v as AccessEffect, expiresOn: choice?.expiresOn ?? '', note: choice?.note ?? '' },
                                  );
                                }}
                              >
                                <option value="">{inRole ? 'Yes, as their role' : 'No, as their role'}</option>
                                {inRole
                                  ? <option value="Revoke">Take away</option>
                                  : <option value="Grant">Give</option>}
                              </select>
                            </td>
                            <td>
                              {choice && (
                                <input
                                  type="date"
                                  aria-label={`Until, ${item.label}`}
                                  min={today}
                                  disabled={view.locked !== null}
                                  value={choice.expiresOn}
                                  onChange={(e) => setChoice(item.permission, { ...choice, expiresOn: e.target.value })}
                                />
                              )}
                            </td>
                            <td>
                              {choice && (
                                <input
                                  aria-label={`Why, ${item.label}`}
                                  maxLength={500}
                                  placeholder="e.g. Asked by Dr Rao"
                                  disabled={view.locked !== null}
                                  value={choice.note}
                                  onChange={(e) => setChoice(item.permission, { ...choice, note: e.target.value })}
                                />
                              )}
                            </td>
                          </tr>
                        );
                      })}
                    </tbody>
                  </table>
                </div>
              ))}

              {!view.locked && (
                <div className="row">
                  <button className="btn btn-primary" onClick={() => void save()} disabled={busy}>
                    {busy ? 'Saving…' : 'Save access'}
                  </button>
                  <button className="btn" onClick={() => show(view)} disabled={busy}>Undo my changes</button>
                </div>
              )}
            </>
          )}
        </section>
      </div>
    </div>
  );
}
