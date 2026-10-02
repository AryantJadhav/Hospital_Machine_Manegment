import { Navigate, useParams } from 'react-router-dom';
import { api } from '../api/client';
import { PdfPreview } from './PdfPreview';

/**
 * A training session's report, shown before anyone downloads it: /training/:id/report.
 * See PdfPreview.
 */
export function TrainingReportPreviewPage() {
  const { id: idParam } = useParams();
  const id = Number(idParam);

  if (!Number.isInteger(id) || id <= 0) return <Navigate to="/training" replace />;

  return (
    <PdfPreview
      loadKey={id}
      kind="Training report"
      backTo={`/training/${id}`}
      backLabel="Back to the training session"
      load={async () => {
        const [session, pdf] = await Promise.all([
          api.get<{ reference: string }>(`/api/training/${id}`),
          api.blob(`/api/training/${id}/report.pdf`),
        ]);
        return { pdf, label: session.reference, fileName: `${session.reference}.pdf` };
      }}
    />
  );
}
