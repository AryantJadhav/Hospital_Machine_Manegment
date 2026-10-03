import { useState } from 'react';
import type { FormEvent } from 'react';
import { api } from '../api/client';
import { useAuth } from '../auth/useAuth';
import { ServerQrCode } from './ServerQrCode';

/**
 * First-run screen. Shown only while the install has no accounts at all.
 *
 * After the admin account is created, shows a mobile-config QR code so the
 * operator can point a phone at the screen and connect the mobile app without
 * typing an IP address. This is the feature the build plan calls out as
 * "Final screen prints the mobile-config QR."
 */
export function SetupPage({ onDone }: { onDone: () => void }) {
  const { login } = useAuth();
  const [fullName, setFullName] = useState('');
  const [userName, setUserName] = useState('');
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  // null = still on the form; string = setup succeeded, holds the server URL
  const [serverUrl, setServerUrl] = useState<string | null>(null);

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    setError(null);

    if (password !== confirm) {
      setError('The two passwords do not match.');
      return;
    }

    setBusy(true);
    try {
      await api.post('/api/setup/first-admin', { userName, fullName, password });
      // Sign straight in. Making the operator retype what they just chose
      // is the kind of friction that makes software feel unfinished.
      await login(userName, password);
      // Show the mobile-config QR before entering the main app.
      setServerUrl(window.location.origin);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Setup failed.');
    } finally {
      setBusy(false);
    }
  }

  // --- Success screen with mobile-config QR ---
  if (serverUrl) {
    return (
      <div className="login-shell">
        <div className="card login-card setup-done">
          <h2>✓ Hospital PM is ready</h2>
          <p className="muted">
            Connect phones to this server by scanning the code below with the Hospital PM app.
          </p>
          <ServerQrCode url={serverUrl} />
          <button className="btn btn-primary" onClick={onDone} style={{ marginTop: '0.5rem' }}>
            Continue to dashboard
          </button>
        </div>
      </div>
    );
  }

  // --- Setup form ---
  return (
    <div className="login-shell">
      <form className="card login-card" onSubmit={onSubmit}>
        <h1 className="login-title">Set up Hospital PM</h1>
        <p className="login-sub">
          Create the first account for this installation: the support account of whoever is setting it up.
          The hospital&apos;s own people, such as the IT team and the Head of Biomedical, are added from the
          Staff page next. This screen appears once.
        </p>

        <label className="field">
          <span>Full name</span>
          <input value={fullName} onChange={(e) => setFullName(e.target.value)} autoFocus required />
        </label>

        <label className="field">
          <span>Username</span>
          <input
            value={userName}
            onChange={(e) => setUserName(e.target.value)}
            autoComplete="username"
            required
          />
        </label>

        <label className="field">
          <span>Password</span>
          <input
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoComplete="new-password"
            required
          />
          <span className="muted" style={{ fontSize: '0.8rem' }}>
            At least 5 characters, with upper case, lower case and a digit.
          </span>
        </label>

        <label className="field">
          <span>Confirm password</span>
          <input
            type="password"
            value={confirm}
            onChange={(e) => setConfirm(e.target.value)}
            autoComplete="new-password"
            required
          />
        </label>

        {error && <p className="alert alert-error" role="alert">{error}</p>}

        <button className="btn btn-primary" type="submit" disabled={busy}>
          {busy ? 'Creating…' : 'Create the first account'}
        </button>
      </form>
    </div>
  );
}
