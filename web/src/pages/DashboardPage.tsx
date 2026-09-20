import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { api } from '../api/client';
import { formatDateTime } from '../time';
import { HandoffNotice } from '../HandoffNotice';
import { StatusIcon } from '../StatusPill';
import { useHandoff } from '../handoff';

type Dashboard = {
  /* Present for an administrator only. */
  backup: { state: 'ok' | 'pending' | 'warn' | 'problem'; lastSuccessUtc: string | null } | null;
  equipment: { total: number; inService: number; underRepair: number };
  pm: {
    overdue: number;
    dueToday: number;
    dueThisWeek: number;
    completedThisMonth: number;
    complianceThisMonth: number | null;
  };
  workOrders: { open: number; critical: number; unassigned: number; mine: number; machinesDown: number };
};

export function DashboardPage() {
  const [data, setData] = useState<Dashboard | null>(null);
  const [error, setError] = useState<string | null>(null);
  // Set when we were sent here from a page that is not theirs, or not there.
  const [handoff] = useHandoff();

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const d = await api.get<Dashboard>('/api/dashboard');
        if (!cancelled) setData(d);
      } catch (e) {
        if (!cancelled) setError(e instanceof Error ? e.message : 'Could not load the dashboard.');
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  return (
    <div className="page">
      <header className="page-head">
        <div>
          <h1>Today</h1>
          <p className="muted">Where the department stands right now.</p>
        </div>
      </header>

      <HandoffNotice handoff={handoff} />

      {error && <p className="alert alert-error" role="alert">{error}</p>}

      {!data && !error && <p className="muted">Loading…</p>}

      {data && (
        <>
          {data.backup && <BackupBanner backup={data.backup} />}

          {/* Ordered by what should interrupt someone's morning. A machine
              that is down and a PM that is overdue both have consequences
              today; totals do not. */}
          <div className="tiles">
            <Tile
              label="Machines down"
              value={data.workOrders.machinesDown}
              tone={data.workOrders.machinesDown > 0 ? 'danger' : undefined}
              to="/work-orders"
            />
            <Tile
              label="Critical faults"
              value={data.workOrders.critical}
              tone={data.workOrders.critical > 0 ? 'danger' : undefined}
              to="/work-orders"
            />
            <Tile
              label="PMs overdue"
              value={data.pm.overdue}
              tone={data.pm.overdue > 0 ? 'warn' : undefined}
              to="/pm?status=30"
            />
            <Tile label="Due this week" value={data.pm.dueThisWeek} to="/pm" />
          </div>

          <div className="tiles">
            {/* First, and for everyone: it is the one number on this page that is
                about the person reading it. Assigned, being worked, or waiting on
                a part - the same set the page behind it lists. */}
            <Tile
              label="Assigned to me"
              value={data.workOrders.mine}
              tone={data.workOrders.mine > 0 ? 'warn' : undefined}
              to="/work-orders?assignee=me"
            />
            <Tile
              label="Unassigned faults"
              value={data.workOrders.unassigned}
              tone={data.workOrders.unassigned > 0 ? 'warn' : undefined}
              to="/work-orders?status=10"
            />
            <Tile label="Open work orders" value={data.workOrders.open} to="/work-orders" />
            <Tile label="PMs done this month" value={data.pm.completedThisMonth} to="/pm?status=40" />
            <Tile
              label="PM compliance"
              /* Null until something has actually fallen due, which is every
                 hospital in its first month. Showing 0% then would be a lie
                 about their record. */
              value={data.pm.complianceThisMonth === null ? '—' : `${data.pm.complianceThisMonth}%`}
              tone={
                data.pm.complianceThisMonth === null
                  ? undefined
                  : data.pm.complianceThisMonth >= 90
                    ? 'ok'
                    : data.pm.complianceThisMonth >= 70
                      ? 'warn'
                      : 'danger'
              }
              hint="Completed against due, this month"
            />
          </div>

          <div className="card">
            <h2 className="section-h">Register</h2>
            <div className="tiles tiles-plain">
              <Tile label="Assets" value={data.equipment.total} to="/equipment" />
              <Tile label="In service" value={data.equipment.inService} to="/equipment?status=20" />
              <Tile label="Under repair" value={data.equipment.underRepair} to="/equipment?status=30" />
            </div>
          </div>
        </>
      )}
    </div>
  );
}

/* Only shown when something needs doing. A healthy backup is not news, and a
   permanent green row would teach people to stop reading this page. Pending is
   a new install that has not yet reached its first overnight backup - nothing
   is wrong, so nothing is said. */
function BackupBanner({ backup }: { backup: NonNullable<Dashboard['backup']> }) {
  if (backup.state === 'ok' || backup.state === 'pending') return null;

  const last = backup.lastSuccessUtc
    ? `The last good backup was on ${formatDateTime(backup.lastSuccessUtc)}.`
    : 'No backup has ever completed.';

  return (
    <p className={`alert ${backup.state === 'problem' ? 'alert-error' : 'alert-warn'}`} role="alert">
      {backup.state === 'problem' ? 'Backups are not running. ' : 'The latest backup failed. '}
      {last} <Link to="/backups">Open Backups</Link>
    </p>
  );
}

// Zero is a quiet morning, not a success, so it has no tone and no word. Green is
// for a figure that is confirmed good, which here is only the compliance rate.
const STATE_WORD = { danger: 'Act now', warn: 'Attention', ok: 'On track' } as const;
const STATE_ICON = { danger: 'danger', warn: 'warning', ok: 'success' } as const;

function Tile({
  label,
  value,
  tone,
  to,
  hint,
}: {
  label: string;
  value: number | string;
  tone?: 'ok' | 'warn' | 'danger';
  to?: string;
  hint?: string;
}) {
  const body = (
    <>
      <span className="tile-value">{value}</span>
      <span className="tile-label">{label}</span>
      {tone && (
        <span className="tile-state">
          <StatusIcon name={STATE_ICON[tone]} />
          {STATE_WORD[tone]}
        </span>
      )}
      {hint && <span className="tile-hint">{hint}</span>}
    </>
  );

  const className = ['tile', tone ? `tile-${tone}` : ''].join(' ').trim();

  return to ? (
    <Link className={className} to={to}>{body}</Link>
  ) : (
    <div className={className}>{body}</div>
  );
}
