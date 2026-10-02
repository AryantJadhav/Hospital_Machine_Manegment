/** One line of the attendance sheet. A line with a userId is a staff member with an account: the name is theirs and cannot be retyped. */
export type AttendeeRow = {
  key: number;
  userId: number | null;
  name: string;
  designation: string;
};

let nextKey = 1;

/** A fresh key for a row; rows are told apart by it, not by their place on the sheet. */
export function newKey(): number {
  return nextKey++;
}

export function blankRow(): AttendeeRow {
  return { key: newKey(), userId: null, name: '', designation: '' };
}

export function rowsFrom(
  attendees: { userId: number | null; name: string; designation: string | null }[],
  spare: number,
): AttendeeRow[] {
  const rows = attendees.map((a) => ({
    key: newKey(),
    userId: a.userId,
    name: a.name,
    designation: a.designation ?? '',
  }));
  while (rows.length < spare) rows.push(blankRow());
  return rows;
}

/** The people on the sheet, as the server wants them. A line left empty is not a person. */
export function attendeesFrom(rows: AttendeeRow[]) {
  return rows
    .filter((r) => r.userId !== null || r.name.trim() !== '')
    .map((r) =>
      r.userId !== null
        ? { userId: r.userId, designation: r.designation.trim() || null }
        : { name: r.name.trim(), designation: r.designation.trim() || null },
    );
}

/** A name that appears on more than one line, or null. Compared the way the server does: ignoring case. */
export function duplicateName(rows: AttendeeRow[]): string | null {
  const seen = new Set<string>();
  for (const r of rows) {
    const name = r.name.trim();
    if (!name) continue;
    const k = name.toLowerCase();
    if (seen.has(k)) return name;
    seen.add(k);
  }
  return null;
}

