/** Hours, the way downtime and uptime are reported: "5.5 h", "0 h", "1,240.25 h". */
export function formatHours(hours: number): string {
  return `${hours.toLocaleString('en-IN', { maximumFractionDigits: 2 })} h`;
}
