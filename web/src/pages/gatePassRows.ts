import type { GatePassItem } from '../gatePassTypes';

/**
 * One line of the gate pass sheet. A line with an equipmentId is a machine from the register: its number
 * and its quantity (always 1) belong to the register and are not retyped. Anything else is typed in.
 */
export type ItemRow = {
  key: number;
  equipmentId: number | null;
  /** The spare part this line is, when it is one. */
  sparePartId: number | null;
  description: string;
  assetCode: string;
  quantity: string;
  remarks: string;
};

let nextKey = 1;

/** A fresh key for a row; rows are told apart by it, not by their place on the sheet. */
export function newKey(): number {
  return nextKey++;
}

export function blankRow(): ItemRow {
  return { key: newKey(), equipmentId: null, sparePartId: null, description: '', assetCode: '', quantity: '1', remarks: '' };
}

export function machineRow(machineId: number, assetTag: string, description: string): ItemRow {
  return { key: newKey(), equipmentId: machineId, sparePartId: null, description, assetCode: assetTag, quantity: '1', remarks: '' };
}

export function partRow(partId: number, partNumber: string, description: string): ItemRow {
  return { key: newKey(), equipmentId: null, sparePartId: partId, description, assetCode: partNumber, quantity: '1', remarks: '' };
}

/** A line nobody has written anything on: not an item. */
export function isBlank(r: ItemRow): boolean {
  return (
    r.equipmentId === null &&
    r.sparePartId === null &&
    r.description.trim() === '' &&
    r.assetCode.trim() === '' &&
    r.remarks.trim() === '' &&
    (r.quantity.trim() === '' || r.quantity.trim() === '1')
  );
}

export function rowsFrom(items: GatePassItem[], spare: number): ItemRow[] {
  const rows = items.map<ItemRow>((i) => ({
    key: newKey(),
    equipmentId: i.equipmentId,
    sparePartId: i.sparePartId,
    description: i.description,
    assetCode: i.assetCode ?? '',
    quantity: String(i.quantity),
    remarks: i.remarks ?? '',
  }));
  while (rows.length < spare) rows.push(blankRow());
  return rows;
}

/** The items on the sheet, as the server wants them. A line left empty is not an item. */
export function itemsFrom(rows: ItemRow[]) {
  return rows
    .filter((r) => !isBlank(r))
    .map((r) => ({
      equipmentId: r.equipmentId,
      sparePartId: r.sparePartId,
      description: r.description.trim() || null,
      assetCode: r.assetCode.trim() || null,
      quantity: r.quantity.trim() === '' ? 1 : Number(r.quantity),
      remarks: r.remarks.trim() || null,
    }));
}

/** The first thing wrong with the sheet, in words, or null. The server checks the same again. */
export function problemWith(rows: ItemRow[]): string | null {
  const filled = rows.filter((r) => !isBlank(r));
  if (filled.length === 0) return 'Add at least one item to send out.';

  const machines = new Set<number>();
  for (const [i, r] of filled.entries()) {
    const n = i + 1;
    if (r.equipmentId === null && r.sparePartId === null && r.description.trim() === '') return `Line ${n} needs a description.`;

    const q = Number(r.quantity);
    if (!Number.isInteger(q) || q < 1) return `Line ${n} needs a quantity of 1 or more.`;

    if (r.equipmentId !== null) {
      if (machines.has(r.equipmentId)) return 'The same machine is on two lines. Take one of them out.';
      machines.add(r.equipmentId);
    }
  }

  return null;
}
