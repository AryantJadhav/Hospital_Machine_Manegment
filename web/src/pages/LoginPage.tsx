import { useState } from 'react';
import type { FormEvent } from 'react';
import { useAuth } from '../auth/useAuth';
import { ROLES } from '../auth/context';

/**
 * Two doors into the same building.
 *
 * The chooser does not decide anything: the account decides, on the server,
 * and pressing "Administration" with a technician's password signs you in as
 * a technician. It is here because "who is this for" is the first question a
 * hospital asks about the software, and answering it at the front door is
 * better than a list of roles in a manual nobody opens.
 *
 * Deliberately not a gate. A chooser that refused the wrong pairing would
 * turn a mis-click into a lockout, and — worse — the refusal would differ
 * from a wrong password, which is how a login form quietly starts telling
 * strangers which accounts are administrators.
 */
const CHOICES = [
  {
    role: ROLES.admin,
    title: 'Administration',
    line: 'Runs the department.',
    does: [
      'Everything an employee can do',
      'The register, the checklists, the PM schedule',
      'Assigning work, and deciding a PM will not happen',
      'Staff accounts, backups, and the licence',
    ],
  },
  {
    role: ROLES.employee,
    title: 'Employee',
    line: 'Works the floor.',
    does: [
      'The PM round, and signing off what was done',
      'Reporting a fault and seeing it through',
      'Reading the register',
    ],
  },
] as const;

// Remembered so a shared ward PC does not ask the same technician every
// morning. Per-browser and nothing more: it is not a credential, and being
// wrong about it costs one click.
const CHOICE_KEY = 'hospitalpm.loginType';

function rememberedChoice(): string | null {
  try {
    const stored = localStorage.getItem(CHOICE_KEY);
    return CHOICES.some((c) => c.role === stored) ? stored : null;
  } catch {
    // Private windows and locked-down browsers throw rather than return null.
    return null;
  }
}

export function LoginPage() {
  const { login } = useAuth();
  const [choice, setChoice] = useState<string | null>(rememberedChoice);
  const [userName, setUserName] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  function choose(role: string) {
    try {
      localStorage.setItem(CHOICE_KEY, role);
    } catch {
      // Not being able to remember it is not a reason to refuse the sign-in.
    }
    setChoice(role);
  }

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setBusy(true);
    try {
      await login(userName, password, choice ?? undefined);
    } catch {
      // The server returns the same 401 for unknown user, deactivated user
      // and wrong password, so the message stays deliberately vague. Being
      // specific here would undo that and let the form enumerate staff
      // accounts.
      setError('Sign-in failed. Check your username and password.');
    } finally {
      setBusy(false);
    }
  }

  if (choice === null) {
    return (
      <div className="login-shell">
        <div className="card login-card login-choices">
          <h1 className="login-title">Hospital PM</h1>
          <p className="login-sub">Biomedical equipment maintenance</p>

          <p className="muted login-ask">How do you use this system?</p>

          {CHOICES.map((c) => (
            <button
              key={c.role}
              type="button"
              className="login-choice"
              onClick={() => choose(c.role)}
            >
              <span className="login-choice-title">{c.title}</span>
              <span className="login-choice-line">{c.line}</span>
              <ul className="login-choice-does">
                {c.does.map((d) => <li key={d}>{d}</li>)}
              </ul>
            </button>
          ))}
        </div>
      </div>
    );
  }

  const chosen = CHOICES.find((c) => c.role === choice)!;

  return (
    <div className="login-shell">
      <form className="card login-card" onSubmit={onSubmit}>
        <h1 className="login-title">{chosen.title} sign-in</h1>
        <p className="login-sub">{chosen.line}</p>

        <label className="field">
          <span>Username</span>
          <input
            value={userName}
            onChange={(e) => setUserName(e.target.value)}
            autoComplete="username"
            autoFocus
            required
          />
        </label>

        <label className="field">
          <span>Password</span>
          <input
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoComplete="current-password"
            required
          />
        </label>

        {error && <p className="alert alert-error" role="alert">{error}</p>}

        <button className="btn btn-primary" type="submit" disabled={busy}>
          {busy ? 'Signing in…' : 'Sign in'}
        </button>

        <button
          type="button"
          className="btn btn-quiet"
          onClick={() => setChoice(null)}
          disabled={busy}
        >
          Not you? Choose a different sign-in
        </button>
      </form>
    </div>
  );
}
