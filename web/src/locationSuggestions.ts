/**
 * Names and codes offered while someone adds a place.
 *
 * A hospital's places are typed in by hand, and most of them are called the same
 * few things. Offering those, and the next number in a run ("Room 3" after "Room
 * 1" and "Room 2"), saves typing and keeps the spelling the same from one ward to
 * the next, which matters when the names are later searched and reported on.
 * Nothing here is imposed: the person can type any name.
 */

export type Place = { id: number; code: string; name: string; level: number; parentId: number | null };

// The levels, as the server numbers them.
const SITE = 20;
const BUILDING = 30;
const FLOOR = 40;
const DEPARTMENT = 50;
const ROOM = 60;

/** Common names by level, as an Indian hospital writes them. */
export const STANDARD_NAMES: Record<number, string[]> = {
  [SITE]: ['Main Campus', 'City Campus', 'Annexe', 'Outpatient Centre'],
  [BUILDING]: [
    'Main Block', 'Outpatient Block', 'Emergency Block', 'Cardiac Wing', 'Maternity Wing',
    'Paediatric Wing', 'Oncology Block', 'Annexe Building', 'Service Block',
  ],
  [FLOOR]: [
    'Basement', 'Ground Floor', '1st Floor', '2nd Floor', '3rd Floor', '4th Floor', '5th Floor', 'Terrace',
  ],
  [DEPARTMENT]: [
    'Emergency / Casualty', 'ICU', 'NICU', 'PICU', 'CCU', 'HDU', 'Operation Theatre', 'Post-op Recovery',
    'Labour Room', 'General Ward', 'Private Ward', 'Semi-private Ward', 'Paediatric Ward', 'Maternity Ward',
    'OPD', 'Dialysis', 'Radiology', 'CT Scan', 'MRI', 'X-Ray', 'Ultrasound', 'Mammography', 'Pathology Lab',
    'Microbiology', 'Biochemistry', 'Blood Bank', 'Pharmacy', 'CSSD', 'Physiotherapy', 'Endoscopy', 'Cath Lab',
    'Cardiology', 'Neurology', 'Nephrology', 'Orthopaedics', 'Ophthalmology', 'ENT', 'Dental', 'Oncology',
    'Radiotherapy', 'Nuclear Medicine', 'Dermatology', 'Urology', 'Gastroenterology', 'Pulmonology',
    'Biomedical Engineering', 'Central Store', 'Laundry', 'Kitchen', 'Mortuary',
  ],
  [ROOM]: [
    'Room 1', 'Bed 1', 'OT 1', 'Cabin 1', 'Consultation Room 1', 'Procedure Room', 'Isolation Room',
    'Nursing Station', 'Store Room', 'Equipment Store', 'Reception', 'Waiting Area',
  ],
};

const TRAILING_NUMBER = /^(.*?)(\d+)\s*$/;

/**
 * The next name in a run. If a place's siblings are "Room 1" and "Room 2", the
 * next is "Room 3". Only where the run is real: two or more names sharing a stem.
 */
export function nextInRun(siblingNames: string[]): string[] {
  const stems = new Map<string, number[]>();

  for (const name of siblingNames) {
    const m = TRAILING_NUMBER.exec(name.trim());
    if (!m) continue;
    const stem = m[1];
    stems.set(stem, [...(stems.get(stem) ?? []), Number(m[2])]);
  }

  return [...stems.entries()]
    .filter(([, numbers]) => numbers.length >= 2)
    .map(([stem, numbers]) => `${stem}${Math.max(...numbers) + 1}`);
}

/**
 * Names to offer for a new place: the next in a run first, then the standard
 * names for its level, leaving out any already used under the same parent.
 */
export function suggestNames(level: number, parentId: number | null, all: Place[]): string[] {
  const siblings = all.filter((p) => p.parentId === parentId).map((p) => p.name);
  const used = new Set(siblings.map((n) => n.trim().toLowerCase()));

  const offered = [...nextInRun(siblings), ...(STANDARD_NAMES[level] ?? [])];

  return [...new Set(offered)].filter((n) => !used.has(n.trim().toLowerCase()));
}

const SKIPPED_WORDS = new Set(['of', 'and', 'the', 'in', 'for', '&', '/']);

/** "Operation Theatre" gives OT, "ICU" stays ICU, "Ground Floor" gives GF, "Room 3" gives R3. */
export function abbreviate(name: string): string {
  const cleaned = name.replace(/[^A-Za-z0-9 ]+/g, ' ').trim();
  if (!cleaned) return '';

  const trailing = TRAILING_NUMBER.exec(cleaned);
  const stem = (trailing ? trailing[1] : cleaned).trim();
  const number = trailing ? trailing[2] : '';

  const words = stem.split(/\s+/).filter((w) => w && !SKIPPED_WORDS.has(w.toLowerCase()));

  let letters: string;
  if (words.length === 0) {
    letters = '';
  } else if (words.length === 1) {
    // A short word in capitals is already an abbreviation (ICU, NICU, ENT).
    const w = words[0];
    // A word with a number after it is one of a run ("Room 3", "Bed 4"): one letter
    // keeps the code short enough to read, R3 rather than ROO3.
    letters = w.length <= 5 && w === w.toUpperCase() ? w : number ? w[0].toUpperCase() : w.slice(0, 3).toUpperCase();
  } else {
    letters = words.slice(0, 4).map((w) => w[0].toUpperCase()).join('');
  }

  return `${letters}${number}`;
}

/**
 * A code for a new place, derived from its name and made unique. Codes are unique
 * across the whole hospital, so a second "ICU" in another building becomes
 * "CARD-ICU", using the code of what it sits inside, and then "-2", "-3" if needed.
 */
export function suggestCode(name: string, parent: Place | null, all: Place[], ignoreId?: number): string {
  const base = abbreviate(name);
  if (!base) return '';

  const taken = new Set(all.filter((p) => p.id !== ignoreId).map((p) => p.code.toLowerCase()));

  const candidates = [base, parent ? `${parent.code}-${base}` : null].filter((c): c is string => !!c);
  for (const c of candidates) {
    if (!taken.has(c.toLowerCase())) return c;
  }

  const root = candidates[candidates.length - 1];
  for (let n = 2; n < 1000; n++) {
    const c = `${root}-${n}`;
    if (!taken.has(c.toLowerCase())) return c;
  }

  return root;
}
