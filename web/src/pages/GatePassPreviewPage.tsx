import { Navigate, useParams } from 'react-router-dom';
import { api } from '../api/client';
import { PdfPreview } from './PdfPreview';

/**
 * A gate pass as it prints, shown before anyone downloads it: /gate-passes/:id/pdf. It has a page for each
 * copy (vendor, security, department). See PdfPreview.
 */
export function GatePassPreviewPage() {
  const { id: idParam } = useParams();
  const id = Number(idParam);

  if (!Number.isInteger(id) || id <= 0) return <Navigate to="/gate-passes" replace />;

  return (
    <PdfPreview
      loadKey={id}
      kind="Gate pass"
      backTo={`/gate-passes/${id}`}
      backLabel="Back to the gate pass"
      load={async () => {
        const [pass, pdf] = await Promise.all([
          api.get<{ reference: string }>(`/api/gate-passes/${id}`),
          api.blob(`/api/gate-passes/${id}/pdf`),
        ]);
        return { pdf, label: pass.reference, fileName: `${pass.reference}.pdf` };
      }}
    />
  );
}
