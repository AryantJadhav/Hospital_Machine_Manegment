import { useCallback, useEffect, useState } from 'react';
import { api } from '../api/client';
import { StatusPill } from '../StatusPill';
import { formatDate, formatDateTime } from '../time';

type Item = {
  id: number;
  licenceId: string;
  hospitalName: string;
  issuedOn: string;
  expiresOn: string | null;
  durationDays: number | null;
  maxEquipment: number | null;
  modules: string[];
  notes: string | null;
  issuedAtUtc: string;
  isLocked: boolean;
  lockSequence: number;
  lockChangedAtUtc: string | null;
};

type List = { available: boolean; problem: string | null; licences: Item[] };

/** What was just made and is waiting to be sent: a licence, or a lock or unlock code. */
type Made = { kind: 'licence' | 'lock' | 'unlock'; hospital: string; text: string; fileId?: number };

type Ends = 'never' | 'date' | 'days';

function describeEnd(l: Item): string {
  if (l.expiresOn) return `until ${formatDate(l.expiresOn)}`;
  if (l.durationDays !== null) return `${l.durationDays} days from installation`;
  return 'never ends';
}

/**
 * The Developer's licence section: make a licence for a hospital, send it again, and lock or unlock an installation.
 *
 * It only works on the copy of the software that holds the signing key. Anywhere else the page says so, and offers
 * nothing that would fail. Nothing here reaches into a hospital: a lock or unlock is a code, sent by message and
 * entered at their end, which they can do whether or not the machine is online.
 */
