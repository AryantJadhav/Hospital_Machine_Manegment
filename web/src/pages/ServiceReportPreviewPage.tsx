import { useEffect, useState } from 'react';
import { Link, Navigate, useParams } from 'react-router-dom';
import { api, ApiError } from '../api/client';
import { usePageTitle } from '../pageTitle';

/**
 * A work order's service report, shown before anyone downloads it: /work-orders/:id/report.
 *
 * Opened in a tab of its own from the work order, so the report is read in the browser's own PDF
 * reader beside the page it came from. Downloading is a choice made from here, and the file is
 * named after the work order rather than a string of letters.
 */
export function ServiceReportPreviewPage() {
  const { id: idParam } = useParams();
  const id = Number(idParam);
  const valid = Number.isInteger(id) && id > 0;

  const [number, setNumber] = useState<string | null>(null);
  const [url, setUrl] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  usePageTitle(number ? `${number} service report` : 'Service report');

  useEffect(() => {
    if (!valid) return;
    let cancelled = false;
    let made: string | null = null;

    (async () => {
      try {
        const [order, pdf] = await Promise.all([
          api.get<{ number: string }>(`/api/work-orders/${id}`),
          api.blob(`/api/reports/work-orders/${id}/report.pdf`),
        ]);
        if (cancelled) return;
        made = URL.createObjectURL(pdf);
        setNumber(order.number);
        setUrl(made);
      } catch (e) {
        if (cancelled) return;
        setError(
          e instanceof ApiError && e.status === 404
            ? 'That service request does not exist.'
            : e instanceof Error
              ? e.message
              : 'Could not make the service report.',
        );
      }
    })();

    return () => {
      cancelled = true;
      if (made) URL.revokeObjectURL(made);
    };
  }, [valid, id]);

  if (!valid) return <Navigate to="/work-orders" replace />;

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <p className="crumb"><Link to={`/work-orders/${id}`}>← Back to the service request</Link></p>
          <h1>{number ? `Service report ${number}` : 'Service report'}</h1>
          <p className="muted">A preview. Download it if you want a copy.</p>
        </div>

        {url && number && (
          <a className="btn btn-primary" href={url} download={`${number}.pdf`}>
            Download PDF
          </a>
        )}
      </header>

      {error && <p className="alert alert-error" role="alert">{error}</p>}
      {!url && !error && <p className="muted">Preparing the report…</p>}

      {url && (
        <iframe
          title={number ? `Service report ${number}` : 'Service report'}
          src={url}
          className="pdf-preview"
        />
      )}
    </div>
  );
}
