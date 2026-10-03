/** What kind of breakdown a service request was. The numbers are the server's. */
export const BREAKDOWN_TYPES: { value: number; label: string }[] = [
  { value: 10, label: 'Hardware' },
  { value: 20, label: 'Software' },
  { value: 30, label: 'Both (Hardware & Software)' },
  { value: 40, label: 'Accessory / Consumable' },
  { value: 50, label: 'Improper usage' },
];

export const BREAKDOWN_LABEL: Record<number, string> = Object.fromEntries(
  BREAKDOWN_TYPES.map((t) => [t.value, t.label]),
);

/** The label for a request's breakdown type, or null while nobody has said. */
export function breakdownLabel(value: number | null | undefined): string | null {
  return value === null || value === undefined ? null : (BREAKDOWN_LABEL[value] ?? null);
}
