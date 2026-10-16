/**
 * A machine's age as the server counted it (whole months), said the way a person says it: "2 years 3 months",
 * "11 months", "1 year", "Under a month". Null when the age is not known, so a screen can show a dash.
 */
export function formatMachineAge(months: number | null | undefined): string | null {
  if (months == null) return null;
  if (months < 1) return 'Under a month';

  const years = Math.floor(months / 12);
  const rest = months % 12;
  const y = years === 1 ? '1 year' : `${years} years`;
  const m = rest === 1 ? '1 month' : `${rest} months`;

  if (years === 0) return m;
  return rest === 0 ? y : `${y} ${m}`;
}

/** Which date the age was counted from, in a few words. */
export function ageBasis(from: string | null | undefined): string | null {
  return from === 'installation' ? 'since installation' : from === 'purchase' ? 'since purchase' : null;
}

/** How much of a machine's expected life is left, or how far past it: "2 years 3 months left", "Past it by 8 months". */
export function formatLifeLeft(remainingMonths: number | null | undefined): string | null {
  if (remainingMonths == null) return null;
  if (remainingMonths < 0) return `Past its expected life by ${formatMachineAge(-remainingMonths)}`;
  if (remainingMonths === 0) return 'Reaches its expected life this month';
  return `${formatMachineAge(remainingMonths)} left`;
}

/** A length of time in minutes, said plainly: "15 minutes", "1 hour 30 minutes", "2 days". */
export function formatMinutes(minutes: number | null | undefined): string | null {
  if (minutes == null) return null;
  if (minutes < 1) return 'under a minute';
  if (minutes < 60) return minutes === 1 ? '1 minute' : `${minutes} minutes`;

  if (minutes >= 1440 && minutes % 1440 === 0) {
    const days = minutes / 1440;
    return days === 1 ? '1 day' : `${days} days`;
  }

  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;
  const h = hours === 1 ? '1 hour' : `${hours} hours`;
  return rest === 0 ? h : `${h} ${rest} minute${rest === 1 ? '' : 's'}`;
}