export function LicenceIssuerPage() {
  const [data, setData] = useState<List | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [made, setMade] = useState<Made | null>(null);
  const [busy, setBusy] = useState<string | null>(null);

  const [hospital, setHospital] = useState('');
  const [ends, setEnds] = useState<Ends>('days');
  const [endDate, setEndDate] = useState('');
  const [days, setDays] = useState('84');
  const [cap, setCap] = useState('');
  const [modules, setModules] = useState('');
  const [notes, setNotes] = useState('');

  const load = useCallback(async () => {
    try {
      setData(await api.get<List>('/api/developer/licences'));
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not load the licences.');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  async function issue() {
    setBusy('issue');
    setError(null);
    setMade(null);
    try {
      const res = await api.post<{ licence: Item; licenceText: string }>('/api/developer/licences', {
        hospitalName: hospital,
        expiresOn: ends === 'date' && endDate ? endDate : null,
        durationDays: ends === 'days' && days ? Number(days) : null,
        maxEquipment: cap ? Number(cap) : null,
        modules: modules.split(',').map((m) => m.trim()).filter(Boolean),
        notes: notes || null,
      });
      setMade({ kind: 'licence', hospital: res.licence.hospitalName, text: res.licenceText, fileId: res.licence.id });
      setHospital('');
      setCap('');
      setModules('');
      setNotes('');
      await load();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'The licence could not be made.');
    } finally {
      setBusy(null);
    }
  }

  async function command(l: Item, action: 'lock' | 'unlock') {
    if (action === 'lock') {
      // Typed, not clicked: a locked installation turns every user away until the code to open it is entered.
      const typed = prompt(
        [
          `Make a lock code for ${l.hospitalName}?`,
          '',
          'Once their IT team enters it, NOBODY at that hospital can sign in or open any record',
          'until they enter an unlock code from you. Nothing happens until they enter it.',
          '',
          'Type LOCK to make the code:',
        ].join('\n'),
      );
      if (typed !== 'LOCK') return;
    }

    setBusy(`${action}-${l.id}`);
    setError(null);
    setMade(null);
    try {
      const res = await api.post<{ code: string }>(`/api/developer/licences/${l.id}/${action}`, {});
      setMade({ kind: action, hospital: l.hospitalName, text: res.code });
      await load();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'The code could not be made.');
    } finally {
      setBusy(null);
    }
  }

  async function copy(text: string) {
    try {
      await navigator.clipboard.writeText(text);
    } catch {
      setError('Could not copy. Select the text and copy it by hand.');
    }
  }

  function downloadText(name: string, text: string) {
    const url = URL.createObjectURL(new Blob([text], { type: 'text/plain' }));
    const link = document.createElement('a');
    link.href = url;
    link.download = name;
    link.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }

  if (loading) return <div className="page"><p className="muted">Loading…</p></div>;

  const available = data?.available === true;
  const slug = (made?.hospital ?? 'hospital').toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '') || 'hospital';

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <h1>Issue licences</h1>
          <p className="muted">
            Make a licence for a hospital, send it again, and lock or unlock an installation with a code. Only the
            Developer sees this page.
          </p>
        </div>
      </header>

      {error && <p className="alert alert-error" role="alert">{error}</p>}

      {!available && (
        <p className="alert alert-error" role="alert">
          <strong>This copy cannot issue anything.</strong> {data?.problem}
        </p>
      )}

      {made && (
        <div className="card stack">
          <h2 className="section-h" style={{ marginBottom: 0 }}>
            {made.kind === 'licence' ? 'The licence' : made.kind === 'lock' ? 'Lock code' : 'Unlock code'} for {made.hospital}
          </h2>
          <p className="muted" style={{ margin: 0 }}>
            {made.kind === 'licence' &&
              'Send this to the hospital. Their IT team installs it on the Licence page. It is kept here, so you can send it again.'}
            {made.kind === 'lock' &&
              'Send this to the hospital. It does nothing until their IT team enters it on the Licence page. From then on nobody there can sign in until they enter an unlock code.'}
            {made.kind === 'unlock' &&
              'Send this to the hospital. They enter it on the lock screen, which needs no sign-in. A newer lock code still wins over an older unlock.'}
          </p>
          <textarea className="licence-box mono" rows={10} readOnly spellCheck={false} aria-label="Text to send" value={made.text} />
          <div className="row">
            <button className="btn btn-primary" onClick={() => void copy(made.text)}>Copy</button>
            <button
              className="btn"
              onClick={() => downloadText(`hospitalpm-${slug}.${made.kind === 'licence' ? 'licence' : `${made.kind}-code.txt`}`, made.text)}
            >
              Download
            </button>
            <button className="btn btn-quiet" onClick={() => setMade(null)}>Done</button>
          </div>
        </div>
      )}

      <div className="card stack">
        <h2 className="section-h" style={{ marginBottom: 0 }}>Make a licence</h2>

        <div style={{ display: 'grid', gap: '0.75rem', maxWidth: '36rem' }}>
          <label className="field" htmlFor="li-hospital">
            <span>Hospital name (shown in the app and on every printed report)</span>
            <input id="li-hospital" value={hospital} maxLength={200} disabled={!available} onChange={(e) => setHospital(e.target.value)} />
          </label>

          <label className="field" htmlFor="li-ends">
            <span>It ends</span>
            <select id="li-ends" value={ends} disabled={!available} onChange={(e) => setEnds(e.target.value as Ends)}>
              <option value="days">After a number of days from the day it is first installed (a pilot)</option>
              <option value="date">On a date (a paid licence)</option>
              <option value="never">Never</option>
            </select>
          </label>

          {ends === 'days' && (
            <label className="field" htmlFor="li-days">
              <span>Days from installation</span>
              <input id="li-days" type="number" min={1} max={3650} value={days} disabled={!available} onChange={(e) => setDays(e.target.value)} />
            </label>
          )}
          {ends === 'date' && (
            <label className="field" htmlFor="li-date">
              <span>Last day</span>
              <input id="li-date" type="date" value={endDate} disabled={!available} onChange={(e) => setEndDate(e.target.value)} />
            </label>
          )}

          <label className="field" htmlFor="li-cap">
            <span>Most machines (leave empty for no limit)</span>
            <input id="li-cap" type="number" min={1} value={cap} disabled={!available} onChange={(e) => setCap(e.target.value)} />
          </label>

          <label className="field" htmlFor="li-modules">
            <span>Modules, separated by commas (leave empty for core only)</span>
            <input id="li-modules" value={modules} disabled={!available} onChange={(e) => setModules(e.target.value)} />
          </label>

          <label className="field" htmlFor="li-notes">
            <span>Notes for yourself (the reseller, the purchase order)</span>
            <input id="li-notes" value={notes} maxLength={500} disabled={!available} onChange={(e) => setNotes(e.target.value)} />
          </label>
        </div>

        <div className="row">
          <button className="btn btn-primary" disabled={!available || busy !== null || hospital.trim().length === 0} onClick={() => void issue()}>
            {busy === 'issue' ? 'Signing…' : 'Make the licence'}
          </button>
        </div>
      </div>

      <div className="card table-wrap">
        <h2 className="section-h">Licences issued</h2>
        <table className="table">
          <thead>
            <tr>
              <th>Hospital</th>
              <th>Runs</th>
              <th>Limit</th>
              <th>Last code</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {data?.licences.length === 0 && (
              <tr><td colSpan={5} className="empty">No licence has been issued from this copy yet.</td></tr>
            )}

            {data?.licences.map((l) => (
              <tr key={l.id}>
                <td>
                  <div className="strong">{l.hospitalName}</div>
                  <div className="muted mono" style={{ fontSize: '0.8rem', wordBreak: 'break-all' }}>{l.licenceId}</div>
                  <div className="muted" style={{ fontSize: '0.8rem' }}>Issued {formatDate(l.issuedOn)}{l.notes ? ` · ${l.notes}` : ''}</div>
                </td>
                <td>{describeEnd(l)}</td>
                <td>{l.maxEquipment ?? 'No limit'}{l.modules.length > 0 && <div className="muted">{l.modules.join(', ')}</div>}</td>
                <td>
                  {l.lockSequence === 0
                    ? <span className="muted">None made</span>
                    : (
                      <>
                        <StatusPill tone={l.isLocked ? 'danger' : 'success'}>{l.isLocked ? 'Lock code' : 'Unlock code'}</StatusPill>
                        <div className="muted" style={{ fontSize: '0.8rem' }}>
                          number {l.lockSequence}{l.lockChangedAtUtc ? `, ${formatDateTime(l.lockChangedAtUtc)}` : ''}
                        </div>
                      </>
                    )}
                </td>
                <td>
                  <button
                    className="btn btn-quiet"
                    onClick={() => void api.download(`/api/developer/licences/${l.id}/file`, `hospitalpm-${l.hospitalName.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '') || 'licence'}.licence`)
                      .catch((e: unknown) => setError(e instanceof Error ? e.message : 'Could not download the licence.'))}
                  >
                    Licence file
                  </button>
                  <button className="btn btn-quiet" disabled={!available || busy !== null} onClick={() => void command(l, 'lock')}>
                    {busy === `lock-${l.id}` ? 'Signing…' : 'Lock…'}
                  </button>
                  <button className="btn btn-quiet" disabled={!available || busy !== null} onClick={() => void command(l, 'unlock')}>
                    {busy === `unlock-${l.id}` ? 'Signing…' : 'Unlock'}
                  </button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        <p className="muted" style={{ marginBottom: 0 }}>
          &ldquo;Last code&rdquo; is the last code made here. Whether the hospital has entered it, this copy cannot
          know: it never connects to an installation.
        </p>
      </div>
    </div>
  );
}
