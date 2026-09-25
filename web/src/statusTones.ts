/**
 * Which tone each status wears. One place, from the design system's status table.
 *
 * A tone is what a status means to the person reading it, not a colour: danger is
 * for act-now states, warning for needs-attention-soon, success only for
 * confirmed-good, info for work in motion, and everything inert is neutral. A
 * status that fits none of them is neutral; nobody invents a new colour for it.
 */

export type Tone = 'success' | 'warning' | 'danger' | 'info' | 'neutral';

/** The tone, plus the small variations a few statuses need. */
export type Look = { tone: Tone; icon?: 'pause'; strong?: boolean };

const T = (tone: Tone, extra: Omit<Look, 'tone'> = {}): Look => ({ tone, ...extra });

/**
 * How much care depends on a machine, as a word and a tone. Critical is the one to
 * notice, semi-critical is the one to keep an eye on, and non-critical is inert.
 */
export const CRITICALITY_LABEL: Record<number, string> = {
  30: 'Critical',
  20: 'Semi-critical',
  10: 'Non-critical',
};

export const CRITICALITY_LOOK: Record<number, Look> = {
  30: T('danger'),
  20: T('warning'),
  10: T('neutral'),
};

/** The short name of each kind of maintenance contract. */
export const CONTRACT_LABEL: Record<number, string> = {
  10: 'AMC',
  20: 'CMC',
};

/** Equipment: In store, In use, Under repair, Condemned, Disposed. */
export const EQUIPMENT_LOOK: Record<number, Look> = {
  10: T('neutral'),
  20: T('success'),
  30: T('warning'),
  40: T('danger'),
  50: T('neutral'),
};

/** Preventive maintenance: Scheduled, Due, Overdue, Completed, Skipped. */
export const PM_LOOK: Record<number, Look> = {
  10: T('neutral'),
  20: T('warning'),
  30: T('danger'),
  40: T('success'),
  50: T('neutral'),
};

/** Work order: Reported, Assigned, In progress, On hold, Resolved, Closed, Cancelled. */
export const WORK_ORDER_LOOK: Record<number, Look> = {
  10: T('warning'),
  20: T('info'),
  30: T('info'),
  40: T('neutral', { icon: 'pause' }),
  50: T('success'),
  60: T('neutral'),
  70: T('neutral'),
};

/** Priority: Low, Medium, High, Critical. Only Critical is filled solid. */
export const PRIORITY_LOOK: Record<number, Look> = {
  10: T('neutral'),
  20: T('neutral'),
  30: T('warning'),
  40: T('danger', { strong: true }),
};

/** A backup run: Running, Succeeded, Failed. */
export const BACKUP_LOOK: Record<number, Look> = {
  10: T('info'),
  20: T('success'),
  30: T('warning'),
};

/** A diagnostics check: fine, worth a look, a problem. */
export const CHECK_LOOK: Record<number, Look> = {
  10: T('success'),
  20: T('warning'),
  30: T('danger'),
};

/** Whether an update file can be installed: green for the one that can, red for ones that must not be. */
export const UPDATE_LOOK: Record<string, Look> = {
  Ready: T('success'),
  NotNewer: T('neutral'),
  Unreadable: T('warning'),
  InstallerMissing: T('warning'),
  InstallerAltered: T('danger'),
  NotOurs: T('danger'),
};
