import { useCallback, useEffect, useState } from 'react';
import type { FormEvent } from 'react';
import { api } from '../api/client';
import { formatDateTime } from '../time';

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
};

const ROLE_LABELS: Record<string, string> = {
  Admin: 'Administrator',
  Employee: 'Employee',
};

const ROLE_HELP: Record<string, string> = {
  Admin:
    'Full access. Edits the register, writes checklists, schedules PMs, assigns ' +
    'work, and manages staff, backups and the licence.',
  Employee:
    'Works the floor. Does PM rounds, reports and resolves faults, reads the ' +
    'register. Cannot change what the department has committed to.',
};

export function StaffPage() {
  const [staff, setStaff] = useState<Staff[]>([]);
  const [showInactive, setShowInactive] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [adding, setAdding] = useState(false);
  const [editing, setEditing] = useState<Staff | null>(null);

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
      + 'Their PM records, signatures and work orders stay exactly as they are — '
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
            their PM signatures and work orders have to stay readable — so someone who
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
              <th>Last signed in</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {staff.length === 0 && (
              <tr><td colSpan={6} className="empty">Nobody yet.</td></tr>
            )}

            {staff.map((p) => (
              <tr key={p.id} style={p.isActive ? undefined : { opacity: 0.55 }}>
                <td>
                  {p.fullName}
                  {!p.isActive && <span className="muted"> — no longer signing in</span>}
                </td>
                <td className="mono">{p.userName}</td>
                <td className="mono">{p.staffCode ?? <span className="muted">—</span>}</td>
                <td>{ROLE_LABELS[p.role] ?? p.role}</td>
                <td>
                  {p.lastLoginAtUtc
                    ? formatDateTime(p.lastLoginAtUtc)
                    : <span className="muted">never</span>}
                </td>
                <td style={{ whiteSpace: 'nowrap' }}>
                  <button className="btn btn-quiet" onClick={() => setEditing(p)}>Edit</button>
                  <button className="btn btn-quiet" onClick={() => void resetPassword(p)}>
                    Reset password
                  </button>
                  {p.isActive ? (
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
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
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
  const [userName, setUserName] = useState(editing?.userName ?? '');
  const [fullName, setFullName] = useState(editing?.fullName ?? '');
  const [staffCode, setStaffCode] = useState(editing?.staffCode ?? '');
  const [role, setRole] = useState(editing?.role ?? 'Employee');
  const [password, setPassword] = useState('');
  const [busy, setBusy] = useState(false);

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
        await onSaved(`${fullName.trim()} updated.`);
      } else {
        await api.post('/api/users', {
          userName: userName.trim(),
          fullName: fullName.trim(),
          staffCode: staffCode.trim() || null,
          role,
          password,
        });
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
          {Object.keys(ROLE_LABELS).map((r) => (
            <option key={r} value={r}>{ROLE_LABELS[r]}</option>
          ))}
        </select>
        <span className="muted">{ROLE_HELP[role]}</span>
      </label>

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

