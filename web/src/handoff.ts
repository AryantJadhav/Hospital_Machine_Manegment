import { useEffect, useState } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';

/** The PM that was just recorded, so the confirmation can offer its certificate. */
export type RecordedPm = { id: number; assetTag: string; dueDate: string };

/**
 * A message handed to the page a form sends you back to.
 *
 * Filling in a PM or reporting a fault now happens on its own screen, opened
 * from wherever the technician was standing - the PM list, or a machine's own
 * page - and it returns them there. The place they come back to is the one that
 * says what happened.
 */
export type Handoff = {
  notice: string;
  recorded: RecordedPm | null;
  /** Success by default; `info` is for "you were sent here" rather than "that worked". */
  tone?: 'ok' | 'info';
};

/**
 * Reads the handoff left in the navigation state, once.
 *
 * It is cleared from the history entry straight away, so refreshing the page or
 * pressing Back does not announce "PM recorded" again about something that
 * happened some time ago.
 */
export function useHandoff(): [Handoff | null, (h: Handoff | null) => void] {
  const location = useLocation();
  const navigate = useNavigate();

  const [handoff, setHandoff] = useState<Handoff | null>(
    () => (location.state as { handoff?: Handoff } | null)?.handoff ?? null,
  );

  useEffect(() => {
    if ((location.state as { handoff?: Handoff } | null)?.handoff) {
      navigate(`${location.pathname}${location.search}`, { replace: true, state: null });
    }
    // Once, on arrival. The state it reads is the arrival state.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return [handoff, setHandoff];
}
