import { useEffect, useState } from 'react';
import { api } from './api/client';

/**
 * Which parts of the product are switched on. Import, export, backups and updates can each be switched
 * off for everyone, in the settings file, and the server then answers as if they were not there. This is
 * how the screens know, so a switched-off part is not offered in the menu or pointed at in the text.
 */
export type Features = {
  import: boolean;
  export: boolean;
  backups: boolean;
  updates: boolean;
};

// Asked once. The switches are read when the service starts, so they cannot change while a page is open.
let known: Features | null = null;

/** The switches, or null until they have been asked for. Null is treated as off by anything that shows a link. */
export function useFeatures(): Features | null {
  const [features, setFeatures] = useState<Features | null>(known);

  useEffect(() => {
    if (known) return;

    let current = true;
    void (async () => {
      try {
        const f = await api.get<Features>('/api/features');
        known = f;
        if (current) setFeatures(f);
      } catch {
        // Not signed in yet, or the service is busy: stay unknown and ask again next time.
      }
    })();

    return () => {
      current = false;
    };
  }, []);

  return features;
}
