import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { api } from './api/client';
import { PERMISSIONS } from './auth/context';
import { useAuth } from './auth/useAuth';
import type { IncidentCounts } from './incidentTypes';

/**
 * Says so, on a machine's page, when it has had incidents: how many, and how many are still open, with a
 * way to the list. A machine that has been dropped twice is worth knowing about before it is handed over.
 * Shown to whoever may see incidents; for anyone else it says nothing.
 */
export function IncidentNotice({ equipmentId, assetTag, from }: { equipmentId: number; assetTag: string; from: string }) {
  const { may } = useAuth();
  const allowed = may(PERMISSIONS.incidentsView);
  const [found, setFound] = useState<{ total: number; open: number } | null>(null);

  useEffect(() => {
    if (!allowed) return;
    let cancelled = false;
    (async () => {
      try {
        const result = await api.get<{ total: number; counts: IncidentCounts }>(`/api/incidents?equipmentId=${equipmentId}&pageSize=1`);
        if (!cancelled) setFound({ total: result.total, open: result.counts.reported + result.counts.inReview });
      } catch {
        // The page is fine without the notice.
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [allowed, equipmentId]);

  if (!found || found.total === 0) return null;

  return (
    <p className={`alert ${found.open > 0 ? 'alert-warn' : 'alert-info'}`} role="status">
      {found.total === 1 ? '1 incident' : `${found.total} incidents`} recorded on this machine
      {found.open > 0 ? `, ${found.open} still open` : ''}.{' '}
      <Link to={`/incidents?status=all&q=${encodeURIComponent(assetTag)}`} state={{ from }}>See them</Link>
    </p>
  );
}
