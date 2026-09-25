import { useEffect, useState } from 'react';
import { api } from '../api/client';
import { formatDate } from '../time';
import { CUSTOM_FREQUENCY } from '../pmSchedule';

/** The preview of the dates a PM schedule choice means, on the Add a machine form. */

/**
 * The dates a choice means, in the first year, worked out by the server with the same
 * code that generates the real ones. "Quarterly" is shown as the four dates it makes
 * rather than trusted as a word.
 */
export function PmDatePreview({ frequency, anchor }: { frequency: number | null; anchor: string }) {
  const usable = frequency !== null && frequency !== CUSTOM_FREQUENCY && Boolean(anchor);
  const key = `${frequency}|${anchor}`;
  const [result, setResult] = useState<{ key: string; dates: string[] } | null>(null);

  useEffect(() => {
    if (!usable) return;

    let current = true;
    void (async () => {
      try {
        const r = await api.get<{ dates: string[] }>(
          `/api/pm/preview?frequency=${frequency}&anchorDate=${anchor}`,
        );
        if (current) setResult({ key, dates: r.dates });
      } catch {
        if (current) setResult({ key, dates: [] });
      }
    })();

    return () => {
      current = false;
    };
  }, [usable, key, frequency, anchor]);

  // Only the answer to the question currently asked, never the last one.
  const dates = usable && result?.key === key ? result.dates : [];
  if (dates.length === 0) return null;

  return (
    <div className="stack" style={{ gap: '0.25rem' }}>
      <span>Due dates in the first year</span>
      <ol className="row" style={{ flexWrap: 'wrap', gap: '0.5rem', listStyle: 'none', margin: 0, padding: 0 }}>
        {dates.map((d) => (
          <li key={d}>
            <span className="pill tone-neutral">{formatDate(d)}</span>
          </li>
        ))}
      </ol>
      <span className="muted">After that the dates carry on the same way.</span>
    </div>
  );
}
