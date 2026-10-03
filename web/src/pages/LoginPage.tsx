import { useState } from 'react';
import type { FormEvent } from 'react';
import { useAuth } from '../auth/useAuth';

/**
 * One door. The account decides what a person may do, on the server, so there is nothing to
 * choose here: the Head of Biomedical, an engineer, the IT team and a person from a ward all sign
 * in the same way, and see the parts of the system that are theirs.
 *
 * The refusal is the same whether the user does not exist, is deactivated, or the password is
 * wrong. Being specific would turn this form into a way to find out which accounts exist.
 */
export function LoginPage() {
  const { login } = useAuth();
  const [userName, setUserName] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setBusy(true);
    try {
      await login(userName, password);
    } catch {
      setError('Sign-in failed. Check your username and password.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="login-shell">
      <form className="card login-card" onSubmit={onSubmit}>
        <h1 className="login-title">Hospital PM</h1>
        <p className="login-sub">Biomedical equipment maintenance</p>

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
      </form>
    </div>
  );
}
