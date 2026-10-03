import { Fragment, useCallback, useEffect, useState } from 'react';
import type { FormEvent } from 'react';
import { api } from '../api/client';
import { useAuth } from '../auth/useAuth';
import { ROLE_HELP, ROLE_LABEL, ROLES } from '../auth/context';
import type { AccessView, CatalogItem } from '../accessTypes';
import { formatDate, formatDateTime } from '../time';

/**
 * The hospital's staff.
 *
 * An installation used to have exactly one account, forever: the installer
 * creates the first administrator and the endpoint that made it then refuses,
 * saying "ask an administrator to create your account" — with no way for an
 * administrator to do that.
 *
 * Everything downstream depended on it and quietly did not work. A biomedical
 * department is a team, and this is what lets it be one.
 */

type Staff = {
  id: number;
  userName: string;
  fullName: string;
  staffCode: string | null;
  role: string;
  isActive: boolean;
  lastLoginAtUtc: string | null;
  /** Sections given on top of the role, and taken away from it. */
  accessGiven: number;
  accessTakenAway: number;
  /** For a department user: how many departments they were given. */
  departments: number;
};

type Place = { id: number; name: string; depth: number; level: number };

export function StaffPage() {
  // Nobody reaches above themselves: only the roles this person may give are offered, and the
  // accounts of anyone above them are shown but not touched.
  const { user } = useAuth();
  const manageable = user?.manageableRoles ?? [];
  const pausable = user?.pausableRoles ?? [];
  const [staff, setStaff] = useState<Staff[]>([]);
  const [showInactive, setShowInactive] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [adding, setAdding] = useState(false);
  const [editing, setEditing] = useState<Staff | null>(null);
  const [looking, setLooking] = useState<number | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      setStaff(await api.get<Staff[]>(`/api/users?includeInactive=${showInactive}`));
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not load the staff list.');
    } finally {
      setLoading(false);
    }
  }, [showInactive]);

  useEffect(() => {
    void load();
  }, [load]);

  async function act(work: () => Promise<unknown>, done: string) {
    setError(null);
    setNotice(null);
    try {
      await work();
      setNotice(done);
      await load();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'That did not work.');
    }
  }

  async function deactivate(person: Staff) {
    // Named, not just confirmed. "Are you sure?" on a list of similar rows is
    // how the wrong person loses their access.
    if (!confirm(
      `Stop ${person.fullName} signing in?\n\n`
      + 'Their PM records, signatures and service requests stay exactly as they are — '
      + 'nothing is deleted. Any session they have open ends immediately.\n\n'
      + 'You can let them back in later.',
    )) return;

    await act(
      () => api.post(`/api/users/${person.id}/deactivate`, {}),
      `${person.fullName} can no longer sign in.`,
    );
  }

  async function resetPassword(person: Staff) {
    const password = prompt(
      `New password for ${person.fullName}:\n\n`
      + 'They will be signed out everywhere, and will need this to sign back in.\n'
      + 'Write it down before continuing — it cannot be recovered from here.',
    );
    if (password === null || password.trim().length === 0) return;

    await act(
      () => api.post(`/api/users/${person.id}/reset-password`, { password }),
      `${person.fullName}'s password has been changed and their sessions ended.`,
    );
  }

  if (loading) return <div className="page"><p className="muted">Loading…</p></div>;

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <h1>Staff</h1>
          <p className="muted">
            Who can sign in, and what they are allowed to do. People are never deleted —
            their PM signatures and service requests have to stay readable — so someone who
            leaves is stopped from signing in instead.
          </p>
        </div>
        {!adding && !editing && (
          <button className="btn btn-primary" onClick={() => setAdding(true)}>Add someone</button>
        )}
      </header>

      {error && <p className="alert alert-error" role="alert">{error}</p>}
      {notice && <p className="alert alert-ok" role="status">{notice}</p>}

      {(adding || editing) && (
        <StaffForm
          editing={editing}
          onCancel={() => { setAdding(false); setEditing(null); }}
          onSaved={async (message) => {
            setAdding(false);
            setEditing(null);
            setNotice(message);
            await load();
          }}
          onError={setError}
        />
      )}

      <label style={{ display: 'flex', gap: '0.4rem', alignItems: 'center' }}>
        <input
          type="checkbox"
          checked={showInactive}
          onChange={(e) => setShowInactive(e.target.checked)}
        />
        <span>Show people who have left</span>
      </label>

      <div className="card table-wrap">
        <table className="table">
          <thead>
            <tr>
              <th>Name</th>
              <th>Username</th>
              <th>Staff code</th>
              <th>Role</th>
              <th>Extra access</th>
              <th>Last signed in</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {staff.length === 0 && (
              <tr><td colSpan={7} className="empty">Nobody yet.</td></tr>
            )}

            {staff.map((p) => (
              <Fragment key={p.id}>
              <tr style={p.isActive ? undefined : { opacity: 0.55 }}>
                <td>
                  {p.fullName}
                  {!p.isActive && <span className="muted"> — no longer signing in</span>}
                </td>
                <td className="mono">{p.userName}</td>
                <td className="mono">{p.staffCode ?? <span className="muted">—</span>}</td>
                <td>
                  {ROLE_LABEL[p.role] ?? p.role}
                  {p.role === ROLES.departmentUser && (
                    <div className="muted" style={{ fontSize: '0.8rem', color: p.departments === 0 ? 'var(--err)' : undefined }}>
                      {p.departments === 0
                        ? 'No department yet: sees nothing'
                        : `${p.departments} ${p.departments === 1 ? 'department' : 'departments'}`}
                    </div>
                  )}
                </td>
                <td>
                  {p.accessGiven === 0 && p.accessTakenAway === 0 ? (
                    <span className="muted">—</span>
                  ) : (
                    <button
                      className="btn btn-quiet"
                      aria-expanded={looking === p.id}
                      onClick={() => setLooking(looking === p.id ? null : p.id)}
                    >
                      {p.accessGiven > 0 && `+${p.accessGiven} given`}
                      {p.accessGiven > 0 && p.accessTakenAway > 0 && ', '}
                      {p.accessTakenAway > 0 && `−${p.accessTakenAway} taken away`}
                    </button>
                  )}
                </td>
                <td>
                  {p.lastLoginAtUtc
                    ? formatDateTime(p.lastLoginAtUtc)
                    : <span className="muted">never</span>}
                </td>
                <td style={{ whiteSpace: 'nowrap' }}>
                  {!pausable.includes(p.role) && <span className="muted">Managed by {p.role === ROLES.developer ? 'the developer' : 'someone above you'}</span>}
                  {manageable.includes(p.role) && <>
                  <button className="btn btn-quiet" onClick={() => setEditing(p)}>Edit</button>
                  <button className="btn btn-quiet" onClick={() => void resetPassword(p)}>
                    Reset password
                  </button>
                  </>}
                  {pausable.includes(p.role) && (p.isActive ? (
                    <button className="btn btn-quiet" onClick={() => void deactivate(p)}>
                      Remove access
                    </button>
                  ) : (
                    <button
                      className="btn btn-quiet"
                      onClick={() => void act(
                        () => api.post(`/api/users/${p.id}/activate`, {}),
                        `${p.fullName} can sign in again.`,
                      )}
                    >
                      Let back in
                    </button>
                  ))}
                  {!manageable.includes(p.role) && pausable.includes(p.role) && (
                    <div className="muted" style={{ fontSize: '0.8rem' }}>
                      You can stop this account signing in. It stays stopped until you let it back in.
                    </div>
                  )}
                </td>
              </tr>
              {looking === p.id && (
                <tr>
                  <td colSpan={7}><AccessDetail personId={p.id} /></td>
                </tr>
              )}
              </Fragment>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

/**
 * What one person has been given, or had taken away, on top of their role. Read-only: the hospital is
 * never kept in the dark about extra access, and only the Developer changes it.
 */
function AccessDetail({ personId }: { personId: number }) {
  const [view, setView] = useState<AccessView | null>(null);
  const [catalog, setCatalog] = useState<CatalogItem[]>([]);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const [v, c] = await Promise.all([
          api.get<AccessView>(`/api/users/${personId}/access`),
          api.get<CatalogItem[]>('/api/access/catalog'),
        ]);
        if (cancelled) return;
        setView(v);
        setCatalog(c);
      } catch (e) {
        if (!cancelled) setError(e instanceof Error ? e.message : 'Could not load their access.');
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [personId]);

  if (error) return <p className="alert alert-error" role="alert">{error}</p>;
  if (!view) return <p className="muted">Loading…</p>;

  const label = (permission: string) => catalog.find((c) => c.permission === permission)?.label ?? permission;

  return (
    <ul style={{ margin: 0, paddingLeft: '1.2rem' }} aria-label="Extra access">
      {view.grants.map((g) => (
        <li key={g.permission}>
          <strong>{g.effect === 'Grant' ? 'Given: ' : 'Taken away: '}</strong>
          {label(g.permission)}
          <span className="muted">
            {g.expiresOn && (g.expired ? ` · ran out on ${formatDate(g.expiresOn)}` : ` · until ${formatDate(g.expiresOn)}`)}
            {g.note && ` · ${g.note}`}
            {g.grantedByName && ` · by ${g.grantedByName}, ${formatDateTime(g.grantedAtUtc)}`}
          </span>
        </li>
      ))}
    </ul>
  );
}

function StaffForm({
  editing,
  onCancel,
  onSaved,
  onError,
}: {
  editing: Staff | null;
  onCancel: () => void;
  onSaved: (message: string) => void | Promise<void>;
  onError: (msg: string | null) => void;
}) {
  const { user } = useAuth();
  const manageable = user?.manageableRoles ?? [];
  const [userName, setUserName] = useState(editing?.userName ?? '');
  const [fullName, setFullName] = useState(editing?.fullName ?? '');
  const [staffCode, setStaffCode] = useState(editing?.staffCode ?? '');
  const [role, setRole] = useState(editing?.role ?? (manageable.includes(ROLES.bmeEngineer) ? ROLES.bmeEngineer : manageable[0] ?? ''));
  const [password, setPassword] = useState('');
  const [busy, setBusy] = useState(false);

  // For a department user: the places they belong to, and everything beneath them is theirs too.
  const [places, setPlaces] = useState<Place[]>([]);
  const [chosen, setChosen] = useState<Set<number>>(new Set());

  useEffect(() => {
    if (role !== ROLES.departmentUser) return;
    let cancelled = false;
    void (async () => {
      try {
        const all = await api.get<Place[]>('/api/lookups/locations');
        if (cancelled) return;
        setPlaces(all);
        if (editing && editing.role === ROLES.departmentUser) {
          const mine = await api.get<{ locationId: number }[]>(`/api/users/${editing.id}/departments`);
          if (!cancelled) setChosen(new Set(mine.map((m) => m.locationId)));
        }
      } catch {
        // The account can still be saved; departments can be set afterwards.
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [role, editing]);

  function toggle(id: number) {
    setChosen((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  async function submit(e: FormEvent) {
    e.preventDefault();
    onError(null);
    setBusy(true);
    try {
      if (editing) {
        await api.put(`/api/users/${editing.id}`, {
          fullName: fullName.trim(),
          staffCode: staffCode.trim() || null,
          role,
        });
        if (role === ROLES.departmentUser) {
          await api.put(`/api/users/${editing.id}/departments`, { locationIds: [...chosen] });
        }
        await onSaved(`${fullName.trim()} updated.`);
      } else {
        const created = await api.post<{ id: number }>('/api/users', {
          userName: userName.trim(),
          fullName: fullName.trim(),
          staffCode: staffCode.trim() || null,
          role,
          password,
        });
        if (role === ROLES.departmentUser) {
          await api.put(`/api/users/${created.id}/departments`, { locationIds: [...chosen] });
        }
        await onSaved(`${fullName.trim()} can now sign in.`);
      }
    } catch (err) {
      onError(err instanceof Error ? err.message : 'Could not save.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <form className="card stack" onSubmit={submit}>
      <h2 style={{ margin: 0, fontSize: '1.05rem' }}>
        {editing ? `Edit ${editing.fullName}` : 'Add someone'}
      </h2>

      <label className="stack">
        <span>Full name</span>
        <input
          className="field"
          required
          maxLength={120}
          placeholder="R Patil"
          value={fullName}
          onChange={(e) => setFullName(e.target.value)}
        />
        <span className="muted">Appears on every PM certificate they sign.</span>
      </label>

      <label className="stack">
        <span>Username</span>
        <input
          className="field mono"
          required
          maxLength={60}
          // A username is what their signed records are tied to, so changing
          // it later would orphan the trail. Fixed once created.
          disabled={editing !== null}
          value={userName}
          onChange={(e) => setUserName(e.target.value)}
        />
        {editing && <span className="muted">A username cannot be changed once records exist under it.</span>}
      </label>

      <label className="stack">
        <span>Staff code (optional)</span>
        <input
          className="field mono"
          maxLength={40}
          placeholder="BME-07"
          value={staffCode}
          onChange={(e) => setStaffCode(e.target.value)}
        />
        <span className="muted">The hospital's own employee number, if they use one.</span>
      </label>

      <label className="stack">
        <span>Role</span>
        <select className="field" value={role} onChange={(e) => setRole(e.target.value)}>
          {manageable.map((r) => (
            <option key={r} value={r}>{ROLE_LABEL[r] ?? r}</option>
          ))}
        </select>
        <span className="muted">{ROLE_HELP[role]}</span>
      </label>

      {role === ROLES.departmentUser && (
        <fieldset className="stack" style={{ border: 'none', padding: 0, margin: 0 }}>
          <legend style={{ fontWeight: 600, padding: 0 }}>Departments</legend>
          <span className="muted">
            They see the equipment in the places ticked, and everything beneath them, and nothing else.
            With none ticked they see nothing.
          </span>
          <div style={{ maxHeight: '16rem', overflow: 'auto', display: 'grid', gap: '0.2rem' }}>
            {places.map((p) => (
              <label key={p.id} className="row" style={{ gap: '0.5rem', alignItems: 'center', paddingLeft: `${p.depth * 1.1}rem` }}>
                <input type="checkbox" checked={chosen.has(p.id)} onChange={() => toggle(p.id)} />
                <span>{p.name}</span>
              </label>
            ))}
          </div>
        </fieldset>
      )}

      {!editing && (
        <label className="stack">
          <span>Password</span>
          <input
            className="field"
            type="password"
            required
            value={password}
            onChange={(e) => setPassword(e.target.value)}
          />
          <span className="muted">
            Write it down and hand it over. There is no email on this system, so nobody can
            send them a reset link — an administrator sets a new one from this page.
          </span>
        </label>
      )}

      <div style={{ display: 'flex', gap: '0.5rem' }}>
        <button className="btn btn-primary" disabled={busy}>
          {busy ? 'Saving…' : editing ? 'Save changes' : 'Add'}
        </button>
        <button type="button" className="btn" onClick={onCancel} disabled={busy}>Cancel</button>
      </div>
    </form>
  );
}

