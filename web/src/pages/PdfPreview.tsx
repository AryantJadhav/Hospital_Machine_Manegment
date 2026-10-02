import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { ApiError } from '../api/client';
import { usePageTitle } from '../pageTitle';

/**
 * A report shown before anyone downloads it.
 *
 * Opened in a tab of its own, so the report is read in the browser's own PDF reader beside the page
 * it came from. Downloading is a choice made from here, and the file is named after the record
 * rather than a string of letters - which a bare PDF tab would not manage.
 *
 * `load` fetches the document and says what to call it; `loadKey` makes it fetch again for another one.
 */
export function PdfPreview({
  loadKey,
  load,
  kind,
  backTo,
  backLabel,
}: {
  loadKey: string | number;
  load: () => Promise<{ pdf: Blob; label: string; fileName: string }>;
  /** "Service report", "Training report". */
  kind: string;
  backTo: string;
  backLabel: string;
}) {
  const [label, setLabel] = useState<string | null>(null);
  const [fileName, setFileName] = useState<string | null>(null);
  const [url, setUrl] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  usePageTitle(label ? `${label} ${kind.toLowerCase()}` : kind);

  useEffect(() => {
    let cancelled = false;
    let made: string | null = null;

    (async () => {
      try {
        const loaded = await load();
        if (cancelled) return;
        made = URL.createObjectURL(loaded.pdf);
        setLabel(loaded.label);
        setFileName(loaded.fileName);
        setUrl(made);
      } catch (e) {
        if (cancelled) return;
        setError(
          e instanceof ApiError && e.status === 404
            ? 'That record does not exist.'
            : e instanceof Error
              ? e.message
              : `Could not make the ${kind.toLowerCase()}.`,
        );
      }
    })();

    return () => {
      cancelled = true;
      if (made) URL.revokeObjectURL(made);
    };
    // `load` is rebuilt on every render of the page that owns it; the key says when it really changed.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [loadKey]);

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <p className="crumb"><Link to={backTo}>← {backLabel}</Link></p>
          <h1>{label ? `${kind} ${label}` : kind}</h1>
          <p className="muted">A preview. Download it if you want a copy.</p>
        </div>

        {url && fileName && (
          <a className="btn btn-primary" href={url} download={fileName}>
            Download PDF
          </a>
        )}
      </header>

      {error && <p className="alert alert-error" role="alert">{error}</p>}
      {!url && !error && <p className="muted">Preparing the report…</p>}

      {url && <iframe title={label ? `${kind} ${label}` : kind} src={url} className="pdf-preview" />}
    </div>
  );
}
