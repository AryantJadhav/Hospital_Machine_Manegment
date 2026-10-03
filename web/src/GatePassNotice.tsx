import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { api } from './api/client';
import { PERMISSIONS } from './auth/context';
import { useAuth } from './auth/useAuth';
import type { GatePassRow } from './gatePassTypes';
import { formatDate } from './time';

/**
 * Says so, at the top of a machine's page or a service request, when the machine is out of the hospital on
 * a gate pass: with whom, since when, and when it is due back. Shown to whoever may see gate passes; for
 * anyone else it says nothing and asks for nothing.
 */
export function GatePassNotice({ equipmentId, from }: { equipmentId: number; from: string }) {
  const { may } = useAuth();
  const allowed = may(PERMISSIONS.gatePassView);
  const [passes, setPasses] = useState<GatePassRow[]>([]);

  useEffect(() => {
    if (!allowed) return;
    let cancelled = false;
    (async () => {
      try {
        const result = await api.get<{ items: GatePassRow[] }>(`/api/gate-passes?equipmentId=${equipmentId}&status=out`);
        if (!cancelled) setPasses(result.items);
      } catch {
        // The page is fine without the notice.
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [allowed, equipmentId]);

  return (
    <>
      {passes.map((p) => (
        <p key={p.id} className={`alert ${p.isOverdue ? 'alert-warn' : 'alert-info'}`} role="status">
          Out of the hospital on gate pass{' '}
          <Link to={`/gate-passes/${p.id}`} state={{ from }} className="mono">{p.reference}</Link> to {p.vendorName}, since{' '}
          {formatDate(p.passDate)}
          {p.expectedReturnDate && (p.isOverdue ? `. It was due back on ${formatDate(p.expectedReturnDate)}.` : `, due back ${formatDate(p.expectedReturnDate)}.`)}
        </p>
      ))}
    </>
  );
}
