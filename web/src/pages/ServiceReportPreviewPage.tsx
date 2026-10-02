import { Navigate, useParams } from 'react-router-dom';
import { api } from '../api/client';
import { PdfPreview } from './PdfPreview';

/**
 * A request's service report, shown before anyone downloads it: /work-orders/:id/report.
 * See PdfPreview.
 */
export function ServiceReportPreviewPage() {
  const { id: idParam } = useParams();
  const id = Number(idParam);

  if (!Number.isInteger(id) || id <= 0) return <Navigate to="/work-orders" replace />;

  return (
    <PdfPreview
      loadKey={id}
      kind="Service report"
      backTo={`/work-orders/${id}`}
      backLabel="Back to the service request"
      load={async () => {
        const [order, pdf] = await Promise.all([
          api.get<{ number: string }>(`/api/work-orders/${id}`),
          api.blob(`/api/reports/work-orders/${id}/report.pdf`),
        ]);
        return { pdf, label: order.number, fileName: `${order.number}.pdf` };
      }}
    />
  );
}
