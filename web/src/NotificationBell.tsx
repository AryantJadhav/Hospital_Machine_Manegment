import { useCallback, useEffect, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import { api } from './api/client';
import { formatDate } from './time';

/**
 * The reminder bell in the top bar, the same for everyone who is signed in.
 *
 * It lists the PMs that are overdue and the ones that fall due in the next few days (seven
 * by default, set by the server). PMs are not assigned to a person; the work list belongs to
 * the department, so the reminder does too, for an Administrator and an Employee alike.
 *
 * It asks the server when the page opens, every few minutes, and when the tab is looked at
 * again, so nothing is pushed and nothing needs the internet, a mail server or a service
 * running in the background.
 */

type Item = {
  taskId: number;
  equipmentId: number;
  assetTag: string;
  equipmentTypeName: string;
  locationName: string;
  checklistName: string;
  dueDate: string;
  status: number;
  daysFromToday: number;
};

type Reminders = {
  leadDays: number;
  overdueCount: number;
  upcomingCount: number;
  overdue: Item[];
  upcoming: Item[];
};

const REFRESH_MS = 5 * 60 * 1000;

/** When it falls due, in words a person would use. */
function when(i: Item): string {
  const d = i.daysFromToday;
  const on = formatDate(i.dueDate);
  if (i.status === 30) return `Overdue by ${-d} day${d === -1 ? '' : 's'} (was due ${on})`;
  if (d < 0) return `Was due ${on}, still within its grace`;
  if (d === 0) return 'Due today';
  if (d === 1) return `Due tomorrow (${on})`;
  return `Due in ${d} days (${on})`;
}

export function NotificationBell() {
  const [data, setData] = useState<Reminders | null>(null);
  const [failed, setFailed] = useState(false);
  const [open, setOpen] = useState(false);
  const root = useRef<HTMLDivElement>(null);

  const load = useCallback(async () => {
    try {
      setData(await api.get<Reminders>('/api/pm/reminders'));
      setFailed(false);
    } catch {
      // A bell that cannot reach the server says so when opened; it never blocks the page.
      setFailed(true);
    }
  }, []);

  useEffect(() => {
    void load();
    const timer = window.setInterval(() => void load(), REFRESH_MS);
    const onVisible = () => {
      if (document.visibilityState === 'visible') void load();
    };
    document.addEventListener('visibilitychange', onVisible);
    return () => {
      window.clearInterval(timer);
      document.removeEventListener('visibilitychange', onVisible);
    };
  }, [load]);

  useEffect(() => {
    if (!open) return;

    const onPointer = (e: MouseEvent) => {
      if (root.current && !root.current.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setOpen(false);
    };

    document.addEventListener('mousedown', onPointer);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onPointer);
      document.removeEventListener('keydown', onKey);
    };
  }, [open]);

  const overdue = data?.overdueCount ?? 0;
  const upcoming = data?.upcomingCount ?? 0;
  const total = overdue + upcoming;

  const label = data
    ? `PM reminders: ${overdue} overdue, ${upcoming} coming up in the next ${data.leadDays} days`
    : 'PM reminders';

  return (
    <div className="nav-menu" ref={root}>
      <button
        type="button"
        className="btn btn-quiet bell-button"
        aria-haspopup="true"
        aria-expanded={open}
        aria-label={label}
        title={label}
        onClick={() => {
          setOpen((o) => !o);
          // Looking at it is a good moment to be sure it is current.
          if (!open) void load();
        }}
      >
        <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" strokeWidth="1.8"
          strokeLinecap="round" strokeLinejoin="round" aria-hidden="true" focusable="false">
          <path d="M6 9a6 6 0 1 1 12 0c0 6 2.5 7.5 2.5 7.5h-17S6 15 6 9z" />
          <path d="M10 19.5a2 2 0 0 0 4 0" />
        </svg>
        {total > 0 && (
          // The number is on the button for anyone who can see it; the label above says the
          // same in words for anyone who cannot.
          <span className={overdue > 0 ? 'bell-badge bell-badge-overdue' : 'bell-badge'} aria-hidden="true">
            {total > 99 ? '99+' : total}
          </span>
        )}
      </button>

      {open && (
        <div className="nav-menu-list bell-panel" role="region" aria-label="PM reminders">
          {failed && !data && <p className="bell-note">Could not load the reminders. Try again in a moment.</p>}

          {data && total === 0 && (
            <p className="bell-note">Nothing is overdue or due in the next {data.leadDays} days.</p>
          )}

          {data && data.overdue.length > 0 && (
            <Section
              title={`Overdue (${overdue})`}
              items={data.overdue}
              more={overdue - data.overdue.length}
              onPick={() => setOpen(false)}
              tone="danger"
            />
          )}

          {data && data.upcoming.length > 0 && (
            <Section
              title={`Due in the next ${data.leadDays} days (${upcoming})`}
              items={data.upcoming}
              more={upcoming - data.upcoming.length}
              onPick={() => setOpen(false)}
            />
          )}

          <Link className="bell-all" to="/pm" onClick={() => setOpen(false)}>See all PMs</Link>
        </div>
      )}
    </div>
  );
}

function Section({
  title,
  items,
  more,
  onPick,
  tone,
}: {
  title: string;
  items: Item[];
  more: number;
  onPick: () => void;
  tone?: 'danger';
}) {
  return (
    <section className="bell-section">
      <h3 className={tone === 'danger' ? 'bell-h bell-h-danger' : 'bell-h'}>{title}</h3>
      <ul className="bell-list">
        {items.map((i) => (
          <li key={i.taskId}>
            <Link className="bell-item" to={`/pm/${i.taskId}/do`} onClick={onPick}>
              <span>
                <span className="mono">{i.assetTag}</span> · {i.equipmentTypeName}
              </span>
              <span className="bell-sub">{i.locationName} · {i.checklistName}</span>
              <span className={tone === 'danger' ? 'bell-when bell-when-danger' : 'bell-when'}>{when(i)}</span>
            </Link>
          </li>
        ))}
      </ul>
      {more > 0 && <p className="bell-more">and {more} more. See all PMs for the full list.</p>}
    </section>
  );
}
