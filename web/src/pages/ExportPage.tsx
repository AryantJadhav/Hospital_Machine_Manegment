import { useState } from 'react';
import { api } from '../api/client';
import { todayAtHospital } from '../time';

const FILES: [name: string, what: string][] = [
  ['equipment.csv', 'Every machine: tag, serial, type, place, maker, model, status, dates, notes.'],
  ['locations.csv', 'The tree of places, from organisation down to rooms.'],
  ['pm_tasks.csv, pm_completions.csv, pm_answers.csv', 'Every PM that fell due, who completed it and when, and every answer on it.'],
  ['work_orders.csv, work_order_notes.csv', 'Every fault, who fixed it, and how long the machine was out of service.'],
  ['checklists.csv and checklists/', 'Every checklist and every version of it, with its questions and limits.'],
  ['pm_schedules.csv, staff.csv', 'Which machine gets which checklist, and the people who use the system.'],
];

/**
 * Take everything out, as files the hospital keeps.
 *
 * Here so that "our data is ours" is something an Administrator can do on a
 * Tuesday afternoon, without asking us.
 */
export function ExportPage() {
  const [signatures, setSignatures] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState(false);

  async function download() {
    setBusy(true);
    setError(null);
    setDone(false);
    try {
      const stamp = todayAtHospital().replaceAll('-', '');
      await api.download(
        `/api/admin/export${signatures ? '?signatures=true' : ''}`,
        `hospitalpm-export-${stamp}.zip`,
      );
      setDone(true);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not prepare the export.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <h1>Export data</h1>
          <p className="muted">
            A copy of everything recorded here, as ordinary files. You do not need Hospital PM to read them.
          </p>
        </div>
        <button className="btn btn-primary" disabled={busy} onClick={() => void download()}>
          {busy ? 'Preparing…' : 'Download everything'}
        </button>
      </header>

      {error && <p className="alert alert-error" role="alert">{error}</p>}
      {done && (
        <p className="alert alert-ok" role="status">
          Downloaded. Open the README.txt inside first: it says what each file is. If manifest.json
          is missing from the zip, the download was cut short, so take it again.
        </p>
      )}

      <section className="card">
        <h2 className="section-h">What is in it</h2>
        <dl className="detail">
          {FILES.map(([name, what]) => (
            <div key={name} style={{ display: 'contents' }}>
              <dt className="mono">{name}</dt>
              <dd>{what}</dd>
            </div>
          ))}
        </dl>
      </section>

      <section className="card stack">
        <label className="row">
          <input
            type="checkbox"
            id="export-signatures"
            checked={signatures}
            onChange={(e) => setSignatures(e.target.checked)}
          />
          <span>Include the signature images (larger download)</span>
        </label>
        <p className="muted" style={{ margin: 0 }}>
          The CSV files open in Excel. The first columns of equipment.csv match the import template, so a
          register can be moved to another installation by saving it as .xlsx and importing it.
        </p>
        <p className="muted" style={{ margin: 0 }}>
          Not included: passwords, sign-in tokens, the licence, and the audit trail, which stays in the
          database and its backups. This works even after a licence has lapsed.
        </p>
      </section>
    </div>
  );
}
