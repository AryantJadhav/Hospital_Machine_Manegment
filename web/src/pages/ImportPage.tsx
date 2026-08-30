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

export function ImportPage() {
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

  async function run(mode: 'validate' | 'commit') {
    if (!file) return;
    setBusy(true);
    setError(null);
    try {
      setResult(await api.upload<ImportResponse>(`/api/equipment/import/${mode}`, file));
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
          <h1>Import equipment</h1>
          <p className="muted">Upload the hospital&rsquo;s asset spreadsheet.</p>
        </div>
        <button
          className="btn"
          onClick={() => api.download('/api/equipment/import/template', 'equipment-template.xlsx')}
        >
          Download template
        </button>
      </header>

      <div className="card stack">
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
