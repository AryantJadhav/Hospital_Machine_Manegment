import { useState } from 'react';
import { api, ApiError } from './api/client';
import type { Handoff, RecordedPm } from './handoff';

/**
 * "PM recorded, with 1 failed check." with a button for its certificate, or a
 * plain confirmation when there is nothing to print.
 */
export function HandoffNotice({ handoff }: { handoff: Handoff | null }) {
  const [problem, setProblem] = useState<string | null>(null);

  if (!handoff) return null;

  async function certificate(pm: RecordedPm) {
    setProblem(null);
    try {
      await api.download(`/api/reports/pm/${pm.id}/certificate.pdf`, `PM-${pm.assetTag}-${pm.dueDate}.pdf`);
    } catch (e) {
      setProblem(
        e instanceof ApiError && e.status === 404
          ? 'That PM has not been completed yet, so there is no certificate.'
          : 'Could not generate the certificate.',
      );
    }
  }

  return (
    <>
      <p className={`alert ${handoff.tone === 'info' ? 'alert-info' : 'alert-ok'}`} role="status">
        {handoff.notice}
        {handoff.recorded && (
          <>
            {' '}
            <button className="btn btn-quiet" onClick={() => void certificate(handoff.recorded!)}>
              Download the certificate
            </button>
          </>
        )}
      </p>
      {problem && <p className="alert alert-error" role="alert">{problem}</p>}
    </>
  );
}
