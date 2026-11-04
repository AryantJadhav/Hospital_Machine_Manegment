/** What the loss-of-equipment pages ask the server for. */

import type { Look } from './statusTones';

/** How the machine was lost. The numbers are the server's. */
export const LOSS_KINDS: { value: number; label: string; help: string }[] = [
  { value: 10, label: 'Lost', help: 'Misplaced or gone missing, and not found.' },
  { value: 20, label: 'Stolen', help: 'Taken without permission.' },
  { value: 30, label: 'Lost while away', help: 'Lost while it was with a vendor or a courier, or on its way back.' },
];

export type LossStatus = 'Reported' | 'Investigating' | 'AwaitingDecision' | 'Recovered' | 'WrittenOff' | 'Cancelled';

export const LOSS_STATUS_LABEL: Record<LossStatus, string> = {
  Reported: 'Waiting for an engineer',
  Investigating: 'Engineer is looking into it',
  AwaitingDecision: 'Waiting for the head',
  Recovered: 'Found and recovered',
  WrittenOff: 'Written off',
  Cancelled: 'Cancelled',
};

/** Reported is the one to act on; being looked into is work in motion; found is good news; written off is a loss. */
export const LOSS_STATUS_LOOK: Record<LossStatus, Look> = {
  Reported: { tone: 'warning' },
  Investigating: { tone: 'info' },
  AwaitingDecision: { tone: 'warning' },
  Recovered: { tone: 'success' },
  WrittenOff: { tone: 'danger' },
  Cancelled: { tone: 'neutral' },
};

export type LossRow = {
  id: number;
  /** What is printed and quoted: LOSS-2026-00012. */
  reference: string;
  kind: number;
  kindLabel: string;
  noticedOn: string;
  status: LossStatus;
  statusLabel: string;
  estimatedValue: number | null;
  hasPoliceReport: boolean;
  summary: string;
  equipmentId: number;
  assetTag: string;
  machineName: string | null;
  locationName: string | null;
};

export type LossCounts = {
  open: number;
  withEngineer: number;
  awaitingDecision: number;
  recovered: number;
  writtenOff: number;
  cancelled: number;
};

export type LossDetail = {
  id: number;
  reference: string;
  kind: number;
  kindLabel: string;
  noticedOn: string;
  lastSeenOn: string | null;
  lastSeenPlace: string | null;
  description: string;
  lastHandledBy: string | null;
  policeReportNumber: string | null;
  policeReportedOn: string | null;
  estimatedValue: number | null;
  status: LossStatus;
  statusLabel: string;
  isFinal: boolean;
  findings: string | null;
  correctiveAction: string | null;
  /** The head's own words with the decision, or why it was sent back to the engineer. */
  decisionNotes: string | null;
  approvedByName: string | null;
  approvedAtUtc: string | null;
  recoveredOn: string | null;
  equipmentId: number;
  machine: {
    id: number;
    assetTag: string;
    machineName: string | null;
    manufacturer: string | null;
    model: string | null;
    serialNumber: string | null;
    purchaseCost: number | null;
  } | null;
  locationName: string | null;
  reportedByName: string | null;
  reportedAtUtc: string;
  closedByName: string | null;
  closedAtUtc: string | null;
};

export type LossTally = { label: string; value: number; number: number };

export type LossSummary = {
  total: number;
  open: number;
  recovered: number;
  writtenOff: number;
  valueReported: number;
  valueLost: number;
  valueRecovered: number;
  withoutValue: number;
  byKind: LossTally[];
  byLocation: LossTally[];
  repeatMachines: { equipmentId: number; assetTag: string; machineName: string | null; losses: number }[];
};

/** What every loss form says about patients, once: this is about the machine. */
export const NO_PATIENTS_LOSS =
  'This record is about the machine only. Do not write a patient’s name or details.';
