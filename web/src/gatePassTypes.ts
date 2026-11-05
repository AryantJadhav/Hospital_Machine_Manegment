/** What the gate pass pages ask the server for. */

import type { Tone } from './statusTones';

export type GatePassStatus = 'Out' | 'Returned' | 'Cancelled' | 'Issued';

/** What each status is called to a person, and the tone it wears. Late is worked out, not a status of its own. */
export const GATE_PASS_LABEL: Record<GatePassStatus, string> = {
  Out: 'Out of the hospital',
  Returned: 'Returned',
  Cancelled: 'Cancelled',
  Issued: 'Issued, not returnable',
};

export const GATE_PASS_TONE: Record<GatePassStatus, Tone> = {
  Out: 'info',
  Returned: 'success',
  Cancelled: 'neutral',
  Issued: 'warning',
};

/** What the form says a pass is for when nothing else is written. The server uses the same words. */
export const DEFAULT_PURPOSE = 'Sending to the company for repair';

/** What a non-returnable pass starts out as: the usual reason a part leaves for good. */
export const DEFAULT_NON_RETURNABLE_PURPOSE = 'Faulty part handed to the vendor';

export type GatePassRow = {
  id: number;
  number: number;
  /** What is printed and quoted: GP-1001. */
  reference: string;
  /** False for a pass where the goods leave for good and are not expected back. */
  isReturnable: boolean;
  /** What is going out: a machine from the register, spare parts, or something typed in. */
  goods: 'Machine' | 'Spare parts' | 'Other';
  passDate: string;
  vendorName: string;
  contactPerson: string | null;
  purpose: string;
  status: GatePassStatus;
  expectedReturnDate: string | null;
  returnedOn: string | null;
  isOverdue: boolean;
  /** Days away so far, or in all once it is back. Null once cancelled. */
  daysOut: number | null;
  workOrderId: number | null;
  workOrderNumber: string | null;
  itemCount: number;
  totalQuantity: number;
  someItems: string[];
};

export type GatePassCounts = { out: number; overdue: number; returned: number; cancelled: number; issued: number };

export type GatePassItem = {
  id: number;
  equipmentId: number | null;
  sparePartId: number | null;
  sparePart: { id: number; partNumber: string; name: string; isReturnable: boolean } | null;
  description: string;
  assetCode: string | null;
  quantity: number;
  remarks: string | null;
  machine: { id: number; assetTag: string; machineName: string | null; locationName: string | null } | null;
};

export type GatePassDetail = {
  id: number;
  number: number;
  reference: string;
  isReturnable: boolean;
  goods: 'Machine' | 'Spare parts' | 'Other';
  passDate: string;
  vendorName: string;
  contactPerson: string | null;
  contactPhone: string | null;
  purpose: string;
  status: GatePassStatus;
  expectedReturnDate: string | null;
  returnedOn: string | null;
  isOverdue: boolean;
  daysOut: number | null;
  authorisedBy: string | null;
  notes: string | null;
  outcomeNotes: string | null;
  workOrder: { id: number; number: string; reportedOn: string } | null;
  totalQuantity: number;
  items: GatePassItem[];
  createdByName: string | null;
  createdAtUtc: string;
};

/** How long a pass has been away, in the words a person would use. */
export function describeDays(days: number | null): string | null {
  if (days === null) return null;
  if (days === 0) return 'today';
  return days === 1 ? '1 day' : `${days} days`;
}

/** What the register says about a machine, as one line a guard can match against what is on the trolley. */
export function describeMachine(m: {
  equipmentTypeName: string | null;
  manufacturer: string | null;
  model: string | null;
  serialNumber: string | null;
  assetTag: string;
}): string {
  const make = [m.manufacturer, m.model].filter((s): s is string => Boolean(s && s.trim())).join(' ');
  let text = [m.equipmentTypeName, make].filter((s): s is string => Boolean(s && s.trim())).join(' - ');
  if (m.serialNumber && m.serialNumber.trim()) {
    text = `${text ? `${text}, ` : ''}serial ${m.serialNumber.trim()}`;
  }
  return text || m.assetTag;
}
