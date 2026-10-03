/** What the work order list and the work order page both ask the server for. */

export type WorkOrderRow = {
  id: number;
  number: string;
  status: number;
  priority: number;
  faultDescription: string;
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
};
