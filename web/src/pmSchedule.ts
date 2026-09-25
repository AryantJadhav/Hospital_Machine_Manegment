/**
 * The plain pieces of a PM schedule that the Add a machine form and the Edit form share.
 *
 * How often a PM falls due is decided by the administrator. The numbers are the
 * server's PmFrequency values.
 */

export const PM_FREQUENCIES = [
  { value: 10, label: 'Monthly — 12 times a year' },
  { value: 15, label: 'Every 2 months — 6 times a year' },
  { value: 20, label: 'Quarterly — 4 times a year' },
  { value: 30, label: 'Half-yearly — 2 times a year' },
  { value: 40, label: 'Yearly — once a year' },
];

/** A number of days between PMs, set on the PM pages. Kept as it is, never offered here. */
export const CUSTOM_FREQUENCY = 90;

/** A schedule as the server sends it. */
export type ScheduleApi = {
  id: number;
  checklistTemplateId: number;
  checklistName: string;
  frequency: number;
  intervalDays: number;
  anchorDate: string;
  graceDays: number;
  isActive: boolean;
  nextDueDate: string | null;
  nextUpcomingDate: string | null;
};

/** A schedule being edited, with what it was when it was opened. */
export type ScheduleEdit = {
  id: number;
  checklistId: number;
  checklistName: string;
  frequency: number;
  intervalDays: number;
  active: boolean;
  nextDue: string;
  graceDays: string;
  original: {
    frequency: number;
    intervalDays: number;
    active: boolean;
    anchorDate: string;
    nextDue: string;
    graceDays: string;
  };
};

export function toScheduleEdit(s: ScheduleApi, today: string): ScheduleEdit {
  // The date shown is the next one the schedule falls on from today, not the oldest PM
  // still open, which may be months overdue: a new pattern counted from an overdue date
  // would invent PMs in the past. A stopped schedule starts from today, which is where it
  // picks up if it is started again.
  const nextDue = s.isActive ? (s.nextUpcomingDate ?? today) : today;
  return {
    id: s.id,
    checklistId: s.checklistTemplateId,
    checklistName: s.checklistName,
    frequency: s.frequency,
    intervalDays: s.intervalDays,
    active: s.isActive,
    nextDue,
    graceDays: String(s.graceDays),
    original: {
      frequency: s.frequency,
      intervalDays: s.intervalDays,
      active: s.isActive,
      anchorDate: s.anchorDate,
      nextDue,
      graceDays: String(s.graceDays),
    },
  };
}

/** What to send for a schedule, or null when nothing about it was changed. */
export function scheduleUpdate(s: ScheduleEdit) {
  const o = s.original;
  const patternChanged = s.frequency !== o.frequency || s.intervalDays !== o.intervalDays;
  const dateChanged = s.nextDue !== o.nextDue;
  const restarted = s.active && !o.active;
  const changed =
    patternChanged || dateChanged || restarted || s.active !== o.active || s.graceDays !== o.graceDays;
  if (!changed) return null;

  return {
    frequency: s.frequency,
    intervalDays: s.frequency === CUSTOM_FREQUENCY ? s.intervalDays : 0,
    // The schedule counts every date from its anchor. Moving the anchor to "the next PM
    // due" without being asked would shift a 31st onto the 28th for good, so the anchor
    // is only moved when the frequency changes (dates counted from an old anchor would
    // invent PMs in the past), the date is changed on purpose, or a stopped schedule is
    // started again.
    nextDueDate: patternChanged || dateChanged || restarted ? s.nextDue : o.anchorDate,
    graceDays: Number(s.graceDays) || 0,
    isActive: s.active,
  };
}
