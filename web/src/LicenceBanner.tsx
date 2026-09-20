import { useEffect, useState } from 'react';
import { useLocation } from 'react-router-dom';
import { api } from './api/client';

type Banner = {
  show: boolean;
  readOnly: boolean;
  state: number;
  message: string;
  daysLeft: number | null;
};

/**
 * Tells everyone signed in when the licence is running out or has run out.
 *
 * Once it has, recording anything new is refused, and a technician whose
 * "Report a fault" fails needs to see why on the same screen, not be left to
 * guess. Administrators are told a fortnight before; everyone else only when it
 * has actually lapsed. It is asked again as people move between pages, because
 * a renewal installed on one PC should clear it on the next click of another.
 */
export function LicenceBanner() {
  const { pathname } = useLocation();
  const [banner, setBanner] = useState<Banner | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const b = await api.get<Banner>('/api/licence/banner');
        if (!cancelled) setBanner(b);
      } catch {
        // No banner is better than a broken page: the licence is not why
        // someone opened this one.
        if (!cancelled) setBanner(null);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [pathname]);

  if (!banner?.show) return null;

  return (
    <div className="page">
      <p className={`alert ${banner.readOnly ? 'alert-error' : 'alert-warn'}`} role="status">
        {banner.readOnly && <strong>Read-only. </strong>}
        {banner.message}
      </p>
    </div>
  );
}
