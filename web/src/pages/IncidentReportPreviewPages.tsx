import { Navigate, useParams, useSearchParams } from 'react-router-dom';
import { api } from '../api/client';
import { PdfPreview } from './PdfPreview';

/**
 * One incident's report, shown before anyone downloads it: /incidents/:id/report.
 * See PdfPreview.
 */
export function IncidentReportPreviewPage() {
  const { id: idParam } = useParams();
  const id = Number(idParam);

  if (!Number.isInteger(id) || id <= 0) return <Navigate to="/incidents" replace />;

  return (
    <PdfPreview
      loadKey={id}
      kind="Incident report"
      backTo={`/incidents/${id}`}
      backLabel="Back to the incident"
      load={async () => {
        const [incident, pdf] = await Promise.all([
          api.get<{ reference: string }>(`/api/incidents/${id}`),
          api.blob(`/api/incidents/${id}/report.pdf`),
        ]);
        return { pdf, label: incident.reference, fileName: `${incident.reference}.pdf` };
      }}
    />
  );
}

/**
 * The incidents of a period at a glance, shown before anyone downloads it: /incidents/report?from=&to=.
 * See PdfPreview.
 */
export function IncidentsSummaryPreviewPage() {
  const [params] = useSearchParams();
  const from = params.get('from') ?? '';
  const to = params.get('to') ?? '';

  const query = new URLSearchParams();
  if (from) query.set('from', from);
  if (to) query.set('to', to);

  return (
    <PdfPreview
      loadKey={`${from}|${to}`}
      kind="Incident summary"
      backTo="/incidents"
      backLabel="Back to Incidents"
      load={async () => {
        const pdf = await api.blob(`/api/incidents/report.pdf?${query}`);
        const label = from || to ? `${from || 'start'} to ${to || 'today'}` : 'all time';
        return { pdf, label, fileName: `Incident-report${from ? `-${from}` : ''}${to ? `-${to}` : ''}.pdf` };
      }}
    />
  );
}
