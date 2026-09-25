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

/** Who does a PM. The numbers are the server's PmPerformedBy values. */
export const PERFORMED_BY = { inHouse: 10, vendor: 20 } as const;

/**
 * No pattern: every PM date is picked by hand, one at a time. Offered as "Choose the dates
 * myself" next to the named frequencies. The server's PmFrequency.Manual.
 */
export const MANUAL_FREQUENCY = 95;

/** The most dates the server takes for one machine at a time. */
export const MAX_MANUAL_DATES = 60;

/** A number of days between PMs, set on the PM pages. Never offered on the form, and it has no preview. */
export const CUSTOM_FREQUENCY = 90;
