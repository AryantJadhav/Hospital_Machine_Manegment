/** What the work order list and the work order page both ask the server for. */

/** How quickly an engineer answered (or must answer): from the fault being raised to In progress. */
export type ResponseInfo = {
  state: 'Waiting' | 'Overdue' | 'Met' | 'Missed' | 'NotApplicable';
  targetMinutes: number | null;
  dueAtUtc: string | null;
  responseMinutes: number | null;
  overdueMinutes: number | null;
  startedAtUtc: string | null;
};

export type WorkOrderRow = {
  id: number;
  number: string;
  status: number;
  priority: number;
  faultDescription: string;
  /** Hardware, software, both, an accessory or consumable, or improper usage. Null until someone says. */
  breakdownType: number | null;
  equipmentId: number;
  assetTag: string;
  equipmentTypeName: string;
  locationName: string;
  reportedAtUtc: string;
  /** When the repair was done, once it has been. */
  resolvedAtUtc: string | null;
  reportedByUserId: number;
  reportedByName: string | null;
  assignedToUserId: number | null;
  assignedToName: string | null;
  outOfServiceAtUtc: string | null;
  backInServiceAtUtc: string | null;
  response: ResponseInfo;
};

export type WorkOrderPhoto = {
  id: number;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  uploadedBy: string | null;
  uploadedAtUtc: string;
};

export type WorkOrderDetail = WorkOrderRow & {
  reportedByUserId: number;
  reportedByName: string | null;
  assignedAtUtc: string | null;
  startedAtUtc: string | null;
  resolutionNotes: string | null;
  resolvedAtUtc: string | null;
  closedAtUtc: string | null;
  downtimeMinutes: number | null;
  /** Hours down for this report, up to now while the machine still is. */
  downtimeHours: number | null;
  stillDown: boolean;
  allowedTransitions: number[];
  /** The department that asked may answer this resolved request: accept it, or say it is not fixed. */
  canAcknowledge: boolean;
  notes: {
    id: number;
    body: string;
    statusAfter: number | null;
    authorUserId: number;
    authorName: string | null;
    createdAtUtc: string;
  }[];
  partsUsed: {
    id: number;
    sparePartId: number;
    partNumber: string;
    name: string;
    quantityUsed: number;
    unitCostAtUse: number | null;
    usedByUserId: number;
    usedAtUtc: string;
  }[];
  photos: WorkOrderPhoto[];
  /** Machines put in place of this one while it was repaired, newest first. At most the first is still in place. */
  replacements: WorkOrderReplacement[];
  /** The engineer may put a free machine in place of this one now. */
  canProvideReplacement: boolean;
  /** The engineer may take the machine standing in back now. */
  canTakeBackReplacement: boolean;
};

/** A machine that stood, or stands, in for a faulty one. */
export type WorkOrderReplacement = {
  id: number;
  equipmentId: number;
  assetTag: string;
  typeName: string;
  manufacturer: string | null;
  model: string | null;
  providedAtUtc: string;
  providedByName: string | null;
  note: string | null;
  /** Where it was put: the faulty machine's place. */
  placedAt: string;
  /** Where it came from, and goes back to. */
  fromLocationName: string;
  returnedAtUtc: string | null;
  returnedByName: string | null;
  isActive: boolean;
};

/** A free machine that could stand in. */
export type ReplacementOption = {
  id: number;
  assetTag: string;
  typeName: string;
  manufacturer: string | null;
  model: string | null;
  serialNumber: string | null;
  locationName: string;
  sameType: boolean;
  sameModel: boolean;
};

export type ReplacementOptions = {
  forMachine: { assetTag: string; typeName: string | null; manufacturer: string | null; model: string | null; locationName: string | null };
  items: ReplacementOption[];
  total: number;
  otherTypes: boolean;
};
