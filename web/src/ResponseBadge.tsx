import { formatMinutes } from './age';
import { StatusPill } from './StatusPill';
import { RESPONSE_LOOK } from './statusTones';
import { formatDateTime } from './time';
import type { ResponseInfo } from './workOrderTypes';

/**
 * Where a fault stands against the time an engineer is allowed to respond: waiting, overdue, on time or late.
 * A response is the time from the fault being raised to the engineer pressing In progress, not the time to fix it.
 */
export function ResponseBadge({ r }: { r: ResponseInfo }) {
  const target = formatMinutes(r.targetMinutes);

  switch (r.state) {
    case 'Waiting':
      return (
        <>
          <StatusPill look={RESPONSE_LOOK.Waiting}>Waiting</StatusPill>
          <div className="muted" style={{ fontSize: '0.8rem' }}>
            Start by {formatDateTime(r.dueAtUtc)}
          </div>
        </>
      );
    case 'Overdue':
      return (
        <>
          <StatusPill look={RESPONSE_LOOK.Overdue}>Overdue by {formatMinutes(r.overdueMinutes)}</StatusPill>
          <div className="muted" style={{ fontSize: '0.8rem' }}>Allowed {target}</div>
        </>
      );
    case 'Met':
    case 'Missed':
      return (
        <>
          <StatusPill look={RESPONSE_LOOK[r.state]}>{r.state === 'Met' ? 'On time' : 'Late'}</StatusPill>
          <div className="muted" style={{ fontSize: '0.8rem' }}>
            Started in {formatMinutes(r.responseMinutes)} (allowed {target})
          </div>
        </>
      );
    default:
      return r.responseMinutes != null ? (
        <span className="muted">Started in {formatMinutes(r.responseMinutes)}</span>
      ) : (
        <span className="muted">—</span>
      );
  }
}

/** The same, as one line of words, for a details list. */
export function responseSentence(r: ResponseInfo): string | null {
  const target = formatMinutes(r.targetMinutes);
  switch (r.state) {
    case 'Waiting':
      return `Not started yet. An engineer should start by ${formatDateTime(r.dueAtUtc)} (allowed ${target}).`;
    case 'Overdue':
      return `Not started, ${formatMinutes(r.overdueMinutes)} past the ${target} allowed.`;
    case 'Met':
      return `On time: started in ${formatMinutes(r.responseMinutes)} (allowed ${target}).`;
    case 'Missed':
      return `Late: started in ${formatMinutes(r.responseMinutes)} (allowed ${target}).`;
    default:
      return r.responseMinutes != null ? `Started in ${formatMinutes(r.responseMinutes)}.` : null;
  }
}
