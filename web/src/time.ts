/**
 * Every date and time the app shows, on the hospital's clock.
 *
 * Times are stored and sent as UTC, which is right for storage and wrong for
 * reading. Each page used to format them with the browser's own local time,
 * so the same backup showed one time on a hospital PC and another on a phone
 * set to a different zone or on a laptop that had been travelling. Here they
 * are always India Standard Time, whatever the browser thinks.
 *
 * Done by adding the offset rather than through Intl's time-zone database, so
 * the answer does not depend on the browser shipping that data. India has no
 * daylight saving, which is what makes a fixed offset correct.
 */

/** Must match ScheduleOptions.UtcOffsetMinutes on the server, whose default is 330. */
const OFFSET_MINUTES = 330;

export const ZONE_LABEL = 'IST';

const DATE_ONLY = /^\d{4}-\d{2}-\d{2}$/;
const HAS_ZONE = /(Z|[+-]\d{2}:?\d{2})$/i;

const pad = (n: number) => String(n).padStart(2, '0');

/**
 * The instant a string names. One with no zone on it is UTC, because that is
 * how everything is stored, and reading it as the browser's local time would
 * shift it by whatever the browser's offset happens to be.
 */
function toInstant(value: string): Date | null {
  const d = new Date(HAS_ZONE.test(value) ? value : `${value}Z`);
  return Number.isNaN(d.getTime()) ? null : d;
}

function hospitalParts(instant: Date) {
  // Shift by the offset, then read the UTC fields: they are now the wall clock.
  const t = new Date(instant.getTime() + OFFSET_MINUTES * 60_000);
  return {
    y: t.getUTCFullYear(),
    mo: t.getUTCMonth() + 1,
    d: t.getUTCDate(),
    h: t.getUTCHours(),
    mi: t.getUTCMinutes(),
  };
}

/**
 * Day-first, as every date in an Indian hospital is written: 20/09/2026.
 *
 * A date with no time in it (a warranty expiry, a due date) is a calendar day,
 * not an instant, and is shown as it is. Moving it across a zone would make a
 * warranty that expires on the 20th expire on the 19th.
 *
 * Null when there is nothing to show, so a row can leave the field out.
 */
export function formatDate(value: string | null | undefined): string | null {
  if (!value) return null;

  if (DATE_ONLY.test(value)) {
    const [y, m, d] = value.split('-');
    return `${d}/${m}/${y}`;
  }

  const instant = toInstant(value);
  if (!instant) return null;
  const p = hospitalParts(instant);
  return `${pad(p.d)}/${pad(p.mo)}/${p.y}`;
}

/** "20/09/2026 00:27 IST". The zone is said, so nobody has to wonder. */
export function formatDateTime(value: string | null | undefined): string {
  if (!value) return '—';

  const instant = toInstant(value);
  if (!instant) return '—';
  const p = hospitalParts(instant);
  return `${pad(p.d)}/${pad(p.mo)}/${p.y} ${pad(p.h)}:${pad(p.mi)} ${ZONE_LABEL}`;
}

/** Today's date at the hospital, as yyyy-mm-dd for a date input. */
export function todayAtHospital(): string {
  const p = hospitalParts(new Date());
  return `${p.y}-${pad(p.mo)}-${pad(p.d)}`;
}
