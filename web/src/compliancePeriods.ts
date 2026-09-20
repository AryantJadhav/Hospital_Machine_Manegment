/**
 * The reporting periods an accreditation assessor asks about, as calendar dates.
 *
 * Worked out on plain year-month-day strings, never on the browser's local time:
 * "today" is the hospital's day (see time.ts), and a period boundary that moved
 * with the viewer's time zone would put a PM on the wrong side of it.
 */

export type Period = { key: string; label: string; from: string; to: string };

const pad = (n: number) => String(n).padStart(2, '0');
const iso = (y: number, m: number, d: number) => `${y}-${pad(m)}-${pad(d)}`;

/** The last day of a month, 1-based month. Day 0 of the next month is it. */
function lastDay(y: number, m: number): number {
  return new Date(Date.UTC(y, m, 0)).getUTCDate();
}

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

function monthPeriod(key: string, label: string, y: number, m: number): Period {
  return { key, label, from: iso(y, m, 1), to: iso(y, m, lastDay(y, m)) };
}

/** A calendar quarter, 0-based index into the year. */
function quarterPeriod(key: string, label: string, y: number, q: number): Period {
  const first = q * 3 + 1;
  const last = first + 2;
  return {
    key,
    label: `${label} (${MONTHS[first - 1]}–${MONTHS[last - 1]} ${y})`,
    from: iso(y, first, 1),
    to: iso(y, last, lastDay(y, last)),
  };
}

/** India's financial year runs April to March. `startYear` is the April it begins in. */
function financialYear(key: string, label: string, startYear: number): Period {
  return {
    key,
    label: `${label} (Apr ${startYear} – Mar ${startYear + 1})`,
    from: iso(startYear, 4, 1),
    to: iso(startYear + 1, 3, 31),
  };
}

/** `today` is a yyyy-mm-dd string. */
export function presetPeriods(today: string): Period[] {
  const [y, m] = today.split('-').map(Number);

  const prevMonthY = m === 1 ? y - 1 : y;
  const prevMonth = m === 1 ? 12 : m - 1;

  const q = Math.floor((m - 1) / 3);
  const prevQ = q === 0 ? 3 : q - 1;
  const prevQY = q === 0 ? y - 1 : y;

  const fy = m >= 4 ? y : y - 1;

  return [
    monthPeriod('this-month', 'This month', y, m),
    monthPeriod('last-month', 'Last month', prevMonthY, prevMonth),
    quarterPeriod('this-quarter', 'This quarter', y, q),
    quarterPeriod('last-quarter', 'Last quarter', prevQY, prevQ),
    financialYear('this-fy', 'This financial year', fy),
    financialYear('last-fy', 'Last financial year', fy - 1),
  ];
}
