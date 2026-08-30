import { useState } from 'react';
import { api } from '../api/client';

type ImportError = { row: number; column: string; message: string };

type ImportResponse = {
  totalRows: number;
  validRows: number;
  committed: boolean;
  errorCount: number;
  errors: ImportError[];
  errorsTruncated: boolean;
};

type Kind = 'locations' | 'equipment';

export function ImportPage() {
  // Locations first by default: equipment import fails without them, and
  // leading with equipment sends operators straight into an error they
  // cannot fix from that screen.
  const [kind, setKind] = useState<Kind>('locations');
  const [file, setFile] = useState<File | null>(null);
  const [result, setResult] = useState<ImportResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // Validate must succeed with zero errors before commit is offered. The
  // server enforces all-or-nothing anyway; this makes the two-step shape
  // visible instead of letting someone press Import and hope.
  const canCommit = result !== null && !result.committed && result.errorCount === 0;

  function pick(next: File | null) {
    setFile(next);
    setResult(null);
    setError(null);
  }

  const base =
    kind === 'locations' ? '/api/equipment/import/locations' : '/api/equipment/import';

  async function run(mode: 'validate' | 'commit') {
    if (!file) return;
    setBusy(true);
    setError(null);
    try {
      setResult(await api.upload<ImportResponse>(`${base}/${mode}`, file));
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Import failed.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <h1>Import</h1>
          <p className="muted">Load the hospital&rsquo;s locations, then its assets.</p>
        </div>
        <button
          className="btn"
          onClick={() =>
            api.download(
              kind === 'locations'
                ? '/api/equipment/import/locations/template'
                : '/api/equipment/import/template',
              `${kind}-template.xlsx`,
            )
          }
        >
          Download template
        </button>
      </header>

      <div className="card stack">
        <div className="row" role="tablist">
          {(['locations', 'equipment'] as Kind[]).map((k) => (
            <button
              key={k}
              role="tab"
              aria-selected={kind === k}
              className={kind === k ? 'btn btn-primary' : 'btn'}
              onClick={() => { setKind(k); setFile(null); setResult(null); setError(null); }}
            >
              {k === 'locations' ? '1. Locations' : '2. Equipment'}
            </button>
          ))}
        </div>

        {kind === 'equipment' && (
          <p className="muted" style={{ margin: 0 }}>
            Every asset must name a location that already exists. Import locations first.
          </p>
        )}

        <ol className="steps">
          <li>Download the template and paste your asset list into it.</li>
          <li>Check the file — nothing is saved at this stage.</li>
          <li>Import. Either every row is saved, or none are.</li>
        </ol>

        <label className="field">
          <span>Spreadsheet (.xlsx)</span>
          <input
            type="file"
            accept=".xlsx"
            onChange={(e) => pick(e.target.files?.[0] ?? null)}
          />
        </label>

        <div className="row">
          <button className="btn" disabled={!file || busy} onClick={() => run('validate')}>
            {busy ? 'Working…' : 'Check file'}
          </button>
          <button
            className="btn btn-primary"
            disabled={!canCommit || busy}
            onClick={() => run('commit')}
          >
            Import {result ? `${result.validRows} rows` : ''}
          </button>
        </div>

        {error && <p className="alert alert-error" role="alert">{error}</p>}

        {result?.committed && (
          <p className="alert alert-ok" role="status">
            Imported {result.validRows.toLocaleString()} assets.
          </p>
        )}

        {result && !result.committed && result.errorCount === 0 && (
          <p className="alert alert-ok" role="status">
            {result.totalRows.toLocaleString()} rows checked, no problems found. Nothing has been
            saved yet — press Import to apply.
          </p>
        )}

        {result && result.errorCount > 0 && (
          <>
            <p className="alert alert-error" role="alert">
              {result.errorCount.toLocaleString()} problem
              {result.errorCount === 1 ? '' : 's'} found in {result.totalRows.toLocaleString()} rows.
              Nothing has been saved. Fix the file and check it again.
            </p>

            <div className="table-wrap">
              <table className="table">
                <thead>
                  <tr><th>Row</th><th>Column</th><th>Problem</th></tr>
                </thead>
                <tbody>
                  {result.errors.map((e, i) => (
                    <tr key={`${e.row}-${e.column}-${i}`}>
                      <td className="mono">{e.row}</td>
                      <td>{e.column}</td>
                      <td>{e.message}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>

            {result.errorsTruncated && (
              <p className="muted">
                Only the first {result.errors.length} problems are listed.
              </p>
            )}
          </>
        )}
      </div>
    </div>
  );
}
