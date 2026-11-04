import { Navigate, useParams, useSearchParams } from 'react-router-dom';
import { api } from '../api/client';
import { PdfPreview } from './PdfPreview';

/**
 * One loss's report, shown before anyone downloads it: /losses/:id/report.
 * See PdfPreview.
 */
export function LossReportPreviewPage() {
  const { id: idParam } = useParams();
  const id = Number(idParam);

  if (!Number.isInteger(id) || id <= 0) return <Navigate to="/losses" replace />;

  return (
    <PdfPreview
      loadKey={id}
      kind="Loss report"
      backTo={`/losses/${id}`}
      backLabel="Back to the loss"
      load={async () => {
        const [loss, pdf] = await Promise.all([
          api.get<{ reference: string }>(`/api/losses/${id}`),
          api.blob(`/api/losses/${id}/report.pdf`),
        ]);
        return { pdf, label: loss.reference, fileName: `${loss.reference}.pdf` };
      }}
    />
  );
}

/**
 * The machines lost in a period at a glance, shown before anyone downloads it: /losses/report?from=&to=.
 * See PdfPreview.
 */
export function LossesSummaryPreviewPage() {
  const [params] = useSearchParams();
  const from = params.get('from') ?? '';
  const to = params.get('to') ?? '';

  const query = new URLSearchParams();
  if (from) query.set('from', from);
  if (to) query.set('to', to);

  return (
    <PdfPreview
      loadKey={`${from}|${to}`}
      kind="Loss of equipment report"
      backTo="/losses"
      backLabel="Back to Loss of equipment"
      load={async () => {
        const pdf = await api.blob(`/api/losses/report.pdf?${query}`);
        const label = from || to ? `${from || 'start'} to ${to || 'today'}` : 'all time';
        return { pdf, label, fileName: `Loss-of-equipment-report${from ? `-${from}` : ''}${to ? `-${to}` : ''}.pdf` };
      }}
    />
  );
}
