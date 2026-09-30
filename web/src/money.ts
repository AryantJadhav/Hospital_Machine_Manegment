/**
 * Money is rupees, grouped the Indian way (12,34,567.50), because that is how
 * every person using this reads a figure. Whole amounts show no paise; anything
 * with paise shows both digits, so 1,500.50 never reads as 1,500.5.
 */
const WHOLE = new Intl.NumberFormat('en-IN', {
  style: 'currency',
  currency: 'INR',
  minimumFractionDigits: 0,
  maximumFractionDigits: 0,
});

const WITH_PAISE = new Intl.NumberFormat('en-IN', {
  style: 'currency',
  currency: 'INR',
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
});

export function formatRupees(amount: number | null | undefined): string {
  if (amount == null) return '—';
  return Number.isInteger(amount) ? WHOLE.format(amount) : WITH_PAISE.format(amount);
}

/**
 * For a headline tile where the full figure would not fit: ₹15.73 Cr, ₹4.87 L, ₹4,750.
 * Lakh and crore are how a hospital finance office says a large sum.
 */
export function formatRupeesCompact(amount: number): string {
  const crore = 1_00_00_000;
  const lakh = 1_00_000;
  if (amount >= crore) return `₹${(amount / crore).toFixed(2)} Cr`;
  if (amount >= lakh) return `₹${(amount / lakh).toFixed(2)} L`;
  return formatRupees(Math.round(amount));
}
